using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recodio;

// One media item from any site yt-dlp supports (YouTube, Twitter/X, TikTok, Instagram,
// SoundCloud, Vimeo, Twitch, Reddit, Facebook, Bilibili, Bandcamp, …).
public record PlaylistEntry(
    int Index,
    string Title,
    string Id,
    string WebpageUrl = "",
    string Extractor = "");

public record AnalyzeResult(
    bool IsPlaylist,
    string? PlaylistTitle,
    List<PlaylistEntry> Entries,
    string Extractor = "",
    string WebpageUrl = "");

public static class YtDlpDownloader
{
    private const string ArchiveFileName = ".recodio-ytdlp-archive.txt";

    private const int MaxBatchPasses = 4;
    private const int MaxPerVideoAttempts = 4;
    private const int MaxAbortRetries = 3;

    // mp4 | mkv | best | mp3 | m4a
    public static readonly string[] OutputFormats = ["mp4", "mkv", "best", "mp3", "m4a"];

    public static readonly string[] BrowserCookieOptions =
        ["", "chrome", "edge", "firefox", "brave", "opera", "chromium"];

    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex ItemRegex = new(@"Downloading item (\d+) of (\d+)", RegexOptions.Compiled);
    private static readonly Regex ErrorLineRegex = new(@"^ERROR:", RegexOptions.Compiled);
    // Archive lines: "youtube VIDEOID" / "twitter 12345" / "Generic hash"
    private static readonly Regex ArchiveIdRegex = new(@"^\S+\s+(\S+)", RegexOptions.Compiled);
    // [extractor] id: message  — works for youtube, twitter, tiktok, vimeo, …
    private static readonly Regex ExtractorIdRegex = new(
        @"\[([A-Za-z0-9_-]+)\]\s+([A-Za-z0-9_.%-]{2,})\s*:", RegexOptions.Compiled);

    public static string ArchivePathFor(string destDir) => Path.Combine(destDir, ArchiveFileName);

    public static bool IsYoutubeExtractor(string? extractor) =>
        !string.IsNullOrEmpty(extractor)
        && extractor.Contains("youtube", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeYoutubeUrl(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
        || url.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase);

    public static async Task<AnalyzeResult> AnalyzeAsync(string ytDlpPath, string url, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Flat playlist works for any multi-entry extractor (channels, albums, sets, …).
        foreach (var a in new[]
        {
            "--flat-playlist", "-J", "--no-warnings",
            "--ignore-no-formats-error", // some flat entries have no formats yet
            url,
        })
            psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            var toolName = Path.GetFileNameWithoutExtension(psi.FileName);
            throw new InvalidOperationException(
                $"No se encontro \"{toolName}\" ({psi.FileName}). Instalalo o revisa que este en el PATH del sistema.",
                ex);
        }
        using var _ = proc;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await ProcessRunner.WaitForExitAsync(proc, ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "No se pudo analizar la URL. ¿Es un sitio que yt-dlp soporta?"
                : stderr);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var rootExtractor = ReadString(root, "extractor_key")
            ?? ReadString(root, "extractor")
            ?? "";
        var rootWebpage = ReadString(root, "webpage_url")
            ?? ReadString(root, "original_url")
            ?? url;

        if (root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            var title = ReadString(root, "title");
            var list = new List<PlaylistEntry>();
            var i = 1;
            foreach (var e in entriesEl.EnumerateArray())
            {
                if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    i++;
                    continue;
                }

                var vTitle = ReadString(e, "title") ?? $"Item {i}";
                var vId = ReadString(e, "id") ?? "";
                var vUrl = ReadString(e, "webpage_url")
                    ?? ReadString(e, "url")
                    ?? "";
                var vExt = ReadString(e, "extractor_key")
                    ?? ReadString(e, "ie_key")
                    ?? ReadString(e, "extractor")
                    ?? rootExtractor;

                if (string.IsNullOrEmpty(vId) && vTitle is "[Deleted video]" or "[Private video]")
                {
                    i++;
                    continue;
                }

                // Flat entries sometimes only have id — build a usable URL for known extractors.
                if (string.IsNullOrWhiteSpace(vUrl))
                    vUrl = BuildFallbackUrl(vExt, vId) ?? "";

                list.Add(new PlaylistEntry(i, vTitle, vId, vUrl, vExt));
                i++;
            }

            if (list.Count == 0)
                throw new InvalidOperationException(
                    "La URL se reconocio como lista/playlist pero no tiene items descargables.");

            return new AnalyzeResult(true, title, list, rootExtractor, rootWebpage);
        }

        var singleTitle = ReadString(root, "title") ?? url;
        var singleId = ReadString(root, "id") ?? "";
        return new AnalyzeResult(
            false, null,
            [new PlaylistEntry(1, singleTitle, singleId, rootWebpage, rootExtractor)],
            rootExtractor, rootWebpage);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    // Best-effort direct URL when flat-playlist omitted webpage_url.
    private static string? BuildFallbackUrl(string extractor, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var ie = extractor.ToLowerInvariant();
        if (ie.Contains("youtube"))
            return $"https://www.youtube.com/watch?v={id}";
        if (ie is "twitter" or "twitterbroadcast" or "twitterspaces")
            return $"https://twitter.com/i/status/{id}";
        if (ie.Contains("tiktok"))
            return $"https://www.tiktok.com/@/video/{id}";
        if (ie.Contains("vimeo"))
            return $"https://vimeo.com/{id}";
        if (ie.Contains("soundcloud"))
            return null; // needs full path
        if (ie.Contains("twitch") && long.TryParse(id, out _))
            return $"https://www.twitch.tv/videos/{id}";
        if (ie.Contains("reddit"))
            return $"https://www.reddit.com/comments/{id}";
        if (ie.Contains("bilibili"))
            return $"https://www.bilibili.com/video/{id}";
        return null;
    }

    public static string BuildFormatSelector(
        string format, string videoQuality, string audioQuality, List<string> extraArgs, bool relaxed = false)
    {
        string? heightFilter = relaxed ? null : videoQuality switch
        {
            "2160" or "1440" or "1080" or "720" or "480" or "360" => videoQuality,
            _ => null,
        };

        // Audio-only outputs
        if (format is "mp3" or "m4a")
        {
            var audioFmt = format == "m4a" ? "m4a" : "mp3";
            extraArgs.AddRange(["-x", "--audio-format", audioFmt, "--audio-quality",
                relaxed ? "0" : audioQuality switch { "medium" => "192K", "low" => "128K", _ => "0" }]);
            return "bestaudio*/bestaudio/best";
        }

        // Keep site-native container when possible (webm, flv, …).
        if (format == "best")
        {
            if (heightFilter != null)
                return $"bestvideo*[height<={heightFilter}]+bestaudio/best[height<={heightFilter}]/best";
            return "bestvideo*+bestaudio/best/bestvideo/bestaudio";
        }

        // Remux/merge into mp4 or mkv. Many non-YouTube sites only offer a single progressive
        // stream — always end with plain `best` so those still download.
        var merge = format == "mkv" ? "mkv" : "mp4";
        extraArgs.AddRange(["--merge-output-format", merge]);

        if (heightFilter != null)
        {
            return $"bestvideo*[height<={heightFilter}]+bestaudio/"
                 + $"best[height<={heightFilter}]/"
                 + "bestvideo*+bestaudio/best/bestvideo/bestaudio";
        }

        return "bestvideo*+bestaudio/best/bestvideo/bestaudio";
    }

    // cookiesFromBrowser: "" or chrome|edge|firefox|brave|…
    public static async Task<int> DownloadAsync(
        string ytDlpPath, string ffmpegPath, string url, List<PlaylistEntry>? selectedEntries,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, string destDir,
        Action<string> onLine, Action<int> onProgress, CancellationToken ct,
        string cookiesFromBrowser = "")
    {
        Directory.CreateDirectory(destDir);
        var archivePath = ArchivePathFor(destDir);
        var entries = selectedEntries is { Count: > 0 }
            ? selectedEntries
            : [new PlaylistEntry(1, url, "", url, "")];

        var isMulti = entries.Count > 1;
        var permanentFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SponsorBlock is a YouTube community DB — only useful for YT (and can error elsewhere).
        var useSponsorBlock = sponsorBlock
            && (LooksLikeYoutubeUrl(url) || entries.Any(e => IsYoutubeExtractor(e.Extractor)));

        if (sponsorBlock && !useSponsorBlock)
            onLine(">> SponsorBlock omitido (solo aplica a YouTube).");

        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
            onLine($">> Usando cookies de {cookiesFromBrowser} (sitios con login / age-gate).");

        // ----- Phase A: batch passes -----
        for (var pass = 1; pass <= MaxBatchPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            var missingBefore = CountMissing(entries, archivePath, permanentFailures);
            if (missingBefore == 0)
            {
                onLine(">> Todos los items ya estan en el archivo de descargas.");
                onProgress(100);
                return 0;
            }

            if (pass > 1)
            {
                var wait = Math.Min(8 + pass * 4, 30);
                onLine($">> Reintento de lista {pass}/{MaxBatchPasses}: faltan {missingBefore}. Esperando {wait}s...");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
            else
            {
                onLine($">> Descargando {entries.Count} item(s) (pasada 1/{MaxBatchPasses})...");
            }

            var playlistItems = isMulti
                ? entries.Where(e => !IsPermanent(e, permanentFailures)).Select(e => e.Index).ToList()
                : null;

            var relaxed = pass >= 3;
            var embedExtras = pass < 3;

            try
            {
                await RunWithAbortRetryAsync(
                    ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                    organizeInFolders, useSponsorBlock, isMulti, destDir, archivePath,
                    relaxed, embedExtras, cookiesFromBrowser, onLine, onProgress, permanentFailures, ct);
            }
            catch (InvalidOperationException ex)
            {
                onLine($">> Pasada {pass} termino con error: {ex.Message}");
            }

            var missingAfter = CountMissing(entries, archivePath, permanentFailures);
            onLine($">> Pasada {pass} lista: {entries.Count - missingAfter}/{entries.Count} en archivo"
                + (permanentFailures.Count > 0 ? $", {permanentFailures.Count} no disponibles" : "")
                + ".");

            if (missingAfter == 0) return 0;
            if (pass >= 2 && missingAfter >= missingBefore) break;
        }

        // ----- Phase B: one-by-one -----
        var stillMissing = GetMissing(entries, archivePath, permanentFailures);
        if (stillMissing.Count == 0) return permanentFailures.Count;

        onLine($">> Reintentando {stillMissing.Count} item(s) uno por uno...");
        var remainingFails = 0;

        for (var i = 0; i < stillMissing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = stillMissing[i];
            onProgress((int)(100.0 * i / stillMissing.Count));

            if (IsPermanent(entry, permanentFailures))
            {
                remainingFails++;
                continue;
            }

            var directUrl = ResolveDirectUrl(entry, url);
            string retryUrl;
            List<int>? retryItems;
            if (isMulti)
            {
                // Prefer playlist URL + index (keeps %(playlist_title)s).
                retryUrl = url;
                retryItems = [entry.Index];
            }
            else
            {
                retryUrl = directUrl ?? url;
                retryItems = null;
            }

            var ok = false;
            for (var attempt = 1; attempt <= MaxPerVideoAttempts && !ok; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 1)
                {
                    var wait = attempt * 5;
                    onLine($">>   reintento {attempt}/{MaxPerVideoAttempts} de \"{entry.Title}\" en {wait}s...");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                else
                {
                    onLine($">> [{i + 1}/{stillMissing.Count}] {entry.Title}");
                }

                var relaxed = attempt >= 2;
                var embedExtras = attempt == 1;
                // Later attempts: hit the item's own webpage URL (any site), not a YT hardcode.
                var useDirect = attempt >= 3 && !string.IsNullOrWhiteSpace(directUrl);
                var attemptUrl = useDirect ? directUrl! : retryUrl;
                var attemptItems = useDirect ? null : retryItems;
                var attemptOrganize = organizeInFolders && !useDirect;
                var itemSponsor = useSponsorBlock
                    && (LooksLikeYoutubeUrl(attemptUrl) || IsYoutubeExtractor(entry.Extractor));

                try
                {
                    await RunWithAbortRetryAsync(
                        ytDlpPath, ffmpegPath, attemptUrl, attemptItems,
                        format, videoQuality, audioQuality,
                        attemptOrganize, itemSponsor, isPlaylist: false, destDir, archivePath,
                        relaxed, embedExtras, cookiesFromBrowser, onLine, onProgress, permanentFailures, ct);

                    if (!string.IsNullOrEmpty(entry.Id))
                        ok = ArchiveContains(archivePath, entry.Id);
                    else
                        ok = true;
                }
                catch (InvalidOperationException ex)
                {
                    onLine($">>   fallo: {ex.Message}");
                }

                if (!ok && !string.IsNullOrEmpty(entry.Id) && ArchiveContains(archivePath, entry.Id))
                    ok = true;
            }

            if (!ok)
            {
                remainingFails++;
                onLine($">>   NO se pudo descargar: {entry.Title}");
            }
            else
            {
                onLine($">>   OK: {entry.Title}");
            }
        }

        onProgress(100);
        var finalMissing = CountMissing(entries, archivePath, permanentFailures);
        var totalFailed = Math.Max(finalMissing, remainingFails);
        if (totalFailed == 0)
            onLine(">> Todos los items se descargaron correctamente.");
        else
            onLine($">> Listo con {totalFailed} item(s) que no se pudieron bajar (privados, eliminados o bloqueados).");

        return totalFailed;
    }

    private static string? ResolveDirectUrl(PlaylistEntry entry, string listUrl)
    {
        if (!string.IsNullOrWhiteSpace(entry.WebpageUrl)
            && Uri.TryCreate(entry.WebpageUrl, UriKind.Absolute, out var u)
            && u.Scheme is "http" or "https")
            return entry.WebpageUrl;

        var built = BuildFallbackUrl(entry.Extractor, entry.Id);
        if (built != null) return built;

        // Single-item job: the pasted URL itself.
        if (!string.IsNullOrWhiteSpace(listUrl)) return listUrl;
        return null;
    }

    private static async Task<int> RunWithAbortRetryAsync(
        string ytDlpPath, string ffmpegPath, string url, List<int>? playlistItems,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, bool isPlaylist, string destDir, string archivePath,
        bool relaxed, bool embedExtras, string cookiesFromBrowser,
        Action<string> onLine, Action<int> onProgress, HashSet<string> permanentFailures,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lastExitCode = 0;
            try
            {
                return await RunOnceAsync(
                    ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                    organizeInFolders, sponsorBlock, isPlaylist, destDir, archivePath,
                    relaxed, embedExtras, cookiesFromBrowser, onLine, onProgress, permanentFailures,
                    code => lastExitCode = code, ct);
            }
            catch (InvalidOperationException) when (lastExitCode < 0 && attempt <= MaxAbortRetries)
            {
                var waitSecs = attempt * 12;
                onLine($">> Conexion cortada (posible rate-limit del sitio). Reintentando en {waitSecs}s... ({attempt}/{MaxAbortRetries})");
                await Task.Delay(TimeSpan.FromSeconds(waitSecs), ct);
            }
        }
    }

    private static async Task<int> RunOnceAsync(
        string ytDlpPath, string ffmpegPath, string url, List<int>? playlistItems,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, bool isPlaylist, string destDir, string archivePath,
        bool relaxed, bool embedExtras, string cookiesFromBrowser,
        Action<string> onLine, Action<int> onProgress, HashSet<string> permanentFailures,
        Action<int> onExitCode, CancellationToken ct)
    {
        var extraArgs = new List<string>();
        var formatSelector = BuildFormatSelector(format, videoQuality, audioQuality, extraArgs, relaxed);

        // %(extractor)s helps avoid filename clashes when mixing sites into one folder.
        var outputTemplate = organizeInFolders
            ? "%(playlist_title)s/%(title)s [%(id)s].%(ext)s"
            : "%(title)s [%(id)s].%(ext)s";

        var args = new List<string>
        {
            "--newline", "--no-warnings",
            "--ffmpeg-location", ffmpegPath,
            "-f", formatSelector,
            "--output-na-placeholder", "",
            "-o", $"{destDir.Replace('\\', '/')}/{outputTemplate}",
            "--ignore-errors",
            "--no-abort-on-error",
            "--no-overwrites",
            "--continue",
            "--download-archive", archivePath,
            // Geo / site quirks: let the extractor pick the right client when it can.
            "--retries", "15",
            "--fragment-retries", "15",
            "--extractor-retries", "5",
            "--file-access-retries", "8",
            "--retry-sleep", "http:linear=1:5:1",
            "--retry-sleep", "fragment:exp=1:20",
            "--retry-sleep", "extractor:exp=1:10",
            "--socket-timeout", "30",
            "--concurrent-fragments", "1",
            // Prefer the page's default language / geo when the site offers variants.
            "--geo-bypass",
        };

        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(cookiesFromBrowser.Trim().ToLowerInvariant());
        }

        if (embedExtras)
        {
            // Thumbnail embed fails harmlessly on many sites; metadata is still useful.
            args.AddRange(["--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg"]);
        }
        else
        {
            args.Add("--embed-metadata");
        }

        if (isPlaylist)
        {
            args.AddRange([
                "--sleep-requests", "1",
                "--sleep-interval", "2",
                "--max-sleep-interval", "6",
            ]);
        }

        // SponsorBlock is YouTube-only; caller already gates this flag.
        if (sponsorBlock)
            args.AddRange(["--sponsorblock-remove", "sponsor,selfpromo,interaction"]);

        args.AddRange(extraArgs);

        if (playlistItems is { Count: > 0 })
            args.AddRange(["--playlist-items", string.Join(",", playlistItems)]);

        args.Add(url);

        var psi = new ProcessStartInfo { FileName = ytDlpPath };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var softErrorCount = 0;
        string? lastVideoHint = null;

        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);

            var pm = ProgressRegex.Match(line);
            if (pm.Success
                && double.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                onProgress((int)pct);
            }

            var im = ItemRegex.Match(line);
            if (im.Success)
            {
                onLine($">> Item {im.Groups[1].Value} de {im.Groups[2].Value}");
                lastVideoHint = im.Groups[1].Value;
            }

            // Generic extractor id tracking: [tiktok] 7123…: Downloading…
            var em = ExtractorIdRegex.Match(line);
            if (em.Success)
                lastVideoHint = em.Groups[2].Value;

            if (!ErrorLineRegex.IsMatch(line)) return;
            if (IsIgnorableError(line)) return;

            if (IsPermanentErrorMessage(line))
            {
                var id = ExtractIdFromError(line) ?? lastVideoHint;
                if (!string.IsNullOrEmpty(id) && id.Length >= 2)
                    permanentFailures.Add(id);
                onLine($">>   (no disponible, no se reintentara): {Truncate(line, 160)}");
                return;
            }

            softErrorCount++;
        }, exitCode =>
        {
            onExitCode(exitCode);
            var hint = exitCode < 0
                ? " Conexion cortada a mitad de descarga; se reintenta. Lo ya bajado queda en el archivo y no se vuelve a bajar."
                : "";
            return $"yt-dlp termino con codigo {exitCode}.{hint}";
        }, ct, throwOnPositiveExit: false);

        return softErrorCount;
    }

    private static bool IsIgnorableError(string line)
    {
        ReadOnlySpan<string> noise =
        [
            "has already been recorded in the archive",
            "has already been downloaded",
            "Exporting cookie data",
            "Skipping embedding",
            "Unable to embed thumbnail",
            "ERROR: Postprocessing: Unsupported codec",
            "There are no subtitles",
        ];
        foreach (var n in noise)
        {
            if (line.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsPermanentErrorMessage(string line)
    {
        ReadOnlySpan<string> markers =
        [
            "Private video",
            "private video",
            "Video unavailable",
            "This video is not available",
            "has been removed",
            "account associated with this video has been terminated",
            "is no longer available",
            "violating YouTube",
            "copyright claim",
            "members-only content",
            "Join this channel to get access",
            "This live event will begin",
            "Premieres in",
            "Sign in to confirm your age",
            "confirm your age",
            "not made this video available in your country",
            "The uploader has not made this video available",
            "[Deleted video]",
            "[Private video]",
            "HTTP Error 404",
            "404 Not Found",
            "Unable to extract", // often permanent page structure / removed
            "Unsupported URL",
            "No video formats found",
            "is not a valid URL",
            "This post is unavailable",
            "Content is not available",
            "only available for registered users",
            "Login required",
            "requires authentication",
            "DRM protected",
            "This content isn't available",
            "status is private",
            "User is private",
        ];
        foreach (var m in markers)
        {
            if (line.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? ExtractIdFromError(string line)
    {
        // ERROR: [tiktok] 7123: …  / ERROR: [youtube] abc: …
        var m = ExtractorIdRegex.Match(line);
        if (m.Success) return m.Groups[2].Value;
        m = Regex.Match(line, @"\[([A-Za-z0-9_-]+)\]\s+([A-Za-z0-9_.%-]{2,})");
        return m.Success ? m.Groups[2].Value : null;
    }

    private static bool IsPermanent(PlaylistEntry e, HashSet<string> permanent)
    {
        if (!string.IsNullOrEmpty(e.Id) && permanent.Contains(e.Id)) return true;
        if (e.Title is "[Deleted video]" or "[Private video]") return true;
        return false;
    }

    private static int CountMissing(List<PlaylistEntry> entries, string archivePath, HashSet<string> permanent) =>
        GetMissing(entries, archivePath, permanent).Count;

    private static List<PlaylistEntry> GetMissing(
        List<PlaylistEntry> entries, string archivePath, HashSet<string> permanent)
    {
        var archived = ReadArchiveIds(archivePath);
        var missing = new List<PlaylistEntry>();
        foreach (var e in entries)
        {
            if (IsPermanent(e, permanent)) continue;
            if (!string.IsNullOrEmpty(e.Id))
            {
                if (!archived.Contains(e.Id)) missing.Add(e);
            }
            else
            {
                missing.Add(e);
            }
        }
        return missing;
    }

    private static HashSet<string> ReadArchiveIds(string archivePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(archivePath)) return set;
        try
        {
            foreach (var line in File.ReadLines(archivePath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var m = ArchiveIdRegex.Match(line.Trim());
                if (m.Success) set.Add(m.Groups[1].Value);
            }
        }
        catch { /* locked */ }
        return set;
    }

    private static bool ArchiveContains(string archivePath, string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return ReadArchiveIds(archivePath).Contains(id);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

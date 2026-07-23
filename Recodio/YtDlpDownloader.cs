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

    public static async Task<AnalyzeResult> AnalyzeAsync(
        string ytDlpPath, string url, CancellationToken ct, string cookiesFromBrowser = "")
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
        var args = new List<string>
        {
            "--flat-playlist", "-J", "--no-warnings",
            "--ignore-no-formats-error", // some flat entries have no formats yet
        };
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(cookiesFromBrowser.Trim().ToLowerInvariant());
        }
        args.Add(url);
        foreach (var a in args) psi.ArgumentList.Add(a);

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
            // yt-dlp --playlist-items is 1-based over the ORIGINAL entry list (including nulls).
            var playlistIndex = 0;
            foreach (var e in entriesEl.EnumerateArray())
            {
                playlistIndex++;
                if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                var vTitle = ReadString(e, "title") ?? $"Item {playlistIndex}";
                var vId = ReadString(e, "id") ?? "";
                var vUrl = ReadString(e, "webpage_url")
                    ?? ReadString(e, "url")
                    ?? "";
                var vExt = ReadString(e, "extractor_key")
                    ?? ReadString(e, "ie_key")
                    ?? ReadString(e, "extractor")
                    ?? rootExtractor;

                if (string.IsNullOrEmpty(vId) && vTitle is "[Deleted video]" or "[Private video]")
                    continue;

                // Flat entries sometimes only have id — build a usable URL for known extractors.
                if (string.IsNullOrWhiteSpace(vUrl) || !LooksLikeHttpUrl(vUrl))
                    vUrl = BuildFallbackUrl(vExt, vId) ?? (LooksLikeHttpUrl(vUrl) ? vUrl : "");

                // Synthetic stable id when the extractor omitted one (needed for archive tracking).
                if (string.IsNullOrWhiteSpace(vId))
                    vId = !string.IsNullOrWhiteSpace(vUrl) ? "url:" + vUrl : $"idx:{playlistIndex}";

                list.Add(new PlaylistEntry(playlistIndex, vTitle, vId, vUrl, vExt));
            }

            if (list.Count == 0)
                throw new InvalidOperationException(
                    "La URL se reconocio como lista/playlist pero no tiene items descargables.");

            return new AnalyzeResult(true, title, list, rootExtractor, rootWebpage);
        }

        var singleTitle = ReadString(root, "title") ?? url;
        var singleId = ReadString(root, "id") ?? "";
        if (string.IsNullOrWhiteSpace(singleId))
            singleId = "url:" + rootWebpage;
        return new AnalyzeResult(
            false, null,
            [new PlaylistEntry(1, singleTitle, singleId, rootWebpage, rootExtractor)],
            rootExtractor, rootWebpage);
    }

    private static bool LooksLikeHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    // Best-effort direct URL when flat-playlist omitted webpage_url.
    private static string? BuildFallbackUrl(string extractor, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("url:", StringComparison.Ordinal) || id.StartsWith("idx:", StringComparison.Ordinal))
            return null;
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
        string? abrFilter = relaxed ? null : audioQuality switch
        {
            "medium" => "192",
            "low" => "128",
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

        var audioSel = abrFilter != null
            ? $"bestaudio[abr<={abrFilter}]/bestaudio*/bestaudio"
            : "bestaudio*/bestaudio";

        // Keep site-native container when possible (webm, flv, …).
        if (format == "best")
        {
            if (heightFilter != null)
                return $"bestvideo*[height<={heightFilter}]+{audioSel}/best[height<={heightFilter}]/best";
            return $"bestvideo*+{audioSel}/best/bestvideo/bestaudio";
        }

        // Remux/merge into mp4 or mkv. Always end with plain `best` for progressive-only sites.
        var merge = format == "mkv" ? "mkv" : "mp4";
        extraArgs.AddRange(["--merge-output-format", merge]);

        if (heightFilter != null)
        {
            return $"bestvideo*[height<={heightFilter}]+{audioSel}/"
                 + $"best[height<={heightFilter}]/"
                 + $"bestvideo*+{audioSel}/best/bestvideo/bestaudio";
        }

        return $"bestvideo*+{audioSel}/best/bestvideo/bestaudio";
    }

    // cookiesFromBrowser: "" or chrome|edge|firefox|brave|…
    // Returns number of items that still failed after all retries (includes permanent).
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
            ? selectedEntries.Select(NormalizeEntry).ToList()
            : [new PlaylistEntry(1, url, "url:" + url, url, "")];

        // True when the original URL is a multi-item page OR the user selected items that
        // carry playlist indices. Even a SINGLE checked item from a playlist must use
        // --playlist-items N (otherwise yt-dlp would re-download the whole list).
        var usePlaylistItems = entries.Any(e => e.Index > 0)
            && (entries.Count > 1 || entries[0].Index > 0 && LooksLikePlaylistContext(url, entries));
        // Safer rule: always pass --playlist-items when we have analyzed entries with real
        // indices and the page URL is not itself a single-item direct URL for that entry.
        usePlaylistItems = ShouldUsePlaylistItems(url, entries);

        var permanentFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                return permanentFailures.Count;
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

            // Only the still-missing, non-permanent indices — avoids re-walking archived items
            // and never drops --playlist-items when a single track was selected from a list.
            List<int>? playlistItems = null;
            if (usePlaylistItems)
            {
                playlistItems = GetMissing(entries, archivePath, permanentFailures)
                    .Select(e => e.Index)
                    .Where(i => i > 0)
                    .Distinct()
                    .OrderBy(i => i)
                    .ToList();
                if (playlistItems.Count == 0)
                {
                    onLine(">> Nada pendiente para --playlist-items.");
                    break;
                }
            }

            var relaxed = pass >= 3;
            var embedExtras = pass < 3;
            var isPlaylistPace = usePlaylistItems && (playlistItems?.Count ?? 0) > 1;

            try
            {
                await RunWithAbortRetryAsync(
                    ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                    organizeInFolders, useSponsorBlock, isPlaylistPace, destDir, archivePath,
                    relaxed, embedExtras, cookiesFromBrowser, onLine, onProgress, permanentFailures, ct);
            }
            catch (InvalidOperationException ex)
            {
                onLine($">> Pasada {pass} termino con error: {ex.Message}");
            }

            // After a batch, mark successful downloads into our synthetic archive keys too
            // (yt-dlp only writes real extractor ids; url:/idx: keys need file presence checks).
            SyncSyntheticArchive(entries, destDir, archivePath);

            var missingAfter = CountMissing(entries, archivePath, permanentFailures);
            onLine($">> Pasada {pass} lista: {entries.Count - missingAfter - permanentFailures.Count}/{entries.Count} ok"
                + (permanentFailures.Count > 0 ? $", {permanentFailures.Count} no disponibles" : "")
                + (missingAfter > 0 ? $", {missingAfter} pendientes" : "")
                + ".");

            if (missingAfter == 0) return permanentFailures.Count;
            if (pass >= 2 && missingAfter >= missingBefore) break;
        }

        // ----- Phase B: one-by-one for whatever is still missing -----
        var stillMissing = GetMissing(entries, archivePath, permanentFailures);
        if (stillMissing.Count == 0) return permanentFailures.Count;

        onLine($">> Reintentando {stillMissing.Count} item(s) uno por uno...");

        for (var i = 0; i < stillMissing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = stillMissing[i];
            onProgress((int)(100.0 * i / stillMissing.Count));

            if (IsPermanent(entry, permanentFailures))
                continue;

            var directUrl = ResolveDirectUrl(entry, url);
            // Prefer direct item URL when we have one; else playlist URL + single index.
            var canPlaylistIndex = usePlaylistItems && entry.Index > 0;

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
                // Attempt 1-2: playlist index (keeps folder layout) when possible.
                // Attempt 3+: direct webpage URL (bypasses flaky playlist extract).
                var useDirect = (attempt >= 3 || !canPlaylistIndex) && !string.IsNullOrWhiteSpace(directUrl);
                string attemptUrl;
                List<int>? attemptItems;
                if (useDirect)
                {
                    attemptUrl = directUrl!;
                    attemptItems = null;
                }
                else if (canPlaylistIndex)
                {
                    attemptUrl = url;
                    attemptItems = [entry.Index];
                }
                else
                {
                    attemptUrl = directUrl ?? url;
                    attemptItems = null;
                }

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
                }
                catch (InvalidOperationException ex)
                {
                    onLine($">>   fallo: {ex.Message}");
                }

                SyncSyntheticArchive([entry], destDir, archivePath);
                ok = IsArchived(entry, archivePath);
            }

            if (!ok)
                onLine($">>   NO se pudo descargar: {entry.Title}");
            else
                onLine($">>   OK: {entry.Title}");
        }

        onProgress(100);
        // Source of truth: anything not in the archive (and not successfully synced) failed.
        var totalFailed = entries.Count(e => !IsArchived(e, archivePath));

        if (totalFailed == 0)
            onLine(">> Todos los items se descargaron correctamente.");
        else
            onLine($">> Listo con {totalFailed} item(s) que no se pudieron bajar (privados, eliminados o bloqueados).");

        return totalFailed;
    }

    private static PlaylistEntry NormalizeEntry(PlaylistEntry e)
    {
        var id = e.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = !string.IsNullOrWhiteSpace(e.WebpageUrl) && LooksLikeHttpUrl(e.WebpageUrl)
                ? "url:" + e.WebpageUrl
                : $"idx:{e.Index}";
        }
        return e with { Id = id };
    }

    // Use --playlist-items whenever the user picked analyze results that refer to positions
    // on the original URL (playlist/channel/set). A single selected track still needs it.
    private static bool ShouldUsePlaylistItems(string pageUrl, List<PlaylistEntry> entries)
    {
        if (entries.Count == 0) return false;
        // All entries from analyze have Index >= 1.
        if (entries.All(e => e.Index <= 0)) return false;
        // Single entry that is the page itself (no playlist context): don't force playlist-items.
        if (entries.Count == 1)
        {
            var e = entries[0];
            // If webpage of entry equals the pasted URL, it's a single video page.
            if (!string.IsNullOrWhiteSpace(e.WebpageUrl)
                && string.Equals(e.WebpageUrl.TrimEnd('/'), pageUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return false;
            // Synthetic single from non-playlist analyze: Index==1, id may match.
            if (e.Index == 1 && !LooksLikePlaylistContext(pageUrl, entries))
                return false;
            // Selected one item from a multi-item page → must use playlist-items.
            return LooksLikePlaylistContext(pageUrl, entries) || e.Index > 1;
        }
        return true;
    }

    private static bool LooksLikePlaylistContext(string url, List<PlaylistEntry> entries)
    {
        if (url.Contains("list=", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("/playlist", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("/channel/", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("/c/", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("/sets/", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("/album/", StringComparison.OrdinalIgnoreCase)) return true;
        // Analyze returned multiple OR any entry has list-ish index > 1.
        if (entries.Count > 1) return true;
        if (entries.Any(e => e.Index > 1)) return true;
        return false;
    }

    private static string? ResolveDirectUrl(PlaylistEntry entry, string listUrl)
    {
        if (!string.IsNullOrWhiteSpace(entry.WebpageUrl) && LooksLikeHttpUrl(entry.WebpageUrl))
            return entry.WebpageUrl;

        var built = BuildFallbackUrl(entry.Extractor, entry.Id);
        if (built != null) return built;

        if (entry.Id.StartsWith("url:", StringComparison.Ordinal) && entry.Id.Length > 4)
            return entry.Id["url:".Length..];

        if (!string.IsNullOrWhiteSpace(listUrl) && !LooksLikePlaylistContext(listUrl, [entry]))
            return listUrl;
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
            "--retries", "15",
            "--fragment-retries", "15",
            "--extractor-retries", "5",
            "--file-access-retries", "8",
            "--retry-sleep", "http:linear=1:5:1",
            "--retry-sleep", "fragment:exp=1:20",
            "--retry-sleep", "extractor:exp=1:10",
            "--socket-timeout", "30",
            "--concurrent-fragments", "1",
            "--geo-bypass",
        };

        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(cookiesFromBrowser.Trim().ToLowerInvariant());
        }

        if (embedExtras)
            args.AddRange(["--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg"]);
        else
            args.Add("--embed-metadata");

        if (isPlaylist)
        {
            args.AddRange([
                "--sleep-requests", "1",
                "--sleep-interval", "2",
                "--max-sleep-interval", "6",
            ]);
        }

        if (sponsorBlock)
            args.AddRange(["--sponsorblock-remove", "sponsor,selfpromo,interaction"]);

        args.AddRange(extraArgs);

        if (playlistItems is { Count: > 0 })
            args.AddRange(["--playlist-items", string.Join(",", playlistItems)]);

        args.Add(url);

        var psi = new ProcessStartInfo { FileName = ytDlpPath };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var softErrorCount = 0;
        string? lastExtractorId = null;

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
                onLine($">> Item {im.Groups[1].Value} de {im.Groups[2].Value}");

            // Only track real extractor ids — never the "Downloading item N of M" number.
            var em = ExtractorIdRegex.Match(line);
            if (em.Success)
                lastExtractorId = em.Groups[2].Value;

            if (!ErrorLineRegex.IsMatch(line)) return;
            if (IsIgnorableError(line)) return;

            if (IsPermanentErrorMessage(line))
            {
                var id = ExtractIdFromError(line) ?? lastExtractorId;
                // Reject junk like pure digits from item counters (length < 3 or all digits short).
                if (!string.IsNullOrEmpty(id) && id.Length >= 3 && !IsLikelyItemCounter(id))
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

    private static bool IsLikelyItemCounter(string id) =>
        id.Length <= 4 && int.TryParse(id, out _);

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
        // Deliberately NOT treating "Login required" / "Unable to extract" / age-gate as
        // permanent: cookies or a later pass often fix those.
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
            "not made this video available in your country",
            "The uploader has not made this video available",
            "[Deleted video]",
            "[Private video]",
            "HTTP Error 404",
            "404 Not Found",
            "Unsupported URL",
            "is not a valid URL",
            "This post is unavailable",
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
        var m = ExtractorIdRegex.Match(line);
        if (m.Success) return m.Groups[2].Value;
        m = Regex.Match(line, @"\[([A-Za-z0-9_-]+)\]\s+([A-Za-z0-9_.%-]{3,})");
        return m.Success ? m.Groups[2].Value : null;
    }

    private static bool IsPermanent(PlaylistEntry e, HashSet<string> permanent)
    {
        if (!string.IsNullOrEmpty(e.Id) && permanent.Contains(e.Id)) return true;
        // Also match bare extractor id if our entry id is the raw id.
        if (permanent.Contains(e.Id)) return true;
        if (e.Title is "[Deleted video]" or "[Private video]") return true;
        return false;
    }

    private static int CountMissing(List<PlaylistEntry> entries, string archivePath, HashSet<string> permanent) =>
        GetMissing(entries, archivePath, permanent).Count;

    private static List<PlaylistEntry> GetMissing(
        List<PlaylistEntry> entries, string archivePath, HashSet<string> permanent)
    {
        var missing = new List<PlaylistEntry>();
        foreach (var e in entries)
        {
            if (IsPermanent(e, permanent)) continue;
            if (!IsArchived(e, archivePath)) missing.Add(e);
        }
        return missing;
    }

    private static bool IsArchived(PlaylistEntry e, string archivePath)
    {
        var archived = ReadArchiveIds(archivePath);
        if (!string.IsNullOrEmpty(e.Id))
        {
            if (archived.Contains(e.Id)) return true;
            // yt-dlp writes bare id; we may store "url:…" — also match bare id if Id is plain.
            if (!e.Id.Contains(':') && archived.Contains(e.Id)) return true;
            // Entry id is youtube-style, archive has same.
            if (e.Id.StartsWith("url:", StringComparison.Ordinal))
            {
                // Synthetic keys: also treat as archived if we wrote them after file detect.
                if (archived.Contains(e.Id)) return true;
            }
        }
        return false;
    }

    // For entries with synthetic ids (url:/idx:), yt-dlp won't write them into the archive.
    // After a run, if a matching media file appeared under destDir, record the synthetic key
    // so we don't retry forever.
    private static void SyncSyntheticArchive(IEnumerable<PlaylistEntry> entries, string destDir, string archivePath)
    {
        var archived = ReadArchiveIds(archivePath);
        var toAppend = new List<string>();
        string[]? files = null;

        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Id)) continue;
            if (archived.Contains(e.Id)) continue;

            // Real extractor ids: if present in archive under any extractor prefix, OK.
            if (!e.Id.StartsWith("url:", StringComparison.Ordinal) && !e.Id.StartsWith("idx:", StringComparison.Ordinal))
            {
                if (archived.Contains(e.Id)) continue;
                // Bare id already handled by yt-dlp archive write; nothing to sync.
                continue;
            }

            // Synthetic: look for a file containing the title or id fragment.
            files ??= SafeEnumerateMedia(destDir);
            var titleHint = SanitizeFileHint(e.Title);
            var idHint = e.Id.StartsWith("url:", StringComparison.Ordinal)
                ? ""
                : e.Id;
            var found = files.Any(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!string.IsNullOrEmpty(titleHint) && name.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(e.Id) && name.Contains(e.Id, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            });
            if (found)
                toAppend.Add($"recodio {e.Id}");
        }

        if (toAppend.Count == 0) return;
        try
        {
            File.AppendAllLines(archivePath, toAppend);
        }
        catch { /* locked */ }
    }

    private static string[] SafeEnumerateMedia(string destDir)
    {
        try
        {
            if (!Directory.Exists(destDir)) return [];
            return Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".mp4" or ".mkv" or ".webm" or ".mp3" or ".m4a" or ".opus"
                        or ".ogg" or ".flac" or ".wav" or ".avi" or ".mov";
                })
                .ToArray();
        }
        catch { return []; }
    }

    private static string SanitizeFileHint(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var chars = title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray();
        var s = new string(chars).Trim();
        return s.Length > 40 ? s[..40] : s;
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

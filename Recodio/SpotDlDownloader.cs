using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recodio;

public record SpotDlTrack(
    string Name,
    string Artist,
    string Url,
    string SongId,
    int DurationSec,
    string? ListName);

public record SpotDlAnalyzeResult(
    string Query,
    string? ListName,
    List<SpotDlTrack> Tracks);

// Wraps the spotdl CLI (Spotify metadata + YouTube Music / YouTube / SoundCloud audio).
// Separate from YtDlpDownloader: different backend, archive, and settings.
public static class SpotDlDownloader
{
    public static readonly string[] Formats = ["mp3", "flac", "ogg", "opus", "m4a", "wav"];
    public static readonly string[] Bitrates =
        ["auto", "disable", "96k", "128k", "160k", "192k", "224k", "256k", "320k"];

    public static readonly string[] AudioProviders =
        ["youtube-music", "youtube", "soundcloud", "bandcamp", "piped"];

    // Default fallback chain: YTM first (best metadata match), then YT, then SoundCloud.
    public static readonly string[] DefaultAudioProviders =
        ["youtube-music", "youtube", "soundcloud"];

    public const int MaxThreads = 32;

    private const string ArchiveFileName = ".recodio-spotdl-archive.spotdl";
    private const int MaxRateLimitRetries = 3;
    private const int MaxBatchPasses = 3;
    private const int MaxPerTrackAttempts = 3;
    private const int SpotifyApiMaxRetries = 6;

    private static readonly Regex CompleteRegex = new(@"^\s*(\d+)/(\d+)\s+complete\s*$", RegexOptions.Compiled);
    private static readonly Regex ErrorLineRegex = new(@"^[A-Za-z][A-Za-z0-9_]*Error: ", RegexOptions.Compiled);
    // save-errors lines: "https://open.spotify.com/track/... - ExceptionName: message"
    private static readonly Regex SaveErrorUrlRegex = new(
        @"^(https://open\.spotify\.com/\S+)\s+-\s+", RegexOptions.Compiled);

    public static string ArchivePathFor(string destDir) => Path.Combine(destDir, ArchiveFileName);

    public static string ResolveSpotDlPath()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "Scripts", "spotdl.exe");
        return File.Exists(candidate) ? candidate : "spotdl";
    }

    public static IReadOnlyList<string> ParseProvidersCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return DefaultAudioProviders;
        var list = csv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => AudioProviders.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count > 0 ? list : DefaultAudioProviders;
    }

    // ----- Analyze / preview (spotdl save → .spotdl JSON) -----

    public static async Task<SpotDlAnalyzeResult> AnalyzeAsync(
        string spotdlPath, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query vacia.", nameof(query));

        var tempDir = Path.Combine(Path.GetTempPath(), "RecodioSpotDl");
        Directory.CreateDirectory(tempDir);
        var savePath = Path.Combine(tempDir, $"preview_{Guid.NewGuid():N}.spotdl");

        try
        {
            var psi = new ProcessStartInfo { FileName = spotdlPath };
            // Metadata-only: no audio download. File must end with .spotdl.
            psi.ArgumentList.Add("--simple-tui");
            psi.ArgumentList.Add("--save-file");
            psi.ArgumentList.Add(savePath);
            psi.ArgumentList.Add("--max-retries");
            psi.ArgumentList.Add(SpotifyApiMaxRetries.ToString());
            psi.ArgumentList.Add("save");
            psi.ArgumentList.Add(query);

            await ProcessRunner.RunAsync(psi, _ => { /* quiet analyze */ },
                code => $"spotdl save termino con codigo {code}", ct);

            if (!File.Exists(savePath))
                throw new InvalidOperationException("spotDL no genero el archivo de preview.");

            var json = await File.ReadAllTextAsync(savePath, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Formato de preview inesperado.");

            var tracks = new List<SpotDlTrack>();
            string? listName = null;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                // Preload failures can insert null entries in the array.
                var name = ReadStr(el, "name") ?? "Sin nombre";
                string artist = ReadStr(el, "artist") ?? "";
                if (string.IsNullOrEmpty(artist)
                    && el.TryGetProperty("artists", out var arts)
                    && arts.ValueKind == JsonValueKind.Array)
                {
                    artist = string.Join(", ",
                        arts.EnumerateArray().Select(a => a.GetString()).Where(s => !string.IsNullOrEmpty(s)));
                }
                var url = ReadStr(el, "url") ?? "";
                var id = ReadStr(el, "song_id") ?? "";
                var duration = el.TryGetProperty("duration", out var d) && d.TryGetInt32(out var sec) ? sec : 0;
                var ln = ReadStr(el, "list_name");
                if (!string.IsNullOrEmpty(ln)) listName ??= ln;
                if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(name)) continue;
                tracks.Add(new SpotDlTrack(name, artist, url, id, duration, ln));
            }

            if (tracks.Count == 0)
                throw new InvalidOperationException(
                    "No se encontraron canciones. ¿La URL/busqueda es valida y publica?");

            return new SpotDlAnalyzeResult(query, listName, tracks);
        }
        finally
        {
            try { if (File.Exists(savePath)) File.Delete(savePath); } catch { /* ignore */ }
        }
    }

    private static string? ReadStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ----- Download with multi-pass + per-track recovery -----
    //
    // trackUrls: if non-null/non-empty, only those Spotify track URLs are downloaded
    // (from the Analizar checklist). Otherwise `query` is used as-is (playlist/album/search).
    //
    // Returns the number of tracks that still failed after all retries.

    public static async Task<int> DownloadAsync(
        string spotdlPath, string ffmpegPath, string query,
        IReadOnlyList<string>? trackUrls,
        string format, string bitrate,
        bool embedLyrics, int threads, bool skipExisting, bool organizeInFolders, bool sponsorBlock,
        string destDir,
        IReadOnlyList<string>? audioProviders,
        string cookiesFromBrowser,
        Action<string> onLine, Action<int, int> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var archivePath = ArchivePathFor(destDir);
        var providers = audioProviders is { Count: > 0 } ? audioProviders : DefaultAudioProviders;
        var knownTracks = trackUrls is { Count: > 0 }
            ? trackUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : null;

        onLine($">> Proveedores de audio: {string.Join(" → ", providers)}");
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
            onLine($">> Cookies de {cookiesFromBrowser} (via yt-dlp) para menos bloqueos de YouTube.");

        var attemptThreads = Math.Clamp(threads, 1, MaxThreads);
        var permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ----- Phase A: batch passes (archive skips finished tracks) -----
        for (var pass = 1; pass <= MaxBatchPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            if (knownTracks != null)
            {
                var missing = GetMissingUrls(knownTracks, archivePath, permanent);
                if (missing.Count == 0)
                {
                    onLine(">> Todas las canciones seleccionadas ya estan en el archivo.");
                    onProgress(knownTracks.Count, knownTracks.Count);
                    return 0;
                }
                if (pass > 1)
                {
                    var wait = Math.Min(5 + pass * 3, 20);
                    onLine($">> Reintento batch {pass}/{MaxBatchPasses}: faltan {missing.Count}. Esperando {wait}s...");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                else
                {
                    onLine($">> Descargando {missing.Count} cancion(es) seleccionada(s)...");
                }

                // On later passes: fewer threads + full provider chain already set.
                if (pass >= 2) attemptThreads = Math.Max(1, Math.Min(attemptThreads, 2));
                if (pass >= 3) attemptThreads = 1;

                try
                {
                    await RunWithRateLimitRetryAsync(
                        spotdlPath, ffmpegPath, queries: missing, format, bitrate, embedLyrics,
                        attemptThreads, skipExisting, organizeInFolders, sponsorBlock, destDir,
                        archivePath, providers, cookiesFromBrowser,
                        onLine, onProgress, permanent, ct);
                }
                catch (InvalidOperationException ex)
                {
                    onLine($">> Pasada {pass}: {ex.Message}");
                }

                var still = GetMissingUrls(knownTracks, archivePath, permanent).Count;
                onLine($">> Pasada {pass}: {knownTracks.Count - still}/{knownTracks.Count} en archivo.");
                if (still == 0) return permanent.Count;
            }
            else
            {
                // Whole query (playlist/search) — re-run is cheap thanks to --archive.
                if (pass > 1)
                {
                    var wait = Math.Min(5 + pass * 3, 20);
                    onLine($">> Reintento de query {pass}/{MaxBatchPasses}. Esperando {wait}s...");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    attemptThreads = Math.Max(1, attemptThreads / 2);
                }
                else
                {
                    onLine($">> Descargando: {query}");
                }

                var errorsPath = Path.Combine(Path.GetTempPath(), $"recodio-spotdl-errors-{Guid.NewGuid():N}.txt");
                try
                {
                    if (File.Exists(errorsPath)) File.Delete(errorsPath);
                    var failCount = await RunWithRateLimitRetryAsync(
                        spotdlPath, ffmpegPath, queries: [query], format, bitrate, embedLyrics,
                        attemptThreads, skipExisting, organizeInFolders, sponsorBlock, destDir,
                        archivePath, providers, cookiesFromBrowser,
                        onLine, onProgress, permanent, ct, errorsPath);

                    var failedUrls = ReadFailedUrls(errorsPath);
                    foreach (var u in failedUrls)
                    {
                        // Keep for phase B; don't mark permanent yet.
                    }

                    if (failCount == 0 && failedUrls.Count == 0)
                    {
                        onLine(">> Query completa sin errores reportados.");
                        return 0;
                    }

                    // If we collected concrete track URLs from errors, switch to knownTracks mode.
                    if (failedUrls.Count > 0)
                    {
                        knownTracks = failedUrls;
                        onLine($">> {failedUrls.Count} cancion(es) con error; se reintentaran individualmente.");
                        // continue loop / fall into phase B via knownTracks
                        if (pass >= MaxBatchPasses) break;
                        continue;
                    }

                    if (failCount == 0) return 0;
                }
                finally
                {
                    try { if (File.Exists(errorsPath)) File.Delete(errorsPath); } catch { /* ignore */ }
                }
            }
        }

        // ----- Phase B: one-by-one for remaining track URLs -----
        if (knownTracks == null || knownTracks.Count == 0)
            return 0;

        var stillMissing = GetMissingUrls(knownTracks, archivePath, permanent);
        if (stillMissing.Count == 0) return permanent.Count;

        onLine($">> Reintentando {stillMissing.Count} cancion(es) una por una (1 hilo)...");
        var remainingFails = 0;

        // Last-resort providers: ensure youtube is present even if user unchecked it.
        var rescueProviders = providers.ToList();
        foreach (var p in new[] { "youtube-music", "youtube", "soundcloud" })
        {
            if (!rescueProviders.Contains(p, StringComparer.OrdinalIgnoreCase))
                rescueProviders.Add(p);
        }

        for (var i = 0; i < stillMissing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var url = stillMissing[i];
            onProgress(i, stillMissing.Count);

            if (permanent.Contains(url))
            {
                remainingFails++;
                continue;
            }

            var ok = false;
            for (var attempt = 1; attempt <= MaxPerTrackAttempts && !ok; attempt++)
            {
                if (attempt > 1)
                {
                    var wait = attempt * 4;
                    onLine($">>   reintento {attempt}/{MaxPerTrackAttempts} en {wait}s...");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                else
                {
                    onLine($">> [{i + 1}/{stillMissing.Count}] {url}");
                }

                var useCookies = cookiesFromBrowser;
                // On last attempt force cookies if none set? No — user choice. Just full providers.
                try
                {
                    await RunOnceAsync(
                        spotdlPath, ffmpegPath, [url], format, bitrate, embedLyrics,
                        threads: 1, skipExisting, organizeInFolders, sponsorBlock, destDir,
                        archivePath, rescueProviders, useCookies,
                        onLine, onProgress, permanent, errorsPath: null, ct);

                    ok = ArchiveContains(archivePath, url);
                }
                catch (InvalidOperationException ex)
                {
                    onLine($">>   fallo: {ex.Message}");
                    ok = ArchiveContains(archivePath, url);
                }
            }

            if (!ok)
            {
                remainingFails++;
                onLine($">>   NO se pudo: {url}");
            }
            else
            {
                onLine($">>   OK");
            }
        }

        onProgress(stillMissing.Count, stillMissing.Count);
        var finalMissing = GetMissingUrls(knownTracks, archivePath, permanent).Count;
        var totalFailed = Math.Max(finalMissing, remainingFails);
        if (totalFailed == 0)
            onLine(">> Todas las canciones se descargaron correctamente.");
        else
            onLine($">> Listo con {totalFailed} cancion(es) que no se pudieron bajar.");

        return totalFailed;
    }

    private static async Task<int> RunWithRateLimitRetryAsync(
        string spotdlPath, string ffmpegPath, IReadOnlyList<string> queries,
        string format, string bitrate, bool embedLyrics, int threads,
        bool skipExisting, bool organizeInFolders, bool sponsorBlock, string destDir,
        string archivePath, IReadOnlyList<string> providers, string cookiesFromBrowser,
        Action<string> onLine, Action<int, int> onProgress, HashSet<string> permanent,
        CancellationToken ct, string? errorsPath = null)
    {
        var attemptThreads = Math.Clamp(threads, 1, MaxThreads);
        for (var attempt = 1; ; attempt++)
        {
            var lastExitCode = 0;
            try
            {
                return await RunOnceAsync(
                    spotdlPath, ffmpegPath, queries, format, bitrate, embedLyrics, attemptThreads,
                    skipExisting, organizeInFolders, sponsorBlock, destDir, archivePath,
                    providers, cookiesFromBrowser, onLine, onProgress, permanent, errorsPath,
                    ct, code => lastExitCode = code);
            }
            catch (InvalidOperationException) when (lastExitCode < 0 && attemptThreads > 1 && attempt <= MaxRateLimitRetries)
            {
                attemptThreads = Math.Max(1, attemptThreads / 2);
                onLine($">> YouTube parece estar bloqueando (rafaga). Reintentando en 8s con {attemptThreads} hilo(s)...");
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
        }
    }

    private static async Task<int> RunOnceAsync(
        string spotdlPath, string ffmpegPath, IReadOnlyList<string> queries,
        string format, string bitrate, bool embedLyrics, int threads,
        bool skipExisting, bool organizeInFolders, bool sponsorBlock, string destDir,
        string archivePath, IReadOnlyList<string> providers, string cookiesFromBrowser,
        Action<string> onLine, Action<int, int> onProgress, HashSet<string> permanent,
        string? errorsPath, CancellationToken ct, Action<int>? onExitCode = null)
    {
        if (queries.Count == 0) return 0;

        var psi = new ProcessStartInfo { FileName = spotdlPath };
        psi.ArgumentList.Add("--simple-tui");
        psi.ArgumentList.Add("--print-errors");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add("--bitrate");
        psi.ArgumentList.Add(bitrate);
        psi.ArgumentList.Add("--threads");
        psi.ArgumentList.Add(Math.Clamp(threads, 1, MaxThreads).ToString());
        psi.ArgumentList.Add("--max-retries");
        psi.ArgumentList.Add(SpotifyApiMaxRetries.ToString());
        psi.ArgumentList.Add("--ffmpeg");
        psi.ArgumentList.Add(ffmpegPath);

        // Audio providers (fallback chain).
        psi.ArgumentList.Add("--audio");
        foreach (var p in providers)
            psi.ArgumentList.Add(p);

        // Cookies via yt-dlp (spotDL downloads audio through yt-dlp).
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            psi.ArgumentList.Add("--yt-dlp-args");
            psi.ArgumentList.Add($"--cookies-from-browser {cookiesFromBrowser.Trim().ToLowerInvariant()}");
        }

        var outputTemplate = organizeInFolders
            ? $"{destDir.Replace('\\', '/')}/{{list-name}}/{{artists}} - {{title}}.{{output-ext}}"
            : $"{destDir.Replace('\\', '/')}/{{artists}} - {{title}}.{{output-ext}}";
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputTemplate);

        if (embedLyrics)
        {
            psi.ArgumentList.Add("--lyrics");
            psi.ArgumentList.Add("genius");
            psi.ArgumentList.Add("musixmatch");
        }

        if (sponsorBlock)
            psi.ArgumentList.Add("--sponsor-block");

        // Always pass archive so re-runs skip completed tracks (skipExisting toggles whether
        // we honor it aggressively — when false we still write archive for our own tracking
        // but use --overwrite force? Actually if skipExisting is false, don't pass archive
        // so spotDL redownloads. For retry logic we need archive though.
        // Compromise: always use archive for reliability of "what's done"; skipExisting=false
        // uses --overwrite force.
        if (skipExisting)
        {
            psi.ArgumentList.Add("--archive");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("--overwrite");
            psi.ArgumentList.Add("skip");
        }
        else
        {
            // Still track successes for our Phase B missing-check, but force re-download.
            psi.ArgumentList.Add("--archive");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("--overwrite");
            psi.ArgumentList.Add("force");
        }

        if (!string.IsNullOrWhiteSpace(errorsPath))
        {
            psi.ArgumentList.Add("--save-errors");
            psi.ArgumentList.Add(errorsPath);
        }

        psi.ArgumentList.Add("download");
        foreach (var q in queries)
            psi.ArgumentList.Add(q);

        var failCount = 0;
        // throwOnPositiveExit: false — spotDL often exits >0 when some songs failed but the
        // batch finished; we track per-song errors ourselves via log / --save-errors / archive.
        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);
            var m = CompleteRegex.Match(line);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out var done)
                && int.TryParse(m.Groups[2].Value, out var total))
            {
                onProgress(done, total);
            }

            if (ErrorLineRegex.IsMatch(line))
            {
                failCount++;
                if (IsPermanentError(line))
                {
                    // Try to associate with a query URL if single-track.
                    if (queries.Count == 1) permanent.Add(queries[0]);
                }
            }
        }, exitCode =>
        {
            onExitCode?.Invoke(exitCode);
            var hint = exitCode < 0
                ? " Suele ser rate-limit de YouTube; se reintenta bajando hilos. Lo ya bajado queda en el archive."
                : "";
            return $"spotdl termino con codigo {exitCode}.{hint}";
        }, ct, throwOnPositiveExit: false);

        // Count failures from save-errors if available (more accurate than log lines).
        if (!string.IsNullOrWhiteSpace(errorsPath) && File.Exists(errorsPath))
        {
            var failed = ReadFailedUrls(errorsPath);
            if (failed.Count > 0) failCount = Math.Max(failCount, failed.Count);
        }

        return failCount;
    }

    private static bool IsPermanentError(string line)
    {
        ReadOnlySpan<string> markers =
        [
            "LookupError",
            "No results found",
            "Could not find a match",
            "not found",
            "Track no longer exists",
            "Invalid URL",
        ];
        foreach (var m in markers)
        {
            if (line.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static List<string> ReadFailedUrls(string errorsPath)
    {
        var list = new List<string>();
        if (!File.Exists(errorsPath)) return list;
        try
        {
            foreach (var line in File.ReadLines(errorsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var m = SaveErrorUrlRegex.Match(line.Trim());
                if (m.Success) list.Add(m.Groups[1].Value);
            }
        }
        catch { /* ignore */ }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> GetMissingUrls(
        IReadOnlyList<string> urls, string archivePath, HashSet<string> permanent)
    {
        var archived = ReadArchive(archivePath);
        return urls
            .Where(u => !permanent.Contains(u) && !archived.Contains(u))
            .ToList();
    }

    private static HashSet<string> ReadArchive(string archivePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(archivePath)) return set;
        try
        {
            foreach (var line in File.ReadLines(archivePath))
            {
                var t = line.Trim();
                if (t.Length > 0) set.Add(t);
            }
        }
        catch { /* locked */ }
        return set;
    }

    private static bool ArchiveContains(string archivePath, string url) =>
        !string.IsNullOrEmpty(url) && ReadArchive(archivePath).Contains(url);

    public static bool IsInArchive(string destDir, string trackUrl) =>
        ArchiveContains(ArchivePathFor(destDir), trackUrl);
}

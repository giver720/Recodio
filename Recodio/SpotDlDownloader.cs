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
        // Common install locations (newest first-ish).
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var rel in new[]
        {
            @"Programs\Python\Python313\Scripts\spotdl.exe",
            @"Programs\Python\Python312\Scripts\spotdl.exe",
            @"Programs\Python\Python311\Scripts\spotdl.exe",
            @"Programs\Python\Python310\Scripts\spotdl.exe",
        })
        {
            var candidate = Path.Combine(local, rel);
            if (File.Exists(candidate)) return candidate;
        }
        if (DependencyChecker.TryFindOnPath("spotdl.exe", out var found) && found != null)
            return found;
        if (DependencyChecker.TryFindOnPath("spotdl", out found) && found != null)
            return found;
        return "spotdl";
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

    // spotDL's built-in default Spotify app is shared by every spotDL install worldwide and
    // gets rate-limited by Spotify for hours (seen: Retry-After 86400s = 24h) under global
    // load - spotDL then silently waits/retries instead of failing fast, which is what makes
    // "revisar metadatos" feel hung. A personal free app (Client Credentials, no user login,
    // no Premium) has its own private quota and sidesteps this entirely.
    private static void AddSpotifyAppCredentials(ProcessStartInfo psi, string? clientId, string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return;
        psi.ArgumentList.Add("--client-id");
        psi.ArgumentList.Add(clientId.Trim());
        psi.ArgumentList.Add("--client-secret");
        psi.ArgumentList.Add(clientSecret.Trim());
    }

    public static async Task<SpotDlAnalyzeResult> AnalyzeAsync(
        string spotdlPath, string query, CancellationToken ct,
        string? spotifyClientId = null, string? spotifyClientSecret = null)
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
            AddSpotifyAppCredentials(psi, spotifyClientId, spotifyClientSecret);
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

    // knownTracksMeta: optional Artist/Name for each URL so we can skip files already on disk
    // even when the .spotdl archive is missing (folder-based skip).
    public static async Task<int> DownloadAsync(
        string spotdlPath, string ffmpegPath, string query,
        IReadOnlyList<string>? trackUrls,
        string format, string bitrate,
        bool embedLyrics, int threads, bool skipExisting, bool organizeInFolders, bool sponsorBlock,
        string destDir,
        IReadOnlyList<string>? audioProviders,
        string cookiesFromBrowser,
        Action<string> onLine, Action<DownloadProgressUpdate> onProgress, CancellationToken ct,
        IReadOnlyList<SpotDlTrack>? knownTracksMeta = null,
        string? spotifyClientId = null, string? spotifyClientSecret = null)
    {
        Directory.CreateDirectory(destDir);
        var archivePath = ArchivePathFor(destDir);
        var providers = audioProviders is { Count: > 0 } ? audioProviders : DefaultAudioProviders;
        var knownTracks = trackUrls is { Count: > 0 }
            ? trackUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : null;

        var preSkipped = 0;
        string? liveTitle = null;
        var permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? archiveCache = null;
        int batchIndex = 0;
        int batchTotal = 0;
        int passIndex = 1;
        int passTotal = 1;

        int TotalKnown() => knownTracks?.Count ?? 0;

        void InvalidateArchive() => archiveCache = null;

        HashSet<string> Arch()
        {
            archiveCache ??= ReadArchive(archivePath);
            return archiveCache;
        }

        int CountArchivedKnown()
        {
            if (knownTracks is null || knownTracks.Count == 0) return 0;
            var a = Arch();
            return knownTracks.Count(u => a.Contains(u));
        }

        /// <summary>Progress against fixed knownTracks.Count (never partial batch as total).</summary>
        void ReportFromArchive(string? status = null, bool isError = false,
            string? itemKey = null, QueueItemState? itemState = null, string? currentTitle = null,
            string phase = "", int? batchDone = null, int? batchTot = null)
        {
            if (currentTitle != null) liveTitle = currentTitle;
            if (batchDone is >= 0) batchIndex = batchDone.Value;
            if (batchTot is > 0) batchTotal = batchTot.Value;

            var tk = TotalKnown();
            int t;
            int archived;
            if (tk > 0)
            {
                t = tk;
                archived = CountArchivedKnown();
            }
            else if (batchTot is > 0 && batchDone is >= 0)
            {
                t = batchTot.Value;
                archived = Math.Clamp(batchDone.Value, 0, t);
            }
            else
            {
                t = 1;
                archived = 0;
            }

            var newlyDone = Math.Max(0, archived - preSkipped);
            var selPos = archived < t && phase is "download" or "retry"
                ? Math.Min(archived + 1, t)
                : archived;

            onProgress(new DownloadProgressUpdate
            {
                Done = newlyDone,
                Total = t,
                Skipped = preSkipped,
                Failed = permanent.Count,
                OverallPercent = (int)Math.Round(100.0 * archived / Math.Max(t, 1)),
                FilePercent = 0,
                CurrentIndex = selPos,
                BatchIndex = batchIndex,
                BatchTotal = batchTotal,
                PassIndex = passIndex,
                PassTotal = passTotal,
                Status = status,
                IsError = isError,
                CurrentTitle = liveTitle,
                ItemKey = itemKey,
                ItemState = itemState,
                Phase = phase,
            });
        }

        void Report(int done, int total, string? status = null, bool isError = false,
            string? itemKey = null, QueueItemState? itemState = null, string? currentTitle = null,
            string phase = "")
        {
            var tk = TotalKnown();
            if (tk > 0 && total > 0 && total != tk)
            {
                InvalidateArchive();
                ReportFromArchive(status, isError, itemKey, itemState, currentTitle, phase,
                    batchDone: done, batchTot: total);
                return;
            }
            InvalidateArchive();
            if (tk > 0)
                ReportFromArchive(status, isError, itemKey, itemState, currentTitle, phase);
            else
                ReportFromArchive(status, isError, itemKey, itemState, currentTitle, phase,
                    batchDone: done, batchTot: Math.Max(total, 1));
        }

        Action<DownloadProgressUpdate> WrapProgress = u =>
        {
            if (!string.IsNullOrWhiteSpace(u.CurrentTitle)) liveTitle = u.CurrentTitle;
            InvalidateArchive();
            if (u.Done > 0 || u.Total > 0)
            {
                ReportFromArchive(u.Status, u.IsError, u.ItemKey, u.ItemState, u.CurrentTitle, u.Phase,
                    batchDone: u.Total > 0 ? u.Done : null, batchTot: u.Total > 0 ? u.Total : null);
            }
            else
            {
                ReportFromArchive(u.Status, u.IsError, u.ItemKey, u.ItemState, u.CurrentTitle, u.Phase);
            }
        };

        onLine($">> Proveedores de audio: {string.Join(" → ", providers)}");

        // Prefer cookies.txt; try one export from browser if needed.
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser) && !CookieManager.BrowserCookiesBroken)
        {
            // spotDL uses yt-dlp under the hood; export with yt-dlp path from PATH if possible.
            var yt = DependencyChecker.TryFindOnPath("yt-dlp.exe", out var ytp) && ytp != null ? ytp
                : DependencyChecker.TryFindOnPath("yt-dlp", out ytp) && ytp != null ? ytp
                : "yt-dlp";
            await CookieManager.TryExportBrowserCookiesAsync(yt, cookiesFromBrowser, onLine, ct);
        }
        var cookieArgs = CookieManager.Resolve(cookiesFromBrowser);
        if (cookieArgs.Mode == CookieMode.File)
            onLine($">> Cookies: archivo {cookieArgs.PathOrKey}");
        else if (cookieArgs.Mode == CookieMode.Browser)
            onLine($">> Cookies: navegador {cookieArgs.PathOrKey} (si falla DPAPI se sigue sin cookies)");
        else if (!string.IsNullOrWhiteSpace(cookiesFromBrowser) && CookieManager.BrowserCookiesBroken)
            onLine(">> Cookies del navegador desactivadas esta sesion (error DPAPI). Continuando sin cookies.");

        // Always skip tracks already present as files under dest (playlist re-run safety).
        // When skipExisting is also true, we keep/update the spotDL archive accordingly.
        if (knownTracks != null && knownTracks.Count > 0)
        {
            // If user deleted media files, drop stale archive URLs so progress starts at 0.
            var pruned = PruneStaleSpotDlArchive(knownTracks, knownTracksMeta, destDir, archivePath, onLine);
            InvalidateArchive();
            if (pruned > 0)
                onLine($">> Se reiniciaron {pruned} cancion(es) del archive (ya no estan en carpeta).");

            var skipped = SeedSpotDlArchiveFromFolder(knownTracks, knownTracksMeta, destDir, archivePath, onLine);
            InvalidateArchive();
            preSkipped = skipped;
            if (skipped > 0)
            {
                onLine($">> Omitidas {skipped} ya presentes en carpeta (no se re-descargan).");
                var a = Arch();
                foreach (var u in knownTracks)
                {
                    if (!a.Contains(u)) continue;
                    var title = knownTracksMeta?.FirstOrDefault(t =>
                        string.Equals(t.Url, u, StringComparison.OrdinalIgnoreCase));
                    var label = title != null ? $"{title.Artist} - {title.Name}" : u;
                    ReportFromArchive(status: "Omitida (ya en carpeta)",
                        itemKey: u, itemState: QueueItemState.Skipped, currentTitle: label);
                }
            }
            ReportFromArchive(status: "Iniciando...");
        }

        var attemptThreads = Math.Clamp(threads, 1, MaxThreads);
        passTotal = MaxBatchPasses;

        // ----- Phase A: batch passes (archive skips finished tracks when skipExisting) -----
        for (var pass = 1; pass <= MaxBatchPasses; pass++)
        {
            passIndex = pass;
            ct.ThrowIfCancellationRequested();

            if (knownTracks != null)
            {
                // Folder scan again (files may have landed mid-run).
                SeedSpotDlArchiveFromFolder(knownTracks, knownTracksMeta, destDir, archivePath, onLine: null);

                // Always skip what's on disk / in archive. "skipExisting=false" only forces
                // overwrite for tracks that are NOT already present as complete files.
                var missing = GetMissingUrls(knownTracks, archivePath, permanent);
                // Extra filter: still on disk by meta even if archive write failed.
                if (knownTracksMeta is { Count: > 0 })
                {
                    var index = ExistingMediaIndex.Build(destDir);
                    var metaByUrl = knownTracksMeta
                        .Where(t => !string.IsNullOrWhiteSpace(t.Url))
                        .GroupBy(t => t.Url, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    missing = missing.Where(u =>
                    {
                        if (!metaByUrl.TryGetValue(u, out var t)) return true;
                        return !index.HasArtistTitle(t.Artist, t.Name) && !index.HasTitle(t.Name);
                    }).ToList();
                }

                if (missing.Count == 0)
                {
                    onLine(">> Todas las canciones seleccionadas ya estan en carpeta/archive.");
                    Report(knownTracks.Count, knownTracks.Count, status: "Todas ya estaban descargadas.");
                    return permanent.Count;
                }
                if (pass > 1)
                {
                    var wait = Math.Min(5 + pass * 3, 20);
                    onLine($">> Reintento batch {pass}/{MaxBatchPasses}: faltan {missing.Count}. Esperando {wait}s...");
                    Report(knownTracks.Count - missing.Count, knownTracks.Count,
                        status: $"Reintento batch {pass}/{MaxBatchPasses}: faltan {missing.Count}", phase: "retry");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                else
                {
                    onLine($">> Descargando {missing.Count} cancion(es) (omitidas las ya en carpeta)...");
                    Report(knownTracks.Count - missing.Count, knownTracks.Count,
                        status: $"Descargando {missing.Count} cancion(es)...", phase: "download");
                }

                // On later passes: fewer threads + full provider chain already set.
                if (pass >= 2) attemptThreads = Math.Max(1, Math.Min(attemptThreads, 2));
                if (pass >= 3) attemptThreads = 1;

                try
                {
                    // Always pass archive when we have a track list so completed songs stay skipped.
                    await RunWithRateLimitRetryAsync(
                        spotdlPath, ffmpegPath, queries: missing, format, bitrate, embedLyrics,
                        attemptThreads, skipExisting: true, organizeInFolders, sponsorBlock, destDir,
                        archivePath, providers, cookieArgs,
                        onLine, WrapProgress, permanent, ct,
                        spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
                    InvalidateArchive();
                }
                catch (InvalidOperationException ex) when (CookieManager.IsCookieFailureText(ex.Message) && cookieArgs.Enabled)
                {
                    CookieManager.MarkBrowserBroken(ex.Message);
                    cookieArgs = CookieArgs.None;
                    onLine(">> Error de cookies (DPAPI). Reintentando sin cookies...");
                    try
                    {
                        await RunWithRateLimitRetryAsync(
                            spotdlPath, ffmpegPath, queries: missing, format, bitrate, embedLyrics,
                            attemptThreads, skipExisting: true, organizeInFolders, sponsorBlock, destDir,
                            archivePath, providers, cookieArgs,
                            onLine, WrapProgress, permanent, ct,
                            spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
                        InvalidateArchive();
                    }
                    catch (InvalidOperationException ex2)
                    {
                        onLine($">> Pasada {pass}: {ex2.Message}");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    onLine($">> Pasada {pass}: {ex.Message}");
                }

                SeedSpotDlArchiveFromFolder(knownTracks, knownTracksMeta, destDir, archivePath, onLine: null);
                var still = GetMissingUrls(knownTracks, archivePath, permanent).Count;
                onLine($">> Pasada {pass}: {knownTracks.Count - still}/{knownTracks.Count} listas.");
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
                    int failCount;
                    try
                    {
                        failCount = await RunWithRateLimitRetryAsync(
                            spotdlPath, ffmpegPath, queries: [query], format, bitrate, embedLyrics,
                            attemptThreads, skipExisting, organizeInFolders, sponsorBlock, destDir,
                            archivePath, providers, cookieArgs,
                            onLine, WrapProgress, permanent, ct, errorsPath,
                            spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
                    }
                    catch (InvalidOperationException ex) when (CookieManager.IsCookieFailureText(ex.Message) && cookieArgs.Enabled)
                    {
                        CookieManager.MarkBrowserBroken(ex.Message);
                        cookieArgs = CookieArgs.None;
                        onLine(">> Error de cookies (DPAPI). Reintentando query sin cookies...");
                        failCount = await RunWithRateLimitRetryAsync(
                            spotdlPath, ffmpegPath, queries: [query], format, bitrate, embedLyrics,
                            attemptThreads, skipExisting, organizeInFolders, sponsorBlock, destDir,
                            archivePath, providers, cookieArgs,
                            onLine, WrapProgress, permanent, ct, errorsPath,
                            spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
                    }

                    var failedUrls = ReadFailedUrls(errorsPath);

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
                        if (pass >= MaxBatchPasses) break;
                        continue;
                    }

                    // Failures reported only via log lines (no per-URL list) — keep retrying the
                    // whole query; don't claim success with a non-zero failCount.
                    if (failCount == 0) return 0;
                    onLine($">> {failCount} error(es) sin URL individual; reintentando query...");
                }
                finally
                {
                    try { if (File.Exists(errorsPath)) File.Delete(errorsPath); } catch { /* ignore */ }
                }
            }
        }

        // ----- Phase B: one-by-one for remaining track URLs -----
        if (knownTracks == null || knownTracks.Count == 0)
        {
            // Playlist query with no per-track URLs: we can't surgically retry. Report last
            // soft-error estimate as at least 1 if we never got a clean pass.
            onLine(">> No hay URLs individuales para reintentar; revisa el log.");
            return 1;
        }

        SeedSpotDlArchiveFromFolder(knownTracks, knownTracksMeta, destDir, archivePath, onLine: null);
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
            var meta = knownTracksMeta?.FirstOrDefault(t =>
                string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase));
            var label = meta != null ? $"{meta.Artist} - {meta.Name}" : url;
            Report(i, stillMissing.Count, status: $"Uno por uno {i + 1}/{stillMissing.Count}",
                itemKey: url, itemState: QueueItemState.Downloading, currentTitle: label, phase: "retry");

            if (permanent.Contains(url))
            {
                remainingFails++;
                Report(i, stillMissing.Count, itemKey: url, itemState: QueueItemState.Failed, currentTitle: label);
                continue;
            }

            var ok = false;
            // Captures the real reason so the final Failed report can show something useful
            // in the queue tooltip instead of just restating the track label.
            string? lastErrorText = null;
            for (var attempt = 1; attempt <= MaxPerTrackAttempts && !ok; attempt++)
            {
                if (attempt > 1)
                {
                    var wait = attempt * 4;
                    onLine($">>   reintento {attempt}/{MaxPerTrackAttempts} en {wait}s...");
                    Report(i, stillMissing.Count, status: $"Reintento {attempt}/{MaxPerTrackAttempts}",
                        currentTitle: label, phase: "retry");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                else
                {
                    onLine($">> [{i + 1}/{stillMissing.Count}] {url}");
                }

                try
                {
                    await RunOnceAsync(
                        spotdlPath, ffmpegPath, [url], format, bitrate, embedLyrics,
                        threads: 1, skipExisting: true, organizeInFolders, sponsorBlock, destDir,
                        archivePath, rescueProviders, cookieArgs,
                        onLine, WrapProgress, permanent, errorsPath: null, ct,
                        spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
                    InvalidateArchive();

                    ok = ArchiveContains(archivePath, url);
                }
                catch (InvalidOperationException ex) when (CookieManager.IsCookieFailureText(ex.Message) && cookieArgs.Enabled)
                {
                    CookieManager.MarkBrowserBroken(ex.Message);
                    cookieArgs = CookieArgs.None;
                    onLine(">>   cookies fallaron; reintento sin cookies...");
                    try
                    {
                        await RunOnceAsync(
                            spotdlPath, ffmpegPath, [url], format, bitrate, embedLyrics,
                            threads: 1, skipExisting: true, organizeInFolders, sponsorBlock, destDir,
                            archivePath, rescueProviders, cookieArgs,
                            onLine, WrapProgress, permanent, errorsPath: null, ct,
                            spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
                        InvalidateArchive();
                        ok = ArchiveContains(archivePath, url);
                    }
                    catch (InvalidOperationException ex2)
                    {
                        lastErrorText = ex2.Message;
                        onLine($">>   fallo: {ex2.Message}");
                        ok = ArchiveContains(archivePath, url);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    lastErrorText = ex.Message;
                    onLine($">>   fallo: {ex.Message}");
                    ok = ArchiveContains(archivePath, url);
                }
            }

            if (!ok)
            {
                remainingFails++;
                onLine($">>   NO se pudo: {url}");
                var errStatus = !string.IsNullOrWhiteSpace(lastErrorText)
                    ? (lastErrorText!.Length > 120 ? lastErrorText[..117] + "..." : lastErrorText)
                    : $"Error: {label}";
                Report(i + 1, stillMissing.Count, status: errStatus,
                    isError: true, itemKey: url, itemState: QueueItemState.Failed, currentTitle: label);
            }
            else
            {
                onLine($">>   OK");
                Report(i + 1, stillMissing.Count, status: $"OK: {label}",
                    itemKey: url, itemState: QueueItemState.Done, currentTitle: label);
            }
        }

        Report(stillMissing.Count, stillMissing.Count, status: "Finalizando...");
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
        string archivePath, IReadOnlyList<string> providers, CookieArgs cookies,
        Action<string> onLine, Action<DownloadProgressUpdate> onProgress, HashSet<string> permanent,
        CancellationToken ct, string? errorsPath = null,
        string? spotifyClientId = null, string? spotifyClientSecret = null)
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
                    providers, cookies, onLine, onProgress, permanent, errorsPath,
                    ct, code => lastExitCode = code,
                    spotifyClientId: spotifyClientId, spotifyClientSecret: spotifyClientSecret);
            }
            catch (InvalidOperationException) when (lastExitCode < 0 && attemptThreads > 1 && attempt <= MaxRateLimitRetries)
            {
                attemptThreads = Math.Max(1, attemptThreads / 2);
                onLine($">> YouTube parece estar bloqueando (rafaga). Reintentando en 8s con {attemptThreads} hilo(s)...");
                onProgress(new DownloadProgressUpdate
                {
                    Status = $"Rate-limit; reintento con {attemptThreads} hilo(s)...",
                    Phase = "retry",
                    IsError = true,
                });
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
        }
    }

    private static async Task<int> RunOnceAsync(
        string spotdlPath, string ffmpegPath, IReadOnlyList<string> queries,
        string format, string bitrate, bool embedLyrics, int threads,
        bool skipExisting, bool organizeInFolders, bool sponsorBlock, string destDir,
        string archivePath, IReadOnlyList<string> providers, CookieArgs cookies,
        Action<string> onLine, Action<DownloadProgressUpdate> onProgress, HashSet<string> permanent,
        string? errorsPath, CancellationToken ct, Action<int>? onExitCode = null,
        string? spotifyClientId = null, string? spotifyClientSecret = null)
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
        AddSpotifyAppCredentials(psi, spotifyClientId, spotifyClientSecret);
        psi.ArgumentList.Add("--ffmpeg");
        psi.ArgumentList.Add(ffmpegPath);

        // Audio providers (fallback chain).
        psi.ArgumentList.Add("--audio");
        foreach (var p in providers)
            psi.ArgumentList.Add(p);

        CookieManager.ApplyToSpotDl(psi, cookies);

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

        // skipExisting=true: honor archive so re-runs only fetch missing tracks.
        // skipExisting=false: do NOT pass --archive (spotDL would still skip archived URLs even
        // with --overwrite force) so the user can actually re-download.
        if (skipExisting)
        {
            psi.ArgumentList.Add("--archive");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("--overwrite");
            psi.ArgumentList.Add("skip");
        }
        else
        {
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
        var cookieFailSeen = false;
        // throwOnPositiveExit: false — spotDL often exits >0 when some songs failed but the
        // batch finished; we track per-song errors ourselves via log / --save-errors / archive.
        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);
            if (CookieManager.IsCookieFailureText(line))
                cookieFailSeen = true;
            var m = CompleteRegex.Match(line);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out var done)
                && int.TryParse(m.Groups[2].Value, out var total))
            {
                // Batch counters only — outer WrapProgress remaps to full selection total.
                onProgress(new DownloadProgressUpdate
                {
                    Done = done,
                    Total = total,
                    BatchIndex = done,
                    BatchTotal = total,
                    Status = $"Lote… {done} de {total}",
                    Phase = "download",
                });
            }

            // Surface current track when spotDL mentions it.
            if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Downloaded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Found YouTube", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Skipping", StringComparison.OrdinalIgnoreCase))
            {
                var title = line.Length > 100 ? line[..97] + "..." : line;
                onProgress(new DownloadProgressUpdate
                {
                    CurrentTitle = title.Trim(),
                    Status = line.Contains("Skipping", StringComparison.OrdinalIgnoreCase)
                        ? "Omitiendo..."
                        : line.Contains("Downloaded", StringComparison.OrdinalIgnoreCase)
                            ? "Cancion lista"
                            : "Descargando cancion...",
                    Phase = "download",
                    ItemState = line.Contains("Skipping", StringComparison.OrdinalIgnoreCase)
                        ? QueueItemState.Skipped
                        : QueueItemState.Downloading,
                });
            }

            if (ErrorLineRegex.IsMatch(line))
            {
                failCount++;
                if (IsPermanentError(line) && queries.Count == 1 && IsSpotifyTrackUrl(queries[0]))
                {
                    // Only mark single *track* URLs permanent — never a playlist/album query.
                    permanent.Add(queries[0]);
                    onProgress(new DownloadProgressUpdate
                    {
                        ItemKey = queries[0],
                        ItemState = QueueItemState.Failed,
                        Status = line.Length > 120 ? line[..117] + "..." : line,
                        IsError = true,
                        Phase = "error",
                    });
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

        if (cookieFailSeen && cookies.Enabled)
        {
            CookieManager.MarkBrowserBroken("DPAPI/cookie error during spotDL");
            throw new InvalidOperationException(CookieManager.FriendlyCookieError("Failed to decrypt with DPAPI"));
        }

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
        // Avoid broad matches (SongError / "not found") that also hit transient failures.
        ReadOnlySpan<string> markers =
        [
            "No results found",
            "Could not find a match",
            "Track no longer exists",
            "Invalid URL",
        ];
        foreach (var m in markers)
        {
            if (line.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsSpotifyTrackUrl(string q) =>
        q.Contains("open.spotify.com/track/", StringComparison.OrdinalIgnoreCase)
        || q.Contains("spotify.com/track/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes archive URLs for tracks whose files are gone (re-download from zero).
    /// </summary>
    private static int PruneStaleSpotDlArchive(
        IReadOnlyList<string> trackUrls,
        IReadOnlyList<SpotDlTrack>? meta,
        string destDir,
        string archivePath,
        Action<string>? onLine)
    {
        if (!File.Exists(archivePath) || trackUrls.Count == 0) return 0;

        var index = ExistingMediaIndex.Build(destDir);
        var metaByUrl = meta?
            .Where(t => !string.IsNullOrWhiteSpace(t.Url))
            .GroupBy(t => t.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in trackUrls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var onDisk = false;
            if (metaByUrl != null && metaByUrl.TryGetValue(url, out var t))
                onDisk = index.HasArtistTitle(t.Artist, t.Name) || index.HasTitle(t.Name);
            if (!onDisk)
            {
                var id = ExtractSpotifyTrackId(url);
                if (!string.IsNullOrEmpty(id) && index.HasId(id))
                    onDisk = true;
            }
            if (!onDisk) stale.Add(url);
        }
        if (stale.Count == 0) return 0;

        string[] lines;
        try { lines = File.ReadAllLines(archivePath); }
        catch { return 0; }

        var kept = new List<string>(lines.Length);
        var removed = 0;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length > 0 && stale.Contains(t))
            {
                removed++;
                continue;
            }
            kept.Add(line);
        }
        if (removed == 0) return 0;
        try
        {
            File.WriteAllLines(archivePath, kept);
            onLine?.Invoke($">> Archive spotDL: se quitaron {removed} URL(s) sin archivo.");
        }
        catch { return 0; }
        return removed;
    }

    /// <summary>
    /// Adds Spotify track URLs to the spotDL archive when a matching media file already
    /// exists under destDir (by "Artist - Title" / title). Returns newly skipped count.
    /// </summary>
    private static int SeedSpotDlArchiveFromFolder(
        IReadOnlyList<string> trackUrls,
        IReadOnlyList<SpotDlTrack>? meta,
        string destDir,
        string archivePath,
        Action<string>? onLine)
    {
        var index = ExistingMediaIndex.Build(destDir);
        if (index.FileCount == 0) return 0;

        var archived = ReadArchive(archivePath);
        var metaByUrl = meta?
            .Where(t => !string.IsNullOrWhiteSpace(t.Url))
            .GroupBy(t => t.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var toAppend = new List<string>();
        var newly = 0;

        foreach (var url in trackUrls)
        {
            if (string.IsNullOrWhiteSpace(url) || archived.Contains(url)) continue;

            var onDisk = false;
            if (metaByUrl != null && metaByUrl.TryGetValue(url, out var t))
                onDisk = index.HasArtistTitle(t.Artist, t.Name) || index.HasTitle(t.Name);

            // Fallback: Spotify track id sometimes ends up in filenames.
            if (!onDisk)
            {
                var id = ExtractSpotifyTrackId(url);
                if (!string.IsNullOrEmpty(id) && index.HasId(id))
                    onDisk = true;
            }

            if (!onDisk) continue;

            toAppend.Add(url);
            archived.Add(url);
            newly++;
            if (metaByUrl != null && metaByUrl.TryGetValue(url, out var mt))
                onLine?.Invoke($">>   ya en carpeta, omitido: {mt.Artist} - {mt.Name}");
            else
                onLine?.Invoke($">>   ya en carpeta, omitido: {url}");
        }

        if (toAppend.Count > 0)
        {
            try { File.AppendAllLines(archivePath, toAppend); } catch { /* locked */ }
        }
        return newly;
    }

    private static string? ExtractSpotifyTrackId(string url)
    {
        // https://open.spotify.com/track/ABC123?...
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var i = Array.FindIndex(segs, s => s.Equals("track", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < segs.Length) return segs[i + 1];
        }
        catch { /* ignore */ }
        return null;
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

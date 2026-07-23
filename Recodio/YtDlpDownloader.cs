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
    // [download]  45.2% of 12.34MiB at 2.10MiB/s ETA 00:03  (fragments of this may be missing)
    private static readonly Regex ProgressDetailRegex = new(
        @"\[download\]\s+(\d+(?:\.\d+)?)%\s+of\s+~?(\S+)(?:\s+at\s+(\S+))?(?:\s+ETA\s+(\S+))?",
        RegexOptions.Compiled);
    private static readonly Regex ItemRegex = new(@"Downloading item (\d+) of (\d+)", RegexOptions.Compiled);
    private static readonly Regex DestinationRegex = new(@"\[download\]\s+Destination:\s+(.+)", RegexOptions.Compiled);
    private static readonly Regex ErrorLineRegex = new(@"^ERROR:", RegexOptions.Compiled);
    // Archive lines: "youtube VIDEOID" / "twitter 12345" / "Generic hash"
    private static readonly Regex ArchiveIdRegex = new(@"^\S+\s+(\S+)", RegexOptions.Compiled);
    // [extractor] id: message  — works for youtube, twitter, tiktok, vimeo, …
    private static readonly Regex ExtractorIdRegex = new(
        @"\[([A-Za-z0-9_-]+)\]\s+([A-Za-z0-9_.%-]{2,})\s*:", RegexOptions.Compiled);

    /// <summary>
    /// Archive lives next to the media: inside the playlist subfolder when organized,
    /// otherwise in destDir. Avoids mixing ids from different playlists in one file.
    /// </summary>
    public static string ArchivePathFor(string destDir, string? playlistFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(playlistFolder))
        {
            var folder = Path.Combine(destDir, playlistFolder);
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, ArchiveFileName);
        }
        return Path.Combine(destDir, ArchiveFileName);
    }

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
        // Prefer cookies.txt; if only browser is configured, try a one-shot export first.
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser) && !CookieManager.BrowserCookiesBroken
            && !File.Exists(CookieManager.CookiesFilePath))
        {
            await CookieManager.TryExportBrowserCookiesAsync(ytDlpPath, cookiesFromBrowser, null, ct);
        }

        var cookies = CookieManager.Resolve(cookiesFromBrowser);
        try
        {
            return await AnalyzeOnceAsync(ytDlpPath, url, cookies, ct);
        }
        catch (InvalidOperationException ex) when (CookieManager.IsCookieFailureText(ex.Message) && cookies.Enabled)
        {
            CookieManager.NoteCookieFailure(ex.Message);
            // Retry without cookies so public playlists still work.
            return await AnalyzeOnceAsync(ytDlpPath, url, CookieArgs.None, ct);
        }
    }

    private static async Task<AnalyzeResult> AnalyzeOnceAsync(
        string ytDlpPath, string url, CookieArgs cookies, CancellationToken ct)
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
        CookieManager.ApplyToArgs(args, cookies);
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
        {
            if (CookieManager.IsCookieFailureText(stderr))
                throw new InvalidOperationException(CookieManager.FriendlyCookieError(stderr));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "No se pudo analizar la URL. ¿Es un sitio que yt-dlp soporta?"
                : stderr);
        }

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
            return $"https://x.com/i/status/{id}";
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

    // Builds -f selector. CRITICAL: never fall back to bare bestvideo (video-only) — that was
    // leaving silent files when the paired video+audio merge could not be selected.
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
            // bestaudio first; progressive "best" as last resort (ffmpeg extracts audio).
            return "bestaudio/bestaudio*/best";
        }

        // For muxed video: do NOT filter audio by abr in the paired selector. If
        // bestaudio[abr<=192] fails to match, the whole "bv+ba" unit fails and older
        // selectors fell through to bestvideo (silent file).
        // Prefer m4a when remuxing to mp4 (H264+AAC plays everywhere).
        var ba = format == "mp4"
            ? "bestaudio[ext=m4a]/bestaudio"
            : "bestaudio";

        if (format is "mp4" or "mkv")
            extraArgs.AddRange(["--merge-output-format", format == "mkv" ? "mkv" : "mp4"]);

        // Pair video+audio, then progressive best (has audio). Never bare bestvideo.
        // bv* = best video excluding weird storyboard formats when * is used by yt-dlp.
        if (heightFilter != null)
        {
            return $"bestvideo*[height<={heightFilter}]+{ba}/"
                 + $"best[height<={heightFilter}]/"
                 + $"bestvideo*+{ba}/"
                 + "best";
        }

        return $"bestvideo*+{ba}/best";
    }

    // cookiesFromBrowser: "" or chrome|edge|firefox|brave|…
    // playlistTitle: from Analyze (preferred fixed subfolder name when organizeInFolders).
    // Returns number of items that still failed after all retries (includes permanent).
    public static async Task<int> DownloadAsync(
        string ytDlpPath, string ffmpegPath, string url, List<PlaylistEntry>? selectedEntries,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, string destDir,
        Action<string> onLine, Action<DownloadProgressUpdate> onProgress, CancellationToken ct,
        string cookiesFromBrowser = "",
        string? playlistTitle = null)
    {
        Directory.CreateDirectory(destDir);
        var entries = selectedEntries is { Count: > 0 }
            ? selectedEntries.Select(NormalizeEntry).ToList()
            : [new PlaylistEntry(1, url, "url:" + url, url, "")];

        var totalItems = entries.Count;
        var preSkipped = 0;
        var preSkippedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? liveTitle = null;
        int batchIndex = 0;
        int batchTotal = 0;
        var permanentFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only mark permanent when the error id belongs to this selection.
        var selectionIds = new HashSet<string>(
            entries.SelectMany(EntryArchiveKeys), StringComparer.OrdinalIgnoreCase);
        HashSet<string>? archiveIdCache = null;
        // archivePath assigned after fixed playlist folder is known
        string archivePath = ArchivePathFor(destDir);

        void InvalidateArchiveCache() => archiveIdCache = null;

        HashSet<string> ArchiveIds()
        {
            archiveIdCache ??= ReadArchiveIds(archivePath);
            return archiveIdCache;
        }

        bool EntryArchived(PlaylistEntry e) => IsArchivedInSet(e, ArchiveIds());

        string? MapItemKey(string? itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return itemKey;
            var match = entries.FirstOrDefault(e =>
                string.Equals(e.Id, itemKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ItemKey(e), itemKey, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(e.Id) && itemKey.Contains(e.Id, StringComparison.OrdinalIgnoreCase)));
            return match != null ? ItemKey(match) : itemKey;
        }

        void Report(
            int? filePct = null,
            string? status = null,
            bool isError = false,
            double? speed = null,
            TimeSpan? eta = null,
            string? sizeInfo = null,
            string phase = "",
            string? itemKey = null,
            QueueItemState? itemState = null,
            string? currentTitle = null)
        {
            if (currentTitle != null) liveTitle = currentTitle;
            if (!string.IsNullOrEmpty(itemKey) && itemState is not null)
                itemKey = MapItemKey(itemKey);

            var archived = entries.Count(EntryArchived);
            var failed = permanentFailures.Count;
            var finished = Math.Max(0, archived);
            var newlyDone = Math.Max(0, finished - preSkipped);
            var fp = filePct ?? 0;
            var overall = totalItems <= 0
                ? fp
                : (int)Math.Round((finished + (fp > 0 && finished < totalItems ? fp / 100.0 : 0))
                    * 100.0 / totalItems);
            // Selection position: next unfinished slot while a file is in progress
            var selPos = finished < totalItems && (fp > 0 || phase is "download" or "merge" or "extract")
                ? Math.Min(finished + 1, totalItems)
                : finished;

            onProgress(new DownloadProgressUpdate
            {
                OverallPercent = Math.Clamp(overall, 0, 100),
                FilePercent = filePct is { } f ? Math.Clamp(f, 0, 100) : null,
                Done = newlyDone,
                Total = totalItems,
                Skipped = preSkipped,
                Failed = failed,
                CurrentIndex = selPos,
                BatchIndex = batchIndex,
                BatchTotal = batchTotal,
                CurrentTitle = liveTitle,
                Status = status,
                IsError = isError,
                SpeedBytesPerSec = speed,
                Eta = eta,
                SizeInfo = sizeInfo,
                Phase = phase,
                ItemKey = itemKey,
                ItemState = itemState,
            });
        }

        // Safer rule: always pass --playlist-items when we have analyzed entries with real
        // indices and the page URL is not itself a single-item direct URL for that entry.
        var usePlaylistItems = ShouldUsePlaylistItems(url, entries);
        var isMultiItem = entries.Count > 1 || usePlaylistItems;

        // Fixed subfolder: prefer title from analyze; else derive a safe name for multi downloads.
        string? fixedPlaylistFolder = null;
        if (organizeInFolders && isMultiItem)
        {
            fixedPlaylistFolder = SanitizeFolderName(
                !string.IsNullOrWhiteSpace(playlistTitle) ? playlistTitle!
                : GuessPlaylistFolderName(url, entries));
            if (!string.IsNullOrWhiteSpace(fixedPlaylistFolder))
            {
                var folderPath = Path.Combine(destDir, fixedPlaylistFolder);
                Directory.CreateDirectory(folderPath);
                onLine($">> Carpeta de playlist: {folderPath}");
            }
        }

        // Archive next to media (playlist folder when organized) so lists don't share one archive.
        archivePath = ArchivePathFor(destDir, fixedPlaylistFolder);
        TryMigrateRootArchive(destDir, archivePath, entries, onLine);
        InvalidateArchiveCache();

        var useSponsorBlock = sponsorBlock
            && (LooksLikeYoutubeUrl(url) || entries.Any(e => IsYoutubeExtractor(e.Extractor)));

        if (sponsorBlock && !useSponsorBlock)
            onLine(">> SponsorBlock omitido (solo aplica a YouTube).");

        // Cookies: try export to cookies.txt once, then resolve file/browser/none.
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser) && !CookieManager.BrowserCookiesBroken)
        {
            await CookieManager.TryExportBrowserCookiesAsync(ytDlpPath, cookiesFromBrowser, onLine, ct);
        }
        var cookieArgs = CookieManager.Resolve(cookiesFromBrowser);
        if (cookieArgs.Mode == CookieMode.File)
            onLine($">> Cookies: archivo {cookieArgs.PathOrKey}");
        else if (cookieArgs.Mode == CookieMode.Browser)
            onLine($">> Cookies: navegador {cookieArgs.PathOrKey} (si falla DPAPI se reintenta sin cookies)");
        else if (!string.IsNullOrWhiteSpace(cookiesFromBrowser) && CookieManager.BrowserCookiesBroken)
            onLine(">> Cookies del navegador desactivadas esta sesion (error DPAPI previo). Continuando sin cookies.");

        // Disk is source of truth: if the user deleted the playlist folder (or files),
        // drop those ids from the archive so progress starts at 0 and yt-dlp re-downloads.
        var pruned = PruneStaleArchiveEntries(entries, destDir, fixedPlaylistFolder, archivePath, onLine);
        InvalidateArchiveCache();
        if (pruned > 0)
            onLine($">> Se reiniciaron {pruned} item(s) del archive (ya no estan en carpeta).");

        // Folder scan: skip anything already on disk (even if archive was wiped).
        // Always on — never re-download a video that already exists in the playlist folder.
        var skippedOnDisk = SeedArchiveFromFolder(entries, destDir, fixedPlaylistFolder, archivePath, onLine);
        InvalidateArchiveCache();
        preSkipped = skippedOnDisk;
        if (skippedOnDisk > 0)
        {
            onLine($">> Omitidas {skippedOnDisk} ya presentes en carpeta (no se re-descargan).");
            // Mark only items that still have files on disk; remember keys so they stay ⏭.
            foreach (var e in entries)
            {
                if (EntryArchived(e) && EntryHasFileOnDisk(e, destDir, fixedPlaylistFolder))
                {
                    var key = ItemKey(e);
                    preSkippedKeys.Add(key);
                    Report(status: "Omitido (ya en carpeta)", itemKey: key, itemState: QueueItemState.Skipped,
                        currentTitle: e.Title);
                }
            }
        }
        Report(status: "Iniciando...");

        // ----- Phase A: batch passes -----
        for (var pass = 1; pass <= MaxBatchPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            // Re-scan folder each pass (in case files appeared between passes).
            SeedArchiveFromFolder(entries, destDir, fixedPlaylistFolder, archivePath, onLine: null);
            InvalidateArchiveCache();

            var missingBefore = CountMissing(entries, archivePath, permanentFailures);
            if (missingBefore == 0)
            {
                onLine(">> Todos los items ya estan descargados (archive + carpeta).");
                Report(filePct: 0, status: "Todos ya estaban descargados.");
                TryWriteM3u(organizeInFolders, isMultiItem, destDir, fixedPlaylistFolder, onLine);
                return permanentFailures.Count;
            }

            if (pass > 1)
            {
                var wait = Math.Min(8 + pass * 4, 30);
                onLine($">> Reintento de lista {pass}/{MaxBatchPasses}: faltan {missingBefore}. Esperando {wait}s...");
                Report(status: $"Reintento lista {pass}/{MaxBatchPasses}: faltan {missingBefore}", phase: "retry");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
            else
            {
                onLine($">> Descargando {entries.Count} item(s) (pasada 1/{MaxBatchPasses})...");
                Report(status: $"Descargando {missingBefore} item(s)...", phase: "download");
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

            Action<DownloadProgressUpdate> batchProgress = u =>
            {
                if (!string.IsNullOrWhiteSpace(u.CurrentTitle)) liveTitle = u.CurrentTitle;
                // yt-dlp "item N of M" is the pending batch, not the full selection
                if (u.BatchIndex > 0 && u.BatchTotal > 0)
                {
                    batchIndex = u.BatchIndex;
                    batchTotal = u.BatchTotal;
                }
                else if (u.CurrentIndex > 0 && u.Total > 0 && u.Total != totalItems)
                {
                    batchIndex = u.CurrentIndex;
                    batchTotal = u.Total;
                }
                InvalidateArchiveCache();
                Report(
                    filePct: u.FilePercent,
                    status: u.Status,
                    isError: u.IsError,
                    speed: u.SpeedBytesPerSec,
                    eta: u.Eta,
                    sizeInfo: u.SizeInfo,
                    phase: u.Phase,
                    itemKey: u.ItemKey,
                    itemState: u.ItemState,
                    currentTitle: u.CurrentTitle);
            };

            try
            {
                await RunWithAbortRetryAsync(
                    ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                    organizeInFolders, useSponsorBlock, isPlaylistPace, destDir, archivePath,
                    relaxed, embedExtras, cookieArgs, fixedPlaylistFolder,
                    onLine, batchProgress, permanentFailures, ct, selectionIds);
                // If cookie args got disabled mid-run, keep local copy in sync for next pass.
                cookieArgs = CookieManager.Resolve(cookiesFromBrowser);
            }
            catch (InvalidOperationException ex)
            {
                if (CookieManager.IsCookieFailureText(ex.Message) && cookieArgs.Enabled)
                {
                    CookieManager.NoteCookieFailure(ex.Message);
                    cookieArgs = CookieArgs.None;
                    onLine(">> Error de cookies (DPAPI). Reintentando esta pasada sin cookies...");
                    try
                    {
                        await RunWithAbortRetryAsync(
                            ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                            organizeInFolders, useSponsorBlock, isPlaylistPace, destDir, archivePath,
                            relaxed, embedExtras, cookieArgs, fixedPlaylistFolder,
                            onLine, batchProgress, permanentFailures, ct, selectionIds);
                    }
                    catch (InvalidOperationException ex2)
                    {
                        onLine($">> Pasada {pass} termino con error: {ex2.Message}");
                    }
                }
                else
                {
                    onLine($">> Pasada {pass} termino con error: {ex.Message}");
                }
            }

            // After a batch, mark successful downloads into archive from disk + yt-dlp archive.
            SeedArchiveFromFolder(entries, destDir, fixedPlaylistFolder, archivePath, onLine: null);
            SyncSyntheticArchive(entries, ResolveScanRoot(destDir, fixedPlaylistFolder), archivePath);
            InvalidateArchiveCache();
            batchIndex = 0;
            batchTotal = 0;

            // Refresh item states from archive after each pass (keep pre-skipped as Skipped).
            foreach (var e in entries)
            {
                var key = ItemKey(e);
                if (IsPermanent(e, permanentFailures))
                    Report(itemKey: key, itemState: QueueItemState.Failed, currentTitle: e.Title);
                else if (EntryArchived(e))
                {
                    var state = preSkippedKeys.Contains(key) ? QueueItemState.Skipped : QueueItemState.Done;
                    Report(itemKey: key, itemState: state, currentTitle: e.Title);
                }
            }

            var missingAfter = CountMissing(entries, archivePath, permanentFailures);
            onLine($">> Pasada {pass} lista: {entries.Count - missingAfter - permanentFailures.Count}/{entries.Count} ok"
                + (permanentFailures.Count > 0 ? $", {permanentFailures.Count} no disponibles" : "")
                + (missingAfter > 0 ? $", {missingAfter} pendientes" : "")
                + ".");
            Report(status: $"Pasada {pass} lista · faltan {missingAfter}");

            if (missingAfter == 0) break;
            if (pass >= 2 && missingAfter >= missingBefore) break;
        }

        // ----- Phase B: one-by-one for whatever is still missing -----
        var stillMissing = GetMissing(entries, archivePath, permanentFailures);
        if (stillMissing.Count > 0)
        {
            onLine($">> Reintentando {stillMissing.Count} item(s) uno por uno...");

            for (var i = 0; i < stillMissing.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = stillMissing[i];
                liveTitle = entry.Title;
                batchIndex = i + 1;
                batchTotal = stillMissing.Count;
                InvalidateArchiveCache();
                Report(status: $"Uno por uno {i + 1}/{stillMissing.Count}", phase: "retry",
                    itemKey: ItemKey(entry), itemState: QueueItemState.Downloading, currentTitle: entry.Title);

                if (IsPermanent(entry, permanentFailures))
                {
                    Report(itemKey: ItemKey(entry), itemState: QueueItemState.Failed, currentTitle: entry.Title);
                    continue;
                }

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
                        Report(status: $"Reintento {attempt}/{MaxPerVideoAttempts}: {entry.Title}", phase: "retry");
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

                    // Always keep the fixed playlist folder on retries (even for direct watch URLs).
                    var attemptOrganize = organizeInFolders;
                    var itemSponsor = useSponsorBlock
                        && (LooksLikeYoutubeUrl(attemptUrl) || IsYoutubeExtractor(entry.Extractor));

                    Action<DownloadProgressUpdate> itemProgress = u =>
                    {
                        InvalidateArchiveCache();
                        Report(
                            filePct: u.FilePercent,
                            status: u.Status ?? $"Descargando: {entry.Title}",
                            speed: u.SpeedBytesPerSec,
                            eta: u.Eta,
                            sizeInfo: u.SizeInfo,
                            phase: u.Phase,
                            itemKey: ItemKey(entry),
                            itemState: QueueItemState.Downloading,
                            currentTitle: entry.Title);
                    };

                    try
                    {
                        await RunWithAbortRetryAsync(
                            ytDlpPath, ffmpegPath, attemptUrl, attemptItems,
                            format, videoQuality, audioQuality,
                            attemptOrganize, itemSponsor, isPlaylist: false, destDir, archivePath,
                            relaxed, embedExtras, cookieArgs, fixedPlaylistFolder,
                            onLine, itemProgress, permanentFailures, ct, selectionIds);
                    }
                    catch (InvalidOperationException ex) when (CookieManager.IsCookieFailureText(ex.Message) && cookieArgs.Enabled)
                    {
                        CookieManager.NoteCookieFailure(ex.Message);
                        cookieArgs = CookieArgs.None;
                        onLine(">>   cookies fallaron; reintento sin cookies...");
                        try
                        {
                            await RunWithAbortRetryAsync(
                                ytDlpPath, ffmpegPath, attemptUrl, attemptItems,
                                format, videoQuality, audioQuality,
                                attemptOrganize, itemSponsor, isPlaylist: false, destDir, archivePath,
                                relaxed, embedExtras, cookieArgs, fixedPlaylistFolder,
                                onLine, itemProgress, permanentFailures, ct, selectionIds);
                        }
                        catch (InvalidOperationException ex2)
                        {
                            onLine($">>   fallo: {ex2.Message}");
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        onLine($">>   fallo: {ex.Message}");
                    }

                    SyncSyntheticArchive([entry], ResolveScanRoot(destDir, fixedPlaylistFolder), archivePath);
                    ok = IsArchived(entry, archivePath);
                }

                if (!ok)
                {
                    onLine($">>   NO se pudo descargar: {entry.Title}");
                    Report(itemKey: ItemKey(entry), itemState: QueueItemState.Failed, currentTitle: entry.Title,
                        status: $"Error: {entry.Title}", isError: true);
                }
                else
                {
                    onLine($">>   OK: {entry.Title}");
                    Report(itemKey: ItemKey(entry), itemState: QueueItemState.Done, currentTitle: entry.Title,
                        status: $"OK: {entry.Title}");
                }
            }
        }

        Report(status: "Finalizando...");
        // Source of truth: files on disk (not archive alone). Partial/empty files don't count.
        SeedArchiveFromFolder(entries, destDir, fixedPlaylistFolder, archivePath, onLine: null);
        InvalidateArchiveCache();
        var totalFailed = entries.Count(e =>
            !EntryHasFileOnDisk(e, destDir, fixedPlaylistFolder));

        TryWriteM3u(organizeInFolders, isMultiItem, destDir, fixedPlaylistFolder, onLine);

        if (totalFailed == 0)
            onLine(">> Todos los items se descargaron correctamente.");
        else
            onLine($">> Listo con {totalFailed} item(s) que no se pudieron bajar (privados, eliminados, bloqueados o archivo incompleto).");

        return totalFailed;
    }

    private static void TryWriteM3u(
        bool organizeInFolders, bool isMultiItem, string destDir, string? fixedPlaylistFolder, Action<string> onLine)
    {
        if (!organizeInFolders || !isMultiItem || string.IsNullOrWhiteSpace(fixedPlaylistFolder)) return;
        try
        {
            var m3uPath = WritePlaylistM3u(destDir, fixedPlaylistFolder!);
            if (m3uPath != null)
                onLine($">> Playlist M3U: {m3uPath}");
        }
        catch (Exception ex)
        {
            onLine($">> No se pudo escribir el .m3u: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes archive lines for the current selection when the media files are gone
    /// (e.g. user deleted the playlist folder to re-download). Returns how many lines dropped.
    /// </summary>
    private static int PruneStaleArchiveEntries(
        List<PlaylistEntry> entries, string destDir, string? fixedPlaylistFolder,
        string archivePath, Action<string>? onLine)
    {
        if (!File.Exists(archivePath) || entries.Count == 0) return 0;

        var scanRoot = !string.IsNullOrWhiteSpace(fixedPlaylistFolder)
            ? Path.Combine(destDir, fixedPlaylistFolder)
            : destDir;
        var index = ExistingMediaIndex.Build(scanRoot);

        // Ids belonging to this run that no longer have a file on disk.
        var staleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (EntryOnDisk(e, index)) continue;
            foreach (var key in EntryArchiveKeys(e))
                staleIds.Add(key);
        }
        if (staleIds.Count == 0) return 0;

        string[] lines;
        try { lines = File.ReadAllLines(archivePath); }
        catch { return 0; }

        var kept = new List<string>(lines.Length);
        var removed = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                kept.Add(line);
                continue;
            }
            var m = ArchiveIdRegex.Match(line.Trim());
            if (m.Success && staleIds.Contains(m.Groups[1].Value))
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
            onLine?.Invoke($">> Archive: se quitaron {removed} id(s) sin archivo en disco.");
        }
        catch { return 0; }
        return removed;
    }

    private static bool EntryOnDisk(PlaylistEntry e, ExistingMediaIndex index)
    {
        var id = e.Id ?? "";
        var bare = id.StartsWith("url:", StringComparison.Ordinal) ? ""
            : id.StartsWith("idx:", StringComparison.Ordinal) ? ""
            : id;
        // Primary: bracket [id] in filename (our yt-dlp template).
        if (!string.IsNullOrEmpty(bare) && index.HasId(bare))
            return true;
        if (index.HasId(id))
            return true;
        // Synthetic ids only: strict long title match (never short/fuzzy).
        if (id.StartsWith("url:", StringComparison.Ordinal) || id.StartsWith("idx:", StringComparison.Ordinal)
            || string.IsNullOrEmpty(bare))
            return index.HasTitleStrict(e.Title);
        return false;
    }

    /// <summary>
    /// Copy selection-related lines from legacy root archive into the playlist-folder archive.
    /// </summary>
    private static void TryMigrateRootArchive(
        string destDir, string newArchivePath, List<PlaylistEntry> entries, Action<string>? onLine)
    {
        var rootArchive = Path.Combine(destDir, ArchiveFileName);
        if (string.Equals(Path.GetFullPath(rootArchive), Path.GetFullPath(newArchivePath), StringComparison.OrdinalIgnoreCase))
            return;
        if (!File.Exists(rootArchive)) return;

        var keys = new HashSet<string>(entries.SelectMany(EntryArchiveKeys), StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return;

        try
        {
            var existing = ReadArchiveIds(newArchivePath);
            var toCopy = new List<string>();
            foreach (var line in File.ReadLines(rootArchive))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var m = ArchiveIdRegex.Match(line.Trim());
                if (!m.Success) continue;
                var id = m.Groups[1].Value;
                if (!keys.Contains(id) || existing.Contains(id)) continue;
                toCopy.Add(line.Trim());
                existing.Add(id);
            }
            if (toCopy.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(newArchivePath)!);
            File.AppendAllLines(newArchivePath, toCopy);
            onLine?.Invoke($">> Archive: migrados {toCopy.Count} id(s) desde la carpeta padre.");
        }
        catch { /* ignore */ }
    }

    private static bool EntryHasFileOnDisk(PlaylistEntry e, string destDir, string? fixedPlaylistFolder)
    {
        // When organized, only scan the playlist folder (don't match sibling playlists in destDir).
        if (!string.IsNullOrWhiteSpace(fixedPlaylistFolder))
            return EntryOnDisk(e, ExistingMediaIndex.Build(Path.Combine(destDir, fixedPlaylistFolder)));
        return EntryOnDisk(e, ExistingMediaIndex.Build(destDir));
    }

    private static IEnumerable<string> EntryArchiveKeys(PlaylistEntry e)
    {
        var id = e.Id ?? "";
        if (string.IsNullOrWhiteSpace(id)) yield break;
        yield return id;
        if (!id.StartsWith("url:", StringComparison.Ordinal)
            && !id.StartsWith("idx:", StringComparison.Ordinal)
            && !id.Contains(':'))
        {
            yield return id; // bare youtube id as stored by yt-dlp
        }
        // Also bare after "extractor " form is handled by ArchiveIdRegex capture group.
    }

    /// <summary>
    /// Marks playlist entries as done in the yt-dlp archive when a matching file already
    /// exists under dest (or the playlist subfolder). Returns how many were newly skipped.
    /// </summary>
    private static int SeedArchiveFromFolder(
        List<PlaylistEntry> entries, string destDir, string? fixedPlaylistFolder,
        string archivePath, Action<string>? onLine)
    {
        // Scan only the media home for this job (playlist folder or destDir).
        var scanRoot = !string.IsNullOrWhiteSpace(fixedPlaylistFolder)
            ? Path.Combine(destDir, fixedPlaylistFolder)
            : destDir;
        var index = ExistingMediaIndex.Build(scanRoot);
        if (index.FileCount == 0) return 0;

        var archived = ReadArchiveIds(archivePath);
        var toAppend = new List<string>();
        var newly = 0;

        foreach (var e in entries)
        {
            var id = e.Id ?? "";
            var bare = id.StartsWith("url:", StringComparison.Ordinal) ? ""
                : id.StartsWith("idx:", StringComparison.Ordinal) ? ""
                : id;

            var onDisk = EntryOnDisk(e, index);

            if (!onDisk) continue;

            // yt-dlp archive lines are "extractor id" (e.g. "youtube dQw4w9WgXcQ"). Writing the
            // real extractor key makes yt-dlp itself skip; our CountMissing only needs the id.
            if (!string.IsNullOrEmpty(bare) && !archived.Contains(bare))
            {
                var ie = string.IsNullOrWhiteSpace(e.Extractor) ? "youtube" : e.Extractor.Trim();
                // Common IE keys are lowercase without spaces.
                ie = ie.Contains("youtube", StringComparison.OrdinalIgnoreCase) ? "youtube"
                    : ie.Contains("twitter", StringComparison.OrdinalIgnoreCase) || ie.Contains("x.com", StringComparison.OrdinalIgnoreCase) ? "twitter"
                    : ie.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ? "tiktok"
                    : ie.Contains("vimeo", StringComparison.OrdinalIgnoreCase) ? "vimeo"
                    : ie.Contains("soundcloud", StringComparison.OrdinalIgnoreCase) ? "soundcloud"
                    : "generic";
                toAppend.Add($"{ie} {bare}");
                archived.Add(bare);
                newly++;
                onLine?.Invoke($">>   ya en carpeta, omitido: {e.Title}");
            }
            else if ((id.StartsWith("url:", StringComparison.Ordinal) || id.StartsWith("idx:", StringComparison.Ordinal))
                     && !archived.Contains(id))
            {
                toAppend.Add($"recodio {id}");
                archived.Add(id);
                newly++;
                onLine?.Invoke($">>   ya en carpeta, omitido: {e.Title}");
            }
        }

        if (toAppend.Count > 0)
        {
            try { File.AppendAllLines(archivePath, toAppend); } catch { /* locked */ }
        }
        return newly;
    }

    private static string ResolveScanRoot(string destDir, string? fixedPlaylistFolder) =>
        !string.IsNullOrWhiteSpace(fixedPlaylistFolder)
            ? Path.Combine(destDir, fixedPlaylistFolder)
            : destDir;

    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Playlist";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length > 120) s = s[..120].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(s) ? "Playlist" : s;
    }

    private static string GuessPlaylistFolderName(string url, List<PlaylistEntry> entries)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                // youtube.com/playlist?list=PLxxx or watch?v=x&list=PLxxx
                var q = u.Query.TrimStart('?');
                foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2
                        && kv[0].Equals("list", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv[1]))
                        return "Playlist " + Uri.UnescapeDataString(kv[1]);
                }
                var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length > 0) return segs[^1];
            }
        }
        catch { /* ignore */ }
        return entries.Count > 1 ? $"Playlist ({entries.Count} items)" : "Playlist";
    }

    // Writes destDir/PlaylistName/PlaylistName.m3u listing media files in that folder.
    private static string? WritePlaylistM3u(string destDir, string playlistFolder)
    {
        var folder = Path.Combine(destDir, playlistFolder);
        if (!Directory.Exists(folder)) return null;

        string[] mediaExts = [".mp4", ".mkv", ".webm", ".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav", ".mov", ".avi"];
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => mediaExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return null;

        var m3uPath = Path.Combine(folder, SanitizeFolderName(playlistFolder) + ".m3u");
        var lines = new List<string> { "#EXTM3U" };
        foreach (var f in files)
        {
            lines.Add("#EXTINF:-1," + Path.GetFileNameWithoutExtension(f));
            // Relative paths so the m3u is portable inside the folder.
            lines.Add(Path.GetFileName(f));
        }
        File.WriteAllLines(m3uPath, lines);
        return m3uPath;
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

    public static string ItemKey(PlaylistEntry e) =>
        !string.IsNullOrWhiteSpace(e.Id) ? e.Id : $"idx:{e.Index}";

    private static async Task<int> RunWithAbortRetryAsync(
        string ytDlpPath, string ffmpegPath, string url, List<int>? playlistItems,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, bool isPlaylist, string destDir, string archivePath,
        bool relaxed, bool embedExtras, CookieArgs cookies, string? fixedPlaylistFolder,
        Action<string> onLine, Action<DownloadProgressUpdate> onProgress, HashSet<string> permanentFailures,
        CancellationToken ct,
        HashSet<string>? selectionIds = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lastExitCode = 0;
            try
            {
                return await RunOnceAsync(
                    ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                    organizeInFolders, sponsorBlock, isPlaylist, destDir, archivePath,
                    relaxed, embedExtras, cookies, fixedPlaylistFolder,
                    onLine, onProgress, permanentFailures,
                    code => lastExitCode = code, ct, selectionIds);
            }
            catch (InvalidOperationException) when (lastExitCode < 0 && attempt <= MaxAbortRetries)
            {
                var waitSecs = attempt * 12;
                onLine($">> Conexion cortada (posible rate-limit del sitio). Reintentando en {waitSecs}s... ({attempt}/{MaxAbortRetries})");
                onProgress(new DownloadProgressUpdate
                {
                    Status = $"Conexion cortada; reintento en {waitSecs}s...",
                    Phase = "retry",
                    IsError = true,
                });
                await Task.Delay(TimeSpan.FromSeconds(waitSecs), ct);
            }
        }
    }

    private static async Task<int> RunOnceAsync(
        string ytDlpPath, string ffmpegPath, string url, List<int>? playlistItems,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, bool isPlaylist, string destDir, string archivePath,
        bool relaxed, bool embedExtras, CookieArgs cookies, string? fixedPlaylistFolder,
        Action<string> onLine, Action<DownloadProgressUpdate> onProgress, HashSet<string> permanentFailures,
        Action<int> onExitCode, CancellationToken ct,
        HashSet<string>? selectionIds = null)
    {
        var extraArgs = new List<string>();
        var formatSelector = BuildFormatSelector(format, videoQuality, audioQuality, extraArgs, relaxed);

        // Output layout:
        // 1) fixed playlist folder from analyze title (most reliable)
        // 2) dynamic yt-dlp playlist fields with fallbacks
        // 3) flat into destDir
        string pathsHome;
        string outputTemplate;
        if (organizeInFolders && !string.IsNullOrWhiteSpace(fixedPlaylistFolder))
        {
            pathsHome = Path.Combine(destDir, fixedPlaylistFolder!);
            Directory.CreateDirectory(pathsHome);
            // Files go directly into the fixed playlist folder (works even for direct watch URLs).
            outputTemplate = "%(title)s [%(id)s].%(ext)s";
        }
        else if (organizeInFolders)
        {
            pathsHome = destDir;
            // Fallbacks: playlist_title → playlist → playlist_id → "Playlist"
            outputTemplate = "%(playlist_title,playlist,playlist_id&Playlist)s/%(title)s [%(id)s].%(ext)s";
        }
        else
        {
            pathsHome = destDir;
            outputTemplate = "%(title)s [%(id)s].%(ext)s";
        }

        Directory.CreateDirectory(pathsHome);
        var pathsHomeUnix = pathsHome.Replace('\\', '/');

        var args = new List<string>
        {
            "--newline", "--no-warnings",
            "--ffmpeg-location", ffmpegPath,
            "-f", formatSelector,
            // Prefer formats that actually exist for this client (drops broken/403 formats).
            "--check-formats",
            // -P sets the base path; -o is relative to it (more reliable than baking dest into -o).
            "-P", pathsHomeUnix,
            "--output-na-placeholder", "NA",
            "-o", outputTemplate,
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
            // When URL is video+playlist, prefer the full list if we asked for playlist items.
            "--yes-playlist",
        };

        // Remux/merge requires ffmpeg; if merge fails yt-dlp may leave separate streams.
        CookieManager.ApplyToArgs(args, cookies);

        // Metadata only — never download/write/embed thumbnails (no .jpg/.webp sidecar files).
        // embedExtras previously also pulled --embed-thumbnail + --convert-thumbnails jpg; that is banned.
        _ = embedExtras;
        args.Add("--embed-metadata");
        args.AddRange([
            "--no-write-thumbnail",
            "--no-write-all-thumbnails",
            "--no-embed-thumbnail",
        ]);

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
        var cookieFailSeen = false;

        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);

            if (CookieManager.IsCookieFailureText(line))
                cookieFailSeen = true;

            var dest = DestinationRegex.Match(line);
            if (dest.Success)
            {
                var name = Path.GetFileName(dest.Groups[1].Value.Trim().Trim('"'));
                var bareFromName = Regex.Match(name, @"\[([A-Za-z0-9_-]{3,})\]").Groups[1].Value;
                onProgress(new DownloadProgressUpdate
                {
                    CurrentTitle = name,
                    Status = "Descargando archivo...",
                    Phase = "download",
                    ItemKey = string.IsNullOrEmpty(bareFromName) ? null : bareFromName,
                    ItemState = QueueItemState.Downloading,
                    // file-only: leave OverallPercent null so UI does not jump
                    FilePercent = null,
                });
            }

            var detail = ProgressDetailRegex.Match(line);
            if (detail.Success
                && double.TryParse(detail.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pctDetail))
            {
                double? speed = detail.Groups[3].Success
                    ? DownloadProgressUpdate.ParseRateToBytes(detail.Groups[3].Value)
                    : null;
                TimeSpan? eta = detail.Groups[4].Success
                    ? DownloadProgressUpdate.ParseEta(detail.Groups[4].Value)
                    : null;
                var sizeInfo = detail.Groups[2].Success ? detail.Groups[2].Value : null;
                onProgress(new DownloadProgressUpdate
                {
                    FilePercent = (int)pctDetail,
                    // Do NOT set OverallPercent = file% — outer Report maps selection progress
                    SpeedBytesPerSec = speed,
                    Eta = eta,
                    SizeInfo = sizeInfo,
                    Status = $"Descargando… {(int)pctDetail}%",
                    Phase = "download",
                });
            }
            else
            {
                var pm = ProgressRegex.Match(line);
                if (pm.Success
                    && double.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    onProgress(new DownloadProgressUpdate
                    {
                        FilePercent = (int)pct,
                        Status = $"Descargando… {(int)pct}%",
                        Phase = "download",
                    });
                }
            }

            var im = ItemRegex.Match(line);
            if (im.Success)
            {
                onLine($">> Item {im.Groups[1].Value} de {im.Groups[2].Value}");
                if (int.TryParse(im.Groups[1].Value, out var n)
                    && int.TryParse(im.Groups[2].Value, out var m))
                {
                    onProgress(new DownloadProgressUpdate
                    {
                        // Batch subset counters (pending playlist-items), not full selection
                        BatchIndex = n,
                        BatchTotal = m,
                        CurrentIndex = n,
                        Total = m,
                        Status = $"Lote {n} de {m}",
                        Phase = "download",
                        ItemState = QueueItemState.Downloading,
                    });
                }
            }

            if (line.Contains("[Merger]", StringComparison.Ordinal)
                || line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
            {
                onProgress(new DownloadProgressUpdate
                {
                    FilePercent = 100,
                    Status = "Uniendo video + audio...",
                    Phase = "merge",
                });
            }
            else if (line.Contains("[ExtractAudio]", StringComparison.Ordinal)
                     || line.Contains("Extracting audio", StringComparison.OrdinalIgnoreCase))
            {
                onProgress(new DownloadProgressUpdate
                {
                    Status = "Extrayendo audio...",
                    Phase = "extract",
                });
            }

            // Only track real extractor ids — never the "Downloading item N of M" number.
            var em = ExtractorIdRegex.Match(line);
            if (em.Success)
            {
                lastExtractorId = em.Groups[2].Value;
                if (lastExtractorId.Length >= 3 && !IsLikelyItemCounter(lastExtractorId))
                {
                    onProgress(new DownloadProgressUpdate
                    {
                        ItemKey = lastExtractorId,
                        ItemState = QueueItemState.Downloading,
                        Phase = "download",
                    });
                }
            }

            if (!ErrorLineRegex.IsMatch(line)) return;
            if (IsIgnorableError(line)) return;

            if (IsPermanentErrorMessage(line))
            {
                var id = ExtractIdFromError(line) ?? lastExtractorId;
                // Only mark permanent if id belongs to this selection (never junk counters / other videos).
                if (!string.IsNullOrEmpty(id) && id.Length >= 3 && !IsLikelyItemCounter(id)
                    && (selectionIds == null || selectionIds.Count == 0 || SelectionContainsId(selectionIds, id)))
                {
                    permanentFailures.Add(id);
                }
                onLine($">>   (no disponible, no se reintentara): {Truncate(line, 160)}");
                onProgress(new DownloadProgressUpdate
                {
                    Status = Truncate(line, 120),
                    IsError = true,
                    ItemKey = id,
                    ItemState = QueueItemState.Failed,
                    Phase = "error",
                });
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

        // Surface cookie/DPAPI failure so the caller can retry without cookies.
        if (cookieFailSeen && cookies.Enabled)
        {
            if (CookieManager.NoteCookieFailure("DPAPI/cookie error during download"))
            {
                throw new InvalidOperationException(CookieManager.FriendlyCookieError("Failed to decrypt with DPAPI"));
            }
            // First failure only: continue without disabling session yet; caller may retry.
            throw new InvalidOperationException(CookieManager.FriendlyCookieError("Failed to decrypt with DPAPI"));
        }

        CookieManager.NoteCookieSuccess();
        return softErrorCount;
    }

    private static bool IsLikelyItemCounter(string id) =>
        id.Length <= 4 && int.TryParse(id, out _);

    private static bool SelectionContainsId(HashSet<string> selectionIds, string id)
    {
        if (selectionIds.Contains(id)) return true;
        foreach (var s in selectionIds)
        {
            if (s.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Length >= 3 && id.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
            if (id.Length >= 3 && s.Contains(id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
        if (permanent.Count == 0) return false;
        if (e.Title is "[Deleted video]" or "[Private video]") return true;
        foreach (var key in EntryArchiveKeys(e))
        {
            if (permanent.Contains(key)) return true;
            foreach (var p in permanent)
            {
                if (p.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Length >= 3 && p.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
                if (p.Length >= 3 && key.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
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

    private static bool IsArchived(PlaylistEntry e, string archivePath) =>
        IsArchivedInSet(e, ReadArchiveIds(archivePath));

    private static bool IsArchivedInSet(PlaylistEntry e, HashSet<string> archived)
    {
        if (string.IsNullOrEmpty(e.Id)) return false;
        if (archived.Contains(e.Id)) return true;
        // yt-dlp writes bare id; we may store "url:…" — also match bare id if Id is plain.
        if (!e.Id.Contains(':') && archived.Contains(e.Id)) return true;
        if (e.Id.StartsWith("url:", StringComparison.Ordinal) && archived.Contains(e.Id))
            return true;
        return false;
    }

    // For entries with synthetic ids (url:/idx:), yt-dlp won't write them into the archive.
    // Match strictly against our output template: "%(title)s [%(id)s].%(ext)s" — never short
    // title substrings alone (that falsely archived "Video" / "Track 1" and skipped retries).
    private static void SyncSyntheticArchive(IEnumerable<PlaylistEntry> entries, string destDir, string archivePath)
    {
        var archived = ReadArchiveIds(archivePath);
        var toAppend = new List<string>();
        string[]? files = null;

        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Id)) continue;
            if (archived.Contains(e.Id)) continue;

            // Real extractor ids are written by yt-dlp itself.
            if (!e.Id.StartsWith("url:", StringComparison.Ordinal) && !e.Id.StartsWith("idx:", StringComparison.Ordinal))
                continue;

            files ??= SafeEnumerateMedia(destDir);
            var bareId = e.Id.StartsWith("idx:", StringComparison.Ordinal) ? e.Id["idx:".Length..] : "";
            // Output template uses "[id]" at end of stem for real ids; synthetic url: keys rarely
            // appear in filenames — require id bracket match when we have a real-looking id, else
            // a long unique title stem (>= 16 chars) as whole-filename contains check.
            var titleHint = SanitizeFileHint(e.Title);
            var found = files.Any(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                // Prefer "[id]" from template when entry had a real id embedded in synthetic key.
                if (!string.IsNullOrEmpty(bareId) && bareId.Length >= 3
                    && name.Contains($"[{bareId}]", StringComparison.OrdinalIgnoreCase))
                    return true;
                // Webpage URL key: only match if filename ends with a long unique title segment.
                if (titleHint.Length >= 16
                    && name.Contains(titleHint, StringComparison.OrdinalIgnoreCase)
                    && name.Length <= titleHint.Length + 40) // not a random long name that merely contains it
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

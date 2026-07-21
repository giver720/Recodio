using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recodio;

public record PlaylistEntry(int Index, string Title, string Id);

public record AnalyzeResult(bool IsPlaylist, string? PlaylistTitle, List<PlaylistEntry> Entries);

public static class YtDlpDownloader
{
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
        foreach (var a in new[] { "--flat-playlist", "-J", "--no-warnings", url }) psi.ArgumentList.Add(a);

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
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "No se pudo analizar la URL" : stderr);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        if (root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var list = new List<PlaylistEntry>();
            var i = 1;
            foreach (var e in entriesEl.EnumerateArray())
            {
                var vTitle = e.TryGetProperty("title", out var vt) ? vt.GetString() ?? $"Video {i}" : $"Video {i}";
                var vId = e.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "";
                list.Add(new PlaylistEntry(i, vTitle, vId));
                i++;
            }
            return new AnalyzeResult(true, title, list);
        }

        var singleTitle = root.TryGetProperty("title", out var st) ? st.GetString() ?? url : url;
        var singleId = root.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "";
        return new AnalyzeResult(false, null, [new PlaylistEntry(1, singleTitle, singleId)]);
    }

    public static string BuildFormatSelector(string format, string videoQuality, string audioQuality, List<string> extraArgs)
    {
        string? heightFilter = videoQuality switch
        {
            "2160" or "1440" or "1080" or "720" or "480" or "360" => videoQuality,
            _ => null,
        };
        string? abrFilter = audioQuality switch { "medium" => "192", "low" => "128", _ => null };

        if (format == "mp3")
        {
            extraArgs.AddRange(["-x", "--audio-format", "mp3", "--audio-quality",
                audioQuality switch { "medium" => "192K", "low" => "128K", _ => "0" }]);
            return "bestaudio/best";
        }

        extraArgs.AddRange(["--merge-output-format", "mp4"]);
        var videoSel = heightFilter != null ? $"bestvideo[height<={heightFilter}]" : "bestvideo";
        var audioSel = abrFilter != null ? $"bestaudio[abr<={abrFilter}]" : "bestaudio";
        var fallback = heightFilter != null ? $"best[height<={heightFilter}]" : "best";
        return $"{videoSel}+{audioSel}/{fallback}";
    }

    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex ItemRegex = new(@"Downloading item (\d+) of (\d+)", RegexOptions.Compiled);
    private static readonly Regex ErrorLineRegex = new(@"^ERROR:", RegexOptions.Compiled);

    private const int MaxAbortRetries = 2;

    // Returns the number of videos that failed. With --ignore-errors a broken video (private,
    // deleted, region-locked) no longer aborts the whole playlist, so exit 0 can still hide
    // per-video failures - the caller shows an honest "N ok, M con error" summary.
    public static async Task<int> DownloadAsync(
        string ytDlpPath, string ffmpegPath, string url, List<int>? playlistItems,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, string destDir,
        Action<string> onLine, Action<int> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var isPlaylist = playlistItems is { Count: > 1 };

        // A negative exit code means yt-dlp was killed mid-run, almost always because YouTube
        // started blocking requests (bot detection from burst traffic). Retrying is safe because
        // --no-overwrites skips already-finished files and resumes partial (.part) ones, so a
        // retry never redoes completed work. Back off progressively between attempts.
        for (var attempt = 1; ; attempt++)
        {
            var lastExitCode = 0;
            try
            {
                return await RunOnceAsync(
                    ytDlpPath, ffmpegPath, url, playlistItems, format, videoQuality, audioQuality,
                    organizeInFolders, sponsorBlock, isPlaylist, destDir, onLine, onProgress,
                    code => lastExitCode = code, ct);
            }
            catch (InvalidOperationException) when (lastExitCode < 0 && attempt <= MaxAbortRetries)
            {
                var waitSecs = attempt * 10;
                onLine($">> YouTube parece estar bloqueando pedidos (corte abrupto). Reintentando en {waitSecs}s... ({attempt}/{MaxAbortRetries})");
                await Task.Delay(TimeSpan.FromSeconds(waitSecs), ct);
            }
        }
    }

    private static async Task<int> RunOnceAsync(
        string ytDlpPath, string ffmpegPath, string url, List<int>? playlistItems,
        string format, string videoQuality, string audioQuality, bool organizeInFolders,
        bool sponsorBlock, bool isPlaylist, string destDir,
        Action<string> onLine, Action<int> onProgress, Action<int> onExitCode, CancellationToken ct)
    {
        var extraArgs = new List<string>();
        var formatSelector = BuildFormatSelector(format, videoQuality, audioQuality, extraArgs);

        // yt-dlp's %(field&if_true|if_false)s conditional only supports LITERAL text in the
        // if_true branch - neither a nested %(playlist)s nor a bare field name gets substituted
        // there (confirmed against the installed yt-dlp: the nested form corrupts the parse,
        // the bare form is inserted as the literal word "playlist"). The correct construct is
        // --output-na-placeholder "" (empty instead of the default "NA") combined with a plain
        // %(playlist_title)s reference: when there's no playlist, the field resolves to "" and
        // the surrounding "//" collapses on its own, landing the file directly in destDir.
        // Joined with "/" by hand (not Path.Combine) because Path.Combine drops destDir
        // entirely when the second argument looks rooted (a leading "\" from a collapsed field).
        var outputTemplate = organizeInFolders
            ? "%(playlist_title)s/%(title)s.%(ext)s"
            : "%(title)s.%(ext)s";

        var args = new List<string>
        {
            "--newline", "--no-warnings",
            "--ffmpeg-location", ffmpegPath,
            "-f", formatSelector,
            "--embed-metadata", "--embed-thumbnail",
            "--output-na-placeholder", "",
            "-o", $"{destDir}/{outputTemplate}",
            // Resilience: one broken video doesn't abort the batch; skip finished files and
            // resume partial ones so retries are cheap; retry transient network/fragment/
            // extractor/file-lock (OneDrive, AV) failures instead of dying on them.
            "--ignore-errors", "--no-overwrites",
            "--retries", "10",
            "--fragment-retries", "10",
            "--extractor-retries", "3",
            "--file-access-retries", "5",
        };
        // Small pauses between requests only for playlists, to stay under YouTube's burst
        // radar; pointless (just latency) for a single video.
        if (isPlaylist)
            args.AddRange(["--sleep-requests", "0.75", "--sleep-interval", "1", "--max-sleep-interval", "3"]);
        // Cut community-flagged promo segments out of the final file. Deliberately limited to
        // sponsor/selfpromo/interaction: removing intros/outros/previews from regular videos
        // is too aggressive a default for a video downloader.
        if (sponsorBlock)
            args.AddRange(["--sponsorblock-remove", "sponsor,selfpromo,interaction"]);
        args.AddRange(extraArgs);
        if (playlistItems is { Count: > 0 })
            args.AddRange(["--playlist-items", string.Join(",", playlistItems)]);
        args.Add(url);

        var psi = new ProcessStartInfo { FileName = ytDlpPath };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var failCount = 0;
        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);
            var pm = ProgressRegex.Match(line);
            if (pm.Success) onProgress((int)double.Parse(pm.Groups[1].Value));
            var im = ItemRegex.Match(line);
            if (im.Success) onLine($">> Video {im.Groups[1].Value} de {im.Groups[2].Value}");
            if (ErrorLineRegex.IsMatch(line)) failCount++;
        }, exitCode =>
        {
            onExitCode(exitCode);
            var hint = exitCode < 0
                ? " Suele pasar cuando YouTube corta la conexion por demasiada descarga en rafaga; ya se reintento automaticamente. Los videos ya bajados quedaron guardados: podes volver a correr la misma URL y se saltean los que ya tenes."
                : "";
            return $"yt-dlp termino con codigo {exitCode}.{hint}";
        }, ct, throwOnPositiveExit: false);

        return failCount;
    }
}

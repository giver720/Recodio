using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Recodio;

// Wraps the spotdl CLI (Spotify metadata + YouTube Music audio matching).
// Kept fully separate from YtDlpDownloader/DownloadForm: different backend, its own
// output-format/bitrate/lyrics options, and its own destination folder, so the two
// download pipelines never share args or config.
public static class SpotDlDownloader
{
    public static readonly string[] Formats = ["mp3", "flac", "ogg", "opus", "m4a", "wav"];
    public static readonly string[] Bitrates =
        ["auto", "disable", "96k", "128k", "160k", "192k", "224k", "256k", "320k"];

    // spotDL's --threads is a plain asyncio.Semaphore capping concurrent song downloads - no
    // hard ceiling on spotDL's side. 8 was our own conservative default; raised so a good
    // connection can actually use it. The automatic rate-limit retry (which halves threads on
    // a YouTube bot-detection crash) is what keeps a high value safe to offer.
    public const int MaxThreads = 32;

    private const string ArchiveFileName = ".recodio-spotdl-archive.spotdl";

    private static readonly Regex CompleteRegex = new(@"^\s*(\d+)/(\d+)\s+complete\s*$", RegexOptions.Compiled);

    // spotDL logs every per-song failure as `logger.error("%s: %s", exception.__class__.__name__,
    // exception)` (see downloader.py), unconditionally regardless of when in the download the
    // failure happened. That line is the only reliable per-song failure marker in --simple-tui
    // output: the "SongName: Error" status line is only emitted when progress changed since the
    // last update, so early failures (e.g. no match found) can skip it entirely.
    private static readonly Regex ErrorLineRegex = new(@"^[A-Za-z][A-Za-z0-9_]*Error: ", RegexOptions.Compiled);

    public static string ArchivePathFor(string destDir) => Path.Combine(destDir, ArchiveFileName);

    public static string ResolveSpotDlPath()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "Scripts", "spotdl.exe");
        return File.Exists(candidate) ? candidate : "spotdl";
    }

    private const int MaxRateLimitRetries = 2;
    private const int MaxFailedSongRetries = 2;

    // spotDL's default (3) for retrying Spotify Web API requests on transient network/HTTP
    // failures during metadata fetch. A bit more headroom costs nothing and helps on flaky
    // connections.
    private const int SpotifyApiMaxRetries = 6;

    // Returns the number of songs that failed (spotDL can exit 0 even when individual songs
    // failed to match/download), so the caller can show an honest "N ok, M con error" summary
    // instead of a blanket "download complete".
    public static async Task<int> DownloadAsync(
        string spotdlPath, string ffmpegPath, string query, string format, string bitrate,
        bool embedLyrics, int threads, bool skipExisting, bool organizeInFolders, bool sponsorBlock,
        string destDir, Action<string> onLine, Action<int, int> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        var attemptThreads = Math.Clamp(threads, 1, MaxThreads);
        var failedSongPasses = 0;

        // spotDL/yt-dlp exit with a negative code when YouTube starts throttling/blocking
        // requests mid-playlist (bot detection from too much burst traffic). That's a burst-rate
        // problem, not a data problem, so retrying the exact same command with fewer threads is
        // safe to automate: spotDL always skips files it already finished (by filename, and via
        // --archive too when skipExisting is on), so a retry never redoes completed work.
        for (var attempt = 1; ; attempt++)
        {
            var lastExitCode = 0;
            int failCount;
            try
            {
                failCount = await RunOnceAsync(
                    spotdlPath, ffmpegPath, query, format, bitrate, embedLyrics, attemptThreads,
                    skipExisting, organizeInFolders, sponsorBlock, destDir,
                    onLine, onProgress, code => lastExitCode = code, ct);
            }
            catch (InvalidOperationException) when (lastExitCode < 0 && attemptThreads > 1 && attempt <= MaxRateLimitRetries)
            {
                attemptThreads = Math.Max(1, attemptThreads / 2);
                onLine($">> YouTube parece estar bloqueando pedidos por demasiada descarga en rafaga. Reintentando en 5s con {attemptThreads} hilo(s)...");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            // spotDL still exited 0 but some individual songs failed - usually a transient
            // YouTube matching hiccup, not a permanent problem. Re-running is cheap: songs
            // already on disk get skipped by filename (and by --archive when skipExisting is
            // on), so this only actually retries the stragglers.
            if (failCount > 0 && failedSongPasses < MaxFailedSongRetries)
            {
                failedSongPasses++;
                onLine($">> {failCount} cancion(es) fallaron, reintentando ({failedSongPasses}/{MaxFailedSongRetries})...");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            return failCount;
        }
    }

    private static async Task<int> RunOnceAsync(
        string spotdlPath, string ffmpegPath, string query, string format, string bitrate,
        bool embedLyrics, int threads, bool skipExisting, bool organizeInFolders, bool sponsorBlock,
        string destDir,
        Action<string> onLine, Action<int, int> onProgress, Action<int> onExitCode, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = spotdlPath };

        psi.ArgumentList.Add("--simple-tui");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add("--bitrate");
        psi.ArgumentList.Add(bitrate);
        psi.ArgumentList.Add("--threads");
        psi.ArgumentList.Add(threads.ToString());
        psi.ArgumentList.Add("--max-retries");
        psi.ArgumentList.Add(SpotifyApiMaxRetries.ToString());
        psi.ArgumentList.Add("--ffmpeg");
        psi.ArgumentList.Add(ffmpegPath);
        // {list-name} resolves to the playlist/album name and gets its own subfolder; for a
        // single track or search query (no list context) spotDL collapses that empty segment
        // back out on its own, so loose songs still land directly in destDir. Forward slashes
        // throughout (even after destDir) because that collapse only matches "//", not "\\".
        var outputTemplate = organizeInFolders
            ? $"{destDir.Replace('\\', '/')}/{{list-name}}/{{artists}} - {{title}}.{{output-ext}}"
            : $"{destDir.Replace('\\', '/')}/{{artists}} - {{title}}.{{output-ext}}";
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputTemplate);
        if (embedLyrics)
        {
            psi.ArgumentList.Add("--lyrics");
            psi.ArgumentList.Add("genius");
        }
        // Runs yt-dlp's SponsorBlock post-processor over each song: queries the community
        // database and cuts non-music segments (sponsors, intros/outros, silence) out of the
        // downloaded audio. Costs one extra API lookup per song, hence opt-in.
        if (sponsorBlock)
        {
            psi.ArgumentList.Add("--sponsor-block");
        }
        if (skipExisting)
        {
            // Tracks already-downloaded Spotify track IDs in a file inside the destination
            // folder, so re-running the same or a bigger playlist only fetches what's missing
            // (robust to renames, unlike matching on the output filename).
            psi.ArgumentList.Add("--archive");
            psi.ArgumentList.Add(ArchivePathFor(destDir));
        }
        psi.ArgumentList.Add("download");
        psi.ArgumentList.Add(query);

        var failCount = 0;
        await ProcessRunner.RunAsync(psi, line =>
        {
            onLine(line);
            var m = CompleteRegex.Match(line);
            if (m.Success) onProgress(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            if (ErrorLineRegex.IsMatch(line)) failCount++;
        }, exitCode =>
        {
            onExitCode(exitCode);
            var hint = exitCode < 0
                ? " Esto suele pasar cuando YouTube empieza a bloquear pedidos por demasiada descarga en rafaga (mas probable en playlists grandes); ya se reintento automaticamente bajando los hilos. Las canciones que ya se completaron quedaron guardadas: podes volver a correr la misma URL mas tarde y spotDL va a saltear las que ya tenes."
                : "";
            return $"spotdl termino con codigo {exitCode}.{hint}";
        }, ct);

        return failCount;
    }
}

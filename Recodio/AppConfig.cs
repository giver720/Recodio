namespace Recodio;

public class AppConfig
{
    public string WatchDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "spotube");
    public string OutputDir { get; set; } = "";
    public string Quality { get; set; } = "high"; // high | medium | low
    public string DownloadDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ytdlp");
    public string Theme { get; set; } = "dark"; // dark | light | system

    // auto = FileSystemWatcher converts new files; manual = only "Convertir pendientes"
    public string WatchMode { get; set; } = "auto"; // auto | manual

    // Format used by the watch-folder sweep (manual button and auto watcher).
    public string WatchConvertFormat { get; set; } = "mp3";

    // When the destination file already exists during conversion: skip | overwrite | rename
    public string OnFileExists { get; set; } = "skip";

    public bool ClipboardAutoFill { get; set; } = true;

    // Global cookies browser for yt-dlp AND spotDL (--cookies-from-browser KEY).
    // "" = off. Default empty; LoadConfig / first run resolves Brave if installed.
    // chrome | edge | firefox | brave | opera | chromium | ""
    public string CookiesBrowser { get; set; } = "";

    // spotDL is a separate download pipeline (Spotify metadata + YouTube Music audio)
    // from the yt-dlp video downloader above, so it keeps its own independent settings.
    public string SpotDlDownloadDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "spotdl");
    public string SpotDlFormat { get; set; } = "mp3"; // mp3 | flac | ogg | opus | m4a | wav
    public string SpotDlBitrate { get; set; } = "auto";
    public bool SpotDlLyrics { get; set; } = false;
    public int SpotDlThreads { get; set; } = 4; // spotDL's own default; lower = less burst load on YouTube
    public bool SpotDlSkipExisting { get; set; } = true; // only download songs not already in the archive
    public bool SpotDlOrganizeInFolders { get; set; } = true; // subfolder per playlist/album; loose songs stay loose
    public bool SpotDlSponsorBlock { get; set; } = false; // cut non-music segments (intros/outros/sponsors) via SponsorBlock

    // Comma-separated: youtube-music,youtube,soundcloud,bandcamp,piped
    public string SpotDlAudioProviders { get; set; } = "youtube-music,youtube,soundcloud";

    // Legacy (pre-1.3.2): migrated into CookiesBrowser on load if CookiesBrowser is empty.
    public string SpotDlCookiesBrowser { get; set; } = "";

    // Optional: your own free Spotify app (Client Credentials flow - app-only auth for reading
    // public catalog metadata, no user login, no Premium required). spotDL's built-in default
    // app is shared by every spotDL install worldwide and gets rate-limited by Spotify for
    // hours at a time under global load; your own app has its own private quota. Empty = fall
    // back to spotDL's shared default (works until it doesn't).
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyClientSecret { get; set; } = "";

    // ISO timestamp of the last automatic yt-dlp/spotDL update check (throttles it to daily).
    public string LastToolsUpdateCheck { get; set; } = "";

    // yt-dlp retry policy (Recodio wrapper). User-controlled from Download form.
    // Defaults = "balanced" (finish reasonably without endless loops).
    public int YtDlpBatchPasses { get; set; } = 2;      // full-list passes (1–4)
    public int YtDlpPerItemAttempts { get; set; } = 2;  // one-by-one retries (1–5)
    public int YtDlpAbortRetries { get; set; } = 1;     // connection-cut retries (0–3)
    public int YtDlpCliRetries { get; set; } = 5;       // yt-dlp --retries (1–15)

    /// <summary>Effective cookies key after legacy migration + Brave auto-default.</summary>
    public string EffectiveCookiesBrowser()
    {
        if (!string.IsNullOrWhiteSpace(CookiesBrowser))
            return CookiesBrowser.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(SpotDlCookiesBrowser))
            return SpotDlCookiesBrowser.Trim().ToLowerInvariant();
        return BrowserCookies.ResolveDefault("");
    }

    public static string QualityLabel(string quality) => quality switch
    {
        "medium" => "Media (192 kbps)",
        "low" => "Baja (128 kbps)",
        _ => "Alta (VBR ~245 kbps)",
    };

    public static string QualityFfmpegArgs(string quality) => quality switch
    {
        "medium" => "-b:a 192k",
        "low" => "-b:a 128k",
        _ => "-q:a 0",
    };

    public bool IsWatchAuto => !string.Equals(WatchMode, "manual", StringComparison.OrdinalIgnoreCase);
}

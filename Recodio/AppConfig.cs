namespace Recodio;

public class AppConfig
{
    public string WatchDir { get; set; } = @"C:\Users\gerar\Music\spotube";
    public string OutputDir { get; set; } = "";
    public string Quality { get; set; } = "high"; // high | medium | low
    public string DownloadDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ytdlp");
    public string Theme { get; set; } = "dark"; // dark | light | system

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

    // ISO timestamp of the last automatic yt-dlp/spotDL update check (throttles it to daily).
    public string LastToolsUpdateCheck { get; set; } = "";

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
}

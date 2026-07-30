use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// En Linux las carpetas XDG pueden no existir (sesión mínima, contenedor,
/// WSL). Caer al directorio personal es infinitamente mejor que caer a `.`,
/// que dejaría las descargas donde sea que se lanzara el ejecutable.
fn media_dir(preferred: Option<PathBuf>) -> PathBuf {
    preferred
        .or_else(dirs::download_dir)
        .or_else(dirs::home_dir)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Recodio")
}

fn default_download_dir() -> PathBuf {
    media_dir(dirs::video_dir())
}

fn default_audio_dir() -> PathBuf {
    media_dir(dirs::audio_dir())
}

/// What to do when a file for the same source id already exists on disk.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DuplicatePolicy {
    /// Silently skip the item.
    Skip,
    /// Download again and replace the existing file.
    Overwrite,
    /// Preselect nothing and let the user decide per item in the preview.
    Ask,
}

impl Default for DuplicatePolicy {
    fn default() -> Self {
        DuplicatePolicy::Skip
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub video_dir: PathBuf,
    pub audio_dir: PathBuf,
    /// How many downloads run at the same time.
    pub concurrency: usize,
    pub duplicate_policy: DuplicatePolicy,

    // SponsorBlock
    pub sponsorblock: bool,
    /// Categories cut out of the file entirely.
    pub sponsorblock_remove: Vec<String>,
    /// Categories kept but marked as chapters.
    pub sponsorblock_mark: Vec<String>,

    // Video
    /// `best` or a max height: `2160`, `1440`, `1080`, `720`, `480`, `360`.
    pub video_quality: String,
    /// Remux container: `mp4`, `mkv`, or `original`.
    pub video_container: String,

    // Audio
    /// `mp3`, `m4a`, `opus`, `flac`, `wav`.
    pub audio_format: String,
    /// Bitrate in kbps for lossy formats.
    pub audio_bitrate: String,

    // Extras
    pub embed_thumbnail: bool,
    pub embed_metadata: bool,
    pub embed_chapters: bool,
    pub write_subtitles: bool,
    pub embed_subtitles: bool,
    pub subtitle_langs: String,
    /// Keep a yt-dlp archive file so already-seen ids are never re-fetched.
    pub use_archive: bool,

    // Access / networking
    /// Browser to pull cookies from (`chrome`, `firefox`, `edge`, …). Needed for
    /// age-gated, private and region-blocked material.
    pub cookies_from_browser: Option<String>,
    /// Path to a Netscape-format cookies.txt file.
    pub cookies_file: Option<PathBuf>,
    pub proxy: Option<String>,
    /// e.g. `2M`. Empty means unlimited.
    pub rate_limit: Option<String>,
    pub retries: u32,
    /// Keep going when one item of a playlist fails.
    pub ignore_errors: bool,

    /// Naming template applied under the destination folder.
    pub output_template: String,
    /// Put playlist downloads in their own subfolder.
    pub playlist_subfolder: bool,

    /// External player launched from the library ("" = system default).
    pub external_player: Option<String>,

    pub theme: String,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            video_dir: default_download_dir(),
            audio_dir: default_audio_dir(),
            concurrency: 3,
            duplicate_policy: DuplicatePolicy::Skip,

            sponsorblock: true,
            sponsorblock_remove: vec![
                "sponsor".into(),
                "selfpromo".into(),
                "interaction".into(),
            ],
            sponsorblock_mark: vec!["intro".into(), "outro".into(), "preview".into()],

            video_quality: "best".into(),
            video_container: "mp4".into(),

            audio_format: "mp3".into(),
            audio_bitrate: "320".into(),

            embed_thumbnail: true,
            embed_metadata: true,
            embed_chapters: true,
            write_subtitles: false,
            embed_subtitles: false,
            subtitle_langs: "es,en".into(),
            use_archive: false,

            cookies_from_browser: None,
            cookies_file: None,
            proxy: None,
            rate_limit: None,
            retries: 10,
            ignore_errors: true,

            output_template: "%(title)s [%(id)s].%(ext)s".into(),
            playlist_subfolder: true,

            external_player: None,

            theme: "dark".into(),
        }
    }
}

impl Settings {
    pub fn load(path: &std::path::Path) -> Self {
        std::fs::read_to_string(path)
            .ok()
            .and_then(|raw| serde_json::from_str(&raw).ok())
            .unwrap_or_default()
    }

    pub fn save(&self, path: &std::path::Path) -> anyhow::Result<()> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(path, serde_json::to_string_pretty(self)?)?;
        Ok(())
    }
}

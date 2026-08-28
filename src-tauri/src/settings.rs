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
    /// Carpetas del equipo que el rastreo repasa además de las de descarga.
    ///
    /// Lo que aparezca en ellas entra en la biblioteca sin agrupar, porque la
    /// música que no está en ninguna lista es la mayoría de la de cualquiera.
    /// Vacío de fábrica: rastrear el disco de alguien sin que lo haya pedido no.
    pub watched_dirs: Vec<PathBuf>,
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

/// Ajustes opcionales propios de una Fuente. `None` significa «heredar el
/// ajuste general», de modo que los perfiles antiguos y los recién creados no
/// cambian el comportamiento hasta que el usuario elige algo explícitamente.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SourceProfile {
    pub dest_dir: Option<String>,
    pub video_quality: Option<String>,
    pub video_container: Option<String>,
    pub audio_format: Option<String>,
    pub audio_bitrate: Option<String>,
    pub sponsorblock: Option<bool>,
    pub write_subtitles: Option<bool>,
    pub embed_subtitles: Option<bool>,
    pub subtitle_langs: Option<String>,
    /// Ruta de la cuenta importada elegida para esta Fuente.
    pub youtube_cookies_file: Option<String>,
}

impl SourceProfile {
    /// Mezcla sólo los valores expresos y conserva el resto de la configuración
    /// general. Esta misma instantánea viaja dentro del trabajo en cola.
    pub fn apply_to(&self, settings: &mut Settings) {
        if let Some(value) = &self.video_quality {
            settings.video_quality = value.clone();
        }
        if let Some(value) = &self.video_container {
            settings.video_container = value.clone();
        }
        if let Some(value) = &self.audio_format {
            settings.audio_format = value.clone();
        }
        if let Some(value) = &self.audio_bitrate {
            settings.audio_bitrate = value.clone();
        }
        if let Some(value) = self.sponsorblock {
            settings.sponsorblock = value;
        }
        if let Some(value) = self.write_subtitles {
            settings.write_subtitles = value;
        }
        if let Some(value) = self.embed_subtitles {
            settings.embed_subtitles = value;
        }
        if let Some(value) = &self.subtitle_langs {
            settings.subtitle_langs = value.clone();
        }
        if let Some(value) = &self.youtube_cookies_file {
            settings.cookies_file = Some(PathBuf::from(value));
            settings.cookies_from_browser = None;
        }
    }
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            video_dir: default_download_dir(),
            audio_dir: default_audio_dir(),
            watched_dirs: Vec::new(),
            concurrency: 3,
            duplicate_policy: DuplicatePolicy::Skip,

            sponsorblock: true,
            sponsorblock_remove: vec!["sponsor".into(), "selfpromo".into(), "interaction".into()],
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

/// Carpetas del sistema donde suele haber música y vídeo, si existen.
///
/// No se rastrean solas: la interfaz las ofrece para añadirlas de un clic, que
/// es la diferencia entre sugerir y meterse donde no te llaman.
pub fn suggested_dirs() -> Vec<PathBuf> {
    [dirs::audio_dir(), dirs::video_dir(), dirs::download_dir()]
        .into_iter()
        .flatten()
        .filter(|p| p.is_dir())
        .collect()
}

impl Settings {
    /// Todo lo que repasa un rastreo: las carpetas de descarga van siempre,
    /// porque lo que Recodio deja ahí es biblioteca por definición.
    pub fn scan_roots(&self) -> Vec<PathBuf> {
        let mut raices = vec![self.video_dir.clone(), self.audio_dir.clone()];
        for d in &self.watched_dirs {
            if !raices.iter().any(|r| r == d) {
                raices.push(d.clone());
            }
        }
        raices
    }

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

#[cfg(test)]
mod tests {
    use super::{Settings, SourceProfile};

    #[test]
    fn un_perfil_solo_sobrescribe_lo_que_declara() {
        let mut settings = Settings::default();
        settings.video_quality = "720".into();
        settings.audio_format = "mp3".into();
        settings.sponsorblock = true;

        SourceProfile {
            video_quality: Some("1080".into()),
            sponsorblock: Some(false),
            ..Default::default()
        }
        .apply_to(&mut settings);

        assert_eq!(settings.video_quality, "1080");
        assert_eq!(settings.audio_format, "mp3");
        assert!(!settings.sponsorblock);
    }

    #[test]
    fn una_cuenta_de_fuente_reemplaza_el_navegador_global() {
        let mut settings = Settings::default();
        settings.cookies_from_browser = Some("brave".into());

        SourceProfile {
            youtube_cookies_file: Some("cuenta.txt".into()),
            ..Default::default()
        }
        .apply_to(&mut settings);

        assert_eq!(
            settings.cookies_file.unwrap().to_string_lossy(),
            "cuenta.txt"
        );
        assert!(settings.cookies_from_browser.is_none());
    }
}

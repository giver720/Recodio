use crate::binaries::Binaries;
use crate::db::Db;
use crate::settings::Settings;
use anyhow::Result;
use std::path::PathBuf;
use std::sync::RwLock;

/// Everything the commands and the queue share.
pub struct Core {
    pub db: Db,
    pub bins: Binaries,
    pub settings: RwLock<Settings>,
    pub settings_path: PathBuf,
    /// yt-dlp archive file, used when "no volver a descargar nunca" is on.
    pub archive_path: PathBuf,
}

impl Core {
    pub fn new(data_dir: PathBuf, config_dir: PathBuf) -> Result<Self> {
        std::fs::create_dir_all(&data_dir)?;
        std::fs::create_dir_all(&config_dir)?;

        let settings_path = config_dir.join("settings.json");
        let settings = Settings::load(&settings_path);
        let _ = std::fs::create_dir_all(&settings.video_dir);
        let _ = std::fs::create_dir_all(&settings.audio_dir);

        Ok(Self {
            db: Db::open(&data_dir.join("recodio.db"))?,
            bins: Binaries::new(data_dir.join("bin")),
            settings: RwLock::new(settings),
            settings_path,
            archive_path: data_dir.join("archive.txt"),
        })
    }

    pub fn save_settings(&self) -> Result<()> {
        self.settings.read().unwrap().save(&self.settings_path)
    }
}

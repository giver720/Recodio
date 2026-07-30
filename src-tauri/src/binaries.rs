use anyhow::{anyhow, Result};
use serde::Serialize;
use std::path::{Path, PathBuf};

/// Recodio ships its own copies of the tools under the app data folder so a
/// broken or outdated system install can never break downloads. If a managed
/// copy is missing we fall back to whatever is on PATH.
pub struct Binaries {
    dir: PathBuf,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolStatus {
    pub name: String,
    pub found: bool,
    pub path: Option<String>,
    pub version: Option<String>,
    /// True when the binary lives in Recodio's own folder.
    pub managed: bool,
}

#[cfg(windows)]
const EXE: &str = ".exe";
#[cfg(not(windows))]
const EXE: &str = "";

impl Binaries {
    pub fn new(dir: PathBuf) -> Self {
        let _ = std::fs::create_dir_all(&dir);
        Self { dir }
    }

    fn managed_path(&self, name: &str) -> PathBuf {
        self.dir.join(format!("{name}{EXE}"))
    }

    /// Managed copy first, then PATH.
    pub fn resolve(&self, name: &str) -> Option<PathBuf> {
        let managed = self.managed_path(name);
        if managed.is_file() {
            return Some(managed);
        }
        which::which(name).ok()
    }

    pub fn require(&self, name: &str) -> Result<PathBuf> {
        self.resolve(name)
            .ok_or_else(|| anyhow!("No se encontró `{name}`. Instálalo desde Ajustes › Herramientas."))
    }

    pub fn status(&self, name: &str, version_arg: &str) -> ToolStatus {
        let Some(path) = self.resolve(name) else {
            return ToolStatus {
                name: name.into(),
                found: false,
                path: None,
                version: None,
                managed: false,
            };
        };
        let managed = path.starts_with(&self.dir);
        let version = std::process::Command::new(&path)
            .arg(version_arg)
            .output()
            .ok()
            .and_then(|o| {
                let text = if o.stdout.is_empty() { o.stderr } else { o.stdout };
                String::from_utf8_lossy(&text)
                    .lines()
                    .next()
                    .map(|s| s.trim().to_string())
            })
            .filter(|s| !s.is_empty());

        ToolStatus {
            name: name.into(),
            found: true,
            path: Some(path.to_string_lossy().into_owned()),
            version,
            managed,
        }
    }

    pub fn status_all(&self) -> Vec<ToolStatus> {
        vec![
            self.status("yt-dlp", "--version"),
            self.status("spotdl", "--version"),
            self.status("ffmpeg", "-version"),
        ]
    }

    /// Download the latest standalone yt-dlp into the managed folder.
    pub async fn install_ytdlp(&self) -> Result<String> {
        let asset = if cfg!(windows) {
            "yt-dlp.exe"
        } else if cfg!(target_os = "macos") {
            "yt-dlp_macos"
        } else {
            "yt-dlp_linux"
        };
        let url = format!("https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}");

        let bytes = reqwest::Client::builder()
            .user_agent("Recodio")
            .build()?
            .get(&url)
            .send()
            .await?
            .error_for_status()?
            .bytes()
            .await?;

        let dest = self.managed_path("yt-dlp");
        std::fs::write(&dest, &bytes)?;
        make_executable(&dest)?;
        Ok(dest.to_string_lossy().into_owned())
    }

    /// yt-dlp knows how to replace itself; that is the fastest way to stay ahead
    /// of YouTube changes.
    pub fn update_ytdlp(&self) -> Result<String> {
        let path = self.require("yt-dlp")?;
        let out = std::process::Command::new(path).arg("-U").output()?;
        let text = format!(
            "{}{}",
            String::from_utf8_lossy(&out.stdout),
            String::from_utf8_lossy(&out.stderr)
        );
        Ok(text.trim().to_string())
    }
}

#[cfg(unix)]
fn make_executable(path: &Path) -> Result<()> {
    use std::os::unix::fs::PermissionsExt;
    let mut perms = std::fs::metadata(path)?.permissions();
    perms.set_mode(0o755);
    std::fs::set_permissions(path, perms)?;
    Ok(())
}

#[cfg(not(unix))]
fn make_executable(_path: &Path) -> Result<()> {
    Ok(())
}

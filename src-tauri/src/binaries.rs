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

    /// Copia propia, luego el `PATH`, y por último las carpetas donde estas
    /// herramientas acaban de verdad.
    ///
    /// Lo último no es paranoia: una aplicación lanzada desde el menú del
    /// escritorio hereda un `PATH` mínimo, sin `~/.local/bin`, que es justo
    /// donde `pip install --user` y `pipx` dejan yt-dlp y spotDL. Buscarlas solo
    /// en el `PATH` hace que la app jure que no están instaladas mientras el
    /// usuario las ejecuta sin problema desde su terminal.
    pub fn resolve(&self, name: &str) -> Option<PathBuf> {
        let managed = self.managed_path(name);
        if managed.is_file() {
            return Some(managed);
        }
        if let Ok(found) = which::which(name) {
            return Some(found);
        }
        fallback_dirs()
            .into_iter()
            .map(|dir| dir.join(format!("{name}{EXE}")))
            .find(|p| p.is_file())
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

    /// Instala una herramienta en la carpeta propia de Recodio, sin tocar el
    /// sistema ni pedir contraseña. `on_progress` recibe la fracción descargada,
    /// o `-1` mientras el servidor no diga cuánto pesa.
    pub async fn install(
        &self,
        name: &str,
        on_progress: impl Fn(f64) + Send + Sync,
    ) -> Result<String> {
        match name {
            "yt-dlp" => self.install_single(name, &ytdlp_url(), &on_progress).await,
            "spotdl" => {
                let url = spotdl_url().await?;
                self.install_single(name, &url, &on_progress).await
            }
            "ffmpeg" => self.install_ffmpeg(&on_progress).await,
            otro => Err(anyhow!("Recodio no sabe instalar `{otro}`")),
        }
    }

    /// Para las herramientas que se publican como un único ejecutable.
    async fn install_single(
        &self,
        name: &str,
        url: &str,
        on_progress: &(impl Fn(f64) + Send + Sync),
    ) -> Result<String> {
        // Se descarga a un temporal y se mueve al final: si algo se corta a
        // medias, no queda un ejecutable truncado que parezca instalado.
        let temp = self.dir.join(format!("{name}.descargando"));
        download_to(url, &temp, on_progress).await?;

        let dest = self.managed_path(name);
        let _ = std::fs::remove_file(&dest);
        std::fs::rename(&temp, &dest)?;
        make_executable(&dest)?;
        Ok(dest.to_string_lossy().into_owned())
    }

    /// ffmpeg no se publica suelto, viene dentro de un comprimido con todo el
    /// paquete. Se extrae con el `tar` del sistema —GNU en Linux, bsdtar en
    /// Windows 10 en adelante, y este último también abre zip— para no arrastrar
    /// media docena de dependencias de compresión solo para esto.
    async fn install_ffmpeg(&self, on_progress: &(impl Fn(f64) + Send + Sync)) -> Result<String> {
        let (url, ext) = ffmpeg_url()?;
        let archivo = self.dir.join(format!("ffmpeg-descarga.{ext}"));
        download_to(&url, &archivo, on_progress).await?;

        let extraido = self.dir.join("ffmpeg-extraido");
        let _ = std::fs::remove_dir_all(&extraido);
        std::fs::create_dir_all(&extraido)?;

        let salida = crate::proc::command("tar")
            .arg("-xf")
            .arg(&archivo)
            .arg("-C")
            .arg(&extraido)
            .output()
            .map_err(|e| {
                anyhow!("No se encontró `tar` para descomprimir ffmpeg: {e}. Instálalo o usa el gestor de paquetes de tu sistema.")
            })?;
        let _ = std::fs::remove_file(&archivo);

        if !salida.status.success() {
            let _ = std::fs::remove_dir_all(&extraido);
            return Err(anyhow!(
                "No se pudo descomprimir ffmpeg: {}",
                String::from_utf8_lossy(&salida.stderr).trim()
            ));
        }

        // El comprimido trae una carpeta con nombre variable según la versión,
        // así que se buscan los ejecutables en vez de adivinar la ruta.
        let mut instalados = 0;
        for herramienta in ["ffmpeg", "ffprobe"] {
            let nombre = format!("{herramienta}{EXE}");
            if let Some(encontrado) = find_file(&extraido, &nombre, 5) {
                let destino = self.dir.join(&nombre);
                let _ = std::fs::remove_file(&destino);
                std::fs::copy(&encontrado, &destino)?;
                make_executable(&destino)?;
                instalados += 1;
            }
        }
        let _ = std::fs::remove_dir_all(&extraido);

        if instalados == 0 {
            return Err(anyhow!("El paquete de ffmpeg no contenía los ejecutables esperados"));
        }
        Ok(self.dir.join(format!("ffmpeg{EXE}")).to_string_lossy().into_owned())
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

#[cfg(test)]
mod tests {
    use super::*;

    /// `~/.local/bin` es donde `pip install --user` y `pipx` dejan yt-dlp y
    /// spotDL, y es justo la carpeta que falta en el `PATH` de una aplicación
    /// lanzada desde el menú del escritorio. Si desaparece de esta lista,
    /// volvemos al fallo de "no encuentro yt-dlp" con yt-dlp instalado.
    #[test]
    #[cfg(unix)]
    fn busca_en_local_bin_del_usuario() {
        let home = dirs::home_dir().expect("debería haber un directorio personal");
        let objetivo = home.join(".local").join("bin");
        std::fs::create_dir_all(&objetivo).ok();

        assert!(
            fallback_dirs().contains(&objetivo),
            "~/.local/bin debe estar entre las carpetas de respaldo, están: {:?}",
            fallback_dirs()
        );
    }

    /// Descarga de verdad las tres herramientas y comprueba que se ejecutan.
    /// Baja bastantes megas, así que no corre en la suite normal:
    ///     cargo test --lib -- --ignored --nocapture instala_las_herramientas
    #[tokio::test]
    #[ignore]
    async fn instala_las_herramientas_y_funcionan() {
        let dir = std::env::temp_dir().join(format!("recodio-install-{}", uuid::Uuid::new_v4()));
        let bins = Binaries::new(dir.clone());

        for (herramienta, arg) in [
            ("yt-dlp", "--version"),
            ("ffmpeg", "-version"),
            ("spotdl", "--version"),
        ] {
            let avance = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
            let contador = avance.clone();

            let ruta = bins
                .install(herramienta, move |_| {
                    contador.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                })
                .await
                .unwrap_or_else(|e| panic!("no se pudo instalar {herramienta}: {e}"));

            assert!(Path::new(&ruta).is_file(), "{herramienta} no quedó en disco");
            assert!(
                avance.load(std::sync::atomic::Ordering::Relaxed) > 1,
                "{herramienta} debería informar del avance de la descarga"
            );

            // Que exista el archivo no basta: tiene que arrancar.
            let estado = bins.status(herramienta, arg);
            assert!(estado.found, "{herramienta} no se encuentra tras instalarlo");
            assert!(estado.managed, "{herramienta} debería ser la copia propia");
            let version = estado
                .version
                .unwrap_or_else(|| panic!("{herramienta} no respondió a {arg}"));
            println!("  {herramienta}: {version}");
        }

        // ffprobe viaja con ffmpeg y yt-dlp lo necesita para algunos formatos.
        assert!(dir.join(format!("ffprobe{EXE}")).is_file(), "falta ffprobe");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn la_copia_propia_gana_al_resto() {
        let dir = std::env::temp_dir().join(format!("recodio-bin-{}", uuid::Uuid::new_v4()));
        let bins = Binaries::new(dir.clone());

        // Un nombre que no existe en ningún sistema, para que solo pueda
        // encontrarse la copia gestionada.
        let nombre = "recodio-herramienta-inventada";
        assert!(bins.resolve(nombre).is_none());

        let propio = dir.join(format!("{nombre}{EXE}"));
        std::fs::write(&propio, b"#!/bin/sh\n").unwrap();
        assert_eq!(bins.resolve(nombre), Some(propio));

        std::fs::remove_dir_all(&dir).ok();
    }
}

fn http() -> Result<reqwest::Client> {
    Ok(reqwest::Client::builder().user_agent("Recodio").build()?)
}

/// Descarga informando del avance. Escribe según llega en vez de acumular el
/// archivo entero en memoria: ffmpeg pasa de los cien megas.
async fn download_to(
    url: &str,
    dest: &Path,
    on_progress: &(impl Fn(f64) + Send + Sync),
) -> Result<()> {
    use futures_util::StreamExt;
    use std::io::Write;

    let respuesta = http()?.get(url).send().await?.error_for_status()?;
    let total = respuesta.content_length().unwrap_or(0);

    let mut archivo = std::fs::File::create(dest)?;
    let mut descargado: u64 = 0;
    let mut stream = respuesta.bytes_stream();

    on_progress(if total > 0 { 0.0 } else { -1.0 });
    while let Some(trozo) = stream.next().await {
        let trozo = trozo?;
        archivo.write_all(&trozo)?;
        descargado += trozo.len() as u64;
        on_progress(if total > 0 {
            (descargado as f64 / total as f64).clamp(0.0, 1.0)
        } else {
            -1.0
        });
    }
    archivo.flush()?;
    Ok(())
}

fn ytdlp_url() -> String {
    let asset = if cfg!(windows) {
        "yt-dlp.exe"
    } else if cfg!(target_os = "macos") {
        "yt-dlp_macos"
    } else {
        "yt-dlp_linux"
    };
    format!("https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}")
}

/// spotDL incluye la versión en el nombre del archivo, así que no vale el enlace
/// fijo a «latest»: hay que preguntar cuál es la última.
async fn spotdl_url() -> Result<String> {
    let sufijo = if cfg!(windows) {
        "-win32.exe"
    } else if cfg!(target_os = "macos") {
        "-darwin"
    } else {
        "-linux"
    };

    let release: serde_json::Value = http()?
        .get("https://api.github.com/repos/spotDL/spotify-downloader/releases/latest")
        .send()
        .await?
        .error_for_status()?
        .json()
        .await?;

    release
        .get("assets")
        .and_then(|a| a.as_array())
        .and_then(|assets| {
            assets.iter().find_map(|a| {
                let nombre = a.get("name")?.as_str()?;
                nombre
                    .ends_with(sufijo)
                    .then(|| a.get("browser_download_url")?.as_str().map(str::to_string))?
            })
        })
        .ok_or_else(|| anyhow!("spotDL no publica un ejecutable para este sistema"))
}

fn ffmpeg_url() -> Result<(String, &'static str)> {
    if cfg!(windows) {
        Ok((
            "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip".into(),
            "zip",
        ))
    } else if cfg!(target_os = "macos") {
        // No hay una compilación estática oficial y estable para macOS.
        Err(anyhow!("En macOS instala ffmpeg con `brew install ffmpeg`"))
    } else {
        Ok((
            "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz".into(),
            "tar.xz",
        ))
    }
}

/// Busca un archivo por nombre bajo `dir`, hasta `profundidad` niveles.
fn find_file(dir: &Path, nombre: &str, profundidad: u32) -> Option<PathBuf> {
    if profundidad == 0 {
        return None;
    }
    let entradas = std::fs::read_dir(dir).ok()?;
    let mut subdirs = Vec::new();

    for entrada in entradas.filter_map(Result::ok) {
        let ruta = entrada.path();
        if ruta.is_dir() {
            subdirs.push(ruta);
        } else if ruta.file_name().map(|n| n == nombre).unwrap_or(false) {
            return Some(ruta);
        }
    }
    subdirs
        .into_iter()
        .find_map(|d| find_file(&d, nombre, profundidad - 1))
}

/// Carpetas donde buscar cuando el `PATH` no basta.
fn fallback_dirs() -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = Vec::new();

    if let Some(home) = dirs::home_dir() {
        dirs.push(home.join(".local").join("bin")); // pip install --user, pipx
        dirs.push(home.join("bin"));
        dirs.push(home.join(".local/share/flatpak/exports/bin"));
    }

    // Las herramientas de Python viven junto al intérprete. Si sabemos dónde
    // está python, sabemos dónde mirar, sin tener que adivinar la versión.
    for py in ["python3", "python"] {
        if let Ok(exe) = which::which(py) {
            if let Some(bin) = exe.parent() {
                dirs.push(bin.to_path_buf());
                dirs.push(bin.join("Scripts")); // Windows
                dirs.push(bin.join("bin")); // entornos virtuales en unix
            }
        }
    }

    #[cfg(unix)]
    {
        for d in [
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/snap/bin",
            "/var/lib/flatpak/exports/bin",
            "/opt/homebrew/bin", // macOS con Apple Silicon
        ] {
            dirs.push(PathBuf::from(d));
        }
    }

    dirs.retain(|d| d.is_dir());
    dirs.dedup();
    dirs
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

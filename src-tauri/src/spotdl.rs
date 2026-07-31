use crate::binaries::Binaries;
use crate::job::{Job, JobPhase, Progress};
use crate::proc::{async_command, LossyLines};
use crate::settings::Settings;
use anyhow::{anyhow, Result};
use std::path::{Path, PathBuf};
use std::process::Stdio;
use tokio::io::BufReader;
use tokio_util::sync::CancellationToken;

const AUDIO_EXTS: [&str; 6] = ["mp3", "flac", "ogg", "opus", "m4a", "wav"];

/// Borra su carpeta al salir del ámbito, pase lo que pase: error, cancelación o
/// éxito. Sin esto, una descarga fallida dejaría basura en la carpeta del
/// usuario.
struct WorkDir(PathBuf);

impl Drop for WorkDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// spotdl does not report byte-level progress, so we track its phases from the
/// log lines and let the UI show an indeterminate bar in between.
pub async fn download(
    job: &Job,
    settings: &Settings,
    bins: &Binaries,
    cancel: CancellationToken,
    mut on_progress: impl FnMut(Progress) + Send,
) -> Result<PathBuf> {
    let exe = bins.require("spotdl")?;
    let dest = PathBuf::from(&job.dest_dir);
    std::fs::create_dir_all(&dest)?;

    // spotdl no dice dónde escribió, así que hay que deducirlo mirando la
    // carpeta. Si varias descargas comparten carpeta —y con concurrencia 3 es
    // lo normal— cada una vería también los archivos de las otras y podría
    // quedarse con el que no es, dejando en la biblioteca un título apuntando a
    // otra canción. Con una carpeta por descarga el archivo es inequívoco.
    let work = dest.join(format!(".recodio-{}", &job.id[..8]));
    let _cleanup = WorkDir(work.clone());
    std::fs::create_dir_all(&work)?;

    let format = if settings.audio_format == "wav" {
        "wav"
    } else if matches!(settings.audio_format.as_str(), "flac" | "opus" | "m4a" | "ogg") {
        settings.audio_format.as_str()
    } else {
        "mp3"
    };

    let mut cmd = async_command(exe);
    cmd.arg("download")
        .arg(if job.entry.url.is_empty() {
            &job.entry.source_id
        } else {
            &job.entry.url
        })
        .arg("--output")
        .arg(work.join("{artist} - {title}.{output-ext}"))
        .arg("--format")
        .arg(format)
        // La carpeta de trabajo está vacía, así que aquí nunca hay nada que
        // sobrescribir: el duplicado se decide al mover el archivo a su sitio.
        .arg("--overwrite")
        .arg("force")
        .arg("--simple-tui")
        .arg("--print-errors")
        .arg("--log-level")
        .arg("INFO");

    if matches!(format, "mp3" | "ogg" | "opus") {
        cmd.arg("--bitrate").arg(format!("{}k", settings.audio_bitrate));
    }
    if settings.sponsorblock {
        cmd.arg("--sponsor-block");
    }
    if let Some(cookies) = settings.cookies_file.as_ref().filter(|p| p.exists()) {
        cmd.arg("--cookie-file").arg(cookies);
    }
    if let Some(ffmpeg) = bins.resolve("ffmpeg") {
        cmd.arg("--ffmpeg").arg(ffmpeg);
    }

    cmd.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = cmd.spawn()?;

    let mut out_lines = LossyLines::new(BufReader::new(child.stdout.take().unwrap()));
    let mut err_lines = LossyLines::new(BufReader::new(child.stderr.take().unwrap()));
    let mut log: Vec<String> = Vec::new();

    on_progress(Progress {
        phase: Some(JobPhase::Downloading),
        progress: Some(-1.0),
        message: Some("Buscando la mejor coincidencia…".into()),
        ..Default::default()
    });

    let status = loop {
        tokio::select! {
            _ = cancel.cancelled() => {
                let _ = child.kill().await;
                return Err(anyhow!("Cancelado"));
            }
            line = out_lines.next_line() => {
                match line? {
                    Some(l) => handle_line(&l, &mut on_progress, &mut log),
                    None => break child.wait().await?,
                }
            }
            line = err_lines.next_line() => {
                if let Some(l) = line? {
                    handle_line(&l, &mut on_progress, &mut log);
                }
            }
        }
    };

    while let Ok(Some(l)) = err_lines.next_line().await {
        handle_line(&l, &mut on_progress, &mut log);
    }

    // Sólo puede haber un audio aquí: la carpeta es de esta descarga y de nadie más.
    if let Some(produced) = audio_files(&work)
        .into_iter()
        .max_by_key(|p| p.metadata().map(|m| m.len()).unwrap_or(0))
    {
        return move_into_place(&produced, &dest, job.overwrite);
    }

    if !status.success() {
        let msg = log
            .iter()
            .rev()
            .find(|l| l.to_lowercase().contains("error"))
            .cloned()
            .unwrap_or_else(|| format!("spotdl terminó con código {:?}", status.code()));
        return Err(anyhow!(msg));
    }
    Err(anyhow!(
        "spotdl no generó ningún archivo nuevo (puede que la pista ya existiera con otro nombre)"
    ))
}

fn handle_line(line: &str, on_progress: &mut impl FnMut(Progress), log: &mut Vec<String>) {
    let line = line.trim();
    if line.is_empty() {
        return;
    }
    log.push(line.to_string());
    if log.len() > 40 {
        log.remove(0);
    }

    let lower = line.to_ascii_lowercase();
    let message = if lower.starts_with("downloading") || lower.contains("download started") {
        Some(("Descargando audio…", JobPhase::Downloading))
    } else if lower.contains("converting") || lower.contains("embedding") {
        Some(("Convirtiendo y etiquetando…", JobPhase::Processing))
    } else if lower.starts_with("downloaded") {
        Some(("Listo", JobPhase::Processing))
    } else {
        None
    };

    if let Some((msg, phase)) = message {
        on_progress(Progress {
            phase: Some(phase),
            message: Some(msg.into()),
            ..Default::default()
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(nombre: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("recodio-test-{nombre}-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn mueve_el_archivo_a_la_carpeta_final() {
        let raiz = temp("mover");
        let (work, dest) = (raiz.join("work"), raiz.join("dest"));
        std::fs::create_dir_all(&work).unwrap();
        std::fs::create_dir_all(&dest).unwrap();

        let src = work.join("Marshmello - Alone.mp3");
        std::fs::write(&src, b"audio").unwrap();

        let final_path = move_into_place(&src, &dest, false).unwrap();
        assert_eq!(final_path, dest.join("Marshmello - Alone.mp3"));
        assert!(final_path.exists());
        assert!(!src.exists(), "no debe quedar copia en la carpeta de trabajo");

        std::fs::remove_dir_all(&raiz).ok();
    }

    #[test]
    fn omitir_conserva_el_archivo_que_ya_estaba() {
        let raiz = temp("omitir");
        let (work, dest) = (raiz.join("work"), raiz.join("dest"));
        std::fs::create_dir_all(&work).unwrap();
        std::fs::create_dir_all(&dest).unwrap();

        let previo = dest.join("Cancion.mp3");
        std::fs::write(&previo, b"el que ya estaba").unwrap();
        let src = work.join("Cancion.mp3");
        std::fs::write(&src, b"el nuevo").unwrap();

        let final_path = move_into_place(&src, &dest, false).unwrap();
        assert_eq!(final_path, previo);
        assert_eq!(std::fs::read(&previo).unwrap(), b"el que ya estaba");
        // Y no aparece ningún "Cancion (2).mp3": omitir significa omitir.
        assert_eq!(audio_files(&dest).len(), 1);

        std::fs::remove_dir_all(&raiz).ok();
    }

    #[test]
    fn sobrescribir_reemplaza_el_archivo() {
        let raiz = temp("sobrescribir");
        let (work, dest) = (raiz.join("work"), raiz.join("dest"));
        std::fs::create_dir_all(&work).unwrap();
        std::fs::create_dir_all(&dest).unwrap();

        std::fs::write(dest.join("Cancion.mp3"), b"viejo").unwrap();
        let src = work.join("Cancion.mp3");
        std::fs::write(&src, b"nuevo").unwrap();

        let final_path = move_into_place(&src, &dest, true).unwrap();
        assert_eq!(std::fs::read(&final_path).unwrap(), b"nuevo");
        assert_eq!(audio_files(&dest).len(), 1);

        std::fs::remove_dir_all(&raiz).ok();
    }

    /// El fallo que motivó todo esto: con varias descargas a la vez, cada una
    /// escribía en la carpeta común y podía llevarse el archivo de otra. Con una
    /// carpeta por descarga, cada una sólo ve lo suyo.
    #[test]
    fn descargas_simultaneas_no_se_roban_los_archivos() {
        let dest = temp("concurrencia");

        let work_a = dest.join(".recodio-aaaaaaaa");
        let work_b = dest.join(".recodio-bbbbbbbb");
        std::fs::create_dir_all(&work_a).unwrap();
        std::fs::create_dir_all(&work_b).unwrap();

        // B produce un archivo mucho más grande: con el método anterior, A se
        // habría quedado con él por ser "el nuevo más grande".
        std::fs::write(work_a.join("Linkin Park - In the End.mp3"), vec![0u8; 100]).unwrap();
        std::fs::write(work_b.join("Marshmello - Alone.mp3"), vec![0u8; 900_000]).unwrap();

        let de_a = audio_files(&work_a);
        assert_eq!(de_a.len(), 1);
        assert!(de_a[0].file_name().unwrap().to_string_lossy().contains("In the End"));

        std::fs::remove_dir_all(&dest).ok();
    }

    #[test]
    fn la_carpeta_de_trabajo_se_borra_sola() {
        let raiz = temp("limpieza");
        let work = raiz.join(".recodio-12345678");
        {
            std::fs::create_dir_all(&work).unwrap();
            std::fs::write(work.join("a medias.mp3"), b"x").unwrap();
            let _cleanup = WorkDir(work.clone());
            assert!(work.exists());
        }
        assert!(!work.exists(), "debe desaparecer aunque la descarga falle");

        std::fs::remove_dir_all(&raiz).ok();
    }
}

fn audio_files(dir: &Path) -> Vec<PathBuf> {
    std::fs::read_dir(dir)
        .map(|rd| {
            rd.filter_map(Result::ok)
                .map(|e| e.path())
                .filter(|p| {
                    p.extension()
                        .and_then(|e| e.to_str())
                        .map(|e| AUDIO_EXTS.contains(&e.to_ascii_lowercase().as_str()))
                        .unwrap_or(false)
                })
                .collect()
        })
        .unwrap_or_default()
}

/// Saca el archivo de la carpeta de trabajo a la carpeta del usuario.
///
/// Si ya hay uno con ese nombre y no se pidió sobrescribir, se conserva el que
/// estaba y se devuelve: el usuario eligió "omitir", y crear un `Canción (2).mp3`
/// sería justo lo contrario de lo que pidió.
fn move_into_place(src: &Path, dest_dir: &Path, overwrite: bool) -> Result<PathBuf> {
    let name = src
        .file_name()
        .ok_or_else(|| anyhow!("el archivo descargado no tiene nombre"))?;
    let target = dest_dir.join(name);

    if target.exists() {
        if !overwrite {
            return Ok(target);
        }
        std::fs::remove_file(&target)?;
    }

    // `rename` es instantáneo dentro del mismo volumen, que es el caso normal
    // porque la carpeta de trabajo cuelga del propio destino. La copia es el
    // respaldo por si el destino resulta ser otro sistema de archivos.
    if std::fs::rename(src, &target).is_err() {
        std::fs::copy(src, &target)?;
        let _ = std::fs::remove_file(src);
    }
    Ok(target)
}

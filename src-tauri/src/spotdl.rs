use crate::binaries::Binaries;
use crate::job::{Job, JobPhase, Progress};
use crate::proc::async_command;
use crate::settings::Settings;
use anyhow::{anyhow, Result};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio_util::sync::CancellationToken;

const AUDIO_EXTS: [&str; 6] = ["mp3", "flac", "ogg", "opus", "m4a", "wav"];

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

    let before = snapshot(&dest);

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
        .arg(dest.join("{artist} - {title}.{output-ext}"))
        .arg("--format")
        .arg(format)
        .arg("--overwrite")
        .arg(if job.overwrite { "force" } else { "skip" })
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

    let mut out_lines = BufReader::new(child.stdout.take().unwrap()).lines();
    let mut err_lines = BufReader::new(child.stderr.take().unwrap()).lines();
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

    // spotdl never prints the path it wrote, so diff the destination folder.
    let after = snapshot(&dest);
    let new_file = after
        .difference(&before)
        .max_by_key(|p| p.metadata().map(|m| m.len()).unwrap_or(0))
        .cloned();

    if let Some(p) = new_file {
        return Ok(p);
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

fn snapshot(dir: &Path) -> HashSet<PathBuf> {
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

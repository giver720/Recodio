use crate::binaries::Binaries;
use crate::job::{Job, JobPhase, Progress};
use crate::proc::{async_command, LossyLines};
use crate::settings::Settings;
use anyhow::{anyhow, Result};
use std::path::PathBuf;
use std::process::Stdio;
use tokio::io::BufReader;
use tokio_util::sync::CancellationToken;

/// Marker prefixes so our machine-readable lines never collide with yt-dlp's
/// own human-readable output.
const DL_TAG: &str = "RCDP|";
const PP_TAG: &str = "RCDPP|";

/// Cookies / proxy / rate limiting — the flags that decide whether restricted
/// material is reachable at all. Shared with the analyze step so a link that
/// previews correctly also downloads correctly.
pub fn apply_access_args(cmd: &mut tokio::process::Command, s: &Settings) {
    if let Some(browser) = s.cookies_from_browser.as_ref().filter(|b| !b.is_empty()) {
        cmd.arg("--cookies-from-browser").arg(browser);
    }
    if let Some(file) = s.cookies_file.as_ref().filter(|p| p.exists()) {
        cmd.arg("--cookies").arg(file);
    }
    if let Some(proxy) = s.proxy.as_ref().filter(|p| !p.is_empty()) {
        cmd.arg("--proxy").arg(proxy);
    }
    if let Some(limit) = s.rate_limit.as_ref().filter(|r| !r.is_empty()) {
        cmd.arg("--limit-rate").arg(limit);
    }
    // Geo-restricted material: try harder before giving up.
    cmd.arg("--geo-bypass");
}

fn apply_format_args(cmd: &mut tokio::process::Command, s: &Settings, kind: &str) {
    if kind == "audio" {
        cmd.arg("-f").arg("ba/b");
        cmd.arg("-x").arg("--audio-format").arg(&s.audio_format);
        if matches!(s.audio_format.as_str(), "mp3" | "m4a" | "opus" | "vorbis") {
            cmd.arg("--audio-quality").arg(format!("{}K", s.audio_bitrate));
        }
        return;
    }

    let fmt = if s.video_quality == "best" {
        "bv*+ba/b".to_string()
    } else {
        let h = &s.video_quality;
        format!("bv*[height<=?{h}]+ba/b[height<=?{h}]/b")
    };
    cmd.arg("-f").arg(fmt);

    if s.video_container != "original" {
        cmd.arg("--merge-output-format").arg(&s.video_container);
    }
}

fn apply_extras_args(cmd: &mut tokio::process::Command, s: &Settings, kind: &str) {
    if s.embed_metadata {
        cmd.arg("--embed-metadata");
    }
    if s.embed_thumbnail {
        cmd.arg("--embed-thumbnail");
    }
    if s.embed_chapters && kind == "video" {
        cmd.arg("--embed-chapters");
    }
    if s.write_subtitles && kind == "video" {
        cmd.arg("--write-subs")
            .arg("--write-auto-subs")
            .arg("--sub-langs")
            .arg(&s.subtitle_langs)
            // WebVTT es el único formato que el reproductor sabe cargar.
            .arg("--sub-format")
            .arg("vtt/best")
            .arg("--convert-subs")
            .arg("vtt");
        if s.embed_subtitles {
            // Solo `--embed-subs`. Aquí hubo un `--keep-subs` que **no existe**
            // en yt-dlp: hacía que rechazara la llamada entera con un error de
            // uso, así que ningún vídeo se descargaba con los subtítulos
            // activados. Y era innecesario: yt-dlp solo borra los archivos
            // sueltos tras incrustarlos cuando no se le han pedido, y aquí
            // siempre se le piden con `--write-subs`. Hacen falta los dos,
            // porque los incrustados en un MP4 el reproductor no sabe leerlos.
            cmd.arg("--embed-subs");
        }
    }

    if s.sponsorblock {
        if !s.sponsorblock_remove.is_empty() {
            cmd.arg("--sponsorblock-remove")
                .arg(s.sponsorblock_remove.join(","));
        }
        if !s.sponsorblock_mark.is_empty() {
            cmd.arg("--sponsorblock-mark")
                .arg(s.sponsorblock_mark.join(","));
        }
    }
}

/// Run one yt-dlp download. `on_progress` is called for every parsed progress
/// line; the returned path is the final file after all post-processing.
pub async fn download(
    job: &Job,
    settings: &Settings,
    bins: &Binaries,
    archive_path: Option<PathBuf>,
    cancel: CancellationToken,
    mut on_progress: impl FnMut(Progress) + Send,
) -> Result<PathBuf> {
    let exe = bins.require("yt-dlp")?;
    let path_file = std::env::temp_dir().join(format!("recodio-path-{}.txt", job.id));

    // Spotify no distribuye audio, así que ningún descargador baja "de Spotify":
    // todos localizan la canción en YouTube. spotDL lo hacía por su cuenta, pero
    // su resolución de enlaces está rota en la 4.5.2 y falla con `KeyError:
    // 'uri'`. Como ya tenemos título, artista y duración, se busca con yt-dlp y
    // se descarta lo que no cuadre en duración con lo que dice Spotify.
    let es_spotify = job.entry.extractor == "spotify";

    let mut cmd = async_command(exe);
    cmd.arg("--ignore-config").arg("--newline");

    if !es_spotify {
        // En una búsqueda esto dejaría solo el primer resultado, saltándose el
        // filtro de duración.
        cmd.arg("--no-playlist");
    }
    cmd
        // `--progress` guarantees progress lines even when other flags imply quiet.
        .arg("--progress")
        .arg("--no-simulate")
        .arg("--progress-template")
        .arg(format!(
            "download:{DL_TAG}%(progress.status)s|%(progress.downloaded_bytes)s|\
             %(progress.total_bytes)s|%(progress.total_bytes_estimate)s|\
             %(progress.speed)s|%(progress.eta)s"
        ))
        .arg("--progress-template")
        .arg(format!(
            "postprocess:{PP_TAG}%(progress.status)s|%(progress.postprocessor)s"
        ))
        .arg("--print-to-file")
        .arg("after_move:filepath")
        .arg(&path_file)
        .arg("--paths")
        .arg(&job.dest_dir)
        .arg("-o")
        .arg(if es_spotify {
            // El vídeo de YouTube se llamará «El Gran Varon [US0GbUpQ9VU]»; en la
            // biblioteca queremos «Willie Colón - El Gran Varón», que es lo que
            // el usuario buscó. El `%` se escapa para que yt-dlp no lo lea como
            // una plantilla.
            let base = crate::m3u::sanitize(&job.entry.title).replace('%', "%%");
            match job.playlist_id {
                // Dentro de una playlist, el número de orden delante. Hace dos
                // cosas: los archivos quedan ordenados en la carpeta como en la
                // lista original, y dos canciones que se llamen igual dejan de
                // poder pisarse, que es como una acababa apuntando a la otra.
                Some(_) => format!("{:03} - {}.%(ext)s", job.entry.index, base),
                None => format!("{base}.%(ext)s"),
            }
        } else {
            settings.output_template.clone()
        })
        .arg("--retries")
        .arg(settings.retries.to_string())
        .arg("--fragment-retries")
        .arg(settings.retries.to_string())
        .arg("--concurrent-fragments")
        .arg("4");

    if job.overwrite {
        cmd.arg("--force-overwrites");
    } else {
        cmd.arg("--no-overwrites");
    }

    if let Some(archive) = archive_path {
        cmd.arg("--download-archive").arg(archive);
    }

    apply_access_args(&mut cmd, settings);
    apply_format_args(&mut cmd, settings, &job.kind);
    apply_extras_args(&mut cmd, settings, &job.kind);

    let target = if es_spotify {
        if let Some(segundos) = job.entry.duration.filter(|d| *d > 0.0) {
            // ±20 s absorbe las diferencias entre la edición de Spotify y la de
            // YouTube (intros, silencios finales) sin colar una versión en
            // directo o un remix, que suelen diferir bastante más.
            let minimo = (segundos - 20.0).max(0.0).round() as i64;
            let maximo = (segundos + 20.0).round() as i64;
            cmd.arg("--match-filter")
                .arg(format!("duration>={minimo} & duration<={maximo}"));
        }
        // Sin esto se descargarían todos los resultados que pasen el filtro.
        cmd.arg("--max-downloads").arg("1");
        format!("ytsearch10:{}", search_query(&job.entry.title))
    } else if job.entry.url.is_empty() {
        job.entry.source_id.clone()
    } else {
        job.entry.url.clone()
    };
    cmd.arg(&target);

    cmd.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = cmd.spawn()?;

    let stdout = child.stdout.take().unwrap();
    let stderr = child.stderr.take().unwrap();
    let mut out_lines = LossyLines::new(BufReader::new(stdout));
    let mut err_lines = LossyLines::new(BufReader::new(stderr));
    let mut error_tail: Vec<String> = Vec::new();

    let status = loop {
        tokio::select! {
            _ = cancel.cancelled() => {
                let _ = child.kill().await;
                let _ = std::fs::remove_file(&path_file);
                return Err(anyhow!("Cancelado"));
            }
            line = out_lines.next_line() => {
                match line? {
                    Some(l) => handle_line(&l, &mut on_progress, &mut error_tail),
                    None => break child.wait().await?,
                }
            }
            line = err_lines.next_line() => {
                if let Some(l) = line? {
                    handle_line(&l, &mut on_progress, &mut error_tail);
                }
            }
        }
    };

    // Drain whatever is still buffered on stderr after the process exits.
    while let Ok(Some(l)) = err_lines.next_line().await {
        handle_line(&l, &mut on_progress, &mut error_tail);
    }

    let printed = std::fs::read_to_string(&path_file).unwrap_or_default();
    let _ = std::fs::remove_file(&path_file);
    let final_path = printed
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .next_back()
        .map(PathBuf::from);

    // Un código distinto de cero con archivo en disco suele ser un aviso
    // cosmético del post-procesado. Y con `--max-downloads` yt-dlp siempre
    // termina en 101, aunque haya ido bien.
    if let Some(p) = final_path.as_ref().filter(|p| p.exists()) {
        return Ok(p.clone());
    }

    if !status.success() || es_spotify {
        let msg = crate::analyze::clean_ytdlp_error(&error_tail.join("\n"));
        if !msg.is_empty() {
            return Err(anyhow!(msg));
        }
        if es_spotify {
            return Err(anyhow!(
                "No se encontró en YouTube ninguna versión de «{}» que cuadre en duración \
                 con la de Spotify. Puede ser una pista muy rara, o estar solo en Spotify.",
                job.entry.title
            ));
        }
        return Err(anyhow!(explicar_salida(status.code())));
    }

    final_path
        .filter(|p| p.exists())
        .ok_or_else(|| anyhow!("La descarga terminó pero no se encontró el archivo resultante"))
}

fn handle_line(line: &str, on_progress: &mut impl FnMut(Progress), error_tail: &mut Vec<String>) {
    let line = line.trim();
    if line.is_empty() {
        return;
    }

    if let Some(rest) = line.strip_prefix(DL_TAG) {
        let f: Vec<&str> = rest.split('|').collect();
        let num = |i: usize| -> Option<f64> {
            f.get(i)
                .map(|s| s.trim())
                .filter(|s| !s.is_empty() && *s != "NA" && *s != "None")
                .and_then(|s| s.parse::<f64>().ok())
        };
        let downloaded = num(1).unwrap_or(0.0);
        let total = num(2).or_else(|| num(3)).unwrap_or(0.0);
        let status = f.first().copied().unwrap_or("downloading");

        on_progress(Progress {
            phase: Some(JobPhase::Downloading),
            progress: Some(if total > 0.0 {
                (downloaded / total).clamp(0.0, 1.0)
            } else {
                -1.0
            }),
            downloaded_bytes: Some(downloaded as u64),
            total_bytes: Some(total as u64),
            speed: Some(num(4).unwrap_or(0.0)),
            eta: num(5).map(|e| e as u64),
            message: (status == "finished").then(|| "Descarga completa".to_string()),
        });
        return;
    }

    if let Some(rest) = line.strip_prefix(PP_TAG) {
        let mut f = rest.split('|');
        let _status = f.next().unwrap_or("");
        let pp = f.next().unwrap_or("").trim();
        on_progress(Progress {
            phase: Some(JobPhase::Processing),
            message: Some(friendly_postprocessor(pp)),
            ..Default::default()
        });
        return;
    }

    // Los errores de uso no llevan el «ERROR:» del resto: los imprime argparse
    // en minúsculas y con el nombre del ejecutable delante, «yt-dlp.exe: error:
    // no such option: --loquesea». Sin recogerlos, una llamada mal formada se
    // quedaba sin explicación y lo único que llegaba a la interfaz era el
    // código de salida pelado, que no dice nada.
    if line.contains("ERROR:") || line.starts_with("WARNING:") || line.contains(": error:") {
        error_tail.push(line.to_string());
        if error_tail.len() > 20 {
            error_tail.remove(0);
        }
    }
}

/// Traduce el código de salida de yt-dlp a algo con lo que hacer algo.
///
/// Es el último recurso, solo para cuando yt-dlp no ha dejado ni una línea
/// aprovechable. Aun así, un número suelto no ayuda a nadie.
fn explicar_salida(codigo: Option<i32>) -> String {
    match codigo {
        Some(2) => "yt-dlp rechazó las opciones con las que se le llamó. Suele ser un \
                    ajuste con un valor que no admite: revisa en Ajustes el límite de \
                    velocidad, el navegador del que se toman las cookies y el formato \
                    de audio."
            .to_string(),
        Some(100) => "yt-dlp necesita actualizarse para poder continuar. Tienes el botón \
                      en Ajustes › Herramientas."
            .to_string(),
        Some(c) => format!("yt-dlp terminó con el código {c} sin explicar el motivo."),
        None => "yt-dlp se cerró de golpe, sin llegar a devolver un código. Puede haberlo \
                 parado el sistema o un antivirus."
            .to_string(),
    }
}

/// Limpia el título antes de buscarlo. Las coletillas entre paréntesis o
/// corchetes —«(Remastered 2019)», «[Official Video]»— estrechan la búsqueda
/// hacia vídeos concretos y hacen perder la grabación que se quiere.
fn search_query(titulo: &str) -> String {
    let mut limpio = String::with_capacity(titulo.len());
    let mut profundidad = 0i32;

    for c in titulo.chars() {
        match c {
            '(' | '[' => profundidad += 1,
            ')' | ']' => profundidad = (profundidad - 1).max(0),
            _ if profundidad == 0 => limpio.push(c),
            _ => {}
        }
    }

    let limpio = limpio.trim();
    if limpio.is_empty() {
        titulo.to_string()
    } else {
        limpio.split_whitespace().collect::<Vec<_>>().join(" ")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn la_busqueda_quita_las_coletillas_del_titulo() {
        // Buscar «(Remastered 2019)» o «[Official Video]» estrecha la búsqueda
        // hacia un vídeo concreto y hace perder la grabación buscada.
        let casos = [
            ("Willie Colón - El Gran Varón", "Willie Colón - El Gran Varón"),
            (
                "Willie Colón - Idilio (Remastered 2019)",
                "Willie Colón - Idilio",
            ),
            (
                "Marshmello - Alone [Official Music Video]",
                "Marshmello - Alone",
            ),
            ("Artista - Tema (feat. Otro) [Live]", "Artista - Tema"),
        ];
        for (entrada, esperado) in casos {
            assert_eq!(search_query(entrada), esperado, "con: {entrada}");
        }
    }

    /// Un título que sea *solo* un paréntesis quedaría vacío, y buscar la cadena
    /// vacía devolvería cualquier cosa.
    #[test]
    fn nunca_devuelve_una_busqueda_vacia() {
        assert_eq!(search_query("(Instrumental)"), "(Instrumental)");
        assert_eq!(search_query(""), "");
    }

    /// Lo que destapó todo esto: una bandera inexistente hacía fallar la
    /// descarga y el usuario solo veía «terminó con código Some(2)», porque el
    /// motivo real no se recogía. Los errores de uso los imprime argparse en
    /// minúsculas, sin el «ERROR:» que llevan los demás.
    #[test]
    fn recoge_los_errores_de_uso_de_yt_dlp() {
        let mut recogido = Vec::new();
        let mut sin_progreso = |_: Progress| {};
        for linea in [
            "Usage: yt-dlp.exe [OPTIONS] URL [URL...]",
            "yt-dlp.exe: error: no such option: --keep-subs",
        ] {
            handle_line(linea, &mut sin_progreso, &mut recogido);
        }

        assert_eq!(recogido.len(), 1, "la línea de uso es ruido: {recogido:?}");
        assert!(recogido[0].contains("no such option"));
        assert_eq!(
            crate::analyze::clean_ytdlp_error(&recogido.join("\n")),
            "no such option: --keep-subs",
            "al usuario le sobra el nombre del ejecutable"
        );
    }

    #[test]
    fn sigue_recogiendo_los_errores_normales() {
        let mut recogido = Vec::new();
        let mut sin_progreso = |_: Progress| {};
        handle_line("[download] 50%", &mut sin_progreso, &mut recogido);
        handle_line("ERROR: Video unavailable", &mut sin_progreso, &mut recogido);
        handle_line("WARNING: algo menor", &mut sin_progreso, &mut recogido);

        assert_eq!(recogido.len(), 2);
        assert_eq!(
            crate::analyze::clean_ytdlp_error(&recogido.join("\n")),
            "Video unavailable"
        );
    }

    /// Un número suelto no es un mensaje de error. El 2 es el caso real que
    /// motivó esto, y el 100 el que se arregla actualizando.
    #[test]
    fn el_codigo_de_salida_se_explica_en_castellano() {
        assert!(explicar_salida(Some(2)).contains("rechazó las opciones"));
        assert!(explicar_salida(Some(100)).contains("actualizarse"));
        assert!(explicar_salida(Some(7)).contains("código 7"));
        assert!(explicar_salida(None).contains("de golpe"));
        // Y en ninguno se escapa el Option de Rust.
        for c in [Some(2), Some(100), Some(7), None] {
            assert!(!explicar_salida(c).contains("Some("), "se filtró el Option con {c:?}");
        }
    }
}

fn friendly_postprocessor(pp: &str) -> String {
    match pp {
        "Merger" => "Uniendo vídeo y audio…".into(),
        "ExtractAudio" => "Extrayendo audio…".into(),
        "ModifyChapters" | "SponsorBlock" => "Recortando segmentos de SponsorBlock…".into(),
        "EmbedThumbnail" => "Incrustando miniatura…".into(),
        "FFmpegMetadata" => "Escribiendo metadatos…".into(),
        "FFmpegVideoConvertor" | "VideoConvertor" => "Convirtiendo formato…".into(),
        "EmbedSubtitle" => "Incrustando subtítulos…".into(),
        "MoveFiles" => "Moviendo a destino…".into(),
        "" => "Procesando…".into(),
        other => format!("Procesando ({other})…"),
    }
}

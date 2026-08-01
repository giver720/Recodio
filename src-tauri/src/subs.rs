//! Subtítulos que acompañan a un vídeo.
//!
//! Los subtítulos incrustados dentro de un MP4 no son accesibles desde el
//! reproductor —el motor del webview no los expone—, pero los que yt-dlp deja
//! como archivos sueltos junto al vídeo sí se pueden cargar. Este módulo los
//! encuentra y, si hace falta, los convierte al único formato que el reproductor
//! entiende.

use crate::binaries::Binaries;
use serde::Serialize;
use std::path::{Path, PathBuf};

/// Formatos que se pueden aprovechar. WebVTT se carga tal cual; el resto hay que
/// convertirlo.
const EXTENSIONES: [&str; 4] = ["vtt", "srt", "ass", "ssa"];

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SubtitleTrack {
    /// Código del idioma tal y como venía en el nombre del archivo.
    pub lang: String,
    /// Nombre para mostrar.
    pub label: String,
    /// Ruta del `.vtt` listo para cargar.
    pub path: String,
}

/// Nombres de idioma más habituales, para no enseñar códigos sueltos.
fn nombre_idioma(codigo: &str) -> String {
    let base = codigo.split('-').next().unwrap_or(codigo).to_lowercase();
    let nombre = match base.as_str() {
        "es" | "spa" => "Español",
        "en" | "eng" => "Inglés",
        "pt" | "por" => "Portugués",
        "fr" | "fra" | "fre" => "Francés",
        "de" | "deu" | "ger" => "Alemán",
        "it" | "ita" => "Italiano",
        "ja" | "jpn" => "Japonés",
        "ko" | "kor" => "Coreano",
        "zh" | "chi" | "zho" => "Chino",
        "ru" | "rus" => "Ruso",
        "ar" | "ara" => "Árabe",
        _ => return codigo.to_string(),
    };
    // Se conserva la variante cuando la hay: «Español (es-419)» dice más que
    // «Español» a secas si hay dos pistas de español.
    if codigo.contains('-') {
        format!("{nombre} ({codigo})")
    } else {
        nombre.to_string()
    }
}

/// Convierte a WebVTT, que es lo único que carga el reproductor.
fn a_vtt(origen: &Path, destino: &Path, bins: &Binaries) -> bool {
    let Some(ffmpeg) = bins.resolve("ffmpeg") else {
        return false;
    };
    crate::proc::command(ffmpeg)
        .args(["-v", "error", "-i"])
        .arg(origen)
        .arg(destino)
        .arg("-y")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
        && destino.is_file()
}

/// Busca los subtítulos que acompañan a un vídeo.
///
/// yt-dlp los nombra `vídeo.es.vtt`, `vídeo.en.srt` y demás, así que el idioma
/// sale del propio nombre.
pub fn find_for(video: &str, bins: &Binaries, cache_dir: &Path) -> Vec<SubtitleTrack> {
    let ruta = Path::new(video);
    let (Some(carpeta), Some(base)) = (ruta.parent(), ruta.file_stem()) else {
        return Vec::new();
    };
    let base = base.to_string_lossy().to_lowercase();

    let Ok(entradas) = std::fs::read_dir(carpeta) else {
        return Vec::new();
    };

    let mut encontrados = Vec::new();
    for entrada in entradas.filter_map(std::result::Result::ok) {
        let p = entrada.path();
        let Some(ext) = p.extension().and_then(|e| e.to_str()) else {
            continue;
        };
        let ext = ext.to_lowercase();
        if !EXTENSIONES.contains(&ext.as_str()) {
            continue;
        }
        let Some(nombre) = p.file_stem().map(|n| n.to_string_lossy().to_lowercase()) else {
            continue;
        };
        // `vídeo.es` pertenece a `vídeo`; `otro.es` no.
        if !nombre.starts_with(&base) {
            continue;
        }

        let sufijo = nombre[base.len()..].trim_start_matches('.').to_string();
        let lang = if sufijo.is_empty() { "und".into() } else { sufijo };

        let listo = if ext == "vtt" {
            Some(p.clone())
        } else {
            let _ = std::fs::create_dir_all(cache_dir);
            let destino = cache_dir.join(format!(
                "{}.{lang}.vtt",
                base.chars().filter(|c| c.is_alphanumeric()).take(40).collect::<String>()
            ));
            if destino.is_file() || a_vtt(&p, &destino, bins) {
                Some(destino)
            } else {
                None
            }
        };

        if let Some(final_path) = listo {
            encontrados.push(SubtitleTrack {
                label: nombre_idioma(&lang),
                lang,
                path: final_path.to_string_lossy().into_owned(),
            });
        }
    }

    encontrados.sort_by(|a, b| a.label.cmp(&b.label));
    encontrados.dedup_by(|a, b| a.lang == b.lang);
    encontrados
}

pub fn cache_dir_de(data_dir: &Path) -> PathBuf {
    data_dir.join("subs")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn traduce_los_codigos_de_idioma() {
        assert_eq!(nombre_idioma("es"), "Español");
        assert_eq!(nombre_idioma("eng"), "Inglés");
        assert_eq!(nombre_idioma("es-419"), "Español (es-419)");
        // Un código que no conocemos se muestra tal cual, mejor que inventarlo.
        assert_eq!(nombre_idioma("xyz"), "xyz");
    }

    #[test]
    fn solo_recoge_los_subtitulos_de_ese_video() {
        let dir = std::env::temp_dir().join(format!("recodio-subs-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let video = dir.join("mi video.mp4");
        std::fs::write(&video, b"x").unwrap();
        std::fs::write(dir.join("mi video.es.vtt"), b"WEBVTT").unwrap();
        std::fs::write(dir.join("mi video.en.vtt"), b"WEBVTT").unwrap();
        // De otro vídeo: no debe colarse.
        std::fs::write(dir.join("otro video.es.vtt"), b"WEBVTT").unwrap();
        // Ni un archivo que no es subtítulo.
        std::fs::write(dir.join("mi video.es.txt"), b"x").unwrap();

        let bins = Binaries::new(dir.join("bin"));
        let pistas = find_for(&video.to_string_lossy(), &bins, &dir.join("cache"));

        assert_eq!(pistas.len(), 2, "encontradas: {pistas:?}");
        let etiquetas: Vec<_> = pistas.iter().map(|p| p.label.as_str()).collect();
        assert!(etiquetas.contains(&"Español"));
        assert!(etiquetas.contains(&"Inglés"));

        std::fs::remove_dir_all(&dir).ok();
    }
}

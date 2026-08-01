//! Miniaturas para los archivos que no traen una de la web.
//!
//! Lo descargado guarda la miniatura del sitio de origen, pero un archivo del
//! propio equipo no tiene ninguna: se saca de él. Las canciones suelen llevar la
//! carátula incrustada; los vídeos no, así que se usa un fotograma.

use crate::binaries::Binaries;
use std::path::{Path, PathBuf};

/// Ancho de la miniatura. Suficiente para la lista y para la vista ampliada del
/// reproductor, sin llenar el disco: a tamaño original una carátula ocupa más de
/// cien kilobytes y así se queda en menos de treinta.
const ANCHO: u32 = 320;

/// Nombre estable para la miniatura de un archivo.
///
/// FNV-1a en lugar de un hash criptográfico: aquí solo hace falta un nombre
/// único y, sobre todo, que no cambie nunca. Si el nombre cambiara entre
/// versiones, cada actualización invalidaría todas las miniaturas y habría que
/// regenerarlas.
fn nombre_para(file_path: &str) -> String {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for b in file_path.to_lowercase().as_bytes() {
        h ^= *b as u64;
        h = h.wrapping_mul(0x1000_0000_01b3);
    }
    format!("{h:016x}.jpg")
}

fn ffmpeg_de(bins: &Binaries) -> Option<PathBuf> {
    bins.resolve("ffmpeg")
}

fn ffprobe_de(bins: &Binaries) -> Option<PathBuf> {
    bins.resolve("ffprobe").or_else(|| {
        bins.resolve("ffmpeg").map(|f| {
            f.with_file_name(if cfg!(windows) { "ffprobe.exe" } else { "ffprobe" })
        })
    })
    .filter(|p| p.is_file())
}

/// Saca la carátula incrustada, si la hay.
fn caratula(origen: &Path, destino: &Path, ffmpeg: &Path) -> bool {
    crate::proc::command(ffmpeg)
        .args(["-v", "error", "-i"])
        .arg(origen)
        .args(["-an", "-vf", &format!("scale={ANCHO}:-2"), "-q:v", "6"])
        .arg(destino)
        .arg("-y")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
        && destino.is_file()
}

/// Saca un fotograma representativo.
///
/// La posición se calcula a partir de la duración. Pedir siempre el segundo diez
/// falla en cualquier vídeo más corto, y ffmpeg lo comunica con un «incorrect
/// parameters such as bit_rate, rate, width or height» que no lleva a ninguna
/// parte.
fn fotograma(origen: &Path, destino: &Path, ffmpeg: &Path, duracion: Option<f64>) -> bool {
    let posicion = duracion
        .filter(|d| *d > 0.0)
        .map(|d| (d * 0.15).clamp(1.0, 10.0))
        .unwrap_or(1.0);

    crate::proc::command(ffmpeg)
        .args(["-v", "error", "-ss", &format!("{posicion:.2}"), "-i"])
        .arg(origen)
        .args([
            "-frames:v",
            "1",
            "-vf",
            &format!("scale={ANCHO}:-2"),
            "-q:v",
            "6",
        ])
        .arg(destino)
        .arg("-y")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
        && destino.is_file()
}

fn duracion_de(origen: &Path, ffprobe: &Path) -> Option<f64> {
    let salida = crate::proc::command(ffprobe)
        .args(["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0"])
        .arg(origen)
        .output()
        .ok()?;
    String::from_utf8_lossy(&salida.stdout).trim().parse().ok()
}

/// Nombres habituales de la portada dentro de una carpeta de música.
const PORTADAS: [&str; 6] = ["cover", "folder", "front", "album", "albumart", "portada"];
const EXT_IMAGEN: [&str; 4] = ["jpg", "jpeg", "png", "webp"];

/// Busca una portada suelta junto al archivo.
///
/// La mayoría de las canciones no llevan carátula incrustada —de veinte archivos
/// reales, solo una la tenía—, pero es muy común que la carpeta tenga un
/// `cover.jpg` o similar. Sin este paso, casi toda la música local se quedaría
/// sin imagen.
fn portada_en_carpeta(origen: &Path) -> Option<PathBuf> {
    let carpeta = origen.parent()?;
    let entradas = std::fs::read_dir(carpeta).ok()?;
    let mut cualquiera: Option<PathBuf> = None;

    for entrada in entradas.filter_map(std::result::Result::ok) {
        let ruta = entrada.path();
        let Some(ext) = ruta.extension().and_then(|e| e.to_str()) else {
            continue;
        };
        if !EXT_IMAGEN.contains(&ext.to_ascii_lowercase().as_str()) {
            continue;
        }
        let nombre = ruta
            .file_stem()
            .map(|n| n.to_string_lossy().to_lowercase())
            .unwrap_or_default();

        if PORTADAS.iter().any(|p| nombre == *p || nombre.starts_with(p)) {
            return Some(ruta);
        }
        cualquiera.get_or_insert(ruta);
    }
    // Una imagen cualquiera de la carpeta es mejor que ninguna: en un álbum
    // suele ser la portada aunque se llame de otro modo.
    cualquiera
}

/// Copia y reduce una imagen ya existente.
fn desde_imagen(origen: &Path, destino: &Path, ffmpeg: &Path) -> bool {
    crate::proc::command(ffmpeg)
        .args(["-v", "error", "-i"])
        .arg(origen)
        .args(["-vf", &format!("scale={ANCHO}:-2"), "-q:v", "6"])
        .arg(destino)
        .arg("-y")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
        && destino.is_file()
}

/// Devuelve la miniatura de un archivo, generándola si hace falta.
///
/// `None` cuando no se puede: sin ffmpeg, o una canción sin carátula incrustada,
/// que es un caso normal y no un error.
pub fn ensure(
    file_path: &str,
    kind: &str,
    duracion: Option<f64>,
    bins: &Binaries,
    cache_dir: &Path,
) -> Option<PathBuf> {
    let origen = Path::new(file_path);
    if !origen.is_file() {
        return None;
    }

    let destino = cache_dir.join(nombre_para(file_path));
    if destino.is_file() {
        return Some(destino);
    }
    std::fs::create_dir_all(cache_dir).ok()?;
    let ffmpeg = ffmpeg_de(bins)?;

    let hecho = if kind == "video" {
        let d = duracion.or_else(|| ffprobe_de(bins).and_then(|p| duracion_de(origen, &p)));
        fotograma(origen, &destino, &ffmpeg, d)
    } else {
        // Primero la carátula del propio archivo; si no la tiene, la portada de
        // la carpeta, que es lo habitual en una discoteca ordenada por álbumes.
        caratula(origen, &destino, &ffmpeg)
            || portada_en_carpeta(origen)
                .map(|img| desde_imagen(&img, &destino, &ffmpeg))
                .unwrap_or(false)
    };

    if hecho {
        Some(destino)
    } else {
        // ffmpeg deja el archivo a medias cuando falla; si se queda, la próxima
        // vez se daría por buena una miniatura vacía.
        let _ = std::fs::remove_file(&destino);
        None
    }
}

/// Borra las miniaturas que ya no corresponden a nada de la biblioteca.
pub fn limpiar_huerfanas(cache_dir: &Path, en_uso: &std::collections::HashSet<String>) -> usize {
    let Ok(entradas) = std::fs::read_dir(cache_dir) else {
        return 0;
    };
    let mut borradas = 0;
    for entrada in entradas.filter_map(std::result::Result::ok) {
        let ruta = entrada.path();
        let nombre = ruta.file_name().map(|n| n.to_string_lossy().into_owned());
        if let Some(n) = nombre {
            if !en_uso.contains(&n) && std::fs::remove_file(&ruta).is_ok() {
                borradas += 1;
            }
        }
    }
    borradas
}

pub fn cache_dir_de(data_dir: &Path) -> PathBuf {
    data_dir.join("thumbs")
}


#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn el_nombre_es_estable_y_no_distingue_mayusculas() {
        let a = nombre_para(r"C:\Musica\Cancion.mp3");
        let b = nombre_para(r"c:\musica\cancion.mp3");
        assert_eq!(a, b, "la misma ruta con otras mayúsculas es el mismo archivo");
        assert!(a.ends_with(".jpg"));
        assert_ne!(a, nombre_para(r"C:\Musica\Otra.mp3"));
    }

    /// De veinte archivos reales, solo uno traía la carátula dentro. Sin mirar
    /// la carpeta, casi toda la música local se quedaría sin imagen.
    #[test]
    fn encuentra_la_portada_de_la_carpeta() {
        let dir = std::env::temp_dir().join(format!("recodio-cover-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let cancion = dir.join("pista.mp3");
        std::fs::write(&cancion, b"x").unwrap();

        // Sin imágenes no hay nada que encontrar.
        assert!(portada_en_carpeta(&cancion).is_none());

        // Una imagen cualquiera vale como último recurso.
        std::fs::write(dir.join("foto random.png"), b"x").unwrap();
        assert!(portada_en_carpeta(&cancion).is_some());

        // Pero un nombre de portada gana a esa imagen cualquiera.
        std::fs::write(dir.join("cover.jpg"), b"x").unwrap();
        let elegida = portada_en_carpeta(&cancion).unwrap();
        assert_eq!(elegida.file_name().unwrap(), "cover.jpg");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn ignora_lo_que_no_son_imagenes() {
        let dir = std::env::temp_dir().join(format!("recodio-cover2-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let cancion = dir.join("pista.mp3");
        std::fs::write(&cancion, b"x").unwrap();
        std::fs::write(dir.join("cover.txt"), b"x").unwrap();
        std::fs::write(dir.join("otra.mp3"), b"x").unwrap();

        assert!(portada_en_carpeta(&cancion).is_none());
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn limpia_solo_lo_que_ya_no_se_usa() {
        let dir = std::env::temp_dir().join(format!("recodio-thumbs-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("enuso.jpg"), b"x").unwrap();
        std::fs::write(dir.join("sobra.jpg"), b"x").unwrap();

        let mut en_uso = std::collections::HashSet::new();
        en_uso.insert("enuso.jpg".to_string());

        assert_eq!(limpiar_huerfanas(&dir, &en_uso), 1);
        assert!(dir.join("enuso.jpg").exists());
        assert!(!dir.join("sobra.jpg").exists());

        std::fs::remove_dir_all(&dir).ok();
    }
}

#[cfg(test)]
mod prueba_real {
    use super::*;

    /// Genera miniaturas de archivos de verdad para medir acierto y coste.
    ///     cargo test --lib -- --ignored --nocapture miniaturas_de_verdad
    #[test]
    #[ignore]
    fn miniaturas_de_verdad() {
        let origen = std::path::PathBuf::from(r"C:\Users\gerar\Music");
        if !origen.is_dir() {
            println!("  sin carpeta de prueba");
            return;
        }
        let cache = std::env::temp_dir().join(format!("recodio-th-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&cache).unwrap();
        let bins = Binaries::new(cache.join("bin"));

        // La música vive en subcarpetas, así que hay que bajar.
        fn buscar(dir: &Path, salida: &mut Vec<PathBuf>, limite: usize) {
            if salida.len() >= limite { return; }
            let Ok(entradas) = std::fs::read_dir(dir) else { return };
            for e in entradas.filter_map(Result::ok) {
                if salida.len() >= limite { return; }
                let p = e.path();
                if p.is_dir() {
                    buscar(&p, salida, limite);
                } else if p.extension().and_then(|x| x.to_str())
                    .map(|x| x.eq_ignore_ascii_case("mp3") || x.eq_ignore_ascii_case("mp4"))
                    .unwrap_or(false)
                {
                    salida.push(p);
                }
            }
        }
        let mut archivos = Vec::new();
        buscar(&origen, &mut archivos, 20);

        let t = std::time::Instant::now();
        let (mut con, mut sin) = (0, 0);
        let mut bytes = 0u64;
        for a in &archivos {
            let kind = if a.extension().map(|e| e == "mp4").unwrap_or(false) { "video" } else { "audio" };
            match ensure(&a.to_string_lossy(), kind, None, &bins, &cache) {
                Some(m) => { con += 1; bytes += std::fs::metadata(&m).map(|x| x.len()).unwrap_or(0); }
                None => sin += 1,
            }
        }
        let tardado = t.elapsed();
        println!("  {} archivos: {con} con miniatura, {sin} sin ella", archivos.len());
        println!("  tardado: {tardado:?}  ({:?} por archivo)", tardado / archivos.len().max(1) as u32);
        if con > 0 { println!("  tamaño medio: {} KB", bytes / con / 1024); }

        std::fs::remove_dir_all(&cache).ok();
    }
}

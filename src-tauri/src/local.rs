//! Importación de carpetas del propio equipo.
//!
//! Recodio deja de ser solo lo que se ha descargado con él: una carpeta de
//! música de siempre entra en la biblioteca y se reproduce igual. Volver a
//! escanearla añade lo nuevo y no toca lo que ya estaba, que es lo que se espera
//! de una carpeta que crece.

use crate::binaries::Binaries;
use crate::db::{Db, LibraryItem, Playlist};
use anyhow::Result;
use serde::Serialize;
use serde_json::Value;
use std::path::{Path, PathBuf};

const AUDIO: [&str; 8] = ["mp3", "m4a", "flac", "opus", "ogg", "wav", "aac", "wma"];
const VIDEO: [&str; 7] = ["mp4", "mkv", "webm", "avi", "mov", "m4v", "wmv"];

/// Profundidad máxima al recorrer subcarpetas. Suficiente para
/// `Música/Artista/Álbum/pista` y evita perderse en árboles enormes.
const MAX_PROFUNDIDAD: u32 = 6;

#[derive(Debug, Default, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportReport {
    /// Archivos reproducibles encontrados en la carpeta.
    pub found: usize,
    /// Los que se han añadido a la biblioteca en este escaneo.
    pub added: usize,
    /// Los que ya estaban y no se han tocado.
    pub skipped: usize,
    /// Identificador de la colección, para poder abrirla después.
    pub playlist_id: String,
    pub title: String,
}

/// La ruta tal cual se guarda en la biblioteca.
fn item_path_str(ruta: &Path) -> String {
    ruta.to_string_lossy().into_owned()
}

fn tipo_de(ruta: &Path) -> Option<&'static str> {
    let ext = ruta.extension()?.to_str()?.to_ascii_lowercase();
    if AUDIO.contains(&ext.as_str()) {
        Some("audio")
    } else if VIDEO.contains(&ext.as_str()) {
        Some("video")
    } else {
        None
    }
}

fn recorrer(dir: &Path, profundidad: u32, salida: &mut Vec<PathBuf>) {
    if profundidad == 0 {
        return;
    }
    let Ok(entradas) = std::fs::read_dir(dir) else {
        return;
    };
    for entrada in entradas.filter_map(std::result::Result::ok) {
        let ruta = entrada.path();
        if ruta.is_dir() {
            // Las carpetas de trabajo de una descarga a medias no son biblioteca.
            let oculta = ruta
                .file_name()
                .and_then(|n| n.to_str())
                .map(|n| n.starts_with('.'))
                .unwrap_or(false);
            if !oculta {
                recorrer(&ruta, profundidad - 1, salida);
            }
        } else if tipo_de(&ruta).is_some() {
            salida.push(ruta);
        }
    }
}

/// ¿El texto arranca con ese nombre, ignorando mayúsculas y acentos?
fn empieza_por(texto: &str, prefijo: &str) -> bool {
    let normalizar = |s: &str| {
        s.to_lowercase()
            .chars()
            .map(|c| match c {
                'á' => 'a',
                'é' => 'e',
                'í' => 'i',
                'ó' => 'o',
                'ú' => 'u',
                c => c,
            })
            .filter(|c| c.is_alphanumeric())
            .collect::<String>()
    };
    let (t, p) = (normalizar(texto), normalizar(prefijo));
    !p.is_empty() && t.starts_with(&p)
}

/// Título, artista y duración de un archivo, leídos con ffprobe.
struct Datos {
    title: String,
    artist: Option<String>,
    duration: Option<f64>,
}

fn leer_datos(ruta: &Path, ffprobe: Option<&Path>) -> Datos {
    let nombre = ruta
        .file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_else(|| "Sin título".into());

    let Some(ffprobe) = ffprobe else {
        return Datos {
            title: nombre,
            artist: None,
            duration: None,
        };
    };

    let salida = crate::proc::command(ffprobe)
        .args(["-v", "quiet", "-print_format", "json", "-show_format"])
        .arg(ruta)
        .output();

    let json: Option<Value> = salida
        .ok()
        .filter(|o| o.status.success())
        .and_then(|o| serde_json::from_slice(&o.stdout).ok());

    let Some(json) = json else {
        return Datos {
            title: nombre,
            artist: None,
            duration: None,
        };
    };

    let etiqueta = |claves: &[&str]| -> Option<String> {
        claves.iter().find_map(|k| {
            json.pointer(&format!("/format/tags/{k}"))
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(str::to_string)
        })
    };

    let artist = etiqueta(&["artist", "ARTIST", "album_artist", "ALBUM_ARTIST"]);
    let titulo_etiqueta = etiqueta(&["title", "TITLE"]);

    // El nombre del archivo suele estar mejor puesto que las etiquetas, que
    // vienen sueltas («Pista 03») en muchas descargas antiguas. Solo se prefiere
    // la etiqueta cuando aporta artista y título, y sin repetir el artista si el
    // título ya lo trae: si no salen cosas como «Papa Roach - Papa Roach - …».
    let title = match (&artist, &titulo_etiqueta) {
        (Some(a), Some(t)) if !nombre.contains(t.as_str()) => {
            if empieza_por(t, a) {
                t.clone()
            } else {
                format!("{a} - {t}")
            }
        }
        _ => nombre,
    };

    Datos {
        title,
        artist,
        duration: json
            .pointer("/format/duration")
            .and_then(|d| d.as_str().and_then(|s| s.parse().ok()).or_else(|| d.as_f64())),
    }
}

/// Escanea una carpeta y añade a la biblioteca lo que no estuviera ya.
///
/// `on_progress` recibe (procesados, total) para poder mostrar avance: leer los
/// datos de cientos de archivos lleva su tiempo la primera vez.
pub fn import_folder(
    db: &Db,
    bins: &Binaries,
    carpeta: &Path,
    on_progress: impl Fn(usize, usize) + Send + Sync,
) -> Result<ImportReport> {
    let mut archivos = Vec::new();
    recorrer(carpeta, MAX_PROFUNDIDAD, &mut archivos);
    archivos.sort();

    let title = carpeta
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| carpeta.to_string_lossy().into_owned());
    let source_id = carpeta.to_string_lossy().to_lowercase();

    // La colección se identifica por la carpeta: volver a escanearla cae en la
    // misma y solo añade lo que falte.
    let playlist_id = db
        .playlist_id_for("local", &source_id)
        .unwrap_or_else(|| uuid::Uuid::new_v4().to_string());

    db.upsert_playlist(&Playlist {
        id: playlist_id.clone(),
        source: "local".into(),
        source_id: source_id.clone(),
        url: carpeta.to_string_lossy().into_owned(),
        title: title.clone(),
        uploader: None,
        thumbnail: None,
        created_at: chrono::Utc::now().timestamp(),
        item_count: 0,
    })?;

    let mut informe = ImportReport {
        found: archivos.len(),
        playlist_id: playlist_id.clone(),
        title,
        ..Default::default()
    };

    let ffprobe = bins.resolve("ffprobe").or_else(|| bins.resolve("ffmpeg").map(|f| {
        // ffprobe viaja junto a ffmpeg; si solo está este, se busca al lado.
        f.with_file_name(if cfg!(windows) { "ffprobe.exe" } else { "ffprobe" })
    }));
    let ffprobe = ffprobe.filter(|p| p.is_file());

    let ahora = chrono::Utc::now().timestamp();
    for (i, ruta) in archivos.iter().enumerate() {
        on_progress(i, archivos.len());

        let kind = tipo_de(ruta).unwrap_or("audio");

        // Lo ya conocido se salta sin leer metadatos, y eso incluye lo que
        // Recodio descargó: añadir la carpeta de descargas es lo primero que
        // hace cualquiera, y sin esto duplicaría todas las playlists. De paso,
        // es lo que hace que volver a escanear una carpeta grande sea
        // instantáneo.
        if db.file_already_known(&item_path_str(ruta)) {
            informe.skipped += 1;
            continue;
        }

        let datos = leer_datos(ruta, ffprobe.as_deref());
        let item = LibraryItem {
            id: uuid::Uuid::new_v4().to_string(),
            source: "local".into(),
            extractor: "local".into(),
            source_id: ruta.to_string_lossy().to_lowercase(),
            url: String::new(),
            title: datos.title,
            uploader: datos.artist,
            duration: datos.duration,
            thumbnail: None,
            file_path: ruta.to_string_lossy().into_owned(),
            file_size: std::fs::metadata(ruta).map(|m| m.len() as i64).unwrap_or(0),
            kind: kind.to_string(),
            ext: ruta
                .extension()
                .and_then(|e| e.to_str())
                .unwrap_or("")
                .to_ascii_lowercase(),
            playlist_id: Some(playlist_id.clone()),
            playlist_index: Some(i as i64 + 1),
            downloaded_at: ahora,
        };

        if db.upsert_item(&item).is_ok() {
            informe.added += 1;
        }
    }
    on_progress(archivos.len(), archivos.len());

    Ok(informe)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn carpeta(nombre: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("recodio-local-{nombre}-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn reconoce_audio_y_video_e_ignora_lo_demas() {
        assert_eq!(tipo_de(Path::new("a/b.mp3")), Some("audio"));
        assert_eq!(tipo_de(Path::new("a/b.FLAC")), Some("audio"));
        assert_eq!(tipo_de(Path::new("a/b.mkv")), Some("video"));
        assert_eq!(tipo_de(Path::new("a/b.txt")), None);
        assert_eq!(tipo_de(Path::new("a/b.jpg")), None);
        assert_eq!(tipo_de(Path::new("a/portada")), None);
    }

    #[test]
    fn recorre_las_subcarpetas_y_salta_las_ocultas() {
        let raiz = carpeta("recorrer");
        std::fs::create_dir_all(raiz.join("Artista/Album")).unwrap();
        std::fs::create_dir_all(raiz.join(".recodio-abc12345")).unwrap();
        std::fs::write(raiz.join("suelta.mp3"), b"x").unwrap();
        std::fs::write(raiz.join("Artista/Album/pista.flac"), b"x").unwrap();
        std::fs::write(raiz.join("Artista/portada.jpg"), b"x").unwrap();
        // Una descarga a medias no debe entrar en la biblioteca.
        std::fs::write(raiz.join(".recodio-abc12345/a medias.mp3"), b"x").unwrap();

        let mut encontrados = Vec::new();
        recorrer(&raiz, MAX_PROFUNDIDAD, &mut encontrados);

        assert_eq!(encontrados.len(), 2, "encontrados: {encontrados:?}");
        assert!(encontrados.iter().any(|p| p.ends_with("suelta.mp3")));
        assert!(encontrados.iter().any(|p| p.ends_with("pista.flac")));

        std::fs::remove_dir_all(&raiz).ok();
    }

    /// Lo que motivó la función: volver a escanear añade lo nuevo y deja en paz
    /// lo que ya estaba.
    #[test]
    fn volver_a_escanear_solo_anade_lo_nuevo() {
        let raiz = carpeta("reescaneo");
        let db = Db::open(&raiz.join("test.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));
        let musica = raiz.join("musica");
        std::fs::create_dir_all(&musica).unwrap();

        std::fs::write(musica.join("una.mp3"), b"x").unwrap();
        std::fs::write(musica.join("otra.mp3"), b"x").unwrap();

        let primero = import_folder(&db, &bins, &musica, |_, _| {}).unwrap();
        assert_eq!(primero.found, 2);
        assert_eq!(primero.added, 2);
        assert_eq!(primero.skipped, 0);

        // Llega una canción nueva a la carpeta.
        std::fs::write(musica.join("nueva.mp3"), b"x").unwrap();

        let segundo = import_folder(&db, &bins, &musica, |_, _| {}).unwrap();
        assert_eq!(segundo.found, 3);
        assert_eq!(segundo.added, 1, "solo la nueva");
        assert_eq!(segundo.skipped, 2, "las de antes se dejan como estaban");
        assert_eq!(
            segundo.playlist_id, primero.playlist_id,
            "la misma carpeta debe caer en la misma colección"
        );
        assert_eq!(db.list_items(Some(&primero.playlist_id), None).unwrap().len(), 3);

        std::fs::remove_dir_all(&raiz).ok();
    }

    /// El caso real que lo destapó: un archivo cuya etiqueta de título ya
    /// incluía el artista salía como «Papa Roach - Papa Roach - Last Resort».
    #[test]
    fn no_repite_el_artista_cuando_el_titulo_ya_lo_lleva() {
        assert!(empieza_por("Papa Roach - Last Resort", "Papa Roach"));
        assert!(empieza_por("papa roach - last resort", "Papa Roach"));
        // Con acentos y signos de por medio sigue reconociéndolo.
        assert!(empieza_por("Rubén Blades — Pedro Navaja", "Ruben Blades"));
        // Y no se pasa de listo con artistas distintos.
        assert!(!empieza_por("Last Resort", "Papa Roach"));
        assert!(!empieza_por("Roachford - Cuddly Toy", "Papa Roach"));
        assert!(!empieza_por("cualquier cosa", ""));
    }

    /// Lo primero que hace cualquiera es añadir la carpeta donde Recodio
    /// descarga. Sin comprobarlo, cada playlist aparecía dos veces: la
    /// descargada y una copia local con los mismos archivos.
    #[test]
    fn no_reimporta_lo_que_ya_esta_descargado() {
        let raiz = carpeta("ya-descargado");
        let db = Db::open(&raiz.join("test.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));
        let descargas = raiz.join("Recodio");
        std::fs::create_dir_all(&descargas).unwrap();

        let ya = descargas.join("Willie Colon - El Gran Varon.mp3");
        std::fs::write(&ya, b"x").unwrap();
        std::fs::write(descargas.join("nueva de verdad.mp3"), b"x").unwrap();

        // Simula una descarga previa: ese archivo ya está en la biblioteca.
        db.upsert_item(&LibraryItem {
            id: uuid::Uuid::new_v4().to_string(),
            source: "spotdl".into(),
            extractor: "spotify".into(),
            source_id: "abc123".into(),
            url: String::new(),
            title: "Willie Colón - El Gran Varón".into(),
            uploader: None,
            duration: None,
            thumbnail: None,
            file_path: ya.to_string_lossy().into_owned(),
            file_size: 1,
            kind: "audio".into(),
            ext: "mp3".into(),
            playlist_id: None,
            playlist_index: None,
            downloaded_at: 0,
        })
        .unwrap();

        let informe = import_folder(&db, &bins, &descargas, |_, _| {}).unwrap();
        assert_eq!(informe.found, 2);
        assert_eq!(informe.added, 1, "solo la que no estaba");
        assert_eq!(informe.skipped, 1, "la ya descargada se respeta");
        assert_eq!(
            db.list_items(None, None).unwrap().len(),
            2,
            "no puede haber dos entradas para el mismo archivo"
        );

        std::fs::remove_dir_all(&raiz).ok();
    }

    #[test]
    fn quitar_una_coleccion_no_borra_los_archivos() {
        let raiz = carpeta("borrar");
        let db = Db::open(&raiz.join("test.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));
        let musica = raiz.join("musica");
        std::fs::create_dir_all(&musica).unwrap();
        let uno = musica.join("una.mp3");
        std::fs::write(&uno, b"x").unwrap();
        std::fs::write(musica.join("otra.mp3"), b"x").unwrap();

        let informe = import_folder(&db, &bins, &musica, |_, _| {}).unwrap();
        assert_eq!(informe.added, 2);

        let quitadas = db.delete_playlist(&informe.playlist_id, false).unwrap();
        assert_eq!(quitadas, 2);
        assert!(db.list_items(None, None).unwrap().is_empty());
        assert!(uno.exists(), "el archivo debe seguir en el disco");
        assert!(
            db.list_playlists().unwrap().is_empty(),
            "la colección desaparece de la lista"
        );

        std::fs::remove_dir_all(&raiz).ok();
    }

    #[test]
    fn una_carpeta_vacia_no_rompe_nada() {
        let raiz = carpeta("vacia");
        let db = Db::open(&raiz.join("test.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));
        let vacia = raiz.join("vacia");
        std::fs::create_dir_all(&vacia).unwrap();

        let informe = import_folder(&db, &bins, &vacia, |_, _| {}).unwrap();
        assert_eq!(informe.found, 0);
        assert_eq!(informe.added, 0);

        std::fs::remove_dir_all(&raiz).ok();
    }
}

#[cfg(test)]
mod prueba_real {
    use super::*;

    /// Escanea una carpeta de verdad para medir el coste. No corre en la suite
    /// normal porque depende del equipo:
    ///     cargo test --lib -- --ignored --nocapture escanea_una_carpeta
    #[test]
    #[ignore]
    fn escanea_una_carpeta_grande_de_verdad() {
        let origen = std::path::PathBuf::from(r"C:\Users\gerar\Music");
        if !origen.is_dir() {
            println!("  sin carpeta de prueba, se omite");
            return;
        }

        let raiz = std::env::temp_dir().join(format!("recodio-real-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&raiz).unwrap();
        let db = Db::open(&raiz.join("t.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));

        let t = std::time::Instant::now();
        let primero = import_folder(&db, &bins, &origen, |_, _| {}).unwrap();
        let en_frio = t.elapsed();

        let t = std::time::Instant::now();
        let segundo = import_folder(&db, &bins, &origen, |_, _| {}).unwrap();
        let al_repetir = t.elapsed();

        println!("  encontrados: {}", primero.found);
        println!("  primera vez: {en_frio:?}  ({} añadidos)", primero.added);
        println!("  repetido:    {al_repetir:?}  ({} ya estaban)", segundo.skipped);

        assert_eq!(segundo.added, 0, "repetir no debe añadir nada");
        assert_eq!(segundo.skipped, primero.added);

        let items = db.list_items(Some(&primero.playlist_id), None).unwrap();
        for it in items.iter().take(5) {
            println!("    {} · {:?}s", it.title, it.duration.map(|d| d.round()));
        }

        std::fs::remove_dir_all(&raiz).ok();
    }
}

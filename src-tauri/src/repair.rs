//! Reparación de la biblioteca.
//!
//! Las versiones hasta la 0.1.2 deducían el archivo que había producido spotDL
//! comparando la carpeta antes y después de la descarga. Con varias descargas
//! simultáneas en la misma carpeta, cada una podía quedarse con el archivo de
//! otra, y la biblioteca acababa con varios títulos apuntando al mismo sitio.
//!
//! Eso ya no ocurre, pero las entradas mal guardadas siguen ahí. Este módulo las
//! limpia comparando cada título con el nombre real del archivo.

use crate::binaries::Binaries;
use crate::db::{Db, LibraryItem};
use anyhow::Result;
use serde::Serialize;
use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Debug, Default, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RepairReport {
    /// Entradas revisadas.
    pub checked: usize,
    /// Entradas eliminadas de la biblioteca. Los archivos no se tocan.
    pub removed: usize,
    /// Archivos que tenían más de una entrada apuntándoles.
    pub shared_files: usize,
    /// Entradas cuyo archivo ya no está en disco.
    pub missing: usize,
    /// Entradas cuya duración no cuadraba con la del archivo: no eran suyas.
    pub mismatched: usize,
}

/// Reduce un texto a palabras comparables: sin acentos, sin signos y en
/// minúsculas, para que «El Gran Varón» y «El Gran Varon» sean lo mismo.
fn palabras(texto: &str) -> Vec<String> {
    texto
        .to_lowercase()
        .chars()
        .map(|c| match c {
            'á' | 'à' | 'ä' | 'â' => 'a',
            'é' | 'è' | 'ë' | 'ê' => 'e',
            'í' | 'ì' | 'ï' | 'î' => 'i',
            'ó' | 'ò' | 'ö' | 'ô' => 'o',
            'ú' | 'ù' | 'ü' | 'û' => 'u',
            'ñ' => 'n',
            c if c.is_alphanumeric() => c,
            _ => ' ',
        })
        .collect::<String>()
        .split_whitespace()
        .filter(|p| p.len() > 1)
        .map(str::to_string)
        .collect()
}

/// Cuántas palabras del título aparecen en el nombre del archivo, en tanto por
/// uno. El nombre del archivo es la fuente fiable: lo escribió el descargador a
/// partir de los metadatos reales de la pista.
fn parecido(titulo: &str, nombre_archivo: &str) -> f64 {
    let del_titulo = palabras(titulo);
    if del_titulo.is_empty() {
        return 0.0;
    }
    let del_archivo = palabras(nombre_archivo);
    let coincidencias = del_titulo
        .iter()
        .filter(|p| del_archivo.contains(p))
        .count();
    coincidencias as f64 / del_titulo.len() as f64
}

fn nombre_de(item: &LibraryItem) -> String {
    Path::new(&item.file_path)
        .file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_default()
}

/// Cuenta el daño sin tocar nada, para poder avisar sin que el usuario tenga que
/// ir a buscar la herramienta a Ajustes.
pub fn health(db: &Db) -> Result<RepairReport> {
    let items = db.list_items(None, None)?;
    let mut informe = RepairReport {
        checked: items.len(),
        ..Default::default()
    };

    let mut por_archivo: HashMap<String, usize> = HashMap::new();
    for item in &items {
        if !Path::new(&item.file_path).exists() {
            informe.missing += 1;
            continue;
        }
        *por_archivo.entry(item.file_path.to_lowercase()).or_default() += 1;
    }

    for cuantas in por_archivo.values() {
        if *cuantas > 1 {
            informe.shared_files += 1;
            informe.removed += cuantas - 1; // Lo que se quitaría al reparar.
        }
    }
    informe.removed += informe.missing;
    Ok(informe)
}

/// Cuánto puede diferir la duración guardada de la real antes de dar por hecho
/// que la entrada no corresponde a ese archivo.
///
/// Medido sobre una biblioteca real: en entradas correctas la diferencia
/// mediana es de medio segundo y la mayor, de 18,7 s — ediciones distintas,
/// silencios finales. En las cruzadas va de 21 a 76 s. Con veinte segundos no se
/// marca ni una sola entrada buena, que es lo que importa: dejar escapar una
/// mala se arregla volviendo a reparar; borrar una buena, no.
const MARGEN_DURACION: f64 = 20.0;

/// Lee la duración real del archivo. `None` si no se puede saber.
fn duracion_real(ruta: &str, ffprobe: Option<&Path>) -> Option<f64> {
    let ffprobe = ffprobe?;
    let salida = crate::proc::command(ffprobe)
        .args(["-v", "quiet", "-print_format", "json", "-show_format"])
        .arg(ruta)
        .output()
        .ok()?;
    if !salida.status.success() {
        return None;
    }
    let json: serde_json::Value = serde_json::from_slice(&salida.stdout).ok()?;
    json.pointer("/format/duration")
        .and_then(|d| d.as_str().and_then(|s| s.parse().ok()).or_else(|| d.as_f64()))
}

fn localizar_ffprobe(bins: &Binaries) -> Option<PathBuf> {
    bins.resolve("ffprobe")
        .or_else(|| {
            bins.resolve("ffmpeg").map(|f| {
                f.with_file_name(if cfg!(windows) { "ffprobe.exe" } else { "ffprobe" })
            })
        })
        .filter(|p| p.is_file())
}

/// Quita de la biblioteca las entradas que no corresponden a su archivo,
/// **sin tocar los archivos**: la música sigue en el disco, solo deja de
/// aparecer bajo un título que no era el suyo.
///
/// El criterio principal es la duración, que es objetiva. Comparar títulos con
/// nombres de archivo no basta: «Daddy Yankee, Shenseea - Echo» apuntando a
/// `Daddy Yankee - Echo.mp3` es correcto y «Gorillaz - Rockit» apuntando a
/// `Gorillaz - Ghost Train.mp3` no lo es, y ambos se parecen igual de poco.
pub fn repair(
    db: &Db,
    bins: &Binaries,
    on_progress: impl Fn(usize, usize) + Send + Sync,
) -> Result<RepairReport> {
    let items = db.list_items(None, None)?;
    let total = items.len();
    let mut informe = RepairReport {
        checked: total,
        ..Default::default()
    };

    let ffprobe = localizar_ffprobe(bins);
    let mut por_archivo: HashMap<String, Vec<LibraryItem>> = HashMap::new();
    let mut sospechosas: Vec<LibraryItem> = Vec::new();

    for (i, item) in items.into_iter().enumerate() {
        on_progress(i, total);

        if !Path::new(&item.file_path).exists() {
            informe.missing += 1;
            db.delete_item(&item.id, false)?;
            informe.removed += 1;
            continue;
        }

        // La duración manda cuando se conoce: es el único dato que distingue una
        // colaboración mal titulada de una canción que sencillamente no es esa.
        if let (Some(esperada), Some(real)) = (
            item.duration.filter(|d| *d > 0.0),
            duracion_real(&item.file_path, ffprobe.as_deref()),
        ) {
            if (esperada - real).abs() > MARGEN_DURACION {
                sospechosas.push(item);
                continue;
            }
            // Coincide en duración: es suya, aunque comparta archivo con otra
            // entrada legítima de otra playlist.
            continue;
        }

        // Sin duración no hay más remedio que comparar nombres, y eso solo es
        // fiable para deshacer empates dentro de un mismo archivo.
        por_archivo
            .entry(item.file_path.to_lowercase())
            .or_default()
            .push(item);
    }

    for item in sospechosas {
        db.delete_item(&item.id, false)?;
        informe.removed += 1;
        informe.mismatched += 1;
    }

    for grupo in por_archivo.into_values() {
        if grupo.len() < 2 {
            continue;
        }
        informe.shared_files += 1;

        let nombre = nombre_de(&grupo[0]);
        let mejor = grupo
            .iter()
            .enumerate()
            .max_by(|(_, a), (_, b)| {
                parecido(&a.title, &nombre)
                    .partial_cmp(&parecido(&b.title, &nombre))
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
            .map(|(i, _)| i)
            .unwrap_or(0);

        for (i, item) in grupo.iter().enumerate() {
            if i != mejor {
                db.delete_item(&item.id, false)?;
                informe.removed += 1;
            }
        }
    }

    on_progress(total, total);
    Ok(informe)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn item(titulo: &str, ruta: &str) -> LibraryItem {
        LibraryItem {
            id: uuid::Uuid::new_v4().to_string(),
            source: "spotdl".into(),
            extractor: "spotify".into(),
            source_id: uuid::Uuid::new_v4().to_string(),
            url: String::new(),
            title: titulo.into(),
            uploader: None,
            duration: None,
            thumbnail: None,
            file_path: ruta.into(),
            file_size: 1,
            kind: "audio".into(),
            ext: "mp3".into(),
            playlist_id: None,
            playlist_index: None,
            downloaded_at: 0,
        }
    }

    /// Lo que decide la reparación es el *orden*, no una nota absoluta: se
    /// conserva el título que más se parezca al archivo. Comprobar un umbral
    /// fijo sería engañoso, porque el nombre del artista se repite en todos los
    /// títulos del grupo e infla la puntuación de todos por igual.
    #[test]
    fn el_titulo_correcto_puntua_por_encima_de_los_demas() {
        let nombre = "NateWantsToBattle - Madness";
        let correcto = parecido("NateWantsToBattle - Madness", nombre);

        for impostor in [
            "NateWantsToBattle - Count the Teeth",
            "NateWantsToBattle - Obsolete",
            "NateWantsToBattle - One Way Ticket",
            "NateWantsToBattle, Jacksepticeye - Enjoy the Show",
        ] {
            assert!(
                correcto > parecido(impostor, nombre),
                "«{impostor}» no debería ganar a «{nombre}»"
            );
        }
    }

    #[test]
    fn los_acentos_no_estorban() {
        assert!(parecido("Willie Colón - El Gran Varón", "Willie Colon - El Gran Varon") > 0.9);
    }

    /// El caso real: cinco títulos distintos apuntando al mismo MP3.
    #[test]
    fn conserva_solo_el_titulo_correcto() {
        let raiz = std::env::temp_dir().join(format!("recodio-rep-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&raiz).unwrap();
        let ruta = raiz.join("NateWantsToBattle - Madness.mp3");
        std::fs::write(&ruta, b"audio").unwrap();
        let ruta = ruta.to_string_lossy().into_owned();

        let db = Db::open(&raiz.join("test.sqlite")).unwrap();
        for titulo in [
            "NateWantsToBattle - Count the Teeth",
            "NateWantsToBattle - Obsolete",
            "NateWantsToBattle - Madness",
            "NateWantsToBattle - One Way Ticket",
            "NateWantsToBattle - Enjoy the Show",
        ] {
            db.upsert_item(&item(titulo, &ruta)).unwrap();
        }
        assert_eq!(db.list_items(None, None).unwrap().len(), 5);

        let bins = Binaries::new(raiz.join("bin"));
        let informe = repair(&db, &bins, |_, _| {}).unwrap();
        assert_eq!(informe.removed, 4);
        assert_eq!(informe.shared_files, 1);

        let quedan = db.list_items(None, None).unwrap();
        assert_eq!(quedan.len(), 1);
        assert_eq!(quedan[0].title, "NateWantsToBattle - Madness");
        // El archivo sigue en su sitio: solo se limpió la biblioteca.
        assert!(Path::new(&ruta).exists());

        std::fs::remove_dir_all(&raiz).ok();
    }

    /// El margen se eligió midiendo una biblioteca real: en entradas correctas
    /// la diferencia máxima fue de 18,7 s y en las cruzadas empezaba en 21 s. Si
    /// alguien lo baja «para afinar», empezará a borrar música buena.
    #[test]
    fn el_margen_de_duracion_no_castiga_las_ediciones_distintas() {
        let correctas = [0.5, 3.0, 9.0, 13.0, 18.7];
        for d in correctas {
            assert!(
                d <= MARGEN_DURACION,
                "una diferencia de {d} s aparece en entradas correctas y no debe marcarse"
            );
        }
        let cruzadas = [21.0, 44.0, 51.0, 76.0];
        for d in cruzadas {
            assert!(
                d > MARGEN_DURACION,
                "una diferencia de {d} s solo aparece en entradas cruzadas"
            );
        }
    }

    /// Dos playlists pueden compartir el mismo archivo legítimamente. Antes se
    /// borraba una de las dos por el simple hecho de compartirlo.
    #[test]
    fn respeta_la_misma_cancion_en_dos_playlists() {
        let raiz = std::env::temp_dir().join(format!("recodio-rep3-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&raiz).unwrap();
        let db = Db::open(&raiz.join("test.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));

        let ruta = raiz.join("Animal Kingdom - Get Away With It.mp3");
        std::fs::write(&ruta, b"audio").unwrap();
        let ruta = ruta.to_string_lossy().into_owned();

        // Sin duración conocida y en playlists distintas: el criterio de nombres
        // las puntúa igual, así que no hay motivo para quitar ninguna.
        for lista in ["fiesta", "gimnasio"] {
            db.upsert_playlist(&crate::db::Playlist {
                id: lista.into(),
                source: "spotdl".into(),
                source_id: lista.into(),
                url: String::new(),
                title: lista.into(),
                uploader: None,
                thumbnail: None,
                created_at: 0,
                item_count: 0,
            })
            .unwrap();

            let mut it = item("Animal Kingdom - Get Away With It", &ruta);
            it.playlist_id = Some(lista.into());
            it.source_id = format!("{lista}-cancion");
            db.upsert_item(&it).unwrap();
        }
        assert_eq!(db.list_items(None, None).unwrap().len(), 2);

        let informe = repair(&db, &bins, |_, _| {}).unwrap();
        assert_eq!(
            db.list_items(None, None).unwrap().len(),
            1,
            "sin duración solo queda una; con duración se conservarían las dos"
        );
        assert_eq!(informe.mismatched, 0, "ninguna es basura, solo empatan");

        std::fs::remove_dir_all(&raiz).ok();
    }

    #[test]
    fn no_toca_las_entradas_sanas() {
        let raiz = std::env::temp_dir().join(format!("recodio-rep2-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&raiz).unwrap();
        let db = Db::open(&raiz.join("test.sqlite")).unwrap();

        for titulo in ["Artista - Una", "Artista - Otra", "Artista - Tercera"] {
            let ruta = raiz.join(format!("{titulo}.mp3"));
            std::fs::write(&ruta, b"audio").unwrap();
            db.upsert_item(&item(titulo, &ruta.to_string_lossy())).unwrap();
        }

        let bins = Binaries::new(raiz.join("bin"));
        let informe = repair(&db, &bins, |_, _| {}).unwrap();
        assert_eq!(informe.removed, 0);
        assert_eq!(informe.shared_files, 0);
        assert_eq!(db.list_items(None, None).unwrap().len(), 3);

        std::fs::remove_dir_all(&raiz).ok();
    }
}

#[cfg(test)]
mod prueba_real {
    use super::*;

    /// Repara una copia de una biblioteca de verdad y enseña qué quitaría.
    ///     cargo test --lib -- --ignored --nocapture repara_una_biblioteca
    #[test]
    #[ignore]
    fn repara_una_biblioteca_real() {
        let origen = std::path::PathBuf::from(
            r"C:\Users\gerar\AppData\Roaming\com.recodio.app\recodio.db",
        );
        if !origen.is_file() {
            println!("  sin biblioteca de prueba, se omite");
            return;
        }
        // Sobre una copia: esto no toca la biblioteca del usuario.
        let raiz = std::env::temp_dir().join(format!("recodio-rep-real-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&raiz).unwrap();
        let copia = raiz.join("copia.sqlite");
        std::fs::copy(&origen, &copia).unwrap();

        let db = Db::open(&copia).unwrap();
        let bins = Binaries::new(raiz.join("bin"));
        let antes = db.list_items(None, None).unwrap().len();

        let t = std::time::Instant::now();
        let informe = repair(&db, &bins, |_, _| {}).unwrap();
        let tardado = t.elapsed();

        let despues = db.list_items(None, None).unwrap().len();
        println!("  antes: {antes}   despues: {despues}   tardado: {tardado:?}");
        println!(
            "  quitadas: {} (sin archivo: {}, no corresponden: {}, empates: {})",
            informe.removed,
            informe.missing,
            informe.mismatched,
            informe.removed - informe.missing - informe.mismatched
        );

        // Lo importante: que no quede nada apuntando a un archivo ajeno.
        let restantes = db.list_items(None, None).unwrap();
        let mut por_archivo: HashMap<String, usize> = HashMap::new();
        for it in &restantes {
            *por_archivo.entry(it.file_path.to_lowercase()).or_default() += 1;
        }
        let compartidos: usize = por_archivo.values().filter(|n| **n > 1).count();
        println!("  archivos que aun comparten entrada: {compartidos}");

        std::fs::remove_dir_all(&raiz).ok();
    }
}

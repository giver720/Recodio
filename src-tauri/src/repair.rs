//! Reparación de la biblioteca.
//!
//! Las versiones hasta la 0.1.2 deducían el archivo que había producido spotDL
//! comparando la carpeta antes y después de la descarga. Con varias descargas
//! simultáneas en la misma carpeta, cada una podía quedarse con el archivo de
//! otra, y la biblioteca acababa con varios títulos apuntando al mismo sitio.
//!
//! Eso ya no ocurre, pero las entradas mal guardadas siguen ahí. Este módulo las
//! limpia comparando cada título con el nombre real del archivo.

use crate::db::{Db, LibraryItem};
use anyhow::Result;
use serde::Serialize;
use std::collections::HashMap;
use std::path::Path;

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

/// Deja una sola entrada por archivo: la que mejor case con su nombre. Las demás
/// se borran de la biblioteca, **sin tocar los archivos**: quien tenga la música
/// en disco la conserva, solo deja de aparecer bajo un título que no era el suyo.
pub fn repair(db: &Db) -> Result<RepairReport> {
    let items = db.list_items(None, None)?;
    let mut informe = RepairReport {
        checked: items.len(),
        ..Default::default()
    };

    let mut por_archivo: HashMap<String, Vec<LibraryItem>> = HashMap::new();
    for item in items {
        if !Path::new(&item.file_path).exists() {
            informe.missing += 1;
            db.delete_item(&item.id, false)?;
            informe.removed += 1;
            continue;
        }
        por_archivo
            .entry(item.file_path.to_lowercase())
            .or_default()
            .push(item);
    }

    for grupo in por_archivo.into_values() {
        // Un archivo con una sola entrada no puede ser víctima de la confusión.
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

        let informe = repair(&db).unwrap();
        assert_eq!(informe.removed, 4);
        assert_eq!(informe.shared_files, 1);

        let quedan = db.list_items(None, None).unwrap();
        assert_eq!(quedan.len(), 1);
        assert_eq!(quedan[0].title, "NateWantsToBattle - Madness");
        // El archivo sigue en su sitio: solo se limpió la biblioteca.
        assert!(Path::new(&ruta).exists());

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

        let informe = repair(&db).unwrap();
        assert_eq!(informe.removed, 0);
        assert_eq!(informe.shared_files, 0);
        assert_eq!(db.list_items(None, None).unwrap().len(), 3);

        std::fs::remove_dir_all(&raiz).ok();
    }
}

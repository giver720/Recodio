//! Puesta al día de la biblioteca.
//!
//! Una sola acción en lugar de tres botones repartidos. Vuelve a mirar las
//! carpetas añadidas, retira lo que ya no está, corrige las entradas que no
//! corresponden a su archivo y genera las miniaturas que falten.

use crate::binaries::Binaries;
use crate::db::Db;
use anyhow::Result;
use serde::Serialize;
use std::collections::HashSet;
use std::path::{Path, PathBuf};

#[derive(Debug, Default, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshReport {
    /// Archivos nuevos encontrados en las carpetas añadidas.
    pub imported: usize,
    /// Archivos sueltos nuevos encontrados en las carpetas vigiladas.
    pub scanned: usize,
    /// Entradas retiradas porque su archivo ya no está.
    pub missing: usize,
    /// Entradas retiradas porque no correspondían a su archivo.
    pub mismatched: usize,
    /// Miniaturas generadas en esta pasada.
    pub thumbnails: usize,
    /// Miniaturas sobrantes eliminadas.
    pub thumbnails_cleaned: usize,
    /// Carpetas locales revisadas.
    pub folders: usize,
}

/// Fases, para que la interfaz pueda decir qué está haciendo en vez de un
/// porcentaje mudo.
#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    /// Buscando archivos nuevos en las carpetas añadidas.
    Scanning,
    /// Comprobando que cada entrada corresponda a su archivo.
    Checking,
    /// Generando las miniaturas que faltan.
    Thumbnails,
}

pub fn refresh(
    db: &Db,
    bins: &Binaries,
    data_dir: &Path,
    raices: &[PathBuf],
    on_progress: impl Fn(Phase, usize, usize) + Send + Sync,
) -> Result<RefreshReport> {
    let mut informe = RefreshReport::default();

    // 1. Las carpetas del equipo pueden haber crecido.
    let carpetas: Vec<(String, String)> = db
        .list_playlists()?
        .into_iter()
        .filter(|p| p.source == "local")
        .map(|p| (p.id, p.url))
        .collect();
    informe.folders = carpetas.len();

    for (i, (_, ruta)) in carpetas.iter().enumerate() {
        on_progress(Phase::Scanning, i, carpetas.len());
        if Path::new(ruta).is_dir() {
            if let Ok(r) = crate::local::import_folder(db, bins, Path::new(ruta), |_, _| {}) {
                informe.imported += r.added;
            }
        }
    }

    // 1b. Y en las carpetas vigiladas puede haber aparecido cualquier cosa
    //     suelta: un mp3 descargado con otra cosa, un vídeo copiado a mano.
    if !raices.is_empty() {
        let r = crate::local::scan_loose(db, bins, raices, |hechos, total| {
            on_progress(Phase::Scanning, hechos, total);
        })?;
        informe.scanned = r.added;
    }

    // 2. Lo que ya no está, y lo que nunca fue suyo.
    let reparacion = crate::repair::repair(db, bins, |hechos, total| {
        on_progress(Phase::Checking, hechos, total);
    })?;
    informe.missing = reparacion.missing;
    informe.mismatched = reparacion.mismatched;

    // 3. Miniaturas. Va al final porque las dos fases anteriores cambian qué
    //    entradas existen, y generar la miniatura de algo que se va a retirar
    //    sería trabajo tirado.
    let items = db.list_items(None, None)?;
    let cache = crate::thumbs::cache_dir_de(data_dir);
    let total = items.len();
    let mut en_uso: HashSet<String> = HashSet::new();

    for (i, item) in items.iter().enumerate() {
        on_progress(Phase::Thumbnails, i, total);

        // Las descargas ya traen la miniatura del sitio de origen.
        let ya_tiene_remota = item
            .thumbnail
            .as_deref()
            .map(|t| t.starts_with("http"))
            .unwrap_or(false);
        if ya_tiene_remota {
            continue;
        }

        if let Some(ruta) =
            crate::thumbs::ensure(&item.file_path, &item.kind, item.duration, bins, &cache)
        {
            let nombre = ruta
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_default();
            en_uso.insert(nombre);

            let texto = ruta.to_string_lossy().into_owned();
            if item.thumbnail.as_deref() != Some(texto.as_str()) {
                let mut actualizado = item.clone();
                actualizado.thumbnail = Some(texto);
                if db.upsert_item(&actualizado).is_ok() {
                    informe.thumbnails += 1;
                }
            } else {
                // Ya la tenía y sigue siendo válida: cuenta como en uso para no
                // borrarla en la limpieza.
            }
        }
    }

    informe.thumbnails_cleaned = crate::thumbs::limpiar_huerfanas(&cache, &en_uso);
    on_progress(Phase::Thumbnails, total, total);
    Ok(informe)
}


#[cfg(test)]
mod prueba_real {
    use super::*;

    /// Pone al día una copia de una biblioteca de verdad.
    ///     cargo test --lib -- --ignored --nocapture pone_al_dia_una_biblioteca
    #[test]
    #[ignore]
    fn pone_al_dia_una_biblioteca_real() {
        let origen = std::path::PathBuf::from(
            r"C:\Users\gerar\AppData\Roaming\com.recodio.app\recodio.db",
        );
        if !origen.is_file() {
            println!("  sin biblioteca de prueba");
            return;
        }
        let raiz = std::env::temp_dir().join(format!("recodio-refr-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&raiz).unwrap();
        std::fs::copy(&origen, raiz.join("copia.sqlite")).unwrap();

        let db = Db::open(&raiz.join("copia.sqlite")).unwrap();
        let bins = Binaries::new(raiz.join("bin"));
        let antes = db.list_items(None, None).unwrap().len();
        let sin_miniatura_antes = db
            .list_items(None, None)
            .unwrap()
            .iter()
            .filter(|i| i.thumbnail.is_none())
            .count();

        let t = std::time::Instant::now();
        let informe = refresh(&db, &bins, &raiz, &[], |_, _, _| {}).unwrap();
        let tardado = t.elapsed();

        let despues = db.list_items(None, None).unwrap();
        let sin_miniatura = despues.iter().filter(|i| i.thumbnail.is_none()).count();

        println!("  entradas: {antes} -> {}", despues.len());
        println!("  sin miniatura: {sin_miniatura_antes} -> {sin_miniatura}");
        println!(
            "  informe: {} nuevas · {} miniaturas · {} corregidas · {} ausentes · {} carpetas",
            informe.imported, informe.thumbnails, informe.mismatched, informe.missing, informe.folders
        );
        println!("  tardado: {tardado:?}");

        std::fs::remove_dir_all(&raiz).ok();
    }
}

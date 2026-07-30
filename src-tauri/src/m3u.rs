//! Generación de la playlist local. Cuando Recodio descarga una playlist, el
//! resultado no es solo una carpeta con archivos sueltos: escribe un `.m3u8` al
//! lado que cualquier reproductor (VLC, foobar, MPV…) abre como playlist, en el
//! orden original.

use crate::db::Db;
use anyhow::{anyhow, Result};
use std::path::{Path, PathBuf};

/// Deja un nombre utilizable como carpeta o archivo en las tres plataformas.
pub fn sanitize(name: &str) -> String {
    let cleaned: String = name
        .chars()
        .map(|c| match c {
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*' => '_',
            c if (c as u32) < 0x20 => '_',
            c => c,
        })
        .collect();
    let trimmed = cleaned.trim().trim_end_matches('.').trim();
    if trimmed.is_empty() {
        "Recodio".into()
    } else {
        trimmed.chars().take(120).collect()
    }
}

/// Escribe (o reescribe) el `.m3u8` de una playlist en la carpeta donde están
/// sus archivos. Devuelve la ruta del archivo generado.
pub fn write(db: &Db, playlist_id: &str) -> Result<PathBuf> {
    let items = db.list_items(Some(playlist_id), None)?;
    if items.is_empty() {
        return Err(anyhow!("Esa playlist no tiene archivos descargados todavía"));
    }

    let title = db
        .list_playlists()?
        .into_iter()
        .find(|p| p.id == playlist_id)
        .map(|p| p.title)
        .unwrap_or_else(|| "playlist".into());

    let dir = Path::new(&items[0].file_path)
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| anyhow!("No se pudo determinar la carpeta de la playlist"))?;
    let path = dir.join(format!("{}.m3u8", sanitize(&title)));

    let mut out = String::from("#EXTM3U\n");
    out.push_str(&format!("#PLAYLIST:{title}\n"));
    for it in &items {
        // Rutas relativas: así la carpeta se puede mover o copiar a otro equipo
        // sin que la playlist se rompa.
        let rel = Path::new(&it.file_path)
            .strip_prefix(&dir)
            .map(|p| p.to_string_lossy().replace('\\', "/"))
            .unwrap_or_else(|_| it.file_path.clone());

        out.push_str(&format!(
            "#EXTINF:{},{}\n{}\n",
            it.duration.unwrap_or(-1.0).round() as i64,
            it.title,
            rel
        ));
    }

    std::fs::write(&path, out)?;
    Ok(path)
}

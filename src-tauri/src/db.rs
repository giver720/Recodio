use anyhow::Result;
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use std::path::Path;
use std::sync::Mutex;

/// One downloaded file on disk.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryItem {
    pub id: String,
    /// `ytdlp` or `spotdl`.
    pub source: String,
    /// yt-dlp extractor key (`youtube`, `twitter`, …) or `spotify`.
    pub extractor: String,
    /// Video / track id as reported by the extractor.
    pub source_id: String,
    pub url: String,
    pub title: String,
    pub uploader: Option<String>,
    pub duration: Option<f64>,
    pub thumbnail: Option<String>,
    pub file_path: String,
    pub file_size: i64,
    /// `video` or `audio`.
    pub kind: String,
    pub ext: String,
    pub playlist_id: Option<String>,
    pub playlist_index: Option<i64>,
    pub downloaded_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Playlist {
    pub id: String,
    pub source: String,
    pub source_id: String,
    pub url: String,
    pub title: String,
    pub uploader: Option<String>,
    pub thumbnail: Option<String>,
    pub created_at: i64,
    /// Filled by queries, not stored.
    #[serde(default)]
    pub item_count: i64,
}

pub struct Db {
    conn: Mutex<Connection>,
}

impl Db {
    pub fn open(path: &Path) -> Result<Self> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let conn = Connection::open(path)?;
        conn.execute_batch(
            r#"
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS playlists (
                id          TEXT PRIMARY KEY,
                source      TEXT NOT NULL,
                source_id   TEXT NOT NULL,
                url         TEXT NOT NULL,
                title       TEXT NOT NULL,
                uploader    TEXT,
                thumbnail   TEXT,
                created_at  INTEGER NOT NULL,
                UNIQUE(source, source_id)
            );

            CREATE TABLE IF NOT EXISTS items (
                id             TEXT PRIMARY KEY,
                source         TEXT NOT NULL,
                extractor      TEXT NOT NULL,
                source_id      TEXT NOT NULL,
                url            TEXT NOT NULL,
                title          TEXT NOT NULL,
                uploader       TEXT,
                duration       REAL,
                thumbnail      TEXT,
                file_path      TEXT NOT NULL,
                file_size      INTEGER NOT NULL DEFAULT 0,
                kind           TEXT NOT NULL,
                ext            TEXT NOT NULL,
                playlist_id    TEXT REFERENCES playlists(id) ON DELETE SET NULL,
                playlist_index INTEGER,
                downloaded_at  INTEGER NOT NULL,
                UNIQUE(extractor, source_id, kind)
            );

            CREATE INDEX IF NOT EXISTS idx_items_playlist ON items(playlist_id);
            CREATE INDEX IF NOT EXISTS idx_items_lookup   ON items(extractor, source_id);
            CREATE INDEX IF NOT EXISTS idx_items_title    ON items(title);
            "#,
        )?;
        Ok(Self {
            conn: Mutex::new(conn),
        })
    }

    /// Look for an already-downloaded item of the same kind. Returns `None` when
    /// the row exists but the file is gone from disk (and cleans the row up).
    pub fn find_existing(&self, extractor: &str, source_id: &str, kind: &str) -> Option<LibraryItem> {
        let item = {
            let conn = self.conn.lock().ok()?;
            let mut stmt = conn
                .prepare(
                    "SELECT id, source, extractor, source_id, url, title, uploader, duration,
                            thumbnail, file_path, file_size, kind, ext, playlist_id,
                            playlist_index, downloaded_at
                     FROM items WHERE extractor = ?1 AND source_id = ?2 AND kind = ?3",
                )
                .ok()?;
            stmt.query_row(params![extractor, source_id, kind], row_to_item)
                .optional()
                .ok()?
        }?;

        if Path::new(&item.file_path).exists() {
            Some(item)
        } else {
            let _ = self.delete_item(&item.id, false);
            None
        }
    }

    pub fn upsert_item(&self, item: &LibraryItem) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO items (id, source, extractor, source_id, url, title, uploader, duration,
                                thumbnail, file_path, file_size, kind, ext, playlist_id,
                                playlist_index, downloaded_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16)
             ON CONFLICT(extractor, source_id, kind) DO UPDATE SET
                url=excluded.url, title=excluded.title, uploader=excluded.uploader,
                duration=excluded.duration, thumbnail=excluded.thumbnail,
                file_path=excluded.file_path, file_size=excluded.file_size,
                ext=excluded.ext, playlist_id=COALESCE(excluded.playlist_id, items.playlist_id),
                playlist_index=COALESCE(excluded.playlist_index, items.playlist_index),
                downloaded_at=excluded.downloaded_at",
            params![
                item.id,
                item.source,
                item.extractor,
                item.source_id,
                item.url,
                item.title,
                item.uploader,
                item.duration,
                item.thumbnail,
                item.file_path,
                item.file_size,
                item.kind,
                item.ext,
                item.playlist_id,
                item.playlist_index,
                item.downloaded_at,
            ],
        )?;
        Ok(())
    }

    pub fn upsert_playlist(&self, pl: &Playlist) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO playlists (id, source, source_id, url, title, uploader, thumbnail, created_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8)
             ON CONFLICT(source, source_id) DO UPDATE SET
                title=excluded.title, uploader=excluded.uploader,
                thumbnail=COALESCE(excluded.thumbnail, playlists.thumbnail)",
            params![
                pl.id,
                pl.source,
                pl.source_id,
                pl.url,
                pl.title,
                pl.uploader,
                pl.thumbnail,
                pl.created_at
            ],
        )?;
        Ok(())
    }

    /// Resolve the internal uuid for a playlist identified by its source id.
    pub fn playlist_id_for(&self, source: &str, source_id: &str) -> Option<String> {
        let conn = self.conn.lock().ok()?;
        conn.query_row(
            "SELECT id FROM playlists WHERE source = ?1 AND source_id = ?2",
            params![source, source_id],
            |r| r.get(0),
        )
        .optional()
        .ok()
        .flatten()
    }

    pub fn list_items(&self, playlist_id: Option<&str>, search: Option<&str>) -> Result<Vec<LibraryItem>> {
        let conn = self.conn.lock().unwrap();
        let base = "SELECT id, source, extractor, source_id, url, title, uploader, duration,
                           thumbnail, file_path, file_size, kind, ext, playlist_id,
                           playlist_index, downloaded_at FROM items";
        let like = search.map(|s| format!("%{s}%"));

        let mut out = Vec::new();
        match (playlist_id, &like) {
            (Some(pid), Some(q)) => {
                let mut stmt = conn.prepare(&format!(
                    "{base} WHERE playlist_id = ?1 AND (title LIKE ?2 OR uploader LIKE ?2)
                     ORDER BY playlist_index, downloaded_at DESC"
                ))?;
                for r in stmt.query_map(params![pid, q], row_to_item)? {
                    out.push(r?);
                }
            }
            (Some(pid), None) => {
                let mut stmt = conn.prepare(&format!(
                    "{base} WHERE playlist_id = ?1 ORDER BY playlist_index, downloaded_at DESC"
                ))?;
                for r in stmt.query_map(params![pid], row_to_item)? {
                    out.push(r?);
                }
            }
            (None, Some(q)) => {
                let mut stmt = conn.prepare(&format!(
                    "{base} WHERE title LIKE ?1 OR uploader LIKE ?1 ORDER BY downloaded_at DESC"
                ))?;
                for r in stmt.query_map(params![q], row_to_item)? {
                    out.push(r?);
                }
            }
            (None, None) => {
                let mut stmt = conn.prepare(&format!("{base} ORDER BY downloaded_at DESC"))?;
                for r in stmt.query_map([], row_to_item)? {
                    out.push(r?);
                }
            }
        }
        Ok(out)
    }

    pub fn list_playlists(&self) -> Result<Vec<Playlist>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT p.id, p.source, p.source_id, p.url, p.title, p.uploader, p.thumbnail,
                    p.created_at, COUNT(i.id)
             FROM playlists p LEFT JOIN items i ON i.playlist_id = p.id
             GROUP BY p.id ORDER BY p.created_at DESC",
        )?;
        let rows = stmt.query_map([], |r| {
            Ok(Playlist {
                id: r.get(0)?,
                source: r.get(1)?,
                source_id: r.get(2)?,
                url: r.get(3)?,
                title: r.get(4)?,
                uploader: r.get(5)?,
                thumbnail: r.get(6)?,
                created_at: r.get(7)?,
                item_count: r.get(8)?,
            })
        })?;
        rows.collect::<Result<Vec<_>, _>>().map_err(Into::into)
    }

    /// Remove an item from the library, optionally deleting the file too.
    pub fn delete_item(&self, id: &str, delete_file: bool) -> Result<()> {
        let path: Option<String> = {
            let conn = self.conn.lock().unwrap();
            let p = conn
                .query_row("SELECT file_path FROM items WHERE id = ?1", params![id], |r| {
                    r.get(0)
                })
                .optional()?;
            conn.execute("DELETE FROM items WHERE id = ?1", params![id])?;
            p
        };
        if delete_file {
            if let Some(p) = path {
                let _ = std::fs::remove_file(p);
            }
        }
        Ok(())
    }

    /// Drop rows whose file no longer exists. Returns how many were removed.
    pub fn prune_missing(&self) -> Result<usize> {
        let stale: Vec<String> = {
            let conn = self.conn.lock().unwrap();
            let mut stmt = conn.prepare("SELECT id, file_path FROM items")?;
            let rows = stmt.query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))?;
            rows.filter_map(|r| r.ok())
                .filter(|(_, p)| !Path::new(p).exists())
                .map(|(id, _)| id)
                .collect()
        };
        for id in &stale {
            self.delete_item(id, false)?;
        }
        Ok(stale.len())
    }
}

fn row_to_item(r: &rusqlite::Row<'_>) -> rusqlite::Result<LibraryItem> {
    Ok(LibraryItem {
        id: r.get(0)?,
        source: r.get(1)?,
        extractor: r.get(2)?,
        source_id: r.get(3)?,
        url: r.get(4)?,
        title: r.get(5)?,
        uploader: r.get(6)?,
        duration: r.get(7)?,
        thumbnail: r.get(8)?,
        file_path: r.get(9)?,
        file_size: r.get(10)?,
        kind: r.get(11)?,
        ext: r.get(12)?,
        playlist_id: r.get(13)?,
        playlist_index: r.get(14)?,
        downloaded_at: r.get(15)?,
    })
}

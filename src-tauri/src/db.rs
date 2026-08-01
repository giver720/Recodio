use anyhow::Result;
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
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

const ITEMS_TABLE: &str = r#"
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
        downloaded_at  INTEGER NOT NULL
    );
"#;

/// Las primeras versiones declaraban `UNIQUE(extractor, source_id, kind)` en la
/// propia tabla, lo que impedía tener una canción en dos playlists distintas. La
/// restricción se movió a un índice que sí tiene en cuenta la playlist, pero eso
/// obliga a rehacer la tabla: en SQLite no se puede quitar un UNIQUE declarado
/// dentro del CREATE TABLE.
fn migrate_items_uniqueness(conn: &Connection) -> Result<()> {
    let definicion: Option<String> = conn
        .query_row(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'items'",
            [],
            |r| r.get(0),
        )
        .optional()?;

    let Some(definicion) = definicion else {
        return Ok(()); // Base de datos recién creada, ya nace bien.
    };
    if !definicion.contains("UNIQUE(extractor, source_id, kind)") {
        return Ok(());
    }

    conn.execute_batch(&format!(
        r#"
        PRAGMA foreign_keys = OFF;
        BEGIN;
        ALTER TABLE items RENAME TO items_antiguo;
        {ITEMS_TABLE}
        INSERT INTO items SELECT
            id, source, extractor, source_id, url, title, uploader, duration,
            thumbnail, file_path, file_size, kind, ext, playlist_id,
            playlist_index, downloaded_at
        FROM items_antiguo;
        DROP TABLE items_antiguo;
        COMMIT;
        PRAGMA foreign_keys = ON;
        "#
    ))?;
    Ok(())
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

            "#,
        )?;
        conn.execute_batch(ITEMS_TABLE)?;
        migrate_items_uniqueness(&conn)?;
        conn.execute_batch(
            r#"
            -- La misma canción puede estar en dos playlists distintas: lo que no
            -- tiene sentido es tenerla dos veces dentro de la misma. COALESCE
            -- hace que las descargas sueltas (sin playlist) cuenten como un
            -- grupo más, porque en SQLite un NULL nunca choca con otro NULL y
            -- si no se duplicarían sin control.
            CREATE UNIQUE INDEX IF NOT EXISTS idx_items_unicos
                ON items(extractor, source_id, kind, COALESCE(playlist_id, ''));

            -- Listados ya analizados. Volver a pedir una playlist de cientos de
            -- temas cuesta minutos si hay que recurrir a spotDL, y su contenido
            -- rara vez cambia entre dos intentos seguidos.
            CREATE TABLE IF NOT EXISTS analysis_cache (
                key       TEXT PRIMARY KEY,
                payload   TEXT NOT NULL,
                cached_at INTEGER NOT NULL
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

    /// Busca una descarga previa **dentro del mismo destino**.
    ///
    /// El duplicado se juzga por playlist, no por biblioteca entera: dos
    /// playlists distintas pueden compartir canciones, y encontrarse con que la
    /// segunda se queda coja porque los temas «ya estaban» en la primera no es
    /// lo que nadie espera. `playlist_id` a `None` compara contra las descargas
    /// sueltas.
    ///
    /// Devuelve `None` si la fila existe pero el archivo ya no está en disco, y
    /// de paso limpia esa fila.
    pub fn find_existing(
        &self,
        extractor: &str,
        source_id: &str,
        kind: &str,
        playlist_id: Option<&str>,
    ) -> Option<LibraryItem> {
        let item = {
            let conn = self.conn.lock().ok()?;
            let mut stmt = conn
                .prepare(
                    "SELECT id, source, extractor, source_id, url, title, uploader, duration,
                            thumbnail, file_path, file_size, kind, ext, playlist_id,
                            playlist_index, downloaded_at
                     FROM items
                     WHERE extractor = ?1 AND source_id = ?2 AND kind = ?3
                       AND COALESCE(playlist_id, '') = COALESCE(?4, '')",
                )
                .ok()?;
            stmt.query_row(params![extractor, source_id, kind, playlist_id], row_to_item)
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

    /// Otras canciones que ya estén usando ese mismo archivo.
    ///
    /// Es la red de seguridad contra el fallo que cruzó bibliotecas enteras: si
    /// una descarga acaba señalando un archivo que ya pertenece a otra canción,
    /// hay que enterarse antes de guardarlo, no cuando el usuario le da al play.
    pub fn others_using_file(&self, file_path: &str, source_id: &str) -> Vec<String> {
        let Ok(conn) = self.conn.lock() else {
            return Vec::new();
        };
        let Ok(mut stmt) = conn.prepare(
            "SELECT title FROM items WHERE file_path = ?1 AND source_id <> ?2 LIMIT 5",
        ) else {
            return Vec::new();
        };
        stmt.query_map(params![file_path, source_id], |r| r.get::<_, String>(0))
            .map(|rows| rows.filter_map(Result::ok).collect())
            .unwrap_or_default()
    }

    /// Todas las rutas que la biblioteca ya conoce, en minúsculas.
    ///
    /// Lo usan la importación y el rastreo: si se añade la carpeta donde Recodio
    /// descarga, sus archivos ya están en la biblioteca y volver a meterlos
    /// duplicaría cada playlist. Se devuelve el conjunto entero en vez de
    /// resolver archivo por archivo porque un rastreo toca miles de rutas y una
    /// consulta por cada una convierte un segundo en un minuto.
    pub fn known_files(&self) -> HashSet<String> {
        let Ok(conn) = self.conn.lock() else {
            return HashSet::new();
        };
        let Ok(mut stmt) = conn.prepare("SELECT file_path FROM items") else {
            return HashSet::new();
        };
        stmt.query_map([], |r| r.get::<_, String>(0))
            .map(|rows| rows.filter_map(Result::ok).map(|p| p.to_lowercase()).collect())
            .unwrap_or_default()
    }

    /// Borra una playlist y las entradas que solo pertenecían a ella.
    ///
    /// Los archivos no se tocan salvo que se pida: quien quiera conservar la
    /// música y solo deshacerse de la lista, la conserva.
    pub fn delete_playlist(&self, playlist_id: &str, delete_files: bool) -> Result<usize> {
        let items = self.list_items(Some(playlist_id), None)?;
        let cuantas = items.len();
        for item in items {
            self.delete_item(&item.id, delete_files)?;
        }
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM playlists WHERE id = ?1", params![playlist_id])?;
        Ok(cuantas)
    }

    pub fn upsert_item(&self, item: &LibraryItem) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO items (id, source, extractor, source_id, url, title, uploader, duration,
                                thumbnail, file_path, file_size, kind, ext, playlist_id,
                                playlist_index, downloaded_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16)
             ON CONFLICT(extractor, source_id, kind, COALESCE(playlist_id, '')) DO UPDATE SET
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

    /// Devuelve el listado guardado y cuándo se guardó.
    pub fn cache_get(&self, key: &str) -> Option<(String, i64)> {
        let conn = self.conn.lock().ok()?;
        conn.query_row(
            "SELECT payload, cached_at FROM analysis_cache WHERE key = ?1",
            params![key],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .optional()
        .ok()
        .flatten()
    }

    pub fn cache_put(&self, key: &str, payload: &str) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO analysis_cache (key, payload, cached_at) VALUES (?1, ?2, ?3)
             ON CONFLICT(key) DO UPDATE SET payload = excluded.payload,
                                            cached_at = excluded.cached_at",
            params![key, payload, chrono::Utc::now().timestamp()],
        )?;
        Ok(())
    }

    pub fn cache_forget(&self, key: &str) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM analysis_cache WHERE key = ?1", params![key])?;
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn db_temporal() -> (Db, PathBuf) {
        let ruta = std::env::temp_dir().join(format!("recodio-db-{}.sqlite", uuid::Uuid::new_v4()));
        (Db::open(&ruta).unwrap(), ruta)
    }

    fn playlist(db: &Db, nombre: &str) -> String {
        let id = uuid::Uuid::new_v4().to_string();
        db.upsert_playlist(&Playlist {
            id: id.clone(),
            source: "spotdl".into(),
            source_id: nombre.into(),
            url: format!("https://open.spotify.com/playlist/{nombre}"),
            title: nombre.into(),
            uploader: None,
            thumbnail: None,
            created_at: 0,
            item_count: 0,
        })
        .unwrap();
        id
    }

    /// Crea un archivo de verdad: `find_existing` descarta las filas cuyo
    /// archivo ya no está, así que sin esto los tests darían falsos negativos.
    fn item(source_id: &str, playlist_id: Option<&str>, dir: &Path) -> LibraryItem {
        let ruta = dir.join(format!("{source_id}-{}.mp3", uuid::Uuid::new_v4()));
        std::fs::write(&ruta, b"audio").unwrap();
        LibraryItem {
            id: uuid::Uuid::new_v4().to_string(),
            source: "spotdl".into(),
            extractor: "spotify".into(),
            source_id: source_id.into(),
            url: format!("https://open.spotify.com/track/{source_id}"),
            title: source_id.into(),
            uploader: None,
            duration: None,
            thumbnail: None,
            file_path: ruta.to_string_lossy().into_owned(),
            file_size: 5,
            kind: "audio".into(),
            ext: "mp3".into(),
            playlist_id: playlist_id.map(str::to_string),
            playlist_index: Some(1),
            downloaded_at: 0,
        }
    }

    /// Lo que motivó el cambio: dos playlists pueden compartir canciones, y la
    /// segunda no debe quedarse coja porque los temas «ya estaban» en la primera.
    #[test]
    fn la_misma_cancion_puede_estar_en_dos_playlists() {
        let (db, ruta) = db_temporal();
        let dir = ruta.parent().unwrap();
        let (fiesta, gimnasio) = (playlist(&db, "fiesta"), playlist(&db, "gimnasio"));

        db.upsert_item(&item("cancion1", Some(&fiesta), dir)).unwrap();

        // En la playlist donde ya está, se omite.
        assert!(
            db.find_existing("spotify", "cancion1", "audio", Some(&fiesta)).is_some(),
            "debería detectarse como repetida dentro de su propia playlist"
        );
        // En otra playlist, no.
        assert!(
            db.find_existing("spotify", "cancion1", "audio", Some(&gimnasio)).is_none(),
            "no debe considerarse repetida en una playlist distinta"
        );
        // Y como descarga suelta, tampoco.
        assert!(db.find_existing("spotify", "cancion1", "audio", None).is_none());

        // Y de hecho puede guardarse en las dos a la vez.
        db.upsert_item(&item("cancion1", Some(&gimnasio), dir)).unwrap();
        assert_eq!(db.list_items(Some(&fiesta), None).unwrap().len(), 1);
        assert_eq!(db.list_items(Some(&gimnasio), None).unwrap().len(), 1);

        std::fs::remove_file(&ruta).ok();
    }

    /// La red de seguridad: si una descarga acaba apuntando a un archivo que ya
    /// es de otra canción, hay que detectarlo antes de guardarlo.
    #[test]
    fn detecta_que_el_archivo_ya_es_de_otra_cancion() {
        let (db, ruta) = db_temporal();
        let dir = ruta.parent().unwrap();

        let primera = item("cancion1", None, dir);
        db.upsert_item(&primera).unwrap();

        // Otra canción distinta señalando el mismo archivo: eso es el fallo.
        let intrusas = db.others_using_file(&primera.file_path, "cancion2");
        assert_eq!(intrusas.len(), 1);
        assert_eq!(intrusas[0], "cancion1");

        // La propia canción reescribiéndose no es una colisión.
        assert!(db
            .others_using_file(&primera.file_path, "cancion1")
            .is_empty());

        std::fs::remove_file(&ruta).ok();
    }

    #[test]
    fn dentro_de_una_playlist_no_se_duplica() {
        let (db, ruta) = db_temporal();
        let dir = ruta.parent().unwrap();
        let fiesta = playlist(&db, "fiesta");

        db.upsert_item(&item("cancion1", Some(&fiesta), dir)).unwrap();
        db.upsert_item(&item("cancion1", Some(&fiesta), dir)).unwrap();

        assert_eq!(
            db.list_items(Some(&fiesta), None).unwrap().len(),
            1,
            "dos veces la misma canción en la misma playlist debe colapsar en una"
        );
        std::fs::remove_file(&ruta).ok();
    }

    #[test]
    fn las_descargas_sueltas_tampoco_se_duplican_entre_si() {
        let (db, ruta) = db_temporal();
        let dir = ruta.parent().unwrap();

        db.upsert_item(&item("suelta", None, dir)).unwrap();
        db.upsert_item(&item("suelta", None, dir)).unwrap();

        assert_eq!(db.list_items(None, None).unwrap().len(), 1);
        std::fs::remove_file(&ruta).ok();
    }

    /// Una base de datos creada por la versión anterior debe poder abrirse y
    /// quedarse con la regla nueva, sin perder lo que ya tenía.
    #[test]
    fn migra_la_restriccion_antigua_conservando_los_datos() {
        let ruta = std::env::temp_dir().join(format!("recodio-mig-{}.sqlite", uuid::Uuid::new_v4()));
        let dir = ruta.parent().unwrap();

        {
            let conn = Connection::open(&ruta).unwrap();
            conn.execute_batch(
                r#"
                CREATE TABLE playlists (
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, source_id TEXT NOT NULL,
                    url TEXT NOT NULL, title TEXT NOT NULL, uploader TEXT, thumbnail TEXT,
                    created_at INTEGER NOT NULL, UNIQUE(source, source_id));
                CREATE TABLE items (
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, extractor TEXT NOT NULL,
                    source_id TEXT NOT NULL, url TEXT NOT NULL, title TEXT NOT NULL,
                    uploader TEXT, duration REAL, thumbnail TEXT, file_path TEXT NOT NULL,
                    file_size INTEGER NOT NULL DEFAULT 0, kind TEXT NOT NULL, ext TEXT NOT NULL,
                    playlist_id TEXT REFERENCES playlists(id) ON DELETE SET NULL,
                    playlist_index INTEGER, downloaded_at INTEGER NOT NULL,
                    UNIQUE(extractor, source_id, kind));
                "#,
            )
            .unwrap();
            let archivo = dir.join(format!("viejo-{}.mp3", uuid::Uuid::new_v4()));
            std::fs::write(&archivo, b"audio").unwrap();
            conn.execute(
                "INSERT INTO items VALUES ('i1','spotdl','spotify','vieja','u','Vieja',NULL,NULL,
                    NULL,?1,5,'audio','mp3',NULL,1,0)",
                params![archivo.to_string_lossy()],
            )
            .unwrap();
        }

        let db = Db::open(&ruta).unwrap();
        assert_eq!(
            db.list_items(None, None).unwrap().len(),
            1,
            "la migración no debe perder lo ya descargado"
        );

        // Y la regla nueva ya está en vigor.
        let otra = playlist(&db, "otra");
        db.upsert_item(&item("vieja", Some(&otra), dir)).unwrap();
        assert_eq!(db.list_items(None, None).unwrap().len(), 2);

        std::fs::remove_file(&ruta).ok();
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

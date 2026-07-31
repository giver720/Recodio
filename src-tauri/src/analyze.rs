use crate::binaries::Binaries;
use crate::db::Db;
use crate::proc::async_command;
use crate::settings::Settings;
use anyhow::{anyhow, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::process::Stdio;

/// One downloadable thing: a video, a track, a post.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Entry {
    pub id: String,
    pub source_id: String,
    pub extractor: String,
    pub title: String,
    pub url: String,
    pub uploader: Option<String>,
    pub duration: Option<f64>,
    pub thumbnail: Option<String>,
    pub index: i64,
    /// Path of an already-downloaded video file, if any.
    pub existing_video: Option<String>,
    /// Path of an already-downloaded audio file, if any.
    pub existing_audio: Option<String>,
    /// True when yt-dlp could not resolve it (private / removed and no mirror).
    #[serde(default)]
    pub unavailable: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistInfo {
    pub source_id: String,
    pub title: String,
    pub url: String,
    pub uploader: Option<String>,
    pub thumbnail: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AnalyzeResult {
    /// `ytdlp` or `spotdl`.
    pub source: String,
    pub is_playlist: bool,
    pub playlist: Option<PlaylistInfo>,
    pub entries: Vec<Entry>,
}

pub fn is_spotify(url: &str) -> bool {
    let u = url.to_ascii_lowercase();
    u.contains("spotify.com") || u.starts_with("spotify:")
}

pub async fn analyze(
    url: &str,
    bins: &Binaries,
    db: &Db,
    settings: &Settings,
) -> Result<AnalyzeResult> {
    let mut result = if is_spotify(url) {
        analyze_spotify(url, bins).await?
    } else {
        analyze_ytdlp(url, bins, settings).await?
    };

    // Los duplicados se marcan respecto al destino de *esta* descarga: si la
    // playlist es nueva, nada está repetido aunque las canciones ya existan en
    // otra playlist.
    let playlist_id = result
        .playlist
        .as_ref()
        .and_then(|pl| db.playlist_id_for(&result.source, &pl.source_id));

    for entry in &mut result.entries {
        entry.existing_video = db
            .find_existing(&entry.extractor, &entry.source_id, "video", playlist_id.as_deref())
            .map(|i| i.file_path);
        entry.existing_audio = db
            .find_existing(&entry.extractor, &entry.source_id, "audio", playlist_id.as_deref())
            .map(|i| i.file_path);
    }
    Ok(result)
}

async fn analyze_ytdlp(url: &str, bins: &Binaries, settings: &Settings) -> Result<AnalyzeResult> {
    let exe = bins.require("yt-dlp")?;
    let mut cmd = async_command(exe);
    cmd.arg("--dump-single-json")
        .arg("--flat-playlist")
        .arg("--no-warnings")
        .arg("--ignore-config")
        .arg("--no-playlist-reverse");

    crate::ytdlp::apply_access_args(&mut cmd, settings);
    cmd.arg(url).stdout(Stdio::piped()).stderr(Stdio::piped());

    let out = cmd.output().await?;
    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr);
        return Err(anyhow!(clean_ytdlp_error(&err)));
    }
    let json: Value = serde_json::from_slice(&out.stdout)?;

    let is_playlist = json.get("_type").and_then(Value::as_str) == Some("playlist");
    if !is_playlist {
        let entry = entry_from_ytdlp(&json, 1, None);
        return Ok(AnalyzeResult {
            source: "ytdlp".into(),
            is_playlist: false,
            playlist: None,
            entries: vec![entry],
        });
    }

    let default_extractor = json
        .get("extractor_key")
        .and_then(Value::as_str)
        .map(|s| s.to_ascii_lowercase());

    let entries: Vec<Entry> = json
        .get("entries")
        .and_then(Value::as_array)
        .map(|arr| {
            arr.iter()
                .enumerate()
                .filter(|(_, e)| !e.is_null())
                .map(|(i, e)| entry_from_ytdlp(e, i as i64 + 1, default_extractor.as_deref()))
                .collect()
        })
        .unwrap_or_default();

    let playlist = PlaylistInfo {
        source_id: json
            .get("id")
            .and_then(Value::as_str)
            .unwrap_or(url)
            .to_string(),
        title: json
            .get("title")
            .and_then(Value::as_str)
            .unwrap_or("Playlist")
            .to_string(),
        url: json
            .get("webpage_url")
            .and_then(Value::as_str)
            .unwrap_or(url)
            .to_string(),
        uploader: str_field(&json, &["uploader", "channel", "playlist_uploader"]),
        thumbnail: pick_thumbnail(&json),
    };

    Ok(AnalyzeResult {
        source: "ytdlp".into(),
        is_playlist: true,
        playlist: Some(playlist),
        entries,
    })
}

fn entry_from_ytdlp(v: &Value, index: i64, fallback_extractor: Option<&str>) -> Entry {
    let source_id = str_field(v, &["id"]).unwrap_or_else(|| format!("unknown-{index}"));
    let extractor = str_field(v, &["extractor_key", "ie_key", "extractor"])
        .map(|s| s.to_ascii_lowercase())
        .or_else(|| fallback_extractor.map(str::to_string))
        .unwrap_or_else(|| "generic".into());

    let url = str_field(v, &["webpage_url", "url", "original_url"]).unwrap_or_default();
    let title = str_field(v, &["title", "fulltitle"]).unwrap_or_else(|| source_id.clone());
    // yt-dlp keeps unavailable entries in the list with a placeholder title.
    let unavailable = matches!(
        title.as_str(),
        "[Private video]" | "[Deleted video]" | "[Unavailable video]"
    );

    Entry {
        id: uuid::Uuid::new_v4().to_string(),
        source_id,
        extractor,
        title,
        url,
        uploader: str_field(v, &["uploader", "channel", "creator", "artist"]),
        duration: v.get("duration").and_then(Value::as_f64),
        thumbnail: pick_thumbnail(v),
        index,
        existing_video: None,
        existing_audio: None,
        unavailable,
    }
}

/// Extrae el tipo y el id de un enlace de Spotify. Tiene que aguantar las URL
/// localizadas (`/intl-es/album/…`), el `?si=` que añade el botón de compartir y
/// los URI nativos (`spotify:album:…`).
fn spotify_ref(url: &str) -> Option<(String, String)> {
    let cleaned = url.split(['?', '#']).next().unwrap_or(url);
    let parts: Vec<&str> = cleaned
        .split(['/', ':'])
        .filter(|s| !s.is_empty())
        .collect();

    let pos = parts
        .iter()
        .position(|p| matches!(*p, "track" | "album" | "playlist" | "artist"))?;
    let id = parts.get(pos + 1)?;
    if id.is_empty() || !id.chars().all(|c| c.is_ascii_alphanumeric()) {
        return None;
    }
    Some((parts[pos].to_string(), (*id).to_string()))
}

/// Listado rápido leyendo la página de incrustación de Spotify.
///
/// `spotdl save` tarda unos 31 s de arranque más 4,75 s por canción — dos
/// minutos y medio para un álbum de 25, y ocho para una playlist de cien, solo
/// para *previsualizar*. Esta página devuelve la misma lista en un segundo y sin
/// autenticación. Si Spotify cambia el formato, se cae al camino de spotdl.
async fn analyze_spotify_embed(kind: &str, id: &str) -> Result<AnalyzeResult> {
    let body = reqwest::Client::builder()
        .user_agent("Mozilla/5.0")
        .build()?
        .get(format!("https://open.spotify.com/embed/{kind}/{id}"))
        .send()
        .await?
        .error_for_status()?
        .text()
        .await?;

    const OPEN: &str = r#"<script id="__NEXT_DATA__" type="application/json">"#;
    let start = body
        .find(OPEN)
        .ok_or_else(|| anyhow!("la página de Spotify no trae los datos esperados"))?
        + OPEN.len();
    let end = body[start..]
        .find("</script>")
        .ok_or_else(|| anyhow!("datos de Spotify incompletos"))?;

    let json: Value = serde_json::from_str(&body[start..start + end])?;
    let entity = json
        .pointer("/props/pageProps/state/data/entity")
        .ok_or_else(|| anyhow!("Spotify cambió la estructura de la página"))?;

    let cover = entity
        .pointer("/coverArt/sources")
        .and_then(Value::as_array)
        .and_then(|a| a.last())
        .and_then(|s| s.get("url"))
        .and_then(Value::as_str)
        .map(str::to_string);

    let tracks = entity.get("trackList").and_then(Value::as_array);

    // Una pista suelta: la propia entidad es la canción, no hay lista.
    if tracks.map(|t| t.is_empty()).unwrap_or(true) {
        let name = str_field(entity, &["name", "title"]).unwrap_or_else(|| "Pista".into());
        let artist = entity
            .pointer("/artists/0/name")
            .and_then(Value::as_str)
            .map(str::to_string);
        return Ok(AnalyzeResult {
            source: "spotdl".into(),
            is_playlist: false,
            playlist: None,
            entries: vec![Entry {
                id: uuid::Uuid::new_v4().to_string(),
                source_id: str_field(entity, &["id"]).unwrap_or_else(|| id.to_string()),
                extractor: "spotify".into(),
                title: match &artist {
                    Some(a) => format!("{a} - {name}"),
                    None => name,
                },
                url: format!("https://open.spotify.com/track/{id}"),
                uploader: artist,
                duration: entity.get("duration").and_then(Value::as_f64).map(|d| d / 1000.0),
                thumbnail: cover,
                index: 1,
                existing_video: None,
                existing_audio: None,
                unavailable: false,
            }],
        });
    }

    let entries: Vec<Entry> = tracks
        .unwrap()
        .iter()
        .enumerate()
        .map(|(i, t)| {
            let title = str_field(t, &["title"]).unwrap_or_else(|| format!("Pista {}", i + 1));
            let artist = str_field(t, &["subtitle"]);
            let track_id = str_field(t, &["uri"])
                .and_then(|u| u.rsplit(':').next().map(str::to_string))
                .unwrap_or_else(|| format!("track-{i}"));
            Entry {
                id: uuid::Uuid::new_v4().to_string(),
                source_id: track_id.clone(),
                extractor: "spotify".into(),
                title: match &artist {
                    Some(a) => format!("{a} - {title}"),
                    None => title,
                },
                url: format!("https://open.spotify.com/track/{track_id}"),
                uploader: artist,
                duration: t.get("duration").and_then(Value::as_f64).map(|d| d / 1000.0),
                thumbnail: cover.clone(),
                index: i as i64 + 1,
                existing_video: None,
                existing_audio: None,
                unavailable: !t.get("isPlayable").and_then(Value::as_bool).unwrap_or(true),
            }
        })
        .collect();

    Ok(AnalyzeResult {
        source: "spotdl".into(),
        is_playlist: true,
        playlist: Some(PlaylistInfo {
            source_id: id.to_string(),
            title: str_field(entity, &["name", "title"]).unwrap_or_else(|| "Spotify".into()),
            url: format!("https://open.spotify.com/{kind}/{id}"),
            uploader: str_field(entity, &["subtitle"]),
            thumbnail: cover,
        }),
        entries,
    })
}

async fn analyze_spotify(url: &str, bins: &Binaries) -> Result<AnalyzeResult> {
    if let Some((kind, id)) = spotify_ref(url) {
        match analyze_spotify_embed(&kind, &id).await {
            Ok(result) => return Ok(result),
            Err(e) => eprintln!("[recodio] listado rápido de Spotify no disponible ({e}); usando spotdl"),
        }
    }

    let exe = bins.require("spotdl")?;
    let tmp = std::env::temp_dir().join(format!("recodio-{}.spotdl", uuid::Uuid::new_v4()));

    let out = async_command(exe)
        .arg("save")
        .arg(url)
        .arg("--save-file")
        .arg(&tmp)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .await?;

    if !tmp.exists() {
        let err = String::from_utf8_lossy(&out.stderr);
        let msg = if err.trim().is_empty() {
            String::from_utf8_lossy(&out.stdout).to_string()
        } else {
            err.to_string()
        };
        return Err(anyhow!("spotdl no pudo leer el enlace: {}", msg.trim()));
    }

    // Lectura tolerante por el mismo motivo que la salida de los procesos: si
    // spotDL escribiera el archivo en la codificación regional, un solo título
    // con tilde tumbaría el análisis entero.
    let raw = String::from_utf8_lossy(&std::fs::read(&tmp)?).into_owned();
    let _ = std::fs::remove_file(&tmp);
    let songs: Vec<Value> = serde_json::from_str(&raw)?;

    let mut list_name: Option<String> = None;
    let mut list_url: Option<String> = None;

    let entries: Vec<Entry> = songs
        .iter()
        .enumerate()
        .map(|(i, s)| {
            if list_name.is_none() {
                list_name = str_field(s, &["list_name"]);
                list_url = str_field(s, &["list_url"]);
            }
            let name = str_field(s, &["name"]).unwrap_or_else(|| "Pista".into());
            let artist = str_field(s, &["artist"]);
            Entry {
                id: uuid::Uuid::new_v4().to_string(),
                source_id: str_field(s, &["song_id"]).unwrap_or_else(|| format!("track-{i}")),
                extractor: "spotify".into(),
                title: match &artist {
                    Some(a) => format!("{a} - {name}"),
                    None => name,
                },
                url: str_field(s, &["url"]).unwrap_or_default(),
                uploader: artist,
                duration: s.get("duration").and_then(Value::as_f64),
                thumbnail: str_field(s, &["cover_url"]),
                index: s
                    .get("list_position")
                    .and_then(Value::as_i64)
                    .unwrap_or(i as i64 + 1),
                existing_video: None,
                existing_audio: None,
                unavailable: false,
            }
        })
        .collect();

    let is_playlist = entries.len() > 1 || list_name.is_some();
    let playlist = if is_playlist {
        Some(PlaylistInfo {
            source_id: list_url.clone().unwrap_or_else(|| url.to_string()),
            title: list_name.clone().unwrap_or_else(|| "Spotify".into()),
            url: list_url.unwrap_or_else(|| url.to_string()),
            uploader: None,
            thumbnail: entries.first().and_then(|e| e.thumbnail.clone()),
        })
    } else {
        None
    };

    Ok(AnalyzeResult {
        source: "spotdl".into(),
        is_playlist,
        playlist,
        entries,
    })
}

fn str_field(v: &Value, keys: &[&str]) -> Option<String> {
    keys.iter()
        .find_map(|k| v.get(*k).and_then(Value::as_str))
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

fn pick_thumbnail(v: &Value) -> Option<String> {
    if let Some(t) = v.get("thumbnail").and_then(Value::as_str) {
        if !t.is_empty() {
            return Some(t.to_string());
        }
    }
    // Flat playlist entries carry a `thumbnails` array instead; take the largest.
    v.get("thumbnails")
        .and_then(Value::as_array)
        .and_then(|arr| {
            arr.iter()
                .filter(|t| t.get("url").and_then(Value::as_str).is_some())
                .max_by_key(|t| {
                    t.get("width").and_then(Value::as_i64).unwrap_or(0)
                        + t.get("preference").and_then(Value::as_i64).unwrap_or(0)
                })
                .and_then(|t| t.get("url").and_then(Value::as_str))
                .map(str::to_string)
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lee_enlaces_de_spotify_en_todas_sus_formas() {
        let casos = [
            // El que rompía: URL localizada con el ?si= del botón de compartir.
            (
                "https://open.spotify.com/intl-es/album/7ePC9qS9mSOTY9E0YPP6yg?si=12b2413b08644101",
                ("album", "7ePC9qS9mSOTY9E0YPP6yg"),
            ),
            (
                "https://open.spotify.com/album/7ePC9qS9mSOTY9E0YPP6yg",
                ("album", "7ePC9qS9mSOTY9E0YPP6yg"),
            ),
            (
                "https://open.spotify.com/intl-pt-br/track/2yg9UN4eo5eMVJ7OB4RWj3",
                ("track", "2yg9UN4eo5eMVJ7OB4RWj3"),
            ),
            (
                "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
                ("playlist", "37i9dQZF1DXcBWIGoYBM5M"),
            ),
            (
                "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x&pt=y",
                ("playlist", "37i9dQZF1DXcBWIGoYBM5M"),
            ),
        ];

        for (url, (kind, id)) in casos {
            let got = spotify_ref(url).unwrap_or_else(|| panic!("no se pudo leer: {url}"));
            assert_eq!((got.0.as_str(), got.1.as_str()), (kind, id), "en {url}");
        }
    }

    #[test]
    fn rechaza_lo_que_no_es_un_enlace_de_spotify() {
        assert!(spotify_ref("https://www.youtube.com/watch?v=dQw4w9WgXcQ").is_none());
        assert!(spotify_ref("https://open.spotify.com/").is_none());
        assert!(spotify_ref("https://open.spotify.com/album/").is_none());
    }

    /// Toca la red de verdad, así que no corre en la suite normal:
    ///     cargo test --lib -- --ignored --nocapture
    /// Sirve para comprobar de un vistazo si Spotify cambió el formato de la
    /// página de incrustación, que es lo único que puede romper la vía rápida.
    #[tokio::test]
    #[ignore]
    async fn listado_rapido_contra_spotify_real() {
        let album = analyze_spotify_embed("album", "1ATL5GLyefJaxhQzSPVrLX")
            .await
            .expect("el álbum debería listarse");
        assert!(album.is_playlist);
        assert_eq!(album.entries.len(), 25, "Scorpion tiene 25 pistas");
        let primera = &album.entries[0];
        assert!(primera.title.contains("Drake"), "título: {}", primera.title);
        assert!(primera.duration.unwrap_or(0.0) > 60.0);
        assert!(primera.url.starts_with("https://open.spotify.com/track/"));

        let track = analyze_spotify_embed("track", "2yg9UN4eo5eMVJ7OB4RWj3")
            .await
            .expect("la pista suelta debería listarse");
        assert!(!track.is_playlist);
        assert_eq!(track.entries.len(), 1);
        println!("pista suelta: {}", track.entries[0].title);
    }

    #[test]
    fn is_spotify_reconoce_las_variantes() {
        assert!(is_spotify("https://open.spotify.com/intl-es/album/abc"));
        assert!(is_spotify("spotify:track:abc"));
        assert!(!is_spotify("https://music.youtube.com/watch?v=abc"));
    }
}

/// yt-dlp errors are verbose; keep the useful line.
pub fn clean_ytdlp_error(raw: &str) -> String {
    let line = raw
        .lines()
        .rev()
        .find(|l| l.contains("ERROR:"))
        .or_else(|| raw.lines().rev().find(|l| !l.trim().is_empty()))
        .unwrap_or(raw)
        .trim();
    line.trim_start_matches("ERROR:").trim().to_string()
}

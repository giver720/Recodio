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
    /// Cuándo se guardó este listado, si viene de uno anterior. La interfaz lo
    /// usa para ofrecer actualizarlo.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cached_at: Option<i64>,
    /// Quedan canciones por llegar: Spotify solo entrega 100 de una vez y el
    /// resto se está pidiendo por detrás.
    #[serde(default)]
    pub partial: bool,
    /// Identifica este análisis para casar los envíos posteriores con él.
    #[serde(default)]
    pub key: String,
    /// Algo que el usuario debe saber sobre este resultado, aunque no sea un
    /// error: por ejemplo, que el enlace pedía una lista y no vino ninguna.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub notice: Option<String>,
}

pub fn is_spotify(url: &str) -> bool {
    let u = url.to_ascii_lowercase();
    u.contains("spotify.com") || u.starts_with("spotify:")
}

/// Clave estable para el listado guardado.
///
/// En Spotify se reduce a tipo e identificador, porque el `?si=` del botón de
/// compartir y el `/intl-es/` de las URL localizadas son ruido que apunta al
/// mismo sitio. En el resto se conserva el enlace **entero**: en YouTube el
/// parámetro no es ruido, es el vídeo, y recortarlo haría que `watch?v=aaa` y
/// `watch?v=bbb` compartieran listado.
fn cache_key(url: &str) -> String {
    match spotify_ref(url) {
        Some((kind, id)) => format!("spotify:{kind}:{id}"),
        None => url.trim().trim_end_matches('/').to_string(),
    }
}

fn account_cache_key(url: &str, settings: &Settings) -> String {
    let key = cache_key(url);
    let lower = url.to_ascii_lowercase();
    let is_youtube = lower.starts_with(":yt")
        || lower.starts_with("ytsearch")
        || lower.contains("youtube.com")
        || lower.contains("youtu.be");
    if !is_youtube {
        return key;
    }

    use std::hash::{Hash, Hasher};
    let Some(identity) = settings
        .cookies_file
        .as_ref()
        .map(|path| format!("file:{}", path.to_string_lossy()))
        .or_else(|| {
            settings
                .cookies_from_browser
                .as_ref()
                .map(|browser| format!("browser:{browser}"))
        })
    else {
        return key;
    };
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    identity.hash(&mut hasher);
    format!("{key}#account:{:016x}", hasher.finish())
}

pub async fn analyze(
    url: &str,
    bins: &Binaries,
    db: &Db,
    settings: &Settings,
    refresh: bool,
) -> Result<AnalyzeResult> {
    let key = account_cache_key(url, settings);

    let mut result = match db.cache_get(&key).filter(|_| !refresh) {
        Some((payload, cached_at)) => match serde_json::from_str::<AnalyzeResult>(&payload) {
            Ok(mut guardado) => {
                guardado.cached_at = Some(cached_at);
                guardado
            }
            // Un listado guardado por una versión anterior con otro formato: se
            // descarta y se vuelve a pedir, en vez de fallar.
            Err(_) => {
                let _ = db.cache_forget(&key);
                analyze_fresh(url, bins, settings).await?
            }
        },
        None => analyze_fresh(url, bins, settings).await?,
    };

    // Una lista incompleta no se guarda: si se guardara, la siguiente vez se
    // serviría recortada creyendo que está entera.
    if result.cached_at.is_none() && !result.partial {
        if let Ok(payload) = serde_json::to_string(&result) {
            let _ = db.cache_put(&key, &payload);
        }
    }
    result.key = key.clone();

    // Los duplicados nunca se guardan: dependen de lo que haya en la biblioteca
    // *ahora*, no de cuando se analizó el enlace.

    // Los duplicados se marcan respecto al destino de *esta* descarga: si la
    // playlist es nueva, nada está repetido aunque las canciones ya existan en
    // otra playlist.
    let playlist_id = result
        .playlist
        .as_ref()
        .and_then(|pl| db.playlist_id_for(&result.source, &pl.source_id));

    marcar_duplicados(&mut result.entries, db, playlist_id.as_deref());
    Ok(result)
}

/// Marca qué entradas ya están descargadas en ese destino.
pub fn marcar_duplicados(entries: &mut [Entry], db: &Db, playlist_id: Option<&str>) {
    for entry in entries.iter_mut() {
        entry.existing_video = db
            .find_existing(&entry.extractor, &entry.source_id, "video", playlist_id)
            .map(|i| i.file_path);
        entry.existing_audio = db
            .find_existing(&entry.extractor, &entry.source_id, "audio", playlist_id)
            .map(|i| i.file_path);
    }
}

async fn analyze_ytdlp(url: &str, bins: &Binaries, settings: &Settings) -> Result<AnalyzeResult> {
    let exe = bins.require("yt-dlp")?;
    let mut cmd = async_command(exe);
    cmd.arg("--dump-single-json")
        .arg("--flat-playlist")
        // Sin esto, yt-dlp decide por su cuenta qué hacer con `watch?v=…&list=…`.
        .arg("--yes-playlist")
        .arg("--no-warnings")
        .arg("--ignore-config")
        .arg("--no-playlist-reverse");

    // Los feeds de una cuenta (historial, suscripciones, etc.) pueden contener
    // miles de elementos. La pantalla es para descubrir y elegir, no para
    // descargar de golpe toda la vida de la cuenta.
    if url.starts_with(":yt") || url == "https://www.youtube.com/feed/playlists" {
        cmd.arg("--playlist-end").arg("100");
    }

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
        // El enlace pedía una lista y ha vuelto un vídeo suelto. Decirlo es mejor
        // que enseñar un único elemento como si eso fuera todo.
        let notice = url.contains("list=").then(|| {
            "El enlace incluye una lista, pero YouTube solo ha devuelto este vídeo.              Puede que el identificador de la lista esté cortado al copiarlo, o que              la lista sea privada o ya no exista. Prueba a copiar de nuevo el enlace              desde YouTube."
                .to_string()
        });
        return Ok(AnalyzeResult {
            source: "ytdlp".into(),
            is_playlist: false,
            playlist: None,
            entries: vec![entry],
            cached_at: None,
            partial: false,
            key: String::new(),
            notice,
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
        cached_at: None,
        partial: false,
        key: String::new(),
        notice: None,
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

/// Pide el listado de verdad, sin mirar lo guardado.
async fn analyze_fresh(
    url: &str,
    bins: &Binaries,
    settings: &Settings,
) -> Result<AnalyzeResult> {
    if is_spotify(url) {
        analyze_spotify(url, bins).await
    } else {
        analyze_ytdlp(url, bins, settings).await
    }
}

/// Listado rápido leyendo la página de incrustación de Spotify.
///
/// `spotdl save` tarda unos 31 s de arranque más 4,75 s por canción — dos
/// minutos y medio para un álbum de 25, y ocho para una playlist de cien, solo
/// para *previsualizar*. Esta página devuelve la misma lista en un segundo y sin
/// autenticación. Si Spotify cambia el formato, se cae al camino de spotdl.
/// El embed corta las listas en 100 pistas. Es un tope duro, sin paginación.
const EMBED_MAX: usize = 100;

const UA: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 \
                  (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

/// Pide la playlist entera, de cien en cien, con el token que viene en el embed.
///
/// Puede fallar: el token es el de la sesión anónima del reproductor incrustado y
/// Spotify limita su uso contra la API pública. Por eso el error se propaga en
/// lugar de tratarse como lista vacía — arriba se recurre a spotDL.
async fn playlist_completa(
    cliente: &reqwest::Client,
    id: &str,
    token: &str,
) -> Result<Vec<Entry>> {
    let mut entradas = Vec::new();
    let mut desde = 0usize;

    loop {
        let url = format!(
            "https://api.spotify.com/v1/playlists/{id}/tracks\
             ?limit=100&offset={desde}\
             &fields=total,items(track(id,name,duration_ms,artists(name)))"
        );
        let respuesta = cliente.get(&url).bearer_auth(token).send().await?;

        if !respuesta.status().is_success() {
            return Err(anyhow!("Spotify respondió {}", respuesta.status()));
        }
        let pagina: Value = respuesta.json().await?;
        let items = pagina
            .get("items")
            .and_then(Value::as_array)
            .ok_or_else(|| anyhow!("respuesta inesperada de Spotify"))?;
        if items.is_empty() {
            break;
        }

        for item in items {
            let Some(track) = item.get("track").filter(|t| !t.is_null()) else {
                continue; // Pistas retiradas del catálogo.
            };
            let nombre = str_field(track, &["name"]).unwrap_or_else(|| "Pista".into());
            let artista = track
                .pointer("/artists/0/name")
                .and_then(Value::as_str)
                .map(str::to_string);
            let track_id = str_field(track, &["id"])
                .unwrap_or_else(|| format!("track-{}", entradas.len()));

            entradas.push(Entry {
                id: uuid::Uuid::new_v4().to_string(),
                source_id: track_id.clone(),
                extractor: "spotify".into(),
                title: match &artista {
                    Some(a) => format!("{a} - {nombre}"),
                    None => nombre,
                },
                url: format!("https://open.spotify.com/track/{track_id}"),
                uploader: artista,
                duration: track
                    .get("duration_ms")
                    .and_then(Value::as_f64)
                    .map(|ms| ms / 1000.0),
                thumbnail: None,
                index: entradas.len() as i64 + 1,
                existing_video: None,
                existing_audio: None,
                unavailable: false,
            });
        }

        let total = pagina.get("total").and_then(Value::as_u64).unwrap_or(0) as usize;
        desde += items.len();
        if desde >= total {
            break;
        }
    }

    Ok(entradas)
}

async fn analyze_spotify_embed(kind: &str, id: &str) -> Result<AnalyzeResult> {
    let cliente = reqwest::Client::builder().user_agent(UA).build()?;
    let body = cliente
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
            cached_at: None,
            partial: false,
            key: String::new(),
            notice: None,
        });
    }

    let tracks = tracks.unwrap();

    // Lista al tope: seguramente hay más. En vez de hacer esperar por la lista
    // completa —que puede tardar minutos— se devuelven estas cien marcadas como
    // incompletas y el resto se pide por detrás.
    let recortada = kind == "playlist" && tracks.len() >= EMBED_MAX;

    let entries: Vec<Entry> = tracks
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
        cached_at: None,
        partial: recortada,
        key: String::new(),
        notice: None,
    })
}

/// Pide la playlist entera. Es el camino lento: primero se intenta la API con el
/// token del embed y, si no puede, se recurre a spotDL, que tarda minutos pero
/// nunca se queda corto. Se llama por detrás, con las primeras cien ya en
/// pantalla.
pub async fn complete_spotify(url: &str, bins: &Binaries) -> Result<Vec<Entry>> {
    let (kind, id) = spotify_ref(url).ok_or_else(|| anyhow!("no es un enlace de Spotify"))?;
    if kind != "playlist" {
        return Err(anyhow!("solo las playlists se entregan a trozos"));
    }

    let cliente = reqwest::Client::builder().user_agent(UA).build()?;
    if let Ok(token) = token_del_embed(&cliente, &kind, &id).await {
        match playlist_completa(&cliente, &id, &token).await {
            Ok(todas) if todas.len() > EMBED_MAX => return Ok(todas),
            Ok(_) => {}
            Err(e) => eprintln!("[recodio] la vía rápida no pudo completar la lista: {e}"),
        }
    }

    // Respaldo: lento, pero devuelve la lista completa sin depender de la API.
    Ok(analyze_spotdl_cli(url, bins).await?.entries)
}

async fn token_del_embed(cliente: &reqwest::Client, kind: &str, id: &str) -> Result<String> {
    let body = cliente
        .get(format!("https://open.spotify.com/embed/{kind}/{id}"))
        .send()
        .await?
        .error_for_status()?
        .text()
        .await?;
    const OPEN: &str = r#"<script id="__NEXT_DATA__" type="application/json">"#;
    let start = body.find(OPEN).ok_or_else(|| anyhow!("sin datos"))? + OPEN.len();
    let end = body[start..].find("</script>").ok_or_else(|| anyhow!("truncado"))?;
    let json: Value = serde_json::from_str(&body[start..start + end])?;
    json.pointer("/props/pageProps/state/settings/session/accessToken")
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| anyhow!("Spotify no entregó el token de sesión"))
}

async fn analyze_spotify(url: &str, bins: &Binaries) -> Result<AnalyzeResult> {
    if let Some((kind, id)) = spotify_ref(url) {
        match analyze_spotify_embed(&kind, &id).await {
            Ok(result) => return Ok(result),
            Err(e) => eprintln!("[recodio] listado rápido de Spotify no disponible ({e}); usando spotdl"),
        }
    }
    analyze_spotdl_cli(url, bins).await
}

/// El camino lento: unos 31 s de arranque más 4,75 s por canción, pero devuelve
/// la lista completa sin depender de ninguna API.
async fn analyze_spotdl_cli(url: &str, bins: &Binaries) -> Result<AnalyzeResult> {
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
        cached_at: None,
        partial: false,
        key: String::new(),
        notice: None,
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
    fn la_clave_de_cache_ignora_el_ruido_del_enlace() {
        // El mismo álbum compartido de tres formas tiene que reutilizar la lista.
        let esperada = cache_key("https://open.spotify.com/album/7ePC9qS9mSOTY9E0YPP6yg");
        assert_eq!(
            cache_key("https://open.spotify.com/intl-es/album/7ePC9qS9mSOTY9E0YPP6yg?si=abc123"),
            esperada
        );
        assert_eq!(cache_key("spotify:album:7ePC9qS9mSOTY9E0YPP6yg"), esperada);

        // Y dos enlaces distintos no pueden compartir clave.
        assert_ne!(
            cache_key("https://www.youtube.com/watch?v=aaa"),
            cache_key("https://www.youtube.com/watch?v=bbb")
        );
    }

    #[test]
    fn los_feeds_de_youtube_se_guardan_por_cuenta() {
        let mut primera = Settings::default();
        primera.cookies_file = Some("C:/Recodio/cuenta-1.txt".into());
        let mut segunda = primera.clone();
        segunda.cookies_file = Some("C:/Recodio/cuenta-2.txt".into());

        assert_ne!(
            account_cache_key(":ytrec", &primera),
            account_cache_key(":ytrec", &segunda)
        );
        assert_ne!(
            account_cache_key("https://www.youtube.com/watch?v=abc", &primera),
            account_cache_key("https://www.youtube.com/watch?v=abc", &segunda)
        );
        assert_eq!(
            account_cache_key("https://open.spotify.com/track/abc", &primera),
            account_cache_key("https://open.spotify.com/track/abc", &segunda)
        );
    }

    /// Los duplicados dependen de lo que haya en la biblioteca *ahora*. Si se
    /// guardaran con la lista, una canción borrada seguiría figurando como ya
    /// descargada para siempre.
    #[test]
    fn el_listado_guardado_no_conserva_los_duplicados() {
        let resultado = AnalyzeResult {
            source: "spotdl".into(),
            is_playlist: false,
            playlist: None,
            entries: vec![Entry {
                id: "x".into(),
                source_id: "abc".into(),
                extractor: "spotify".into(),
                title: "Prueba".into(),
                url: String::new(),
                uploader: None,
                duration: None,
                thumbnail: None,
                index: 1,
                existing_video: Some("C:/algo.mp4".into()),
                existing_audio: Some("C:/algo.mp3".into()),
                unavailable: false,
            }],
            cached_at: None,
            partial: false,
            key: String::new(),
            notice: None,
        };

        let json = serde_json::to_string(&resultado).unwrap();
        let recuperado: AnalyzeResult = serde_json::from_str(&json).unwrap();

        // Se recuperan tal cual, pero `analyze` los recalcula antes de devolver
        // el resultado: este test documenta que hay que hacerlo.
        assert!(recuperado.entries[0].existing_audio.is_some());
        assert!(recuperado.cached_at.is_none());
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
    let lower = raw.to_ascii_lowercase();
    if lower.contains("failed to decrypt with dpapi") {
        return "Windows no permitió leer las cookies cifradas de Brave/Chrome. Importa un cookies.txt en Recodio y vuelve a intentarlo.".into();
    }
    if lower.contains("failed to load cookies") {
        return "El archivo de cookies no es válido o ya caducó. Expórtalo de nuevo en formato Netscape cookies.txt.".into();
    }
    let line = raw
        .lines()
        .rev()
        .find(|l| l.contains("ERROR:"))
        // Los errores de uso vienen de argparse y no llevan «ERROR:», sino el
        // nombre del ejecutable: «yt-dlp.exe: error: no such option: --x».
        .or_else(|| raw.lines().rev().find(|l| l.contains(": error:")))
        .or_else(|| raw.lines().rev().find(|l| !l.trim().is_empty()))
        .unwrap_or(raw)
        .trim();
    let line = line.trim_start_matches("ERROR:").trim();
    // El «yt-dlp.exe: error:» de delante no le dice nada a quien lo lee.
    match line.split_once(": error:") {
        Some((_, motivo)) => motivo.trim().to_string(),
        None => line.to_string(),
    }
}

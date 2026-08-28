mod analyze;
mod binaries;
mod core;
mod db;
mod job;
mod local;
mod m3u;
mod proc;
mod queue;
mod refresh;
mod repair;
mod settings;
mod spotify;
mod subs;
mod thumbs;
mod ytdlp;

use crate::core::Core;
use analyze::{AnalyzeResult, Entry, PlaylistInfo};
use binaries::ToolStatus;
use db::{DiscoveredSourceItem, LibraryItem, MediaSource, Playlist, StoredSourceItem};
use job::Job;
use queue::{Queue, QueueStats};
use serde::{Deserialize, Serialize};
use settings::{Settings, SourceProfile};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tauri::{Manager, State};
use tauri_plugin_opener::OpenerExt;

pub struct AppState {
    core: Arc<Core>,
    queue: Queue,
    spotify: Arc<spotify::SpotifyAuth>,
}

type CmdResult<T> = Result<T, String>;

fn err<E: std::fmt::Display>(e: E) -> String {
    e.to_string()
}

// ---------------------------------------------------------------- análisis

#[tauri::command]
async fn analyze_url(
    url: String,
    refresh: Option<bool>,
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<AnalyzeResult> {
    let core = state.core.clone();
    let settings = core.settings.read().unwrap().clone();
    let url = url.trim().to_string();

    let result = analyze::analyze(
        &url,
        &core.bins,
        &core.db,
        &settings,
        refresh.unwrap_or(false),
    )
    .await
    .map_err(err)?;

    // Spotify solo entrega 100 canciones de una vez. En vez de hacer esperar por
    // la lista completa —que puede tardar minutos— se devuelven esas cien y el
    // resto se pide por detrás: así se puede empezar a marcar y descargar ya.
    if result.partial {
        let core = core.clone();
        let key = result.key.clone();
        let playlist = result.playlist.clone();
        let fuente = result.source.clone();

        tauri::async_runtime::spawn(async move {
            use tauri::Emitter;
            match analyze::complete_spotify(&url, &core.bins).await {
                Ok(mut todas) => {
                    let playlist_id = playlist
                        .as_ref()
                        .and_then(|pl| core.db.playlist_id_for(&fuente, &pl.source_id));
                    analyze::marcar_duplicados(&mut todas, &core.db, playlist_id.as_deref());

                    // Se guarda la lista entera, ya sí, para la próxima vez.
                    let completo = AnalyzeResult {
                        source: fuente,
                        is_playlist: true,
                        playlist,
                        entries: todas.clone(),
                        cached_at: None,
                        partial: false,
                        key: key.clone(),
                        notice: None,
                    };
                    if let Ok(payload) = serde_json::to_string(&completo) {
                        let _ = core.db.cache_put(&key, &payload);
                    }

                    // Se envían todas, no solo las que faltan. Saltarse las
                    // primeras daría por hecho que el camino lento devuelve el
                    // mismo orden que el rápido, y si spotDL entrega otro orden
                    // se perderían canciones. La interfaz descarta las repetidas
                    // por su identificador, que sí es fiable.
                    let _ = app.emit("analyze-more", (key, todas, true));
                }
                Err(e) => {
                    let _ = app.emit("analyze-failed", (key, e.to_string()));
                }
            }
        });
    }

    Ok(result)
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct YoutubeSessionStatus {
    connected: bool,
    message: String,
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct YoutubeAccount {
    id: String,
    name: String,
    cookies_file: PathBuf,
    created_at: u64,
}

#[derive(Debug, Default, serde::Serialize, serde::Deserialize)]
#[serde(default)]
struct YoutubeAccountRegistry {
    accounts: Vec<YoutubeAccount>,
}

fn youtube_config_dir(core: &Core) -> CmdResult<&Path> {
    core.settings_path
        .parent()
        .ok_or_else(|| "No se encontró la carpeta de configuración de Recodio".into())
}

fn youtube_account_name(name: String) -> CmdResult<String> {
    let name = name.trim();
    if name.is_empty() {
        return Err("Escribe un nombre para reconocer esta cuenta".into());
    }
    if name.chars().count() > 60 || name.chars().any(char::is_control) {
        return Err("El nombre de la cuenta debe tener entre 1 y 60 caracteres".into());
    }
    Ok(name.to_string())
}

fn validate_youtube_cookies(data: &[u8]) -> CmdResult<()> {
    const MAX_COOKIE_FILE: usize = 10 * 1024 * 1024;
    if data.is_empty() || data.len() > MAX_COOKIE_FILE {
        return Err("El archivo de cookies está vacío o es demasiado grande".into());
    }

    let text = String::from_utf8_lossy(data);
    let header = text.lines().next().unwrap_or_default().trim();
    if header != "# Netscape HTTP Cookie File" && header != "# HTTP Cookie File" {
        return Err(
            "El archivo debe estar en formato Netscape cookies.txt (no JSON ni CSV)".into(),
        );
    }
    let lower = text.to_ascii_lowercase();
    if !lower.contains("youtube.com") && !lower.contains("google.com") {
        return Err("El archivo no contiene cookies de YouTube".into());
    }
    Ok(())
}

fn save_youtube_accounts(core: &Core, registry: &YoutubeAccountRegistry) -> CmdResult<()> {
    let config_dir = youtube_config_dir(core)?;
    std::fs::create_dir_all(config_dir).map_err(err)?;
    let path = config_dir.join("youtube-accounts.json");
    let temp = config_dir.join("youtube-accounts.json.new");
    std::fs::write(&temp, serde_json::to_vec_pretty(registry).map_err(err)?).map_err(err)?;
    if path.exists() {
        std::fs::remove_file(&path).map_err(err)?;
    }
    std::fs::rename(&temp, &path).map_err(err)
}

fn load_youtube_accounts(core: &Core) -> CmdResult<YoutubeAccountRegistry> {
    let config_dir = youtube_config_dir(core)?;
    let path = config_dir.join("youtube-accounts.json");
    let mut registry = if path.exists() {
        let raw = std::fs::read_to_string(&path).map_err(err)?;
        serde_json::from_str(&raw)
            .map_err(|_| "No se pudo leer la lista de cuentas de YouTube".to_string())?
    } else {
        YoutubeAccountRegistry::default()
    };

    let previous_len = registry.accounts.len();
    registry
        .accounts
        .retain(|account| account.cookies_file.is_file());
    let mut changed = registry.accounts.len() != previous_len;

    // La versión 0.5.0 guardaba una única cuenta con este nombre. Se registra
    // automáticamente para que la actualización no obligue a importarla otra vez.
    let legacy = config_dir.join("youtube-cookies.txt");
    if legacy.is_file()
        && !registry
            .accounts
            .iter()
            .any(|account| account.cookies_file == legacy)
    {
        let created_at = legacy
            .metadata()
            .and_then(|m| m.modified())
            .ok()
            .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|duration| duration.as_secs())
            .unwrap_or_default();
        registry.accounts.push(YoutubeAccount {
            id: "legacy-import".into(),
            name: "Cuenta importada".into(),
            cookies_file: legacy,
            created_at,
        });
        changed = true;
    }

    registry
        .accounts
        .sort_by_key(|account| (account.created_at, account.name.to_ascii_lowercase()));
    if changed {
        save_youtube_accounts(core, &registry)?;
    }
    Ok(registry)
}

#[tauri::command]
fn youtube_accounts_list(state: State<'_, AppState>) -> CmdResult<Vec<YoutubeAccount>> {
    Ok(load_youtube_accounts(&state.core)?.accounts)
}

/// Comprueba de verdad que yt-dlp puede leer la cuenta elegida. Guardar el
/// nombre de un navegador no significa que haya una sesión de YouTube dentro.
#[tauri::command]
async fn youtube_session_check(
    browser: Option<String>,
    cookies_file: Option<PathBuf>,
    state: State<'_, AppState>,
) -> CmdResult<YoutubeSessionStatus> {
    const NAVEGADORES: &[&str] = &[
        "brave", "chrome", "chromium", "edge", "firefox", "opera", "safari", "vivaldi", "whale",
    ];

    let exe = state.core.bins.require("yt-dlp").map_err(err)?;
    let mut settings = state.core.settings.read().unwrap().clone();
    match (
        browser
            .map(|b| b.trim().to_string())
            .filter(|b| !b.is_empty()),
        cookies_file.filter(|p| p.is_file()),
    ) {
        (Some(browser), None) => {
            let nombre = browser.split([':', '+']).next().unwrap_or("");
            if !NAVEGADORES.contains(&nombre) {
                return Err("El navegador elegido no es compatible con yt-dlp".into());
            }
            settings.cookies_from_browser = Some(browser);
            settings.cookies_file = None;
        }
        (None, Some(file)) => {
            settings.cookies_from_browser = None;
            settings.cookies_file = Some(file);
        }
        (Some(_), Some(_)) => {
            return Err("Elige un navegador o un archivo de cookies, no ambos".into());
        }
        (None, None) => {
            return Err("Elige un navegador o importa un archivo cookies.txt".into());
        }
    }

    let mut cmd = proc::async_command(exe);
    cmd.arg("--ignore-config")
        .arg("--flat-playlist")
        .arg("--playlist-end")
        .arg("1")
        .arg("--dump-single-json")
        .arg("--no-warnings");
    ytdlp::apply_access_args(&mut cmd, &settings);
    cmd.arg(":ytfav");

    let output = cmd.output().await.map_err(err)?;
    if output.status.success() && !output.stdout.is_empty() {
        return Ok(YoutubeSessionStatus {
            connected: true,
            message: "Sesión de YouTube verificada".into(),
        });
    }

    let raw = String::from_utf8_lossy(&output.stderr);
    let raw_lower = raw.to_ascii_lowercase();
    let cleaned = analyze::clean_ytdlp_error(&raw);
    let message = if cleaned.is_empty() {
        "No se encontró una sesión de YouTube activa en ese navegador".into()
    } else if raw_lower.contains("failed to decrypt with dpapi") {
        "Brave, Chrome y Edge protegen sus cookies en Windows y no permiten que Recodio las descifre. Usa Firefox o pulsa «Importar cookies.txt».".into()
    } else if raw_lower.contains("could not copy") || raw_lower.contains("database") {
        "Cierra completamente el navegador y vuelve a comprobar la cuenta".into()
    } else if raw_lower.contains("failed to load cookies") {
        "El archivo de cookies no es válido o ya caducó. Expórtalo de nuevo en formato Netscape cookies.txt".into()
    } else {
        cleaned
    };

    Ok(YoutubeSessionStatus {
        connected: false,
        message,
    })
}

/// Copia un `cookies.txt` a la carpeta privada de la cuenta indicada. Cada
/// importación usa un archivo diferente, así que cambiar de cuenta no destruye
/// la sesión que estaba guardada antes. El contenido nunca vuelve al webview.
#[tauri::command]
fn youtube_import_cookies(
    source: PathBuf,
    name: String,
    state: State<'_, AppState>,
) -> CmdResult<YoutubeAccount> {
    const MAX_COOKIE_FILE: u64 = 10 * 1024 * 1024;

    let metadata = std::fs::metadata(&source)
        .map_err(|_| "No se pudo abrir el archivo de cookies seleccionado".to_string())?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_COOKIE_FILE {
        return Err("El archivo de cookies está vacío o es demasiado grande".into());
    }

    let data = std::fs::read(&source).map_err(err)?;
    validate_youtube_cookies(&data)?;
    let name = youtube_account_name(name)?;

    let mut registry = load_youtube_accounts(&state.core)?;
    if registry
        .accounts
        .iter()
        .any(|account| account.name.eq_ignore_ascii_case(&name))
    {
        return Err("Ya existe una cuenta guardada con ese nombre".into());
    }

    let config_dir = youtube_config_dir(&state.core)?;
    let accounts_dir = config_dir.join("youtube-accounts");
    std::fs::create_dir_all(&accounts_dir).map_err(err)?;
    let created_at = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(err)?;
    let id = format!("youtube-{:x}", created_at.as_nanos());
    let dest = accounts_dir.join(format!("{id}.txt"));
    let temp = accounts_dir.join(format!("{id}.txt.new"));
    std::fs::write(&temp, data).map_err(err)?;

    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&temp, std::fs::Permissions::from_mode(0o600)).map_err(err)?;
    }

    std::fs::rename(&temp, &dest).map_err(err)?;

    let account = YoutubeAccount {
        id,
        name,
        cookies_file: dest.clone(),
        created_at: created_at.as_secs(),
    };
    registry.accounts.push(account.clone());
    if let Err(error) = save_youtube_accounts(&state.core, &registry) {
        let _ = std::fs::remove_file(dest);
        return Err(error);
    }
    Ok(account)
}

#[tauri::command]
fn youtube_account_rename(
    id: String,
    name: String,
    state: State<'_, AppState>,
) -> CmdResult<YoutubeAccount> {
    let name = youtube_account_name(name)?;
    let mut registry = load_youtube_accounts(&state.core)?;
    if registry
        .accounts
        .iter()
        .any(|account| account.id != id && account.name.eq_ignore_ascii_case(&name))
    {
        return Err("Ya existe una cuenta guardada con ese nombre".into());
    }
    let account = registry
        .accounts
        .iter_mut()
        .find(|account| account.id == id)
        .ok_or_else(|| "La cuenta de YouTube ya no existe".to_string())?;
    account.name = name;
    let updated = account.clone();
    save_youtube_accounts(&state.core, &registry)?;
    Ok(updated)
}

#[tauri::command]
fn youtube_account_delete(id: String, state: State<'_, AppState>) -> CmdResult<()> {
    let mut registry = load_youtube_accounts(&state.core)?;
    let index = registry
        .accounts
        .iter()
        .position(|account| account.id == id)
        .ok_or_else(|| "La cuenta de YouTube ya no existe".to_string())?;
    let account = registry.accounts.remove(index);

    let config_dir = youtube_config_dir(&state.core)?;
    let managed_dir = config_dir.join("youtube-accounts");
    let legacy = config_dir.join("youtube-cookies.txt");
    let managed = account.cookies_file.parent() == Some(managed_dir.as_path())
        || account.cookies_file == legacy;
    if !managed {
        return Err("Recodio se negó a borrar un archivo que no administra".into());
    }
    if account.cookies_file.exists() {
        std::fs::remove_file(&account.cookies_file).map_err(err)?;
    }
    save_youtube_accounts(&state.core, &registry)?;

    let active =
        state.core.settings.read().unwrap().cookies_file.as_ref() == Some(&account.cookies_file);
    if active {
        state.core.settings.write().unwrap().cookies_file = None;
        state.core.save_settings().map_err(err)?;
    }
    Ok(())
}

#[tauri::command]
fn youtube_open_login(browser: Option<String>, app: tauri::AppHandle) -> CmdResult<()> {
    const URL: &str = "https://accounts.google.com/ServiceLogin?service=youtube";
    let Some(browser) = browser.filter(|b| !b.trim().is_empty()) else {
        return app.opener().open_url(URL, None::<&str>).map_err(err);
    };
    let name = browser.split([':', '+']).next().unwrap_or("");
    let program = browser_program(name)
        .ok_or_else(|| format!("No se encontró {name} instalado en este equipo"))?;
    app.opener().open_url(URL, Some(program)).map_err(err)
}

fn browser_program(browser: &str) -> Option<String> {
    let executable = match browser {
        "chrome" => "chrome",
        "edge" => "msedge",
        "firefox" => "firefox",
        "brave" => "brave",
        "opera" => "opera",
        "vivaldi" => "vivaldi",
        "chromium" => "chromium",
        _ => return None,
    };
    if let Ok(path) = which::which(executable) {
        return Some(path.to_string_lossy().into_owned());
    }

    #[cfg(windows)]
    {
        let local = std::env::var_os("LOCALAPPDATA").map(PathBuf::from);
        let program_files = std::env::var_os("ProgramFiles").map(PathBuf::from);
        let program_files_x86 = std::env::var_os("ProgramFiles(x86)").map(PathBuf::from);
        let relative: &[&str] = match browser {
            "chrome" => &[r"Google\Chrome\Application\chrome.exe"],
            "edge" => &[r"Microsoft\Edge\Application\msedge.exe"],
            "firefox" => &[r"Mozilla Firefox\firefox.exe"],
            "brave" => &[r"BraveSoftware\Brave-Browser\Application\brave.exe"],
            "opera" => &[r"Programs\Opera\launcher.exe"],
            "vivaldi" => &[r"Vivaldi\Application\vivaldi.exe"],
            "chromium" => &[r"Chromium\Application\chrome.exe"],
            _ => &[],
        };
        for base in [local, program_files, program_files_x86]
            .into_iter()
            .flatten()
        {
            for rel in relative {
                let candidate = base.join(rel);
                if candidate.is_file() {
                    return Some(candidate.to_string_lossy().into_owned());
                }
            }
        }
    }

    None
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct SpotifySessionStatus {
    connected: bool,
    profile: Option<spotify::SpotifyProfile>,
    message: String,
}

#[tauri::command]
async fn spotify_status(state: State<'_, AppState>) -> CmdResult<SpotifySessionStatus> {
    if !state.spotify.has_session().await {
        return Ok(SpotifySessionStatus {
            connected: false,
            profile: None,
            message: "Conecta tu cuenta para ver tu música de Spotify".into(),
        });
    }
    match state.spotify.profile().await {
        Ok(profile) => Ok(SpotifySessionStatus {
            connected: true,
            profile: Some(profile),
            message: "Sesión de Spotify activa".into(),
        }),
        Err(error) => Ok(SpotifySessionStatus {
            connected: false,
            profile: None,
            message: error.to_string(),
        }),
    }
}

#[tauri::command]
async fn spotify_login(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<spotify::SpotifyProfile> {
    state.spotify.login(&app).await.map_err(err)
}

#[tauri::command]
async fn spotify_logout(state: State<'_, AppState>) -> CmdResult<()> {
    state.spotify.logout().await;
    Ok(())
}

#[tauri::command]
async fn spotify_playlists(state: State<'_, AppState>) -> CmdResult<Vec<spotify::SpotifyPlaylist>> {
    state.spotify.playlists().await.map_err(err)
}

fn spotify_analyze_result(
    tracks: Vec<spotify::SpotifyTrack>,
    title: String,
    source_id: String,
    url: String,
    state: &AppState,
) -> AnalyzeResult {
    let playlist_id = state.core.db.playlist_id_for("spotdl", &source_id);
    let thumbnail = tracks.first().and_then(|track| track.image_url.clone());
    let mut entries = tracks
        .into_iter()
        .enumerate()
        .map(|(index, track)| Entry {
            id: uuid::Uuid::new_v4().to_string(),
            source_id: track.id,
            extractor: "spotify".into(),
            title: if track.artists.is_empty() {
                track.name
            } else {
                format!("{} - {}", track.artists, track.name)
            },
            url: track.external_url,
            uploader: (!track.artists.is_empty()).then_some(track.artists),
            duration: track.duration,
            thumbnail: track.image_url,
            index: index as i64 + 1,
            existing_video: None,
            existing_audio: None,
            unavailable: false,
            live_status: None,
            release_timestamp: None,
        })
        .collect::<Vec<_>>();
    analyze::marcar_duplicados(&mut entries, &state.core.db, playlist_id.as_deref());
    AnalyzeResult {
        source: "spotdl".into(),
        is_playlist: true,
        playlist: Some(PlaylistInfo {
            source_id,
            title,
            url,
            uploader: Some("Spotify".into()),
            thumbnail,
        }),
        entries,
        cached_at: None,
        partial: false,
        key: String::new(),
        notice: None,
    }
}

#[tauri::command]
async fn spotify_collection(
    collection: String,
    state: State<'_, AppState>,
) -> CmdResult<AnalyzeResult> {
    let profile = state.spotify.profile().await.map_err(err)?;
    let (tracks, label, key) = match collection.as_str() {
        "saved" => (
            state.spotify.saved_tracks().await,
            "Canciones que te gustan",
            "saved",
        ),
        "top" => (
            state.spotify.top_tracks().await,
            "Más escuchadas para ti",
            "top",
        ),
        "recent" => (
            state.spotify.recent_tracks().await,
            "Escuchado recientemente",
            "recent",
        ),
        _ => return Err("Colección de Spotify desconocida".into()),
    };
    Ok(spotify_analyze_result(
        tracks.map_err(err)?,
        label.into(),
        format!("spotify:{}:{key}", profile.id),
        "https://open.spotify.com/collection".into(),
        &state,
    ))
}

#[tauri::command]
async fn spotify_playlist(
    id: String,
    name: String,
    url: String,
    state: State<'_, AppState>,
) -> CmdResult<AnalyzeResult> {
    let tracks = state.spotify.playlist_tracks(&id).await.map_err(err)?;
    Ok(spotify_analyze_result(tracks, name, id, url, &state))
}

// ----------------------------------------------------------------- fuentes

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct MediaSourceItem {
    remote_id: String,
    status: String,
    first_seen_at: i64,
    last_seen_at: i64,
    present: bool,
    entry: Entry,
}

const SOURCE_INTERVALS: [i64; 7] = [15, 60, 360, 720, 1440, 4320, 10080];

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MediaSourcesBackup {
    version: u32,
    exported_at: i64,
    sources: Vec<MediaSource>,
}

async fn analyze_complete_source(
    url: &str,
    core: &Core,
    profile: Option<&SourceProfile>,
) -> anyhow::Result<AnalyzeResult> {
    let mut settings = core.settings.read().unwrap().clone();
    if let Some(profile) = profile {
        profile.apply_to(&mut settings);
    }
    let mut result = analyze::analyze(url, &core.bins, &core.db, &settings, true).await?;
    if result.partial {
        result.entries = analyze::complete_spotify(url, &core.bins).await?;
        let playlist_id = result
            .playlist
            .as_ref()
            .and_then(|pl| core.db.playlist_id_for(&result.source, &pl.source_id));
        analyze::marcar_duplicados(&mut result.entries, &core.db, playlist_id.as_deref());
        result.partial = false;
    }
    if !result.is_playlist || result.playlist.is_none() {
        anyhow::bail!("Las Fuentes deben ser un canal, una playlist, un álbum o una colección; no un elemento individual");
    }
    Ok(result)
}

fn persist_source_result(
    core: &Core,
    id: &str,
    result: &AnalyzeResult,
) -> anyhow::Result<MediaSource> {
    let playlist = result.playlist.as_ref().expect("validado antes de guardar");
    let playlist_id = core.db.playlist_id_for(&result.source, &playlist.source_id);
    let items: Vec<DiscoveredSourceItem> = result
        .entries
        .iter()
        .map(|entry| DiscoveredSourceItem {
            extractor: entry.extractor.clone(),
            remote_id: entry.source_id.clone(),
            title: entry.title.clone(),
            url: entry.url.clone(),
            uploader: entry.uploader.clone(),
            duration: entry.duration,
            thumbnail: entry.thumbnail.clone(),
            position: entry.index,
            unavailable: entry.unavailable,
            live_status: entry.live_status.clone(),
            release_timestamp: entry.release_timestamp,
            already_downloaded: core
                .db
                .find_existing(
                    &entry.extractor,
                    &entry.source_id,
                    "video",
                    playlist_id.as_deref(),
                )
                .is_some()
                || core
                    .db
                    .find_existing(
                        &entry.extractor,
                        &entry.source_id,
                        "audio",
                        playlist_id.as_deref(),
                    )
                    .is_some(),
        })
        .collect();
    core.db.apply_source_discovery(
        id,
        &playlist.title,
        playlist.uploader.as_deref(),
        playlist.thumbnail.as_deref(),
        &playlist.source_id,
        &items,
    )?;
    core.db
        .media_source(id)?
        .ok_or_else(|| anyhow::anyhow!("La Fuente desapareció mientras se actualizaba"))
}

#[tauri::command]
fn media_sources_list(state: State<'_, AppState>) -> CmdResult<Vec<MediaSource>> {
    state.core.db.list_media_sources().map_err(err)
}

#[tauri::command]
async fn media_source_add(
    url: String,
    media_kind: String,
    state: State<'_, AppState>,
) -> CmdResult<MediaSource> {
    if !matches!(media_kind.as_str(), "video" | "audio") {
        return Err("El tipo de descarga de la Fuente no es válido".into());
    }
    let url = url.trim().to_string();
    if url.is_empty() {
        return Err("Pega el enlace de un canal o una playlist".into());
    }
    if let Some(existing) = state.core.db.media_source_by_url(&url).map_err(err)? {
        return Ok(existing);
    }

    let result = analyze_complete_source(&url, &state.core, None)
        .await
        .map_err(err)?;
    let playlist = result.playlist.as_ref().unwrap();
    let source = MediaSource {
        id: uuid::Uuid::new_v4().to_string(),
        url,
        source: result.source.clone(),
        source_id: playlist.source_id.clone(),
        title: playlist.title.clone(),
        uploader: playlist.uploader.clone(),
        thumbnail: playlist.thumbnail.clone(),
        media_kind,
        created_at: chrono::Utc::now().timestamp(),
        last_checked_at: None,
        last_success_at: None,
        last_error: None,
        total_items: 0,
        new_items: 0,
        profile: SourceProfile::default(),
        check_interval_minutes: None,
        auto_download: false,
    };
    let id = state.core.db.upsert_media_source(&source).map_err(err)?;
    persist_source_result(&state.core, &id, &result).map_err(err)
}

#[tauri::command]
async fn media_source_sync(id: String, state: State<'_, AppState>) -> CmdResult<MediaSource> {
    let source = state
        .core
        .db
        .media_source(&id)
        .map_err(err)?
        .ok_or_else(|| "La Fuente ya no existe".to_string())?;
    match analyze_complete_source(&source.url, &state.core, Some(&source.profile)).await {
        Ok(result) => persist_source_result(&state.core, &id, &result).map_err(err),
        Err(error) => {
            let message = error.to_string();
            let _ = state.core.db.update_media_source_failure(&id, &message);
            Err(message)
        }
    }
}

fn source_item_to_entry(
    core: &Core,
    source: &MediaSource,
    item: StoredSourceItem,
) -> MediaSourceItem {
    let playlist_id = core.db.playlist_id_for(&source.source, &source.source_id);
    let existing_video = core
        .db
        .find_existing(
            &item.extractor,
            &item.remote_id,
            "video",
            playlist_id.as_deref(),
        )
        .map(|i| i.file_path);
    let existing_audio = core
        .db
        .find_existing(
            &item.extractor,
            &item.remote_id,
            "audio",
            playlist_id.as_deref(),
        )
        .map(|i| i.file_path);
    let downloaded = if source.media_kind == "audio" {
        existing_audio.is_some()
    } else {
        existing_video.is_some()
    };
    let unavailable = item.status == "unavailable" || !item.present;
    MediaSourceItem {
        remote_id: item.remote_id.clone(),
        status: if downloaded {
            "downloaded".into()
        } else {
            item.status
        },
        first_seen_at: item.first_seen_at,
        last_seen_at: item.last_seen_at,
        present: item.present,
        entry: Entry {
            id: uuid::Uuid::new_v4().to_string(),
            source_id: item.remote_id,
            extractor: item.extractor,
            title: item.title,
            url: item.url,
            uploader: item.uploader,
            duration: item.duration,
            thumbnail: item.thumbnail,
            index: item.position,
            existing_video,
            existing_audio,
            unavailable,
            live_status: item.live_status,
            release_timestamp: item.release_timestamp,
        },
    }
}

#[tauri::command]
fn media_source_items(id: String, state: State<'_, AppState>) -> CmdResult<Vec<MediaSourceItem>> {
    let source = state
        .core
        .db
        .media_source(&id)
        .map_err(err)?
        .ok_or_else(|| "La Fuente ya no existe".to_string())?;
    state
        .core
        .db
        .media_source_items(&id)
        .map(|items| {
            items
                .into_iter()
                .map(|item| source_item_to_entry(&state.core, &source, item))
                .collect()
        })
        .map_err(err)
}

#[tauri::command]
fn media_source_mark_seen(
    id: String,
    remote_ids: Vec<String>,
    state: State<'_, AppState>,
) -> CmdResult<()> {
    state
        .core
        .db
        .mark_media_source_items_seen(&id, &remote_ids)
        .map_err(err)
}

fn valid_profile_choice(value: &Option<String>, choices: &[&str]) -> bool {
    value
        .as_deref()
        .map(|value| choices.contains(&value))
        .unwrap_or(true)
}

#[tauri::command]
fn media_source_update_profile(
    id: String,
    media_kind: String,
    mut profile: SourceProfile,
    state: State<'_, AppState>,
) -> CmdResult<MediaSource> {
    if !matches!(media_kind.as_str(), "video" | "audio") {
        return Err("El tipo de descarga no es válido".into());
    }
    if !valid_profile_choice(
        &profile.video_quality,
        &["best", "2160", "1440", "1080", "720", "480", "360"],
    ) || !valid_profile_choice(
        &profile.video_container,
        &["original", "mp4", "mkv", "webm"],
    ) || !valid_profile_choice(
        &profile.audio_format,
        &["mp3", "m4a", "opus", "flac", "wav"],
    ) || !valid_profile_choice(&profile.audio_bitrate, &["320", "256", "192", "160", "128"])
    {
        return Err("El perfil contiene un formato o una calidad no válidos".into());
    }

    profile.dest_dir = profile.dest_dir.and_then(|value| {
        let value = value.trim().to_string();
        (!value.is_empty()).then_some(value)
    });
    profile.subtitle_langs = profile.subtitle_langs.and_then(|value| {
        let value = value.trim().to_string();
        (!value.is_empty()).then_some(value)
    });
    if let Some(cookies_file) = &profile.youtube_cookies_file {
        let known = load_youtube_accounts(&state.core)?
            .accounts
            .into_iter()
            .any(|account| account.cookies_file.to_string_lossy() == cookies_file.as_str());
        if !known {
            return Err("La cuenta de YouTube elegida ya no está disponible".into());
        }
    }

    state
        .core
        .db
        .update_media_source_profile(&id, &media_kind, &profile)
        .map_err(err)?;
    state
        .core
        .db
        .media_source(&id)
        .map_err(err)?
        .ok_or_else(|| "La Fuente ya no existe".into())
}

#[tauri::command]
fn media_source_update_schedule(
    id: String,
    interval_minutes: Option<i64>,
    auto_download: bool,
    state: State<'_, AppState>,
) -> CmdResult<MediaSource> {
    if interval_minutes
        .map(|minutes| !SOURCE_INTERVALS.contains(&minutes))
        .unwrap_or(false)
    {
        return Err("El intervalo de comprobación no es válido".into());
    }
    if auto_download && interval_minutes.is_none() {
        return Err("Activa una comprobación periódica antes de descargar automáticamente".into());
    }
    state
        .core
        .db
        .update_media_source_schedule(&id, interval_minutes, auto_download)
        .map_err(err)?;
    state
        .core
        .db
        .media_source(&id)
        .map_err(err)?
        .ok_or_else(|| "La Fuente ya no existe".into())
}

fn sanitized_sources_backup(mut sources: Vec<MediaSource>) -> MediaSourcesBackup {
    for source in &mut sources {
        // Una ruta de cookies identifica una cuenta local y no debe salir en un
        // respaldo compartible. Al importar se hereda la cuenta activa.
        source.profile.youtube_cookies_file = None;
        source.last_error = None;
    }
    MediaSourcesBackup {
        version: 1,
        exported_at: chrono::Utc::now().timestamp(),
        sources,
    }
}

#[tauri::command]
fn media_sources_export(path: String, state: State<'_, AppState>) -> CmdResult<usize> {
    let sources = state.core.db.list_media_sources().map_err(err)?;
    let count = sources.len();
    let backup = sanitized_sources_backup(sources);
    let path = PathBuf::from(path);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(err)?;
    }
    std::fs::write(&path, serde_json::to_vec_pretty(&backup).map_err(err)?).map_err(err)?;
    Ok(count)
}

#[tauri::command]
fn media_sources_import(path: String, state: State<'_, AppState>) -> CmdResult<usize> {
    const MAX_BACKUP_SIZE: u64 = 10 * 1024 * 1024;
    let path = PathBuf::from(path);
    let metadata = std::fs::metadata(&path).map_err(err)?;
    if !metadata.is_file() || metadata.len() > MAX_BACKUP_SIZE {
        return Err("El respaldo no es un archivo válido o es demasiado grande".into());
    }
    let raw = std::fs::read(&path).map_err(err)?;
    let backup: MediaSourcesBackup = serde_json::from_slice(&raw)
        .map_err(|_| "El archivo no es un respaldo de Fuentes válido".to_string())?;
    if backup.version != 1 || backup.sources.len() > 5_000 {
        return Err("La versión o el tamaño del respaldo no es compatible".into());
    }

    let mut imported = 0;
    for mut source in backup.sources {
        source.url = source.url.trim().to_string();
        source.title = source.title.trim().to_string();
        if source.url.is_empty()
            || source.title.is_empty()
            || !matches!(source.source.as_str(), "ytdlp" | "spotdl")
            || !matches!(source.media_kind.as_str(), "video" | "audio")
            || source
                .check_interval_minutes
                .map(|minutes| !SOURCE_INTERVALS.contains(&minutes))
                .unwrap_or(false)
            || !valid_profile_choice(
                &source.profile.video_quality,
                &["best", "2160", "1440", "1080", "720", "480", "360"],
            )
            || !valid_profile_choice(
                &source.profile.video_container,
                &["original", "mp4", "mkv", "webm"],
            )
            || !valid_profile_choice(
                &source.profile.audio_format,
                &["mp3", "m4a", "opus", "flac", "wav"],
            )
            || !valid_profile_choice(
                &source.profile.audio_bitrate,
                &["320", "256", "192", "160", "128"],
            )
        {
            continue;
        }
        source.profile.youtube_cookies_file = None;
        source.auto_download &= source.check_interval_minutes.is_some();

        let id = if let Some(existing) = state
            .core
            .db
            .media_source_by_url(&source.url)
            .map_err(err)?
        {
            existing.id
        } else {
            source.id = uuid::Uuid::new_v4().to_string();
            source.created_at = chrono::Utc::now().timestamp();
            source.last_checked_at = None;
            source.last_success_at = None;
            source.last_error = None;
            source.total_items = 0;
            source.new_items = 0;
            state.core.db.upsert_media_source(&source).map_err(err)?
        };
        state
            .core
            .db
            .update_media_source_profile(&id, &source.media_kind, &source.profile)
            .map_err(err)?;
        state
            .core
            .db
            .update_media_source_schedule(&id, source.check_interval_minutes, source.auto_download)
            .map_err(err)?;
        imported += 1;
    }
    Ok(imported)
}

#[tauri::command]
fn media_source_delete(id: String, state: State<'_, AppState>) -> CmdResult<()> {
    state.core.db.delete_media_source(&id).map_err(err)
}

// ------------------------------------------------------------------- cola

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct EnqueueRequest {
    entries: Vec<Entry>,
    /// `video` o `audio`.
    kind: String,
    /// `ytdlp` o `spotdl`.
    source: String,
    /// Carpeta elegida por el usuario; si falta se usa la de ajustes.
    dest_dir: Option<String>,
    playlist: Option<PlaylistInfo>,
    /// Ids de entradas que deben reemplazar el archivo existente.
    #[serde(default)]
    overwrite_ids: Vec<String>,
    #[serde(default)]
    profile: Option<SourceProfile>,
}

#[tauri::command]
fn enqueue(req: EnqueueRequest, state: State<'_, AppState>) -> CmdResult<usize> {
    enqueue_request(req, &state.core, &state.queue)
}

fn enqueue_request(req: EnqueueRequest, core: &Core, queue: &Queue) -> CmdResult<usize> {
    let settings = core.settings.read().unwrap().clone();

    let base = match req.dest_dir.as_deref().filter(|d| !d.trim().is_empty()) {
        Some(d) => PathBuf::from(d),
        None => {
            if req.kind == "audio" {
                settings.audio_dir.clone()
            } else {
                settings.video_dir.clone()
            }
        }
    };

    let (dest, playlist_id, playlist_title) = match &req.playlist {
        Some(pl) => {
            let id = core
                .db
                .playlist_id_for(&req.source, &pl.source_id)
                .unwrap_or_else(|| uuid::Uuid::new_v4().to_string());
            let row = Playlist {
                id: id.clone(),
                source: req.source.clone(),
                source_id: pl.source_id.clone(),
                url: pl.url.clone(),
                title: pl.title.clone(),
                uploader: pl.uploader.clone(),
                thumbnail: pl.thumbnail.clone(),
                created_at: chrono::Utc::now().timestamp(),
                item_count: 0,
            };
            core.db.upsert_playlist(&row).map_err(err)?;

            let dir = if settings.playlist_subfolder {
                base.join(m3u::sanitize(&pl.title))
            } else {
                base.clone()
            };
            (dir, Some(id), Some(pl.title.clone()))
        }
        None => (base, None, None),
    };

    std::fs::create_dir_all(&dest).map_err(err)?;
    let dest_str = dest.to_string_lossy().into_owned();

    // En una playlist grande, comprobar cada entrada contra un Vec de miles de
    // ids era cuadrático. El conjunto mantiene rápida incluso la opción de
    // sobrescribir una playlist completa.
    let overwrite_ids: HashSet<String> = req.overwrite_ids.into_iter().collect();
    let jobs: Vec<Job> = req
        .entries
        .into_iter()
        .map(|entry| {
            let overwrite = overwrite_ids.contains(&entry.id);
            Job::new(
                entry,
                req.kind.clone(),
                req.source.clone(),
                dest_str.clone(),
                overwrite,
                playlist_id.clone(),
                playlist_title.clone(),
                req.profile.clone(),
            )
        })
        .collect();

    let count = jobs.len();
    queue.add(jobs);
    Ok(count)
}

fn source_check_due(source: &MediaSource, now: i64) -> bool {
    source
        .check_interval_minutes
        .filter(|minutes| *minutes > 0)
        .map(|minutes| {
            let previous = source.last_checked_at.unwrap_or(source.created_at);
            previous.saturating_add(minutes.saturating_mul(60)) <= now
        })
        .unwrap_or(false)
}

async fn run_scheduled_source(
    source: MediaSource,
    core: &Core,
    queue: &Queue,
) -> anyhow::Result<usize> {
    let result = analyze_complete_source(&source.url, core, Some(&source.profile)).await?;
    let updated = persist_source_result(core, &source.id, &result)?;
    if !updated.auto_download {
        return Ok(0);
    }

    let candidates: Vec<MediaSourceItem> = core
        .db
        .media_source_items(&updated.id)?
        .into_iter()
        .map(|item| source_item_to_entry(core, &updated, item))
        .filter(|item| {
            item.status == "new"
                && item.present
                && !item.entry.unavailable
                && item.entry.live_status.as_deref() != Some("is_upcoming")
        })
        .collect();
    if candidates.is_empty() {
        return Ok(0);
    }

    let remote_ids: Vec<String> = candidates
        .iter()
        .map(|item| item.remote_id.clone())
        .collect();
    let request = EnqueueRequest {
        entries: candidates.into_iter().map(|item| item.entry).collect(),
        kind: updated.media_kind.clone(),
        source: updated.source.clone(),
        dest_dir: updated.profile.dest_dir.clone(),
        playlist: Some(PlaylistInfo {
            source_id: updated.source_id.clone(),
            title: updated.title.clone(),
            url: updated.url.clone(),
            uploader: updated.uploader.clone(),
            thumbnail: updated.thumbnail.clone(),
        }),
        overwrite_ids: Vec::new(),
        profile: Some(updated.profile.clone()),
    };
    let count = enqueue_request(request, core, queue).map_err(anyhow::Error::msg)?;
    core.db
        .mark_media_source_items_seen(&updated.id, &remote_ids)?;
    Ok(count)
}

async fn source_scheduler(core: Arc<Core>, queue: Queue, app: tauri::AppHandle) {
    use tauri::Emitter;

    // Da tiempo a que abra la ventana y a que termine el rastreo inicial.
    tokio::time::sleep(std::time::Duration::from_secs(10)).await;
    loop {
        let now = chrono::Utc::now().timestamp();
        let due = core
            .db
            .list_media_sources()
            .unwrap_or_default()
            .into_iter()
            .filter(|source| source_check_due(source, now))
            .collect::<Vec<_>>();

        for source in due {
            let id = source.id.clone();
            if let Err(error) = run_scheduled_source(source, &core, &queue).await {
                let _ = core.db.update_media_source_failure(&id, &error.to_string());
            }
            let _ = app.emit("media-sources-changed", id);
        }
        tokio::time::sleep(std::time::Duration::from_secs(60)).await;
    }
}

#[tauri::command]
fn queue_list(state: State<'_, AppState>) -> Vec<Job> {
    state.queue.list()
}

#[tauri::command]
fn queue_stats(state: State<'_, AppState>) -> QueueStats {
    state.queue.stats()
}

#[tauri::command]
fn queue_cancel(id: String, state: State<'_, AppState>) {
    state.queue.cancel(&id);
}

#[tauri::command]
fn queue_cancel_all(state: State<'_, AppState>) {
    state.queue.cancel_all();
}

#[tauri::command]
fn queue_retry(id: String, state: State<'_, AppState>) {
    state.queue.retry(&id);
}

#[tauri::command]
fn queue_retry_failed(state: State<'_, AppState>) -> usize {
    state.queue.retry_failed()
}

#[tauri::command]
fn queue_clear_finished(state: State<'_, AppState>) {
    state.queue.clear_finished();
}

#[tauri::command]
fn queue_set_paused(paused: bool, state: State<'_, AppState>) {
    state.queue.set_paused(paused);
}

#[tauri::command]
fn queue_is_paused(state: State<'_, AppState>) -> bool {
    state.queue.is_paused()
}

// -------------------------------------------------------------- ajustes

#[tauri::command]
fn get_settings(state: State<'_, AppState>) -> Settings {
    state.core.settings.read().unwrap().clone()
}

#[tauri::command]
fn set_settings(settings: Settings, state: State<'_, AppState>) -> CmdResult<Settings> {
    let _ = std::fs::create_dir_all(&settings.video_dir);
    let _ = std::fs::create_dir_all(&settings.audio_dir);
    *state.core.settings.write().unwrap() = settings;
    state.core.save_settings().map_err(err)?;
    Ok(state.core.settings.read().unwrap().clone())
}

// ----------------------------------------------------------- biblioteca

#[tauri::command]
fn library_playlists(state: State<'_, AppState>) -> CmdResult<Vec<Playlist>> {
    state.core.db.list_playlists().map_err(err)
}

#[tauri::command]
fn library_items(
    playlist_id: Option<String>,
    search: Option<String>,
    state: State<'_, AppState>,
) -> CmdResult<Vec<LibraryItem>> {
    let search = search.filter(|s| !s.trim().is_empty());
    state
        .core
        .db
        .list_items(playlist_id.as_deref(), search.as_deref())
        .map_err(err)
}

#[tauri::command]
fn library_delete(id: String, delete_file: bool, state: State<'_, AppState>) -> CmdResult<()> {
    state.core.db.delete_item(&id, delete_file).map_err(err)
}

/// Quita una colección de la biblioteca. Por defecto no toca los archivos: se
/// deja de ver la lista, pero la música sigue en el disco.
#[tauri::command]
fn library_delete_playlist(
    playlist_id: String,
    delete_files: bool,
    state: State<'_, AppState>,
) -> CmdResult<usize> {
    state
        .core
        .db
        .delete_playlist(&playlist_id, delete_files)
        .map_err(err)
}

#[tauri::command]
fn library_prune(state: State<'_, AppState>) -> CmdResult<usize> {
    state.core.db.prune_missing().map_err(err)
}

/// Añade una carpeta del equipo a la biblioteca. Volver a escanearla solo
/// incorpora lo que falte: emite `import-progress` con (hechos, total) porque
/// leer los datos de cientos de archivos lleva su tiempo la primera vez.
#[tauri::command]
async fn library_import_folder(
    path: String,
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<local::ImportReport> {
    use tauri::Emitter;
    let core = state.core.clone();
    tauri::async_runtime::spawn_blocking(move || {
        local::import_folder(&core.db, &core.bins, Path::new(&path), |hechos, total| {
            let _ = app.emit("import-progress", (hechos, total));
        })
    })
    .await
    .map_err(err)?
    .map_err(err)
}

/// Rastrea las carpetas vigiladas y añade lo que no estuviera, **sin agrupar**.
///
/// Es lo que hace que la biblioteca sea la música del equipo y no solo la que
/// pasó por Recodio: un mp3 suelto en Descargas entra igual que uno descargado.
/// Emite `scan-progress` con (hechos, total).
#[tauri::command]
async fn library_scan(
    paths: Option<Vec<String>>,
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<local::ScanReport> {
    use tauri::Emitter;
    let core = state.core.clone();
    let raices: Vec<PathBuf> = match paths {
        Some(p) if !p.is_empty() => p.into_iter().map(PathBuf::from).collect(),
        _ => core.settings.read().unwrap().scan_roots(),
    };

    tauri::async_runtime::spawn_blocking(move || {
        let _guardia = GuardiaRastreo::tomar();
        local::scan_loose(&core.db, &core.bins, &raices, |hechos, total| {
            let _ = app.emit("scan-progress", (hechos, total));
        })
    })
    .await
    .map_err(err)?
    .map_err(err)
}

/// Solo puede haber un rastreo a la vez.
///
/// Hay tres cosas que lo lanzan —el arranque, abrir la biblioteca y el botón—,
/// y dos a la vez leerían el disco por duplicado para acabar insertando lo
/// mismo. El que llega segundo se va de vacío en lugar de esperar: lo que
/// buscaba ya lo está metiendo el primero.
static RASTREANDO: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

struct GuardiaRastreo;

impl GuardiaRastreo {
    /// `None` si ya hay otro rastreo en marcha.
    fn intentar() -> Option<Self> {
        use std::sync::atomic::Ordering;
        RASTREANDO
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .ok()
            .map(|_| Self)
    }

    /// Para el rastreo que pide el usuario a mano, que sí espera resultados.
    fn tomar() -> Self {
        use std::sync::atomic::Ordering;
        RASTREANDO.store(true, Ordering::Release);
        Self
    }
}

impl Drop for GuardiaRastreo {
    fn drop(&mut self) {
        RASTREANDO.store(false, std::sync::atomic::Ordering::Release);
    }
}

/// Rastrea sin avisar de nada: ni barra de progreso ni ruido.
///
/// Lo llama la biblioteca al abrirse. Antes había que acordarse de pulsar
/// «Rastrear ahora» para que apareciera un mp3 copiado a mano, y quien no lo
/// sabía daba la biblioteca por incompleta.
#[tauri::command]
async fn library_scan_quiet(state: State<'_, AppState>) -> CmdResult<local::ScanReport> {
    let core = state.core.clone();
    let raices = core.settings.read().unwrap().scan_roots();

    tauri::async_runtime::spawn_blocking(move || {
        let Some(_guardia) = GuardiaRastreo::intentar() else {
            return Ok(local::ScanReport::default());
        };
        local::scan_loose(&core.db, &core.bins, &raices, |_, _| {})
    })
    .await
    .map_err(err)?
    .map_err(err)
}

/// Carpetas del sistema con música o vídeo que merece la pena ofrecer.
#[tauri::command]
fn suggested_folders() -> Vec<String> {
    settings::suggested_dirs()
        .iter()
        .map(|p| p.to_string_lossy().into_owned())
        .collect()
}

/// Subtítulos que acompañan a un vídeo, listos para cargar en el reproductor.
#[tauri::command]
async fn subtitles_for(
    path: String,
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<Vec<subs::SubtitleTrack>> {
    let core = state.core.clone();
    let cache = subs::cache_dir_de(&app.path().app_data_dir().map_err(err)?);
    tauri::async_runtime::spawn_blocking(move || subs::find_for(&path, &core.bins, &cache))
        .await
        .map_err(err)
}

/// Pone la biblioteca al día de una vez: busca lo nuevo en las carpetas
/// añadidas, retira lo que ya no está, corrige lo que no corresponde a su
/// archivo y genera las miniaturas que falten.
#[tauri::command]
async fn library_refresh(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<refresh::RefreshReport> {
    use tauri::Emitter;
    let core = state.core.clone();
    let data_dir = app.path().app_data_dir().map_err(err)?;
    let emisor = app.clone();
    let raices = core.settings.read().unwrap().scan_roots();

    tauri::async_runtime::spawn_blocking(move || {
        refresh::refresh(
            &core.db,
            &core.bins,
            &data_dir,
            &raices,
            |fase, hechos, total| {
                let _ = emisor.emit("refresh-progress", (fase, hechos, total));
            },
        )
    })
    .await
    .map_err(err)?
    .map_err(err)
}

/// Revisa la biblioteca sin modificarla, para poder avisar de que hay entradas
/// cruzadas en cuanto se abre la pestaña.
#[tauri::command]
async fn library_health(state: State<'_, AppState>) -> CmdResult<repair::RepairReport> {
    let core = state.core.clone();
    tauri::async_runtime::spawn_blocking(move || repair::health(&core.db))
        .await
        .map_err(err)?
        .map_err(err)
}

/// Limpia las entradas que quedaron apuntando al archivo equivocado por el fallo
/// de las versiones hasta la 0.1.2. No borra ningún archivo.
#[tauri::command]
async fn library_repair(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<repair::RepairReport> {
    use tauri::Emitter;
    let core = state.core.clone();
    tauri::async_runtime::spawn_blocking(move || {
        repair::repair(&core.db, &core.bins, |hechos, total| {
            let _ = app.emit("repair-progress", (hechos, total));
        })
    })
    .await
    .map_err(err)?
    .map_err(err)
}

/// Rewrite the playlist file on demand. Recodio already does this on its own
/// when a playlist finishes downloading; this is for regenerating it after
/// deleting or adding files by hand.
#[tauri::command]
fn export_m3u(playlist_id: String, state: State<'_, AppState>) -> CmdResult<String> {
    m3u::write(&state.core.db, &playlist_id)
        .map(|p| p.to_string_lossy().into_owned())
        .map_err(err)
}

// ------------------------------------------------------ abrir / reproducir

#[tauri::command]
fn play_file(path: String, state: State<'_, AppState>) -> CmdResult<()> {
    if !Path::new(&path).exists() {
        return Err("El archivo ya no existe en disco".into());
    }
    let player = state
        .core
        .settings
        .read()
        .unwrap()
        .external_player
        .clone()
        .filter(|p| !p.trim().is_empty());

    match player {
        Some(exe) => {
            proc::command(exe).arg(&path).spawn().map_err(err)?;
            Ok(())
        }
        None => open_path(&path),
    }
}

#[tauri::command]
fn reveal_file(path: String) -> CmdResult<()> {
    let p = Path::new(&path);
    if !p.exists() {
        return Err("El archivo ya no existe en disco".into());
    }

    #[cfg(windows)]
    {
        // `explorer` returns a non-zero exit code even on success, so ignore it.
        let _ = proc::command("explorer")
            .arg(format!("/select,{}", p.display()))
            .spawn();
        Ok(())
    }

    #[cfg(target_os = "macos")]
    {
        proc::command("open")
            .arg("-R")
            .arg(p)
            .spawn()
            .map_err(err)?;
        Ok(())
    }

    #[cfg(all(unix, not(target_os = "macos")))]
    {
        // Nautilus, Dolphin, Nemo y compañía implementan esta interfaz D-Bus,
        // que abre la carpeta *y* deja el archivo seleccionado. Si no hay bus de
        // sesión (una sesión mínima, un contenedor), abrimos la carpeta a secas.
        let revealed = proc::command("dbus-send")
            .args([
                "--session",
                "--dest=org.freedesktop.FileManager1",
                "--type=method_call",
                "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems",
            ])
            .arg(format!("array:string:{}", file_uri(p)))
            .arg("string:")
            .status()
            .map(|s| s.success())
            .unwrap_or(false);

        if revealed {
            return Ok(());
        }
        let dir = p.parent().unwrap_or(p);
        open_path(&dir.to_string_lossy())
    }
}

/// Percent-encode a path into a `file://` URI. Sin esto, cualquier ruta con
/// espacios o acentos —o sea, casi cualquier título de vídeo— rompe la llamada.
#[cfg(all(unix, not(target_os = "macos")))]
fn file_uri(path: &Path) -> String {
    let mut out = String::from("file://");
    for byte in path.to_string_lossy().as_bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' | b'/' => {
                out.push(*byte as char)
            }
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }
    out
}

fn open_path(path: &str) -> CmdResult<()> {
    #[cfg(windows)]
    {
        proc::command("cmd")
            .args(["/C", "start", "", path])
            .spawn()
            .map_err(err)?;
    }
    #[cfg(target_os = "macos")]
    {
        proc::command("open").arg(path).spawn().map_err(err)?;
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        proc::command("xdg-open").arg(path).spawn().map_err(err)?;
    }
    Ok(())
}

#[tauri::command]
fn open_folder(path: String) -> CmdResult<()> {
    open_path(&path)
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct PlayerOption {
    name: String,
    path: String,
}

/// Reproductores instalados en el equipo.
///
/// Sin esto, dejar el reproductor sin configurar significa usar la asociación
/// del sistema, que puede ser distinta para cada formato: es fácil acabar con
/// los MP3 abriéndose en un reproductor y los MP4 en otro sin haber elegido eso.
/// Y la alternativa era buscar el ejecutable a mano por el disco.
#[tauri::command]
fn detect_players() -> Vec<PlayerOption> {
    // Nombre visible, ejecutable a buscar en el PATH, y rutas habituales.
    let candidatos: &[(&str, &str, &[&str])] = if cfg!(windows) {
        &[
            (
                "VLC",
                "vlc",
                &[
                    r"C:\Program Files\VideoLAN\VLC\vlc.exe",
                    r"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
                ],
            ),
            ("mpv", "mpv", &[r"C:\Program Files\mpv\mpv.exe"]),
            (
                "MPC-HC",
                "mpc-hc64",
                &[
                    r"C:\Program Files\MPC-HC\mpc-hc64.exe",
                    r"C:\Program Files (x86)\MPC-HC\mpc-hc.exe",
                ],
            ),
            (
                "PotPlayer",
                "PotPlayerMini64",
                &[r"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe"],
            ),
        ]
    } else if cfg!(target_os = "macos") {
        &[
            ("VLC", "vlc", &["/Applications/VLC.app/Contents/MacOS/VLC"]),
            (
                "IINA",
                "iina",
                &["/Applications/IINA.app/Contents/MacOS/IINA"],
            ),
            ("mpv", "mpv", &[]),
        ]
    } else {
        &[
            ("VLC", "vlc", &["/usr/bin/vlc", "/snap/bin/vlc"]),
            ("mpv", "mpv", &["/usr/bin/mpv"]),
            ("Celluloid", "celluloid", &[]),
            ("SMPlayer", "smplayer", &[]),
            ("Totem", "totem", &[]),
        ]
    };

    candidatos
        .iter()
        .filter_map(|(nombre, comando, rutas)| {
            let encontrado = which::which(comando)
                .ok()
                .or_else(|| rutas.iter().map(PathBuf::from).find(|p| p.is_file()))?;
            Some(PlayerOption {
                name: (*nombre).to_string(),
                path: encontrado.to_string_lossy().into_owned(),
            })
        })
        .collect()
}

/// La interfaz cambia un par de detalles según el sistema (filtros del selector
/// de archivos, sobre todo). Preguntarlo una vez es más honesto que adivinarlo
/// desde el user agent del webview.
#[tauri::command]
fn app_platform() -> &'static str {
    if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "macos"
    } else if cfg!(target_os = "android") {
        "android"
    } else {
        "linux"
    }
}

// -------------------------------------------------------- herramientas

/// Comprobar las herramientas cuesta un proceso por cada una. Va en un hilo
/// aparte para no congelar la interfaz, y `force` distingue entre mirar lo ya
/// sabido y volver a preguntar de verdad.
#[tauri::command]
async fn tools_status(force: bool, state: State<'_, AppState>) -> CmdResult<Vec<ToolStatus>> {
    let core = state.core.clone();
    tauri::async_runtime::spawn_blocking(move || {
        if force {
            core.bins.refresh_status()
        } else {
            core.bins.status_all()
        }
    })
    .await
    .map_err(err)
}

/// Instala una herramienta en la carpeta de Recodio. Emite `tool-install-progress`
/// con `(nombre, fracción)` para que la interfaz muestre el avance: ffmpeg pasa
/// de cien megas y sin barra parecería congelado.
#[tauri::command]
async fn tools_install(
    name: String,
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> CmdResult<String> {
    use tauri::Emitter;

    let core = state.core.clone();
    let nombre_evento = name.clone();
    core.bins
        .install(&name, move |fraccion| {
            let _ = app.emit("tool-install-progress", (nombre_evento.clone(), fraccion));
        })
        .await
        .map_err(err)
}

#[tauri::command]
fn tools_update_ytdlp(state: State<'_, AppState>) -> CmdResult<String> {
    state.core.bins.update_ytdlp().map_err(err)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // WebKitGTK con el renderizador DMA-BUF deja la ventana en negro en muchas
    // combinaciones de GPU y driver (NVIDIA propietario, Mesa antiguo, WSLg).
    // Es el fallo número uno de las apps Tauri en Linux y el remedio estándar
    // es desactivarlo; no se pierde nada apreciable. Tiene que hacerse antes de
    // que arranque GTK, y se respeta el valor si el usuario ya puso el suyo.
    #[cfg(target_os = "linux")]
    if std::env::var_os("WEBKIT_DISABLE_DMABUF_RENDERER").is_none() {
        std::env::set_var("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
    }

    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            // El actualizador y el reinicio solo existen en escritorio; en móvil
            // registrarlos rompería el arranque.
            #[cfg(desktop)]
            {
                app.handle()
                    .plugin(tauri_plugin_updater::Builder::new().build())?;
                app.handle().plugin(tauri_plugin_process::init())?;
            }

            let data_dir = app.path().app_data_dir()?;
            let config_dir = app.path().app_config_dir()?;
            let core = Arc::new(Core::new(data_dir, config_dir)?);
            let queue = Queue::new(core.clone(), app.handle().clone());

            tauri::async_runtime::spawn(source_scheduler(
                core.clone(),
                queue.clone(),
                app.handle().clone(),
            ));

            // Rastreo de arranque: lo que haya aparecido en las carpetas desde
            // la última vez entra solo. Va en un hilo aparte para no retrasar la
            // ventana, y solo avisa si de verdad ha encontrado algo.
            let nucleo = core.clone();
            let manejador = app.handle().clone();
            std::thread::spawn(move || {
                use tauri::Emitter;
                let Some(_guardia) = GuardiaRastreo::intentar() else {
                    return;
                };
                let raices = nucleo.settings.read().unwrap().scan_roots();
                if let Ok(informe) = local::scan_loose(&nucleo.db, &nucleo.bins, &raices, |_, _| {})
                {
                    if informe.added > 0 {
                        let _ = manejador.emit("library-scanned", informe.added);
                        let _ = manejador.emit("library-changed", ());
                    }
                }
            });

            let spotify = Arc::new(spotify::SpotifyAuth::new()?);
            app.manage(AppState {
                core,
                queue,
                spotify,
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            analyze_url,
            youtube_session_check,
            youtube_accounts_list,
            youtube_import_cookies,
            youtube_account_rename,
            youtube_account_delete,
            youtube_open_login,
            spotify_status,
            spotify_login,
            spotify_logout,
            spotify_playlists,
            spotify_collection,
            spotify_playlist,
            media_sources_list,
            media_source_add,
            media_source_sync,
            media_source_items,
            media_source_mark_seen,
            media_source_update_profile,
            media_source_update_schedule,
            media_sources_export,
            media_sources_import,
            media_source_delete,
            enqueue,
            queue_list,
            queue_stats,
            queue_cancel,
            queue_cancel_all,
            queue_retry,
            queue_retry_failed,
            queue_clear_finished,
            queue_set_paused,
            queue_is_paused,
            get_settings,
            set_settings,
            library_playlists,
            library_items,
            library_delete,
            library_prune,
            library_delete_playlist,
            library_repair,
            library_health,
            library_refresh,
            subtitles_for,
            library_import_folder,
            library_scan,
            library_scan_quiet,
            suggested_folders,
            export_m3u,
            play_file,
            reveal_file,
            open_folder,
            app_platform,
            detect_players,
            tools_status,
            tools_install,
            tools_update_ytdlp,
        ])
        .run(tauri::generate_context!())
        .expect("error al iniciar Recodio");
}

#[cfg(test)]
mod source_scheduler_tests {
    use super::{
        sanitized_sources_backup, source_check_due, source_item_to_entry, Core,
        DiscoveredSourceItem, MediaSource, SourceProfile,
    };

    fn source(interval: Option<i64>, last_checked_at: Option<i64>) -> MediaSource {
        MediaSource {
            id: "fuente".into(),
            url: "https://example.com/lista".into(),
            source: "ytdlp".into(),
            source_id: "lista".into(),
            title: "Lista".into(),
            uploader: None,
            thumbnail: None,
            media_kind: "video".into(),
            created_at: 1_000,
            last_checked_at,
            last_success_at: None,
            last_error: None,
            total_items: 0,
            new_items: 0,
            profile: SourceProfile::default(),
            check_interval_minutes: interval,
            auto_download: false,
        }
    }

    #[test]
    fn solo_las_fuentes_vencidas_entran_en_el_programador() {
        assert!(!source_check_due(&source(None, None), 100_000));
        assert!(!source_check_due(&source(Some(60), Some(10_000)), 13_599));
        assert!(source_check_due(&source(Some(60), Some(10_000)), 13_600));
    }

    #[test]
    fn el_respaldo_no_exporta_la_cuenta_de_youtube() {
        let mut value = source(Some(60), None);
        value.profile.youtube_cookies_file = Some("C:/secreto/cookies.txt".into());
        let backup = sanitized_sources_backup(vec![value]);
        assert!(backup.sources[0].profile.youtube_cookies_file.is_none());
    }

    #[test]
    fn un_estreno_guardado_llega_serializado_a_la_interfaz() {
        let root = std::env::temp_dir().join(format!("recodio-live-{}", uuid::Uuid::new_v4()));
        let core = Core::new(root.join("data"), root.join("config")).unwrap();
        let value = source(None, None);
        core.db.upsert_media_source(&value).unwrap();
        core.db
            .apply_source_discovery(
                &value.id,
                &value.title,
                None,
                None,
                &value.source_id,
                &[DiscoveredSourceItem {
                    extractor: "youtube".into(),
                    remote_id: "estreno".into(),
                    title: "Estreno".into(),
                    url: "https://youtube.com/watch?v=estreno".into(),
                    uploader: None,
                    duration: None,
                    thumbnail: None,
                    position: 1,
                    unavailable: false,
                    live_status: Some("is_upcoming".into()),
                    release_timestamp: Some(2_000_000_000),
                    already_downloaded: false,
                }],
            )
            .unwrap();
        let stored = core.db.media_source_items(&value.id).unwrap().remove(0);
        let frontend = source_item_to_entry(&core, &value, stored);
        let json = serde_json::to_value(frontend).unwrap();
        assert_eq!(
            json.pointer("/entry/liveStatus").and_then(|v| v.as_str()),
            Some("is_upcoming")
        );
        std::fs::remove_dir_all(root).ok();
    }
}

#[cfg(test)]
mod youtube_account_tests {
    use super::{load_youtube_accounts, validate_youtube_cookies, youtube_account_name, Core};

    #[test]
    fn acepta_cookies_netscape_de_youtube() {
        let cookies = b"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tvalue\n";
        assert!(validate_youtube_cookies(cookies).is_ok());
    }

    #[test]
    fn rechaza_json_y_archivos_de_otro_sitio() {
        assert!(validate_youtube_cookies(br#"[{"domain":"youtube.com"}]"#).is_err());
        assert!(validate_youtube_cookies(
            b"# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t0\tSID\tvalue\n"
        )
        .is_err());
    }

    #[test]
    fn limpia_y_limita_el_nombre_de_la_cuenta() {
        assert_eq!(
            youtube_account_name("  Personal  ".into()).unwrap(),
            "Personal"
        );
        assert!(youtube_account_name("   ".into()).is_err());
        assert!(youtube_account_name("x".repeat(61)).is_err());
        assert!(youtube_account_name("Cuenta\nnueva".into()).is_err());
    }

    #[test]
    fn migra_una_sola_vez_la_cuenta_de_la_version_anterior() {
        let root =
            std::env::temp_dir().join(format!("recodio-youtube-accounts-{}", uuid::Uuid::new_v4()));
        let data = root.join("data");
        let config = root.join("config");
        std::fs::create_dir_all(&config).unwrap();
        std::fs::write(
            config.join("youtube-cookies.txt"),
            b"# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tvalue\n",
        )
        .unwrap();
        let core = Core::new(data, config.clone()).unwrap();

        let first = load_youtube_accounts(&core).unwrap();
        let second = load_youtube_accounts(&core).unwrap();
        assert_eq!(first.accounts.len(), 1);
        assert_eq!(second.accounts.len(), 1);
        assert_eq!(second.accounts[0].name, "Cuenta importada");
        assert!(config.join("youtube-accounts.json").is_file());

        drop(core);
        std::fs::remove_dir_all(root).ok();
    }
}

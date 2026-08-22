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
use db::{LibraryItem, Playlist};
use job::Job;
use queue::{Queue, QueueStats};
use serde::Deserialize;
use settings::Settings;
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

/// Copia un `cookies.txt` a la carpeta privada de configuración de Recodio.
/// Guardar una copia evita que la sesión deje de funcionar cuando el usuario
/// borra el archivo original de Descargas. El contenido nunca vuelve al webview.
#[tauri::command]
fn youtube_import_cookies(source: PathBuf, state: State<'_, AppState>) -> CmdResult<String> {
    const MAX_COOKIE_FILE: u64 = 10 * 1024 * 1024;

    let metadata = std::fs::metadata(&source)
        .map_err(|_| "No se pudo abrir el archivo de cookies seleccionado".to_string())?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_COOKIE_FILE {
        return Err("El archivo de cookies está vacío o es demasiado grande".into());
    }

    let data = std::fs::read(&source).map_err(err)?;
    let text = String::from_utf8_lossy(&data);
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

    let config_dir = state
        .core
        .settings_path
        .parent()
        .ok_or_else(|| "No se encontró la carpeta de configuración de Recodio".to_string())?;
    std::fs::create_dir_all(config_dir).map_err(err)?;
    let dest = config_dir.join("youtube-cookies.txt");
    let temp = config_dir.join("youtube-cookies.txt.new");
    std::fs::write(&temp, data).map_err(err)?;

    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&temp, std::fs::Permissions::from_mode(0o600)).map_err(err)?;
    }

    if dest.exists() {
        std::fs::remove_file(&dest).map_err(err)?;
    }
    std::fs::rename(&temp, &dest).map_err(err)?;
    Ok(dest.to_string_lossy().into_owned())
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
}

#[tauri::command]
fn enqueue(req: EnqueueRequest, state: State<'_, AppState>) -> CmdResult<usize> {
    let core = &state.core;
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

    let jobs: Vec<Job> = req
        .entries
        .into_iter()
        .map(|entry| {
            let overwrite = req.overwrite_ids.contains(&entry.id);
            Job::new(
                entry,
                req.kind.clone(),
                req.source.clone(),
                dest_str.clone(),
                overwrite,
                playlist_id.clone(),
                playlist_title.clone(),
            )
        })
        .collect();

    let count = jobs.len();
    state.queue.add(jobs);
    Ok(count)
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
            youtube_import_cookies,
            youtube_open_login,
            spotify_status,
            spotify_login,
            spotify_logout,
            spotify_playlists,
            spotify_collection,
            spotify_playlist,
            enqueue,
            queue_list,
            queue_stats,
            queue_cancel,
            queue_cancel_all,
            queue_retry,
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

mod analyze;
mod binaries;
mod core;
mod db;
mod job;
mod local;
mod m3u;
mod proc;
mod queue;
mod repair;
mod settings;
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

pub struct AppState {
    core: Arc<Core>,
    queue: Queue,
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
async fn library_repair(state: State<'_, AppState>) -> CmdResult<repair::RepairReport> {
    let core = state.core.clone();
    tauri::async_runtime::spawn_blocking(move || repair::repair(&core.db))
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
            ("IINA", "iina", &["/Applications/IINA.app/Contents/MacOS/IINA"]),
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
            let encontrado = which::which(comando).ok().or_else(|| {
                rutas
                    .iter()
                    .map(PathBuf::from)
                    .find(|p| p.is_file())
            })?;
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
            app.manage(AppState { core, queue });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            analyze_url,
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
            library_repair,
            library_health,
            library_import_folder,
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

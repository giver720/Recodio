mod analyze;
mod binaries;
mod core;
mod db;
mod job;
mod m3u;
mod proc;
mod queue;
mod settings;
mod spotdl;
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
async fn analyze_url(url: String, state: State<'_, AppState>) -> CmdResult<AnalyzeResult> {
    let core = state.core.clone();
    let settings = core.settings.read().unwrap().clone();
    analyze::analyze(url.trim(), &core.bins, &core.db, &settings)
        .await
        .map_err(err)
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
    #[cfg(not(windows))]
    {
        let dir = p.parent().unwrap_or(p);
        open_path(&dir.to_string_lossy())
    }
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

// -------------------------------------------------------- herramientas

#[tauri::command]
fn tools_status(state: State<'_, AppState>) -> Vec<ToolStatus> {
    state.core.bins.status_all()
}

#[tauri::command]
async fn tools_install_ytdlp(state: State<'_, AppState>) -> CmdResult<String> {
    let core = state.core.clone();
    core.bins.install_ytdlp().await.map_err(err)
}

#[tauri::command]
fn tools_update_ytdlp(state: State<'_, AppState>) -> CmdResult<String> {
    state.core.bins.update_ytdlp().map_err(err)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
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
            export_m3u,
            play_file,
            reveal_file,
            open_folder,
            tools_status,
            tools_install_ytdlp,
            tools_update_ytdlp,
        ])
        .run(tauri::generate_context!())
        .expect("error al iniciar Recodio");
}

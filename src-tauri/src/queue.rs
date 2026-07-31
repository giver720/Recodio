use crate::core::Core;
use crate::db::LibraryItem;
use crate::job::{Job, JobPhase, JobStatus, Progress};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

/// Progress events are throttled to this interval per job so a 20-item queue
/// cannot flood the webview.
const EMIT_INTERVAL: Duration = Duration::from_millis(120);

pub struct Queue {
    inner: Arc<Inner>,
}

struct Inner {
    core: Arc<Core>,
    app: AppHandle,
    jobs: Mutex<Vec<Job>>,
    cancels: Mutex<HashMap<String, CancellationToken>>,
    running: AtomicUsize,
    paused: AtomicBool,
    wake: Notify,
}

#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueueStats {
    pub queued: usize,
    pub running: usize,
    pub done: usize,
    pub failed: usize,
    pub skipped: usize,
    pub paused: bool,
    /// Combined 0–1 progress of everything currently in the queue.
    pub overall: f64,
}

impl Queue {
    pub fn new(core: Arc<Core>, app: AppHandle) -> Self {
        let inner = Arc::new(Inner {
            core,
            app,
            jobs: Mutex::new(Vec::new()),
            cancels: Mutex::new(HashMap::new()),
            running: AtomicUsize::new(0),
            paused: AtomicBool::new(false),
            wake: Notify::new(),
        });

        let scheduler = inner.clone();
        tauri::async_runtime::spawn(async move {
            loop {
                scheduler.schedule();
                tokio::select! {
                    _ = scheduler.wake.notified() => {}
                    _ = tokio::time::sleep(Duration::from_millis(500)) => {}
                }
            }
        });

        Self { inner }
    }

    pub fn add(&self, jobs: Vec<Job>) {
        {
            let mut all = self.inner.jobs.lock().unwrap();
            all.extend(jobs);
        }
        self.inner.emit_all();
        self.inner.wake.notify_one();
    }

    pub fn list(&self) -> Vec<Job> {
        self.inner.jobs.lock().unwrap().clone()
    }

    pub fn stats(&self) -> QueueStats {
        self.inner.stats()
    }

    pub fn cancel(&self, id: &str) {
        if let Some(token) = self.inner.cancels.lock().unwrap().get(id) {
            token.cancel();
        }
        let mut jobs = self.inner.jobs.lock().unwrap();
        if let Some(job) = jobs.iter_mut().find(|j| j.id == id) {
            if job.status == JobStatus::Queued {
                job.status = JobStatus::Canceled;
                job.phase = JobPhase::Finished;
            }
        }
        drop(jobs);
        self.inner.emit_all();
    }

    pub fn cancel_all(&self) {
        for token in self.inner.cancels.lock().unwrap().values() {
            token.cancel();
        }
        let mut jobs = self.inner.jobs.lock().unwrap();
        for job in jobs.iter_mut().filter(|j| j.status == JobStatus::Queued) {
            job.status = JobStatus::Canceled;
            job.phase = JobPhase::Finished;
        }
        drop(jobs);
        self.inner.emit_all();
    }

    pub fn retry(&self, id: &str) {
        {
            let mut jobs = self.inner.jobs.lock().unwrap();
            if let Some(job) = jobs.iter_mut().find(|j| j.id == id) {
                if matches!(
                    job.status,
                    JobStatus::Failed | JobStatus::Canceled | JobStatus::Skipped
                ) {
                    job.status = JobStatus::Queued;
                    job.phase = JobPhase::Waiting;
                    job.progress = -1.0;
                    job.error = None;
                    job.message = None;
                    // A manual retry is an explicit "yes, do it again".
                    job.overwrite = true;
                }
            }
        }
        self.inner.emit_all();
        self.inner.wake.notify_one();
    }

    /// Drop everything that is not queued or running.
    pub fn clear_finished(&self) {
        {
            let mut jobs = self.inner.jobs.lock().unwrap();
            jobs.retain(|j| matches!(j.status, JobStatus::Queued | JobStatus::Running));
        }
        self.inner.emit_all();
    }

    pub fn set_paused(&self, paused: bool) {
        self.inner.paused.store(paused, Ordering::SeqCst);
        self.inner.emit_stats();
        if !paused {
            self.inner.wake.notify_one();
        }
    }

    pub fn is_paused(&self) -> bool {
        self.inner.paused.load(Ordering::SeqCst)
    }
}

impl Inner {
    fn schedule(self: &Arc<Self>) {
        if self.paused.load(Ordering::SeqCst) {
            return;
        }
        let limit = self.core.settings.read().unwrap().concurrency.max(1);

        while self.running.load(Ordering::SeqCst) < limit {
            let job = {
                let mut jobs = self.jobs.lock().unwrap();
                match jobs.iter_mut().find(|j| j.status == JobStatus::Queued) {
                    Some(j) => {
                        j.status = JobStatus::Running;
                        j.phase = JobPhase::Waiting;
                        j.message = Some("Preparando…".into());
                        j.clone()
                    }
                    None => return,
                }
            };

            self.running.fetch_add(1, Ordering::SeqCst);
            let token = CancellationToken::new();
            self.cancels
                .lock()
                .unwrap()
                .insert(job.id.clone(), token.clone());

            let inner = self.clone();
            tauri::async_runtime::spawn(async move {
                inner.run_job(job, token).await;
                inner.running.fetch_sub(1, Ordering::SeqCst);
                inner.wake.notify_one();
            });
        }
    }

    async fn run_job(self: &Arc<Self>, job: Job, token: CancellationToken) {
        let id = job.id.clone();
        self.emit_job(&id);

        // Re-check right before downloading: another job in this same batch may
        // have just produced the file.
        if !job.overwrite {
            if let Some(existing) = self.core.db.find_existing(
                &job.entry.extractor,
                &job.entry.source_id,
                &job.kind,
                job.playlist_id.as_deref(),
            ) {
                self.finish(
                    &id,
                    JobStatus::Skipped,
                    Some(existing.file_path),
                    Some("Ya estaba en la biblioteca".into()),
                    None,
                );
                return;
            }
        }

        let settings = self.core.settings.read().unwrap().clone();
        let archive = settings.use_archive.then(|| self.core.archive_path.clone());

        let progress_inner = self.clone();
        let progress_id = id.clone();
        let last_emit = Mutex::new(Instant::now() - EMIT_INTERVAL);
        let on_progress = move |p: Progress| {
            let mut should_emit = false;
            {
                let mut jobs = progress_inner.jobs.lock().unwrap();
                if let Some(j) = jobs.iter_mut().find(|j| j.id == progress_id) {
                    if let Some(v) = p.phase {
                        should_emit |= j.phase != v;
                        j.phase = v;
                    }
                    if let Some(v) = p.progress {
                        j.progress = v;
                    }
                    if let Some(v) = p.downloaded_bytes {
                        j.downloaded_bytes = v;
                    }
                    if let Some(v) = p.total_bytes {
                        j.total_bytes = v;
                    }
                    if let Some(v) = p.speed {
                        j.speed = v;
                    }
                    j.eta = p.eta.or(j.eta);
                    if let Some(v) = p.message {
                        should_emit |= j.message.as_deref() != Some(v.as_str());
                        j.message = Some(v);
                    }
                }
            }
            let mut last = last_emit.lock().unwrap();
            if should_emit || last.elapsed() >= EMIT_INTERVAL {
                *last = Instant::now();
                drop(last);
                progress_inner.emit_job(&progress_id);
            }
        };

        // Todo pasa por yt-dlp, incluida la música de Spotify: ver el comentario
        // en `ytdlp::download`.
        let result = crate::ytdlp::download(
            &job,
            &settings,
            &self.core.bins,
            archive,
            token.clone(),
            on_progress,
        )
        .await;

        match result {
            Ok(path) => {
                let size = std::fs::metadata(&path).map(|m| m.len() as i64).unwrap_or(0);
                let ext = path
                    .extension()
                    .and_then(|e| e.to_str())
                    .unwrap_or("")
                    .to_string();

                let item = LibraryItem {
                    id: uuid::Uuid::new_v4().to_string(),
                    source: job.source.clone(),
                    extractor: job.entry.extractor.clone(),
                    source_id: job.entry.source_id.clone(),
                    url: job.entry.url.clone(),
                    title: job.entry.title.clone(),
                    uploader: job.entry.uploader.clone(),
                    duration: job.entry.duration,
                    thumbnail: job.entry.thumbnail.clone(),
                    file_path: path.to_string_lossy().into_owned(),
                    file_size: size,
                    kind: job.kind.clone(),
                    ext,
                    playlist_id: job.playlist_id.clone(),
                    playlist_index: Some(job.entry.index),
                    downloaded_at: chrono::Utc::now().timestamp(),
                };
                if let Err(e) = self.core.db.upsert_item(&item) {
                    eprintln!("[recodio] no se pudo guardar en la biblioteca: {e}");
                }
                let _ = self.app.emit("library-changed", ());
                self.finish(
                    &id,
                    JobStatus::Done,
                    Some(item.file_path),
                    Some("Completado".into()),
                    None,
                );
            }
            Err(e) => {
                let msg = e.to_string();
                let status = if token.is_cancelled() {
                    JobStatus::Canceled
                } else {
                    JobStatus::Failed
                };
                self.finish(&id, status, None, None, Some(msg));
            }
        }

        self.cancels.lock().unwrap().remove(&id);

        if let Some(playlist_id) = job.playlist_id.as_deref() {
            self.write_playlist_when_complete(playlist_id);
        }
    }

    /// Cuando el último elemento de una playlist sale de la cola, la playlist
    /// local queda escrita sola: carpeta propia (si está activado) más su
    /// `.m3u8` listo para abrir en VLC. El usuario no tiene que exportar nada.
    fn write_playlist_when_complete(&self, playlist_id: &str) {
        let pending = self
            .jobs
            .lock()
            .unwrap()
            .iter()
            .filter(|j| j.playlist_id.as_deref() == Some(playlist_id))
            .any(|j| matches!(j.status, JobStatus::Queued | JobStatus::Running));
        if pending {
            return;
        }

        match crate::m3u::write(&self.core.db, playlist_id) {
            Ok(path) => {
                let _ = self
                    .app
                    .emit("playlist-ready", path.to_string_lossy().into_owned());
            }
            Err(e) => eprintln!("[recodio] no se pudo escribir la playlist: {e}"),
        }
    }

    fn finish(
        &self,
        id: &str,
        status: JobStatus,
        file_path: Option<String>,
        message: Option<String>,
        error: Option<String>,
    ) {
        {
            let mut jobs = self.jobs.lock().unwrap();
            if let Some(j) = jobs.iter_mut().find(|j| j.id == id) {
                j.status = status;
                j.phase = JobPhase::Finished;
                j.speed = 0.0;
                j.eta = None;
                if status == JobStatus::Done {
                    j.progress = 1.0;
                }
                j.file_path = file_path;
                j.message = message;
                j.error = error;
            }
        }
        self.emit_job(id);
    }

    fn emit_job(&self, id: &str) {
        let job = self
            .jobs
            .lock()
            .unwrap()
            .iter()
            .find(|j| j.id == id)
            .cloned();
        if let Some(job) = job {
            let _ = self.app.emit("job-update", job);
        }
        self.emit_stats();
    }

    fn emit_all(&self) {
        let jobs = self.jobs.lock().unwrap().clone();
        let _ = self.app.emit("queue-replace", jobs);
        self.emit_stats();
    }

    fn emit_stats(&self) {
        let _ = self.app.emit("queue-stats", self.stats());
    }

    fn stats(&self) -> QueueStats {
        let jobs = self.jobs.lock().unwrap();
        let mut s = QueueStats {
            queued: 0,
            running: 0,
            done: 0,
            failed: 0,
            skipped: 0,
            paused: self.paused.load(Ordering::SeqCst),
            overall: 0.0,
        };
        let mut sum = 0.0;
        for j in jobs.iter() {
            match j.status {
                JobStatus::Queued => s.queued += 1,
                JobStatus::Running => s.running += 1,
                JobStatus::Done => s.done += 1,
                JobStatus::Failed => s.failed += 1,
                JobStatus::Skipped => s.skipped += 1,
                JobStatus::Canceled => {}
            }
            sum += match j.status {
                JobStatus::Done | JobStatus::Skipped => 1.0,
                JobStatus::Running => j.progress.max(0.0),
                _ => 0.0,
            };
        }
        if !jobs.is_empty() {
            s.overall = sum / jobs.len() as f64;
        }
        s
    }
}

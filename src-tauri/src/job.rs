use crate::analyze::Entry;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum JobStatus {
    Queued,
    Running,
    /// Finished and written to disk.
    Done,
    /// Already in the library, nothing was downloaded.
    Skipped,
    Failed,
    Canceled,
}

/// Which phase of the pipeline a running job is in — the UI colours the bar
/// differently for each.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum JobPhase {
    Waiting,
    Downloading,
    /// ffmpeg merging / extracting / cutting SponsorBlock segments.
    Processing,
    Finished,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Job {
    pub id: String,
    pub entry: Entry,
    /// `video` or `audio`.
    pub kind: String,
    /// `ytdlp` or `spotdl`.
    pub source: String,
    pub dest_dir: String,
    /// Set when the user chose to replace an existing file.
    pub overwrite: bool,
    /// Internal playlist uuid this job belongs to.
    pub playlist_id: Option<String>,
    pub playlist_title: Option<String>,

    pub status: JobStatus,
    pub phase: JobPhase,
    /// 0.0 – 1.0. `-1` while the total size is unknown.
    pub progress: f64,
    pub downloaded_bytes: u64,
    pub total_bytes: u64,
    /// Bytes per second.
    pub speed: f64,
    /// Seconds remaining, when known.
    pub eta: Option<u64>,
    pub message: Option<String>,
    pub error: Option<String>,
    pub file_path: Option<String>,
    pub created_at: i64,
}

impl Job {
    pub fn new(
        entry: Entry,
        kind: String,
        source: String,
        dest_dir: String,
        overwrite: bool,
        playlist_id: Option<String>,
        playlist_title: Option<String>,
    ) -> Self {
        Self {
            id: uuid::Uuid::new_v4().to_string(),
            entry,
            kind,
            source,
            dest_dir,
            overwrite,
            playlist_id,
            playlist_title,
            status: JobStatus::Queued,
            phase: JobPhase::Waiting,
            progress: -1.0,
            downloaded_bytes: 0,
            total_bytes: 0,
            speed: 0.0,
            eta: None,
            message: None,
            error: None,
            file_path: None,
            created_at: chrono::Utc::now().timestamp(),
        }
    }
}

/// Incremental update emitted while a job runs.
#[derive(Debug, Clone, Default)]
pub struct Progress {
    pub phase: Option<JobPhase>,
    pub progress: Option<f64>,
    pub downloaded_bytes: Option<u64>,
    pub total_bytes: Option<u64>,
    pub speed: Option<f64>,
    pub eta: Option<u64>,
    pub message: Option<String>,
}

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

    for entry in &mut result.entries {
        entry.existing_video = db
            .find_existing(&entry.extractor, &entry.source_id, "video")
            .map(|i| i.file_path);
        entry.existing_audio = db
            .find_existing(&entry.extractor, &entry.source_id, "audio")
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

async fn analyze_spotify(url: &str, bins: &Binaries) -> Result<AnalyzeResult> {
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

    let raw = std::fs::read_to_string(&tmp)?;
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

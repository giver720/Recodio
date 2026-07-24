package com.recodio.app

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

// selectedIndices == null means "whole query" (single video, search, or full playlist).
// Non-null means a subset of a playlist, downloaded via yt-dlp's --playlist-items against
// the ORIGINAL playlist URL (safer than trying to reconstruct a per-entry URL from
// --flat-playlist output, which is unreliable across extractors).
data class QueueItem(
    val label: String,
    val url: String,
    val selectedIndices: List<Int>? = null,
    // Known ahead of time from our own analysis, not from yt-dlp's runtime field presence -
    // avoids needing --output-na-placeholder for the "loose video" case entirely (that flag's
    // empty-string value tripped a bug in the library's option marshalling: it silently
    // consumed the NEXT flag as its value, producing a literal "--embed-metadata" folder).
    val isPlaylist: Boolean = false,
)
data class PlaylistEntry(val index: Int, val title: String, var selected: Boolean = true)

enum class ItemStatus { QUEUED, DOWNLOADING, DONE, SKIPPED, ERROR }

// Per-item visual state for the download list. A plain data class wouldn't do - Compose only
// recomposes readers when the STATE OBJECT they read changes, and a `var` on a data class
// sitting inside a mutableStateListOf is invisible to snapshot tracking unless the field
// itself is a mutableStateOf. This way item.status = X recomposes just that row.
class DownloadItemUi(val label: String) {
    var status by mutableStateOf(ItemStatus.QUEUED)
    var progress by mutableStateOf(0f)
    // Last "ERROR:" line yt-dlp printed for this item, same idea as desktop's lastErrorText -
    // null unless status == ERROR, shown to the user on tap so "Error" isn't a dead end.
    var errorDetail by mutableStateOf<String?>(null)
}

// Single source of truth shared between the Compose UI and the foreground Service. Compose
// snapshot state (mutableStateOf/mutableStateListOf) is safe to read/write from any thread in
// the same process, so the Service can drive this directly while the Activity observes it -
// no LiveData/Binder plumbing needed since both live in the same process, and a foreground
// Service is exactly what keeps that process (and this object) alive while the app is
// backgrounded mid-download.
object DownloadState {
    var url by mutableStateOf("")
    var audioOnly by mutableStateOf(true)
    var organizeInFolders by mutableStateOf(true)
    var sponsorBlock by mutableStateOf(false)

    // null = usar la carpeta privada por defecto de la app (Android/data/.../Recodio).
    // Non-null = ruta absoluta elegida por el usuario, ya traducida desde la SAF tree URI.
    var downloadDirPath by mutableStateOf<String?>(null)

    // Mirrors CookiesPrefs.hasCookies(context) - kept as state so the UI recomposes right after
    // an import/removal instead of needing to re-check the filesystem.
    var hasCookies by mutableStateOf(false)

    val queue = mutableStateListOf<QueueItem>()

    // Built fresh at the start of each runQueue() run - one entry per item actually being
    // downloaded, in order, so the UI can show live per-item progress instead of a text log.
    val downloadItems = mutableStateListOf<DownloadItemUi>()

    var analyzing by mutableStateOf(false)
    var analyzedUrl by mutableStateOf("")
    val analyzedEntries = mutableStateListOf<PlaylistEntry>()
    var analyzedPlaylistTitle by mutableStateOf<String?>(null)

    var running by mutableStateOf(false)
    var status by mutableStateOf("Listo.")

    // Overall queue progress (0..1), combining completed items + the live fraction of whatever
    // item is downloading right now - drives the single progress bar shown next to the queue,
    // which replaced the raw text log.
    var progress by mutableStateOf(0f)

    // Retry-pass indicator, mirrors desktop's "pasada X/Y" - only shown once a 2nd pass over
    // failed items actually starts, so a normal single-pass run never mentions it.
    var passIndex by mutableStateOf(1)
    var passTotal by mutableStateOf(1)

    // Projected time remaining for the whole queue, recomputed after each item finishes from
    // the running average seconds/item - null until at least one item has completed.
    var etaText by mutableStateOf<String?>(null)

    fun resetAnalysis() {
        analyzedEntries.clear()
        analyzedPlaylistTitle = null
        analyzedUrl = ""
    }
}

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

enum class ItemStatus { QUEUED, DOWNLOADING, DONE, ERROR }

// Per-item visual state for the download list. A plain data class wouldn't do - Compose only
// recomposes readers when the STATE OBJECT they read changes, and a `var` on a data class
// sitting inside a mutableStateListOf is invisible to snapshot tracking unless the field
// itself is a mutableStateOf. This way item.status = X recomposes just that row.
class DownloadItemUi(val label: String) {
    var status by mutableStateOf(ItemStatus.QUEUED)
    var progress by mutableStateOf(0f)
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
    var progress by mutableStateOf(0f)
    var log by mutableStateOf("")

    fun appendLog(line: String) {
        val next = log + line + "\n"
        // Bounded so a long playlist doesn't grow this into a multi-MB string.
        log = if (next.length > 40_000) next.takeLast(30_000) else next
    }

    fun resetAnalysis() {
        analyzedEntries.clear()
        analyzedPlaylistTitle = null
        analyzedUrl = ""
    }
}

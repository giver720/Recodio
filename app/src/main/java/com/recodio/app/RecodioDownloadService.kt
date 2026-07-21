package com.recodio.app

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.yausername.ffmpeg.FFmpeg
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDL.CanceledException
import com.yausername.youtubedl_android.YoutubeDLRequest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.File
import java.util.regex.Pattern

// Runs analysis and downloads as a foreground Service so the process (and DownloadState)
// survives the app being backgrounded or the Activity being destroyed mid-download - the
// #1 gap the pure-ViewModel version had versus the desktop app.
class RecodioDownloadService : Service() {

    companion object {
        const val CHANNEL_ID = "recodio_downloads"
        const val NOTIF_ID = 1
        const val ACTION_ANALYZE = "com.recodio.app.ANALYZE"
        const val ACTION_START_QUEUE = "com.recodio.app.START_QUEUE"
        const val ACTION_CANCEL = "com.recodio.app.CANCEL"
        const val EXTRA_URL = "url"

        private val ERROR_LINE = Pattern.compile("^ERROR:")
        private const val MAX_ABORT_RETRIES = 2
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null
    private val processId = "recodio-download"
    private var initialized = false

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_ANALYZE -> {
                val url = intent.getStringExtra(EXTRA_URL) ?: return START_NOT_STICKY
                startForegroundNotif("Analizando...")
                job = scope.launch {
                    analyze(url)
                    stopForegroundAndSelf()
                }
            }
            ACTION_CANCEL -> {
                runCatching { YoutubeDL.getInstance().destroyProcessById(processId) }
                job?.cancel()
            }
            ACTION_START_QUEUE -> {
                startForegroundNotif("Descargando...")
                job = scope.launch {
                    runQueue()
                    stopForegroundAndSelf()
                }
            }
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    // ---------- notification plumbing ----------

    private fun createChannel() {
        val channel = NotificationChannel(CHANNEL_ID, "Descargas", NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun buildNotif(text: String, indeterminate: Boolean, percent: Int) =
        NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Recodio")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setOngoing(true)
            .setProgress(100, percent, indeterminate)
            .build()

    private fun startForegroundNotif(text: String) {
        val notif = buildNotif(text, indeterminate = true, percent = 0)
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIF_ID, notif)
        }
    }

    private fun updateNotif(text: String, percent: Int) {
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, buildNotif(text, false, percent))
    }

    private fun stopForegroundAndSelf() {
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    // ---------- yt-dlp plumbing ----------

    private fun ensureInit() {
        if (initialized) return
        YoutubeDL.getInstance().init(applicationContext)
        FFmpeg.getInstance().init(applicationContext)
        initialized = true
    }

    private val downloadDir: File
        get() {
            // The Service can outlive the Activity that set DownloadState.downloadDirPath (or
            // even start fresh after process death), so fall back to the persisted prefs value
            // directly rather than trusting only the in-memory singleton.
            val chosen = DownloadState.downloadDirPath ?: DownloadDirPrefs.load(applicationContext)
            return chosen?.let { File(it) } ?: File(getExternalFilesDir(null), "Recodio")
        }

    // Mirrors YtDlpDownloader.AnalyzeAsync on desktop: --flat-playlist -J, then hand-parsed
    // JSON (the library's VideoInfo mapper doesn't expose a playlist's "entries" array).
    private fun analyze(url: String) {
        DownloadState.analyzing = true
        DownloadState.resetAnalysis()
        try {
            ensureInit()
            val request = YoutubeDLRequest(url).apply {
                addOption("--flat-playlist")
                addOption("-J")
                addOption("--no-warnings")
            }
            val response = YoutubeDL.getInstance().execute(request, processId)
            val root = JSONObject(response.out)
            val entriesArr = root.optJSONArray("entries")
            if (entriesArr != null) {
                DownloadState.analyzedUrl = url
                DownloadState.analyzedPlaylistTitle = if (root.isNull("title")) null else root.optString("title")
                for (i in 0 until entriesArr.length()) {
                    val e = entriesArr.getJSONObject(i)
                    val title = if (e.isNull("title")) "Video ${i + 1}" else e.optString("title")
                    DownloadState.analyzedEntries.add(PlaylistEntry(i + 1, title))
                }
                DownloadState.status = "Playlist: ${DownloadState.analyzedPlaylistTitle} (${entriesArr.length()} videos) - desmarca los que no quieras"
            } else {
                DownloadState.analyzedUrl = url
                val title = if (root.isNull("title")) url else root.optString("title")
                DownloadState.analyzedEntries.add(PlaylistEntry(1, title))
                DownloadState.status = "Video individual detectado."
            }
        } catch (e: CanceledException) {
            DownloadState.status = "Analisis cancelado."
        } catch (e: Exception) {
            DownloadState.status = "No se pudo analizar la URL: ${e.message}"
        } finally {
            DownloadState.analyzing = false
        }
    }

    // Empty queue = "just download whatever's typed/analyzed right now", same convention as
    // the desktop spotDL dialog.
    private fun runQueue() {
        val items = if (DownloadState.queue.isNotEmpty()) {
            DownloadState.queue.toList()
        } else {
            val url = DownloadState.url.trim()
            if (url.isEmpty()) {
                DownloadState.status = "Pega una URL o agrega items a la cola."
                return
            }
            // Only trust the analyzed entries if they're for THIS url - otherwise the field
            // was edited after analyzing and they'd be stale.
            val matchesAnalysis = DownloadState.analyzedUrl == url
            val isPlaylist = matchesAnalysis && DownloadState.analyzedEntries.size > 1
            val label = if (matchesAnalysis) DownloadState.analyzedPlaylistTitle ?: url else url
            val selected = if (isPlaylist) {
                DownloadState.analyzedEntries.filter { it.selected }.map { it.index }
                    .takeIf { it.size < DownloadState.analyzedEntries.size && it.isNotEmpty() }
            } else null
            listOf(QueueItem(label, url, selected, isPlaylist))
        }

        DownloadState.running = true
        DownloadState.progress = 0f
        DownloadState.log = ""
        DownloadState.appendLog(">> Carpeta de destino: ${downloadDir.absolutePath}")

        var totalOk = 0
        var totalFail = 0
        try {
            ensureInit()
            downloadDir.mkdirs()

            for ((i, item) in items.withIndex()) {
                // A small gap between consecutive requests, even for loose videos back-to-back
                // in a queue - burst traffic from the same IP is exactly what trips YouTube's
                // throttling, and a personal download rarely needs to start within milliseconds
                // of the previous one finishing.
                if (i > 0) Thread.sleep(2_000)

                DownloadState.status = if (items.size > 1) "Cola ${i + 1} de ${items.size}: ${item.label}" else "Descargando..."
                DownloadState.appendLog(">> ${item.label}")
                updateNotif(item.label, 0)

                val failCount = downloadWithRetry(item)
                totalFail += failCount
                if (failCount == 0) totalOk++
            }

            val summary = if (totalFail > 0)
                "Descarga completa: ${items.size - totalFail} ok, $totalFail con error (revisa el log)."
            else
                "Descarga completa."
            DownloadState.status = summary
            DownloadState.appendLog(">> $summary")
            DownloadState.queue.clear()
        } catch (e: CanceledException) {
            DownloadState.status = "Descarga cancelada."
            DownloadState.appendLog(">> Descarga cancelada.")
        } catch (e: Exception) {
            DownloadState.status = "Error al descargar."
            DownloadState.appendLog(">> ERROR: ${e.message}")
        } finally {
            DownloadState.running = false
        }
    }

    // Retries on a total failure (YouTube bot-detection / 403s blocking every video in the
    // batch), same backoff intent as YtDlpDownloader.DownloadAsync on desktop. --no-overwrites
    // makes a retry cheap: finished files are skipped, partials resumed.
    //
    // Unlike the desktop yt-dlp.exe, this library doesn't surface a negative exit code for an
    // aborted run - execute() just throws YoutubeDLException directly (confirmed by testing: a
    // real HTTP 403 came back as a thrown exception, never as a populated response object), so
    // that's the actual signal to retry on, not an exit-code check.
    private fun downloadWithRetry(item: QueueItem): Int {
        for (attempt in 1..(MAX_ABORT_RETRIES + 1)) {
            try {
                return downloadOnce(item)
            } catch (e: CanceledException) {
                throw e
            } catch (e: Exception) {
                if (attempt > MAX_ABORT_RETRIES) throw RuntimeException(e.message ?: "yt-dlp fallo")
                val waitMs = attempt * 10_000L
                DownloadState.appendLog(">> Fallo (${e.message?.take(100)}). Reintentando en ${waitMs / 1000}s... ($attempt/$MAX_ABORT_RETRIES)")
                Thread.sleep(waitMs)
            }
        }
        return 0
    }

    private fun downloadOnce(item: QueueItem): Int {
        // item.isPlaylist is known ahead of time from our own analysis, so the template only
        // ever references %(playlist_title)s when it's guaranteed to actually be set - no need
        // for --output-na-placeholder at all. (That flag's empty-string value tripped a bug in
        // the request builder's option marshalling: passing "" as a value silently dropped it,
        // so yt-dlp's argparse consumed the NEXT flag, --embed-metadata, as the placeholder's
        // value instead - literally created a folder named "--embed-metadata" on disk.)
        val outputTemplate = if (DownloadState.organizeInFolders && item.isPlaylist)
            "%(playlist_title)s/%(title)s.%(ext)s"
        else
            "%(title)s.%(ext)s"

        val request = YoutubeDLRequest(item.url).apply {
            addOption("--no-warnings")
            // No explicit --ffmpeg-location: FFmpeg.getInstance().init() already wires yt-dlp
            // to the bundled binary (proven working - the mp3 conversion test succeeded
            // without this flag).
            addOption("-o", "${downloadDir.absolutePath}/$outputTemplate")
            addOption("--embed-metadata")
            addOption("--ignore-errors")
            addOption("--no-overwrites")
            addOption("--retries", "10")
            addOption("--fragment-retries", "10")
            addOption("--extractor-retries", "3")

            if (DownloadState.audioOnly) {
                addOption("-x")
                addOption("--audio-format", "mp3")
                addOption("--audio-quality", "0")
                addOption("-f", "bestaudio/best")
            } else {
                addOption("--embed-thumbnail")
                addOption("-f", "bestvideo+bestaudio/best")
                addOption("--merge-output-format", "mp4")
            }

            if (item.isPlaylist) {
                addOption("--sleep-requests", "0.75")
                addOption("--sleep-interval", "1")
                addOption("--max-sleep-interval", "3")
            }
            if (DownloadState.sponsorBlock) {
                addOption("--sponsorblock-remove", "sponsor,selfpromo,interaction")
            }
            item.selectedIndices?.let { addOption("--playlist-items", it.joinToString(",")) }
        }

        var failCount = 0
        // execute() throws directly on total failure (see downloadWithRetry) - a returned
        // response here always means the process actually ran to completion, even if some
        // individual videos in the batch failed (that's what failCount / --ignore-errors is for).
        YoutubeDL.getInstance().execute(request, processId) { pct, _, line ->
            if (pct >= 0) {
                DownloadState.progress = pct / 100f
                updateNotif(item.label, pct.toInt())
            }
            if (line.isNotBlank()) {
                val trimmed = line.trim()
                DownloadState.appendLog(trimmed)
                if (ERROR_LINE.matcher(trimmed).find()) failCount++
            }
        }
        return failCount
    }
}

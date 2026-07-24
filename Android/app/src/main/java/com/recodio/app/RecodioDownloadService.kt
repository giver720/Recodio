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
        private val ALREADY_LINE = Pattern.compile("has already been (recorded in the archive|downloaded)")
        private val DEST_LINE = Pattern.compile("^\\[download] Destination:")
        private const val MAX_ABORT_RETRIES = 2
        // Full-queue retry passes over whatever's still ERROR after the first pass, same idea
        // as desktop's YtDlpBatchPasses (default 2) - a second pass often succeeds on items that
        // failed transiently (rate-limit blip, flaky connection) without the user re-triggering
        // the whole download by hand.
        private const val MAX_QUEUE_PASSES = 2
    }

    private class ItemResult(val failCount: Int, val skipped: Boolean)

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
        YtDlpUpdater.updateIfDue(applicationContext) { DownloadState.status = it.removePrefix(">> ") }
    }

    // Playlist detection without a prior analysis pass - a plain URL-shape check, not a network
    // call, so it's cheap to run on every download.
    private fun looksLikePlaylistUrl(url: String): Boolean =
        url.contains("list=", ignoreCase = true) || url.contains("/playlist", ignoreCase = true)

    private val downloadDir: File
        get() {
            // The Service can outlive the Activity that set DownloadState.downloadDirPath (or
            // even start fresh after process death), so fall back to the persisted prefs value
            // directly rather than trusting only the in-memory singleton.
            val chosen = DownloadState.downloadDirPath ?: DownloadDirPrefs.load(applicationContext)
            return chosen?.let { File(it) } ?: File(getExternalFilesDir(null), "Recodio")
        }

    // Applies the user-imported cookies.txt (if any) to a request - needed for any site that
    // gates content behind login or an age check, not just YouTube. Without this, "descarga de
    // cualquier sitio" is only true for public content.
    private fun addCookiesIfAny(request: YoutubeDLRequest) {
        val cookies = CookiesPrefs.cookiesFile(applicationContext)
        if (cookies.exists()) request.addOption("--cookies", cookies.absolutePath)
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
            addCookiesIfAny(request)
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
            // If the user never hit "Analizar" (or edited the URL after), fall back to a plain
            // URL-shape check - without this, a playlist link downloaded without prior analysis
            // silently skipped both the subfolder template AND the --sleep-requests/
            // --sleep-interval throttling below, which is exactly the kind of unthrottled batch
            // request pattern that gets an IP rate-limited.
            val isPlaylist = if (matchesAnalysis) DownloadState.analyzedEntries.size > 1 else looksLikePlaylistUrl(url)
            val label = if (matchesAnalysis) DownloadState.analyzedPlaylistTitle ?: url else url
            val selected = if (isPlaylist) {
                DownloadState.analyzedEntries.filter { it.selected }.map { it.index }
                    .takeIf { it.size < DownloadState.analyzedEntries.size && it.isNotEmpty() }
            } else null
            listOf(QueueItem(label, url, selected, isPlaylist))
        }

        DownloadState.running = true
        DownloadState.progress = 0f
        DownloadState.etaText = null
        DownloadState.passIndex = 1
        DownloadState.passTotal = 1
        DownloadState.downloadItems.clear()
        items.forEach { DownloadState.downloadItems.add(DownloadItemUi(it.label)) }

        var totalOk = 0
        var totalSkipped = 0
        var totalFail = 0
        val queueStart = System.currentTimeMillis()
        try {
            ensureInit()
            downloadDir.mkdirs()

            // indices left to (re)try in this pass - starts as the whole queue, shrinks to just
            // the failures between passes.
            var pending = items.indices.toMutableList()
            var pass = 1
            // Total attempts across all passes, for the ETA average - simpler and honest about
            // what's actually happening (a retried item takes real time twice) rather than
            // trying to project against the original queue size once passes diverge from it.
            val totalAttempts = pending.size
            var attemptsDone = 0
            while (pending.isNotEmpty() && pass <= MAX_QUEUE_PASSES) {
                DownloadState.passIndex = pass
                val stillFailing = mutableListOf<Int>()

                for ((n, i) in pending.withIndex()) {
                    val item = items[i]
                    // A small gap between consecutive requests, even for loose videos back-to-back
                    // in a queue - burst traffic from the same IP is exactly what trips YouTube's
                    // throttling, and a personal download rarely needs to start within milliseconds
                    // of the previous one finishing.
                    if (n > 0 || pass > 1) Thread.sleep(2_000)

                    val passLabel = if (pass > 1) " (pasada $pass)" else ""
                    DownloadState.status = if (items.size > 1)
                        "Cola ${n + 1} de ${pending.size}$passLabel: ${item.label}"
                    else "Descargando...$passLabel"
                    DownloadState.downloadItems[i].status = ItemStatus.DOWNLOADING
                    DownloadState.downloadItems[i].errorDetail = null
                    updateNotif(item.label, 0)

                    // Base for the overall bar before this item starts - captured here (not read
                    // lazily inside the lambda after the fact) so it reflects attemptsDone at
                    // the moment this item began, not whatever it drifts to later.
                    val completedBase = attemptsDone
                    val result = downloadWithRetry(item, i) { frac ->
                        DownloadState.progress = ((completedBase + frac) / totalAttempts).coerceIn(0f, 1f)
                    }
                    val finalState = when {
                        result.failCount > 0 -> ItemStatus.ERROR
                        result.skipped -> ItemStatus.SKIPPED
                        else -> ItemStatus.DONE
                    }
                    DownloadState.downloadItems[i].status = finalState
                    if (finalState == ItemStatus.ERROR) stillFailing.add(i)

                    attemptsDone++
                    DownloadState.progress = (attemptsDone.toFloat() / totalAttempts).coerceIn(0f, 1f)
                    updateEta(queueStart, totalAttempts, attemptsDone)
                }

                pending = stillFailing
                if (pending.isNotEmpty() && pass < MAX_QUEUE_PASSES) {
                    DownloadState.passTotal = pass + 1
                }
                pass++
            }

            DownloadState.downloadItems.forEach {
                when (it.status) {
                    ItemStatus.DONE -> totalOk++
                    ItemStatus.SKIPPED -> totalSkipped++
                    ItemStatus.ERROR -> totalFail++
                    else -> {}
                }
            }

            val parts = mutableListOf("$totalOk ok")
            if (totalSkipped > 0) parts.add("$totalSkipped omitidos")
            if (totalFail > 0) parts.add("$totalFail con error")
            val summary = "Descarga completa: ${parts.joinToString(", ")}" + if (totalFail > 0) " (toca un item en rojo para ver el motivo)." else "."
            DownloadState.status = summary
            DownloadState.queue.clear()
        } catch (e: CanceledException) {
            DownloadState.status = "Descarga cancelada."
            DownloadState.downloadItems.firstOrNull { it.status == ItemStatus.DOWNLOADING }?.status = ItemStatus.ERROR
        } catch (e: Exception) {
            DownloadState.status = "Error al descargar."
            DownloadState.downloadItems.firstOrNull { it.status == ItemStatus.DOWNLOADING }?.status = ItemStatus.ERROR
        } finally {
            DownloadState.running = false
            DownloadState.etaText = null
        }
    }

    // Recomputed after each item finishes from the running average seconds/item so far - null
    // until at least one item has completed, same convention as desktop's ETA.
    private fun updateEta(queueStart: Long, totalItems: Int, completedSoFar: Int) {
        if (completedSoFar <= 0) return
        val elapsedSec = (System.currentTimeMillis() - queueStart) / 1000.0
        val perItem = elapsedSec / completedSoFar
        val remaining = (totalItems - completedSoFar).coerceAtLeast(0)
        if (remaining == 0) {
            DownloadState.etaText = null
            return
        }
        val etaSec = (perItem * remaining).toInt()
        val mm = etaSec / 60
        val ss = etaSec % 60
        DownloadState.etaText = "ETA total %d:%02d".format(mm, ss)
    }

    // Retries on a total failure (YouTube bot-detection / 403s blocking every video in the
    // batch), same backoff intent as YtDlpDownloader.DownloadAsync on desktop. --no-overwrites
    // makes a retry cheap: finished files are skipped, partials resumed.
    //
    // Unlike the desktop yt-dlp.exe, this library doesn't surface a negative exit code for an
    // aborted run - execute() just throws YoutubeDLException directly (confirmed by testing: a
    // real HTTP 403 came back as a thrown exception, never as a populated response object), so
    // that's the actual signal to retry on, not an exit-code check.
    private fun downloadWithRetry(item: QueueItem, index: Int, onFileProgress: (Float) -> Unit): ItemResult {
        for (attempt in 1..(MAX_ABORT_RETRIES + 1)) {
            try {
                return downloadOnce(item, index, onFileProgress)
            } catch (e: CanceledException) {
                throw e
            } catch (e: Exception) {
                if (attempt > MAX_ABORT_RETRIES) {
                    // Mark just THIS item as failed instead of throwing - a single permanently
                    // broken item (bad URL, geoblock) shouldn't abort the rest of the queue, and
                    // this is what lets the "retry pass" collect it and move on to other items.
                    val msg = e.message ?: "yt-dlp fallo"
                    DownloadState.downloadItems[index].errorDetail = msg.take(300)
                    return ItemResult(failCount = 1, skipped = false)
                }
                val waitMs = attempt * 10_000L
                Thread.sleep(waitMs)
            }
        }
        return ItemResult(failCount = 1, skipped = false)
    }

    private fun downloadOnce(item: QueueItem, index: Int, onFileProgress: (Float) -> Unit): ItemResult {
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

        // Single archive file for the whole download folder (not per-playlist-subfolder like
        // desktop) - simpler given the library only exposes one flat downloadDir, and still
        // does the job: yt-dlp skips any id it already recorded, regardless of which item in
        // the queue asks for it again.
        val archiveFile = File(downloadDir, ".recodio_archive.txt")

        val request = YoutubeDLRequest(item.url).apply {
            addOption("--no-warnings")
            // No explicit --ffmpeg-location: FFmpeg.getInstance().init() already wires yt-dlp
            // to the bundled binary (proven working - the mp3 conversion test succeeded
            // without this flag).
            addOption("-o", "${downloadDir.absolutePath}/$outputTemplate")
            addOption("--embed-metadata")
            addOption("--ignore-errors")
            addOption("--no-overwrites")
            addOption("--download-archive", archiveFile.absolutePath)
            addOption("--retries", "10")
            addOption("--fragment-retries", "10")
            addOption("--extractor-retries", "3")
            addCookiesIfAny(this)

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
                // Without this, yt-dlp resolves metadata for EVERY entry up front before the
                // first byte downloads - on a 20+ video playlist that's a long stretch where the
                // UI shows 0% with nothing visibly happening. This starts downloading each entry
                // as soon as its own metadata is ready instead.
                addOption("--lazy-playlist")
            }
            if (DownloadState.sponsorBlock) {
                addOption("--sponsorblock-remove", "sponsor,selfpromo,interaction")
            }
            item.selectedIndices?.let { addOption("--playlist-items", it.joinToString(",")) }
        }

        var failCount = 0
        var alreadyCount = 0
        var newDownloadCount = 0
        var lastErrorLine: String? = null
        // execute() throws directly on total failure (see downloadWithRetry) - a returned
        // response here always means the process actually ran to completion, even if some
        // individual videos in the batch failed (that's what failCount / --ignore-errors is for).
        YoutubeDL.getInstance().execute(request, processId) { pct, _, line ->
            if (pct >= 0) {
                onFileProgress(pct / 100f)
                DownloadState.downloadItems[index].progress = pct / 100f
                updateNotif(item.label, pct.toInt())
            }
            if (line.isNotBlank()) {
                val trimmed = line.trim()
                when {
                    ERROR_LINE.matcher(trimmed).find() -> {
                        failCount++
                        lastErrorLine = trimmed
                    }
                    ALREADY_LINE.matcher(trimmed).find() -> alreadyCount++
                    DEST_LINE.matcher(trimmed).find() -> newDownloadCount++
                }
            }
        }
        if (failCount > 0) {
            DownloadState.downloadItems[index].errorDetail = lastErrorLine?.take(300)
        }
        val skipped = failCount == 0 && newDownloadCount == 0 && alreadyCount > 0
        return ItemResult(failCount = failCount, skipped = skipped)
    }
}

package com.recodio.app

import android.app.DownloadManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.Uri
import android.os.Build
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

// Self-update via the public GitHub Releases API - the repo is public specifically so this
// works with zero embedded credentials (a token baked into a distributed APK is trivially
// extractable by decompiling it, so that was never an option).
object AppUpdateChecker {
    private const val REPO = "giver720/RecodioAndroid"
    private const val PREFS = "recodio_prefs"
    private const val KEY_LAST_CHECK = "last_app_update_check"
    private const val CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000L

    // Silent daily check on app start - only speaks up if an update is actually found.
    suspend fun checkIfDue(context: Context) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val now = System.currentTimeMillis()
        if (now - prefs.getLong(KEY_LAST_CHECK, 0L) < CHECK_INTERVAL_MS) return
        checkNow(context, silent = true)
        prefs.edit().putLong(KEY_LAST_CHECK, now).apply()
    }

    suspend fun checkNow(context: Context, silent: Boolean = false) = withContext(Dispatchers.IO) {
        UpdateState.checking = true
        if (!silent) UpdateState.status = "Buscando actualizaciones..."
        try {
            val json = (URL("https://api.github.com/repos/$REPO/releases/latest")
                .openConnection() as HttpURLConnection).run {
                requestMethod = "GET"
                setRequestProperty("Accept", "application/vnd.github+json")
                connectTimeout = 10_000
                readTimeout = 10_000
                inputStream.bufferedReader().use { it.readText() }
            }
            val root = JSONObject(json)
            val remoteVersion = root.optString("tag_name").removePrefix("v")

            var apkUrl: String? = null
            root.optJSONArray("assets")?.let { assets ->
                for (i in 0 until assets.length()) {
                    val asset = assets.getJSONObject(i)
                    if (asset.optString("name").endsWith(".apk")) {
                        apkUrl = asset.optString("browser_download_url")
                        break
                    }
                }
            }

            val currentVersion = BuildConfig.VERSION_NAME
            if (apkUrl != null && remoteVersion.isNotBlank() && isNewer(remoteVersion, currentVersion)) {
                UpdateState.availableVersion = remoteVersion
                UpdateState.downloadUrl = apkUrl
                UpdateState.status = "Nueva version disponible: $remoteVersion"
            } else if (!silent) {
                UpdateState.status = "Ya estas en la ultima version ($currentVersion)."
            }
        } catch (e: Exception) {
            if (!silent) UpdateState.status = "No se pudo buscar actualizaciones (${e.message})."
        } finally {
            UpdateState.checking = false
        }
    }

    // Plain dotted-integer comparison (1.0.10 > 1.0.9) - good enough since we control both the
    // release tags and versionName ourselves and always keep them in that shape.
    private fun isNewer(remote: String, current: String): Boolean {
        val r = remote.split(".").map { it.toIntOrNull() ?: 0 }
        val c = current.split(".").map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(r.size, c.size)) {
            val rv = r.getOrElse(i) { 0 }
            val cv = c.getOrElse(i) { 0 }
            if (rv != cv) return rv > cv
        }
        return false
    }

    // DownloadManager instead of a hand-rolled HTTP stream: it survives the app being
    // backgrounded, shows its own progress notification, and its content:// URI for the
    // finished file is exactly what the install intent needs - no FileProvider setup required.
    fun startDownload(context: Context, url: String, version: String) {
        val appContext = context.applicationContext
        val dm = appContext.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        val request = DownloadManager.Request(Uri.parse(url))
            .setTitle("Recodio $version")
            .setDescription("Descargando actualizacion")
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setDestinationInExternalFilesDir(appContext, null, "Recodio-update.apk")
            .setMimeType("application/vnd.android.package-archive")
        val downloadId = dm.enqueue(request)
        UpdateState.downloading = true
        UpdateState.status = "Descargando actualizacion..."

        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                if (intent.getLongExtra(DownloadManager.EXTRA_DOWNLOAD_ID, -1) != downloadId) return
                appContext.unregisterReceiver(this)
                UpdateState.downloading = false

                val apkUri = dm.getUriForDownloadedFile(downloadId)
                if (apkUri == null) {
                    UpdateState.status = "La descarga de la actualizacion fallo."
                    return
                }
                UpdateState.status = "Descarga completa, instalando..."
                appContext.startActivity(Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(apkUri, "application/vnd.android.package-archive")
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)
                })
            }
        }
        val filter = IntentFilter(DownloadManager.ACTION_DOWNLOAD_COMPLETE)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            appContext.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            appContext.registerReceiver(receiver, filter)
        }
    }
}

package com.recodio.app

import android.content.Context
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLException

// YouTube changes its extraction defenses often enough that an outdated bundled yt-dlp is the
// single biggest cause of "everything suddenly gets blocked" - much more than the IP itself.
// The library ships its own self-update mechanism (downloads a fresh yt-dlp release into app
// storage), so we just need to call it periodically instead of waiting for a full app update.
object YtDlpUpdater {
    private const val PREFS = "recodio_prefs"
    private const val KEY_LAST_CHECK = "last_ytdlp_update_check"
    private const val CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000L

    fun updateIfDue(context: Context, onLog: (String) -> Unit) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val now = System.currentTimeMillis()
        if (now - prefs.getLong(KEY_LAST_CHECK, 0L) < CHECK_INTERVAL_MS) return

        try {
            onLog(">> Buscando actualizaciones de yt-dlp...")
            val status = YoutubeDL.getInstance().updateYoutubeDL(context, YoutubeDL.UpdateChannel.STABLE)
            onLog(
                when (status) {
                    YoutubeDL.UpdateStatus.DONE -> ">> yt-dlp actualizado a la ultima version."
                    YoutubeDL.UpdateStatus.ALREADY_UP_TO_DATE -> ">> yt-dlp ya estaba al dia."
                    else -> ">> yt-dlp: chequeo de actualizacion completo."
                }
            )
            // Only mark as checked on success - a transient failure (no network yet, etc.)
            // shouldn't cost a full day before the next retry.
            prefs.edit().putLong(KEY_LAST_CHECK, now).apply()
        } catch (e: YoutubeDLException) {
            onLog(">> No se pudo actualizar yt-dlp (${e.message?.take(120)}). Se reintentara mas tarde.")
        } catch (e: Exception) {
            onLog(">> No se pudo actualizar yt-dlp (${e.message?.take(120)}).")
        }
    }
}

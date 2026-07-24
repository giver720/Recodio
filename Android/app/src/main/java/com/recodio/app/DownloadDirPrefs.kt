package com.recodio.app

import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import java.io.File

// Persists the user's chosen download folder across app restarts (DownloadState itself
// is an in-memory singleton that resets whenever the process dies).
object DownloadDirPrefs {
    private const val PREFS = "recodio_prefs"
    private const val KEY_PATH = "download_dir_path"

    fun load(context: Context): String? =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_PATH, null)

    fun save(context: Context, path: String?) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_PATH, path)
            .apply()
    }

    // Translates a SAF tree URI (from ACTION_OPEN_DOCUMENT_TREE) into a real absolute
    // filesystem path so yt-dlp's native writer can target it directly. Only reliable for
    // the primary storage volume - a folder picked on a removable SD card returns null and
    // the caller falls back to the app-private default.
    fun uriToPath(treeUri: Uri): String? {
        val docId = DocumentsContract.getTreeDocumentId(treeUri)
        val split = docId.split(":")
        if (split.size < 2 || !split[0].equals("primary", ignoreCase = true)) return null
        return File(Environment.getExternalStorageDirectory(), split[1]).absolutePath
    }
}

package com.recodio.app

import android.content.Context
import java.io.File

// Netscape cookies.txt imported by the user (exported from their browser via an extension like
// "Get cookies.txt"), same mechanism as desktop's CookiesFile fallback. Needed for sites yt-dlp
// otherwise can't reach at all - age-gated YouTube, private/friends-only content, Instagram,
// X/Twitter, etc. There's no Android equivalent of desktop's automatic browser-DPAPI cookie
// read (that's a Windows-only trick), so this is a manual one-time import instead.
object CookiesPrefs {
    fun cookiesFile(context: Context): File = File(context.filesDir, "cookies.txt")

    fun hasCookies(context: Context): Boolean = cookiesFile(context).exists()

    fun importFrom(context: Context, input: java.io.InputStream) {
        cookiesFile(context).outputStream().use { out -> input.copyTo(out) }
    }

    fun clear(context: Context) {
        cookiesFile(context).delete()
    }
}

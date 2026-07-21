package com.recodio.app

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

// Mirrors DownloadState's pattern: a plain Compose-observable singleton, safe to touch from
// the background coroutine doing the network check/download.
object UpdateState {
    var checking by mutableStateOf(false)
    var downloading by mutableStateOf(false)
    var status by mutableStateOf("")
    var availableVersion by mutableStateOf<String?>(null)
    var downloadUrl by mutableStateOf<String?>(null)
}

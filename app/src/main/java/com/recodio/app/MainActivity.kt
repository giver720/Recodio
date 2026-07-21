package com.recodio.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

class MainActivity : ComponentActivity() {

    private val notifPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* no-op either way */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notifPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                RecodioScreen()
            }
        }
    }
}

private fun startService(activity: ComponentActivity, action: String, url: String? = null) {
    val intent = Intent(activity, RecodioDownloadService::class.java).apply {
        this.action = action
        url?.let { putExtra(RecodioDownloadService.EXTRA_URL, it) }
    }
    activity.startForegroundService(intent)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RecodioScreen() {
    val activity = androidx.compose.ui.platform.LocalContext.current as ComponentActivity
    val s = DownloadState

    Scaffold(topBar = { TopAppBar(title = { Text("Recodio") }) }) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            OutlinedTextField(
                value = s.url,
                onValueChange = { s.url = it },
                label = { Text("URL de YouTube (video o playlist)") },
                singleLine = true,
                enabled = !s.running && !s.analyzing,
                modifier = Modifier.fillMaxWidth()
            )

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(
                    onClick = { startService(activity, RecodioDownloadService.ACTION_ANALYZE, s.url.trim()) },
                    enabled = !s.running && !s.analyzing && s.url.isNotBlank()
                ) { Text(if (s.analyzing) "Analizando..." else "Analizar") }

                TextButton(
                    onClick = {
                        val url = s.url.trim()
                        if (url.isNotEmpty()) {
                            // Only trust the analyzed entries if they're actually for THIS url -
                            // otherwise the user edited the field after analyzing and they'd be
                            // stale (wrong playlist, wrong selection).
                            val matchesAnalysis = s.analyzedUrl == url
                            val isPlaylist = matchesAnalysis && s.analyzedEntries.size > 1
                            val label = if (matchesAnalysis) s.analyzedPlaylistTitle ?: url else url
                            val selected = if (isPlaylist) {
                                s.analyzedEntries.filter { it.selected }.map { it.index }
                                    .takeIf { it.size < s.analyzedEntries.size && it.isNotEmpty() }
                            } else null
                            s.queue.add(QueueItem(label, url, selected, isPlaylist))
                        }
                    },
                    enabled = !s.running && s.url.isNotBlank()
                ) { Text("+ A la cola") }
            }

            if (s.analyzedEntries.size > 1) {
                Text(
                    "Playlist: ${s.analyzedPlaylistTitle} (${s.analyzedEntries.size} videos)",
                    style = MaterialTheme.typography.bodySmall
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    TextButton(onClick = { s.analyzedEntries.forEach { it.selected = true } }) { Text("Todos") }
                    TextButton(onClick = { s.analyzedEntries.forEach { it.selected = false } }) { Text("Ninguno") }
                }
                LazyColumn(modifier = Modifier.fillMaxWidth().height(120.dp)) {
                    items(s.analyzedEntries) { entry ->
                        Row {
                            Checkbox(checked = entry.selected, onCheckedChange = { entry.selected = it })
                            Text(entry.title, modifier = Modifier.padding(top = 12.dp))
                        }
                    }
                }
            }

            if (s.queue.isNotEmpty()) {
                Text("Cola (${s.queue.size}):", style = MaterialTheme.typography.bodySmall)
                LazyColumn(modifier = Modifier.fillMaxWidth().height(80.dp)) {
                    items(s.queue) { item -> Text("• ${item.label}", style = MaterialTheme.typography.bodySmall) }
                }
                TextButton(onClick = { s.queue.clear() }) { Text("Vaciar cola") }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(selected = s.audioOnly, onClick = { s.audioOnly = true }, label = { Text("MP3") }, enabled = !s.running)
                FilterChip(selected = !s.audioOnly, onClick = { s.audioOnly = false }, label = { Text("MP4") }, enabled = !s.running)
            }

            Row {
                Checkbox(checked = s.organizeInFolders, onCheckedChange = { s.organizeInFolders = it }, enabled = !s.running)
                Text("Subcarpeta por playlist", modifier = Modifier.padding(top = 12.dp))
            }
            Row {
                Checkbox(checked = s.sponsorBlock, onCheckedChange = { s.sponsorBlock = it }, enabled = !s.running)
                Text("Quitar sponsors (SponsorBlock)", modifier = Modifier.padding(top = 12.dp))
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { startService(activity, RecodioDownloadService.ACTION_START_QUEUE) },
                    enabled = !s.running && (s.url.isNotBlank() || s.queue.isNotEmpty())
                ) { Text("Descargar") }
                OutlinedButton(onClick = { startService(activity, RecodioDownloadService.ACTION_CANCEL) }, enabled = s.running) {
                    Text("Cancelar")
                }
            }

            LinearProgressIndicator(progress = { s.progress }, modifier = Modifier.fillMaxWidth())
            Text(s.status, style = MaterialTheme.typography.bodyMedium)

            val scroll = rememberScrollState()
            LaunchedEffect(s.log) { scroll.scrollTo(scroll.maxValue) }
            Text(
                s.log.ifEmpty { "(sin actividad todavia)" },
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f, fill = false)
                    .verticalScroll(scroll)
            )
        }
    }
}

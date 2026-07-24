package com.recodio.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    // Not tied to lifecycleScope on purpose - a startup update check that gets cancelled by a
    // quick screen rotation or activity recreation would just silently never run some days.
    private val updateCheckScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    private val notifPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* no-op either way */ }

    private val folderPickerLauncher =
        registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
            if (uri == null) return@registerForActivityResult
            contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            )
            val path = DownloadDirPrefs.uriToPath(uri)
            if (path == null) {
                DownloadState.status = "Esa carpeta no esta en el almacenamiento principal, elegi otra."
                return@registerForActivityResult
            }
            DownloadState.downloadDirPath = path
            DownloadDirPrefs.save(this, path)
        }

    private val cookiesPickerLauncher =
        registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
            if (uri == null) return@registerForActivityResult
            try {
                contentResolver.openInputStream(uri)?.use { CookiesPrefs.importFrom(this, it) }
                DownloadState.hasCookies = CookiesPrefs.hasCookies(this)
                DownloadState.status = if (DownloadState.hasCookies)
                    "Cookies importadas." else "No se pudo leer el archivo de cookies."
            } catch (e: Exception) {
                DownloadState.status = "No se pudo importar cookies.txt: ${e.message}"
            }
        }

    fun pickCookiesFile() {
        // text/plain covers a real cookies.txt export; "*/*" as a fallback since some browser
        // extensions save it without a recognized MIME type.
        cookiesPickerLauncher.launch(arrayOf("text/plain", "*/*"))
    }

    fun clearCookies() {
        CookiesPrefs.clear(this)
        DownloadState.hasCookies = false
        DownloadState.status = "Cookies eliminadas."
    }

    fun pickDownloadFolder() {
        if (Build.VERSION.SDK_INT >= 30 && !Environment.isExternalStorageManager()) {
            val intent = Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION).apply {
                data = Uri.parse("package:$packageName")
            }
            startActivity(intent)
            DownloadState.status = "Habilita \"Permitir acceso a todos los archivos\" para Recodio y volve a intentar."
            return
        }
        folderPickerLauncher.launch(null)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notifPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        DownloadState.downloadDirPath = DownloadDirPrefs.load(this)
        DownloadState.hasCookies = CookiesPrefs.hasCookies(this)
        updateCheckScope.launch { AppUpdateChecker.checkIfDue(this@MainActivity) }

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
    val activity = androidx.compose.ui.platform.LocalContext.current as MainActivity
    val s = DownloadState
    val u = UpdateState
    val scroll = rememberScrollState()
    val updateScope = rememberCoroutineScope()

    Scaffold(topBar = { TopAppBar(title = { Text("Recodio") }) }) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp)
                .verticalScroll(scroll),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            if (u.availableVersion != null) {
                Surface(
                    color = MaterialTheme.colorScheme.primaryContainer,
                    shape = RoundedCornerShape(8.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(10.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text("Version ${u.availableVersion} disponible", style = MaterialTheme.typography.bodySmall)
                        Button(
                            onClick = { AppUpdateChecker.startDownload(activity, u.downloadUrl!!, u.availableVersion!!) },
                            enabled = !u.downloading
                        ) { Text(if (u.downloading) "Descargando..." else "Actualizar") }
                    }
                }
            } else {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    TextButton(
                        onClick = { updateScope.launch { AppUpdateChecker.checkNow(activity, silent = false) } },
                        enabled = !u.checking && !u.downloading
                    ) { Text(if (u.checking) "Buscando..." else "Buscar actualizaciones") }
                    if (u.status.isNotBlank()) Text(u.status, style = MaterialTheme.typography.bodySmall)
                }
            }

            OutlinedTextField(
                value = s.url,
                onValueChange = { s.url = it },
                label = { Text("URL (YouTube, TikTok, Instagram, X, SoundCloud, Vimeo...)") },
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

            Text("Carpeta de descarga:", style = MaterialTheme.typography.bodySmall)
            Text(
                s.downloadDirPath ?: "Predeterminada (privada de la app)",
                style = MaterialTheme.typography.bodySmall
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { activity.pickDownloadFolder() }, enabled = !s.running) { Text("Elegir carpeta") }
                if (s.downloadDirPath != null) {
                    TextButton(
                        onClick = {
                            s.downloadDirPath = null
                            DownloadDirPrefs.save(activity, null)
                        },
                        enabled = !s.running
                    ) { Text("Restablecer") }
                }
            }

            Text(
                "Cookies (sitios con login o edad restringida):",
                style = MaterialTheme.typography.bodySmall
            )
            Text(
                if (s.hasCookies) "Cookies cargadas" else "Sin cookies - Instagram, X y videos con edad restringida las necesitan",
                style = MaterialTheme.typography.bodySmall
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { activity.pickCookiesFile() }, enabled = !s.running) {
                    Text(if (s.hasCookies) "Reemplazar cookies.txt" else "Importar cookies.txt")
                }
                if (s.hasCookies) {
                    TextButton(onClick = { activity.clearCookies() }, enabled = !s.running) { Text("Quitar") }
                }
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

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(s.status, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                if (s.passTotal > 1) {
                    Text(
                        "pasada ${s.passIndex}/${s.passTotal}",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.outline
                    )
                }
            }

            if (s.running || s.downloadItems.isNotEmpty()) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    LinearProgressIndicator(
                        progress = { s.progress },
                        modifier = Modifier.weight(1f).height(6.dp)
                    )
                    Text("${(s.progress * 100).toInt()}%", style = MaterialTheme.typography.labelSmall)
                }
                s.etaText?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.outline) }
            }

            if (s.downloadItems.isNotEmpty()) {
                Text("Descargas:", style = MaterialTheme.typography.bodySmall)
                Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    s.downloadItems.forEach { item -> DownloadItemRow(item) }
                }
            }
        }
    }
}

@Composable
private fun DownloadItemRow(item: DownloadItemUi) {
    val (dotColor, statusText) = when (item.status) {
        ItemStatus.QUEUED -> MaterialTheme.colorScheme.outline to "En cola"
        ItemStatus.DOWNLOADING -> MaterialTheme.colorScheme.primary to "${(item.progress * 100).toInt()}%"
        ItemStatus.DONE -> Color(0xFF4CAF50) to "Listo"
        ItemStatus.SKIPPED -> MaterialTheme.colorScheme.outline to "Omitido"
        ItemStatus.ERROR -> MaterialTheme.colorScheme.error to "Error"
    }
    var showError by remember { mutableStateOf(false) }
    if (showError && item.errorDetail != null) {
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { showError = false },
            confirmButton = { TextButton(onClick = { showError = false }) { Text("Cerrar") } },
            title = { Text(item.label, maxLines = 2, overflow = TextOverflow.Ellipsis) },
            text = { Text(item.errorDetail ?: "", style = MaterialTheme.typography.bodySmall) }
        )
    }

    Surface(
        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.4f),
        shape = RoundedCornerShape(8.dp),
        modifier = Modifier
            .fillMaxWidth()
            .then(
                if (item.status == ItemStatus.ERROR && item.errorDetail != null)
                    Modifier.clickable { showError = true }
                else Modifier
            )
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .clip(CircleShape)
                        .background(dotColor)
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    item.label,
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f)
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(statusText, style = MaterialTheme.typography.labelSmall, color = dotColor)
            }
            if (item.status == ItemStatus.DOWNLOADING) {
                LinearProgressIndicator(
                    progress = { item.progress },
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 4.dp)
                        .height(3.dp)
                )
            }
            if (item.status == ItemStatus.ERROR && item.errorDetail != null) {
                Text(
                    "Toca para ver el motivo",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
    }
}

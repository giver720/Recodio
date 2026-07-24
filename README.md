# Recodio

App de escritorio para Windows (WinForms, **.NET 10**) que unifica tres flujos de audio/video:

| Flujo | Qué hace |
|-------|----------|
| **spotDL** | Playlists, álbumes y canciones de **Spotify** (audio desde YouTube Music / YouTube / SoundCloud…) |
| **yt-dlp** | Video/audio de **cualquier sitio** que soporte yt-dlp (YouTube, X, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit, …) |
| **ffmpeg** | Conversión de formatos, carpeta vigilada (Spotube, etc.), drag & drop y menú contextual de Windows |

Corre en la **bandeja del sistema**, con historial unificado, chequeo de dependencias, tema oscuro/claro, una sola instancia (IPC por named pipe) y **auto-actualización** desde GitHub Releases.

**Versión actual: 1.3.23**

## Requisitos

- Windows 10/11 con [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [spotDL](https://github.com/spotDL/spotify-downloader): `pip install spotdl`
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) y [ffmpeg](https://ffmpeg.org/) (por ejemplo vía `winget`):

```powershell
winget install yt-dlp.yt-dlp
winget install Gyan.FFmpeg
pip install spotdl
```

## Instalación rápida

1. Descargá el ZIP de la [última release](https://github.com/giver720/Recodio/releases/latest)
2. Extraé y ejecutá `Recodio.exe`
3. (Opcional) En **Configuración**, activá el menú contextual y “Iniciar con Windows”

La app puede buscar actualizaciones solas desde Configuración → **Buscar actualización de Recodio**.

## Compilar desde el código

```powershell
cd Recodio
dotnet publish -c Release -r win-x64 --self-contained false
```

Salida: `Recodio/bin/Release/net10.0-windows/win-x64/publish/`

## Datos de usuario

A partir de **v1.1** la config y el historial viven en el perfil del usuario (no junto al exe), para evitar bloqueos de OneDrive/antivirus al actualizar:

| Qué | Ruta |
|-----|------|
| Config | `%AppData%\Recodio\config.json` |
| Historial | `%AppData%\Recodio\history.json` |
| Log | `%LocalAppData%\Recodio\watcher.log` |

Si tenías `config.json` / `history.json` al lado del ejecutable, se **migran una vez** al arrancar.

## Funciones (v1.3)

### General
- Bandeja del sistema con accesos a convertir, descargar media, spotDL, carpetas e historial
- Historial unificado (conversiones + yt-dlp + spotDL) con filtros
- Chequeo de dependencias (ffmpeg / yt-dlp / spotDL) al inicio y en Configuración
- Tema oscuro / claro / sistema (con oferta de reinicio al cambiar)
- Watch folder **automático** o manual; formato de conversión configurable
- Si el destino ya existe: omitir / sobrescribir / renombrar
- Autocompletar URL desde el portapapeles
- Auto-update de la app y de yt-dlp / spotDL
- **Cookies del navegador automáticas** (Brave por defecto si está instalado) para yt-dlp y spotDL

### yt-dlp (cualquier sitio)
- YouTube, X/Twitter, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit, y el resto de extractores de yt-dlp
- Formatos: MP4, MKV, mejor original, MP3, M4A (siempre con audio al merge)
- Cookies del navegador automáticas (login / age-gate)
- **Omite videos ya en la carpeta** al re-bajar playlists (escaneo de disco + archive)
- Subcarpeta por playlist + archivo **.m3u**
- Reintentos de playlist y por ítem; SponsorBlock (solo YouTube)
- Progreso con **%**, cola de items y estado (sin volcar el log crudo)

### spotDL (Spotify)
- **Analizar** playlist/álbum: lista tracks, desmarcá las que no quieras
- Proveedores de audio en fallback: YouTube Music → YouTube → SoundCloud (+ Bandcamp opcional)
- Mismas cookies del navegador que yt-dlp (menos bot-check)
- **Omite canciones ya en la carpeta** (match Artista - Título + archive)
- Reintentos por batch y canción por canción
- Cola de varias URLs, subcarpetas por playlist/álbum, SponsorBlock, letras Genius + Musixmatch
- Progreso con **%**, cola de URLs/canciones y estado actual

### Conversión (ffmpeg)
- Muchos formatos audio/video
- Drag & drop, menú contextual “Convertir con Recodio”
- Progreso por archivo y opción de borrar el original

## Changelog resumido

### 1.3.23
- Barra de progreso: cola con colores por estado (verde/rojo/gris/azul) en vez de solo texto plano
- Tooltip al pasar el mouse sobre un item fallido con el motivo real del error (yt-dlp y spotDL)
- ETA proyectada para toda la cola (no solo el archivo en curso)
- Se muestra la pasada de reintento actual ("pasada 2/2") en el panel, no solo en el log

### 1.3.22
- yt-dlp: fix real de "toda descarga termina en error" — `--no-write-all-thumbnails` no es una opcion valida de yt-dlp; el proceso rechazaba el comando completo antes de descargar nada
- yt-dlp: items sueltos (no playlist) ya no caen en una carpeta "NA" espuria cuando "Subcarpeta" esta activado

### 1.3.21
- yt-dlp playlists: descarga **item por item por URL directa** (termina de forma predecible)
- Sin sleeps largos de lista completa; reintentos de conexion con tope fijo

### 1.3.20
- yt-dlp: fix descargas que fallaban siempre (falsos "Permission denied" como cookies)
- Mejor deteccion de archivos bajados; no marcar permanentes por coincidencias parciales de id

### 1.3.19
- Scroll en ventanas yt-dlp y spotDL; botones fijos abajo

### 1.3.18
- yt-dlp: al borrar la carpeta de una playlist, el **archive se limpia** y se vuelve a descargar de verdad
- La verdad es el **archivo en disco** (no archive fantasma del padre)

### 1.3.17
- yt-dlp: **UI de reintentos** (Rapido / Equilibrado / Persistente / Personalizado)
- El usuario elige pasadas de lista, intentos por item, reintentos de conexion y `--retries`

### 1.3.16
- yt-dlp: **menos reintentos** (2 pasadas, 2 por item, 1 abort) y reintentos CLI más bajos
- Si un item falla tras los intentos, se marca y **no se reintenta en bucle**
- Éxito por archivo en disco, no solo archive

### 1.3.15
- yt-dlp (Recodio): archive **por carpeta de playlist**; seed solo por `[id]` (título estricto)
- Ignora archivos parciales (&lt;32KB / `.part`); fallos = sin archivo en disco
- Permanent fail solo si el id es de la selección; cookies DPAPI requieren 2 fallos

### 1.3.14
- App: settings se aplican a ventanas abiertas; aviso si cambia la URL analizada
- IPC: 2.ª instancia trae Recodio al frente; pipe multi-cliente
- Watch: ignora `.part` y carpetas de descarga activas; confirmar al cerrar con operación en curso
- No actualizar Recodio con descargas abiertas/en curso

### 1.3.13
- Progreso: barra de archivo no baja en merge video+audio; omitidos se mantienen ⏭
- spotDL cola multi-URL en checklist; totales ok más fiables; timer se detiene al cerrar

### 1.3.12
- Progreso: cancel/error no fuerza 100%; conserva velocidad/ETA; item de selección vs lote
- Checklist yt-dlp con ItemKey; cache de archive; spotDL prune + totales fijos

### 1.3.11
- yt-dlp: si borrás la carpeta de una descarga, el **archive se limpia** y el progreso arranca en 0 (re-descarga real)

### 1.3.10
- yt-dlp: **sin thumbnails** — no descarga, escribe ni embebe `.jpg`/imágenes de portada

### 1.3.9
- Progreso avanzado: **doble barra** (global + archivo), **velocidad / ETA / tamaño** (yt-dlp)
- Contadores **ok · omitidos · errores · pendientes** y checklist de cola con estados
- Temporizador y enlace **Abrir carpeta** al terminar
- Mismo panel en **conversión ffmpeg** (sin log grande)
- Callback tipado `DownloadProgressUpdate` en yt-dlp y spotDL

### 1.3.8
- **UI de progreso** en yt-dlp y spotDL: se eliminó el log de texto grande
- Barra con **porcentaje**, línea de **cola** (items/canciones/URLs) y estado del archivo/canción actual

### 1.3.7
- Cookies más robustas: export a `%AppData%\Recodio\cookies.txt`, reintento automático sin cookies si falla DPAPI
- Mensajes claros en español; link a carpeta de cookies en Configuración

### 1.3.6
- **Skip por carpeta**: yt-dlp y spotDL no re-descargan ítems que ya existen en el destino
- Escaneo de disco + archive; log de omitidos
- Bugfixes de estabilidad (watch, URL analizada, archive sintético, audio en merge, etc. desde 1.3.3–1.3.5)

### 1.3.2
- Cookies del navegador **globales y automáticas** (detecta Brave al primer arranque)
- Preferencia en Configuración + combos de yt-dlp/spotDL

### 1.3.1 / 1.3.0
- Fixes de playlists, watch, conversión, multi-sitio yt-dlp, preview spotDL

### 1.0.x
- Versión inicial: conversor + yt-dlp + spotDL, auto-update de tools y de la app

## Licencia

Uso personal / según lo indicado en el repositorio. Las herramientas externas (yt-dlp, spotDL, ffmpeg, Spotify) tienen sus propias licencias y términos de uso.

# Recodio

App de escritorio para Windows (WinForms, **.NET 10**) que unifica tres flujos de audio/video:

| Flujo | Qué hace |
|-------|----------|
| **spotDL** | Playlists, álbumes y canciones de **Spotify** (audio desde YouTube Music / YouTube / SoundCloud…) |
| **yt-dlp** | Video/audio de **cualquier sitio** que soporte yt-dlp (YouTube, X, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit, …) |
| **ffmpeg** | Conversión de formatos, carpeta vigilada (Spotube, etc.), drag & drop y menú contextual de Windows |

Corre en la **bandeja del sistema**, con historial unificado, chequeo de dependencias, tema oscuro/claro, una sola instancia (IPC por named pipe) y **auto-actualización** desde GitHub Releases.

**Versión actual: 1.3.8**

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

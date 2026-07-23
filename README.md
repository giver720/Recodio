# Recodio

App de escritorio para Windows (WinForms, **.NET 10**) que unifica tres flujos de audio/video:

| Flujo | Qué hace |
|-------|----------|
| **spotDL** | Playlists, álbumes y canciones de **Spotify** (audio desde YouTube Music / YouTube / SoundCloud…) |
| **yt-dlp** | Video/audio de **cualquier sitio** que soporte yt-dlp (YouTube, X, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit, …) |
| **ffmpeg** | Conversión de formatos, carpeta vigilada (Spotube, etc.), drag & drop y menú contextual de Windows |

Corre en la **bandeja del sistema**, con historial unificado, chequeo de dependencias, tema oscuro/claro, una sola instancia (IPC por named pipe) y **auto-actualización** desde GitHub Releases.

**Versión actual: 1.3.1**

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

### yt-dlp (cualquier sitio)
- YouTube, X/Twitter, TikTok, Instagram, SoundCloud, Vimeo, Twitch, Reddit, y el resto de extractores de yt-dlp
- Formatos: MP4, MKV, mejor original, MP3, M4A
- Cookies del navegador (login / age-gate)
- Archive + reintentos de playlist y por ítem
- SponsorBlock (solo YouTube)

### spotDL (Spotify)
- **Analizar** playlist/álbum: lista tracks, desmarcá las que no quieras
- Proveedores de audio en fallback: YouTube Music → YouTube → SoundCloud (+ Bandcamp opcional)
- Cookies del navegador vía yt-dlp (menos bot-check)
- Reintentos por batch y **canción por canción** con archive
- Cola de varias URLs, subcarpetas por playlist/álbum, SponsorBlock, letras Genius + Musixmatch

### Conversión (ffmpeg)
- Muchos formatos audio/video
- Drag & drop, menú contextual “Convertir con Recodio”
- Progreso por archivo y opción de borrar el original

## Changelog resumido

### 1.3.1
- Fixes: selección de 1 ítem en playlist yt-dlp, archive/ids sintéticos, cancelar conversión mata ffmpeg
- Watch: no saltea `.mp3` si el destino es otro formato; solo convierte media real
- spotDL: no reporta éxito si falló sin URLs de error; permanent-errors más precisos
- Settings: winget solo consulta estado (no instala); updater con timeout y ZIP anidado
- Estabilidad UI: logs/tray/debounce sin crash al cerrar

### 1.3.0
- spotDL: preview/Analizar, proveedores con fallback, cookies, reintentos por track
- yt-dlp: multi-sitio, más formatos, cookies, reintentos de playlist
- Watch automático, AppData, historial unificado, menú de bandeja enriquecido

### 1.0.x
- Versión inicial: conversor + yt-dlp + spotDL, auto-update de tools y de la app

## Licencia

Uso personal / según lo indicado en el repositorio. Las herramientas externas (yt-dlp, spotDL, ffmpeg, Spotify) tienen sus propias licencias y términos de uso.

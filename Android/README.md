# Recodio (Android)

App nativa Android (Kotlin + Jetpack Compose) para descargar video y audio de **cualquier sitio que soporte yt-dlp** (YouTube, TikTok, Instagram, X/Twitter, SoundCloud, Vimeo, Twitch, Reddit, y el resto de extractores de yt-dlp), con paridad de funciones respecto al Recodio de escritorio: cola de descargas, selección de videos dentro de una playlist, organización en subcarpetas por playlist, cookies para sitios con login/edad restringida, SponsorBlock, y un Foreground Service para que las descargas sigan corriendo en segundo plano.

Usa [youtubedl-android](https://github.com/junkfood02/youtubedl-android), que empaqueta yt-dlp + ffmpeg nativamente dentro de la app (no requiere Termux ni dependencias externas).

## Instalación

Descargá el APK desde la última [Release](../../releases/latest) e instalalo directamente en tu dispositivo (activá "Instalar apps desconocidas" para tu navegador/administrador de archivos si Android lo pide).

## Funciones

- Descarga de **cualquier sitio soportado por yt-dlp** (no solo YouTube), video (MP4) o solo audio (MP3)
- **Cookies importadas** (`cookies.txt` exportado del navegador) para sitios con login o edad
  restringida - Instagram, X, YouTube age-gated, etc.
- Detección y selección de videos dentro de una playlist
- Organización automática en subcarpetas por playlist
- Cola de descargas con reintentos automáticos, con una **2ª pasada** sobre los items que
  quedaron en error tras la primera vuelta
- **Omite lo ya descargado**: usa `--download-archive` en la carpeta de destino, así una
  playlist re-descargada no repite lo que ya está
- Motivo real del error por item: tocá un item en rojo para ver el mensaje de yt-dlp
- ETA proyectada para toda la cola e indicador de pasada de reintento ("pasada 2/2")
- SponsorBlock (elimina segmentos de sponsors/autopromoción)
- Notificación persistente con el progreso de la descarga en curso

## Changelog

### 1.0.9
- Soporte real para **cualquier sitio de yt-dlp**, no solo YouTube (campo de URL y mensajes
  actualizados; la deteccion de playlist/analisis ya era generica)
- **Importar cookies.txt** (boton en la pantalla principal) para sitios que requieren login o
  verificacion de edad - sin esto muchos sitios fuera de YouTube fallaban

### 1.0.8
- Barra de progreso general de la cola (junto al % y la ETA), reemplaza al log de texto crudo
- Se elimino la vista de log ("Ver log")

### 1.0.7
- Omite items ya descargados (archive de yt-dlp en la carpeta de destino)
- Tooltip/diálogo con el motivo real del error al tocar un item fallido
- ETA proyectada de la cola completa + 2ª pasada de reintento sobre los items fallidos

## Build

```
./gradlew assembleDebug
```

Requiere Android SDK (compileSdk/targetSdk 35, minSdk 24) y JDK 17.

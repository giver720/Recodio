# Recodio (Android)

App nativa Android (Kotlin + Jetpack Compose) para descargar videos y audio de YouTube, con paridad de funciones respecto al Recodio de escritorio: cola de descargas, selección de videos dentro de una playlist, organización en subcarpetas por playlist, SponsorBlock, y un Foreground Service para que las descargas sigan corriendo en segundo plano.

Usa [youtubedl-android](https://github.com/junkfood02/youtubedl-android), que empaqueta yt-dlp + ffmpeg nativamente dentro de la app (no requiere Termux ni dependencias externas).

## Instalación

Descargá el APK desde la última [Release](../../releases/latest) e instalalo directamente en tu dispositivo (activá "Instalar apps desconocidas" para tu navegador/administrador de archivos si Android lo pide).

## Funciones

- Descarga de video (MP4) o solo audio (MP3)
- Detección y selección de videos dentro de una playlist
- Organización automática en subcarpetas por playlist
- Cola de descargas con reintentos automáticos
- SponsorBlock (elimina segmentos de sponsors/autopromoción)
- Notificación persistente con el progreso de la descarga en curso

## Build

```
./gradlew assembleDebug
```

Requiere Android SDK (compileSdk/targetSdk 35, minSdk 24) y JDK 17.

# Recodio

Descargador de vídeo y música con una interfaz que no parece un formulario.
Envuelve **yt-dlp** y **spotDL** con cola paralela, SponsorBlock, detección de
duplicados y una biblioteca local desde la que abrir los archivos en VLC.

> Estado: **v0.1 — escritorio (Windows)**. Los ports a Linux y Android
> reutilizan este mismo frontend; ver [Portabilidad](#portabilidad).

## Qué hace

- **Descarga de todo lo que soporta yt-dlp**, no solo YouTube: Twitch, X, TikTok,
  Vimeo, Instagram, Reddit… más de mil sitios.
- **Playlists y canales completos** con vista previa marcable elemento a elemento.
- **Vídeo o solo audio** (MP3, M4A, Opus, FLAC, WAV) con calidad y contenedor
  configurables.
- **SponsorBlock integrado**: corta patrocinios y autopromoción, o los deja
  marcados como capítulos.
- **Detección de duplicados**: si ya tienes el vídeo o la canción, Recodio lo
  omite. Con clic derecho eliges *omitir* o *sobrescribir* por elemento.
- **Contenido restringido**: cookies del navegador, proxy y `--geo-bypass` para
  vídeos con edad, privados, de miembros o bloqueados por región. Los elementos
  no disponibles de una playlist se marcan en lugar de romper la descarga.
- **Cola con progreso real**: bytes, velocidad, ETA y fase (descarga, unión,
  extracción de audio, recorte de SponsorBlock).
- **Playlists locales automáticas**: al descargar una playlist, Recodio le da su
  propia carpeta y escribe dentro un `.m3u8` con el orden original y rutas
  relativas — se abre en VLC de un doble clic y la carpeta se puede mover o
  copiar a otro equipo sin romper nada. No hay que exportar nada a mano.
- **Biblioteca** por playlist / vídeos / música, con búsqueda y reproducción en
  el reproductor externo que elijas.

## Requisitos

`yt-dlp`, `spotDL` y `ffmpeg`. Recodio los busca primero en su propia carpeta de
datos y, si no están, en el `PATH` del sistema. Desde **Ajustes › Herramientas**
puedes instalar y actualizar una copia propia de yt-dlp — recomendado, porque los
cambios de YouTube obligan a actualizarlo a menudo.

## Desarrollo

```bash
npm install
npm run tauri dev
```

Para generar el instalador:

```bash
npm run tauri build
```

## Arquitectura

```
React + TypeScript (UI)
        │  invoke / eventos Tauri
        ▼
Rust  ─ cola paralela, SQLite (biblioteca), detección de duplicados
        │  procesos hijo
        ▼
yt-dlp · spotDL · ffmpeg
```

| Ruta | Qué contiene |
| --- | --- |
| `src-tauri/src/queue.rs` | Cola con concurrencia, cancelación y eventos de progreso |
| `src-tauri/src/ytdlp.rs` | Construcción de argumentos y parseo del progreso de yt-dlp |
| `src-tauri/src/spotdl.rs` | Lo mismo para spotDL |
| `src-tauri/src/analyze.rs` | Previsualización de enlaces y playlists |
| `src-tauri/src/db.rs` | Biblioteca en SQLite y detección de duplicados |
| `src/views/` | Las cuatro pantallas: Descargar, Cola, Biblioteca, Ajustes |

El progreso no se estima: yt-dlp se lanza con `--progress-template` y una
plantilla propia, así que los bytes y la ETA vienen del propio descargador.

## Portabilidad

El frontend es el mismo en las tres plataformas.

- **Linux**: `npm run tauri build` sin cambios; los binarios se resuelven igual.
- **Android**: `npm run tauri android init`. Ahí no se puede ejecutar `yt-dlp.exe`,
  así que el backend tendrá que empaquetar Python (Chaquopy) o hablar con una
  instancia de Recodio en el escritorio. La UI no cambia.

## Licencia

Pendiente de definir.

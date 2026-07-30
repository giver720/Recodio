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
| `tools/make_icon.py` | Genera el icono; la misma geometría vive en `src/components/Mark.tsx` |

El progreso no se estima: yt-dlp se lanza con `--progress-template` y una
plantilla propia, así que los bytes y la ETA vienen del propio descargador.

## Linux

Compila y empaqueta con la misma orden que en Windows; sale un `.deb` y un
`.AppImage` en `src-tauri/target/release/bundle/`.

```bash
npm run tauri build -- --bundles deb,appimage
```

Dependencias de compilación (Ubuntu / Debian):

```bash
sudo apt install libwebkit2gtk-4.1-dev libgtk-3-dev librsvg2-dev libsoup-3.0-dev build-essential
```

Las herramientas de descarga van como **`Recommends`** del paquete, no como
`Depends`: Recodio arranca sin ellas y sabe instalarse su propio yt-dlp desde
Ajustes, así que una dependencia dura solo serviría para bloquear la instalación
en distros donde el paquete se llama de otra forma.

Lo que cambia respecto a Windows, y por qué:

| Detalle | Windows | Linux |
| --- | --- | --- |
| Mostrar en la carpeta | `explorer /select` | D-Bus `FileManager1.ShowItems`, con la ruta percent-encoded; si no hay bus de sesión, abre la carpeta |
| Abrir archivos y enlaces | `cmd /C start` | `xdg-open` |
| Ventanas de consola | `CREATE_NO_WINDOW` en cada proceso hijo | innecesario |
| Binario propio de yt-dlp | `yt-dlp.exe` | `yt-dlp_linux`, con `chmod 755` |
| Carpetas por defecto | Vídeos / Música | XDG, con respaldo al directorio personal |
| Selector de reproductor | filtra `.exe` | sin filtro: `/usr/bin/vlc` no tiene extensión |

También se desactiva el renderizador DMA-BUF de WebKitGTK al arrancar
(`WEBKIT_DISABLE_DMABUF_RENDERER=1`). Es el fallo más común de las aplicaciones
Tauri en Linux — ventana en negro con NVIDIA propietario, Mesa antiguo o WSLg — y
se respeta el valor si ya viene puesto desde fuera.

## Actualizaciones

Recodio comprueba si hay versión nueva desde **Ajustes › Actualizaciones**, se
descarga el paquete y se reinstala al reiniciar. Las actualizaciones van firmadas:
la clave pública está en `tauri.conf.json` y la app rechaza cualquier paquete que
no case con ella.

Para publicar una versión hay que firmar los artefactos con la clave privada:

```bash
export TAURI_SIGNING_PRIVATE_KEY_PATH=~/.tauri/recodio.key
npm run tauri build
```

> La clave privada **nunca** entra en el repositorio. Si se pierde, no se pueden
> volver a firmar actualizaciones para los usuarios que ya tengan Recodio
> instalado: habría que reinstalar a mano con una clave nueva.

El actualizador solo puede reemplazar formatos que se auto-sustituyen: el
instalador de Windows y el AppImage. Quien instale el `.deb` recibe las
actualizaciones por su gestor de paquetes, y la propia interfaz se lo dice en vez
de fallar en silencio.

## Android

Pendiente. `npm run tauri android init` genera el proyecto, pero ahí no se puede
ejecutar un binario de yt-dlp: habrá que empaquetar Python (Chaquopy) o convertir
el móvil en cliente de un Recodio de escritorio. La interfaz no cambia.

## Licencia

Pendiente de definir.

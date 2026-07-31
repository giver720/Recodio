# Recodio

Descargador de vídeo y música con una interfaz que no parece un formulario.
Envuelve **yt-dlp** y **spotDL** con cola paralela, SponsorBlock, detección de
duplicados y una biblioteca local desde la que abrir los archivos en VLC.

> Estado: **v0.1.1 — Windows y Linux**. El port a Android reutilizará este mismo
> frontend; ver [Android](#android).

## Qué hace

- **Descarga de todo lo que soporta yt-dlp**, no solo YouTube: Twitch, X, TikTok,
  Vimeo, Instagram, Reddit… más de mil sitios.
- **Playlists y canales completos** con vista previa marcable elemento a elemento.
- **Vídeo o solo audio** (MP3, M4A, Opus, FLAC, WAV) con calidad y contenedor
  configurables.
- **SponsorBlock integrado**: corta patrocinios y autopromoción, o los deja
  marcados como capítulos.
- **Detección de duplicados por playlist**: si ya tienes el vídeo o la canción en
  ese mismo destino, Recodio lo omite. Dos playlists distintas pueden compartir
  canciones sin que la segunda se quede coja. Con clic derecho eliges *omitir* o
  *sobrescribir* por elemento.
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

`yt-dlp` y `ffmpeg` — pero **no hace falta instalarlas a mano**.

> **spotDL ya no se usa para descargar.** Spotify no distribuye audio, así que
> ningún programa baja «de Spotify»: todos localizan la canción en YouTube.
> Recodio lee la lista desde Spotify y baja cada tema con yt-dlp, eligiendo el
> resultado que cuadre en duración con el original. Así el progreso es real por
> bytes, se aplican los mismos ajustes que al resto, y hay una dependencia menos.
> spotDL queda como respaldo opcional del listado si Spotify cambia su página.
 Si
falta alguna, Recodio lo dice al abrir la pantalla de Descargar y ofrece
descargárselas a su propia carpeta de datos, sin permisos de administrador y sin
tocar el sistema. También están en **Ajustes › Herramientas**.

Si prefieres las tuyas, las busca en el `PATH` y en las carpetas donde suelen
acabar estas herramientas, `~/.local/bin` incluida. Mantener la copia propia de
yt-dlp al día es recomendable: los cambios de YouTube obligan a actualizarla a
menudo.

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

### Las herramientas de descarga

Recodio no descarga por sí mismo: lanza `yt-dlp`, y `spotDL` para Spotify, con
`ffmpeg` para unir y convertir. En Windows suelen venir ya instaladas de otras
cosas; en una instalación limpia de Linux no hay ninguna.

**No hace falta instalarlas a mano.** Si faltan, Recodio lo dice nada más abrir
la pantalla de Descargar y ofrece un botón que se las descarga a su propia
carpeta de datos: no toca el sistema, no pide contraseña y no interfiere con lo
que ya tengas. También están en **Ajustes › Herramientas**.

Usa los ejecutables autónomos que publican los propios proyectos, así que no hace
falta ni Python: yt-dlp y spotDL vienen de sus releases de GitHub, y ffmpeg de
las compilaciones estáticas de johnvansickle en Linux y de BtbN en Windows. Del
comprimido de ffmpeg se extraen `ffmpeg` y `ffprobe` con el `tar` del sistema
—GNU en Linux, bsdtar desde Windows 10, que además abre zip—, para no arrastrar
media docena de dependencias de compresión solo para eso.

Si prefieres las del sistema, también valen:

```bash
sudo apt install yt-dlp ffmpeg   # imprescindibles
pipx install spotdl              # solo para enlaces de Spotify
```

El `.deb` trae yt-dlp y ffmpeg como `Recommends`, así que `apt` las instala sola
salvo que tengas desactivadas las recomendaciones. spotDL no está empaquetado en
Debian ni en Ubuntu, y por eso va aparte con `pipx`.

> Las herramientas instaladas con `pip install --user` o `pipx` van a
> `~/.local/bin`, que **no** está en el `PATH` de una aplicación lanzada desde el
> menú del escritorio. Recodio busca ahí explícitamente, además de en el `PATH`,
> junto a tu intérprete de Python, en `/snap/bin` y en las rutas de Flatpak.

### Compilación

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
export TAURI_SIGNING_PRIVATE_KEY="$(cat ~/.tauri/recodio.key)"
export TAURI_SIGNING_PRIVATE_KEY_PASSWORD=""
npm run tauri build
```

> La variable es `TAURI_SIGNING_PRIVATE_KEY`, no `..._PATH`, aunque el generador
> de claves mencione ambas: el empaquetador solo lee la primera. Y ojo en
> PowerShell, donde `$env:VAR = ""` **borra** la variable en vez de dejarla
> vacía, así que la contraseña vacía nunca llega y la compilación se queda
> esperando en un aviso que no se ve.

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

## Licencia

MIT — ver [LICENSE](LICENSE).

Recodio **invoca** yt-dlp, spotDL y ffmpeg como programas externos; no incorpora
su código ni enlaza con ellos, así que sus licencias no se propagan a este
proyecto. Cada uno conserva la suya y se descarga de su origen oficial.

# Recodio

App de escritorio para Windows (WinForms, .NET 10) que unifica tres flujos de audio/video:

- **Descarga con spotDL** — playlists, álbumes y canciones de Spotify (audio desde YouTube Music), con cola de descargas, subcarpetas automáticas por playlist/álbum, reintentos automáticos ante bloqueos de YouTube, SponsorBlock opcional y resumen honesto de éxitos/fallos.
- **Descarga con yt-dlp** — videos y playlists de YouTube (MP4/MP3), con selección de calidad, subcarpetas por playlist y SponsorBlock opcional.
- **Conversión de formatos** — vía ffmpeg, con carpeta vigilada (watch folder), arrastrar-y-soltar y menú contextual de Windows.

Corre en la bandeja del sistema, con historial de conversiones, tema oscuro y una sola instancia (IPC por named pipe).

## Requisitos

- Windows 10/11 con [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download)
- [spotDL](https://github.com/spotDL/spotify-downloader) (`pip install spotdl`)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) y [ffmpeg](https://ffmpeg.org/) (por ejemplo vía `winget`)

## Compilar

```powershell
cd Recodio
dotnet publish -c Release -r win-x64 --self-contained false
```

El resultado queda en `Recodio/bin/Release/net10.0-windows/win-x64/publish/`.

## Nota

La configuración (carpetas, credenciales de tu app de Spotify, etc.) se guarda en `config.json` junto al exe — no se versiona en este repo.

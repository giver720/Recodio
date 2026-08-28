import { invoke } from "@tauri-apps/api/core";
import type {
  AnalyzeResult,
  Entry,
  Job,
  Kind,
  LibraryItem,
  MediaSource,
  MediaSourceItem,
  ImportReport,
  Platform,
  PlayerOption,
  RefreshReport,
  RepairReport,
  ScanReport,
  Playlist,
  PlaylistInfo,
  QueueStats,
  Settings,
  SpotifyPlaylist,
  SpotifyProfile,
  SpotifySessionStatus,
  Source,
  SourceProfile,
  SubtitleTrack,
  ToolStatus,
  YoutubeAccount,
  YoutubeSessionStatus,
} from "./types";

export const api = {
  /** `refresh` ignora el listado guardado y vuelve a pedirlo. */
  analyzeUrl: (url: string, refresh = false) =>
    invoke<AnalyzeResult>("analyze_url", { url, refresh }),

  youtubeSessionCheck: (browser: string | null, cookiesFile: string | null) =>
    invoke<YoutubeSessionStatus>("youtube_session_check", { browser, cookiesFile }),

  youtubeAccounts: () => invoke<YoutubeAccount[]>("youtube_accounts_list"),
  youtubeImportCookies: (source: string, name: string) =>
    invoke<YoutubeAccount>("youtube_import_cookies", { source, name }),
  youtubeRenameAccount: (id: string, name: string) =>
    invoke<YoutubeAccount>("youtube_account_rename", { id, name }),
  youtubeDeleteAccount: (id: string) =>
    invoke<void>("youtube_account_delete", { id }),
  youtubeOpenLogin: (browser: string | null) =>
    invoke<void>("youtube_open_login", { browser }),

  spotifyStatus: () => invoke<SpotifySessionStatus>("spotify_status"),
  spotifyLogin: () => invoke<SpotifyProfile>("spotify_login"),
  spotifyLogout: () => invoke<void>("spotify_logout"),
  spotifyPlaylists: () => invoke<SpotifyPlaylist[]>("spotify_playlists"),
  spotifyCollection: (collection: "saved" | "top" | "recent") =>
    invoke<AnalyzeResult>("spotify_collection", { collection }),
  spotifyPlaylist: (playlist: SpotifyPlaylist) =>
    invoke<AnalyzeResult>("spotify_playlist", {
      id: playlist.id,
      name: playlist.name,
      url: playlist.externalUrl,
    }),

  mediaSources: () => invoke<MediaSource[]>("media_sources_list"),
  mediaSourceAdd: (url: string, mediaKind: Kind) =>
    invoke<MediaSource>("media_source_add", { url, mediaKind }),
  mediaSourceSync: (id: string) =>
    invoke<MediaSource>("media_source_sync", { id }),
  mediaSourceItems: (id: string) =>
    invoke<MediaSourceItem[]>("media_source_items", { id }),
  mediaSourceMarkSeen: (id: string, remoteIds: string[]) =>
    invoke<void>("media_source_mark_seen", { id, remoteIds }),
  mediaSourceUpdateProfile: (id: string, mediaKind: Kind, profile: SourceProfile) =>
    invoke<MediaSource>("media_source_update_profile", { id, mediaKind, profile }),
  mediaSourceUpdateSchedule: (id: string, intervalMinutes: number | null, autoDownload: boolean) =>
    invoke<MediaSource>("media_source_update_schedule", { id, intervalMinutes, autoDownload }),
  mediaSourcesExport: (path: string) => invoke<number>("media_sources_export", { path }),
  mediaSourcesImport: (path: string) => invoke<number>("media_sources_import", { path }),
  mediaSourceDelete: (id: string) =>
    invoke<void>("media_source_delete", { id }),

  enqueue: (req: {
    entries: Entry[];
    kind: Kind;
    source: Source;
    destDir?: string | null;
    playlist?: PlaylistInfo | null;
    overwriteIds?: string[];
    profile?: SourceProfile | null;
  }) =>
    invoke<number>("enqueue", {
      req: {
        entries: req.entries,
        kind: req.kind,
        source: req.source,
        destDir: req.destDir ?? null,
        playlist: req.playlist ?? null,
        overwriteIds: req.overwriteIds ?? [],
        profile: req.profile ?? null,
      },
    }),

  queueList: () => invoke<Job[]>("queue_list"),
  queueStats: () => invoke<QueueStats>("queue_stats"),
  queueCancel: (id: string) => invoke<void>("queue_cancel", { id }),
  queueCancelAll: () => invoke<void>("queue_cancel_all"),
  queueRetry: (id: string) => invoke<void>("queue_retry", { id }),
  queueRetryFailed: () => invoke<number>("queue_retry_failed"),
  queueClearFinished: () => invoke<void>("queue_clear_finished"),
  queueSetPaused: (paused: boolean) =>
    invoke<void>("queue_set_paused", { paused }),
  queueIsPaused: () => invoke<boolean>("queue_is_paused"),

  getSettings: () => invoke<Settings>("get_settings"),
  setSettings: (settings: Settings) =>
    invoke<Settings>("set_settings", { settings }),

  libraryPlaylists: () => invoke<Playlist[]>("library_playlists"),
  libraryItems: (playlistId?: string | null, search?: string | null) =>
    invoke<LibraryItem[]>("library_items", {
      playlistId: playlistId ?? null,
      search: search ?? null,
    }),
  libraryDelete: (id: string, deleteFile: boolean) =>
    invoke<void>("library_delete", { id, deleteFile }),
  libraryPrune: () => invoke<number>("library_prune"),
  libraryDeletePlaylist: (playlistId: string, deleteFiles: boolean) =>
    invoke<number>("library_delete_playlist", { playlistId, deleteFiles }),
  libraryRepair: () => invoke<RepairReport>("library_repair"),
  libraryHealth: () => invoke<RepairReport>("library_health"),
  libraryRefresh: () => invoke<RefreshReport>("library_refresh"),
  libraryImportFolder: (path: string) =>
    invoke<ImportReport>("library_import_folder", { path }),
  /** Sin `paths` rastrea las carpetas de descarga más las vigiladas. */
  libraryScan: (paths?: string[]) =>
    invoke<ScanReport>("library_scan", { paths: paths ?? null }),
  /** Rastreo sin barra de progreso, para el que hace la biblioteca al abrirse. */
  libraryScanQuiet: () => invoke<ScanReport>("library_scan_quiet"),
  suggestedFolders: () => invoke<string[]>("suggested_folders"),
  exportM3u: (playlistId: string) =>
    invoke<string>("export_m3u", { playlistId }),

  playFile: (path: string) => invoke<void>("play_file", { path }),
  revealFile: (path: string) => invoke<void>("reveal_file", { path }),
  openFolder: (path: string) => invoke<void>("open_folder", { path }),
  appPlatform: () => invoke<Platform>("app_platform"),
  detectPlayers: () => invoke<PlayerOption[]>("detect_players"),
  subtitlesFor: (path: string) => invoke<SubtitleTrack[]>("subtitles_for", { path }),

  /** `force` vuelve a lanzar las herramientas; sin él se usa lo ya averiguado. */
  toolsStatus: (force = false) => invoke<ToolStatus[]>("tools_status", { force }),
  toolsInstall: (name: string) => invoke<string>("tools_install", { name }),
  toolsUpdateYtdlp: () => invoke<string>("tools_update_ytdlp"),
};

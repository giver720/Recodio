import { invoke } from "@tauri-apps/api/core";
import type {
  AnalyzeResult,
  Entry,
  Job,
  Kind,
  LibraryItem,
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
  Source,
  SubtitleTrack,
  ToolStatus,
} from "./types";

export const api = {
  /** `refresh` ignora el listado guardado y vuelve a pedirlo. */
  analyzeUrl: (url: string, refresh = false) =>
    invoke<AnalyzeResult>("analyze_url", { url, refresh }),

  enqueue: (req: {
    entries: Entry[];
    kind: Kind;
    source: Source;
    destDir?: string | null;
    playlist?: PlaylistInfo | null;
    overwriteIds?: string[];
  }) =>
    invoke<number>("enqueue", {
      req: {
        entries: req.entries,
        kind: req.kind,
        source: req.source,
        destDir: req.destDir ?? null,
        playlist: req.playlist ?? null,
        overwriteIds: req.overwriteIds ?? [],
      },
    }),

  queueList: () => invoke<Job[]>("queue_list"),
  queueStats: () => invoke<QueueStats>("queue_stats"),
  queueCancel: (id: string) => invoke<void>("queue_cancel", { id }),
  queueCancelAll: () => invoke<void>("queue_cancel_all"),
  queueRetry: (id: string) => invoke<void>("queue_retry", { id }),
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

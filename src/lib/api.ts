import { invoke } from "@tauri-apps/api/core";
import type {
  AnalyzeResult,
  Entry,
  Job,
  Kind,
  LibraryItem,
  Platform,
  Playlist,
  PlaylistInfo,
  QueueStats,
  Settings,
  Source,
  ToolStatus,
} from "./types";

export const api = {
  analyzeUrl: (url: string) => invoke<AnalyzeResult>("analyze_url", { url }),

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
  exportM3u: (playlistId: string) =>
    invoke<string>("export_m3u", { playlistId }),

  playFile: (path: string) => invoke<void>("play_file", { path }),
  revealFile: (path: string) => invoke<void>("reveal_file", { path }),
  openFolder: (path: string) => invoke<void>("open_folder", { path }),
  appPlatform: () => invoke<Platform>("app_platform"),

  toolsStatus: () => invoke<ToolStatus[]>("tools_status"),
  toolsInstall: (name: string) => invoke<string>("tools_install", { name }),
  toolsUpdateYtdlp: () => invoke<string>("tools_update_ytdlp"),
};

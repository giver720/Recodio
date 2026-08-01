export type Platform = "windows" | "linux" | "macos";

/** Resultado de limpiar la biblioteca. */
export interface RepairReport {
  checked: number;
  removed: number;
  sharedFiles: number;
  missing: number;
}

/** Resultado de escanear una carpeta del equipo. */
export interface ImportReport {
  found: number;
  added: number;
  skipped: number;
  playlistId: string;
  title: string;
}

/** Reproductor encontrado en el equipo. */
export interface PlayerOption {
  name: string;
  path: string;
}

export type Kind = "video" | "audio";
/** `local` son las carpetas del propio equipo importadas a la biblioteca. */
export type Source = "ytdlp" | "spotdl" | "local";

export interface Entry {
  id: string;
  sourceId: string;
  extractor: string;
  title: string;
  url: string;
  uploader: string | null;
  duration: number | null;
  thumbnail: string | null;
  index: number;
  existingVideo: string | null;
  existingAudio: string | null;
  unavailable: boolean;
}

export interface PlaylistInfo {
  sourceId: string;
  title: string;
  url: string;
  uploader: string | null;
  thumbnail: string | null;
}

export interface AnalyzeResult {
  /** Marca de tiempo si la lista viene de un análisis anterior. */
  cachedAt?: number;
  /** Faltan canciones por llegar; se están pidiendo en segundo plano. */
  partial?: boolean;
  /** Identifica el análisis para casar los envíos posteriores. */
  key?: string;
  source: Source;
  isPlaylist: boolean;
  playlist: PlaylistInfo | null;
  entries: Entry[];
}

export type JobStatus =
  | "queued"
  | "running"
  | "done"
  | "skipped"
  | "failed"
  | "canceled";

export type JobPhase = "waiting" | "downloading" | "processing" | "finished";

export interface Job {
  id: string;
  entry: Entry;
  kind: Kind;
  source: Source;
  destDir: string;
  overwrite: boolean;
  playlistId: string | null;
  playlistTitle: string | null;
  status: JobStatus;
  phase: JobPhase;
  progress: number;
  downloadedBytes: number;
  totalBytes: number;
  speed: number;
  eta: number | null;
  message: string | null;
  error: string | null;
  filePath: string | null;
  createdAt: number;
}

export interface QueueStats {
  queued: number;
  running: number;
  done: number;
  failed: number;
  skipped: number;
  paused: boolean;
  overall: number;
}

export interface LibraryItem {
  id: string;
  source: Source;
  extractor: string;
  sourceId: string;
  url: string;
  title: string;
  uploader: string | null;
  duration: number | null;
  thumbnail: string | null;
  filePath: string;
  fileSize: number;
  kind: Kind;
  ext: string;
  playlistId: string | null;
  playlistIndex: number | null;
  downloadedAt: number;
}

export interface Playlist {
  id: string;
  source: Source;
  sourceId: string;
  url: string;
  title: string;
  uploader: string | null;
  thumbnail: string | null;
  createdAt: number;
  itemCount: number;
}

export type DuplicatePolicy = "skip" | "overwrite" | "ask";

export interface Settings {
  videoDir: string;
  audioDir: string;
  concurrency: number;
  duplicatePolicy: DuplicatePolicy;
  sponsorblock: boolean;
  sponsorblockRemove: string[];
  sponsorblockMark: string[];
  videoQuality: string;
  videoContainer: string;
  audioFormat: string;
  audioBitrate: string;
  embedThumbnail: boolean;
  embedMetadata: boolean;
  embedChapters: boolean;
  writeSubtitles: boolean;
  embedSubtitles: boolean;
  subtitleLangs: string;
  useArchive: boolean;
  cookiesFromBrowser: string | null;
  cookiesFile: string | null;
  proxy: string | null;
  rateLimit: string | null;
  retries: number;
  ignoreErrors: boolean;
  outputTemplate: string;
  playlistSubfolder: boolean;
  externalPlayer: string | null;
  theme: string;
}

export interface ToolStatus {
  name: string;
  found: boolean;
  path: string | null;
  version: string | null;
  managed: boolean;
}

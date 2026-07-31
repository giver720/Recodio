import { listen } from "@tauri-apps/api/event";
import { create } from "zustand";
import { api } from "./api";
import type { Job, Platform, QueueStats, Settings, ToolStatus } from "./types";

export type Toast = {
  id: number;
  kind: "info" | "success" | "error";
  text: string;
};

const emptyStats: QueueStats = {
  queued: 0,
  running: 0,
  done: 0,
  failed: 0,
  skipped: 0,
  paused: false,
  overall: 0,
};

interface State {
  ready: boolean;
  platform: Platform;
  settings: Settings | null;
  jobs: Job[];
  stats: QueueStats;
  toasts: Toast[];
  /** Bumped whenever the library changes so views can refetch. */
  libraryVersion: number;
  /** Estado de yt-dlp, spotDL y ffmpeg. */
  tools: ToolStatus[];

  init: () => Promise<void>;
  refreshTools: () => Promise<void>;
  saveSettings: (patch: Partial<Settings>) => Promise<void>;
  toast: (kind: Toast["kind"], text: string) => void;
  dismissToast: (id: number) => void;
}

let toastSeq = 0;

export const useStore = create<State>((set, get) => ({
  ready: false,
  platform: "windows",
  settings: null,
  jobs: [],
  stats: emptyStats,
  toasts: [],
  libraryVersion: 0,
  tools: [],

  refreshTools: async () => set({ tools: await api.toolsStatus(true) }),

  init: async () => {
    const [settings, jobs, stats, platform, tools] = await Promise.all([
      api.getSettings(),
      api.queueList(),
      api.queueStats(),
      api.appPlatform(),
      api.toolsStatus(),
    ]);
    set({ settings, jobs, stats, platform, tools, ready: true });

    listen<Job>("job-update", (e) => {
      const job = e.payload;
      set((s) => {
        const i = s.jobs.findIndex((j) => j.id === job.id);
        if (i === -1) return { jobs: [...s.jobs, job] };
        const next = s.jobs.slice();
        next[i] = job;
        return { jobs: next };
      });
    });

    listen<Job[]>("queue-replace", (e) => set({ jobs: e.payload }));
    listen<QueueStats>("queue-stats", (e) => set({ stats: e.payload }));
    listen("library-changed", () =>
      set((s) => ({ libraryVersion: s.libraryVersion + 1 })),
    );

    listen<string>("playlist-ready", (e) => {
      const name = e.payload.split(/[\\/]/).pop() ?? e.payload;
      get().toast("success", `Playlist creada: ${name}`);
      set((s) => ({ libraryVersion: s.libraryVersion + 1 }));
    });
  },

  saveSettings: async (patch) => {
    const current = get().settings;
    if (!current) return;
    const settings = await api.setSettings({ ...current, ...patch });
    set({ settings });
  },

  toast: (kind, text) => {
    const id = ++toastSeq;
    set((s) => ({ toasts: [...s.toasts, { id, kind, text }] }));
    setTimeout(() => get().dismissToast(id), kind === "error" ? 7000 : 3500);
  },

  dismissToast: (id) =>
    set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
}));

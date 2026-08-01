import { convertFileSrc } from "@tauri-apps/api/core";
import { create } from "zustand";
import type { LibraryItem } from "./types";

export type RepeatMode = "off" | "all" | "one";

interface PlayerState {
  /** Lo que suena ahora. */
  current: LibraryItem | null;
  /** Cola en el orden en que se reproducirá. */
  queue: LibraryItem[];
  index: number;

  playing: boolean;
  /** Segundos reproducidos y duración real del archivo. */
  position: number;
  duration: number;
  volume: number;
  muted: boolean;
  rate: number;
  repeat: RepeatMode;
  shuffle: boolean;
  /** El vídeo ocupa toda la ventana en vez de la barra inferior. */
  expanded: boolean;
  /** Escuchar un vídeo sin verlo, como si fuera una canción. */
  audioOnly: boolean;

  /** Empieza a reproducir `item`, con `list` como cola. */
  play: (item: LibraryItem, list?: LibraryItem[]) => void;
  toggle: () => void;
  next: (auto?: boolean) => void;
  previous: () => void;
  stop: () => void;
  seek: (seconds: number) => void;
  setVolume: (v: number) => void;
  toggleMute: () => void;
  setRate: (r: number) => void;
  cycleRepeat: () => void;
  toggleShuffle: () => void;
  setExpanded: (v: boolean) => void;
  toggleAudioOnly: () => void;

  /** Las escribe el elemento de audio o vídeo mientras suena. */
  reportTime: (position: number, duration: number) => void;
}

/** Ruta local a algo que el webview pueda cargar. */
export function mediaSrc(path: string): string {
  return convertFileSrc(path);
}

export const isVideo = (item: LibraryItem) => item.kind === "video";

/** Baraja sin tocar el original, dejando delante el que ya está sonando. */
function shuffled(list: LibraryItem[], keepFirst: LibraryItem | null): LibraryItem[] {
  const resto = list.filter((i) => i.id !== keepFirst?.id);
  for (let i = resto.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [resto[i], resto[j]] = [resto[j], resto[i]];
  }
  return keepFirst ? [keepFirst, ...resto] : resto;
}

export const usePlayer = create<PlayerState>((set, get) => ({
  current: null,
  queue: [],
  index: -1,
  playing: false,
  position: 0,
  duration: 0,
  volume: 1,
  muted: false,
  rate: 1,
  repeat: "off",
  shuffle: false,
  expanded: false,
  // Se recuerda entre sesiones: quien escucha vídeos como música lo quiere
  // siempre, no una vez.
  audioOnly: localStorage.getItem("recodio.audioOnly") === "1",

  play: (item, list) => {
    const base = list && list.length > 0 ? list : [item];
    const cola = get().shuffle ? shuffled(base, item) : base;
    const index = cola.findIndex((i) => i.id === item.id);
    set({
      current: item,
      queue: cola,
      index: index < 0 ? 0 : index,
      playing: true,
      position: 0,
      duration: 0,
      // Un vídeo pide pantalla; una canción se queda en la barra de abajo. Y en
      // modo solo audio, tampoco un vídeo la pide.
      expanded: isVideo(item) && !get().audioOnly,
    });
  },

  toggle: () => set((s) => ({ playing: s.current ? !s.playing : false })),

  next: (auto = false) => {
    const { queue, index, repeat } = get();
    if (queue.length === 0) return;

    // En modo «repetir una», el salto automático la repite pero el botón
    // siguiente avanza: si no, el botón parecería roto.
    if (auto && repeat === "one") {
      set({ position: 0, playing: true });
      return;
    }

    const siguiente = index + 1;
    if (siguiente >= queue.length) {
      if (repeat === "all") {
        set({ index: 0, current: queue[0], position: 0, playing: true });
      } else {
        set({ playing: false, position: 0 });
      }
      return;
    }
    set({
      index: siguiente,
      current: queue[siguiente],
      position: 0,
      playing: true,
      expanded: get().expanded && isVideo(queue[siguiente]),
    });
  },

  previous: () => {
    const { queue, index, position } = get();
    // Como en cualquier reproductor: si ya ha avanzado, «anterior» reinicia.
    if (position > 3) {
      set({ position: 0 });
      return;
    }
    const anterior = index - 1;
    if (anterior < 0 || queue.length === 0) {
      set({ position: 0 });
      return;
    }
    set({
      index: anterior,
      current: queue[anterior],
      position: 0,
      playing: true,
      expanded: get().expanded && isVideo(queue[anterior]),
    });
  },

  stop: () =>
    set({
      current: null,
      queue: [],
      index: -1,
      playing: false,
      position: 0,
      duration: 0,
      expanded: false,
    }),

  seek: (seconds) => set({ position: Math.max(0, seconds) }),
  setVolume: (v) => set({ volume: Math.min(1, Math.max(0, v)), muted: false }),
  toggleMute: () => set((s) => ({ muted: !s.muted })),
  setRate: (r) => set({ rate: r }),
  cycleRepeat: () =>
    set((s) => ({
      repeat: s.repeat === "off" ? "all" : s.repeat === "all" ? "one" : "off",
    })),
  toggleShuffle: () =>
    set((s) => {
      const shuffle = !s.shuffle;
      if (s.queue.length === 0) return { shuffle };
      // Al desordenar se recoloca la cola conservando lo que suena; al volver al
      // orden normal no se puede reconstruir el original, así que se deja como
      // está y solo cambia el comportamiento futuro.
      if (!shuffle) return { shuffle };
      const cola = shuffled(s.queue, s.current);
      return { shuffle, queue: cola, index: 0 };
    }),
  setExpanded: (v) => set({ expanded: v }),

  toggleAudioOnly: () =>
    set((s) => {
      const audioOnly = !s.audioOnly;
      localStorage.setItem("recodio.audioOnly", audioOnly ? "1" : "0");
      // Al pasar a solo audio se repliega; al volver a vídeo se abre, que es
      // justo lo que se acaba de pedir en cada caso.
      return {
        audioOnly,
        expanded: audioOnly ? false : Boolean(s.current && isVideo(s.current)),
      };
    }),

  reportTime: (position, duration) => set({ position, duration }),
}));

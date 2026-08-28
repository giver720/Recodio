import { emit, listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { WebviewWindow } from "@tauri-apps/api/webviewWindow";
import type { FitMode } from "./player";
import type { LibraryItem } from "./types";
import type { AudioMeterSnapshot, AudioSettings } from "./audioSettings";

/**
 * Ventana flotante del reproductor.
 *
 * Es una ventana de verdad del sistema, así que puede quedarse encima de otras
 * aplicaciones y se redimensiona por los bordes como cualquier otra. Carga la
 * misma web con `#popup`, y ahí `main.tsx` monta solo el mini reproductor.
 *
 * Mientras el popup está abierto, él es el único que reproduce: la ventana
 * principal deja su vídeo en pausa y se limita a mandar órdenes y a recibir el
 * avance. El estado sigue viviendo en la ventana principal, que es la que tiene
 * la cola; el popup no decide nada por su cuenta.
 */
export const POPUP_LABEL = "popup";

const CANAL_AL_POPUP = "recodio:al-popup";
const CANAL_DESDE_POPUP = "recodio:desde-popup";

/** Todo lo que el popup necesita saber para ponerse al día de golpe. */
export interface PopupSnapshot {
  item: LibraryItem | null;
  position: number;
  playing: boolean;
  volume: number;
  muted: boolean;
  audio: AudioSettings;
  rate: number;
  fit: FitMode;
  index: number;
  total: number;
}

export type MensajeAlPopup =
  | { t: "estado"; snap: PopupSnapshot }
  | { t: "reproducir"; v: boolean }
  | { t: "saltar"; s: number }
  | { t: "volumen"; v: number; muted: boolean }
  | { t: "audio-settings"; settings: AudioSettings }
  | { t: "velocidad"; v: number }
  | { t: "encaje"; v: FitMode };

export type OrdenPopup =
  | "alternar"
  | "siguiente"
  | "anterior"
  | "silencio"
  | "restaurar"
  | "parar";

export type MensajeDesdePopup =
  | { t: "hola" }
  | { t: "tiempo"; position: number; duration: number }
  | { t: "fin" }
  | { t: "orden"; a: OrdenPopup }
  | { t: "saltar"; s: number }
  | { t: "volumen"; v: number }
  | { t: "audio-settings"; settings: AudioSettings }
  | { t: "audio-meter"; meter: AudioMeterSnapshot }
  | { t: "abrir-audio" }
  | { t: "velocidad"; v: number }
  | { t: "cerrado" };

/** Esta pestaña es la ventana flotante, no la principal. */
export const esVentanaPopup = () => window.location.hash === "#popup";

export function alPopup(m: MensajeAlPopup) {
  void emit(CANAL_AL_POPUP, m);
}

export function desdePopup(m: MensajeDesdePopup) {
  return emit(CANAL_DESDE_POPUP, m);
}

export function escucharDelPopup(fn: (m: MensajeDesdePopup) => void) {
  return listen<MensajeDesdePopup>(CANAL_DESDE_POPUP, (e) => fn(e.payload));
}

export function escucharDelPrincipal(fn: (m: MensajeAlPopup) => void) {
  return listen<MensajeAlPopup>(CANAL_AL_POPUP, (e) => fn(e.payload));
}

/** Abre la ventana flotante, o la trae al frente si ya estaba. */
export async function abrirPopup(): Promise<void> {
  const existente = await WebviewWindow.getByLabel(POPUP_LABEL);
  if (existente) {
    await existente.setFocus();
    return;
  }

  const ventana = new WebviewWindow(POPUP_LABEL, {
    url: "index.html#popup",
    title: "Recodio — Reproductor",
    width: 480,
    height: 300,
    minWidth: 240,
    minHeight: 150,
    resizable: true,
    alwaysOnTop: true,
    decorations: true,
    skipTaskbar: false,
    // Negro desde el primer fotograma: el gris por defecto se ve al abrirse.
    backgroundColor: [0, 0, 0, 255],
  });

  await new Promise<void>((listo, fallo) => {
    ventana.once("tauri://created", () => listo());
    ventana.once("tauri://error", (e) => fallo(e.payload));
  });

  // Si se cierra la principal, la flotante no puede quedarse suelta: sin ella no
  // hay cola ni estado, y la aplicación seguiría viva por una ventana huérfana.
  await getCurrentWindow().onCloseRequested(() => {
    void cerrarPopup();
  });
}

export async function cerrarPopup(): Promise<void> {
  const ventana = await WebviewWindow.getByLabel(POPUP_LABEL);
  await ventana?.destroy();
}

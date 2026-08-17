import { useEffect } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { usePlayer } from "./player";
import {
  alPopup,
  cerrarPopup,
  escucharDelPopup,
  type PopupSnapshot,
} from "./popup";

/** Lo que hay que contarle al popup para que arranque igual que la principal. */
function instantanea(): PopupSnapshot {
  const p = usePlayer.getState();
  return {
    item: p.current,
    position: p.position,
    playing: p.playing,
    volume: p.volume,
    muted: p.muted,
    rate: p.rate,
    fit: p.fit,
    index: p.index,
    total: p.queue.length,
  };
}

/**
 * Lado principal del puente con la ventana flotante: recibe lo que pasa allí y
 * le manda los cambios de aquí. Se usa una sola vez, desde el reproductor.
 */
export function usePuenteConPopup() {
  const popup = usePlayer((s) => s.popup);
  const item = usePlayer((s) => s.current);
  const playing = usePlayer((s) => s.playing);
  const volume = usePlayer((s) => s.volume);
  const muted = usePlayer((s) => s.muted);
  const rate = usePlayer((s) => s.rate);
  const fit = usePlayer((s) => s.fit);
  const seekNonce = usePlayer((s) => s.seekNonce);

  // Recibir. Se escucha siempre, incluso con el popup cerrado: el «hola» del
  // popup recién abierto es justo lo que enciende el estado por este lado.
  useEffect(() => {
    const suscripcion = escucharDelPopup((m) => {
      const p = usePlayer.getState();
      switch (m.t) {
        case "hola":
          p.setPopup(true);
          alPopup({ t: "estado", snap: instantanea() });
          break;
        case "tiempo":
          p.reportTime(m.position, m.duration);
          break;
        case "fin":
          p.next(true);
          break;
        case "saltar":
          p.seek(m.s);
          break;
        case "volumen":
          p.setVolume(m.v);
          break;
        case "velocidad":
          p.setRate(m.v);
          break;
        case "orden":
          if (m.a === "alternar") p.toggle();
          else if (m.a === "siguiente") p.next();
          else if (m.a === "anterior") p.previous();
          else if (m.a === "silencio") p.toggleMute();
          else if (m.a === "parar") p.stop();
          else if (m.a === "restaurar") {
            p.setPopup(false);
            usePlayer.setState({ expanded: Boolean(p.current) && !p.audioOnly });
            void getCurrentWindow().setFocus();
          }
          break;
        case "cerrado":
          p.setPopup(false);
          break;
      }
    });
    return () => {
      void suscripcion.then((quitar) => quitar());
    };
  }, []);

  // Enviar: un efecto por cosa que puede cambiar, para no reenviar el estado
  // entero cuatro veces por segundo.
  useEffect(() => {
    if (popup) alPopup({ t: "estado", snap: instantanea() });
    // La posición se manda dentro de la instantánea al cambiar de pista, pero no
    // es la que dispara este efecto: eso lo hace el nonce de abajo.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [popup, item?.id]);

  useEffect(() => {
    if (popup) alPopup({ t: "reproducir", v: playing });
  }, [popup, playing]);

  useEffect(() => {
    if (popup && seekNonce > 0) {
      alPopup({ t: "saltar", s: usePlayer.getState().position });
    }
  }, [popup, seekNonce]);

  useEffect(() => {
    if (popup) alPopup({ t: "volumen", v: volume, muted });
  }, [popup, volume, muted]);

  useEffect(() => {
    if (popup) alPopup({ t: "velocidad", v: rate });
  }, [popup, rate]);

  useEffect(() => {
    if (popup) alPopup({ t: "encaje", v: fit });
  }, [popup, fit]);

  // Al cerrar el reproductor no queda nada que ver flotando.
  useEffect(() => {
    if (popup && !item) {
      usePlayer.getState().setPopup(false);
      void cerrarPopup();
    }
  }, [popup, item]);
}

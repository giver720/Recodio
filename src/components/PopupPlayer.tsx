import { useCallback, useEffect, useRef, useState } from "react";
import {
  Maximize,
  Minimize,
  Pause,
  Pin,
  PinOff,
  Play,
  PictureInPicture2,
  SkipBack,
  SkipForward,
  Subtitles,
  Volume2,
  VolumeX,
  X,
} from "lucide-react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { api } from "../lib/api";
import { mediaSrc, type FitMode } from "../lib/player";
import {
  desdePopup,
  escucharDelPrincipal,
  type OrdenPopup,
  type PopupSnapshot,
} from "../lib/popup";
import type { SubtitleTrack } from "../lib/types";

const VACIO: PopupSnapshot = {
  item: null,
  position: 0,
  playing: false,
  volume: 1,
  muted: false,
  rate: 1,
  fit: "contain",
  index: 0,
  total: 0,
};

const ENCAJE: Record<FitMode, string> = {
  contain: "object-contain",
  cover: "object-cover",
  fill: "object-fill",
  none: "object-none",
};

function reloj(total: number): string {
  if (!Number.isFinite(total) || total < 0) return "0:00";
  const s = Math.floor(total % 60);
  const m = Math.floor((total / 60) % 60);
  const h = Math.floor(total / 3600);
  const dd = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${dd(m)}:${dd(s)}` : `${m}:${dd(s)}`;
}

/**
 * Mini reproductor de la ventana flotante.
 *
 * No tiene estado propio de reproducción: pinta lo que le manda la ventana
 * principal y le devuelve órdenes. Lo único que decide aquí es lo que solo
 * afecta a esta ventana: si se queda encima, si va a pantalla completa y cuándo
 * se esconden los controles.
 */
export function PopupPlayer() {
  const ref = useRef<HTMLVideoElement>(null);
  const [snap, setSnap] = useState<PopupSnapshot>(VACIO);
  const [posicion, setPosicion] = useState(0);
  const [duracion, setDuracion] = useState(0);
  const [arrastrando, setArrastrando] = useState<number | null>(null);
  const [encima, setEncima] = useState(true);
  const [completa, setCompleta] = useState(false);
  const [controles, setControles] = useState(true);
  const [subtitulos, setSubtitulos] = useState<SubtitleTrack[]>([]);
  const [subActivo, setSubActivo] = useState<string | null>(null);
  const ultimoAviso = useRef(0);

  const ordenar = useCallback((a: OrdenPopup) => desdePopup({ t: "orden", a }), []);

  // Escuchar a la principal. Se monta una vez y lee el elemento por referencia,
  // así que no hace falta rehacer la suscripción en cada cambio.
  useEffect(() => {
    const suscripcion = escucharDelPrincipal((m) => {
      const el = ref.current;
      switch (m.t) {
        case "estado":
          setSnap(m.snap);
          if (el && Math.abs(el.currentTime - m.snap.position) > 0.5) {
            el.currentTime = m.snap.position;
          }
          break;
        case "reproducir":
          setSnap((s) => ({ ...s, playing: m.v }));
          break;
        case "saltar":
          setSnap((s) => ({ ...s, position: m.s }));
          if (el && Math.abs(el.currentTime - m.s) > 0.5) el.currentTime = m.s;
          break;
        case "volumen":
          setSnap((s) => ({ ...s, volume: m.v, muted: m.muted }));
          break;
        case "velocidad":
          setSnap((s) => ({ ...s, rate: m.v }));
          break;
        case "encaje":
          setSnap((s) => ({ ...s, fit: m.v }));
          break;
      }
    });

    // El saludo es lo que le dice a la principal que ya hay alguien escuchando;
    // ella responde con el estado completo.
    void desdePopup({ t: "hola" });

    return () => {
      void suscripcion.then((quitar) => quitar());
    };
  }, []);

  // Cerrar avisando: si la principal no se entera, se queda creyendo que el
  // vídeo sigue sonando aquí y no vuelve a reproducir por su cuenta.
  useEffect(() => {
    const ventana = getCurrentWindow();
    const promesa = ventana.onCloseRequested(async (e) => {
      e.preventDefault();
      await desdePopup({ t: "cerrado" });
      await ventana.destroy();
    });
    return () => {
      void promesa.then((quitar) => quitar());
    };
  }, []);

  useEffect(() => {
    const el = ref.current;
    if (!el || !snap.item) return;
    if (snap.playing) el.play().catch(() => ordenar("alternar"));
    else el.pause();
  }, [snap.playing, snap.item, ordenar]);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.volume = snap.volume;
    el.muted = snap.muted;
    el.playbackRate = snap.rate;
  }, [snap.volume, snap.muted, snap.rate, snap.item]);

  useEffect(() => {
    setSubActivo(null);
    if (!snap.item) {
      setSubtitulos([]);
      return;
    }
    let vigente = true;
    api
      .subtitlesFor(snap.item.filePath)
      .then((s) => vigente && setSubtitulos(s))
      .catch(() => vigente && setSubtitulos([]));
    return () => {
      vigente = false;
    };
  }, [snap.item?.filePath]);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    for (let i = 0; i < el.textTracks.length; i++) {
      const pista = el.textTracks[i];
      pista.mode = pista.language === subActivo ? "showing" : "disabled";
    }
  }, [subActivo, subtitulos]);

  useEffect(() => {
    const alCambiar = () => setCompleta(Boolean(document.fullscreenElement));
    document.addEventListener("fullscreenchange", alCambiar);
    return () => document.removeEventListener("fullscreenchange", alCambiar);
  }, []);

  // Los controles tapan una ventana que suele ser pequeña, así que se van solos
  // mientras se reproduce y vuelven al mover el ratón.
  useEffect(() => {
    if (!controles || !snap.playing) return;
    const t = setTimeout(() => setControles(false), 2500);
    return () => clearTimeout(t);
  }, [controles, snap.playing, posicion]);

  useEffect(() => {
    const manejar = (e: KeyboardEvent) => {
      if (e.code === "Space") {
        e.preventDefault();
        void ordenar("alternar");
      } else if (e.code === "ArrowRight") {
        void desdePopup({ t: "saltar", s: posicion + 5 });
      } else if (e.code === "ArrowLeft") {
        void desdePopup({ t: "saltar", s: Math.max(0, posicion - 5) });
      } else if (e.code === "KeyF") {
        void alternarCompleta();
      }
    };
    window.addEventListener("keydown", manejar);
    return () => window.removeEventListener("keydown", manejar);
  }, [posicion, ordenar]);

  async function alternarCompleta() {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await document.documentElement.requestFullscreen();
    } catch {
      /* el sistema puede negarlo */
    }
  }

  async function alternarEncima() {
    const nuevo = !encima;
    setEncima(nuevo);
    try {
      await getCurrentWindow().setAlwaysOnTop(nuevo);
    } catch {
      setEncima(!nuevo);
    }
  }

  const item = snap.item;
  const total = duracion || item?.duration || 0;
  const mostrado = arrastrando ?? posicion;

  return (
    <div
      className="relative h-screen w-screen overflow-hidden bg-black text-white"
      onMouseMove={() => setControles(true)}
      onMouseLeave={() => snap.playing && setControles(false)}
    >
      {item ? (
        <video
          ref={ref}
          src={mediaSrc(item.filePath)}
          className={`h-full w-full ${ENCAJE[snap.fit]}`}
          onClick={() => void ordenar("alternar")}
          onDoubleClick={() => void alternarCompleta()}
          onTimeUpdate={(e) => {
            const el = e.currentTarget;
            setPosicion(el.currentTime);
            setDuracion(el.duration || 0);
            // El elemento avisa varias veces por segundo; a la principal le
            // sobra con dos, y así no se inunda el canal de eventos.
            const ahora = performance.now();
            if (ahora - ultimoAviso.current > 500) {
              ultimoAviso.current = ahora;
              void desdePopup({
                t: "tiempo",
                position: el.currentTime,
                duration: el.duration || 0,
              });
            }
          }}
          onLoadedMetadata={(e) => {
            const el = e.currentTarget;
            if (snap.position > 0.5 && Math.abs(el.currentTime - snap.position) > 0.5) {
              el.currentTime = snap.position;
            }
            setDuracion(el.duration || 0);
            if (snap.playing && el.paused) el.play().catch(() => undefined);
          }}
          onEnded={() => void desdePopup({ t: "fin" })}
          playsInline
          crossOrigin="anonymous"
        >
          {subtitulos.map((s) => (
            <track
              key={s.path}
              kind="subtitles"
              src={mediaSrc(s.path)}
              srcLang={s.lang}
              label={s.label}
            />
          ))}
        </video>
      ) : (
        <div className="flex h-full items-center justify-center text-[12px] text-white/50">
          Nada en reproducción
        </div>
      )}

      {/* ---- Cabecera ---- */}
      <div
        className={`absolute inset-x-0 top-0 flex items-center gap-1 bg-gradient-to-b from-black/80 to-transparent px-2 py-1.5 transition-opacity ${
          controles ? "opacity-100" : "pointer-events-none opacity-0"
        }`}
      >
        <p className="min-w-0 flex-1 truncate text-[11.5px] font-medium">
          {item?.title ?? "Recodio"}
        </p>
        <Icono
          title={encima ? "Dejar de mantener encima" : "Mantener encima de todo"}
          activo={encima}
          onClick={() => void alternarEncima()}
        >
          {encima ? <Pin size={13} /> : <PinOff size={13} />}
        </Icono>
        <Icono
          title="Volver a la ventana de Recodio"
          onClick={() => void ordenar("restaurar")}
        >
          <PictureInPicture2 size={13} />
        </Icono>
        <Icono title="Cerrar" onClick={() => void getCurrentWindow().close()}>
          <X size={13} />
        </Icono>
      </div>

      {/* ---- Controles ---- */}
      <div
        className={`absolute inset-x-0 bottom-0 flex flex-col gap-1 bg-gradient-to-t from-black/85 to-transparent px-2.5 pb-2 pt-4 transition-opacity ${
          controles ? "opacity-100" : "pointer-events-none opacity-0"
        }`}
      >
        <div className="flex items-center gap-1.5">
          <span className="w-9 shrink-0 text-right text-[10px] tabular-nums text-white/70">
            {reloj(mostrado)}
          </span>
          <input
            type="range"
            min={0}
            max={Math.max(total, 0.1)}
            step={0.1}
            value={mostrado}
            onChange={(e) => setArrastrando(Number(e.target.value))}
            onPointerUp={() => {
              if (arrastrando !== null) void desdePopup({ t: "saltar", s: arrastrando });
              setArrastrando(null);
            }}
            onKeyUp={() => {
              if (arrastrando !== null) void desdePopup({ t: "saltar", s: arrastrando });
              setArrastrando(null);
            }}
            className="h-1 flex-1 cursor-pointer accent-[var(--rc-accent)]"
            aria-label="Posición"
          />
          <span className="w-9 shrink-0 text-[10px] tabular-nums text-white/70">
            {reloj(total)}
          </span>
        </div>

        <div className="flex items-center gap-0.5">
          <Icono title="Anterior" onClick={() => void ordenar("anterior")}>
            <SkipBack size={14} />
          </Icono>
          <Icono
            title={snap.playing ? "Pausar" : "Reproducir"}
            onClick={() => void ordenar("alternar")}
          >
            {snap.playing ? <Pause size={16} /> : <Play size={16} />}
          </Icono>
          <Icono title="Siguiente" onClick={() => void ordenar("siguiente")}>
            <SkipForward size={14} />
          </Icono>
          <Icono
            title={snap.muted ? "Quitar silencio" : "Silenciar"}
            onClick={() => void ordenar("silencio")}
          >
            {snap.muted || snap.volume === 0 ? (
              <VolumeX size={14} />
            ) : (
              <Volume2 size={14} />
            )}
          </Icono>
          <input
            type="range"
            min={0}
            max={1}
            step={0.01}
            value={snap.muted ? 0 : snap.volume}
            onChange={(e) => void desdePopup({ t: "volumen", v: Number(e.target.value) })}
            className="h-1 w-14 cursor-pointer accent-[var(--rc-accent)]"
            aria-label="Volumen"
          />

          <span className="ml-auto flex items-center gap-0.5">
            {snap.total > 1 && (
              <span className="mr-1 text-[10px] tabular-nums text-white/50">
                {snap.index + 1}/{snap.total}
              </span>
            )}
            {subtitulos.length > 0 && (
              <Icono
                title={subActivo ? "Quitar subtítulos" : "Poner subtítulos"}
                activo={subActivo !== null}
                onClick={() => {
                  // Con una sola pista basta encender y apagar; con varias, se
                  // van pasando y al final se vuelve a «sin subtítulos».
                  const i = subtitulos.findIndex((s) => s.lang === subActivo);
                  const siguiente = subtitulos[i + 1];
                  setSubActivo(
                    subActivo === null
                      ? subtitulos[0].lang
                      : (siguiente?.lang ?? null),
                  );
                }}
              >
                <Subtitles size={14} />
              </Icono>
            )}
            <Icono
              title={completa ? "Salir de pantalla completa (F)" : "Pantalla completa (F)"}
              onClick={() => void alternarCompleta()}
            >
              {completa ? <Minimize size={14} /> : <Maximize size={14} />}
            </Icono>
          </span>
        </div>
      </div>
    </div>
  );
}

function Icono({
  children,
  title,
  onClick,
  activo,
}: {
  children: React.ReactNode;
  title: string;
  onClick: () => void;
  activo?: boolean;
}) {
  return (
    <button
      type="button"
      title={title}
      onClick={onClick}
      className={`rounded-md p-1.5 transition hover:bg-white/15 ${
        activo ? "text-accent" : "text-white/80 hover:text-white"
      }`}
    >
      {children}
    </button>
  );
}

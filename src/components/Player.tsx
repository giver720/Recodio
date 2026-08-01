import {
  ChevronDown,
  ListMusic,
  Maximize2,
  Music,
  Pause,
  Play,
  Repeat,
  Repeat1,
  Shuffle,
  SkipBack,
  SkipForward,
  Volume1,
  Volume2,
  VolumeX,
  X,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { isVideo, mediaSrc, usePlayer } from "../lib/player";
import { useStore } from "../lib/store";

/** mm:ss, o h:mm:ss si pasa de la hora. */
function clock(total: number): string {
  if (!Number.isFinite(total) || total < 0) return "0:00";
  const s = Math.floor(total % 60);
  const m = Math.floor((total / 60) % 60);
  const h = Math.floor(total / 3600);
  const dosDigitos = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${dosDigitos(m)}:${dosDigitos(s)}` : `${m}:${dosDigitos(s)}`;
}

export function Player() {
  const p = usePlayer();
  const toast = useStore((s) => s.toast);
  const ref = useRef<HTMLVideoElement>(null);
  const [arrastrando, setArrastrando] = useState<number | null>(null);

  // Play/pausa. El navegador puede rechazar la reproducción, así que el estado
  // se corrige si eso ocurre en vez de quedarse mintiendo.
  useEffect(() => {
    const el = ref.current;
    if (!el || !p.current) return;
    if (p.playing) {
      el.play().catch(() => usePlayer.setState({ playing: false }));
    } else {
      el.pause();
    }
  }, [p.playing, p.current]);

  useEffect(() => {
    const el = ref.current;
    if (el) {
      el.volume = p.volume;
      el.muted = p.muted;
      el.playbackRate = p.rate;
    }
  }, [p.volume, p.muted, p.rate, p.current]);

  // Solo se salta cuando la diferencia es real: asignar currentTime en cada
  // actualización cortaría el sonido continuamente.
  useEffect(() => {
    const el = ref.current;
    if (el && Math.abs(el.currentTime - p.position) > 0.5) {
      el.currentTime = p.position;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.position]);

  useEffect(() => {
    if (!p.current) return;
    const manejar = (e: KeyboardEvent) => {
      const dentroDeCampo =
        e.target instanceof HTMLElement &&
        ["INPUT", "TEXTAREA", "SELECT"].includes(e.target.tagName);
      if (dentroDeCampo) return;

      if (e.code === "Space") {
        e.preventDefault();
        p.toggle();
      } else if (e.code === "ArrowRight" && e.shiftKey) {
        p.next();
      } else if (e.code === "ArrowLeft" && e.shiftKey) {
        p.previous();
      } else if (e.code === "ArrowRight") {
        p.seek(p.position + 5);
      } else if (e.code === "ArrowLeft") {
        p.seek(p.position - 5);
      } else if (e.code === "Escape" && p.expanded) {
        p.setExpanded(false);
      }
    };
    window.addEventListener("keydown", manejar);
    return () => window.removeEventListener("keydown", manejar);
  }, [p]);

  if (!p.current) return null;

  const item = p.current;
  const esVideo = isVideo(item);
  const mostrado = arrastrando ?? p.position;
  // El archivo manda sobre lo que dijera la web al descargarlo.
  const total = p.duration || item.duration || 0;

  const media = (
    <video
      ref={ref}
      src={mediaSrc(item.filePath)}
      className={p.expanded && esVideo ? "max-h-full max-w-full" : "hidden"}
      onTimeUpdate={(e) =>
        p.reportTime(e.currentTarget.currentTime, e.currentTarget.duration || 0)
      }
      onLoadedMetadata={(e) =>
        p.reportTime(e.currentTarget.currentTime, e.currentTarget.duration || 0)
      }
      onEnded={() => p.next(true)}
      onError={() => {
        toast(
          "error",
          `No se puede reproducir «${item.title}». Puede que el formato no sea compatible con el reproductor interno.`,
        );
        usePlayer.setState({ playing: false });
      }}
      playsInline
    />
  );

  const barraProgreso = (
    <div className="flex items-center gap-2">
      <span className="w-10 shrink-0 text-right text-[11px] tabular-nums text-muted">
        {clock(mostrado)}
      </span>
      <input
        type="range"
        min={0}
        max={Math.max(total, 0.1)}
        step={0.1}
        value={mostrado}
        onChange={(e) => setArrastrando(Number(e.target.value))}
        onPointerUp={() => {
          if (arrastrando !== null) p.seek(arrastrando);
          setArrastrando(null);
        }}
        onKeyUp={() => {
          if (arrastrando !== null) p.seek(arrastrando);
          setArrastrando(null);
        }}
        className="h-1 flex-1 cursor-pointer accent-[var(--rc-accent)]"
        aria-label="Posición"
      />
      <span className="w-10 shrink-0 text-[11px] tabular-nums text-muted">
        {clock(total)}
      </span>
    </div>
  );

  const controles = (
    <div className="flex items-center gap-1">
      <Boton title="Aleatorio" activo={p.shuffle} onClick={p.toggleShuffle}>
        <Shuffle size={15} />
      </Boton>
      <Boton title="Anterior" onClick={p.previous}>
        <SkipBack size={17} />
      </Boton>
      <button
        type="button"
        onClick={p.toggle}
        title={p.playing ? "Pausar" : "Reproducir"}
        className="mx-1 flex h-9 w-9 items-center justify-center rounded-full bg-gradient-to-br from-accent to-accent2 text-white shadow-lg shadow-accent/30 transition hover:brightness-110"
      >
        {p.playing ? <Pause size={17} /> : <Play size={17} className="ml-0.5" />}
      </button>
      <Boton title="Siguiente" onClick={() => p.next()}>
        <SkipForward size={17} />
      </Boton>
      <Boton
        title={
          p.repeat === "one"
            ? "Repetir esta"
            : p.repeat === "all"
              ? "Repetir la lista"
              : "Sin repetición"
        }
        activo={p.repeat !== "off"}
        onClick={p.cycleRepeat}
      >
        {p.repeat === "one" ? <Repeat1 size={15} /> : <Repeat size={15} />}
      </Boton>
    </div>
  );

  const volumen = (
    <div className="flex items-center gap-1.5">
      <Boton title={p.muted ? "Quitar silencio" : "Silenciar"} onClick={p.toggleMute}>
        {p.muted || p.volume === 0 ? (
          <VolumeX size={15} />
        ) : p.volume < 0.5 ? (
          <Volume1 size={15} />
        ) : (
          <Volume2 size={15} />
        )}
      </Boton>
      <input
        type="range"
        min={0}
        max={1}
        step={0.01}
        value={p.muted ? 0 : p.volume}
        onChange={(e) => p.setVolume(Number(e.target.value))}
        className="h-1 w-20 cursor-pointer accent-[var(--rc-accent)]"
        aria-label="Volumen"
      />
    </div>
  );

  // ---- Vista ampliada, para vídeo ----
  if (p.expanded && esVideo) {
    return (
      <div className="fixed inset-0 z-40 flex flex-col bg-black/95 backdrop-blur">
        <div className="flex items-center gap-3 px-5 py-3 text-white">
          <button
            type="button"
            onClick={() => p.setExpanded(false)}
            title="Volver a la barra (Esc)"
            className="rounded-lg p-1.5 transition hover:bg-white/10"
          >
            <ChevronDown size={18} />
          </button>
          <p className="min-w-0 flex-1 truncate text-[14px] font-medium">{item.title}</p>
          <button
            type="button"
            onClick={p.stop}
            title="Cerrar"
            className="rounded-lg p-1.5 transition hover:bg-white/10"
          >
            <X size={18} />
          </button>
        </div>

        <div
          className="flex min-h-0 flex-1 items-center justify-center"
          onClick={p.toggle}
        >
          {media}
        </div>

        <div className="flex flex-col gap-2 px-6 py-4">
          {barraProgreso}
          <div className="flex items-center">
            <div className="flex-1">{volumen}</div>
            {controles}
            <div className="flex flex-1 justify-end">
              <Velocidad rate={p.rate} onChange={p.setRate} />
            </div>
          </div>
        </div>
      </div>
    );
  }

  // ---- Barra inferior ----
  return (
    <div className="flex items-center gap-4 border-t border-line bg-surface2/80 px-4 py-2.5 backdrop-blur">
      {media}

      <div className="flex min-w-0 flex-1 items-center gap-3">
        <div className="flex h-10 w-10 shrink-0 items-center justify-center overflow-hidden rounded-lg bg-surface3">
          {item.thumbnail ? (
            <img src={item.thumbnail} alt="" className="h-full w-full object-cover" />
          ) : esVideo ? (
            <ListMusic size={16} className="text-muted" />
          ) : (
            <Music size={16} className="text-muted" />
          )}
        </div>
        <div className="min-w-0">
          <p className="truncate text-[12.5px] font-medium">{item.title}</p>
          <p className="truncate text-[11px] text-muted">
            {item.uploader ?? (esVideo ? "Vídeo" : "Música")}
            {p.queue.length > 1 && ` · ${p.index + 1} de ${p.queue.length}`}
          </p>
        </div>
      </div>

      <div className="flex min-w-0 flex-[2] flex-col gap-1">
        <div className="flex justify-center">{controles}</div>
        {barraProgreso}
      </div>

      <div className="flex flex-1 items-center justify-end gap-2">
        <Velocidad rate={p.rate} onChange={p.setRate} />
        {volumen}
        {esVideo && (
          <Boton title="Ampliar" onClick={() => p.setExpanded(true)}>
            <Maximize2 size={15} />
          </Boton>
        )}
        <Boton title="Cerrar el reproductor" onClick={p.stop}>
          <X size={15} />
        </Boton>
      </div>
    </div>
  );
}

function Boton({
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
      className={`rounded-lg p-1.5 transition hover:bg-surface3 ${
        activo ? "text-accent" : "text-muted hover:text-fg"
      }`}
    >
      {children}
    </button>
  );
}

function Velocidad({ rate, onChange }: { rate: number; onChange: (r: number) => void }) {
  const opciones = [0.5, 0.75, 1, 1.25, 1.5, 2];
  return (
    <button
      type="button"
      title="Velocidad de reproducción"
      onClick={() => onChange(opciones[(opciones.indexOf(rate) + 1) % opciones.length])}
      className={`rounded-lg px-2 py-1 text-[11.5px] tabular-nums transition hover:bg-surface3 ${
        rate === 1 ? "text-muted hover:text-fg" : "text-accent"
      }`}
    >
      {rate}×
    </button>
  );
}

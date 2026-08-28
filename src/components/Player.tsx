import {
  ChevronDown,
  ListMusic,
  Maximize,
  Maximize2,
  Minimize,
  Music,
  MonitorPlay,
  Ratio,
  Subtitles,
  Pause,
  PictureInPicture2,
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
import {
  FIT_HINTS,
  FIT_LABELS,
  isVideo,
  mediaSrc,
  usePlayer,
  type FitMode,
} from "../lib/player";
import { api } from "../lib/api";
import { useAudioBoost } from "../lib/audioBoost";
import { useAudioMeter } from "../lib/audioMeter";
import { AudioQuickControl } from "./AudioQuickControl";
import { abrirPopup, cerrarPopup } from "../lib/popup";
import { usePuenteConPopup } from "../lib/popupBridge";
import { useStore } from "../lib/store";
import type { SubtitleTrack } from "../lib/types";

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
  usePuenteConPopup();
  const ref = useRef<HTMLVideoElement>(null);
  const [arrastrando, setArrastrando] = useState<number | null>(null);
  const [pantallaCompleta, setPantallaCompleta] = useState(false);
  const contenedor = useRef<HTMLDivElement>(null);
  const [subtitulos, setSubtitulos] = useState<SubtitleTrack[]>([]);
  const [subActivo, setSubActivo] = useState<string | null>(null);
  const [menuSubs, setMenuSubs] = useState(false);

  useAudioBoost(
    ref,
    p.audio,
    p.current?.id,
    useAudioMeter.getState().report,
    () => {
      usePlayer.getState().patchAudio({ enabled: false });
      toast("error", "Audio Pro no está disponible en este reproductor. El audio continuará sin procesar.");
    },
  );

  const audioRapido = (
    <AudioQuickControl
      audio={p.audio}
      onChange={p.setAudio}
      onOpenSettings={() => window.dispatchEvent(new CustomEvent("recodio:abrir-audio"))}
    />
  );

  // El navegador puede salir de pantalla completa por su cuenta (Esc del
  // sistema, cambio de ventana), así que el estado se lee de él, no se supone.
  useEffect(() => {
    const alCambiar = () => setPantallaCompleta(Boolean(document.fullscreenElement));
    document.addEventListener("fullscreenchange", alCambiar);
    return () => document.removeEventListener("fullscreenchange", alCambiar);
  }, []);

  async function alternarPantallaCompleta() {
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
        return;
      }
      // Se amplía primero: en la barra de abajo no hay nada que llenar. Y hay
      // que dejar que se pinte, porque el contenedor está oculto mientras el
      // reproductor va replegado y el navegador rechaza poner en pantalla
      // completa un elemento que todavía no se ve.
      if (!usePlayer.getState().expanded) {
        usePlayer.getState().setExpanded(true);
        await new Promise((listo) =>
          requestAnimationFrame(() => requestAnimationFrame(listo)),
        );
      }
      await contenedor.current?.requestFullscreen();
    } catch {
      /* el sistema puede negarlo; no hay nada que hacer */
    }
  }

  useEffect(() => {
    setSubActivo(null);
    setMenuSubs(false);
    if (!p.current || !isVideo(p.current)) {
      setSubtitulos([]);
      return;
    }
    let vigente = true;
    api
      .subtitlesFor(p.current.filePath)
      .then((s) => vigente && setSubtitulos(s))
      .catch(() => vigente && setSubtitulos([]));
    return () => {
      vigente = false;
    };
  }, [p.current]);

  // El elemento de vídeo expone las pistas cargadas; se activa la elegida y se
  // apagan las demás, porque el navegador puede encender una por su cuenta.
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    for (let i = 0; i < el.textTracks.length; i++) {
      const pista = el.textTracks[i];
      pista.mode = pista.language === subActivo ? "showing" : "disabled";
    }
  }, [subActivo, subtitulos]);

  // Play/pausa. El navegador puede rechazar la reproducción, así que el estado
  // se corrige si eso ocurre en vez de quedarse mintiendo.
  useEffect(() => {
    const el = ref.current;
    if (!el || !p.current) return;
    // Con la ventana flotante abierta reproduce ella; aquí solo se calla, o se
    // oiría el mismo archivo dos veces y desfasado.
    if (p.playing && !p.popup) {
      el.play().catch(() => usePlayer.setState({ playing: false }));
    } else {
      el.pause();
    }
  }, [p.playing, p.current, p.popup]);

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
    // Mientras reproduce la flotante, la posición que llega es la suya: seguirla
    // aquí sería mover un vídeo en pausa que nadie está viendo.
    if (el && !p.popup && Math.abs(el.currentTime - p.position) > 0.5) {
      el.currentTime = p.position;
    }
    // Al cerrarse la flotante también hay que recolocar: aquí el vídeo se quedó
    // donde estaba cuando se abrió.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.position, p.popup]);

  // Controles del sistema: el panel multimedia de Windows, las teclas de
  // reproducción del teclado y la pantalla de bloqueo. Se alimenta del elemento
  // de vídeo que ya existe, así que no hay un segundo reproductor que sincronizar.
  useEffect(() => {
    const ms = navigator.mediaSession;
    if (!ms || !p.current) return;

    ms.metadata = new MediaMetadata({
      title: p.current.title,
      artist: p.current.uploader ?? "",
      album: p.queue.length > 1 ? `${p.index + 1} de ${p.queue.length}` : "",
      artwork: p.current.thumbnail
        ? [{ src: p.current.thumbnail, sizes: "512x512" }]
        : [],
    });
    ms.playbackState = p.playing ? "playing" : "paused";

    const acciones: [MediaSessionAction, MediaSessionActionHandler][] = [
      ["play", () => usePlayer.setState({ playing: true })],
      ["pause", () => usePlayer.setState({ playing: false })],
      ["nexttrack", () => usePlayer.getState().next()],
      ["previoustrack", () => usePlayer.getState().previous()],
      ["stop", () => usePlayer.getState().stop()],
      [
        "seekbackward",
        (d) => usePlayer.getState().seek(p.position - (d.seekOffset ?? 10)),
      ],
      [
        "seekforward",
        (d) => usePlayer.getState().seek(p.position + (d.seekOffset ?? 10)),
      ],
      [
        "seekto",
        (d) => d.seekTime != null && usePlayer.getState().seek(d.seekTime),
      ],
    ];
    for (const [accion, manejador] of acciones) {
      // Los sistemas no soportan todas las acciones; las que no, lanzan.
      try {
        ms.setActionHandler(accion, manejador);
      } catch {
        /* esa acción no está disponible aquí */
      }
    }

    return () => {
      for (const [accion] of acciones) {
        try {
          ms.setActionHandler(accion, null);
        } catch {
          /* nada que limpiar */
        }
      }
    };
  }, [p.current, p.playing, p.index, p.queue.length, p.position]);

  // La posición en la barra del sistema, para que el panel muestre el avance.
  useEffect(() => {
    const ms = navigator.mediaSession;
    if (!ms?.setPositionState || !p.current || !p.duration) return;
    try {
      ms.setPositionState({
        duration: p.duration,
        position: Math.min(p.position, p.duration),
        playbackRate: p.rate,
      });
    } catch {
      /* algunas versiones no lo admiten */
    }
  }, [p.position, p.duration, p.rate, p.current]);

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
      } else if (e.code === "KeyF" && p.current && isVideo(p.current)) {
        e.preventDefault();
        alternarPantallaCompleta();
      } else if (e.code === "Escape" && p.expanded && !document.fullscreenElement) {
        // Con pantalla completa activa, Esc lo gestiona el propio sistema.
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
      className={
        p.expanded && esVideo
          ? `h-full w-full ${
              {
                contain: "object-contain",
                cover: "object-cover",
                fill: "object-fill",
                none: "object-none",
              }[p.fit]
            }`
          : "hidden"
      }
      onTimeUpdate={(e) =>
        p.reportTime(e.currentTarget.currentTime, e.currentTarget.duration || 0)
      }
      onLoadedMetadata={(e) => {
        const el = e.currentTarget;
        const { position, playing } = usePlayer.getState();
        // Red de seguridad: si el elemento se rehiciera por lo que sea, nacería
        // en el segundo cero y en pausa aunque la reproducción fuera por la
        // mitad. Se recoloca antes de informar, para no machacar la posición
        // buena con el cero del elemento recién cargado.
        if (position > 0.5 && Math.abs(el.currentTime - position) > 0.5) {
          el.currentTime = position;
        }
        if (playing && el.paused) {
          el.play().catch(() => usePlayer.setState({ playing: false }));
        }
        p.reportTime(el.currentTime, el.duration || 0);
      }}
      onEnded={() => p.next(true)}
      onError={() => {
        toast(
          "error",
          `No se puede reproducir «${item.title}». Puede que el formato no sea compatible con el reproductor interno.`,
        );
        usePlayer.setState({ playing: false });
      }}
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

  // Solo tiene sentido en vídeos: una canción ya es solo audio.
  const modoAudio = esVideo ? (
    <Boton
      title={
        p.audioOnly
          ? "Volver a ver el vídeo"
          : "Escucharlo como música, sin vídeo"
      }
      activo={p.audioOnly}
      onClick={p.toggleAudioOnly}
    >
      {p.audioOnly ? <MonitorPlay size={15} /> : <Music size={15} />}
    </Boton>
  ) : null;

  const selectorSubs = esVideo && subtitulos.length > 0 ? (
    <div className="relative">
      <Boton
        title="Subtítulos"
        activo={subActivo !== null}
        onClick={() => setMenuSubs((v) => !v)}
      >
        <Subtitles size={15} />
      </Boton>
      {menuSubs && (
        <div className="absolute bottom-full right-0 mb-1 min-w-40 overflow-hidden rounded-xl border border-line bg-surface2 py-1 shadow-xl">
          <BotonSub activo={subActivo === null} onClick={() => { setSubActivo(null); setMenuSubs(false); }}>
            Sin subtítulos
          </BotonSub>
          {subtitulos.map((s) => (
            <BotonSub
              key={s.path}
              activo={subActivo === s.lang}
              onClick={() => { setSubActivo(s.lang); setMenuSubs(false); }}
            >
              {s.label}
            </BotonSub>
          ))}
        </div>
      )}
    </div>
  ) : null;

  // Solo para vídeo: una canción no necesita una ventana donde mirarla.
  const botonPopup = esVideo ? (
    <Boton
      title={
        p.popup
          ? "Cerrar la ventana flotante"
          : "Ver en una ventana flotante, encima de todo"
      }
      activo={p.popup}
      onClick={() => {
        if (p.popup) {
          p.setPopup(false);
          void cerrarPopup();
        } else {
          abrirPopup().catch(() =>
            toast("error", "No se ha podido abrir la ventana flotante."),
          );
        }
      }}
    >
      <PictureInPicture2 size={15} />
    </Boton>
  ) : null;

  const botonPantalla = esVideo ? (
    <Boton
      title={
        pantallaCompleta ? "Salir de pantalla completa (F)" : "Pantalla completa (F)"
      }
      onClick={alternarPantallaCompleta}
    >
      {pantallaCompleta ? <Minimize size={15} /> : <Maximize size={15} />}
    </Boton>
  ) : null;

  const ajuste = esVideo && p.expanded ? (
    <button
      type="button"
      title={`Encaje del vídeo: ${FIT_LABELS[p.fit]} — ${FIT_HINTS[p.fit]}`}
      onClick={() => {
        const orden: FitMode[] = ["contain", "cover", "fill", "none"];
        p.setFit(orden[(orden.indexOf(p.fit) + 1) % orden.length]);
      }}
      className="flex items-center gap-1.5 rounded-lg px-2 py-1 text-[11.5px] text-muted transition hover:bg-white/10 hover:text-fg"
    >
      <Ratio size={15} />
      {FIT_LABELS[p.fit]}
    </button>
  ) : null;

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

  // Las dos vistas se montan siempre y solo se oculta la que no toca.
  //
  // Antes se devolvía un árbol u otro según `ampliado`, y eso destruía el
  // elemento de vídeo cada vez que se cambiaba de vista: React encontraba tipos
  // distintos en la misma posición y lo rehacía. El elemento nuevo nacía en el
  // segundo cero y en pausa, y el efecto de play no volvía a dispararse porque
  // ni `playing` ni `current` habían cambiado. Por eso pasar a «solo audio»
  // paraba la reproducción en seco. Manteniéndolo en el árbol, cambiar de vista
  // es solo un cambio de CSS y el sonido no se entera.
  const ampliado = p.expanded && esVideo;

  return (
    <>
      {/* ---- Vista ampliada, para vídeo ---- */}
      <div
        ref={contenedor}
        className={
          ampliado
            ? "fixed inset-0 z-40 flex flex-col bg-black/95 backdrop-blur"
            : "hidden"
        }
      >
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

        {/* El vídeo vive aquí y solo aquí, ampliado o no. */}
        <div
          className="flex min-h-0 flex-1 items-center justify-center"
          onClick={p.toggle}
        >
          {media}
        </div>

        <div className="flex flex-col gap-2 px-6 py-4">
          {ampliado && barraProgreso}
          <div className="flex items-center">
            <div className="flex-1">{ampliado && volumen}</div>
            {ampliado && controles}
            <div className="flex flex-1 items-center justify-end gap-1">
              {ampliado && (
                <>
                  {ajuste}
                  {selectorSubs}
                  {botonPopup}
                  {botonPantalla}
                  {modoAudio}
                  {audioRapido}
                  <Velocidad rate={p.rate} onChange={p.setRate} />
                </>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* ---- Barra inferior ---- */}
      <div
        className={
          ampliado
            ? "hidden"
            : "flex items-center gap-4 border-t border-line bg-surface2/80 px-4 py-2.5 backdrop-blur"
        }
      >
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
          <div className="flex justify-center">{!ampliado && controles}</div>
          {!ampliado && barraProgreso}
        </div>

        <div className="flex flex-1 items-center justify-end gap-2">
          {!ampliado && (
            <>
              <Velocidad rate={p.rate} onChange={p.setRate} />
              {volumen}
              {audioRapido}
              {selectorSubs}
              {botonPopup}
              {modoAudio}
              {esVideo && !p.audioOnly && !p.popup && (
                <Boton title="Ampliar" onClick={() => p.setExpanded(true)}>
                  <Maximize2 size={15} />
                </Boton>
              )}
              <Boton title="Cerrar el reproductor" onClick={p.stop}>
                <X size={15} />
              </Boton>
            </>
          )}
        </div>
      </div>
    </>
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

function BotonSub({
  children,
  activo,
  onClick,
}: {
  children: React.ReactNode;
  activo: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`block w-full px-3 py-1.5 text-left text-[12px] transition hover:bg-surface3 ${
        activo ? "text-accent" : "text-fg"
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

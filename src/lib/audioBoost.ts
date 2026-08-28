import { useEffect, useRef, type RefObject } from "react";

/** El mismo recorrido que ofrece Vortex. */
export const MAX_AUDIO_BOOST_DB = 15;

export function dbToGain(db: number): number {
  const safe = Math.min(MAX_AUDIO_BOOST_DB, Math.max(0, db));
  return 10 ** (safe / 20);
}

interface AudioGraph {
  context: AudioContext;
  gain: GainNode;
  limiter: DynamicsCompressorNode;
}

/**
 * Inserta una etapa de ganancia entre el elemento multimedia y la salida.
 *
 * El volumen normal del reproductor sigue viviendo en `HTMLMediaElement.volume`;
 * este nodo sólo aporta la ganancia adicional. El compresor funciona como
 * protección de picos para que el boost no convierta inmediatamente todo lo que
 * supera 0 dBFS en distorsión digital.
 */
export function useAudioBoost(
  mediaRef: RefObject<HTMLVideoElement | null>,
  enabled: boolean,
  boostDb: number,
  peakProtection: boolean,
  mediaId: string | undefined,
) {
  const graph = useRef<AudioGraph | null>(null);

  useEffect(() => {
    const media = mediaRef.current;
    if (!media || (!enabled && !graph.current)) return;

    if (!graph.current) {
      try {
        const context = new AudioContext();
        const source = context.createMediaElementSource(media);
        const gain = context.createGain();
        const limiter = context.createDynamicsCompressor();
        source.connect(gain).connect(limiter).connect(context.destination);
        graph.current = { context, gain, limiter };
      } catch (error) {
        // Un WebView sin Web Audio debe conservar al menos la reproducción
        // normal, en lugar de derribar todo el reproductor por el extra.
        console.error("No se pudo iniciar el boost de audio", error);
        return;
      }
    }

    const current = graph.current;
    const now = current.context.currentTime;
    const target = enabled ? dbToGain(boostDb) : 1;
    current.gain.gain.cancelScheduledValues(now);
    current.gain.gain.setTargetAtTime(target, now, 0.015);

    if (peakProtection) {
      current.limiter.threshold.setValueAtTime(-1, now);
      current.limiter.knee.setValueAtTime(0, now);
      current.limiter.ratio.setValueAtTime(20, now);
      current.limiter.attack.setValueAtTime(0.003, now);
      current.limiter.release.setValueAtTime(0.25, now);
    } else {
      // Ratio 1:1 equivale a pasar limpio sin tener que reconstruir el grafo.
      current.limiter.threshold.setValueAtTime(0, now);
      current.limiter.knee.setValueAtTime(0, now);
      current.limiter.ratio.setValueAtTime(1, now);
    }

    const despertar = () => {
      if (current.context.state === "suspended") void current.context.resume();
    };
    despertar();
    media.addEventListener("play", despertar);
    window.addEventListener("pointerdown", despertar, { once: true });
    return () => {
      media.removeEventListener("play", despertar);
      window.removeEventListener("pointerdown", despertar);
    };
  }, [mediaRef, enabled, boostDb, peakProtection, mediaId]);

  // No se cierra el contexto en el cleanup: React StrictMode simula un
  // desmontaje y volvería a intentar enlazar el mismo <video>, algo que Web
  // Audio prohíbe. La vida del contexto coincide con la de su WebView (la
  // principal o la flotante), que es quien finalmente libera los recursos.
}

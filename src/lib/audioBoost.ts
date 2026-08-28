import { useEffect, useRef, type RefObject } from "react";
import {
  EQ_FREQUENCIES,
  buildAudioPlan,
  classifySignal,
  type AudioMeterSnapshot,
  type AudioSettings,
} from "./audioSettings";

interface AudioGraph {
  context: AudioContext;
  filters: BiquadFilterNode[];
  boost: GainNode;
  limiter: DynamicsCompressorNode;
  before: AnalyserNode;
  after: AnalyserNode;
}

export function smoothAudioParam(param: AudioParam, value: number, now: number) {
  param.cancelScheduledValues(now);
  param.setTargetAtTime(value, now, 0.015);
}

export function connectAudioNodes(
  source: AudioNode,
  filters: AudioNode[],
  boost: AudioNode,
  before: AudioNode,
  limiter: AudioNode,
  after: AudioNode,
  destination: AudioNode,
) {
  let tail = source;
  for (const filter of filters) {
    tail.connect(filter);
    tail = filter;
  }
  tail.connect(boost).connect(before).connect(limiter).connect(after).connect(destination);
}

function peakDb(analyser: AnalyserNode, buffer: Float32Array<ArrayBuffer>): number {
  analyser.getFloatTimeDomainData(buffer);
  let peak = 0;
  for (const sample of buffer) peak = Math.max(peak, Math.abs(sample));
  return Math.max(-60, 20 * Math.log10(Math.max(peak, 0.001)));
}

function createGraph(media: HTMLMediaElement): AudioGraph {
  const context = new AudioContext();
  const source = context.createMediaElementSource(media);
  const filters = EQ_FREQUENCIES.map((frequency) => {
    const filter = context.createBiquadFilter();
    filter.type = "peaking";
    filter.frequency.value = frequency;
    filter.Q.value = Math.SQRT2;
    return filter;
  });
  const boost = context.createGain();
  const before = context.createAnalyser();
  const limiter = context.createDynamicsCompressor();
  const after = context.createAnalyser();
  before.fftSize = 512;
  after.fftSize = 512;

  connectAudioNodes(source, filters, boost, before, limiter, after, context.destination);
  return { context, filters, boost, limiter, before, after };
}

/** Procesador completo del elemento multimedia, compartido por audio y vídeo. */
export function useAudioBoost(
  mediaRef: RefObject<HTMLVideoElement | null>,
  settings: AudioSettings,
  mediaId: string | undefined,
  onMeter?: (meter: AudioMeterSnapshot) => void,
  onUnavailable?: () => void,
) {
  const graph = useRef<AudioGraph | null>(null);
  const unavailable = useRef(false);
  const callbacks = useRef({ onMeter, onUnavailable });
  callbacks.current = { onMeter, onUnavailable };

  useEffect(() => {
    const media = mediaRef.current;
    if (!media || unavailable.current || (!settings.enabled && !graph.current)) return;
    if (!graph.current) {
      try {
        graph.current = createGraph(media);
      } catch (error) {
        unavailable.current = true;
        console.error("No se pudo iniciar Audio Pro", error);
        callbacks.current.onUnavailable?.();
        return;
      }
    }

    const current = graph.current;
    const plan = buildAudioPlan(settings);
    const now = current.context.currentTime;
    current.filters.forEach((filter, index) => smoothAudioParam(filter.gain, plan.bands[index], now));
    smoothAudioParam(current.boost.gain, plan.boostGain, now);

    if (plan.limiterOn) {
      smoothAudioParam(current.limiter.threshold, -1, now);
      smoothAudioParam(current.limiter.knee, 0, now);
      smoothAudioParam(current.limiter.ratio, 20, now);
      smoothAudioParam(current.limiter.attack, 0.003, now);
      smoothAudioParam(current.limiter.release, 0.25, now);
    } else {
      smoothAudioParam(current.limiter.threshold, 0, now);
      smoothAudioParam(current.limiter.knee, 0, now);
      smoothAudioParam(current.limiter.ratio, 1, now);
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
  }, [mediaRef, settings, mediaId]);

  useEffect(() => {
    const current = graph.current;
    const media = mediaRef.current;
    if (!current || !media || !settings.enabled || settings.bypass) {
      callbacks.current.onMeter?.({ beforeDb: -60, afterDb: -60, reductionDb: 0, risk: "safe", active: false });
      return;
    }
    const beforeBuffer = new Float32Array(current.before.fftSize);
    const afterBuffer = new Float32Array(current.after.fftSize);
    let frame = 0;
    let last = 0;
    const sample = (time: number) => {
      if (time - last >= 100 && !media.paused) {
        last = time;
        const beforeDb = peakDb(current.before, beforeBuffer);
        const afterDb = peakDb(current.after, afterBuffer);
        callbacks.current.onMeter?.({
          beforeDb,
          afterDb,
          reductionDb: Math.abs(current.limiter.reduction),
          risk: classifySignal(beforeDb),
          active: true,
        });
      }
      frame = requestAnimationFrame(sample);
    };
    frame = requestAnimationFrame(sample);
    return () => cancelAnimationFrame(frame);
  }, [mediaRef, settings.enabled, settings.bypass, mediaId]);

  // El contexto vive lo mismo que su WebView. Cerrarlo en el cleanup rompería
  // React StrictMode, porque un elemento multimedia sólo puede enlazarse una vez.
}

export { dbToGain, MAX_AUDIO_BOOST_DB } from "./audioSettings";

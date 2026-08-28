import { describe, expect, it, vi } from "vitest";
import {
  DEFAULT_AUDIO_SETTINGS,
  EQ_PRESETS,
  applyAudioMode,
  buildAudioPlan,
  classifySignal,
  dbToGain,
  effectiveBands,
  loadAudioSettings,
  resetAudioSettings,
  saveAudioSettings,
  type StorageLike,
} from "./audioSettings";
import { connectAudioNodes, smoothAudioParam } from "./audioBoost";

class MemoryStorage implements StorageLike {
  values = new Map<string, string>();
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
}

describe("Audio Pro", () => {
  it("convierte decibelios a ganancia y limita el recorrido", () => {
    expect(dbToGain(0)).toBe(1);
    expect(dbToGain(6)).toBeCloseTo(1.995, 2);
    expect(dbToGain(99)).toBeCloseTo(5.623, 2);
  });

  it("incluye los 16 presets completos de diez bandas", () => {
    expect(EQ_PRESETS).toHaveLength(16);
    expect(EQ_PRESETS.every((preset) => preset.gains.length === 10)).toBe(true);
  });

  it("combina ecualizador, graves y claridad sin salir de ±12 dB", () => {
    const settings = { ...DEFAULT_AUDIO_SETTINGS, preset: "urban" as const, bassOn: true, bass: 1, clarityOn: true, clarity: 1 };
    const bands = effectiveBands(settings);
    expect(bands).toHaveLength(10);
    expect(Math.max(...bands)).toBeLessThanOrEqual(12);
    expect(Math.min(...bands)).toBeGreaterThanOrEqual(-12);
  });

  it("aplica perfiles completos y vuelve a personal al pedirlo", () => {
    const powerful = applyAudioMode("powerful", DEFAULT_AUDIO_SETTINGS);
    expect(powerful.enabled).toBe(true);
    expect(powerful.preset).toBe("smile");
    expect(powerful.boostDb).toBe(6);
    expect(applyAudioMode("custom", powerful).mode).toBe("custom");
  });

  it("crea bypass neutro y activa limitador sólo durante el procesado", () => {
    const active = buildAudioPlan({ ...DEFAULT_AUDIO_SETTINGS, enabled: true, boostOn: true });
    expect(active.boostGain).toBeGreaterThan(1);
    expect(active.limiterOn).toBe(true);
    const bypass = buildAudioPlan({ ...DEFAULT_AUDIO_SETTINGS, enabled: true, bypass: true, boostOn: true });
    expect(bypass.boostGain).toBe(1);
    expect(bypass.bands).toEqual(Array(10).fill(0));
  });

  it("clasifica la señal medida", () => {
    expect(classifySignal(-6)).toBe("safe");
    expect(classifySignal(-1)).toBe("caution");
    expect(classifySignal(0.1)).toBe("high");
  });

  it("migra los ajustes del boost 0.7.0", () => {
    const storage = new MemoryStorage();
    storage.setItem("recodio.audioBoost.enabled", "1");
    storage.setItem("recodio.audioBoost.db", "9");
    storage.setItem("recodio.audioBoost.peakProtection", "0");
    const migrated = loadAudioSettings(storage);
    expect(migrated.enabled).toBe(true);
    expect(migrated.boostOn).toBe(true);
    expect(migrated.boostDb).toBe(9);
    expect(migrated.peakProtection).toBe(false);
  });

  it("persiste valores saneados y restablece conservando el maestro", () => {
    const storage = new MemoryStorage();
    const saved = saveAudioSettings(storage, { ...DEFAULT_AUDIO_SETTINGS, enabled: true, bass: 9, boostDb: -5 });
    expect(saved.bass).toBe(1);
    expect(saved.boostDb).toBe(0);
    expect(resetAudioSettings(saved).enabled).toBe(true);
  });

  it("conecta los nodos en el orden documentado", () => {
    const order: string[] = [];
    const node = (name: string) => ({ connect(next: { name: string }) { order.push(`${name}>${next.name}`); return next; }, name }) as unknown as AudioNode;
    const source = node("source");
    const filters = [node("eq1"), node("eq2")];
    connectAudioNodes(source, filters, node("boost"), node("before"), node("limiter"), node("after"), node("destination"));
    expect(order).toEqual(["source>eq1", "eq1>eq2", "eq2>boost", "boost>before", "before>limiter", "limiter>after", "after>destination"]);
  });

  it("suaviza parámetros para evitar cambios bruscos", () => {
    const param = { cancelScheduledValues: vi.fn(), setTargetAtTime: vi.fn() } as unknown as AudioParam;
    smoothAudioParam(param, 4, 2);
    expect(param.cancelScheduledValues).toHaveBeenCalledWith(2);
    expect(param.setTargetAtTime).toHaveBeenCalledWith(4, 2, 0.015);
  });
});

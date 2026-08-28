export const EQ_FREQUENCIES = [31, 62, 125, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000] as const;
export const EQ_MIN_DB = -12;
export const EQ_MAX_DB = 12;
export const MAX_AUDIO_BOOST_DB = 15;

export type AudioProMode = "custom" | "safe" | "powerful" | "night" | "voice";
export type EqPresetId =
  | "flat" | "rock" | "pop" | "electronic" | "urban" | "acoustic"
  | "classical" | "bass" | "treble" | "smile" | "vocal" | "night"
  | "cinema" | "tv" | "gaming" | "podcast";

export interface EqPreset {
  id: EqPresetId;
  label: string;
  gains: readonly number[];
}

export interface AudioSettings {
  enabled: boolean;
  bypass: boolean;
  mode: AudioProMode;
  equalizerOn: boolean;
  preset: EqPresetId | null;
  bands: number[];
  boostOn: boolean;
  boostDb: number;
  bassOn: boolean;
  bass: number;
  clarityOn: boolean;
  clarity: number;
  peakProtection: boolean;
}

export interface AudioMeterSnapshot {
  beforeDb: number;
  afterDb: number;
  reductionDb: number;
  risk: "safe" | "caution" | "high";
  active: boolean;
}

export const EQ_PRESETS: readonly EqPreset[] = [
  { id: "flat", label: "Plano", gains: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
  { id: "rock", label: "Rock", gains: [5, 4, 2, -1, -1.5, 0, 2.5, 4, 4.5, 3] },
  { id: "pop", label: "Pop", gains: [-1, 1, 3, 2, -0.5, -1, 1.5, 3.5, 4, 3] },
  { id: "electronic", label: "Electrónica", gains: [7, 6, 3, 0, -2, -1, 1, 3, 5, 6] },
  { id: "urban", label: "Urbano", gains: [8, 6.5, 3, 1, -1, -1.5, 0.5, 2, 3, 2] },
  { id: "acoustic", label: "Acústico", gains: [2, 1.5, 0, 0.5, 1.5, 2, 2, 1.5, 2, 2.5] },
  { id: "classical", label: "Clásica", gains: [3, 2.5, 1, 0, 0, 0, 0.5, 1.5, 2.5, 3] },
  { id: "bass", label: "Graves", gains: [7, 6, 4.5, 2.5, 0, 0, 0, 0, 1, 2] },
  { id: "treble", label: "Agudos", gains: [-1, -1, 0, 0, 0, 1, 2.5, 4, 5.5, 6] },
  { id: "smile", label: "Sonrisa", gains: [6, 5, 3, 0, -2, -2, 0, 3, 5, 6] },
  { id: "vocal", label: "Voz", gains: [-2, -1.5, 0, 1.5, 3.5, 4, 3, 1.5, 0, -1] },
  { id: "night", label: "Noche", gains: [2, 1.5, 1, 1.5, 2.5, 2.5, 2, 1, 0.5, 0] },
  { id: "cinema", label: "Cine", gains: [4, 3, 1, -0.5, 1.5, 3, 3, 1.5, 1, 2] },
  { id: "tv", label: "TV", gains: [-4, -2, 0, 1, 3, 3.5, 2.5, 1, 0, -1] },
  { id: "gaming", label: "Juegos", gains: [3, 2, 0, -1, 0, 2, 4, 4.5, 3, 1] },
  { id: "podcast", label: "Podcast", gains: [-6, -4, -1, 2, 4, 4.5, 3.5, 2, 0, -2] },
] as const;

export const AUDIO_MODE_LABELS: Record<AudioProMode, string> = {
  custom: "Personal", safe: "Seguro", powerful: "Potente", night: "Noche", voice: "Voz",
};

export const DEFAULT_AUDIO_SETTINGS: AudioSettings = {
  enabled: false,
  bypass: false,
  mode: "custom",
  equalizerOn: true,
  preset: "flat",
  bands: Array(10).fill(0),
  boostOn: false,
  boostDb: 6,
  bassOn: false,
  bass: 0,
  clarityOn: false,
  clarity: 0,
  peakProtection: true,
};

const STORAGE_KEY = "recodio.audio.v2";
const AUDIO_MODES: readonly AudioProMode[] = ["custom", "safe", "powerful", "night", "voice"];
const BASS_CURVE = [1, 0.85, 0.6, 0.3, 0.1, 0, 0, 0, 0, 0];
const CLARITY_CURVE = [0, 0, 0, 0, 0.1, 0.35, 0.8, 1, 0.85, 0.4];
const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

export function presetById(id: EqPresetId | null): EqPreset | undefined {
  return EQ_PRESETS.find((preset) => preset.id === id);
}

export function effectiveBands(settings: AudioSettings): number[] {
  const source = settings.equalizerOn
    ? (presetById(settings.preset)?.gains ?? settings.bands)
    : Array(10).fill(0);
  return EQ_FREQUENCIES.map((_, index) => {
    const bass = settings.bassOn ? BASS_CURVE[index] * 10 * clamp(settings.bass, 0, 1) : 0;
    const clarity = settings.clarityOn ? CLARITY_CURVE[index] * 7 * clamp(settings.clarity, 0, 1) : 0;
    return clamp((source[index] ?? 0) + bass + clarity, EQ_MIN_DB, EQ_MAX_DB);
  });
}

export function dbToGain(db: number): number {
  return 10 ** (clamp(db, 0, MAX_AUDIO_BOOST_DB) / 20);
}

export function buildAudioPlan(settings: AudioSettings) {
  const active = settings.enabled && !settings.bypass;
  return {
    active,
    bands: active ? effectiveBands(settings) : Array(10).fill(0),
    boostGain: active && settings.boostOn ? dbToGain(settings.boostDb) : 1,
    limiterOn: active && settings.peakProtection,
  };
}

export function classifySignal(beforeDb: number): AudioMeterSnapshot["risk"] {
  if (beforeDb > 0) return "high";
  if (beforeDb > -3) return "caution";
  return "safe";
}

export function applyAudioMode(mode: AudioProMode, current: AudioSettings): AudioSettings {
  if (mode === "custom") return { ...current, mode };
  const common = { ...current, enabled: true, bypass: false, mode, equalizerOn: true, peakProtection: true };
  if (mode === "safe") return { ...common, preset: "flat", bands: Array(10).fill(0), bassOn: true, bass: 0.15, clarityOn: true, clarity: 0.15, boostOn: false };
  if (mode === "powerful") return { ...common, preset: "smile", bands: [...presetById("smile")!.gains], bassOn: true, bass: 0.7, clarityOn: true, clarity: 0.25, boostOn: true, boostDb: 6 };
  if (mode === "night") return { ...common, preset: "night", bands: [...presetById("night")!.gains], bassOn: true, bass: 0.1, clarityOn: true, clarity: 0.35, boostOn: false };
  return { ...common, preset: "vocal", bands: [...presetById("vocal")!.gains], bassOn: false, bass: 0, clarityOn: true, clarity: 0.5, boostOn: false };
}

function sanitize(raw: Partial<AudioSettings>): AudioSettings {
  const preset = EQ_PRESETS.some((p) => p.id === raw.preset) ? raw.preset! : raw.preset === null ? null : "flat";
  const mode = AUDIO_MODES.includes(raw.mode as AudioProMode) ? raw.mode! : DEFAULT_AUDIO_SETTINGS.mode;
  const bands = Array.isArray(raw.bands) && raw.bands.length === 10
    ? raw.bands.map((v) => clamp(Number(v) || 0, EQ_MIN_DB, EQ_MAX_DB))
    : Array(10).fill(0);
  return {
    ...DEFAULT_AUDIO_SETTINGS,
    ...raw,
    mode,
    preset,
    bands,
    boostDb: clamp(Number(raw.boostDb ?? 6), 0, MAX_AUDIO_BOOST_DB),
    bass: clamp(Number(raw.bass ?? 0), 0, 1),
    clarity: clamp(Number(raw.clarity ?? 0), 0, 1),
  };
}

export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export function loadAudioSettings(storage: StorageLike): AudioSettings {
  const stored = storage.getItem(STORAGE_KEY);
  if (stored) {
    try { return sanitize(JSON.parse(stored) as Partial<AudioSettings>); } catch { /* migrar */ }
  }
  const oldEnabled = storage.getItem("recodio.audioBoost.enabled") === "1";
  const oldDb = Number(storage.getItem("recodio.audioBoost.db") ?? 6);
  const migrated = sanitize({
    enabled: oldEnabled,
    boostOn: oldEnabled,
    boostDb: oldDb,
    peakProtection: storage.getItem("recodio.audioBoost.peakProtection") !== "0",
  });
  storage.setItem(STORAGE_KEY, JSON.stringify(migrated));
  return migrated;
}

export function saveAudioSettings(storage: StorageLike, settings: AudioSettings): AudioSettings {
  const safe = sanitize(settings);
  storage.setItem(STORAGE_KEY, JSON.stringify(safe));
  return safe;
}

export function resetAudioSettings(current: AudioSettings): AudioSettings {
  return { ...DEFAULT_AUDIO_SETTINGS, enabled: current.enabled };
}

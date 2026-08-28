import { RotateCcw, Shield, SlidersHorizontal } from "lucide-react";
import { Toggle } from "./ui";
import {
  AUDIO_MODE_LABELS,
  EQ_FREQUENCIES,
  EQ_MAX_DB,
  EQ_MIN_DB,
  EQ_PRESETS,
  MAX_AUDIO_BOOST_DB,
  applyAudioMode,
  presetById,
  resetAudioSettings,
  type AudioProMode,
  type AudioSettings,
} from "../lib/audioSettings";
import { useAudioMeter } from "../lib/audioMeter";
import { usePlayer } from "../lib/player";

const MODES: AudioProMode[] = ["custom", "safe", "powerful", "night", "voice"];

export function AudioProPanel() {
  const audio = usePlayer((p) => p.audio);
  const setAudio = usePlayer((p) => p.setAudio);
  const active = audio.enabled && !audio.bypass;
  const manual = (patch: Partial<AudioSettings>) => setAudio({ ...audio, ...patch, mode: "custom" });

  return (
    <section id="audio-pro" className="rounded-2xl border border-line bg-surface/75 p-4 shadow-sm">
      <div className="mb-4 flex items-center gap-2.5">
        <SlidersHorizontal size={17} className="text-accent2" />
        <div className="min-w-0 flex-1">
          <h2 className="text-[14px] font-semibold">Audio Pro</h2>
          <p className="text-[11px] text-muted">Ecualización, tono y volumen extra del reproductor interno.</p>
        </div>
        <button
          type="button"
          role="switch"
          aria-checked={audio.enabled}
          onClick={() => setAudio({ ...audio, enabled: !audio.enabled, bypass: false })}
          className={`h-6 w-11 rounded-full p-0.5 transition ${audio.enabled ? "bg-gradient-to-r from-accent to-accent2" : "bg-surface3"}`}
        >
          <span className={`block h-5 w-5 rounded-full bg-white shadow transition-transform ${audio.enabled ? "translate-x-5" : ""}`} />
        </button>
      </div>

      <div className={!audio.enabled ? "pointer-events-none opacity-45" : ""}>
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted">Audio Pro</p>
        <div className="flex flex-wrap gap-1.5">
          {MODES.map((mode) => (
            <Chip key={mode} active={audio.mode === mode} onClick={() => setAudio(applyAudioMode(mode, audio))}>
              {AUDIO_MODE_LABELS[mode]}
            </Chip>
          ))}
        </div>
        <p className="mt-2 text-[11px] text-muted">
          {audio.mode === "custom" ? "Conserva tus ajustes manuales." : "Un perfil configura toda la cadena con un toque."}
        </p>

        <div className="mt-3 inline-flex rounded-xl border border-line bg-surface2 p-1">
          <Chip active={audio.bypass} onClick={() => setAudio({ ...audio, bypass: true })}>Tono original</Chip>
          <Chip active={!audio.bypass} onClick={() => setAudio({ ...audio, bypass: false })}>Procesado</Chip>
        </div>

        <div className="mt-5 border-t border-line pt-4">
          <Toggle
            checked={audio.equalizerOn}
            onChange={(equalizerOn) => manual({ equalizerOn })}
            label="Ecualizador de 10 bandas"
            hint="De 31 Hz a 16 kHz, con un recorrido de ±12 dB."
          />
          <div className={`mt-3 ${!active || !audio.equalizerOn ? "pointer-events-none opacity-45" : ""}`}>
            <div className="flex gap-1.5 overflow-x-auto pb-2">
              {EQ_PRESETS.map((preset) => (
                <Chip
                  key={preset.id}
                  active={audio.preset === preset.id}
                  onClick={() => setAudio({ ...audio, preset: preset.id, bands: [...preset.gains], equalizerOn: true, mode: "custom" })}
                >
                  {preset.label}
                </Chip>
              ))}
            </div>
            <Equalizer settings={audio} onChange={setAudio} />
          </div>
        </div>

        <EffectSlider
          title="Boost"
          hint="Ganancia real por encima del volumen normal. Los niveles altos hacen trabajar más al limitador."
          on={audio.boostOn}
          value={audio.boostDb}
          max={MAX_AUDIO_BOOST_DB}
          readout={`+${audio.boostDb.toFixed(1)} dB`}
          enabled={active}
          onToggle={(on) => manual({ boostOn: on, boostDb: on && audio.boostDb === 0 ? 6 : audio.boostDb })}
          onChange={(boostDb) => manual({ boostDb, boostOn: boostDb > 0 })}
        />

        <SignalMeter />

        <EffectSlider
          title="Graves"
          hint="Realza el bajo sin alterar los medios donde vive la voz."
          on={audio.bassOn}
          value={audio.bass}
          max={1}
          readout={`${Math.round(audio.bass * 100)} %`}
          enabled={active}
          onToggle={(on) => manual({ bassOn: on, bass: on && audio.bass === 0 ? 0.4 : audio.bass })}
          onChange={(bass) => manual({ bass, bassOn: bass > 0 })}
        />

        <EffectSlider
          title="Claridad"
          hint="Realza la presencia entre 2 y 8 kHz para voces y detalles."
          on={audio.clarityOn}
          value={audio.clarity}
          max={1}
          readout={`${Math.round(audio.clarity * 100)} %`}
          enabled={active}
          onToggle={(on) => manual({ clarityOn: on, clarity: on && audio.clarity === 0 ? 0.35 : audio.clarity })}
          onChange={(clarity) => manual({ clarity, clarityOn: clarity > 0 })}
        />

        <div className="mt-5 rounded-xl border border-line bg-surface2 p-3">
          <Toggle
            checked={audio.peakProtection}
            onChange={(peakProtection) => manual({ peakProtection })}
            label="Protección de picos"
            hint="Limitador dinámico posterior al boost. Reduce clipping sin modificar los archivos."
          />
        </div>

      </div>
      <button
        type="button"
        onClick={() => setAudio(resetAudioSettings(audio))}
        className="mt-4 flex items-center gap-2 rounded-lg px-2 py-1.5 text-[12px] font-medium text-bad transition hover:bg-bad/10"
      >
        <RotateCcw size={14} /> Restablecer todo
      </button>
    </section>
  );
}

function Equalizer({ settings, onChange }: { settings: AudioSettings; onChange: (s: AudioSettings) => void }) {
  const shown = settings.preset ? [...presetById(settings.preset)!.gains] : settings.bands;
  const points = shown.map((gain, index) => `${5 + index * 10},${50 - (gain / 12) * 42}`).join(" ");
  const change = (index: number, value: number) => {
    const bands = [...shown];
    bands[index] = Math.min(EQ_MAX_DB, Math.max(EQ_MIN_DB, value));
    onChange({ ...settings, preset: null, bands, equalizerOn: true, mode: "custom" });
  };
  return (
    <div className="relative mt-2 rounded-xl border border-line bg-base/45 px-2 pb-2 pt-3">
      <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="pointer-events-none absolute inset-x-3 top-3 h-32 w-auto" aria-hidden="true">
        <line x1="0" y1="50" x2="100" y2="50" stroke="var(--rc-line)" strokeWidth="0.7" />
        <polyline points={points} fill="none" stroke="var(--rc-accent-2)" strokeWidth="1.1" vectorEffect="non-scaling-stroke" />
      </svg>
      <div className="relative grid h-40 grid-cols-10 gap-1">
        {shown.map((gain, index) => (
          <label key={EQ_FREQUENCIES[index]} className="flex min-w-0 flex-col items-center justify-end gap-1">
            <span className="text-[9px] tabular-nums text-muted">{gain > 0 ? "+" : ""}{gain.toFixed(1)}</span>
            <input
              type="range"
              min={EQ_MIN_DB}
              max={EQ_MAX_DB}
              step={0.5}
              value={gain}
              onChange={(e) => change(index, Number(e.target.value))}
              className="h-24 w-4 cursor-pointer accent-[var(--rc-accent)]"
              style={{ writingMode: "vertical-lr", direction: "rtl" }}
              aria-label={`${EQ_FREQUENCIES[index]} Hz`}
            />
            <span className="text-[9px] text-muted">{EQ_FREQUENCIES[index] >= 1000 ? `${EQ_FREQUENCIES[index] / 1000}k` : EQ_FREQUENCIES[index]}</span>
          </label>
        ))}
      </div>
    </div>
  );
}

function SignalMeter() {
  const meter = useAudioMeter();
  const meta = meter.risk === "safe"
    ? { label: "Margen seguro", color: "bg-ok", text: "text-ok" }
    : meter.risk === "caution"
      ? { label: "Precaución", color: "bg-warn", text: "text-warn" }
      : { label: "Riesgo alto", color: "bg-bad", text: "text-bad" };
  const width = Math.max(1, Math.min(100, ((meter.beforeDb + 60) / 60) * 100));
  return (
    <div className="mt-4 rounded-xl border border-line bg-surface2 p-3">
      <div className="flex items-center gap-2">
        <Shield size={14} className={meta.text} />
        <span className="flex-1 text-[11px] font-semibold uppercase tracking-wider">Diagnóstico de ganancia</span>
        <span className={`text-[10px] font-semibold uppercase tracking-wider ${meta.text}`}>{meter.active ? meta.label : "Esperando audio"}</span>
      </div>
      <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-line"><div className={`h-full transition-[width] ${meta.color}`} style={{ width: `${width}%` }} /></div>
      <p className="mt-2 text-[11px] text-muted">
        {meter.active
          ? `Antes ${meter.beforeDb.toFixed(1)} dBFS · salida ${meter.afterDb.toFixed(1)} dBFS · limitador ${meter.reductionDb.toFixed(1)} dB`
          : "Reproduce música o vídeo para medir la señal real."}
      </p>
    </div>
  );
}

function EffectSlider(props: { title: string; hint: string; on: boolean; value: number; max: number; readout: string; enabled: boolean; onToggle: (on: boolean) => void; onChange: (value: number) => void }) {
  return (
    <div className={`mt-5 border-t border-line pt-4 ${!props.enabled ? "opacity-45" : ""}`}>
      <div className="flex items-center gap-3">
        <div className="flex-1"><p className="text-[12px] font-semibold uppercase tracking-wider">{props.title}</p><p className="mt-0.5 text-[11px] text-muted">{props.hint}</p></div>
        <span className="text-[11px] tabular-nums text-accent2">{props.readout}</span>
        <button type="button" role="switch" aria-checked={props.on} disabled={!props.enabled} onClick={() => props.onToggle(!props.on)} className={`h-5 w-9 rounded-full p-0.5 ${props.on ? "bg-accent" : "bg-surface3"}`}><span className={`block h-4 w-4 rounded-full bg-white transition-transform ${props.on ? "translate-x-4" : ""}`} /></button>
      </div>
      <input type="range" min={0} max={props.max} step={props.max === 1 ? 0.01 : 0.5} value={props.value} disabled={!props.enabled || !props.on} onChange={(e) => props.onChange(Number(e.target.value))} className="mt-3 w-full accent-[var(--rc-accent)] disabled:cursor-not-allowed" />
    </div>
  );
}

function Chip({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return <button type="button" onClick={onClick} className={`shrink-0 rounded-lg border px-2.5 py-1 text-[11px] font-medium transition ${active ? "border-accent/50 bg-accent/20 text-accent2" : "border-line bg-surface2 text-muted hover:text-ink"}`}>{children}</button>;
}

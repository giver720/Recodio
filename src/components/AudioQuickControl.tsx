import { SlidersHorizontal } from "lucide-react";
import { useState } from "react";
import { AUDIO_MODE_LABELS, type AudioSettings } from "../lib/audioSettings";

export function AudioQuickControl({ audio, onChange, onOpenSettings, dark = false }: { audio: AudioSettings; onChange: (audio: AudioSettings) => void; onOpenSettings: () => void; dark?: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="relative">
      <button type="button" title="Audio Pro" onClick={() => setOpen((v) => !v)} className={`rounded-lg p-1.5 transition ${audio.enabled && !audio.bypass ? "text-accent2" : dark ? "text-white/70 hover:bg-white/15" : "text-muted hover:bg-surface3 hover:text-fg"}`}>
        <SlidersHorizontal size={15} />
      </button>
      {open && (
        <div className={`absolute bottom-full right-0 z-50 mb-2 w-64 rounded-xl border p-3 shadow-2xl ${dark ? "border-white/15 bg-black/95 text-white" : "border-line bg-surface2 text-ink"}`}>
          <div className="flex items-center justify-between"><p className="text-[12px] font-semibold">Audio Pro</p><span className="text-[10px] text-accent2">{AUDIO_MODE_LABELS[audio.mode]}</span></div>
          <button type="button" onClick={() => onChange({ ...audio, enabled: !audio.enabled, bypass: false })} className="mt-3 flex w-full items-center justify-between text-[11px]"><span>Procesamiento</span><span className={audio.enabled ? "text-ok" : "text-muted"}>{audio.enabled ? "ACTIVO" : "APAGADO"}</span></button>
          <button type="button" disabled={!audio.enabled} onClick={() => onChange({ ...audio, bypass: !audio.bypass })} className="mt-2 flex w-full items-center justify-between text-[11px] disabled:opacity-40"><span>Comparación A/B</span><span className={audio.bypass ? "text-warn" : "text-accent2"}>{audio.bypass ? "TONO ORIGINAL" : "PROCESADO"}</span></button>
          <p className="mt-2 text-[10px] text-muted">Boost {audio.boostOn ? `+${audio.boostDb.toFixed(1)} dB` : "apagado"}</p>
          <button type="button" onClick={() => { setOpen(false); onOpenSettings(); }} className="mt-3 w-full rounded-lg bg-accent/20 px-2 py-1.5 text-[11px] font-medium text-accent2 hover:bg-accent/30">Abrir ajustes completos</button>
        </div>
      )}
    </div>
  );
}

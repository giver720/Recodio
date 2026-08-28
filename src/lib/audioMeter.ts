import { create } from "zustand";
import type { AudioMeterSnapshot } from "./audioSettings";

export const SILENT_METER: AudioMeterSnapshot = {
  beforeDb: -60,
  afterDb: -60,
  reductionDb: 0,
  risk: "safe",
  active: false,
};

interface AudioMeterState extends AudioMeterSnapshot {
  report: (meter: AudioMeterSnapshot) => void;
  reset: () => void;
}

export const useAudioMeter = create<AudioMeterState>((set) => ({
  ...SILENT_METER,
  report: (meter) => set(meter),
  reset: () => set(SILENT_METER),
}));

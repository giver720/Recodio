import { AlertCircle, CheckCircle2, Info, X } from "lucide-react";
import { useStore } from "../lib/store";

const icons = {
  info: Info,
  success: CheckCircle2,
  error: AlertCircle,
} as const;

const tones = {
  info: "text-accent2",
  success: "text-ok",
  error: "text-bad",
} as const;

export function Toasts() {
  const toasts = useStore((s) => s.toasts);
  const dismiss = useStore((s) => s.dismissToast);

  return (
    <div className="pointer-events-none fixed bottom-5 right-5 z-50 flex w-80 flex-col gap-2">
      {toasts.map((t) => {
        const Icon = icons[t.kind];
        return (
          <div
            key={t.id}
            className="rc-fade-in rc-card pointer-events-auto flex items-start gap-2.5 p-3 text-[13px]"
            style={{ boxShadow: "0 12px 40px var(--rc-shadow)" }}
          >
            <Icon size={16} className={`mt-0.5 shrink-0 ${tones[t.kind]}`} />
            <p className="flex-1 leading-snug break-words">{t.text}</p>
            <button
              onClick={() => dismiss(t.id)}
              className="rc-ring shrink-0 rounded p-0.5 text-muted hover:text-ink"
              aria-label="Cerrar aviso"
            >
              <X size={14} />
            </button>
          </div>
        );
      })}
    </div>
  );
}

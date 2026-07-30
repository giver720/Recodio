import { Download, Library as LibraryIcon, ListVideo, Settings2 } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { Mark } from "./components/Mark";
import { ProgressBar } from "./components/ProgressBar";
import { Toasts } from "./components/Toasts";
import { useStore } from "./lib/store";
import { Downloader } from "./views/Downloader";
import { Library } from "./views/Library";
import { QueueView } from "./views/QueueView";
import { SettingsView } from "./views/SettingsView";

type Tab = "download" | "queue" | "library" | "settings";

const tabs: { id: Tab; label: string; icon: ReactNode }[] = [
  { id: "download", label: "Descargar", icon: <Download size={17} /> },
  { id: "queue", label: "Cola", icon: <ListVideo size={17} /> },
  { id: "library", label: "Biblioteca", icon: <LibraryIcon size={17} /> },
  { id: "settings", label: "Ajustes", icon: <Settings2 size={17} /> },
];

export default function App() {
  const ready = useStore((s) => s.ready);
  const init = useStore((s) => s.init);
  const theme = useStore((s) => s.settings?.theme);
  const stats = useStore((s) => s.stats);
  const [tab, setTab] = useState<Tab>("download");

  useEffect(() => {
    init();
  }, [init]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme ?? "dark";
  }, [theme]);

  const busy = stats.running + stats.queued;

  if (!ready) {
    return (
      <div className="flex h-full items-center justify-center bg-base">
        <Logo className="animate-pulse" />
      </div>
    );
  }

  return (
    <div className="relative flex h-full overflow-hidden bg-base text-ink">
      <div className="rc-aurora pointer-events-none absolute inset-0" />

      {/* ---- Navegación ---- */}
      <nav className="relative z-10 flex w-[190px] shrink-0 flex-col border-r border-line bg-surface/60 backdrop-blur-xl">
        <div className="px-4 pb-3 pt-5">
          <Logo />
        </div>

        <div className="flex flex-col gap-1 px-2.5">
          {tabs.map((t) => (
            <button
              key={t.id}
              onClick={() => setTab(t.id)}
              className={`rc-ring relative flex items-center gap-2.5 rounded-xl px-3 py-2.5 text-[13.5px] font-medium transition
                ${
                  tab === t.id
                    ? "bg-surface3 text-ink"
                    : "text-muted hover:bg-surface2 hover:text-ink"
                }`}
            >
              {tab === t.id && (
                <span className="absolute left-0 top-1/2 h-5 w-[3px] -translate-y-1/2 rounded-r bg-gradient-to-b from-accent to-accent2" />
              )}
              <span className={tab === t.id ? "text-accent2" : ""}>{t.icon}</span>
              {t.label}
              {t.id === "queue" && busy > 0 && (
                <span className="ml-auto rounded-full bg-accent/20 px-1.5 py-0.5 text-[11px] tabular-nums text-accent2">
                  {busy}
                </span>
              )}
            </button>
          ))}
        </div>

        {/* ---- Mini estado de la cola ---- */}
        {busy > 0 && (
          <button
            onClick={() => setTab("queue")}
            className="rc-ring m-2.5 mt-auto rounded-xl border border-line bg-surface2/70 p-3 text-left transition hover:border-accent/40"
          >
            <div className="flex items-baseline justify-between">
              <span className="text-[12px] text-muted">
                {stats.paused ? "En pausa" : "Descargando"}
              </span>
              <span className="text-[12px] font-semibold tabular-nums">
                {Math.round(stats.overall * 100)}%
              </span>
            </div>
            <ProgressBar
              value={stats.overall}
              status={stats.paused ? "queued" : "running"}
              height={6}
              className="mt-2"
            />
          </button>
        )}
      </nav>

      {/* ---- Contenido ---- */}
      <main className="relative z-10 min-w-0 flex-1 overflow-y-auto">
        <div key={tab} className="rc-fade-in h-full">
          {tab === "download" && <Downloader onQueued={() => setTab("queue")} />}
          {tab === "queue" && <QueueView />}
          {tab === "library" && <Library />}
          {tab === "settings" && <SettingsView />}
        </div>
      </main>

      <Toasts />
    </div>
  );
}

function Logo({ className = "" }: { className?: string }) {
  return (
    <div className={`flex items-center gap-2.5 ${className}`}>
      <span className="flex h-8 w-8 items-center justify-center rounded-[10px] bg-gradient-to-br from-accent via-accent3 to-accent2 shadow-lg shadow-accent/30">
        <Mark size={19} className="text-white" />
      </span>
      <span className="text-[17px] font-semibold tracking-tight">Recodio</span>
    </div>
  );
}

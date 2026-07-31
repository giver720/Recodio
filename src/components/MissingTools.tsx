import { listen } from "@tauri-apps/api/event";
import { AlertTriangle, Check, Copy, Download, Loader2 } from "lucide-react";
import { useEffect, useState } from "react";
import { api } from "../lib/api";
import { useStore } from "../lib/store";
import type { Platform, ToolStatus } from "../lib/types";
import { ProgressBar } from "./ProgressBar";
import { Button } from "./ui";

/** Comando manual, para quien prefiera instalarlas del sistema. */
const COMANDOS: Record<Platform, Record<string, string>> = {
  linux: {
    "yt-dlp": "sudo apt install yt-dlp",
    ffmpeg: "sudo apt install ffmpeg",
    spotdl: "pipx install spotdl",
  },
  windows: {
    "yt-dlp": "winget install yt-dlp.yt-dlp",
    ffmpeg: "winget install Gyan.FFmpeg",
    spotdl: "pip install spotdl",
  },
  macos: {
    "yt-dlp": "brew install yt-dlp",
    ffmpeg: "brew install ffmpeg",
    spotdl: "pipx install spotdl",
  },
};

const PARA_QUE: Record<string, string> = {
  "yt-dlp": "Sin esto no se puede descargar nada. Es lo único imprescindible.",
  ffmpeg: "Une vídeo y audio, convierte a MP3 y recorta SponsorBlock.",
  spotdl:
    "Opcional: solo se usa como respaldo si Spotify cambia su página y falla la lectura rápida de las listas.",
};

/** Lo que ocupa cada descarga, para que nadie se lleve una sorpresa. */
const PESO: Record<string, string> = {
  "yt-dlp": "unos 30 MB",
  ffmpeg: "unos 30 MB en Linux, 160 MB en Windows",
  spotdl: "unos 70 MB",
};

/**
 * Aviso de herramientas ausentes, con instalación incluida.
 *
 * Recodio se las descarga a su propia carpeta: no toca el sistema, no pide
 * contraseña y no interfiere con lo que ya tengas instalado.
 */
export function MissingTools({ tools }: { tools: ToolStatus[] }) {
  const platform = useStore((s) => s.platform);
  const refreshTools = useStore((s) => s.refreshTools);
  const toast = useStore((s) => s.toast);
  const [progress, setProgress] = useState<Record<string, number>>({});
  const [copied, setCopied] = useState<string | null>(null);

  useEffect(() => {
    const un = listen<[string, number]>("tool-install-progress", (e) => {
      const [name, fraction] = e.payload;
      setProgress((p) => ({ ...p, [name]: fraction }));
    });
    return () => {
      un.then((f) => f());
    };
  }, []);

  // spotDL dejó de hacer falta para descargar: la música de Spotify se baja con
  // yt-dlp. Avisar de que «falta» sería alarmar por nada.
  const missing = tools.filter((t) => !t.found && t.name !== "spotdl");
  if (missing.length === 0) return null;

  const blocking = missing.some((t) => t.name === "yt-dlp");
  const anyInstalling = Object.keys(progress).length > 0;

  async function copy(cmd: string) {
    await navigator.clipboard.writeText(cmd);
    setCopied(cmd);
    setTimeout(() => setCopied(null), 1500);
  }

  async function install(name: string) {
    setProgress((p) => ({ ...p, [name]: -1 }));
    try {
      await api.toolsInstall(name);
      await refreshTools();
      toast("success", `${name} instalado`);
    } catch (e) {
      toast("error", `No se pudo instalar ${name}: ${e}`);
    } finally {
      setProgress((p) => {
        const { [name]: _, ...resto } = p;
        return resto;
      });
    }
  }

  async function installAll() {
    for (const t of missing) await install(t.name);
  }

  return (
    <div
      className={`rc-card flex flex-col gap-3 border p-4 ${
        blocking ? "border-danger/40" : "border-warn/30"
      }`}
    >
      <div className="flex items-start gap-2.5">
        <AlertTriangle
          size={17}
          className={`mt-0.5 shrink-0 ${blocking ? "text-danger" : "text-warn"}`}
        />
        <div className="min-w-0 flex-1">
          <p className="text-[13.5px] font-medium">
            {blocking
              ? "Falta yt-dlp: las descargas no funcionarán"
              : "Faltan herramientas opcionales"}
          </p>
          <p className="text-[12px] leading-snug text-muted">
            Recodio las descarga en su propia carpeta. No toca el sistema ni pide
            contraseña.
          </p>
        </div>
        {missing.length > 1 && (
          <Button variant="primary" onClick={installAll} disabled={anyInstalling}>
            {anyInstalling ? (
              <Loader2 size={13} className="animate-spin" />
            ) : (
              <Download size={13} />
            )}
            Instalar todo
          </Button>
        )}
      </div>

      <div className="flex flex-col gap-2">
        {missing.map((t) => {
          const installing = t.name in progress;
          return (
            <div key={t.name} className="rounded-xl border border-line bg-surface2 p-2.5">
              <div className="flex items-center justify-between gap-2">
                <div className="min-w-0">
                  <span className="text-[12.5px] font-medium">{t.name}</span>
                  <span className="ml-1.5 text-[11px] text-muted">{PESO[t.name]}</span>
                </div>
                <Button onClick={() => install(t.name)} disabled={anyInstalling}>
                  {installing ? (
                    <Loader2 size={13} className="animate-spin" />
                  ) : (
                    <Download size={13} />
                  )}
                  Instalar
                </Button>
              </div>

              <p className="mt-0.5 text-[11.5px] leading-snug text-muted">
                {PARA_QUE[t.name]}
              </p>

              {installing ? (
                <ProgressBar
                  value={progress[t.name]}
                  status="running"
                  phase="downloading"
                  className="mt-2"
                />
              ) : (
                <button
                  type="button"
                  onClick={() => copy(COMANDOS[platform][t.name])}
                  title="O instálala tú, con este comando"
                  className="mt-1.5 flex w-full items-center gap-2 rounded-lg bg-surface3 px-2 py-1.5 text-left font-mono text-[11.5px] text-fg/90 transition hover:bg-surface3/70"
                >
                  <span className="min-w-0 flex-1 truncate">
                    {COMANDOS[platform][t.name]}
                  </span>
                  {copied === COMANDOS[platform][t.name] ? (
                    <Check size={13} className="shrink-0 text-ok" />
                  ) : (
                    <Copy size={13} className="shrink-0 text-muted" />
                  )}
                </button>
              )}
            </div>
          );
        })}
      </div>

      <Button onClick={refreshTools} disabled={anyInstalling}>
        Volver a comprobar
      </Button>
    </div>
  );
}

import { ArrowUpCircle, CheckCircle2, Download, RefreshCw, RotateCw } from "lucide-react";
import { useState } from "react";
import { checkForUpdate, installUpdate, restartApp, type UpdateState } from "../lib/updater";
import { useStore } from "../lib/store";
import { ProgressBar } from "./ProgressBar";
import { Button } from "./ui";

export function UpdateCard({ version }: { version: string }) {
  const [state, setState] = useState<UpdateState>({ status: "idle" });
  const platform = useStore((s) => s.platform);

  const busy = state.status === "checking" || state.status === "downloading";

  async function check() {
    setState({ status: "checking" });
    setState(await checkForUpdate());
  }

  async function install() {
    setState({ status: "downloading", progress: -1 });
    const result = await installUpdate((progress) =>
      setState({ status: "downloading", progress }),
    );
    setState(result);
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-3">
        <div className="min-w-0 flex-1">
          <p className="text-[13px] font-medium">Recodio {version}</p>
          <p className="text-[11.5px] text-muted">
            {state.status === "checking" && "Comprobando…"}
            {state.status === "none" && "Estás en la última versión"}
            {state.status === "available" && `Hay una versión nueva: ${state.version}`}
            {state.status === "downloading" && "Descargando la actualización…"}
            {state.status === "ready" && "Listo. Se aplicará al reiniciar."}
            {state.status === "error" && state.message}
            {state.status === "idle" && "Comprueba si hay una versión nueva"}
          </p>
        </div>

        {state.status === "available" ? (
          <Button variant="primary" onClick={install}>
            <Download size={14} /> Actualizar
          </Button>
        ) : state.status === "ready" ? (
          <Button variant="primary" onClick={restartApp}>
            <RotateCw size={14} /> Reiniciar
          </Button>
        ) : (
          <Button onClick={check} disabled={busy}>
            <RefreshCw size={14} className={busy ? "animate-spin" : ""} /> Buscar
          </Button>
        )}
      </div>

      {state.status === "downloading" && (
        <ProgressBar value={state.progress} status="running" phase="downloading" />
      )}

      {state.status === "available" && state.notes && (
        <p className="max-h-32 overflow-y-auto whitespace-pre-line rounded-xl border border-line bg-surface2 p-2.5 text-[12px] leading-snug text-muted">
          {state.notes}
        </p>
      )}

      {state.status === "none" && (
        <p className="flex items-center gap-1.5 text-[11.5px] text-ok">
          <CheckCircle2 size={13} /> Nada que hacer
        </p>
      )}

      {platform === "linux" && (
        <p className="flex items-start gap-1.5 text-[11.5px] leading-snug text-muted">
          <ArrowUpCircle size={13} className="mt-0.5 shrink-0" />
          El actualizador solo puede reemplazar el AppImage. Si instalaste el
          <code className="mx-1 rounded bg-surface3 px-1">.deb</code>
          actualiza con tu gestor de paquetes.
        </p>
      )}
    </div>
  );
}

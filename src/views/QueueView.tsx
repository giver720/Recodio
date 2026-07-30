import {
  Ban,
  Copy,
  Eraser,
  FolderOpen,
  ListX,
  Pause,
  Play,
  PlayCircle,
  RotateCcw,
} from "lucide-react";
import { ProgressBar } from "../components/ProgressBar";
import { Thumb } from "../components/Thumb";
import { useMenu } from "../components/Menu";
import { Button, EmptyState, IconButton } from "../components/ui";
import { api } from "../lib/api";
import { bytes, duration, eta, speed } from "../lib/format";
import { useStore } from "../lib/store";
import type { Job } from "../lib/types";

const statusLabel: Record<Job["status"], string> = {
  queued: "En cola",
  running: "Descargando",
  done: "Completado",
  skipped: "Omitido",
  failed: "Error",
  canceled: "Cancelado",
};

const statusTone: Record<Job["status"], string> = {
  queued: "text-muted bg-surface3",
  running: "text-accent2 bg-accent2/12",
  done: "text-ok bg-ok/12",
  skipped: "text-warn bg-warn/12",
  failed: "text-bad bg-bad/12",
  canceled: "text-muted bg-surface3",
};

export function QueueView() {
  const jobs = useStore((s) => s.jobs);
  const stats = useStore((s) => s.stats);
  const toast = useStore((s) => s.toast);
  const { openMenu, menu } = useMenu();

  const active = jobs.filter((j) => j.status === "running" || j.status === "queued");
  const finished = jobs.filter((j) => !active.includes(j));

  if (jobs.length === 0) {
    return (
      <EmptyState
        icon={<PlayCircle size={22} />}
        title="La cola está vacía"
        body="Analiza un enlace en la pestaña Descargar y lo verás aquí con su progreso en vivo."
      />
    );
  }

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-4 px-6 py-6">
      {/* ---- Resumen ---- */}
      <div className="rc-card p-4">
        <div className="flex items-center gap-3">
          <div className="min-w-0 flex-1">
            <div className="flex items-baseline gap-2">
              <span className="text-[22px] font-semibold tabular-nums">
                {Math.round(stats.overall * 100)}%
              </span>
              <span className="text-[13px] text-muted">
                {stats.running} activas · {stats.queued} en cola · {stats.done} listas
                {stats.failed > 0 && ` · ${stats.failed} con error`}
                {stats.skipped > 0 && ` · ${stats.skipped} omitidas`}
              </span>
            </div>
            <ProgressBar
              value={stats.overall}
              status={stats.failed > 0 && stats.running === 0 ? "failed" : "running"}
              phase="downloading"
              height={10}
              className="mt-2.5"
            />
          </div>

          <Button onClick={() => api.queueSetPaused(!stats.paused)}>
            {stats.paused ? <Play size={14} /> : <Pause size={14} />}
            {stats.paused ? "Reanudar" : "Pausar"}
          </Button>
          <IconButton title="Cancelar todo" onClick={() => api.queueCancelAll()}>
            <ListX size={16} />
          </IconButton>
          <IconButton title="Limpiar terminadas" onClick={() => api.queueClearFinished()}>
            <Eraser size={16} />
          </IconButton>
        </div>
      </div>

      {[...active, ...finished].map((job) => (
        <div
          key={job.id}
          className="rc-card flex items-center gap-3 p-3"
          onContextMenu={(e) =>
            openMenu(e, [
              ...(job.filePath
                ? [
                    {
                      label: "Reproducir",
                      icon: <Play size={14} />,
                      onClick: () =>
                        api.playFile(job.filePath!).catch((err) => toast("error", String(err))),
                    },
                    {
                      label: "Mostrar en la carpeta",
                      icon: <FolderOpen size={14} />,
                      onClick: () => api.revealFile(job.filePath!),
                    },
                  ]
                : []),
              ...(job.status === "running" || job.status === "queued"
                ? [
                    {
                      label: "Cancelar",
                      icon: <Ban size={14} />,
                      danger: true,
                      onClick: () => api.queueCancel(job.id),
                    },
                  ]
                : [
                    {
                      label: "Reintentar (forzando descarga)",
                      icon: <RotateCcw size={14} />,
                      onClick: () => api.queueRetry(job.id),
                    },
                  ]),
              ...(job.error
                ? [
                    { separator: true, label: "" },
                    {
                      label: "Copiar el error",
                      icon: <Copy size={14} />,
                      onClick: () => navigator.clipboard.writeText(job.error!),
                    },
                  ]
                : []),
            ])
          }
        >
          <Thumb
            src={job.entry.thumbnail}
            kind={job.kind}
            className="h-12 w-20"
            badge={duration(job.entry.duration)}
          />

          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <p className="min-w-0 flex-1 truncate text-[13.5px] font-medium">
                {job.entry.title}
              </p>
              <span
                className={`shrink-0 rounded-md px-2 py-0.5 text-[11px] font-medium ${statusTone[job.status]}`}
              >
                {statusLabel[job.status]}
              </span>
            </div>

            <ProgressBar
              value={job.progress}
              status={job.status}
              phase={job.phase}
              className="mt-2"
            />

            <div className="mt-1.5 flex items-center gap-2 text-[11.5px] text-muted">
              <span className="truncate">
                {job.error
                  ? job.error
                  : job.status === "running"
                    ? (job.message ?? "Preparando…")
                    : (job.message ?? job.playlistTitle ?? job.entry.uploader ?? "")}
              </span>
              <span className="ml-auto shrink-0 tabular-nums">
                {job.status === "running" && job.totalBytes > 0 && (
                  <>
                    {bytes(job.downloadedBytes)} / {bytes(job.totalBytes)}
                  </>
                )}
              </span>
              {job.speed > 0 && (
                <span className="shrink-0 tabular-nums text-accent2">{speed(job.speed)}</span>
              )}
              {job.eta != null && job.status === "running" && (
                <span className="shrink-0 tabular-nums">{eta(job.eta)}</span>
              )}
            </div>
          </div>

          <div className="flex shrink-0 gap-1">
            {job.status === "running" || job.status === "queued" ? (
              <IconButton title="Cancelar" onClick={() => api.queueCancel(job.id)}>
                <Ban size={15} />
              </IconButton>
            ) : (
              <IconButton title="Reintentar" onClick={() => api.queueRetry(job.id)}>
                <RotateCcw size={15} />
              </IconButton>
            )}
            {job.filePath && (
              <IconButton
                title="Reproducir"
                onClick={() =>
                  api.playFile(job.filePath!).catch((err) => toast("error", String(err)))
                }
              >
                <Play size={15} />
              </IconButton>
            )}
          </div>
        </div>
      ))}

      {menu}
    </div>
  );
}

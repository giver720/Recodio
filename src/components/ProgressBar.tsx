import type { JobPhase, JobStatus } from "../lib/types";

interface Props {
  /** 0–1, or a negative number when the total size is unknown. */
  value: number;
  status?: JobStatus;
  phase?: JobPhase;
  className?: string;
  height?: number;
}

export function ProgressBar({
  value,
  status = "running",
  phase = "downloading",
  className = "",
  height = 8,
}: Props) {
  const indeterminate =
    (status === "running" && value < 0) || phase === "waiting";

  const tone =
    status === "failed" || status === "canceled"
      ? "is-error"
      : status === "done" || status === "skipped"
        ? "is-done"
        : phase === "processing"
          ? "is-processing"
          : "";

  const pct =
    status === "done" || status === "skipped"
      ? 100
      : Math.max(0, Math.min(1, value)) * 100;

  return (
    <div
      className={`rc-bar ${indeterminate ? "rc-bar-indeterminate" : ""} ${className}`}
      style={{ height }}
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={indeterminate ? undefined : Math.round(pct)}
    >
      {!indeterminate && (
        <div className={`rc-bar-fill ${tone}`} style={{ width: `${pct}%` }} />
      )}
    </div>
  );
}

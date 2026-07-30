import type { ButtonHTMLAttributes, ReactNode } from "react";

type Variant = "primary" | "ghost" | "soft" | "danger";

const variants: Record<Variant, string> = {
  primary:
    "text-white shadow-lg shadow-accent/25 bg-gradient-to-r from-accent via-accent3 to-accent2 hover:brightness-110",
  soft: "bg-surface2 text-ink border border-line hover:bg-surface3",
  ghost: "text-muted hover:text-ink hover:bg-surface2",
  danger: "bg-bad/15 text-bad border border-bad/30 hover:bg-bad/25",
};

export function Button({
  variant = "soft",
  className = "",
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  return (
    <button
      {...rest}
      className={`rc-ring inline-flex items-center justify-center gap-2 rounded-xl px-3.5 py-2 text-[13px] font-medium
        transition disabled:cursor-not-allowed disabled:opacity-45 ${variants[variant]} ${className}`}
    >
      {children}
    </button>
  );
}

export function IconButton({
  className = "",
  title,
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      {...rest}
      title={title}
      aria-label={title}
      className={`rc-ring inline-flex h-8 w-8 items-center justify-center rounded-lg text-muted
        transition hover:bg-surface3 hover:text-ink disabled:opacity-40 ${className}`}
    >
      {children}
    </button>
  );
}

export function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-[12px] font-medium text-muted">{label}</span>
      {children}
      {hint && <span className="text-[11px] leading-snug text-muted/75">{hint}</span>}
    </label>
  );
}

export const inputClass =
  "rc-ring w-full rounded-xl border border-line bg-surface2 px-3 py-2 text-[13px] text-ink placeholder:text-muted/60 outline-none transition focus:border-accent/60";

export function Select({
  value,
  onChange,
  options,
}: {
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string }[];
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className={`${inputClass} cursor-pointer appearance-none pr-8`}
      style={{
        backgroundImage:
          "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' fill='none' stroke='%238e97b2' stroke-width='2'><path d='M4 6l4 4 4-4'/></svg>\")",
        backgroundRepeat: "no-repeat",
        backgroundPosition: "right 10px center",
      }}
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

export function Toggle({
  checked,
  onChange,
  label,
  hint,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  hint?: string;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className="rc-ring flex w-full items-start gap-3 rounded-xl px-1 py-1.5 text-left"
    >
      <span
        className={`mt-0.5 flex h-5 w-9 shrink-0 items-center rounded-full p-0.5 transition
          ${checked ? "bg-gradient-to-r from-accent to-accent2" : "bg-surface3"}`}
      >
        <span
          className={`h-4 w-4 rounded-full bg-white shadow transition-transform ${checked ? "translate-x-4" : ""}`}
        />
      </span>
      <span className="flex-1">
        <span className="block text-[13px] font-medium">{label}</span>
        {hint && <span className="block text-[11px] leading-snug text-muted">{hint}</span>}
      </span>
    </button>
  );
}

export function SegmentedControl<T extends string>({
  value,
  onChange,
  options,
}: {
  value: T;
  onChange: (v: T) => void;
  options: { value: T; label: string; icon?: ReactNode }[];
}) {
  return (
    <div className="inline-flex rounded-xl border border-line bg-surface2 p-1">
      {options.map((o) => (
        <button
          key={o.value}
          onClick={() => onChange(o.value)}
          className={`rc-ring flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-[13px] font-medium transition
            ${
              value === o.value
                ? "bg-gradient-to-r from-accent to-accent2 text-white shadow"
                : "text-muted hover:text-ink"
            }`}
        >
          {o.icon}
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function EmptyState({
  icon,
  title,
  body,
  action,
}: {
  icon: ReactNode;
  title: string;
  body?: string;
  action?: ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 py-20 text-center">
      <div className="flex h-14 w-14 items-center justify-center rounded-2xl border border-line bg-surface2 text-muted">
        {icon}
      </div>
      <div>
        <p className="text-[15px] font-semibold">{title}</p>
        {body && <p className="mt-1 max-w-sm text-[13px] text-muted">{body}</p>}
      </div>
      {action}
    </div>
  );
}

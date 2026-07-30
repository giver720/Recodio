import { Music, Video } from "lucide-react";
import { useState } from "react";

interface Props {
  src?: string | null;
  alt?: string;
  kind?: "video" | "audio";
  className?: string;
  /** Duration badge, already formatted. */
  badge?: string;
}

export function Thumb({ src, alt = "", kind = "video", className = "", badge }: Props) {
  const [failed, setFailed] = useState(false);
  const Icon = kind === "audio" ? Music : Video;

  return (
    <div
      className={`relative shrink-0 overflow-hidden rounded-lg bg-surface2 ${className}`}
    >
      {src && !failed ? (
        <img
          src={src}
          alt={alt}
          loading="lazy"
          onError={() => setFailed(true)}
          className="h-full w-full object-cover"
        />
      ) : (
        <div className="flex h-full w-full items-center justify-center text-muted">
          <Icon size={18} />
        </div>
      )}
      {badge && (
        <span className="absolute bottom-1 right-1 rounded bg-black/75 px-1 text-[10px] font-medium tabular-nums text-white">
          {badge}
        </span>
      )}
    </div>
  );
}

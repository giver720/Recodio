import { Check } from "lucide-react";
import { useMemo } from "react";
import type { Entry } from "../lib/types";
import { Select } from "./ui";

/** Tamaños de bloque. Por debajo de 30 hay demasiados botones; por encima de 90
 *  el bloque deja de ser una tanda manejable. */
const TAMANOS = [30, 50, 90];

/** Listas más cortas que esto se manejan bien de una sola vez. */
export const MIN_PARA_BLOQUES = 60;

export interface Block {
  index: number;
  from: number;
  to: number;
  ids: string[];
  /** Entradas del bloque que ya están descargadas. */
  done: number;
  /** Entradas del bloque marcadas ahora mismo. */
  selected: number;
  /** Descargables: ni no disponibles ni ya presentes. */
  available: number;
}

export function buildBlocks(
  entries: Entry[],
  size: number,
  selected: Set<string>,
  isDone: (e: Entry) => boolean,
): Block[] {
  const blocks: Block[] = [];
  for (let i = 0; i < entries.length; i += size) {
    const trozo = entries.slice(i, i + size);
    blocks.push({
      index: blocks.length + 1,
      from: i + 1,
      to: i + trozo.length,
      ids: trozo.map((e) => e.id),
      done: trozo.filter(isDone).length,
      selected: trozo.filter((e) => selected.has(e.id)).length,
      available: trozo.filter((e) => !e.unavailable && !isDone(e)).length,
    });
  }
  return blocks;
}

/**
 * Selector por tandas para listas largas.
 *
 * Marcar quinientas canciones de una en una no es viable, y marcarlas todas de
 * golpe tampoco siempre interesa: así se puede bajar la playlist por partes,
 * saber cuál va por dónde y retomarla otro día.
 */
export function BlockPicker({
  entries,
  size,
  onSizeChange,
  selected,
  isDone,
  onToggleBlock,
}: {
  entries: Entry[];
  size: number;
  onSizeChange: (n: number) => void;
  selected: Set<string>;
  isDone: (e: Entry) => boolean;
  onToggleBlock: (ids: string[], on: boolean) => void;
}) {
  const blocks = useMemo(
    () => buildBlocks(entries, size, selected, isDone),
    [entries, size, selected, isDone],
  );

  if (entries.length < MIN_PARA_BLOQUES) return null;

  return (
    <div className="flex flex-col gap-2 border-t border-line bg-surface2/40 px-4 py-3">
      <div className="flex items-center gap-2">
        <span className="text-[12px] font-medium">
          {entries.length} canciones en {blocks.length} tandas
        </span>
        <span className="text-[11.5px] text-muted">
          Pulsa una tanda para marcarla entera
        </span>
        <div className="ml-auto w-28">
          <Select
            value={String(size)}
            onChange={(v) => onSizeChange(Number(v))}
            options={TAMANOS.map((n) => ({ value: String(n), label: `De ${n}` }))}
          />
        </div>
      </div>

      <div className="flex flex-wrap gap-1.5">
        {blocks.map((b) => {
          // Completa = todo lo descargable ya está marcado.
          const full = b.available > 0 && b.selected >= b.available;
          const partial = b.selected > 0 && !full;
          const allDone = b.available === 0;

          return (
            <button
              key={b.index}
              type="button"
              title={
                allDone
                  ? `Canciones ${b.from}–${b.to}: ya las tienes todas`
                  : `Canciones ${b.from}–${b.to} · ${b.available} por descargar${
                      b.done > 0 ? ` · ${b.done} ya en tu biblioteca` : ""
                    }`
              }
              onClick={() => onToggleBlock(b.ids, !full)}
              className={`flex min-w-[3.25rem] items-center justify-center gap-1 rounded-xl border px-2.5 py-1.5 text-[12px] tabular-nums transition ${
                allDone
                  ? "border-line bg-surface3/50 text-muted"
                  : full
                    ? "border-accent/50 bg-accent/20 text-accent"
                    : partial
                      ? "border-accent/30 bg-accent/10 text-fg"
                      : "border-line bg-surface3 text-muted hover:text-fg"
              }`}
            >
              {allDone ? <Check size={12} /> : null}
              {b.index}
            </button>
          );
        })}
      </div>

      <p className="text-[11px] leading-snug text-muted">
        Las tandas en gris ya están completas en tu biblioteca. Puedes descargar
        unas ahora y el resto otro día: lo que ya tengas no se vuelve a bajar.
      </p>
    </div>
  );
}

import { useCallback, useEffect, useState, type ReactNode } from "react";

export interface MenuItem {
  label: string;
  icon?: ReactNode;
  onClick?: () => void;
  danger?: boolean;
  disabled?: boolean;
  /** Renders a divider instead of a row. */
  separator?: boolean;
}

interface MenuState {
  x: number;
  y: number;
  items: MenuItem[];
}

/**
 * Right-click menus that stay inside the window. Returns the opener and the
 * element to drop somewhere in the tree.
 */
export function useMenu() {
  const [state, setState] = useState<MenuState | null>(null);

  const openMenu = useCallback(
    (e: { preventDefault(): void; clientX: number; clientY: number }, items: MenuItem[]) => {
      e.preventDefault();
      setState({ x: e.clientX, y: e.clientY, items });
    },
    [],
  );

  const close = useCallback(() => setState(null), []);

  useEffect(() => {
    if (!state) return;
    const onDown = () => close();
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && close();
    window.addEventListener("mousedown", onDown);
    window.addEventListener("resize", close);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("mousedown", onDown);
      window.removeEventListener("resize", close);
      window.removeEventListener("keydown", onKey);
    };
  }, [state, close]);

  const width = 236;
  const height = state ? state.items.length * 34 + 12 : 0;
  const x = state ? Math.min(state.x, window.innerWidth - width - 8) : 0;
  const y = state ? Math.min(state.y, window.innerHeight - height - 8) : 0;

  const menu = state ? (
    <div
      className="rc-fade-in fixed z-50 rounded-xl border border-line bg-surface p-1.5 shadow-2xl"
      style={{ left: x, top: y, width, boxShadow: "0 18px 50px var(--rc-shadow)" }}
      onMouseDown={(e) => e.stopPropagation()}
    >
      {state.items.map((item, i) =>
        item.separator ? (
          <div key={i} className="my-1 h-px bg-line" />
        ) : (
          <button
            key={i}
            disabled={item.disabled}
            onClick={() => {
              close();
              item.onClick?.();
            }}
            className={`flex w-full items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-left text-[13px] transition
              disabled:cursor-not-allowed disabled:opacity-40
              ${item.danger ? "text-bad hover:bg-bad/12" : "hover:bg-surface3"}`}
          >
            {item.icon && <span className="opacity-80">{item.icon}</span>}
            <span className="truncate">{item.label}</span>
          </button>
        ),
      )}
    </div>
  ) : null;

  return { openMenu, menu, closeMenu: close };
}

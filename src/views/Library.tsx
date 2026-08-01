import {
  AlertTriangle,
  Copy,
  ExternalLink,
  FileVideo,
  FolderOpen,
  FolderPlus,
  Library as LibraryIcon,
  ListMusic,
  Music,
  Play,
  RefreshCw,
  Search,
  Trash2,
} from "lucide-react";
import { listen } from "@tauri-apps/api/event";
import { open as openDialog } from "@tauri-apps/plugin-dialog";
import { useEffect, useMemo, useState, type MouseEvent, type ReactNode } from "react";
import { Thumb } from "../components/Thumb";
import { useMenu } from "../components/Menu";
import { EmptyState, IconButton, inputClass } from "../components/ui";
import { api } from "../lib/api";
import { bytes, duration, relativeDate } from "../lib/format";
import { usePlayer } from "../lib/player";
import { useStore } from "../lib/store";
import type { LibraryItem, Playlist, RepairReport } from "../lib/types";

type Bucket = { id: string; label: string; icon: ReactNode; count?: number };

export function Library() {
  const libraryVersion = useStore((s) => s.libraryVersion);
  const toast = useStore((s) => s.toast);
  const { openMenu, menu } = useMenu();
  const play = usePlayer((s) => s.play);

  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [items, setItems] = useState<LibraryItem[]>([]);
  const [bucket, setBucket] = useState("all");
  const [search, setSearch] = useState("");
  const [loading, setLoading] = useState(true);
  const [health, setHealth] = useState<RepairReport | null>(null);
  const [fixing, setFixing] = useState(false);
  const [importing, setImporting] = useState<{ done: number; total: number } | null>(null);

  useEffect(() => {
    const un = listen<[number, number]>("import-progress", (e) => {
      const [done, total] = e.payload;
      setImporting({ done, total });
    });
    return () => {
      un.then((f) => f());
    };
  }, []);

  async function importFolder() {
    const picked = await openDialog({ directory: true });
    if (typeof picked !== "string") return;
    setImporting({ done: 0, total: 0 });
    try {
      const r = await api.libraryImportFolder(picked);
      toast(
        "success",
        r.added > 0
          ? `${r.title}: ${r.added} ${r.added === 1 ? "archivo añadido" : "archivos añadidos"}` +
              (r.skipped > 0 ? `, ${r.skipped} ya estaban` : "")
          : r.found === 0
            ? `No se encontró música ni vídeo en ${r.title}`
            : `${r.title} ya estaba al día: nada nuevo que añadir`,
      );
      useStore.setState((s) => ({ libraryVersion: s.libraryVersion + 1 }));
    } catch (e) {
      toast("error", String(e));
    } finally {
      setImporting(null);
    }
  }


  const playlistId = playlists.find((p) => p.id === bucket)?.id ?? null;

  async function refresh() {
    setLoading(true);
    try {
      const [pls, its] = await Promise.all([
        api.libraryPlaylists(),
        api.libraryItems(playlistId, search),
      ]);
      setPlaylists(pls);
      setItems(its);
    } catch (e) {
      toast("error", String(e));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bucket, libraryVersion]);

  useEffect(() => {
    const t = setTimeout(refresh, 220);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search]);

  useEffect(() => {
    // Se comprueba al abrir la pestaña: una herramienta escondida en Ajustes no
    // la encuentra quien tiene el problema.
    api.libraryHealth().then(setHealth).catch(() => setHealth(null));
  }, [libraryVersion]);

  const shown = useMemo(() => {
    if (bucket === "video") return items.filter((i) => i.kind === "video");
    if (bucket === "audio") return items.filter((i) => i.kind === "audio");
    return items;
  }, [items, bucket]);

  const buckets: Bucket[] = [
    { id: "all", label: "Todo", icon: <LibraryIcon size={15} /> },
    { id: "video", label: "Vídeos", icon: <FileVideo size={15} /> },
    { id: "audio", label: "Música", icon: <Music size={15} /> },
  ];

  async function del(item: LibraryItem, deleteFile: boolean) {
    try {
      await api.libraryDelete(item.id, deleteFile);
      toast("success", deleteFile ? "Archivo eliminado" : "Quitado de la biblioteca");
      refresh();
    } catch (e) {
      toast("error", String(e));
    }
  }

  return (
    <div className="flex h-full min-h-0">
      {/* ---- Colecciones ---- */}
      <aside className="flex w-60 shrink-0 flex-col gap-1 overflow-y-auto border-r border-line px-3 py-4">
        {buckets.map((b) => (
          <BucketButton
            key={b.id}
            active={bucket === b.id}
            onClick={() => setBucket(b.id)}
            icon={b.icon}
            label={b.label}
          />
        ))}

        {playlists.length > 0 && (
          <p className="mt-4 px-2 pb-1 text-[11px] font-semibold uppercase tracking-wider text-muted">
            Playlists
          </p>
        )}
        {playlists.map((p) => (
          <BucketButton
            key={p.id}
            active={bucket === p.id}
            onClick={() => setBucket(p.id)}
            icon={<ListMusic size={15} />}
            label={p.title}
            count={p.itemCount}
            onContextMenu={(e) =>
              openMenu(e, [
                ...(p.source === "local"
                  ? [
                      {
                        label: "Buscar canciones nuevas",
                        icon: <RefreshCw size={14} />,
                        onClick: async () => {
                          setImporting({ done: 0, total: 0 });
                          try {
                            const r = await api.libraryImportFolder(p.url);
                            toast(
                              "success",
                              r.added > 0
                                ? `${r.added} ${r.added === 1 ? "archivo nuevo" : "archivos nuevos"} en ${r.title}`
                                : `${r.title} ya estaba al día`,
                            );
                            useStore.setState((s) => ({
                              libraryVersion: s.libraryVersion + 1,
                            }));
                          } catch (err) {
                            toast("error", String(err));
                          } finally {
                            setImporting(null);
                          }
                        },
                      },
                    ]
                  : []),
                {
                  label: "Regenerar el .m3u8",
                  icon: <RefreshCw size={14} />,
                  onClick: async () => {
                    try {
                      const path = await api.exportM3u(p.id);
                      toast("success", `Playlist regenerada: ${path}`);
                    } catch (err) {
                      toast("error", String(err));
                    }
                  },
                },
                {
                  label: "Abrir el origen",
                  icon: <ExternalLink size={14} />,
                  onClick: () => api.openFolder(p.url),
                },
              ])
            }
          />
        ))}
      </aside>

      {/* ---- Contenido ---- */}
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center gap-2 border-b border-line px-6 py-3">
          <Search size={15} className="text-muted" />
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Buscar en la biblioteca…"
            className={`${inputClass} max-w-sm border-transparent bg-transparent`}
          />
          <span className="ml-auto text-[12px] text-muted">
            {shown.length} {shown.length === 1 ? "archivo" : "archivos"}
          </span>
          <IconButton
            title="Añadir una carpeta de tu equipo a la biblioteca"
            onClick={importFolder}
            disabled={importing !== null}
          >
            <FolderPlus size={15} />
          </IconButton>
          <IconButton
            title="Revisar archivos que ya no existen"
            onClick={async () => {
              const n = await api.libraryPrune();
              toast("success", n > 0 ? `${n} entradas obsoletas eliminadas` : "Todo en orden");
              refresh();
            }}
          >
            <RefreshCw size={15} />
          </IconButton>
        </div>

        {importing && (
          <div className="mx-6 mt-4 flex items-center gap-3 rounded-2xl border border-accent2/30 bg-accent2/5 px-3.5 py-2.5">
            <FolderPlus size={16} className="shrink-0 animate-pulse text-accent2" />
            <span className="min-w-0 flex-1 text-[12px] leading-snug">
              {importing.total > 0
                ? `Leyendo la carpeta: ${importing.done} de ${importing.total} archivos`
                : "Buscando música y vídeo en la carpeta…"}
            </span>
            {importing.total > 0 && (
              <span className="shrink-0 text-[12px] tabular-nums text-muted">
                {Math.round((importing.done / importing.total) * 100)}%
              </span>
            )}
          </div>
        )}

        {health && health.removed > 0 && (
          <div className="mx-6 mt-4 flex items-start gap-3 rounded-2xl border border-warn/40 bg-warn/5 p-3.5">
            <AlertTriangle size={17} className="mt-0.5 shrink-0 text-warn" />
            <div className="min-w-0 flex-1">
              <p className="text-[13px] font-medium">
                {health.sharedFiles > 0
                  ? "Hay canciones apuntando al archivo equivocado"
                  : "Hay entradas cuyos archivos ya no existen"}
              </p>
              <p className="text-[11.5px] leading-snug text-muted">
                {health.sharedFiles > 0 && (
                  <>
                    {health.sharedFiles} archivos tienen más de una canción asignada, así
                    que al reproducir unas suena otra. Es secuela de un fallo de las
                    versiones anteriores a la 0.1.3.{" "}
                  </>
                )}
                Se corregirían {health.removed} de {health.checked} entradas.{" "}
                <strong className="text-fg/90">No se borra ningún archivo.</strong>
              </p>
            </div>
            <button
              type="button"
              disabled={fixing}
              onClick={async () => {
                setFixing(true);
                try {
                  const r = await api.libraryRepair();
                  toast(
                    "success",
                    `${r.removed} entradas corregidas. Vuelve a analizar tus playlists para recuperar lo que falte.`,
                  );
                  setHealth(await api.libraryHealth());
                  refresh();
                } catch (e) {
                  toast("error", String(e));
                } finally {
                  setFixing(false);
                }
              }}
              className="shrink-0 rounded-xl bg-warn/20 px-3 py-1.5 text-[12px] font-medium text-warn transition hover:bg-warn/30 disabled:opacity-50"
            >
              {fixing ? "Corrigiendo…" : "Corregir"}
            </button>
          </div>
        )}

        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
          {loading && shown.length === 0 ? (
            <div className="flex flex-col gap-2">
              {[0, 1, 2, 3, 4].map((i) => (
                <div key={i} className="rc-skeleton h-16 rounded-2xl" />
              ))}
            </div>
          ) : shown.length === 0 ? (
            <EmptyState
              icon={<LibraryIcon size={22} />}
              title="Aquí no hay nada todavía"
              body="Lo que descargues aparecerá en esta biblioteca, agrupado por playlist y listo para reproducirse."
            />
          ) : (
            <div className="flex flex-col gap-1.5">
              {shown.map((item) => (
                <div
                  key={item.id}
                  onDoubleClick={() => play(item, shown)}
                  onContextMenu={(e) =>
                    openMenu(e, [
                      {
                        label: "Reproducir aquí",
                        icon: <Play size={14} />,
                        onClick: () => play(item, shown),
                      },
                      {
                        // El reproductor interno no cubre todos los formatos, y
                        // hay quien prefiere el suyo: sigue a un clic derecho.
                        label: "Abrir en el reproductor externo",
                        icon: <ExternalLink size={14} />,
                        onClick: () =>
                          api.playFile(item.filePath).catch((err) => toast("error", String(err))),
                      },
                      {
                        label: "Mostrar en la carpeta",
                        icon: <FolderOpen size={14} />,
                        onClick: () => api.revealFile(item.filePath),
                      },
                      {
                        label: "Copiar la ruta",
                        icon: <Copy size={14} />,
                        onClick: () => navigator.clipboard.writeText(item.filePath),
                      },
                      {
                        label: "Abrir el enlace original",
                        icon: <ExternalLink size={14} />,
                        onClick: () => api.openFolder(item.url),
                      },
                      { separator: true, label: "" },
                      {
                        label: "Quitar de la biblioteca",
                        icon: <Trash2 size={14} />,
                        onClick: () => del(item, false),
                      },
                      {
                        label: "Eliminar también el archivo",
                        icon: <Trash2 size={14} />,
                        danger: true,
                        onClick: () => del(item, true),
                      },
                    ])
                  }
                  className="rc-card group flex cursor-default items-center gap-3 p-2.5 transition hover:border-accent/40 hover:bg-surface2/60"
                >
                  <Thumb
                    src={item.thumbnail}
                    kind={item.kind}
                    className="h-11 w-20"
                    badge={duration(item.duration)}
                  />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-[13.5px] font-medium">{item.title}</p>
                    <p className="truncate text-[12px] text-muted">
                      {[item.uploader, item.ext.toUpperCase(), bytes(item.fileSize),
                        relativeDate(item.downloadedAt)]
                        .filter(Boolean)
                        .join(" · ")}
                    </p>
                  </div>
                  {/* La fila entera responde al doble clic, así que los botones
                      tienen que cortar la propagación: si no, un doble clic
                      encima de uno dispara su onClick dos veces *y además* el
                      doble clic de la fila, abriendo tres reproductores. */}
                  <div
                    className="flex shrink-0 gap-1 opacity-0 transition group-hover:opacity-100"
                    onDoubleClick={(e) => e.stopPropagation()}
                  >
                    <IconButton title="Reproducir" onClick={() => play(item, shown)}>
                      <Play size={15} />
                    </IconButton>
                    <IconButton
                      title="Mostrar en la carpeta"
                      onClick={() => api.revealFile(item.filePath)}
                    >
                      <FolderOpen size={15} />
                    </IconButton>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {menu}
    </div>
  );
}

function BucketButton({
  active,
  onClick,
  icon,
  label,
  count,
  onContextMenu,
}: {
  active: boolean;
  onClick: () => void;
  icon: ReactNode;
  label: string;
  count?: number;
  onContextMenu?: (e: MouseEvent) => void;
}) {
  return (
    <button
      onClick={onClick}
      onContextMenu={onContextMenu}
      className={`rc-ring flex items-center gap-2.5 rounded-xl px-2.5 py-2 text-left text-[13px] transition
        ${active ? "bg-accent/12 text-ink" : "text-muted hover:bg-surface2 hover:text-ink"}`}
    >
      <span className={active ? "text-accent2" : ""}>{icon}</span>
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {count != null && <span className="shrink-0 text-[11px] tabular-nums">{count}</span>}
    </button>
  );
}

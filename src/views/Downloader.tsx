import { open as openDialog } from "@tauri-apps/plugin-dialog";
import {
  AlertTriangle,
  CheckCheck,
  ClipboardPaste,
  Copy,
  Download,
  ExternalLink,
  Film,
  FolderOpen,
  History,
  Link2,
  ListChecks,
  Music,
  Play,
  RotateCcw,
  Search,
  Sparkles,
  Trash2,
  X,
} from "lucide-react";
import { listen } from "@tauri-apps/api/event";
import { useEffect, useMemo, useRef, useState } from "react";
import { BlockPicker } from "../components/BlockPicker";
import { MissingTools } from "../components/MissingTools";
import { Thumb } from "../components/Thumb";
import { useMenu } from "../components/Menu";
import {
  Button,
  EmptyState,
  IconButton,
  SegmentedControl,
  Select,
  inputClass,
} from "../components/ui";
import { api } from "../lib/api";
import { duration } from "../lib/format";
import { useStore } from "../lib/store";
import type { AnalyzeResult, Entry, Kind } from "../lib/types";

const WEB_URL = /^(?:https?:\/\/|www\.)/i;

function inputTargets(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return { targets: [] as string[], query: null as string | null };

  // Una frase normal es una búsqueda. Solo se separa por espacios cuando todo
  // lo introducido son enlaces; así «daft punk around the world» no termina
  // analizándose como cinco direcciones distintas.
  const parts = trimmed.split(/[\n\s]+/).filter(Boolean);
  if (parts.every((part) => WEB_URL.test(part))) {
    return { targets: parts, query: null };
  }

  return { targets: [`ytsearch20:${trimmed}`], query: trimmed };
}

export function Downloader({ onQueued }: { onQueued: () => void }) {
  const settings = useStore((s) => s.settings);
  const toast = useStore((s) => s.toast);
  const saveSettings = useStore((s) => s.saveSettings);
  const { openMenu, menu } = useMenu();

  const [url, setUrl] = useState("");
  const [analyzing, setAnalyzing] = useState(false);
  const [startedAt, setStartedAt] = useState(0);
  const tools = useStore((s) => s.tools);
  const [result, setResult] = useState<AnalyzeResult | null>(null);
  const [kind, setKind] = useState<Kind>("video");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [overwrite, setOverwrite] = useState<Set<string>>(new Set());
  const [destDir, setDestDir] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [blockSize, setBlockSize] = useState(50);
  const [searchedQuery, setSearchedQuery] = useState<string | null>(null);

  // El análisis en curso, para que los envíos tardíos no se cuelen en otro.
  const analisisActivo = useRef<string | null>(null);

  useEffect(() => {
    const mas = listen<[string, Entry[], boolean]>("analyze-more", (ev) => {
      const [key, nuevas] = ev.payload;
      if (key !== analisisActivo.current) return; // De un análisis ya descartado.
      setResult((prev) => {
        if (!prev) return prev;
        const vistos = new Set(prev.entries.map((e) => e.sourceId));
        const añadir = nuevas.filter((e) => !vistos.has(e.sourceId));
        return { ...prev, entries: [...prev.entries, ...añadir], partial: false };
      });
    });
    const fallo = listen<[string, string]>("analyze-failed", (ev) => {
      const [key, mensaje] = ev.payload;
      if (key !== analisisActivo.current) return;
      setResult((prev) => (prev ? { ...prev, partial: false } : prev));
      toast("error", `No se pudo completar la lista: ${mensaje}`);
    });
    return () => {
      mas.then((f) => f());
      fallo.then((f) => f());
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const existingOf = (e: Entry, k: Kind) =>
    k === "audio" ? e.existingAudio : e.existingVideo;

  function applyDefaults(res: AnalyzeResult, k: Kind) {
    const policy = settings?.duplicatePolicy ?? "skip";
    const sel = new Set<string>();
    const ow = new Set<string>();
    for (const e of res.entries) {
      if (e.unavailable) continue;
      const dup = existingOf(e, k);
      if (!dup) {
        sel.add(e.id);
      } else if (policy === "overwrite") {
        sel.add(e.id);
        ow.add(e.id);
      }
      // `skip` and `ask` both leave duplicates unchecked; `ask` just makes the
      // badge louder so the user notices there is a decision to make.
    }
    setSelected(sel);
    setOverwrite(ow);
  }

  async function analyze(refresh = false) {
    const { targets, query } = inputTargets(url);
    if (targets.length === 0) return;

    setAnalyzing(true);
    setStartedAt(Date.now());
    setResult(null);
    try {
      const results: AnalyzeResult[] = [];
      for (const u of targets) {
        results.push(await api.analyzeUrl(u, refresh));
      }
      let merged: AnalyzeResult =
        results.length === 1
          ? results[0]
          : {
              source: results[0].source,
              isPlaylist: false,
              playlist: null,
              entries: results.flatMap((r) => r.entries),
            };

      if (query) {
        // Los resultados de ytsearch llegan con forma de playlist, pero una
        // búsqueda no debe crear una colección artificial en la biblioteca.
        merged = { ...merged, isPlaylist: false, playlist: null };
      }

      // spotDL only produces audio, so don't offer a video toggle that lies.
      const nextKind: Kind = merged.source === "spotdl" ? "audio" : kind;
      setKind(nextKind);
      analisisActivo.current = merged.key ?? null;
      setResult(merged);
      setSearchedQuery(query);
      applyDefaults(merged, nextKind);
      setDestDir(null);
      setFilter("");
    } catch (e) {
      toast("error", String(e));
    } finally {
      setAnalyzing(false);
    }
  }

  function changeKind(k: Kind) {
    setKind(k);
    if (result) applyDefaults(result, k);
  }

  async function paste() {
    try {
      const text = await navigator.clipboard.readText();
      if (text.trim()) setUrl(text.trim());
    } catch {
      toast("info", "No se pudo leer el portapapeles");
    }
  }

  async function pickFolder() {
    const picked = await openDialog({
      directory: true,
      defaultPath: destDir ?? (kind === "audio" ? settings?.audioDir : settings?.videoDir),
    });
    if (typeof picked === "string") setDestDir(picked);
  }

  async function start() {
    if (!result) return;
    const entries = result.entries.filter((e) => selected.has(e.id));
    if (entries.length === 0) {
      toast("info", "No hay nada seleccionado");
      return;
    }
    try {
      const n = await api.enqueue({
        entries,
        kind,
        source: result.source,
        destDir,
        playlist: result.isPlaylist ? result.playlist : null,
        overwriteIds: [...overwrite],
      });
      toast("success", `${n} ${n === 1 ? "elemento añadido" : "elementos añadidos"} a la cola`);
      setResult(null);
      setUrl("");
      setSearchedQuery(null);
      onQueued();
    } catch (e) {
      toast("error", String(e));
    }
  }

  const visible = useMemo(() => {
    if (!result) return [];
    const q = filter.trim().toLowerCase();
    if (!q) return result.entries;
    return result.entries.filter(
      (e) =>
        e.title.toLowerCase().includes(q) ||
        (e.uploader ?? "").toLowerCase().includes(q),
    );
  }, [result, filter]);

  const duplicates = result
    ? result.entries.filter((e) => existingOf(e, kind)).length
    : 0;
  const dest =
    destDir ?? (kind === "audio" ? settings?.audioDir : settings?.videoDir) ?? "";

  const setAll = (ids: string[], on: boolean) => {
    const next = new Set(selected);
    for (const id of ids) (on ? next.add(id) : next.delete(id));
    setSelected(next);
  };

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-5 px-6 py-6">
      <MissingTools tools={tools} />

      {/* ---- Barra de búsqueda / enlace ---- */}
      <div className="rc-card p-4">
        <div className="flex items-center gap-2">
          {WEB_URL.test(url.trim()) ? (
            <Link2 size={17} className="ml-1 shrink-0 text-muted" />
          ) : (
            <Search size={17} className="ml-1 shrink-0 text-muted" />
          )}
          <input
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) analyze();
            }}
            placeholder="Busca en YouTube o pega un enlace de YouTube, Spotify, Twitch, TikTok…"
            className={`${inputClass} border-transparent bg-transparent text-[14px] focus:border-transparent`}
            spellCheck={false}
          />
          <IconButton title="Pegar del portapapeles" onClick={paste}>
            <ClipboardPaste size={16} />
          </IconButton>
          {/* Sin la lambda, React pasaría el evento del clic como `refresh`, que
              es un objeto y por tanto siempre verdadero: nunca se usaría lo
              guardado. */}
          <Button
            variant="primary"
            onClick={() => analyze()}
            disabled={analyzing || !url.trim()}
          >
            {analyzing ? (
              <>
                <Sparkles size={15} className="animate-pulse" /> Analizando…
              </>
            ) : (
              <>
                <Search size={15} /> {WEB_URL.test(url.trim()) ? "Analizar" : "Buscar"}
              </>
            )}
          </Button>
        </div>
      </div>

      {analyzing && <AnalyzingSkeleton startedAt={startedAt} />}

      {!analyzing && !result && (
        <EmptyState
          icon={<Download size={22} />}
          title="Busca un vídeo o pega un enlace"
          body="Escribe un título, artista o tema para buscar directamente en YouTube. También puedes pegar enlaces de YouTube, Twitch, X, TikTok, Vimeo, Instagram o Spotify."
        />
      )}

      {result?.notice && (
        <div className="flex items-start gap-2.5 rounded-xl border border-warn/40 bg-warn/5 px-3 py-2.5 text-[12px]">
          <AlertTriangle size={15} className="mt-0.5 shrink-0 text-warn" />
          <span className="min-w-0 flex-1 leading-snug">{result.notice}</span>
        </div>
      )}

      {result?.partial && (
        <div className="flex items-center gap-2.5 rounded-xl border border-accent2/30 bg-accent2/5 px-3 py-2 text-[12px]">
          <Sparkles size={14} className="shrink-0 animate-pulse text-accent2" />
          <span className="min-w-0 flex-1 leading-snug">
            Spotify entrega las listas de cien en cien. Estas{" "}
            {result.entries.length} ya se pueden descargar; el resto va llegando.
          </span>
        </div>
      )}

      {result?.cachedAt && (
        <div className="flex items-center gap-2 rounded-xl border border-line bg-surface2 px-3 py-2 text-[12px] text-muted">
          <History size={14} className="shrink-0" />
          <span className="min-w-0 flex-1">
            Lista guardada de hace {sinceText(result.cachedAt)}. Si has añadido
            canciones desde entonces, actualízala.
          </span>
          <Button onClick={() => analyze(true)} disabled={analyzing}>
            <RotateCcw size={13} /> Actualizar
          </Button>
        </div>
      )}

      {result && (
        <>
          {/* ---- Cabecera del resultado ---- */}
          <div className="rc-card overflow-hidden">
            <div className="flex items-start gap-4 p-4">
              <Thumb
                src={result.playlist?.thumbnail ?? result.entries[0]?.thumbnail}
                kind={kind}
                className="h-20 w-32"
              />
              <div className="min-w-0 flex-1">
                <p className="text-[11px] font-semibold uppercase tracking-wider text-accent2">
                  {searchedQuery
                    ? `Resultados de YouTube · ${result.entries.length}`
                    : result.isPlaylist
                    ? `Playlist · ${result.entries.length} elementos`
                    : result.source === "spotdl"
                      ? "Pista de Spotify"
                      : "Elemento suelto"}
                </p>
                <h2 className="mt-0.5 truncate text-[17px] font-semibold">
                  {searchedQuery ?? result.playlist?.title ?? result.entries[0]?.title ?? "Sin título"}
                </h2>
                <p className="truncate text-[13px] text-muted">
                  {result.playlist?.uploader ?? result.entries[0]?.uploader ?? ""}
                </p>
                {duplicates > 0 && (
                  <p className="mt-2 inline-flex items-center gap-1.5 rounded-lg bg-warn/12 px-2 py-1 text-[12px] text-warn">
                    <CheckCheck size={13} />
                    {duplicates} ya {duplicates === 1 ? "está" : "están"} en tu equipo
                    {settings?.duplicatePolicy === "skip" && " · se omitirán"}
                  </p>
                )}
              </div>
              <IconButton title="Descartar" onClick={() => {
                  analisisActivo.current = null;
                  setResult(null);
                }}>
                <X size={16} />
              </IconButton>
            </div>

            {/* ---- Opciones ---- */}
            <div className="flex flex-wrap items-end gap-4 border-t border-line bg-surface2/40 px-4 py-3">
              <SegmentedControl<Kind>
                value={kind}
                onChange={changeKind}
                options={
                  result.source === "spotdl"
                    ? [{ value: "audio", label: "Música", icon: <Music size={14} /> }]
                    : [
                        { value: "video", label: "Vídeo", icon: <Film size={14} /> },
                        { value: "audio", label: "Solo audio", icon: <Music size={14} /> },
                      ]
                }
              />

              {kind === "video" ? (
                <div className="w-36">
                  <Select
                    value={settings?.videoQuality ?? "best"}
                    onChange={(v) => saveSettings({ videoQuality: v })}
                    options={[
                      { value: "best", label: "Máxima calidad" },
                      { value: "2160", label: "4K · 2160p" },
                      { value: "1440", label: "2K · 1440p" },
                      { value: "1080", label: "Full HD · 1080p" },
                      { value: "720", label: "HD · 720p" },
                      { value: "480", label: "SD · 480p" },
                    ]}
                  />
                </div>
              ) : (
                <div className="w-32">
                  <Select
                    value={settings?.audioFormat ?? "mp3"}
                    onChange={(v) => saveSettings({ audioFormat: v })}
                    options={[
                      { value: "mp3", label: "MP3" },
                      { value: "m4a", label: "M4A" },
                      { value: "opus", label: "Opus" },
                      { value: "flac", label: "FLAC" },
                      { value: "wav", label: "WAV" },
                    ]}
                  />
                </div>
              )}

              <button
                onClick={pickFolder}
                title={dest}
                className="rc-ring flex min-w-0 max-w-sm flex-1 items-center gap-2 rounded-xl border border-line bg-surface2 px-3 py-2 text-left text-[12px] text-muted transition hover:border-accent/50 hover:text-ink"
              >
                <FolderOpen size={15} className="shrink-0" />
                <span className="truncate" dir="rtl">
                  {dest}
                </span>
              </button>

              <Button variant="primary" onClick={start} className="ml-auto px-5 py-2.5">
                <Download size={15} />
                Descargar {selected.size > 0 && `(${selected.size})`}
              </Button>
            </div>
          </div>

          {/* ---- Lista ---- */}
          {result.entries.length > 1 && (
            <BlockPicker
              entries={result.entries}
              size={blockSize}
              onSizeChange={setBlockSize}
              selected={selected}
              isDone={(e) => Boolean(existingOf(e, kind))}
              onToggleBlock={setAll}
            />
          )}

          {result.entries.length > 1 && (
            <div className="flex flex-wrap items-center gap-2">
              <Button onClick={() => setAll(visible.map((e) => e.id), true)}>
                <ListChecks size={14} /> Todos
              </Button>
              <Button onClick={() => setAll(visible.map((e) => e.id), false)}>
                <X size={14} /> Ninguno
              </Button>
              <Button
                onClick={() =>
                  setSelected(
                    new Set(
                      result.entries
                        .filter((e) => !e.unavailable && !existingOf(e, kind))
                        .map((e) => e.id),
                    ),
                  )
                }
              >
                <Sparkles size={14} /> Solo lo nuevo
              </Button>
              <input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="Filtrar…"
                className={`${inputClass} ml-auto max-w-56`}
              />
            </div>
          )}

          <div className="flex flex-col gap-1.5 pb-4">
            {visible.map((entry) => {
              const existing = existingOf(entry, kind);
              const isSelected = selected.has(entry.id);
              const willOverwrite = overwrite.has(entry.id);

              return (
                <div
                  key={entry.id}
                  onContextMenu={(e) =>
                    openMenu(e, [
                      {
                        label: isSelected ? "Quitar de la selección" : "Añadir a la selección",
                        icon: <ListChecks size={14} />,
                        onClick: () => setAll([entry.id], !isSelected),
                      },
                      ...(existing
                        ? [
                            {
                              label: willOverwrite
                                ? "Omitir (mantener el archivo actual)"
                                : "Sobrescribir el archivo existente",
                              icon: <RotateCcw size={14} />,
                              onClick: () => {
                                const next = new Set(overwrite);
                                if (willOverwrite) next.delete(entry.id);
                                else {
                                  next.add(entry.id);
                                  setAll([entry.id], true);
                                }
                                setOverwrite(next);
                              },
                            },
                            {
                              label: "Reproducir el que ya tengo",
                              icon: <Play size={14} />,
                              onClick: () =>
                                api.playFile(existing).catch((e) => toast("error", String(e))),
                            },
                          ]
                        : []),
                      { separator: true, label: "" },
                      {
                        label: "Copiar enlace",
                        icon: <Copy size={14} />,
                        onClick: () => navigator.clipboard.writeText(entry.url),
                      },
                      {
                        label: "Abrir en el navegador",
                        icon: <ExternalLink size={14} />,
                        onClick: () => api.openFolder(entry.url),
                      },
                      { separator: true, label: "" },
                      {
                        label: "Descartar de la lista",
                        icon: <Trash2 size={14} />,
                        danger: true,
                        onClick: () =>
                          setResult({
                            ...result,
                            entries: result.entries.filter((e) => e.id !== entry.id),
                          }),
                      },
                    ])
                  }
                  onClick={() => !entry.unavailable && setAll([entry.id], !isSelected)}
                  className={`rc-card flex cursor-pointer items-center gap-3 p-2.5 transition
                    ${isSelected ? "border-accent/45 bg-accent/6" : "hover:border-line/80 hover:bg-surface2/60"}
                    ${entry.unavailable ? "cursor-not-allowed opacity-45" : ""}`}
                >
                  <span
                    className={`flex h-5 w-5 shrink-0 items-center justify-center rounded-md border transition
                      ${
                        isSelected
                          ? "border-transparent bg-gradient-to-br from-accent to-accent2 text-white"
                          : "border-line"
                      }`}
                  >
                    {isSelected && <CheckCheck size={12} />}
                  </span>

                  <Thumb
                    src={entry.thumbnail}
                    kind={kind}
                    className="h-11 w-20"
                    badge={duration(entry.duration)}
                  />

                  <div className="min-w-0 flex-1">
                    <p className="truncate text-[13.5px] font-medium">{entry.title}</p>
                    <p className="truncate text-[12px] text-muted">
                      {entry.uploader ?? entry.extractor}
                    </p>
                  </div>

                  {entry.unavailable && (
                    <span className="flex shrink-0 items-center gap-1 rounded-lg bg-bad/12 px-2 py-1 text-[11px] text-bad">
                      <AlertTriangle size={12} /> No disponible
                    </span>
                  )}
                  {existing && !entry.unavailable && (
                    <span
                      className={`shrink-0 rounded-lg px-2 py-1 text-[11px] ${
                        willOverwrite
                          ? "bg-accent3/15 text-accent3"
                          : "bg-warn/12 text-warn"
                      }`}
                    >
                      {willOverwrite ? "Sobrescribir" : "Ya lo tienes"}
                    </span>
                  )}
                </div>
              );
            })}
          </div>
        </>
      )}

      {menu}
    </div>
  );
}

/** «hace 3 minutos», «hace 2 días»: sin precisión falsa. */
function sinceText(epochSeconds: number): string {
  const s = Math.max(0, Math.floor(Date.now() / 1000 - epochSeconds));
  if (s < 90) return "un momento";
  const m = Math.round(s / 60);
  if (m < 60) return `${m} minutos`;
  const h = Math.round(m / 60);
  if (h < 36) return h === 1 ? "una hora" : `${h} horas`;
  const d = Math.round(h / 24);
  return d === 1 ? "un día" : `${d} días`;
}

/**
 * Un esqueleto mudo hace que cualquier espera larga parezca un cuelgue. El
 * contador quita esa duda, y a partir de unos segundos explica de qué depende
 * la espera en vez de dejar al usuario adivinando.
 */
function AnalyzingSkeleton({ startedAt }: { startedAt: number }) {
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    const t = setInterval(
      () => setElapsed(Math.floor((Date.now() - startedAt) / 1000)),
      500,
    );
    return () => clearInterval(t);
  }, [startedAt]);

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-2 px-1 text-[12px] text-muted">
        <Sparkles size={13} className="animate-pulse text-accent2" />
        <span>Leyendo el enlace…</span>
        <span className="tabular-nums">{elapsed}s</span>
        {elapsed >= 8 && (
          <span className="ml-1 text-muted/80">
            las listas muy largas tardan más; puedes descartar y volver a intentarlo
          </span>
        )}
      </div>
      <div className="rc-skeleton h-24 rounded-2xl" />
      {[0, 1, 2, 3].map((i) => (
        <div
          key={i}
          className="rc-skeleton h-16 rounded-2xl"
          style={{ animationDelay: `${i * 90}ms`, opacity: 1 - i * 0.18 }}
        />
      ))}
    </div>
  );
}

import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  Download,
  Eye,
  FileDown,
  FileUp,
  FolderOpen,
  ListPlus,
  Music,
  Plus,
  Radio,
  RefreshCw,
  Rss,
  Search,
  Save,
  Settings2,
  Trash2,
  Video,
} from "lucide-react";
import { open as openDialog, save as saveDialog } from "@tauri-apps/plugin-dialog";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Thumb } from "../components/Thumb";
import { Button, EmptyState, Field, IconButton, SegmentedControl, Select, Toggle, inputClass } from "../components/ui";
import { api } from "../lib/api";
import { duration, relativeDate } from "../lib/format";
import { useStore } from "../lib/store";
import type { Kind, MediaSource, MediaSourceItem, MediaSourceItemStatus, SourceProfile, YoutubeAccount } from "../lib/types";

type Filter = "all" | MediaSourceItemStatus | "live" | "upcoming";

const STATUS: Record<MediaSourceItemStatus, { label: string; cls: string }> = {
  new: { label: "Nuevo", cls: "bg-accent/15 text-accent2" },
  seen: { label: "Revisado", cls: "bg-surface3 text-muted" },
  downloaded: { label: "Descargado", cls: "bg-ok/15 text-ok" },
  unavailable: { label: "No disponible", cls: "bg-warn/15 text-warn" },
  removed: { label: "Desaparecido", cls: "bg-bad/15 text-bad" },
};

const INHERIT = { value: "", label: "Heredar de Ajustes" };

function triValue(value: boolean | null) {
  return value === null ? "" : value ? "on" : "off";
}

function triState(value: string): boolean | null {
  return value === "" ? null : value === "on";
}

function futureDate(unixSeconds: number) {
  const seconds = Math.max(0, unixSeconds - Date.now() / 1000);
  if (seconds < 60) return "en menos de un minuto";
  if (seconds < 3600) return `en ${Math.ceil(seconds / 60)} min`;
  if (seconds < 86400) return `en ${Math.ceil(seconds / 3600)} h`;
  if (seconds < 604800) return `en ${Math.ceil(seconds / 86400)} d`;
  return new Date(unixSeconds * 1000).toLocaleDateString();
}

function SourceProfileEditor({ source, onSaved }: { source: MediaSource; onSaved: (source: MediaSource) => void }) {
  const toast = useStore((s) => s.toast);
  const [profile, setProfile] = useState<SourceProfile>({ ...source.profile });
  const [mediaKind, setMediaKind] = useState<Kind>(source.mediaKind);
  const [accounts, setAccounts] = useState<YoutubeAccount[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void api.youtubeAccounts().then(setAccounts).catch(() => setAccounts([]));
  }, []);

  const set = <K extends keyof SourceProfile>(key: K, value: SourceProfile[K]) => {
    setProfile((current) => ({ ...current, [key]: value }));
  };

  async function chooseFolder() {
    const picked = await openDialog({ directory: true, defaultPath: profile.destDir ?? undefined });
    if (typeof picked === "string") set("destDir", picked);
  }

  async function save() {
    if (saving) return;
    setSaving(true);
    try {
      const updated = await api.mediaSourceUpdateProfile(source.id, mediaKind, profile);
      setProfile({ ...updated.profile });
      onSaved(updated);
      toast("success", `Perfil guardado para ${updated.title}`);
    } catch (error) {
      toast("error", String(error));
    } finally {
      setSaving(false);
    }
  }

  const customCount = Object.values(profile).filter((value) => value !== null && value !== "").length;

  return (
    <details className="border-b border-line bg-surface2/35">
      <summary className="flex cursor-pointer list-none items-center gap-3 px-4 py-3 text-[12px] font-medium hover:bg-surface2">
        <Settings2 size={15} className="text-accent2" />
        <span className="flex-1">Perfil de descarga de esta Fuente</span>
        {customCount > 0 && <span className="rounded-full bg-accent/15 px-2 py-0.5 text-[10px] text-accent2">{customCount} ajustes propios</span>}
        <span className="text-[10.5px] text-muted">Calidad, formato, cuenta y destino</span>
      </summary>
      <div className="space-y-4 border-t border-line px-4 py-4">
        <p className="text-[11px] leading-relaxed text-muted">
          Los campos en “Heredar” siguen los Ajustes generales. El perfil se copia al trabajo al añadirlo a la cola.
        </p>

        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
          <Field label="Descargar como">
            <SegmentedControl value={mediaKind} onChange={setMediaKind} options={[
              { value: "video", label: "Vídeo", icon: <Video size={13} /> },
              { value: "audio", label: "Audio", icon: <Music size={13} /> },
            ]} />
          </Field>
          {mediaKind === "video" ? (
            <>
              <Field label="Calidad máxima">
                <Select value={profile.videoQuality ?? ""} onChange={(value) => set("videoQuality", value || null)} options={[
                  INHERIT, { value: "best", label: "La mejor" }, { value: "2160", label: "4K (2160p)" },
                  { value: "1440", label: "1440p" }, { value: "1080", label: "1080p" },
                  { value: "720", label: "720p" }, { value: "480", label: "480p" }, { value: "360", label: "360p" },
                ]} />
              </Field>
              <Field label="Contenedor">
                <Select value={profile.videoContainer ?? ""} onChange={(value) => set("videoContainer", value || null)} options={[
                  INHERIT, { value: "original", label: "Original" }, { value: "mp4", label: "MP4" },
                  { value: "mkv", label: "MKV" }, { value: "webm", label: "WebM" },
                ]} />
              </Field>
            </>
          ) : (
            <>
              <Field label="Formato de audio">
                <Select value={profile.audioFormat ?? ""} onChange={(value) => set("audioFormat", value || null)} options={[
                  INHERIT, { value: "mp3", label: "MP3" }, { value: "m4a", label: "M4A" },
                  { value: "opus", label: "Opus" }, { value: "flac", label: "FLAC" }, { value: "wav", label: "WAV" },
                ]} />
              </Field>
              <Field label="Bitrate">
                <Select value={profile.audioBitrate ?? ""} onChange={(value) => set("audioBitrate", value || null)} options={[
                  INHERIT, { value: "320", label: "320 kbps" }, { value: "256", label: "256 kbps" },
                  { value: "192", label: "192 kbps" }, { value: "160", label: "160 kbps" }, { value: "128", label: "128 kbps" },
                ]} />
              </Field>
            </>
          )}
          <Field label="SponsorBlock">
            <Select value={triValue(profile.sponsorblock)} onChange={(value) => set("sponsorblock", triState(value))} options={[
              INHERIT, { value: "on", label: "Activado" }, { value: "off", label: "Desactivado" },
            ]} />
          </Field>
        </div>

        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
          <Field label="Descargar subtítulos">
            <Select value={triValue(profile.writeSubtitles)} onChange={(value) => set("writeSubtitles", triState(value))} options={[
              INHERIT, { value: "on", label: "Activado" }, { value: "off", label: "Desactivado" },
            ]} />
          </Field>
          <Field label="Incrustar subtítulos">
            <Select value={triValue(profile.embedSubtitles)} onChange={(value) => set("embedSubtitles", triState(value))} options={[
              INHERIT, { value: "on", label: "Activado" }, { value: "off", label: "Desactivado" },
            ]} />
          </Field>
          <Field label="Idiomas" hint="Ejemplo: es,en; vacío hereda el valor general.">
            <input className={inputClass} value={profile.subtitleLangs ?? ""} onChange={(event) => set("subtitleLangs", event.target.value || null)} placeholder="Heredar de Ajustes" />
          </Field>
          {source.source === "ytdlp" && (
            <Field label="Cuenta de YouTube">
              <Select value={profile.youtubeCookiesFile ?? ""} onChange={(value) => set("youtubeCookiesFile", value || null)} options={[
                INHERIT,
                ...accounts.map((account) => ({ value: account.cookiesFile, label: account.name })),
              ]} />
            </Field>
          )}
        </div>

        <Field label="Carpeta de destino" hint="Vacía usa la carpeta general de vídeo o audio.">
          <div className="flex gap-2">
            <input className={inputClass} value={profile.destDir ?? ""} onChange={(event) => set("destDir", event.target.value || null)} placeholder="Heredar de Ajustes" />
            <IconButton title="Elegir carpeta" onClick={() => void chooseFolder()}><FolderOpen size={16} /></IconButton>
            {profile.destDir && <Button variant="ghost" onClick={() => set("destDir", null)}>Heredar</Button>}
          </div>
        </Field>

        <div className="flex justify-end">
          <Button variant="primary" disabled={saving} onClick={() => void save()}>
            {saving ? <RefreshCw size={14} className="animate-spin" /> : <Save size={14} />}
            {saving ? "Guardando…" : "Guardar perfil"}
          </Button>
        </div>
      </div>
    </details>
  );
}

const SCHEDULES = [
  { value: "", label: "Sólo manual" },
  { value: "15", label: "Cada 15 minutos" },
  { value: "60", label: "Cada hora" },
  { value: "360", label: "Cada 6 horas" },
  { value: "720", label: "Cada 12 horas" },
  { value: "1440", label: "Cada día" },
  { value: "4320", label: "Cada 3 días" },
  { value: "10080", label: "Cada semana" },
];

function SourceScheduleEditor({ source, onSaved }: { source: MediaSource; onSaved: (source: MediaSource) => void }) {
  const toast = useStore((s) => s.toast);
  const [interval, setInterval] = useState(source.checkIntervalMinutes?.toString() ?? "");
  const [autoDownload, setAutoDownload] = useState(source.autoDownload);
  const [saving, setSaving] = useState(false);
  const scheduleLabel = SCHEDULES.find((option) => option.value === interval)?.label ?? "Sólo manual";
  const nextCheck = interval
    ? (source.lastCheckedAt ?? source.createdAt) + Number(interval) * 60
    : null;

  async function save() {
    if (saving) return;
    setSaving(true);
    try {
      const updated = await api.mediaSourceUpdateSchedule(
        source.id,
        interval ? Number(interval) : null,
        Boolean(interval) && autoDownload,
      );
      setAutoDownload(updated.autoDownload);
      onSaved(updated);
      toast("success", interval ? `Programación guardada: ${scheduleLabel.toLocaleLowerCase()}` : "Comprobación automática desactivada");
    } catch (error) {
      toast("error", String(error));
    } finally {
      setSaving(false);
    }
  }

  return (
    <details className="border-b border-line bg-surface2/20">
      <summary className="flex cursor-pointer list-none items-center gap-3 px-4 py-3 text-[12px] font-medium hover:bg-surface2">
        <Clock3 size={15} className="text-accent2" />
        <span className="flex-1">Comprobación automática</span>
        {source.autoDownload && <span className="rounded-full bg-ok/15 px-2 py-0.5 text-[10px] text-ok">Descarga automática</span>}
        <span className="text-[10.5px] text-muted">{SCHEDULES.find((option) => option.value === (source.checkIntervalMinutes?.toString() ?? ""))?.label}</span>
      </summary>
      <div className="grid gap-4 border-t border-line px-4 py-4 md:grid-cols-[minmax(220px,0.8fr)_minmax(280px,1.2fr)_auto] md:items-end">
        <Field label="Frecuencia" hint={nextCheck ? `Próxima comprobación aproximada: ${futureDate(nextCheck)}` : "Recodio sólo comprobará cuando pulses el botón."}>
          <Select value={interval} onChange={(value) => { setInterval(value); if (!value) setAutoDownload(false); }} options={SCHEDULES} />
        </Field>
        <div className={!interval ? "pointer-events-none opacity-45" : ""}>
          <Toggle
            checked={autoDownload}
            onChange={setAutoDownload}
            label="Descargar novedades automáticamente"
            hint="Después de comprobar, añade sólo los elementos nuevos a la cola con el perfil de esta Fuente."
          />
        </div>
        <Button variant="primary" disabled={saving} onClick={() => void save()}>
          {saving ? <RefreshCw size={14} className="animate-spin" /> : <Save size={14} />}
          Guardar programación
        </Button>
      </div>
    </details>
  );
}

export function SourcesView({ onQueued }: { onQueued: () => void }) {
  const toast = useStore((s) => s.toast);
  const [sources, setSources] = useState<MediaSource[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [items, setItems] = useState<MediaSourceItem[]>([]);
  const [url, setUrl] = useState("");
  const [kind, setKind] = useState<Kind>("video");
  const [loading, setLoading] = useState(true);
  const [adding, setAdding] = useState(false);
  const [syncing, setSyncing] = useState<string | null>(null);
  const [enqueueing, setEnqueueing] = useState(false);
  const [itemsLoading, setItemsLoading] = useState(false);
  const [itemsError, setItemsError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>("all");
  const [search, setSearch] = useState("");
  const [limit, setLimit] = useState(100);
  const [fileBusy, setFileBusy] = useState<"import" | "export" | null>(null);

  const selected = sources.find((source) => source.id === selectedId) ?? null;

  const loadSources = useCallback(async () => {
    try {
      const next = await api.mediaSources();
      setSources(next);
      setSelectedId((current) => current && next.some((s) => s.id === current) ? current : (next[0]?.id ?? null));
    } catch (error) {
      toast("error", String(error));
    } finally {
      setLoading(false);
    }
  }, [toast]);

  const loadItems = useCallback(async (id: string) => {
    setItemsLoading(true);
    setItemsError(null);
    try {
      setItems(await api.mediaSourceItems(id));
    } catch (error) {
      toast("error", String(error));
      setItemsError(String(error));
      setItems([]);
    } finally {
      setItemsLoading(false);
    }
  }, [toast]);

  useEffect(() => { void loadSources(); }, [loadSources]);
  useEffect(() => {
    if (selectedId) void loadItems(selectedId);
    else setItems([]);
    setLimit(100);
  }, [selectedId, loadItems]);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void listen<string>("media-sources-changed", () => {
      void loadSources();
      if (selectedId) void loadItems(selectedId);
    }).then((stop) => { unlisten = stop; });
    return () => unlisten?.();
  }, [loadSources, loadItems, selectedId]);

  useEffect(() => {
    if (/spotify\.com|^spotify:/i.test(url.trim())) setKind("audio");
  }, [url]);

  async function addSource() {
    if (!url.trim() || adding) return;
    setAdding(true);
    try {
      const source = await api.mediaSourceAdd(url.trim(), kind);
      setUrl("");
      await loadSources();
      setSelectedId(source.id);
      await loadItems(source.id);
      toast("success", `Fuente añadida: ${source.title}`);
    } catch (error) {
      toast("error", String(error));
    } finally {
      setAdding(false);
    }
  }

  async function syncSource(source: MediaSource) {
    if (syncing) return;
    setSyncing(source.id);
    try {
      const updated = await api.mediaSourceSync(source.id);
      setSources((current) => current.map((s) => s.id === updated.id ? updated : s));
      if (selectedId === source.id) await loadItems(source.id);
      toast("success", updated.newItems > 0
        ? `${updated.newItems} elementos nuevos en ${updated.title}`
        : `${updated.title} está al día`);
    } catch (error) {
      await loadSources();
      toast("error", String(error));
    } finally {
      setSyncing(null);
    }
  }

  async function downloadNew() {
    if (!selected || enqueueing) return;
    const candidates = items.filter((item) => item.status === "new" && item.present
      && !item.entry.unavailable && item.entry.liveStatus !== "is_upcoming");
    if (candidates.length === 0) {
      const waiting = items.filter((item) => item.status === "new" && item.entry.liveStatus === "is_upcoming").length;
      toast("info", waiting > 0
        ? `${waiting} ${waiting === 1 ? "estreno todavía no ha comenzado" : "estrenos todavía no han comenzado"}`
        : "Esta Fuente no tiene elementos nuevos disponibles");
      return;
    }
    setEnqueueing(true);
    try {
      const count = await api.enqueue({
        entries: candidates.map((item) => item.entry),
        kind: selected.mediaKind,
        source: selected.source,
        destDir: selected.profile.destDir,
        profile: selected.profile,
        playlist: {
          sourceId: selected.sourceId,
          title: selected.title,
          url: selected.url,
          uploader: selected.uploader,
          thumbnail: selected.thumbnail,
        },
      });
      await api.mediaSourceMarkSeen(selected.id, candidates.map((item) => item.remoteId));
      await Promise.all([loadSources(), loadItems(selected.id)]);
      toast("success", `${count} ${count === 1 ? "elemento añadido" : "elementos añadidos"} a la cola`);
      onQueued();
    } catch (error) {
      toast("error", String(error));
    } finally {
      setEnqueueing(false);
    }
  }

  async function deleteSource(source: MediaSource) {
    if (!window.confirm(`¿Quitar «${source.title}» de Fuentes? Los archivos descargados no se borrarán.`)) return;
    try {
      await api.mediaSourceDelete(source.id);
      setSources((current) => current.filter((s) => s.id !== source.id));
      if (selectedId === source.id) setSelectedId(null);
      toast("success", "Fuente eliminada; la biblioteca se conserva");
    } catch (error) {
      toast("error", String(error));
    }
  }

  async function exportSources() {
    if (fileBusy) return;
    const path = await saveDialog({
      defaultPath: "recodio-fuentes.json",
      filters: [{ name: "Respaldo de Fuentes", extensions: ["json"] }],
    });
    if (!path) return;
    setFileBusy("export");
    try {
      const count = await api.mediaSourcesExport(path);
      toast("success", `${count} ${count === 1 ? "Fuente exportada" : "Fuentes exportadas"}; las cookies no se incluyen`);
    } catch (error) {
      toast("error", String(error));
    } finally {
      setFileBusy(null);
    }
  }

  async function importSources() {
    if (fileBusy) return;
    const path = await openDialog({
      multiple: false,
      filters: [{ name: "Respaldo de Fuentes", extensions: ["json"] }],
    });
    if (typeof path !== "string") return;
    setFileBusy("import");
    try {
      const count = await api.mediaSourcesImport(path);
      await loadSources();
      toast("success", `${count} ${count === 1 ? "Fuente importada" : "Fuentes importadas"}. Compruébalas para cargar su contenido.`);
    } catch (error) {
      toast("error", String(error));
    } finally {
      setFileBusy(null);
    }
  }

  const counts = useMemo(() => {
    const result: Record<string, number> = { all: items.length };
    for (const item of items) {
      result[item.status] = (result[item.status] ?? 0) + 1;
      if (["is_live", "was_live", "post_live"].includes(item.entry.liveStatus ?? "")) result.live = (result.live ?? 0) + 1;
      if (item.entry.liveStatus === "is_upcoming") result.upcoming = (result.upcoming ?? 0) + 1;
    }
    return result;
  }, [items]);

  const visible = useMemo(() => {
    const q = search.trim().toLocaleLowerCase();
    return items.filter((item) => {
      if (filter === "live" && !["is_live", "was_live", "post_live"].includes(item.entry.liveStatus ?? "")) return false;
      if (filter === "upcoming" && item.entry.liveStatus !== "is_upcoming") return false;
      if (!(["all", "live", "upcoming"] as Filter[]).includes(filter) && item.status !== filter) return false;
      if (!q) return true;
      return item.entry.title.toLocaleLowerCase().includes(q)
        || item.entry.uploader?.toLocaleLowerCase().includes(q);
    });
  }, [items, filter, search]);

  return (
    <div className="mx-auto flex min-h-full max-w-[1180px] flex-col gap-5 px-6 py-6 pb-28">
      <header className="flex items-start gap-3">
        <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-accent/15 text-accent2"><Rss size={20} /></div>
        <div className="min-w-0 flex-1">
          <h1 className="text-[21px] font-semibold tracking-tight">Fuentes</h1>
          <p className="mt-0.5 text-[12px] text-muted">Guarda canales y playlists, comprueba novedades y descarga sólo lo nuevo.</p>
        </div>
        <Button variant="ghost" disabled={fileBusy !== null} onClick={() => void importSources()}>
          {fileBusy === "import" ? <RefreshCw size={14} className="animate-spin" /> : <FileUp size={14} />} Importar
        </Button>
        <Button variant="ghost" disabled={fileBusy !== null || sources.length === 0} onClick={() => void exportSources()}>
          {fileBusy === "export" ? <RefreshCw size={14} className="animate-spin" /> : <FileDown size={14} />} Exportar
        </Button>
        <span className="rounded-full border border-line bg-surface2 px-2.5 py-1 text-[11px] text-muted">
          {sources.length} {sources.length === 1 ? "Fuente" : "Fuentes"}
        </span>
      </header>

      <section className="rc-card p-4">
        <div className="flex flex-wrap items-end gap-3">
          <label className="min-w-64 flex-1">
            <span className="mb-1.5 block text-[12px] font-medium text-muted">Canal, playlist, álbum o colección</span>
            <input
              value={url}
              onChange={(event) => setUrl(event.target.value)}
              onKeyDown={(event) => { if (event.key === "Enter") void addSource(); }}
              placeholder="Pega un enlace de YouTube, Spotify u otro sitio compatible…"
              className={inputClass}
            />
          </label>
          <SegmentedControl
            value={kind}
            onChange={setKind}
            options={[
              { value: "video", label: "Vídeo", icon: <Video size={14} /> },
              { value: "audio", label: "Audio", icon: <Music size={14} /> },
            ]}
          />
          <Button variant="primary" disabled={!url.trim() || adding} onClick={() => void addSource()}>
            {adding ? <RefreshCw size={15} className="animate-spin" /> : <Plus size={15} />}
            {adding ? "Analizando…" : "Añadir Fuente"}
          </Button>
        </div>
        <p className="mt-2 text-[11px] text-muted">La primera comprobación puede tardar en playlists grandes. No comienza ninguna descarga por sí sola.</p>
      </section>

      {loading ? (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">{[0, 1, 2].map((n) => <div key={n} className="rc-skeleton h-32 rounded-2xl" />)}</div>
      ) : sources.length === 0 ? (
        <EmptyState icon={<Radio size={25} />} title="Todavía no tienes Fuentes" body="Añade un canal o una playlist para que Recodio recuerde su contenido y encuentre novedades." />
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {sources.map((source) => (
            <button
              type="button"
              key={source.id}
              onClick={() => setSelectedId(source.id)}
              className={`rc-card rc-ring p-3 text-left transition hover:border-accent/40 ${selectedId === source.id ? "border-accent/60 bg-surface2" : ""}`}
            >
              <div className="flex gap-3">
                <Thumb src={source.thumbnail} alt={source.title} kind={source.mediaKind} className="h-16 w-24" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-1.5">
                    <span className={`rounded px-1.5 py-0.5 text-[9px] font-semibold uppercase ${source.source === "spotdl" ? "bg-ok/15 text-ok" : "bg-bad/15 text-bad"}`}>
                      {source.source === "spotdl" ? "Spotify" : "Web"}
                    </span>
                    {source.newItems > 0 && <span className="rounded-full bg-accent px-1.5 py-0.5 text-[9px] font-bold text-white">{source.newItems} nuevos</span>}
                  </div>
                  <p className="mt-1.5 line-clamp-2 text-[13px] font-semibold leading-tight">{source.title}</p>
                  <p className="mt-1 truncate text-[10.5px] text-muted">{source.uploader ?? `${source.totalItems} elementos`}</p>
                </div>
              </div>
              <div className="mt-3 flex items-center border-t border-line pt-2 text-[10.5px] text-muted">
                <span className="flex-1">{source.lastCheckedAt ? `Comprobada ${relativeDate(source.lastCheckedAt)}` : "Sin comprobar"}</span>
                {source.lastError && <AlertTriangle size={13} className="mr-1 text-bad" />}
                <span>{source.totalItems}</span>
              </div>
            </button>
          ))}
        </div>
      )}

      {selected && (
        <section className="rc-card overflow-hidden">
          <div className="flex flex-wrap items-center gap-3 border-b border-line p-4">
            <div className="min-w-0 flex-1">
              <p className="truncate text-[15px] font-semibold">{selected.title}</p>
              <p className="mt-0.5 text-[11px] text-muted">
                {selected.totalItems} elementos · {selected.mediaKind === "audio" ? "Sólo audio" : "Vídeo"}
                {selected.lastSuccessAt ? ` · Actualizada ${relativeDate(selected.lastSuccessAt)}` : ""}
              </p>
            </div>
            <Button disabled={syncing !== null} onClick={() => void syncSource(selected)}>
              <RefreshCw size={14} className={syncing === selected.id ? "animate-spin" : ""} /> Comprobar ahora
            </Button>
            <Button variant="primary" disabled={enqueueing || selected.newItems === 0} onClick={() => void downloadNew()}>
              {enqueueing ? <RefreshCw size={14} className="animate-spin" /> : <ListPlus size={14} />}
              Descargar nuevas ({selected.newItems})
            </Button>
            <IconButton title="Eliminar Fuente" onClick={() => void deleteSource(selected)}><Trash2 size={15} /></IconButton>
          </div>

          {selected.lastError && (
            <div className="m-4 flex gap-2 rounded-xl border border-bad/30 bg-bad/10 p-3 text-[11px] text-bad">
              <AlertTriangle size={15} className="shrink-0" /><span>{selected.lastError}</span>
            </div>
          )}
          {itemsError && (
            <div className="m-4 flex gap-2 rounded-xl border border-bad/30 bg-bad/10 p-3 text-[11px] text-bad">
              <AlertTriangle size={15} className="shrink-0" /><span>No se pudo cargar el contenido: {itemsError}</span>
            </div>
          )}

          <SourceProfileEditor
            key={selected.id}
            source={selected}
            onSaved={(updated) => setSources((current) => current.map((source) => source.id === updated.id ? updated : source))}
          />
          <SourceScheduleEditor
            key={`schedule-${selected.id}`}
            source={selected}
            onSaved={(updated) => setSources((current) => current.map((source) => source.id === updated.id ? updated : source))}
          />

          <div className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
            <div className="relative min-w-52 flex-1">
              <Search size={14} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted" />
              <input value={search} onChange={(event) => { setSearch(event.target.value); setLimit(100); }} placeholder="Buscar en esta Fuente…" className={`${inputClass} pl-9`} />
            </div>
            <div className="flex gap-1 overflow-x-auto">
              {([
                ["all", "Todos"], ["new", "Nuevos"], ["downloaded", "Descargados"],
                ["live", "Directos"], ["upcoming", "Estrenos"], ["seen", "Revisados"],
                ["unavailable", "No disponibles"], ["removed", "Desaparecidos"],
              ] as [Filter, string][]).map(([value, label]) => (
                <button key={value} type="button" onClick={() => { setFilter(value); setLimit(100); }} className={`shrink-0 rounded-lg px-2.5 py-1.5 text-[11px] transition ${filter === value ? "bg-accent/20 text-accent2" : "text-muted hover:bg-surface2 hover:text-ink"}`}>
                  {label}{counts[value] ? ` ${counts[value]}` : ""}
                </button>
              ))}
            </div>
          </div>

          {itemsLoading ? (
            <div className="p-4"><div className="rc-skeleton h-16 rounded-xl" /></div>
          ) : visible.length === 0 ? (
            <EmptyState icon={<Eye size={22} />} title="No hay elementos con este filtro" />
          ) : (
            <div className="divide-y divide-line">
              {visible.slice(0, limit).map((item) => {
                const meta = STATUS[item.status];
                return (
                  <div key={`${item.entry.extractor}:${item.remoteId}`} className={`flex items-center gap-3 px-4 py-2.5 ${!item.present ? "opacity-60" : ""}`}>
                    <span className="w-7 shrink-0 text-right text-[10px] tabular-nums text-muted">{item.entry.index}</span>
                    <Thumb src={item.entry.thumbnail ?? selected.thumbnail} alt={item.entry.title} kind={selected.mediaKind} badge={duration(item.entry.duration)} className="h-11 w-16" />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[12px] font-medium">{item.entry.title}</p>
                      <p className="mt-0.5 truncate text-[10.5px] text-muted">
                        {item.entry.liveStatus === "is_upcoming" && item.entry.releaseTimestamp
                          ? `Estreno ${futureDate(item.entry.releaseTimestamp)}`
                          : item.entry.uploader ?? `Descubierto ${relativeDate(item.firstSeenAt)}`}
                      </p>
                    </div>
                    {item.entry.liveStatus === "is_live" && <span className="flex items-center gap-1 rounded-md bg-bad/15 px-2 py-1 text-[9.5px] font-semibold text-bad"><Radio size={11} className="animate-pulse" /> EN DIRECTO</span>}
                    {item.entry.liveStatus === "is_upcoming" && <span className="rounded-md bg-warn/15 px-2 py-1 text-[9.5px] font-semibold text-warn">ESTRENO</span>}
                    {["was_live", "post_live"].includes(item.entry.liveStatus ?? "") && <span className="rounded-md bg-surface3 px-2 py-1 text-[9.5px] font-semibold text-muted">DIRECTO</span>}
                    <span className={`rounded-md px-2 py-1 text-[9.5px] font-semibold ${meta.cls}`}>{meta.label}</span>
                    {item.status === "downloaded" ? <CheckCircle2 size={15} className="text-ok" /> : item.status === "new" ? <Download size={15} className="text-accent2" /> : <Clock3 size={15} className="text-muted" />}
                  </div>
                );
              })}
              {visible.length > limit && (
                <div className="p-3 text-center"><Button onClick={() => setLimit((value) => value + 100)}>Mostrar 100 más</Button></div>
              )}
            </div>
          )}
        </section>
      )}
    </div>
  );
}

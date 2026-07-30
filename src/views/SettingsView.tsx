import { getVersion } from "@tauri-apps/api/app";
import { open as openDialog } from "@tauri-apps/plugin-dialog";
import {
  ArrowUpCircle,
  CheckCircle2,
  Cookie,
  Download,
  FolderOpen,
  Gauge,
  Palette,
  RefreshCw,
  Shield,
  Sparkles,
  Terminal,
  XCircle,
} from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { UpdateCard } from "../components/UpdateCard";
import { Button, Field, Select, Toggle, inputClass } from "../components/ui";
import { api } from "../lib/api";
import { useStore } from "../lib/store";
import type { PlayerOption, Settings, ToolStatus } from "../lib/types";

const SPONSOR_CATEGORIES: { value: string; label: string }[] = [
  { value: "sponsor", label: "Patrocinios" },
  { value: "selfpromo", label: "Autopromoción" },
  { value: "interaction", label: "«Suscríbete»" },
  { value: "intro", label: "Intro" },
  { value: "outro", label: "Outro" },
  { value: "preview", label: "Resumen / recap" },
  { value: "music_offtopic", label: "Secciones sin música" },
  { value: "filler", label: "Relleno / tangentes" },
];

export function SettingsView() {
  const settings = useStore((s) => s.settings);
  const save = useStore((s) => s.saveSettings);
  const toast = useStore((s) => s.toast);
  const platform = useStore((s) => s.platform);
  const [tools, setTools] = useState<ToolStatus[]>([]);
  const [busy, setBusy] = useState(false);
  const [version, setVersion] = useState("");

  const [players, setPlayers] = useState<PlayerOption[]>([]);

  useEffect(() => {
    api.toolsStatus().then(setTools);
    getVersion().then(setVersion);
    api.detectPlayers().then(setPlayers);
  }, []);

  if (!settings) return null;
  const s: Settings = settings;

  async function pickDir(key: "videoDir" | "audioDir") {
    const picked = await openDialog({ directory: true, defaultPath: s[key] });
    if (typeof picked === "string") save({ [key]: picked } as Partial<Settings>);
  }

  async function pickPlayer() {
    // En Linux y macOS los ejecutables no tienen extensión (`/usr/bin/vlc`), así
    // que cualquier filtro los escondería justamente a ellos.
    const picked = await openDialog({
      multiple: false,
      filters:
        platform === "windows"
          ? [{ name: "Programas", extensions: ["exe", "bat", "cmd"] }]
          : undefined,
    });
    if (typeof picked === "string") save({ externalPlayer: picked });
  }

  const playerPlaceholder =
    platform === "windows"
      ? "p. ej. C:\\Program Files\\VideoLAN\\VLC\\vlc.exe"
      : platform === "macos"
        ? "p. ej. /Applications/VLC.app/Contents/MacOS/VLC"
        : "p. ej. /usr/bin/vlc o /usr/bin/mpv";

  function toggleCategory(list: "sponsorblockRemove" | "sponsorblockMark", value: string) {
    const current = new Set(s[list]);
    const other = list === "sponsorblockRemove" ? "sponsorblockMark" : "sponsorblockRemove";
    const otherSet = new Set(s[other]);
    if (current.has(value)) current.delete(value);
    else {
      current.add(value);
      otherSet.delete(value);
    }
    save({ [list]: [...current], [other]: [...otherSet] } as Partial<Settings>);
  }

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-4 px-6 py-6">
      <Section icon={<FolderOpen size={16} />} title="Carpetas de destino">
        <PathRow
          label="Vídeos"
          value={s.videoDir}
          onPick={() => pickDir("videoDir")}
          onOpen={() => api.openFolder(s.videoDir)}
        />
        <PathRow
          label="Música"
          value={s.audioDir}
          onPick={() => pickDir("audioDir")}
          onOpen={() => api.openFolder(s.audioDir)}
        />
        <Toggle
          checked={s.playlistSubfolder}
          onChange={(v) => save({ playlistSubfolder: v })}
          label="Carpeta propia para cada playlist"
          hint="Mantiene ordenado el destino en lugar de mezclar cientos de archivos."
        />
        <Field
          label="Plantilla de nombre"
          hint="Sintaxis de yt-dlp. Por ejemplo: %(playlist_index)s - %(title)s.%(ext)s"
        >
          <input
            className={inputClass}
            value={s.outputTemplate}
            onChange={(e) => save({ outputTemplate: e.target.value })}
            spellCheck={false}
          />
        </Field>
      </Section>

      <Section icon={<Gauge size={16} />} title="Descargas">
        <Field label={`Descargas simultáneas: ${s.concurrency}`}>
          <input
            type="range"
            min={1}
            max={8}
            value={s.concurrency}
            onChange={(e) => save({ concurrency: Number(e.target.value) })}
            className="w-full accent-[var(--rc-accent)]"
          />
        </Field>

        <Field
          label="Cuando el archivo ya existe"
          hint="También puedes decidirlo elemento por elemento con clic derecho antes de descargar."
        >
          <Select
            value={s.duplicatePolicy}
            onChange={(v) => save({ duplicatePolicy: v as Settings["duplicatePolicy"] })}
            options={[
              { value: "skip", label: "Omitirlo (recomendado)" },
              { value: "overwrite", label: "Volver a descargar y reemplazar" },
              { value: "ask", label: "Preguntarme cada vez" },
            ]}
          />
        </Field>

        <Toggle
          checked={s.useArchive}
          onChange={(v) => save({ useArchive: v })}
          label="Archivo histórico"
          hint="Recuerda todo lo descargado alguna vez, aunque borres el archivo. Útil para sincronizar un canal."
        />

        <div className="grid grid-cols-2 gap-3">
          <Field label="Reintentos por fallo">
            <input
              type="number"
              min={0}
              max={50}
              className={inputClass}
              value={s.retries}
              onChange={(e) => save({ retries: Number(e.target.value) })}
            />
          </Field>
          <Field label="Límite de velocidad" hint="Ej. 2M. Vacío = sin límite.">
            <input
              className={inputClass}
              value={s.rateLimit ?? ""}
              placeholder="sin límite"
              onChange={(e) => save({ rateLimit: e.target.value || null })}
            />
          </Field>
        </div>
      </Section>

      <Section icon={<Sparkles size={16} />} title="Calidad y formato">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Calidad de vídeo">
            <Select
              value={s.videoQuality}
              onChange={(v) => save({ videoQuality: v })}
              options={[
                { value: "best", label: "Máxima disponible" },
                { value: "2160", label: "4K · 2160p" },
                { value: "1440", label: "2K · 1440p" },
                { value: "1080", label: "Full HD · 1080p" },
                { value: "720", label: "HD · 720p" },
                { value: "480", label: "SD · 480p" },
              ]}
            />
          </Field>
          <Field label="Contenedor">
            <Select
              value={s.videoContainer}
              onChange={(v) => save({ videoContainer: v })}
              options={[
                { value: "mp4", label: "MP4 (compatible)" },
                { value: "mkv", label: "MKV (sin pérdidas)" },
                { value: "original", label: "Dejar el original" },
              ]}
            />
          </Field>
          <Field label="Formato de audio">
            <Select
              value={s.audioFormat}
              onChange={(v) => save({ audioFormat: v })}
              options={[
                { value: "mp3", label: "MP3" },
                { value: "m4a", label: "M4A / AAC" },
                { value: "opus", label: "Opus" },
                { value: "flac", label: "FLAC (sin pérdidas)" },
                { value: "wav", label: "WAV" },
              ]}
            />
          </Field>
          <Field label="Bitrate de audio">
            <Select
              value={s.audioBitrate}
              onChange={(v) => save({ audioBitrate: v })}
              options={["320", "256", "192", "128", "96"].map((b) => ({
                value: b,
                label: `${b} kbps`,
              }))}
            />
          </Field>
        </div>

        <Toggle
          checked={s.embedThumbnail}
          onChange={(v) => save({ embedThumbnail: v })}
          label="Incrustar miniatura / carátula"
        />
        <Toggle
          checked={s.embedMetadata}
          onChange={(v) => save({ embedMetadata: v })}
          label="Incrustar metadatos (título, artista, año…)"
        />
        <Toggle
          checked={s.embedChapters}
          onChange={(v) => save({ embedChapters: v })}
          label="Incrustar capítulos"
        />
        <Toggle
          checked={s.writeSubtitles}
          onChange={(v) => save({ writeSubtitles: v })}
          label="Descargar subtítulos"
          hint="Incluye los automáticos cuando no hay manuales."
        />
        {s.writeSubtitles && (
          <div className="grid grid-cols-2 gap-3 pl-1">
            <Field label="Idiomas" hint="Códigos separados por comas.">
              <input
                className={inputClass}
                value={s.subtitleLangs}
                onChange={(e) => save({ subtitleLangs: e.target.value })}
              />
            </Field>
            <div className="self-end pb-1">
              <Toggle
                checked={s.embedSubtitles}
                onChange={(v) => save({ embedSubtitles: v })}
                label="Incrustarlos en el vídeo"
              />
            </div>
          </div>
        )}
      </Section>

      <Section icon={<Shield size={16} />} title="SponsorBlock">
        <Toggle
          checked={s.sponsorblock}
          onChange={(v) => save({ sponsorblock: v })}
          label="Usar SponsorBlock"
          hint="Corta o marca los segmentos que la comunidad ha señalado."
        />
        {s.sponsorblock && (
          <div className="flex flex-col gap-2 pt-1">
            <p className="text-[12px] text-muted">
              Un clic marca la categoría como capítulo; dos clics la eliminan del archivo.
            </p>
            <div className="flex flex-wrap gap-1.5">
              {SPONSOR_CATEGORIES.map((c) => {
                const removed = s.sponsorblockRemove.includes(c.value);
                const marked = s.sponsorblockMark.includes(c.value);
                return (
                  <button
                    key={c.value}
                    onClick={() =>
                      marked
                        ? toggleCategory("sponsorblockRemove", c.value)
                        : toggleCategory("sponsorblockMark", c.value)
                    }
                    onContextMenu={(e) => {
                      e.preventDefault();
                      if (removed) toggleCategory("sponsorblockRemove", c.value);
                      if (marked) toggleCategory("sponsorblockMark", c.value);
                    }}
                    className={`rc-ring rounded-lg border px-2.5 py-1 text-[12px] transition
                      ${
                        removed
                          ? "border-bad/40 bg-bad/12 text-bad"
                          : marked
                            ? "border-warn/40 bg-warn/12 text-warn"
                            : "border-line text-muted hover:text-ink"
                      }`}
                  >
                    {c.label}
                    {removed && " · cortar"}
                    {marked && " · marcar"}
                  </button>
                );
              })}
            </div>
          </div>
        )}
      </Section>

      <Section icon={<Cookie size={16} />} title="Acceso a contenido restringido">
        <Field
          label="Tomar cookies del navegador"
          hint="Necesario para vídeos con edad, privados o de miembros. Cierra el navegador antes de descargar."
        >
          <Select
            value={s.cookiesFromBrowser ?? ""}
            onChange={(v) => save({ cookiesFromBrowser: v || null })}
            options={[
              { value: "", label: "No usar cookies" },
              { value: "chrome", label: "Chrome" },
              { value: "edge", label: "Edge" },
              { value: "firefox", label: "Firefox" },
              { value: "brave", label: "Brave" },
              { value: "opera", label: "Opera" },
              { value: "vivaldi", label: "Vivaldi" },
              { value: "chromium", label: "Chromium" },
            ]}
          />
        </Field>
        <Field label="Proxy" hint="Ej. socks5://127.0.0.1:1080. Útil para bloqueos por región.">
          <input
            className={inputClass}
            value={s.proxy ?? ""}
            placeholder="sin proxy"
            onChange={(e) => save({ proxy: e.target.value || null })}
          />
        </Field>
      </Section>

      <Section icon={<Terminal size={16} />} title="Herramientas">
        <div className="flex flex-col gap-2">
          {tools.map((t) => (
            <div
              key={t.name}
              className="flex items-center gap-3 rounded-xl border border-line bg-surface2 px-3 py-2"
            >
              {t.found ? (
                <CheckCircle2 size={16} className="shrink-0 text-ok" />
              ) : (
                <XCircle size={16} className="shrink-0 text-bad" />
              )}
              <div className="min-w-0 flex-1">
                <p className="text-[13px] font-medium">
                  {t.name}
                  {t.managed && (
                    <span className="ml-2 rounded bg-accent/15 px-1.5 py-0.5 text-[10px] text-accent2">
                      gestionado por Recodio
                    </span>
                  )}
                </p>
                <p className="truncate text-[11px] text-muted">
                  {t.found ? (t.version ?? t.path) : "No encontrado en el sistema"}
                </p>
              </div>
            </div>
          ))}
        </div>
        <div className="flex flex-wrap gap-2">
          {["yt-dlp", "spotdl", "ffmpeg"].map((name) => (
            <Button
              key={name}
              disabled={busy}
              onClick={async () => {
                setBusy(true);
                try {
                  await api.toolsInstall(name);
                  const actualizadas = await api.toolsStatus();
                  setTools(actualizadas);
                  useStore.setState({ tools: actualizadas });
                  toast("success", `${name} instalado en la carpeta de Recodio`);
                } catch (e) {
                  toast("error", String(e));
                } finally {
                  setBusy(false);
                }
              }}
            >
              <Download size={14} /> {name}
            </Button>
          ))}
          <Button
            disabled={busy}
            onClick={async () => {
              setBusy(true);
              try {
                const out = await api.toolsUpdateYtdlp();
                setTools(await api.toolsStatus());
                toast("success", out.split("\n").pop() || "yt-dlp actualizado");
              } catch (e) {
                toast("error", String(e));
              } finally {
                setBusy(false);
              }
            }}
          >
            <RefreshCw size={14} /> Actualizar yt-dlp
          </Button>
        </div>
      </Section>

      <Section icon={<ArrowUpCircle size={16} />} title="Actualizaciones">
        <UpdateCard version={version} />
      </Section>

      <Section icon={<Palette size={16} />} title="Apariencia y reproducción">
        <Field label="Tema">
          <Select
            value={s.theme}
            onChange={(v) => save({ theme: v })}
            options={[
              { value: "dark", label: "Oscuro" },
              { value: "light", label: "Claro" },
            ]}
          />
        </Field>
        <Field
          label="Reproductor externo"
          hint="Déjalo vacío para usar el programa predeterminado del sistema."
        >
          <div className="flex gap-2">
            <input
              className={inputClass}
              value={s.externalPlayer ?? ""}
              placeholder={playerPlaceholder}
              onChange={(e) => save({ externalPlayer: e.target.value || null })}
              spellCheck={false}
            />
            <Button onClick={pickPlayer}>Elegir…</Button>
          </div>

          {players.length > 0 && (
            <div className="mt-2 flex flex-wrap items-center gap-1.5">
              <span className="text-[11.5px] text-muted">Detectados:</span>
              {players.map((p) => (
                <button
                  key={p.path}
                  type="button"
                  onClick={() => save({ externalPlayer: p.path })}
                  title={p.path}
                  className={`rounded-lg px-2 py-1 text-[11.5px] transition ${
                    s.externalPlayer === p.path
                      ? "bg-accent/20 text-accent"
                      : "bg-surface3 text-muted hover:text-fg"
                  }`}
                >
                  {p.name}
                </button>
              ))}
              <button
                type="button"
                onClick={() => save({ externalPlayer: null })}
                className={`rounded-lg px-2 py-1 text-[11.5px] transition ${
                  !s.externalPlayer
                    ? "bg-accent/20 text-accent"
                    : "bg-surface3 text-muted hover:text-fg"
                }`}
              >
                El del sistema
              </button>
            </div>
          )}

          {!s.externalPlayer && (
            <p className="mt-1.5 text-[11.5px] leading-snug text-muted">
              Sin reproductor elegido se usa el que tenga asociado el sistema, que
              puede ser distinto para cada formato: es fácil acabar con los MP3
              abriéndose en uno y los MP4 en otro.
            </p>
          )}
        </Field>
      </Section>
    </div>
  );
}

function Section({
  icon,
  title,
  children,
}: {
  icon: ReactNode;
  title: string;
  children: ReactNode;
}) {
  return (
    <section className="rc-card p-4">
      <h2 className="mb-3 flex items-center gap-2 text-[14px] font-semibold">
        <span className="text-accent2">{icon}</span>
        {title}
      </h2>
      <div className="flex flex-col gap-3">{children}</div>
    </section>
  );
}

function PathRow({
  label,
  value,
  onPick,
  onOpen,
}: {
  label: string;
  value: string;
  onPick: () => void;
  onOpen: () => void;
}) {
  return (
    <Field label={label}>
      <div className="flex gap-2">
        <button
          onClick={onPick}
          title={value}
          className={`${inputClass} flex items-center gap-2 text-left`}
        >
          <FolderOpen size={14} className="shrink-0 text-muted" />
          <span className="truncate">{value}</span>
        </button>
        <Button onClick={onOpen}>Abrir</Button>
      </div>
    </Field>
  );
}

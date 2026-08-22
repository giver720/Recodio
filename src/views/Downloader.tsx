import { open as openDialog } from "@tauri-apps/plugin-dialog";
import {
  AlertTriangle,
  CheckCircle2,
  CheckCheck,
  ClipboardPaste,
  Copy,
  Download,
  ExternalLink,
  FileText,
  Film,
  FolderOpen,
  History,
  Heart,
  Link2,
  ListChecks,
  ListVideo,
  LogIn,
  LogOut,
  LoaderCircle,
  Music,
  Pencil,
  Play,
  Plus,
  Radio,
  RotateCcw,
  Search,
  Sparkles,
  Trash2,
  UserRound,
  X,
} from "lucide-react";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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
import type {
  AnalyzeResult,
  Entry,
  Kind,
  SpotifyPlaylist,
  SpotifyProfile,
  YoutubeAccount,
  YoutubeSessionStatus,
} from "../lib/types";

const WEB_URL = /^(?:https?:\/\/|www\.)/i;
const RESULT_PAGE_SIZE = 100;

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

const YOUTUBE_BROWSERS = [
  { value: "", label: "Elegir navegador" },
  { value: "chrome", label: "Chrome" },
  { value: "edge", label: "Edge" },
  { value: "firefox", label: "Firefox" },
  { value: "brave", label: "Brave" },
  { value: "opera", label: "Opera" },
  { value: "vivaldi", label: "Vivaldi" },
  { value: "chromium", label: "Chromium" },
];

const YOUTUBE_FEEDS = [
  { target: ":ytrec", label: "Recomendados", icon: Sparkles },
  { target: ":ytsubs", label: "Suscripciones", icon: Radio },
  { target: ":ytfav", label: "Me gusta", icon: Heart },
  { target: ":ytwatchlater", label: "Ver más tarde", icon: History },
  { target: ":ythis", label: "Historial", icon: History },
  {
    target: "https://www.youtube.com/feed/playlists",
    label: "Mis playlists",
    icon: ListVideo,
  },
];

type YoutubeAccountDraft =
  | { mode: "import"; source: string; name: string }
  | { mode: "rename"; id: string; name: string };

const normalizedPath = (path: string) => path.replace(/\\/g, "/").toLowerCase();

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
  const [listPage, setListPage] = useState(0);
  const [enqueueing, setEnqueueing] = useState(false);
  const [searchedQuery, setSearchedQuery] = useState<string | null>(null);
  const [youtubeStatus, setYoutubeStatus] = useState<YoutubeSessionStatus | null>(null);
  const [checkingYoutube, setCheckingYoutube] = useState(false);
  const [youtubeAccounts, setYoutubeAccounts] = useState<YoutubeAccount[]>([]);
  const [youtubeAccountDraft, setYoutubeAccountDraft] = useState<YoutubeAccountDraft | null>(null);
  const [spotifyProfile, setSpotifyProfile] = useState<SpotifyProfile | null>(null);
  const [spotifyMessage, setSpotifyMessage] = useState("Conecta tu cuenta para ver tu música de Spotify");
  const [spotifyPlaylists, setSpotifyPlaylists] = useState<SpotifyPlaylist[]>([]);
  const [spotifyLoading, setSpotifyLoading] = useState(false);
  const [showSpotifyPlaylists, setShowSpotifyPlaylists] = useState(false);
  const cookieSpec = settings?.cookiesFromBrowser ?? "";
  const cookieFile = settings?.cookiesFile ?? "";
  const cookieFileName = cookieFile.split(/[\\/]/).pop() ?? "cookies.txt";
  const [cookieBrowser = "", ...profileParts] = cookieSpec.split(":");
  const cookieProfile = profileParts.join(":");
  const activeYoutubeAccount = youtubeAccounts.find(
    (account) => normalizedPath(account.cookiesFile) === normalizedPath(cookieFile),
  );
  const isEntryDone = useCallback(
    (entry: Entry) => Boolean(kind === "audio" ? entry.existingAudio : entry.existingVideo),
    [kind],
  );

  // El análisis en curso, para que los envíos tardíos no se cuelen en otro.
  const analisisActivo = useRef<string | null>(null);
  const analysisGeneration = useRef(0);
  const resultRef = useRef<AnalyzeResult | null>(null);
  const selectionDefaultsRef = useRef({
    kind,
    duplicatePolicy: settings?.duplicatePolicy ?? "skip",
  });

  useEffect(() => {
    resultRef.current = result;
  }, [result]);

  useEffect(() => {
    selectionDefaultsRef.current = {
      kind,
      duplicatePolicy: settings?.duplicatePolicy ?? "skip",
    };
  }, [kind, settings?.duplicatePolicy]);

  useEffect(() => {
    const mas = listen<[string, Entry[], boolean]>("analyze-more", (ev) => {
      const [key, nuevas] = ev.payload;
      if (key !== analisisActivo.current) return; // De un análisis ya descartado.
      const prev = resultRef.current;
      if (!prev) return;
      const vistos = new Set(prev.entries.map((e) => e.sourceId));
      const añadir = nuevas.filter((e) => !vistos.has(e.sourceId));
      const next = { ...prev, entries: [...prev.entries, ...añadir], partial: false };
      resultRef.current = next;
      setResult(next);

      // Las primeras cien pistas ya se marcaban con la política elegida. Las
      // que llegan por detrás deben seguir la misma regla; de lo contrario una
      // playlist de 2.000 canciones parecía completa, pero solo descargaba 100.
      const defaults = selectionDefaultsRef.current;
      const resultKind: Kind = prev.source === "spotdl" ? "audio" : defaults.kind;
      setSelected((current) => {
        const selectedNext = new Set(current);
        for (const entry of añadir) {
          if (entry.unavailable) continue;
          const existing = resultKind === "audio" ? entry.existingAudio : entry.existingVideo;
          if (!existing || defaults.duplicatePolicy === "overwrite") selectedNext.add(entry.id);
        }
        return selectedNext;
      });
      if (defaults.duplicatePolicy === "overwrite") {
        setOverwrite((current) => {
          const overwriteNext = new Set(current);
          for (const entry of añadir) {
            const existing = resultKind === "audio" ? entry.existingAudio : entry.existingVideo;
            if (!entry.unavailable && existing) overwriteNext.add(entry.id);
          }
          return overwriteNext;
        });
      }
    });
    const fallo = listen<[string, string]>("analyze-failed", (ev) => {
      const [key, mensaje] = ev.payload;
      if (key !== analisisActivo.current) return;
      const prev = resultRef.current;
      if (prev) {
        const next = { ...prev, partial: false };
        resultRef.current = next;
        setResult(next);
      }
      toast("error", `No se pudo completar la lista: ${mensaje}`);
    });
    return () => {
      mas.then((f) => f());
      fallo.then((f) => f());
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    // Cambiar de navegador o perfil invalida la comprobación anterior.
    setYoutubeStatus(null);
  }, [cookieSpec, cookieFile]);

  useEffect(() => {
    let active = true;
    api
      .youtubeAccounts()
      .then((accounts) => {
        if (active) setYoutubeAccounts(accounts);
      })
      .catch((error) => {
        if (active) toast("error", `No se pudieron cargar las cuentas de YouTube: ${error}`);
      });
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    let active = true;
    api
      .spotifyStatus()
      .then(async (status) => {
        if (!active) return;
        setSpotifyMessage(status.message);
        setSpotifyProfile(status.profile);
        if (status.connected) {
          try {
            const playlists = await api.spotifyPlaylists();
            if (active) setSpotifyPlaylists(playlists);
          } catch {
            // El perfil sigue siendo útil aunque la lista falle temporalmente.
          }
        }
      })
      .catch((error) => {
        if (active) setSpotifyMessage(String(error));
      });
    return () => {
      active = false;
    };
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

  async function analyze(
    refresh = false,
    value = url,
    feedLabel: string | null = null,
  ) {
    const generation = ++analysisGeneration.current;
    const parsed = inputTargets(value);
    const targets = feedLabel ? [value] : parsed.targets;
    const query = feedLabel ?? parsed.query;
    if (targets.length === 0) return;

    setAnalyzing(true);
    setStartedAt(Date.now());
    resultRef.current = null;
    setResult(null);
    try {
      const results: AnalyzeResult[] = [];
      for (const u of targets) {
        results.push(await api.analyzeUrl(u, refresh));
        if (generation !== analysisGeneration.current) return;
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
      resultRef.current = merged;
      setResult(merged);
      setSearchedQuery(query);
      applyDefaults(merged, nextKind);
      setDestDir(null);
      setFilter("");
      setListPage(0);
    } catch (e) {
      if (generation === analysisGeneration.current) toast("error", String(e));
    } finally {
      if (generation === analysisGeneration.current) setAnalyzing(false);
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

  function setYoutubeBrowser(browser: string, profile = cookieProfile) {
    const spec = browser ? `${browser}${profile.trim() ? `:${profile.trim()}` : ""}` : null;
    void saveSettings({ cookiesFromBrowser: spec, cookiesFile: null });
    clearYoutubeResult();
  }

  async function importYoutubeCookies() {
    try {
      const picked = await openDialog({
        multiple: false,
        filters: [{ name: "Cookies de YouTube", extensions: ["txt"] }],
      });
      if (typeof picked !== "string") return;
      let number = youtubeAccounts.length + 1;
      while (youtubeAccounts.some((account) => account.name === `Cuenta ${number}`)) number += 1;
      setYoutubeAccountDraft({
        mode: "import",
        source: picked,
        name: `Cuenta ${number}`,
      });
    } catch (e) {
      toast("error", String(e));
    }
  }

  function clearYoutubeResult() {
    analysisGeneration.current += 1;
    analisisActivo.current = null;
    setAnalyzing(false);
    resultRef.current = null;
    setResult(null);
    setSelected(new Set());
    setSearchedQuery(null);
    setListPage(0);
  }

  async function saveYoutubeAccountDraft() {
    if (!youtubeAccountDraft?.name.trim()) return;
    try {
      if (youtubeAccountDraft.mode === "import") {
        const account = await api.youtubeImportCookies(
          youtubeAccountDraft.source,
          youtubeAccountDraft.name,
        );
        setYoutubeAccounts((accounts) => [...accounts, account]);
        await saveSettings({ cookiesFromBrowser: null, cookiesFile: account.cookiesFile });
        clearYoutubeResult();
        toast("success", `${account.name} quedó activa; pulsa Comprobar`);
      } else {
        const account = await api.youtubeRenameAccount(
          youtubeAccountDraft.id,
          youtubeAccountDraft.name,
        );
        setYoutubeAccounts((accounts) =>
          accounts.map((current) => (current.id === account.id ? account : current)),
        );
        toast("success", `Cuenta renombrada como ${account.name}`);
      }
      setYoutubeAccountDraft(null);
    } catch (e) {
      toast("error", String(e));
    }
  }

  async function activateYoutubeAccount(account: YoutubeAccount) {
    try {
      await saveSettings({ cookiesFromBrowser: null, cookiesFile: account.cookiesFile });
      clearYoutubeResult();
      toast("success", `${account.name} es ahora la cuenta activa; pulsa Comprobar`);
    } catch (e) {
      toast("error", String(e));
    }
  }

  async function deleteYoutubeAccount(account: YoutubeAccount) {
    if (!window.confirm(`¿Eliminar «${account.name}» de Recodio?`)) return;
    try {
      const wasActive = activeYoutubeAccount?.id === account.id;
      await api.youtubeDeleteAccount(account.id);
      setYoutubeAccounts((accounts) => accounts.filter((current) => current.id !== account.id));
      if (wasActive) {
        await saveSettings({ cookiesFromBrowser: null, cookiesFile: null });
        clearYoutubeResult();
      }
      if (youtubeAccountDraft?.mode === "rename" && youtubeAccountDraft.id === account.id) {
        setYoutubeAccountDraft(null);
      }
      toast("success", `${account.name} se eliminó de Recodio`);
    } catch (e) {
      toast("error", String(e));
    }
  }

  async function openYoutubeLogin() {
    try {
      await api.youtubeOpenLogin(cookieBrowser || null);
      toast("info", "Inicia sesión en YouTube y vuelve a Recodio cuando termines");
    } catch (e) {
      toast("error", String(e));
    }
  }

  async function checkYoutube() {
    if (!cookieSpec && !cookieFile) {
      toast("info", "Elige un navegador o importa un archivo cookies.txt");
      return;
    }
    setCheckingYoutube(true);
    setYoutubeStatus(null);
    try {
      const status = await api.youtubeSessionCheck(cookieSpec || null, cookieFile || null);
      setYoutubeStatus(status);
      toast(status.connected ? "success" : "error", status.message);
    } catch (e) {
      const status = { connected: false, message: String(e) };
      setYoutubeStatus(status);
      toast("error", status.message);
    } finally {
      setCheckingYoutube(false);
    }
  }

  async function connectSpotify() {
    setSpotifyLoading(true);
    setSpotifyMessage("Completa el acceso en tu navegador…");
    try {
      const profile = await api.spotifyLogin();
      setSpotifyProfile(profile);
      setSpotifyMessage("Sesión de Spotify activa");
      setSpotifyPlaylists(await api.spotifyPlaylists());
      toast("success", `Spotify conectado como ${profile.displayName}`);
    } catch (e) {
      const message = String(e);
      setSpotifyMessage(message);
      toast("error", message);
    } finally {
      setSpotifyLoading(false);
    }
  }

  async function disconnectSpotify() {
    try {
      await api.spotifyLogout();
      setSpotifyProfile(null);
      setSpotifyPlaylists([]);
      setShowSpotifyPlaylists(false);
      setSpotifyMessage("Conecta tu cuenta para ver tu música de Spotify");
      toast("success", "Sesión de Spotify cerrada");
    } catch (e) {
      toast("error", String(e));
    }
  }

  function showSpotifyResult(next: AnalyzeResult, label: string) {
    if (next.entries.length === 0) {
      toast("info", `${label} no contiene canciones disponibles`);
      return;
    }
    setKind("audio");
    analisisActivo.current = null;
    resultRef.current = next;
    setResult(next);
    setSearchedQuery(label);
    applyDefaults(next, "audio");
    setDestDir(null);
    setFilter("");
    setListPage(0);
    document.querySelector("[data-download-results]")?.scrollIntoView({ behavior: "smooth" });
  }

  async function openSpotifyCollection(
    collection: "saved" | "top" | "recent",
    label: string,
  ) {
    setSpotifyLoading(true);
    try {
      showSpotifyResult(await api.spotifyCollection(collection), label);
    } catch (e) {
      toast("error", String(e));
    } finally {
      setSpotifyLoading(false);
    }
  }

  async function openSpotifyPlaylist(playlist: SpotifyPlaylist) {
    setSpotifyLoading(true);
    try {
      showSpotifyResult(await api.spotifyPlaylist(playlist), playlist.name);
    } catch {
      // En modo desarrollo Spotify solo entrega el contenido de playlists
      // propias o colaborativas. Las públicas seguidas aún pueden leerse por
      // la ruta normal de Recodio.
      await analyze(false, playlist.externalUrl, playlist.name);
    } finally {
      setSpotifyLoading(false);
    }
  }

  function openYoutubeFeed(target: string, label: string) {
    if (!youtubeStatus?.connected) {
      toast("info", "Comprueba primero la sesión de YouTube");
      return;
    }
    setUrl(label);
    analyze(false, target, label);
  }

  async function pickFolder() {
    const picked = await openDialog({
      directory: true,
      defaultPath: destDir ?? (kind === "audio" ? settings?.audioDir : settings?.videoDir),
    });
    if (typeof picked === "string") setDestDir(picked);
  }

  async function start() {
    if (!result || enqueueing) return;
    const entries = result.entries.filter((e) => selected.has(e.id) && !e.unavailable);
    if (entries.length === 0) {
      toast("info", "No hay nada seleccionado");
      return;
    }
    setEnqueueing(true);
    try {
      // Deja que React pinte el estado antes de serializar playlists de miles
      // de canciones para el proceso nativo.
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      const n = await api.enqueue({
        entries,
        kind,
        source: result.source,
        destDir,
        playlist: result.isPlaylist ? result.playlist : null,
        overwriteIds: [...overwrite],
      });
      toast("success", `${n} ${n === 1 ? "elemento añadido" : "elementos añadidos"} a la cola`);
      analisisActivo.current = null;
      resultRef.current = null;
      setResult(null);
      setUrl("");
      setSearchedQuery(null);
      onQueued();
    } catch (e) {
      toast("error", String(e));
    } finally {
      setEnqueueing(false);
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

  const pageCount = Math.max(1, Math.ceil(visible.length / RESULT_PAGE_SIZE));
  const safePage = Math.min(listPage, pageCount - 1);
  const pageFrom = safePage * RESULT_PAGE_SIZE;
  const pageEntries = visible.slice(pageFrom, pageFrom + RESULT_PAGE_SIZE);
  const selectedDownloadable = useMemo(
    () =>
      result?.entries.filter((entry) => selected.has(entry.id) && !entry.unavailable).length ?? 0,
    [result, selected],
  );

  useEffect(() => {
    if (listPage >= pageCount) setListPage(pageCount - 1);
  }, [listPage, pageCount]);

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

      {/* ---- Cuenta de YouTube ---- */}
      <div className="rc-card overflow-hidden">
        <div className="flex flex-wrap items-center gap-3 p-4">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-accent/12 text-accent2">
            <UserRound size={19} />
          </div>
          <div className="min-w-48 flex-1">
            <p className="text-[13px] font-semibold">
              {youtubeStatus?.connected
                ? activeYoutubeAccount
                  ? `YouTube · ${activeYoutubeAccount.name}`
                  : "YouTube conectado"
                : cookieSpec || cookieFile
                  ? "Cuenta de YouTube sin comprobar"
                  : "Conecta tu cuenta de YouTube"}
            </p>
            <p className="text-[11.5px] leading-snug text-muted">
              {youtubeStatus
                ? youtubeStatus.message
                : activeYoutubeAccount
                  ? `Cuenta activa: ${activeYoutubeAccount.name}.`
                  : cookieFile
                  ? `Archivo protegido en Recodio: ${cookieFileName}.`
                  : cookieSpec
                  ? `Preparado para comprobar ${cookieBrowser}${cookieProfile ? ` · perfil ${cookieProfile}` : ""}.`
                : "Recodio reutiliza la sesión de tu navegador; nunca pide ni guarda tu contraseña."}
            </p>
          </div>
          <div className="w-40">
            <Select
              value={cookieBrowser}
              onChange={(browser) => setYoutubeBrowser(browser)}
              options={YOUTUBE_BROWSERS}
            />
          </div>
          {cookieBrowser && (
            <input
              value={cookieProfile}
              onChange={(e) => setYoutubeBrowser(cookieBrowser, e.target.value)}
              placeholder="Perfil (opcional)"
              title="Ejemplo: Default o Profile 1"
              className={`${inputClass} w-40`}
              spellCheck={false}
            />
          )}
          <Button onClick={openYoutubeLogin}>
            <LogIn size={14} /> {cookieSpec || cookieFile ? "Abrir otra cuenta" : "Iniciar sesión"}
          </Button>
          <Button onClick={importYoutubeCookies} title="Alternativa para Brave, Chrome y Edge en Windows">
            <Plus size={14} /> Añadir cuenta
          </Button>
          {(cookieSpec || cookieFile) && (
            <Button
              variant={youtubeStatus?.connected ? "soft" : "primary"}
              onClick={checkYoutube}
              disabled={checkingYoutube}
            >
              {checkingYoutube ? (
                <LoaderCircle size={14} className="animate-spin" />
              ) : (
                <CheckCircle2 size={14} />
              )}
              {checkingYoutube ? "Comprobando…" : "Comprobar"}
            </Button>
          )}
        </div>

        {(youtubeAccounts.length > 0 || youtubeAccountDraft) && (
          <div className="border-t border-line bg-surface2/25 px-4 py-3">
            <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2">
              <p className="text-[12px] font-semibold">Cuentas guardadas</p>
              <p className="text-[11px] text-muted">
                Cambiar de cuenta también separa sus recomendados, historial y playlists.
              </p>
            </div>
            <div className="flex flex-wrap gap-2">
              {youtubeAccounts.map((account) => {
                const active = activeYoutubeAccount?.id === account.id;
                return (
                  <div
                    key={account.id}
                    className={`flex items-center rounded-xl border transition ${
                      active
                        ? "border-accent/55 bg-accent/10 text-ink"
                        : "border-line bg-surface2 text-muted hover:text-ink"
                    }`}
                  >
                    <button
                      type="button"
                      onClick={() => activateYoutubeAccount(account)}
                      disabled={checkingYoutube}
                      className="rc-ring flex items-center gap-2 rounded-l-xl py-2 pl-3 pr-2 text-[12px] font-medium disabled:opacity-45"
                      title={`Usar ${account.name}`}
                    >
                      {active ? <CheckCircle2 size={14} className="text-accent2" /> : <UserRound size={14} />}
                      {account.name}
                    </button>
                    <IconButton
                      title={`Renombrar ${account.name}`}
                      onClick={() =>
                        setYoutubeAccountDraft({ mode: "rename", id: account.id, name: account.name })
                      }
                      className="h-7 w-7"
                    >
                      <Pencil size={12} />
                    </IconButton>
                    <IconButton
                      title={`Eliminar ${account.name}`}
                      onClick={() => deleteYoutubeAccount(account)}
                      className="mr-1 h-7 w-7 hover:text-bad"
                    >
                      <Trash2 size={12} />
                    </IconButton>
                  </div>
                );
              })}
            </div>

            {youtubeAccountDraft && (
              <div className="mt-3 flex flex-wrap items-center gap-2 rounded-xl border border-accent/30 bg-accent/8 p-2.5">
                <FileText size={15} className="ml-1 shrink-0 text-accent2" />
                <span className="text-[12px] font-medium">
                  {youtubeAccountDraft.mode === "import" ? "Nombre de la nueva cuenta" : "Nuevo nombre"}
                </span>
                <input
                  autoFocus
                  value={youtubeAccountDraft.name}
                  maxLength={60}
                  onChange={(event) =>
                    setYoutubeAccountDraft({ ...youtubeAccountDraft, name: event.target.value })
                  }
                  onKeyDown={(event) => {
                    if (event.key === "Enter") void saveYoutubeAccountDraft();
                    if (event.key === "Escape") setYoutubeAccountDraft(null);
                  }}
                  placeholder="Ej. Personal, Música o Trabajo"
                  className={`${inputClass} min-w-52 flex-1`}
                />
                <Button
                  variant="primary"
                  onClick={saveYoutubeAccountDraft}
                  disabled={!youtubeAccountDraft.name.trim()}
                >
                  {youtubeAccountDraft.mode === "import" ? "Guardar y usar" : "Guardar"}
                </Button>
                <IconButton title="Cancelar" onClick={() => setYoutubeAccountDraft(null)}>
                  <X size={14} />
                </IconButton>
              </div>
            )}
          </div>
        )}

        {youtubeStatus?.connected && (
          <div className="flex flex-wrap gap-2 border-t border-line bg-surface2/40 px-4 py-3">
            {YOUTUBE_FEEDS.map(({ target, label, icon: Icon }) => (
              <Button key={target} onClick={() => openYoutubeFeed(target, label)}>
                <Icon size={14} /> {label}
              </Button>
            ))}
          </div>
        )}
      </div>

      {/* ---- Cuenta de Spotify ---- */}
      <div className="rc-card overflow-hidden">
        <div className="flex flex-wrap items-center gap-3 p-4">
          {spotifyProfile?.imageUrl ? (
            <img
              src={spotifyProfile.imageUrl}
              alt=""
              className="h-10 w-10 shrink-0 rounded-xl object-cover"
            />
          ) : (
            <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-[#1ed760]/12 text-[#1ed760]">
              <Music size={19} />
            </div>
          )}
          <div className="min-w-48 flex-1">
            <p className="text-[13px] font-semibold">
              {spotifyProfile ? `Spotify · ${spotifyProfile.displayName}` : "Conecta Spotify"}
            </p>
            <p className="text-[11.5px] leading-snug text-muted">{spotifyMessage}</p>
          </div>
          {spotifyProfile ? (
            <>
              {spotifyProfile.externalUrl && (
                <Button onClick={() => api.openFolder(spotifyProfile.externalUrl!)}>
                  <ExternalLink size={14} /> Perfil
                </Button>
              )}
              <Button onClick={disconnectSpotify} disabled={spotifyLoading}>
                <LogOut size={14} /> Cerrar sesión
              </Button>
            </>
          ) : (
            <Button variant="primary" onClick={connectSpotify} disabled={spotifyLoading}>
              {spotifyLoading ? (
                <LoaderCircle size={14} className="animate-spin" />
              ) : (
                <LogIn size={14} />
              )}
              {spotifyLoading ? "Esperando a Spotify…" : "Iniciar sesión"}
            </Button>
          )}
        </div>

        {spotifyProfile && (
          <div className="border-t border-line bg-surface2/40 px-4 py-3">
            <div className="flex flex-wrap gap-2">
              <Button
                onClick={() => openSpotifyCollection("saved", "Canciones que te gustan")}
                disabled={spotifyLoading}
              >
                <Heart size={14} /> Me gusta
              </Button>
              <Button
                onClick={() => openSpotifyCollection("top", "Más escuchadas para ti")}
                disabled={spotifyLoading}
              >
                <Sparkles size={14} /> Para ti
              </Button>
              <Button
                onClick={() => openSpotifyCollection("recent", "Escuchado recientemente")}
                disabled={spotifyLoading}
              >
                <History size={14} /> Recientes
              </Button>
              <Button
                variant={showSpotifyPlaylists ? "primary" : "soft"}
                onClick={() => setShowSpotifyPlaylists((show) => !show)}
                disabled={spotifyLoading}
              >
                <ListVideo size={14} /> Playlists ({spotifyPlaylists.length})
              </Button>
              {spotifyLoading && <LoaderCircle size={16} className="m-2 animate-spin text-muted" />}
            </div>

            {showSpotifyPlaylists && (
              <div className="mt-3 grid max-h-64 grid-cols-1 gap-2 overflow-y-auto pr-1 sm:grid-cols-2 lg:grid-cols-3">
                {spotifyPlaylists.map((playlist) => (
                  <button
                    key={playlist.id}
                    onClick={() => openSpotifyPlaylist(playlist)}
                    className="rc-ring flex min-w-0 items-center gap-2 rounded-xl border border-line bg-surface px-2.5 py-2 text-left transition hover:border-accent/40 hover:bg-surface2"
                  >
                    {playlist.imageUrl ? (
                      <img
                        src={playlist.imageUrl}
                        alt=""
                        className="h-9 w-9 shrink-0 rounded-lg object-cover"
                      />
                    ) : (
                      <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-[#1ed760]/10 text-[#1ed760]">
                        <ListVideo size={15} />
                      </div>
                    )}
                    <span className="min-w-0">
                      <span className="block truncate text-[12px] font-medium">{playlist.name}</span>
                      <span className="block text-[10.5px] text-muted">
                        {playlist.itemCount} canciones
                      </span>
                    </span>
                  </button>
                ))}
                {spotifyPlaylists.length === 0 && (
                  <p className="col-span-full py-3 text-center text-[12px] text-muted">
                    Esta cuenta no tiene playlists disponibles.
                  </p>
                )}
              </div>
            )}
          </div>
        )}
      </div>

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
        <div className="contents" data-download-results>
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
                    ? result.source === "spotdl"
                      ? `Spotify · ${result.entries.length} canciones`
                      : `Resultados de YouTube · ${result.entries.length}`
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
              <IconButton
                title="Descartar"
                onClick={() => {
                  analisisActivo.current = null;
                  resultRef.current = null;
                  setResult(null);
                }}
              >
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

              <Button
                variant="primary"
                onClick={start}
                disabled={enqueueing || selectedDownloadable === 0}
                className="ml-auto px-5 py-2.5"
              >
                {enqueueing ? (
                  <LoaderCircle size={15} className="animate-spin" />
                ) : (
                  <Download size={15} />
                )}
                {enqueueing
                  ? `Añadiendo ${selectedDownloadable}…`
                  : `Descargar${selectedDownloadable > 0 ? ` (${selectedDownloadable})` : ""}`}
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
              isDone={isEntryDone}
              onToggleBlock={setAll}
            />
          )}

          {result.entries.length > 1 && (
            <div className="flex flex-wrap items-center gap-2">
              <Button
                onClick={() =>
                  setAll(
                    visible.filter((entry) => !entry.unavailable).map((entry) => entry.id),
                    true,
                  )
                }
              >
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
                onChange={(e) => {
                  setFilter(e.target.value);
                  setListPage(0);
                }}
                placeholder="Filtrar…"
                className={`${inputClass} ml-auto max-w-56`}
              />
            </div>
          )}

          {visible.length > RESULT_PAGE_SIZE && (
            <div className="flex flex-wrap items-center gap-2 rounded-xl border border-line bg-surface2/45 px-3 py-2 text-[12px] text-muted">
              <span>
                Mostrando {pageFrom + 1}–{Math.min(pageFrom + RESULT_PAGE_SIZE, visible.length)} de{" "}
                {visible.length}
              </span>
              <span className="text-[11px] text-muted/80">
                La selección también incluye las canciones de otras páginas.
              </span>
              <div className="ml-auto flex items-center gap-2">
                <Button
                  onClick={() => setListPage((page) => Math.max(0, page - 1))}
                  disabled={safePage === 0}
                  className="px-3 py-1.5"
                >
                  Anterior
                </Button>
                <div className="w-36">
                  <Select
                    value={String(safePage)}
                    onChange={(value) => setListPage(Number(value))}
                    options={Array.from({ length: pageCount }, (_, page) => {
                      const from = page * RESULT_PAGE_SIZE + 1;
                      const to = Math.min((page + 1) * RESULT_PAGE_SIZE, visible.length);
                      return { value: String(page), label: `${from}–${to}` };
                    })}
                  />
                </div>
                <Button
                  onClick={() => setListPage((page) => Math.min(pageCount - 1, page + 1))}
                  disabled={safePage >= pageCount - 1}
                  className="px-3 py-1.5"
                >
                  Siguiente
                </Button>
              </div>
            </div>
          )}

          <div className="flex flex-col gap-1.5 pb-4">
            {pageEntries.map((entry) => {
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
                        onClick: () => {
                          const next = {
                            ...result,
                            entries: result.entries.filter((e) => e.id !== entry.id),
                          };
                          resultRef.current = next;
                          setResult(next);
                        },
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
        </div>
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

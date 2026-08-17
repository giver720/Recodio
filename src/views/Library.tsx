import {
  AlertTriangle,
  ChevronRight,
  Clock,
  Copy,
  Disc3,
  ExternalLink,
  FileVideo,
  Folder,
  FolderOpen,
  FolderPlus,
  FolderSearch,
  LayoutGrid,
  Library as LibraryIcon,
  ListMusic,
  Music,
  Pin,
  PinOff,
  Play,
  Plus,
  RefreshCw,
  RotateCcw,
  Rows3,
  Search,
  Trash2,
  User,
  X,
} from "lucide-react";
import { listen } from "@tauri-apps/api/event";
import { open as openDialog } from "@tauri-apps/plugin-dialog";
import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type MouseEvent,
  type ReactNode,
} from "react";
import { Thumb } from "../components/Thumb";
import { useMenu, type MenuItem } from "../components/Menu";
import { EmptyState, IconButton, inputClass } from "../components/ui";
import { api } from "../lib/api";
import { bytes, duration, folderName, folderOf, relativeDate } from "../lib/format";
import { usePlayer } from "../lib/player";
import { useStore } from "../lib/store";
import type {
  LibraryItem,
  Playlist,
  RefreshPhase,
  RepairReport,
} from "../lib/types";

type Sort = "recent" | "title" | "artist" | "duration" | "size";
type Group = "none" | "artist" | "folder";
type View = "list" | "grid";

/** Un mes: lo bastante cerca para que «reciente» signifique algo. */
const RECIENTE = 30 * 24 * 3600;

const ORDENES: { value: Sort; label: string }[] = [
  { value: "recent", label: "Más reciente" },
  { value: "title", label: "Título" },
  { value: "artist", label: "Artista" },
  { value: "duration", label: "Duración" },
  { value: "size", label: "Tamaño" },
];

type PlSort = "recent" | "title" | "count" | "manual";

const ORDENES_PL: { value: PlSort; label: string }[] = [
  { value: "recent", label: "Añadidas antes" },
  { value: "title", label: "Por título" },
  { value: "count", label: "Más canciones" },
  { value: "manual", label: "A mi manera" },
];

const AGRUPACIONES: { value: Group; label: string }[] = [
  { value: "none", label: "Sin agrupar" },
  { value: "artist", label: "Por artista" },
  { value: "folder", label: "Por carpeta" },
];

/** Las preferencias de vista sobreviven al cierre: se eligen una vez. */
function usePreferencia<T extends string>(clave: string, inicial: T) {
  const [valor, setValor] = useState<T>(
    () => (localStorage.getItem(clave) as T) ?? inicial,
  );
  useEffect(() => {
    localStorage.setItem(clave, valor);
  }, [clave, valor]);
  return [valor, setValor] as const;
}

const SIN_ARTISTA = "Sin artista";

/** Prefijo de las secciones que son una carpeta de archivos sueltos. */
const CARPETA = "loose:";

/**
 * Cuándo se rastreó por última vez sin que nadie lo pidiera.
 *
 * Vive fuera del componente porque la biblioteca se monta y se desmonta cada vez
 * que se cambia de pestaña, y rastrear en cada visita sería releer el disco
 * entero por ir y volver.
 */
let ultimoRastreoAuto = 0;
const ESPERA_RASTREO = 2 * 60 * 1000;

/** Lista de identificadores guardada entre sesiones (fijadas, orden a mano). */
function useListaGuardada(clave: string) {
  const [lista, setLista] = useState<string[]>(() => {
    try {
      const crudo = JSON.parse(localStorage.getItem(clave) ?? "[]");
      return Array.isArray(crudo) ? crudo.filter((x) => typeof x === "string") : [];
    } catch {
      return [];
    }
  });
  useEffect(() => {
    localStorage.setItem(clave, JSON.stringify(lista));
  }, [clave, lista]);
  return [lista, setLista] as const;
}

export function Library() {
  const libraryVersion = useStore((s) => s.libraryVersion);
  const settings = useStore((s) => s.settings);
  const saveSettings = useStore((s) => s.saveSettings);
  const toast = useStore((s) => s.toast);
  const { openMenu, menu } = useMenu();
  const play = usePlayer((s) => s.play);

  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  // La biblioteca entera vive en memoria: buscar, ordenar y agrupar sin ir y
  // volver al disco es lo que hace que la lista responda al teclear.
  const [items, setItems] = useState<LibraryItem[]>([]);
  const [bucket, setBucket] = useState("all");
  const [search, setSearch] = useState("");
  const [loading, setLoading] = useState(true);
  const [health, setHealth] = useState<RepairReport | null>(null);
  const [fixing, setFixing] = useState(false);
  const [fixProgress, setFixProgress] = useState<{ done: number; total: number } | null>(null);
  const [importing, setImporting] = useState<{ done: number; total: number } | null>(null);
  const [scanning, setScanning] = useState<{ done: number; total: number } | null>(null);
  const [sugerencias, setSugerencias] = useState<string[]>([]);
  const [cerrados, setCerrados] = useState<Set<string>>(new Set());
  // Al buscar se busca en todo, no solo en lo que haya abierto: quien escribe
  // algo quiere encontrarlo, no que le digan que ahí no está.
  const [soloSeccion, setSoloSeccion] = useState(false);
  const [formato, setFormato] = useState("todos");
  const [carpetasAbiertas, setCarpetasAbiertas] = useState(false);
  const [refreshing, setRefreshing] = useState<{
    phase: RefreshPhase;
    done: number;
    total: number;
  } | null>(null);

  // ---- barra lateral: playlists ----
  const [busquedaPl, setBusquedaPl] = useState("");
  const [plSort, setPlSort] = usePreferencia<PlSort>("rc.lib.plSort", "recent");
  const [fijadas, setFijadas] = useListaGuardada("rc.lib.plPinned");
  const [ordenManual, setOrdenManual] = useListaGuardada("rc.lib.plOrder");
  const [arrastrada, setArrastrada] = useState<string | null>(null);
  const [encima, setEncima] = useState<string | null>(null);

  const [sort, setSort] = usePreferencia<Sort>("rc.lib.sort", "recent");
  const [group, setGroup] = usePreferencia<Group>("rc.lib.group", "none");
  const [view, setView] = usePreferencia<View>("rc.lib.view", "list");

  const vigiladas = settings?.watchedDirs ?? [];
  // Lo que Recodio descarga es biblioteca por definición, así que sus carpetas
  // se rastrean siempre. Se enseñan igual: si no, no hay manera de saber qué
  // mira el rastreo y qué no.
  const fijas = useMemo(() => {
    const d = [settings?.videoDir, settings?.audioDir].filter(Boolean) as string[];
    return [...new Set(d)];
  }, [settings?.videoDir, settings?.audioDir]);

  useEffect(() => {
    const un = listen<[number, number]>("import-progress", (e) => {
      const [done, total] = e.payload;
      setImporting({ done, total });
    });
    const sc = listen<[number, number]>("scan-progress", (e) => {
      const [done, total] = e.payload;
      setScanning({ done, total });
    });
    const ref = listen<[RefreshPhase, number, number]>("refresh-progress", (e) => {
      const [phase, done, total] = e.payload;
      setRefreshing({ phase, done, total });
    });
    const rep = listen<[number, number]>("repair-progress", (e) => {
      const [done, total] = e.payload;
      setFixProgress({ done, total });
    });
    return () => {
      un.then((f) => f());
      sc.then((f) => f());
      rep.then((f) => f());
      ref.then((f) => f());
    };
  }, []);

  useEffect(() => {
    api.suggestedFolders().then(setSugerencias).catch(() => setSugerencias([]));
  }, []);

  // Rastreo al abrir la biblioteca: un mp3 copiado a mano aparece sin que haya
  // que saber que existe un botón para ello. Va callado y sin barra de progreso;
  // solo se nota si encuentra algo.
  useEffect(() => {
    if (Date.now() - ultimoRastreoAuto < ESPERA_RASTREO) return;
    ultimoRastreoAuto = Date.now();
    api
      .libraryScanQuiet()
      .then((r) => {
        if (r.added === 0) return;
        toast(
          "info",
          r.added === 1
            ? "1 archivo nuevo encontrado en tus carpetas"
            : `${r.added} archivos nuevos encontrados en tus carpetas`,
        );
        useStore.setState((s) => ({ libraryVersion: s.libraryVersion + 1 }));
      })
      .catch(() => {
        /* si el rastreo falla, la biblioteca sigue sirviendo igual */
      });
  }, [toast]);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const [pls, its] = await Promise.all([
        api.libraryPlaylists(),
        api.libraryItems(null, null),
      ]);
      setPlaylists(pls);
      setItems(its);
    } catch (e) {
      toast("error", String(e));
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    refresh();
  }, [refresh, libraryVersion]);

  useEffect(() => {
    // Se comprueba al abrir la pestaña: una herramienta escondida en Ajustes no
    // la encuentra quien tiene el problema.
    api.libraryHealth().then(setHealth).catch(() => setHealth(null));
  }, [libraryVersion]);

  function bump() {
    useStore.setState((s) => ({ libraryVersion: s.libraryVersion + 1 }));
  }

  async function deletePlaylist(p: Playlist, deleteFiles: boolean) {
    const aviso = deleteFiles
      ? `¿Eliminar «${p.title}» y BORRAR sus ${p.itemCount} archivos del disco? Esto no se puede deshacer.`
      : `¿Quitar «${p.title}» de la biblioteca? Los ${p.itemCount} archivos seguirán en tu disco.`;
    if (!window.confirm(aviso)) return;
    try {
      const n = await api.libraryDeletePlaylist(p.id, deleteFiles);
      toast(
        "success",
        deleteFiles
          ? `«${p.title}» eliminada junto con ${n} archivos`
          : `«${p.title}» quitada de la biblioteca; sus ${n} archivos siguen en el disco`,
      );
      if (bucket === p.id) setBucket("all");
      bump();
    } catch (e) {
      toast("error", String(e));
    }
  }

  // Supr sobre la colección abierta la quita, como en cualquier gestor de
  // archivos. Con Shift, además borra los archivos.
  useEffect(() => {
    function alPulsar(e: KeyboardEvent) {
      if (e.key !== "Delete") return;
      const enCampo =
        e.target instanceof HTMLElement &&
        ["INPUT", "TEXTAREA", "SELECT"].includes(e.target.tagName);
      if (enCampo) return;
      const p = playlists.find((x) => x.id === bucket);
      if (!p) return;
      e.preventDefault();
      deletePlaylist(p, e.shiftKey);
    }
    window.addEventListener("keydown", alPulsar);
    return () => window.removeEventListener("keydown", alPulsar);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bucket, playlists]);

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
      bump();
    } catch (e) {
      toast("error", String(e));
    } finally {
      setImporting(null);
    }
  }

  /// Rastrea las carpetas vigiladas: lo que aparezca entra suelto.
  async function scan(paths?: string[]) {
    setScanning({ done: 0, total: 0 });
    try {
      const r = await api.libraryScan(paths);
      toast(
        "success",
        r.added > 0
          ? `${r.added} ${r.added === 1 ? "archivo nuevo" : "archivos nuevos"} en la biblioteca` +
              (r.skipped > 0 ? `; ${r.skipped} ya estaban` : "")
          : r.found === 0
            ? "No se encontró música ni vídeo en las carpetas vigiladas"
            : `Nada nuevo: los ${r.found} archivos ya estaban en la biblioteca`,
      );
      bump();
    } catch (e) {
      toast("error", String(e));
    } finally {
      setScanning(null);
    }
  }

  async function vigilar(path: string) {
    if (vigiladas.some((d) => d.toLowerCase() === path.toLowerCase())) {
      toast("info", `${folderName(path)} ya estaba vigilada`);
      return;
    }
    await saveSettings({ watchedDirs: [...vigiladas, path] });
    await scan([path]);
  }

  async function dejarDeVigilar(path: string) {
    await saveSettings({ watchedDirs: vigiladas.filter((d) => d !== path) });
    toast(
      "info",
      `${folderName(path)} ya no se vigila. Lo que ya estaba en la biblioteca sigue ahí.`,
    );
  }

  async function del(item: LibraryItem, deleteFile: boolean) {
    try {
      await api.libraryDelete(item.id, deleteFile);
      toast("success", deleteFile ? "Archivo eliminado" : "Quitado de la biblioteca");
      bump();
    } catch (e) {
      toast("error", String(e));
    }
  }

  // ------------------------------------------------------------- selección

  const conteos = useMemo(() => {
    const ahora = Date.now() / 1000;
    return {
      all: items.length,
      audio: items.filter((i) => i.kind === "audio").length,
      video: items.filter((i) => i.kind === "video").length,
      loose: items.filter((i) => !i.playlistId).length,
      recent: items.filter((i) => ahora - i.downloadedAt < RECIENTE).length,
    };
  }, [items]);

  // Las playlists se ven una a una con su carpeta; lo suelto, en cambio, cae
  // todo en el mismo saco aunque venga de sitios distintos. Aquí se recuperan
  // sus carpetas para poder navegarlas igual.
  const carpetasSueltas = useMemo(() => {
    const mapa = new Map<string, number>();
    for (const i of items) {
      if (i.playlistId) continue;
      const carpeta = folderOf(i.filePath);
      mapa.set(carpeta, (mapa.get(carpeta) ?? 0) + 1);
    }
    return [...mapa.entries()]
      .map(([ruta, cuenta]) => ({ ruta, cuenta }))
      .sort((a, b) => b.cuenta - a.cuenta || a.ruta.localeCompare(b.ruta, "es"));
  }, [items]);

  // Las fijadas van arriba, en el orden en que se fijaron; el resto, por lo que
  // se haya elegido. El orden a mano se guarda por identificador, así que
  // aguanta que aparezcan playlists nuevas o se borren otras.
  const ordenarPlaylists = useCallback(
    (lista: Playlist[]) => {
      const posicion = new Map(ordenManual.map((id, i) => [id, i]));
      const ultimo = Number.MAX_SAFE_INTEGER;
      const ordenadas = [...lista].sort((a, b) => {
        switch (plSort) {
          case "title":
            return a.title.localeCompare(b.title, "es");
          case "count":
            return b.itemCount - a.itemCount;
          case "manual":
            return (
              (posicion.get(a.id) ?? ultimo) - (posicion.get(b.id) ?? ultimo) ||
              b.createdAt - a.createdAt
            );
          default:
            return b.createdAt - a.createdAt;
        }
      });
      // Segunda pasada en vez de meterlo en la comparación: `sort` es estable,
      // así que subir las fijadas no deshace el orden que acaba de aplicarse.
      const rango = (id: string) => {
        const i = fijadas.indexOf(id);
        return i === -1 ? ultimo : i;
      };
      return ordenadas.sort((a, b) => rango(a.id) - rango(b.id));
    },
    [plSort, ordenManual, fijadas],
  );

  const playlistsVisibles = useMemo(() => {
    const q = busquedaPl.trim().toLowerCase();
    const filtradas = q
      ? playlists.filter(
          (p) =>
            p.title.toLowerCase().includes(q) ||
            (p.uploader ?? "").toLowerCase().includes(q),
        )
      : playlists;
    return ordenarPlaylists(filtradas);
  }, [playlists, busquedaPl, ordenarPlaylists]);

  function fijar(id: string) {
    setFijadas((f) => (f.includes(id) ? f.filter((x) => x !== id) : [...f, id]));
  }

  /** Deja la playlist arrastrada en el sitio de `destino`. */
  function soltarSobre(destino: string) {
    const origen = arrastrada;
    setArrastrada(null);
    setEncima(null);
    if (!origen || origen === destino) return;

    const mover = (ids: string[]) => {
      const copia = [...ids];
      const desde = copia.indexOf(origen);
      if (desde < 0) return ids;
      const [movida] = copia.splice(desde, 1);
      const hasta = copia.indexOf(destino);
      if (hasta < 0) return ids;
      // Soltando hacia abajo se coloca detrás; hacia arriba, delante. Es lo que
      // se espera al ver dónde queda el hueco mientras se arrastra.
      copia.splice(desde <= hasta ? hasta + 1 : hasta, 0, movida);
      return copia;
    };

    // Entre fijadas manda el orden de fijado, así que reordenarlas es reordenar
    // esa lista; si no, la playlist volvería a su sitio nada más soltarla.
    if (fijadas.includes(origen) && fijadas.includes(destino)) {
      setFijadas(mover(fijadas));
      return;
    }
    // Sacar una fijada al montón de abajo es dejar de fijarla: quedarse fijada
    // la devolvería arriba en el acto.
    if (fijadas.includes(origen)) {
      setFijadas((f) => f.filter((x) => x !== origen));
    }
    setOrdenManual(mover(ordenarPlaylists(playlists).map((p) => p.id)));
    // Arrastrar es decir «este es mi orden»; mantener el anterior haría que el
    // arrastre no sirviera de nada.
    setPlSort("manual");
  }

  const delBucket = useMemo(() => {
    const ahora = Date.now() / 1000;
    if (bucket.startsWith(CARPETA)) {
      const carpeta = bucket.slice(CARPETA.length);
      return items.filter((i) => !i.playlistId && folderOf(i.filePath) === carpeta);
    }
    switch (bucket) {
      case "all":
        return items;
      case "audio":
        return items.filter((i) => i.kind === "audio");
      case "video":
        return items.filter((i) => i.kind === "video");
      case "loose":
        return items.filter((i) => !i.playlistId);
      case "recent":
        return items.filter((i) => ahora - i.downloadedAt < RECIENTE);
      default:
        return items.filter((i) => i.playlistId === bucket);
    }
  }, [items, bucket]);

  /** Extensiones que hay de verdad, para no ofrecer filtros vacíos. */
  const formatos = useMemo(() => {
    const mapa = new Map<string, number>();
    for (const i of items) mapa.set(i.ext, (mapa.get(i.ext) ?? 0) + 1);
    return [
      { value: "todos", label: "Cualquier formato" },
      ...[...mapa.entries()]
        .sort((a, b) => b[1] - a[1])
        .map(([ext, n]) => ({ value: ext, label: `${ext.toUpperCase()} (${n})` })),
    ];
  }, [items]);

  // Un formato que deja de existir (se borró lo último que quedaba de él) no
  // puede quedarse filtrando en silencio una lista vacía.
  useEffect(() => {
    if (formato !== "todos" && !formatos.some((f) => f.value === formato)) {
      setFormato("todos");
    }
  }, [formatos, formato]);

  const buscando = search.trim().length > 0;
  const global = buscando && !soloSeccion;

  const shown = useMemo(() => {
    const q = search.trim().toLowerCase();
    // Buscar mira toda la biblioteca aunque estés dentro de una playlist. Antes
    // solo se buscaba en la sección abierta, y desde una lista de 20 canciones
    // parecía que el resto no existiera.
    const base = q && !soloSeccion ? items : delBucket;
    let filtrados = q
      ? base.filter(
          (i) =>
            i.title.toLowerCase().includes(q) ||
            (i.uploader ?? "").toLowerCase().includes(q) ||
            i.filePath.toLowerCase().includes(q),
        )
      : base;
    if (formato !== "todos") filtrados = filtrados.filter((i) => i.ext === formato);

    // Dentro de una playlist el orden que importa es el suyo, salvo que se pida
    // otro: es la lista tal y como la hizo quien la hizo.
    const esPlaylist = !q && playlists.some((p) => p.id === bucket);
    const orden = [...filtrados];
    if (esPlaylist && sort === "recent") {
      orden.sort((a, b) => (a.playlistIndex ?? 0) - (b.playlistIndex ?? 0));
      return orden;
    }

    const texto = (s: string | null) => (s ?? "").toLocaleLowerCase("es");
    orden.sort((a, b) => {
      switch (sort) {
        case "title":
          return texto(a.title).localeCompare(texto(b.title), "es");
        case "artist":
          return (
            texto(a.uploader || SIN_ARTISTA).localeCompare(
              texto(b.uploader || SIN_ARTISTA),
              "es",
            ) || texto(a.title).localeCompare(texto(b.title), "es")
          );
        case "duration":
          return (b.duration ?? 0) - (a.duration ?? 0);
        case "size":
          return b.fileSize - a.fileSize;
        default:
          return b.downloadedAt - a.downloadedAt;
      }
    });
    return orden;
  }, [delBucket, items, playlists, search, soloSeccion, formato, sort, bucket]);

  const grupos = useMemo(() => {
    if (group === "none") return null;
    const mapa = new Map<string, LibraryItem[]>();
    for (const item of shown) {
      const clave =
        group === "artist" ? item.uploader || SIN_ARTISTA : folderOf(item.filePath);
      const lista = mapa.get(clave);
      if (lista) lista.push(item);
      else mapa.set(clave, [item]);
    }
    return [...mapa.entries()]
      .map(([clave, lista]) => ({
        clave,
        etiqueta: group === "folder" ? folderName(clave) : clave,
        detalle: group === "folder" ? clave : null,
        items: lista,
      }))
      .sort((a, b) => a.etiqueta.localeCompare(b.etiqueta, "es"));
  }, [shown, group]);

  const pesoTotal = useMemo(
    () => shown.reduce((n, i) => n + i.fileSize, 0),
    [shown],
  );

  function menuDe(item: LibraryItem, cola: LibraryItem[]): MenuItem[] {
    return [
      {
        label: "Reproducir aquí",
        icon: <Play size={14} />,
        onClick: () => play(item, cola),
      },
      {
        // El reproductor interno no cubre todos los formatos, y hay quien
        // prefiere el suyo: sigue a un clic derecho.
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
      ...(item.url
        ? [
            {
              label: "Abrir el enlace original",
              icon: <ExternalLink size={14} />,
              onClick: () => api.openFolder(item.url),
            },
          ]
        : []),
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
    ];
  }

  const ocupado = importing !== null || scanning !== null || refreshing !== null;

  return (
    <div className="flex h-full min-h-0">
      {/* ---- Colecciones ---- */}
      <aside className="flex w-64 shrink-0 flex-col gap-1 overflow-y-auto border-r border-line px-3 py-4">
        <BucketButton
          active={bucket === "all"}
          onClick={() => setBucket("all")}
          icon={<LibraryIcon size={15} />}
          label="Todo"
          count={conteos.all}
        />
        <BucketButton
          active={bucket === "audio"}
          onClick={() => setBucket("audio")}
          icon={<Music size={15} />}
          label="Música"
          count={conteos.audio}
        />
        <BucketButton
          active={bucket === "video"}
          onClick={() => setBucket("video")}
          icon={<FileVideo size={15} />}
          label="Vídeos"
          count={conteos.video}
        />
        <div className="flex items-center gap-1">
          <div className="min-w-0 flex-1">
            <BucketButton
              active={bucket === "loose"}
              onClick={() => setBucket("loose")}
              icon={<Disc3 size={15} />}
              label="Sueltos"
              count={conteos.loose}
              title="Archivos que no pertenecen a ninguna playlist"
            />
          </div>
          {carpetasSueltas.length > 1 && (
            <button
              type="button"
              title={
                carpetasAbiertas
                  ? "Ocultar las carpetas"
                  : `Ver las ${carpetasSueltas.length} carpetas donde están`
              }
              aria-expanded={carpetasAbiertas}
              onClick={() => setCarpetasAbiertas((v) => !v)}
              className="rc-ring shrink-0 rounded-md p-1 text-muted transition hover:bg-surface2 hover:text-ink"
            >
              <ChevronRight
                size={14}
                className={`transition-transform ${carpetasAbiertas ? "rotate-90" : ""}`}
              />
            </button>
          )}
        </div>

        {/* Las carpetas de lo suelto, para poder navegarlas como las playlists. */}
        {carpetasAbiertas &&
          carpetasSueltas.map((c) => (
            <button
              key={c.ruta}
              type="button"
              onClick={() => setBucket(CARPETA + c.ruta)}
              title={c.ruta}
              className={`rc-ring ml-3 flex items-center gap-2 rounded-xl px-2.5 py-1.5 text-left text-[12.5px] transition
                ${
                  bucket === CARPETA + c.ruta
                    ? "bg-accent/12 text-ink"
                    : "text-muted hover:bg-surface2 hover:text-ink"
                }`}
            >
              <Folder
                size={13}
                className={`shrink-0 ${bucket === CARPETA + c.ruta ? "text-accent2" : ""}`}
              />
              <span className="min-w-0 flex-1 truncate">{folderName(c.ruta)}</span>
              <span className="shrink-0 text-[10.5px] tabular-nums">{c.cuenta}</span>
            </button>
          ))}

        <BucketButton
          active={bucket === "recent"}
          onClick={() => setBucket("recent")}
          icon={<Clock size={15} />}
          label="Recientes"
          count={conteos.recent}
          title="Lo llegado en el último mes"
        />

        {playlists.length > 0 && (
          <>
            <div className="mt-4 flex items-center justify-between gap-1 px-2 pb-1">
              <span className="text-[11px] font-semibold uppercase tracking-wider text-muted">
                Playlists
              </span>
              <span className="text-[10.5px] tabular-nums text-muted/70">
                {playlistsVisibles.length}
                {busquedaPl.trim() && ` de ${playlists.length}`}
              </span>
            </div>

            {/* Con cuarenta playlists, encontrarla a ojo deja de funcionar. */}
            {playlists.length > 6 && (
              <div className="mb-1 flex items-center gap-1.5 rounded-xl border border-line bg-surface2/60 px-2 py-1">
                <Search size={13} className="shrink-0 text-muted" />
                <input
                  value={busquedaPl}
                  onChange={(e) => setBusquedaPl(e.target.value)}
                  placeholder="Filtrar playlists…"
                  aria-label="Filtrar playlists"
                  className="min-w-0 flex-1 bg-transparent text-[12px] outline-none placeholder:text-muted/70"
                />
                {busquedaPl && (
                  <button
                    type="button"
                    title="Quitar el filtro"
                    onClick={() => setBusquedaPl("")}
                    className="rc-ring shrink-0 rounded-md p-0.5 text-muted transition hover:text-ink"
                  >
                    <X size={12} />
                  </button>
                )}
              </div>
            )}

            <div className="mb-1 flex items-center gap-1 px-0.5">
              <SelectorMini
                value={plSort}
                onChange={(v) => setPlSort(v as PlSort)}
                options={ORDENES_PL}
                title="Ordenar las playlists. Arrástralas para ponerlas a tu gusto."
              />
              {plSort === "manual" && ordenManual.length > 0 && (
                <button
                  type="button"
                  title="Olvidar el orden que hiciste arrastrando"
                  onClick={() => {
                    setOrdenManual([]);
                    setPlSort("recent");
                  }}
                  className="rc-ring rounded-md p-1 text-muted transition hover:bg-surface2 hover:text-ink"
                >
                  <RotateCcw size={13} />
                </button>
              )}
            </div>
          </>
        )}

        {playlistsVisibles.map((p) => (
          <div
            key={p.id}
            draggable
            onDragStart={(e) => {
              setArrastrada(p.id);
              e.dataTransfer.effectAllowed = "move";
              // Firefox no arranca el arrastre sin datos puestos.
              e.dataTransfer.setData("text/plain", p.id);
            }}
            onDragOver={(e) => {
              if (!arrastrada || arrastrada === p.id) return;
              e.preventDefault();
              e.dataTransfer.dropEffect = "move";
              setEncima(p.id);
            }}
            onDragLeave={() => setEncima((v) => (v === p.id ? null : v))}
            onDrop={(e) => {
              e.preventDefault();
              soltarSobre(p.id);
            }}
            onDragEnd={() => {
              setArrastrada(null);
              setEncima(null);
            }}
            className={`rounded-xl transition ${arrastrada === p.id ? "opacity-40" : ""} ${
              encima === p.id ? "ring-1 ring-accent/70" : ""
            }`}
          >
          <BucketButton
            active={bucket === p.id}
            onClick={() => setBucket(p.id)}
            icon={
              fijadas.includes(p.id) ? <Pin size={14} /> : <ListMusic size={15} />
            }
            label={p.title}
            count={p.itemCount}
            title={`${p.title}\n\nArrástrala para cambiarla de sitio. Clic derecho para fijarla arriba.`}
            onContextMenu={(e) =>
              openMenu(e, [
                {
                  label: fijadas.includes(p.id) ? "Dejar de fijar" : "Fijar arriba",
                  icon: fijadas.includes(p.id) ? <PinOff size={14} /> : <Pin size={14} />,
                  onClick: () => fijar(p.id),
                },
                { separator: true, label: "" },
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
                            bump();
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
                {
                  label: "Quitar de la biblioteca (Supr)",
                  icon: <Trash2 size={14} />,
                  onClick: () => deletePlaylist(p, false),
                },
                {
                  label: "Eliminar también los archivos",
                  icon: <Trash2 size={14} />,
                  danger: true,
                  onClick: () => deletePlaylist(p, true),
                },
              ])
            }
          />
          </div>
        ))}

        {busquedaPl.trim() && playlistsVisibles.length === 0 && (
          <p className="px-2 py-1 text-[11.5px] text-muted/75">
            Ninguna playlist se llama así.
          </p>
        )}

        {/* ---- Carpetas vigiladas ---- */}
        <div className="mt-4 flex items-center justify-between gap-1 px-2 pb-1">
          <span className="text-[11px] font-semibold uppercase tracking-wider text-muted">
            Carpetas vigiladas
          </span>
          <button
            type="button"
            title="Vigilar otra carpeta de tu equipo"
            disabled={ocupado}
            onClick={async () => {
              const picked = await openDialog({ directory: true });
              if (typeof picked === "string") vigilar(picked);
            }}
            className="rc-ring rounded-md p-0.5 text-muted transition hover:bg-surface2 hover:text-ink disabled:opacity-40"
          >
            <Plus size={14} />
          </button>
        </div>

        {fijas.map((d) => (
          <div
            key={d}
            title={`${d}\n\nCarpeta de descargas: se rastrea siempre.`}
            className="flex items-center gap-2 rounded-xl px-2.5 py-1.5"
          >
            <Folder size={14} className="shrink-0 text-accent2" />
            <span className="min-w-0 flex-1 truncate text-[12.5px]">{folderName(d)}</span>
            <span className="shrink-0 text-[10px] text-muted/70">descargas</span>
          </div>
        ))}

        {vigiladas.length === 0 && (
          <p className="px-2 pb-1 text-[11px] leading-snug text-muted/75">
            Añade aquí donde tengas tu música y aparecerá en la biblioteca
            aunque no esté en ninguna lista.
          </p>
        )}

        {vigiladas.map((d) => (
          <div key={d} className="group flex items-center gap-2 rounded-xl px-2.5 py-1.5">
            <Folder size={14} className="shrink-0 text-muted" />
            <span className="min-w-0 flex-1 truncate text-[12.5px]" title={d}>
              {folderName(d)}
            </span>
            <button
              type="button"
              title="Dejar de vigilar esta carpeta"
              onClick={() => dejarDeVigilar(d)}
              className="rc-ring shrink-0 rounded-md p-0.5 text-muted opacity-0 transition hover:text-bad group-hover:opacity-100"
            >
              <X size={13} />
            </button>
          </div>
        ))}

        {sugerencias
          .filter(
            (s) =>
              ![...vigiladas, ...fijas].some(
                (d) => d.toLowerCase() === s.toLowerCase(),
              ),
          )
          .map((s) => (
            <button
              key={s}
              type="button"
              disabled={ocupado}
              onClick={() => vigilar(s)}
              title={`Vigilar ${s}`}
              className="rc-ring flex items-center gap-2 rounded-xl px-2.5 py-1.5 text-left text-[12.5px]
                text-muted transition hover:bg-surface2 hover:text-ink disabled:opacity-40"
            >
              <Plus size={13} className="shrink-0" />
              <span className="min-w-0 flex-1 truncate">{folderName(s)}</span>
            </button>
          ))}

        <button
          type="button"
          disabled={ocupado}
          onClick={() => scan()}
          className="rc-ring mt-2 flex items-center justify-center gap-2 rounded-xl border border-line
            bg-surface2 px-2.5 py-2 text-[12.5px] font-medium transition hover:bg-surface3 disabled:opacity-40"
        >
          <FolderSearch size={14} className={scanning ? "animate-pulse" : ""} />
          Rastrear ahora
        </button>
      </aside>

      {/* ---- Contenido ---- */}
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex flex-wrap items-center gap-2 border-b border-line px-6 py-3">
          <div className="flex min-w-[180px] flex-1 items-center gap-2">
            <Search size={15} className="shrink-0 text-muted" />
            <input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Buscar por título, artista o carpeta…"
              className={`${inputClass} max-w-sm border-transparent bg-transparent`}
            />
          </div>

          <SelectorMini
            value={formato}
            onChange={setFormato}
            options={formatos}
            title="Filtrar por formato de archivo"
          />
          <SelectorMini
            value={group}
            onChange={(v) => setGroup(v as Group)}
            options={AGRUPACIONES}
            title="Agrupar la lista"
          />
          <SelectorMini
            value={sort}
            onChange={(v) => setSort(v as Sort)}
            options={ORDENES}
            title="Ordenar por"
          />

          <div className="flex rounded-lg border border-line bg-surface2 p-0.5">
            <BotonVista
              activo={view === "list"}
              onClick={() => setView("list")}
              title="Ver en lista"
            >
              <Rows3 size={14} />
            </BotonVista>
            <BotonVista
              activo={view === "grid"}
              onClick={() => setView("grid")}
              title="Ver en cuadrícula"
            >
              <LayoutGrid size={14} />
            </BotonVista>
          </div>

          <IconButton
            title="Añadir una carpeta de tu equipo como colección"
            onClick={importFolder}
            disabled={ocupado}
          >
            <FolderPlus size={15} />
          </IconButton>
          <IconButton
            title="Rastrear las carpetas vigiladas en busca de archivos sueltos"
            onClick={() => scan()}
            disabled={ocupado}
          >
            <FolderSearch size={15} className={scanning ? "animate-pulse" : ""} />
          </IconButton>
          <IconButton
            title="Actualizar la biblioteca: buscar lo nuevo, corregir lo que no cuadre y generar las miniaturas que falten"
            disabled={ocupado}
            onClick={async () => {
              setRefreshing({ phase: "scanning", done: 0, total: 0 });
              try {
                const r = await api.libraryRefresh();
                const partes = [
                  r.imported > 0 && `${r.imported} en colecciones`,
                  r.scanned > 0 && `${r.scanned} sueltos`,
                  r.thumbnails > 0 && `${r.thumbnails} miniaturas`,
                  r.mismatched > 0 && `${r.mismatched} entradas corregidas`,
                  r.missing > 0 && `${r.missing} ya no existían`,
                ].filter(Boolean);
                toast(
                  "success",
                  partes.length > 0
                    ? `Biblioteca al día: ${partes.join(" · ")}`
                    : "La biblioteca ya estaba al día",
                );
                bump();
              } catch (e) {
                toast("error", String(e));
              } finally {
                setRefreshing(null);
              }
            }}
          >
            <RefreshCw size={15} className={refreshing ? "animate-spin" : ""} />
          </IconButton>
        </div>

        <div className="flex items-center gap-2 border-b border-line px-6 py-1.5 text-[11.5px] text-muted">
          <span className="tabular-nums">
            {shown.length} {shown.length === 1 ? "archivo" : "archivos"}
          </span>
          {pesoTotal > 0 && <span>· {bytes(pesoTotal)}</span>}
          {grupos && (
            <span>
              · {grupos.length} {group === "artist" ? "artistas" : "carpetas"}
            </span>
          )}
          {buscando && (
            <span className="ml-auto flex items-center gap-2">
              <button
                type="button"
                onClick={() => setSoloSeccion((v) => !v)}
                title={
                  global
                    ? "Buscar solo dentro de la sección abierta"
                    : "Buscar en toda la biblioteca"
                }
                className="rc-ring rounded-md px-1.5 py-0.5 transition hover:bg-surface2 hover:text-ink"
              >
                {global ? "En toda la biblioteca" : "Solo en esta sección"}
              </button>
              <button
                type="button"
                onClick={() => setSearch("")}
                className="rc-ring rounded-md px-1.5 py-0.5 transition hover:bg-surface2 hover:text-ink"
              >
                Quitar el filtro
              </button>
            </span>
          )}
        </div>

        {refreshing && (
          <Aviso icono={<RefreshCw size={16} className="animate-spin text-accent2" />}>
            {
              {
                scanning: "Buscando archivos nuevos en tus carpetas…",
                checking: "Comprobando que cada canción sea la suya…",
                thumbnails: "Generando miniaturas…",
              }[refreshing.phase]
            }
            {refreshing.total > 0 && ` ${refreshing.done} de ${refreshing.total}`}
          </Aviso>
        )}

        {scanning && (
          <Aviso icono={<FolderSearch size={16} className="animate-pulse text-accent2" />}>
            {scanning.total > 0
              ? `Rastreando: ${scanning.done} de ${scanning.total} archivos`
              : "Rastreando las carpetas vigiladas…"}
          </Aviso>
        )}

        {importing && (
          <Aviso icono={<FolderPlus size={16} className="animate-pulse text-accent2" />}>
            {importing.total > 0
              ? `Leyendo la carpeta: ${importing.done} de ${importing.total} archivos`
              : "Buscando música y vídeo en la carpeta…"}
          </Aviso>
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
                Revisar comprueba la duración de cada archivo y quita solo las
                entradas que no le corresponden; las que sí, aunque compartan
                archivo con otra playlist, se quedan.{" "}
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
                    r.removed === 0
                      ? "Todo correcto: cada canción apunta a su archivo"
                      : `${r.removed} entradas quitadas: no correspondían a su archivo. ` +
                        "Vuelve a analizar esas playlists para descargar lo que falte.",
                  );
                  setHealth(await api.libraryHealth());
                  bump();
                } catch (e) {
                  toast("error", String(e));
                } finally {
                  setFixing(false);
                  setFixProgress(null);
                }
              }}
              className="shrink-0 rounded-xl bg-warn/20 px-3 py-1.5 text-[12px] font-medium text-warn transition hover:bg-warn/30 disabled:opacity-50"
            >
              {fixing
                ? fixProgress && fixProgress.total > 0
                  ? `${Math.round((fixProgress.done / fixProgress.total) * 100)}%`
                  : "Revisando…"
                : "Revisar"}
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
            <Vacio
              hayBiblioteca={items.length > 0}
              buscando={buscando}
              filtrandoFormato={formato !== "todos"}
              bucket={bucket}
              onScan={() => scan()}
            />
          ) : grupos ? (
            <div className="flex flex-col gap-5">
              {grupos.map((g) => {
                const abierto = !cerrados.has(g.clave);
                return (
                  <section key={g.clave}>
                    <button
                      type="button"
                      onClick={() =>
                        setCerrados((prev) => {
                          const next = new Set(prev);
                          if (next.has(g.clave)) next.delete(g.clave);
                          else next.add(g.clave);
                          return next;
                        })
                      }
                      className="rc-ring mb-2 flex w-full items-center gap-2 rounded-lg px-1 py-1 text-left"
                    >
                      <ChevronRight
                        size={14}
                        className={`shrink-0 text-muted transition-transform ${abierto ? "rotate-90" : ""}`}
                      />
                      {group === "artist" ? (
                        <User size={14} className="shrink-0 text-accent2" />
                      ) : (
                        <Folder size={14} className="shrink-0 text-accent2" />
                      )}
                      <span className="truncate text-[13px] font-semibold">{g.etiqueta}</span>
                      <span className="shrink-0 text-[11px] tabular-nums text-muted">
                        {g.items.length}
                      </span>
                      {g.detalle && (
                        <span className="ml-2 min-w-0 truncate text-[11px] text-muted/70">
                          {g.detalle}
                        </span>
                      )}
                    </button>
                    {abierto &&
                      (view === "grid" ? (
                        <Cuadricula
                          items={g.items}
                          cola={shown}
                          play={play}
                          openMenu={openMenu}
                          menuDe={menuDe}
                        />
                      ) : (
                        <Lista
                          items={g.items}
                          cola={shown}
                          play={play}
                          openMenu={openMenu}
                          menuDe={menuDe}
                        />
                      ))}
                  </section>
                );
              })}
            </div>
          ) : view === "grid" ? (
            <Cuadricula
              items={shown}
              cola={shown}
              play={play}
              openMenu={openMenu}
              menuDe={menuDe}
            />
          ) : (
            <Lista items={shown} cola={shown} play={play} openMenu={openMenu} menuDe={menuDe} />
          )}
        </div>
      </div>

      {menu}
    </div>
  );
}

// -------------------------------------------------------------- piezas

type Reproducir = (item: LibraryItem, cola: LibraryItem[]) => void;
type AbrirMenu = (e: MouseEvent, items: MenuItem[]) => void;

interface ListaProps {
  items: LibraryItem[];
  /** La cola de reproducción es la lista visible entera, no solo el grupo. */
  cola: LibraryItem[];
  play: Reproducir;
  openMenu: AbrirMenu;
  menuDe: (item: LibraryItem, cola: LibraryItem[]) => MenuItem[];
}

function Lista({ items, cola, play, openMenu, menuDe }: ListaProps) {
  return (
    <div className="flex flex-col gap-1.5">
      {items.map((item) => (
        <div
          key={item.id}
          onDoubleClick={() => play(item, cola)}
          onContextMenu={(e) => openMenu(e, menuDe(item, cola))}
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
              {[
                item.uploader,
                item.ext.toUpperCase(),
                bytes(item.fileSize),
                relativeDate(item.downloadedAt),
              ]
                .filter(Boolean)
                .join(" · ")}
            </p>
          </div>
          {!item.playlistId && (
            <span
              title="No pertenece a ninguna playlist"
              className="shrink-0 rounded-md border border-line px-1.5 py-0.5 text-[10px] text-muted"
            >
              suelto
            </span>
          )}
          {/* La fila entera responde al doble clic, así que los botones tienen
              que cortar la propagación: si no, un doble clic encima de uno
              dispara su onClick dos veces *y además* el doble clic de la fila,
              abriendo tres reproductores. */}
          <div
            className="flex shrink-0 gap-1 opacity-0 transition group-hover:opacity-100"
            onDoubleClick={(e) => e.stopPropagation()}
          >
            <IconButton title="Reproducir" onClick={() => play(item, cola)}>
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
  );
}

function Cuadricula({ items, cola, play, openMenu, menuDe }: ListaProps) {
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(168px,1fr))] gap-3">
      {items.map((item) => (
        <div
          key={item.id}
          onDoubleClick={() => play(item, cola)}
          onContextMenu={(e) => openMenu(e, menuDe(item, cola))}
          className="rc-card group cursor-default overflow-hidden p-0 transition hover:border-accent/40"
        >
          <div className="relative">
            <Thumb
              src={item.thumbnail}
              kind={item.kind}
              className="aspect-video w-full rounded-none"
              badge={duration(item.duration)}
            />
            <button
              type="button"
              title="Reproducir"
              onDoubleClick={(e) => e.stopPropagation()}
              onClick={() => play(item, cola)}
              className="absolute inset-0 flex items-center justify-center bg-black/45 opacity-0
                transition group-hover:opacity-100"
            >
              <span className="flex h-10 w-10 items-center justify-center rounded-full bg-white/95 text-black shadow-lg">
                <Play size={17} className="ml-0.5" />
              </span>
            </button>
          </div>
          <div className="p-2.5">
            <p className="truncate text-[12.5px] font-medium" title={item.title}>
              {item.title}
            </p>
            <p className="truncate text-[11px] text-muted">
              {[item.uploader, item.ext.toUpperCase(), bytes(item.fileSize)]
                .filter(Boolean)
                .join(" · ")}
            </p>
          </div>
        </div>
      ))}
    </div>
  );
}

function Vacio({
  hayBiblioteca,
  buscando,
  filtrandoFormato,
  bucket,
  onScan,
}: {
  hayBiblioteca: boolean;
  buscando: boolean;
  filtrandoFormato: boolean;
  bucket: string;
  onScan: () => void;
}) {
  if (buscando || filtrandoFormato) {
    return (
      <EmptyState
        icon={<Search size={22} />}
        title="Sin resultados"
        body={
          filtrandoFormato
            ? "Nada que coincida con ese formato. Prueba con «Cualquier formato»."
            : "No hay nada que coincida con lo que has escrito, ni siquiera en el resto de la biblioteca. Prueba con menos palabras."
        }
      />
    );
  }
  if (hayBiblioteca) {
    return (
      <EmptyState
        icon={<LibraryIcon size={22} />}
        title={bucket === "loose" ? "No hay archivos sueltos" : "Aquí no hay nada"}
        body={
          bucket === "loose"
            ? "Todo lo que tienes pertenece a alguna playlist o colección."
            : "Cambia de sección en la barra de la izquierda para ver el resto de la biblioteca."
        }
      />
    );
  }
  return (
    <EmptyState
      icon={<LibraryIcon size={22} />}
      title="Tu biblioteca está vacía"
      body="Lo que descargues aparece aquí solo. Y si ya tienes música en el equipo, vigila sus carpetas y Recodio encontrará hasta los mp3 sueltos."
      action={
        <button
          type="button"
          onClick={onScan}
          className="rc-ring inline-flex items-center gap-2 rounded-xl bg-gradient-to-r from-accent to-accent2
            px-4 py-2 text-[13px] font-medium text-white shadow-lg shadow-accent/25 transition hover:brightness-110"
        >
          <FolderSearch size={15} />
          Buscar música en mi equipo
        </button>
      }
    />
  );
}

function Aviso({ icono, children }: { icono: ReactNode; children: ReactNode }) {
  return (
    <div className="mx-6 mt-4 flex items-center gap-3 rounded-2xl border border-accent2/30 bg-accent2/5 px-3.5 py-2.5">
      <span className="shrink-0">{icono}</span>
      <span className="min-w-0 flex-1 text-[12px] leading-snug">{children}</span>
    </div>
  );
}


function SelectorMini({
  value,
  onChange,
  options,
  title,
}: {
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string }[];
  title: string;
}) {
  return (
    <select
      value={value}
      title={title}
      aria-label={title}
      onChange={(e) => onChange(e.target.value)}
      className="rc-ring cursor-pointer rounded-lg border border-line bg-surface2 py-1.5 pl-2.5 pr-7
        text-[12px] text-muted outline-none transition hover:text-ink focus:border-accent/60"
      style={{
        appearance: "none",
        backgroundImage:
          "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' fill='none' stroke='%238e97b2' stroke-width='2'><path d='M4 6l4 4 4-4'/></svg>\")",
        backgroundRepeat: "no-repeat",
        backgroundPosition: "right 6px center",
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

function BotonVista({
  activo,
  onClick,
  title,
  children,
}: {
  activo: boolean;
  onClick: () => void;
  title: string;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      aria-label={title}
      aria-pressed={activo}
      className={`rc-ring rounded-md px-2 py-1 transition
        ${activo ? "bg-surface3 text-ink" : "text-muted hover:text-ink"}`}
    >
      {children}
    </button>
  );
}

function BucketButton({
  active,
  onClick,
  icon,
  label,
  count,
  title,
  onContextMenu,
}: {
  active: boolean;
  onClick: () => void;
  icon: ReactNode;
  label: string;
  count?: number;
  title?: string;
  onContextMenu?: (e: MouseEvent) => void;
}) {
  return (
    <button
      onClick={onClick}
      onContextMenu={onContextMenu}
      title={title}
      className={`rc-ring flex items-center gap-2.5 rounded-xl px-2.5 py-2 text-left text-[13px] transition
        ${active ? "bg-accent/12 text-ink" : "text-muted hover:bg-surface2 hover:text-ink"}`}
    >
      <span className={active ? "text-accent2" : ""}>{icon}</span>
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {count != null && <span className="shrink-0 text-[11px] tabular-nums">{count}</span>}
    </button>
  );
}

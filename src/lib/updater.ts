import { relaunch } from "@tauri-apps/plugin-process";
import { check, type Update } from "@tauri-apps/plugin-updater";

export type UpdateState =
  | { status: "idle" }
  | { status: "checking" }
  | { status: "none" }
  | { status: "available"; version: string; notes: string | null }
  | { status: "downloading"; progress: number }
  | { status: "ready" }
  | { status: "error"; message: string };

let pending: Update | null = null;

/**
 * El actualizador solo funciona sobre paquetes que puedan reemplazarse a sí
 * mismos: el instalador de Windows y el AppImage. Quien instale el `.deb` recibe
 * las actualizaciones por su gestor de paquetes, no por aquí, así que conviene
 * decírselo en vez de dejarlo fallando en silencio.
 */
export async function checkForUpdate(): Promise<UpdateState> {
  try {
    const update = await check();
    if (!update) {
      pending = null;
      return { status: "none" };
    }
    pending = update;
    return {
      status: "available",
      version: update.version,
      notes: update.body ?? null,
    };
  } catch (e) {
    pending = null;
    return { status: "error", message: String(e) };
  }
}

export async function installUpdate(
  onProgress: (fraction: number) => void,
): Promise<UpdateState> {
  if (!pending) return { status: "error", message: "No hay ninguna actualización preparada" };

  try {
    let total = 0;
    let downloaded = 0;

    await pending.downloadAndInstall((event) => {
      switch (event.event) {
        case "Started":
          total = event.data.contentLength ?? 0;
          downloaded = 0;
          onProgress(total > 0 ? 0 : -1);
          break;
        case "Progress":
          downloaded += event.data.chunkLength;
          onProgress(total > 0 ? downloaded / total : -1);
          break;
        case "Finished":
          onProgress(1);
          break;
      }
    });

    return { status: "ready" };
  } catch (e) {
    return { status: "error", message: String(e) };
  }
}

export async function restartApp(): Promise<void> {
  await relaunch();
}

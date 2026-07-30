"""Genera el `latest.json` que consulta el actualizador de Recodio.

    python tools/make_latest_json.py 0.1.0 --notes notas.md

Tauri firma cada artefacto y deja la firma en un `.sig` al lado. Este script las
recoge y arma el manifiesto apuntando a las URL que tendrá la release en GitHub.

Solo entran los formatos que saben reemplazarse a sí mismos: el instalador de
Windows y el AppImage. El `.deb` se actualiza por el gestor de paquetes, así que
incluirlo aquí solo serviría para que el actualizador fallara.
"""

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

REPO = "giver720/Recodio"

# (clave de plataforma, ruta del artefacto relativa a bundle/)
TARGETS = [
    ("windows-x86_64", "nsis/Recodio_{v}_x64-setup.exe"),
    ("linux-x86_64", "appimage/Recodio_{v}_amd64.AppImage"),
]


def read_signature(artifact: Path) -> str:
    sig = artifact.with_suffix(artifact.suffix + ".sig")
    if not sig.exists():
        raise SystemExit(
            f"Falta la firma de {artifact.name}.\n"
            f"Compila con TAURI_SIGNING_PRIVATE_KEY_PATH definido."
        )
    return sig.read_text(encoding="utf8").strip()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("version")
    ap.add_argument("--notes", type=Path, help="Archivo con las notas de la versión")
    ap.add_argument(
        "--bundle-dir",
        type=Path,
        default=Path("src-tauri/target/release/bundle"),
        help="Carpeta de artefactos de Windows",
    )
    ap.add_argument(
        "--linux-bundle-dir",
        type=Path,
        help="Carpeta de artefactos de Linux, si se compilaron aparte",
    )
    ap.add_argument("-o", "--out", type=Path, default=Path("latest.json"))
    args = ap.parse_args()

    notes = args.notes.read_text(encoding="utf8").strip() if args.notes else ""

    platforms = {}
    for key, pattern in TARGETS:
        base = args.linux_bundle_dir if key.startswith("linux") and args.linux_bundle_dir else args.bundle_dir
        artifact = base / pattern.format(v=args.version)
        if not artifact.exists():
            print(f"  · sin {key}: no existe {artifact}")
            continue
        platforms[key] = {
            "signature": read_signature(artifact),
            "url": f"https://github.com/{REPO}/releases/download/v{args.version}/{artifact.name}",
        }
        print(f"  · {key}: {artifact.name}")

    if not platforms:
        raise SystemExit("No se encontró ningún artefacto firmado.")

    manifest = {
        "version": args.version,
        "notes": notes,
        "pub_date": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "platforms": platforms,
    }
    args.out.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf8")
    print(f"escrito {args.out}")


if __name__ == "__main__":
    main()

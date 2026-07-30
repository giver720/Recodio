#!/usr/bin/env bash
# Compila los paquetes de Linux desde WSL.
#
#   wsl -e bash "/mnt/d/mis programas/recodio/tools/build-linux.sh"
#
# Las fuentes se copian al sistema de archivos de WSL antes de compilar. Hacerlo
# directamente sobre /mnt/d es entre cinco y diez veces más lento, porque cada
# acceso pasa por el puente 9p, y cargo escribe decenas de miles de archivos.

set -euo pipefail

SRC="${SRC:-/mnt/d/mis programas/recodio}"
WORK="${WORK:-$HOME/recodio}"

if [[ ! -d "$SRC" ]]; then
    echo "No encuentro las fuentes en: $SRC" >&2
    echo "Pásalas con SRC=/ruta/al/proyecto $0" >&2
    exit 1
fi

echo "==> Copiando fuentes a $WORK"
mkdir -p "$WORK"
rsync -a --delete \
    --exclude node_modules \
    --exclude dist \
    --exclude target \
    --exclude .git \
    "$SRC/" "$WORK/"

cd "$WORK"

echo "==> Dependencias de npm"
if [[ -f package-lock.json ]]; then
    npm ci
else
    npm install
fi

echo "==> Empaquetando"
npm run tauri build -- --bundles "${BUNDLES:-deb,appimage}"

echo
echo "==> Resultado"
find "$WORK/src-tauri/target/release/bundle" -type f \
    \( -name '*.deb' -o -name '*.AppImage' -o -name '*.rpm' \) \
    -printf '%10s  %p\n' 2>/dev/null || true

echo
echo "Para copiarlos a Windows:"
echo "  cp \$WORK/src-tauri/target/release/bundle/deb/*.deb '$SRC/dist-linux/'"

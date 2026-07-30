"""Genera el icono de Recodio.

Ejecutar desde la raíz del proyecto:

    python tools/make_icon.py
    npm run tauri icon tools/icon-source.png

La marca es una flecha de descarga sobre tres barras de ecualizador: descarga +
medios, las dos cosas que hace el programa. Las barras se funden en una línea
base a tamaño pequeño, así que el glifo sigue leyéndose como "descargar" a 16 px.
Los colores son los mismos del degradado de la app (src/styles.css).
"""

from pathlib import Path

from PIL import Image, ImageDraw

# Se dibuja 4x y se reduce al final: es la única forma de tener bordes suaves,
# porque ImageDraw no hace antialiasing.
SCALE = 4
SIZE = 1024
N = SIZE * SCALE

# Familia de --rc-accent / --rc-accent-2, con el punto medio algo más saturado:
# en un degradado diagonal la banda central ocupa más superficie que los
# extremos, así que un rosa claro ahí lavaría el icono entero.
STOPS = [
    (0.00, (0x7C, 0x4D, 0xFF)),  # violeta
    (0.45, (0xB8, 0x45, 0xF0)),  # púrpura
    (1.00, (0x18, 0xC8, 0xE8)),  # cian
]

# Recuadro redondeado, en coordenadas de 1024.
BOX = (48, 48, 976, 976)
BOX_RADIUS = 224

# Glifo, en coordenadas de 1024.
SHAFT = (456, 165, 568, 400)  # asta de la flecha
HEAD = [(350, 340), (674, 340), (512, 560)]  # punta
# x0, y_top, x1 — alturas desiguales para que se lean como ecualizador y no
# como tres puntos. La barra central es la más baja: deja respirar la punta.
BARS = [(288, 600, 396), (456, 640, 568), (624, 578, 732)]
BARS_BASE = 856
PILL_RADIUS = 56


def gradient_color(t: float) -> tuple[int, int, int]:
    t = min(max(t, 0.0), 1.0)
    for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
        if t <= t1:
            f = 0.0 if t1 == t0 else (t - t0) / (t1 - t0)
            return tuple(round(a + (b - a) * f) for a, b in zip(c0, c1))
    return STOPS[-1][1]


def diagonal_gradient(size: int) -> Image.Image:
    """Degradado en diagonal. Se calcula pequeño y se escala: un degradado no
    pierde nada al ampliarse y evita iterar sobre 16 millones de píxeles."""
    small = 256
    img = Image.new("RGB", (small, small))
    px = img.load()
    for y in range(small):
        for x in range(small):
            px[x, y] = gradient_color((x + y) / (2 * (small - 1)))
    return img.resize((size, size), Image.BICUBIC)


def main() -> None:
    s = lambda v: v * SCALE  # noqa: E731

    base = diagonal_gradient(N)

    plate = Image.new("L", (N, N), 0)
    ImageDraw.Draw(plate).rounded_rectangle(
        [s(BOX[0]), s(BOX[1]), s(BOX[2]), s(BOX[3])],
        radius=s(BOX_RADIUS),
        fill=255,
    )

    icon = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    icon.paste(base, (0, 0), plate)

    glyph = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(glyph)
    d.rounded_rectangle(
        [s(SHAFT[0]), s(SHAFT[1]), s(SHAFT[2]), s(SHAFT[3])],
        radius=s(PILL_RADIUS),
        fill=255,
    )
    d.polygon([(s(x), s(y)) for x, y in HEAD], fill=255)
    for x0, y_top, x1 in BARS:
        d.rounded_rectangle(
            [s(x0), s(y_top), s(x1), s(BARS_BASE)],
            radius=s(PILL_RADIUS),
            fill=255,
        )

    white = Image.new("RGBA", (N, N), (255, 255, 255, 255))
    icon = Image.composite(white, icon, glyph)

    out = Path(__file__).parent / "icon-source.png"
    icon.resize((SIZE, SIZE), Image.LANCZOS).save(out)
    print(f"escrito {out} ({SIZE}x{SIZE})")


if __name__ == "__main__":
    main()

"""Regenerate the repository-owned vector mark and size-specific Windows assets.

Development tool only: Python + Pillow. No application/runtime dependency.
Each raster is drawn from geometry at its own size (8x coverage sampling);
the small optical master uses integer-aligned, thicker panes.
"""
from pathlib import Path
import io
import struct
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
PALETTE = {"edge": "#B8C8D4", "base": "#252B32",
           "accent": "#59C8DF", "fold": "#B6EDF5"}


def shapes(small):
    if small:
        return 16, [
            ("rect", (1, 1, 13, 13), 2, "edge"),
            ("rect", (2, 2, 12, 12), 1, "base"),
            ("rect", (3, 3, 15, 15), 2, "edge"),
            ("rect", (4, 4, 14, 14), 1, "base"),
            ("poly", (5, 5, 13, 5, 8, 13, 5, 13), 0, "accent"),
            ("poly", (13, 9, 13, 13, 10, 13), 0, "fold"),
        ]
    return 32, [
        ("rect", (3, 3, 25, 25), 5, "edge"),
        ("rect", (5, 5, 23, 23), 3, "base"),
        ("rect", (7, 7, 29, 29), 5, "edge"),
        ("rect", (9, 9, 27, 27), 3, "base"),
        ("poly", (11, 11, 25, 11, 17, 25, 11, 25), 0, "accent"),
        ("poly", (25, 16, 25, 25, 20, 25), 0, "fold"),
    ]


def svg(small):
    base, parts = shapes(small)
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {base} {base}">']
    for kind, coords, radius, color in parts:
        fill = PALETTE[color]
        if kind == "rect":
            x, y, right, bottom = coords
            out.append(f'  <rect x="{x}" y="{y}" width="{right-x}" height="{bottom-y}" rx="{radius}" fill="{fill}"/>')
        else:
            points = " ".join(f"{coords[i]},{coords[i+1]}" for i in range(0, len(coords), 2))
            out.append(f'  <polygon points="{points}" fill="{fill}"/>')
    return "\n".join(out + ["</svg>", ""])


def raster(size, small):
    base, parts = shapes(small)
    aa = 8
    im = Image.new("RGBA", (size * aa, size * aa))
    draw = ImageDraw.Draw(im)
    # Snap small masters to output pixels before coverage sampling.
    def coord(v):
        return (round(v * size / base) if size <= 32 else v * size / base) * aa
    for kind, coords, radius, color in parts:
        scaled = [coord(v) for v in coords]
        if kind == "rect":
            # Pillow includes the last pixel; SVG uses half-open geometry.
            scaled[2] -= 1
            scaled[3] -= 1
            draw.rounded_rectangle(scaled, radius=radius * size / base * aa,
                                   fill=PALETTE[color])
        else:
            draw.polygon(list(zip(scaled[::2], scaled[1::2])), fill=PALETTE[color])
    return im.resize((size, size), Image.Resampling.LANCZOS)


def ico(name, images):
    data = []
    for im in images:
        stream = io.BytesIO()
        im.save(stream, format="PNG")
        data.append(stream.getvalue())
    offset = 6 + 16 * len(images)
    entries = []
    for im, blob in zip(images, data):
        size = im.width
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0,
                                   1, 32, len(blob), offset))
        offset += len(blob)
    (ROOT / name).write_bytes(struct.pack("<HHH", 0, 1, len(images)) +
                             b"".join(entries) + b"".join(data))


for variant in ("app", "tray"):
    images = []
    for size in SIZES:
        image = raster(size, variant == "tray" or size <= 32)
        image.save(ROOT / f"nicewp-{variant}-{size}.png")
        images.append(image)
    ico(f"nicewp-{variant}.ico", images)
    (ROOT / f"nicewp-{variant}.svg").write_text(svg(variant == "tray"), encoding="utf-8")

# Actual pixels and enlarged nearest-neighbour samples, on both shell backgrounds.
sheet = Image.new("RGB", (900, 400), "#191C20")
d = ImageDraw.Draw(sheet)
d.rectangle((0, 200, 900, 400), fill="#F3F4F6")
for y, ink in ((0, "white"), (200, "#252B32")):
    d.text((16, y + 12), "NiceWP | desktop panes + diagonal wallpaper fold", fill=ink)
    for i, size in enumerate(SIZES[:7]):
        im = Image.open(ROOT / f"nicewp-app-{size}.png")
        x = 20 + i * 90
        sheet.paste(im, (x, y + 50), im)
        d.text((x, y + 126), str(size), fill=ink)
    tray = Image.open(ROOT / "nicewp-tray-16.png")
    zoom = tray.resize((128, 128), Image.Resampling.NEAREST)
    sheet.paste(zoom, (730, y + 40), zoom)
    d.text((730, y + 176), "tray 16 px / 8x", fill=ink)
sheet.save(ROOT / "preview.png")

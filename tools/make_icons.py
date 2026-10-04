#!/usr/bin/env python3
"""Generează Resources/app.ico din PNG-ul sursă.

Rulare: python3 tools/make_icons.py
Necesită Pillow (pip install pillow).
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Keep-Alive Monitor Icon.png"
OUT = ROOT / "Resources"
SIZES = [(256, 256), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]


def main() -> None:
    img = Image.open(SRC).convert("RGBA")

    # Fundalul alb din jurul pătratului rotunjit devine transparent (flood fill din colțuri).
    w, h = img.size
    for corner in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
        ImageDraw.floodfill(img, corner, (0, 0, 0, 0), thresh=60)

    # Decupăm strâns pe conținutul rămas.
    bbox = img.getchannel("A").getbbox()
    img = img.crop(bbox)

    # Canvas pătrat, altfel nivelurile .ico ies ne-pătrate (ex. 255x256).
    side = max(img.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(img, ((side - img.width) // 2, (side - img.height) // 2))
    img = square

    OUT.mkdir(exist_ok=True)
    img.save(OUT / "app.ico", format="ICO", sizes=SIZES)

    print(f"OK: {OUT / 'app.ico'} ({img.size[0]}x{img.size[1]} sursă)")


if __name__ == "__main__":
    main()

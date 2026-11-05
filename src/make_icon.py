#!/usr/bin/env python3
"""Draws icon.ico for the launcher: a ship on the horizon, in the colors of the window. Run it again only if the drawing changes. Needs Pillow."""
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
SIZE = 1024  # drawn large and reduced, so the edges come out smooth


def draw() -> Image.Image:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, SIZE - 1, SIZE - 1), 190, fill=255)
    background = Image.new("RGBA", (SIZE, SIZE))
    pixels = background.load()
    horizon = 640
    top, middle, low = (10, 26, 42), (36, 78, 98), (238, 168, 92)
    for y in range(SIZE):
        if y < horizon:
            t = y / horizon
            a, b, u = (top, middle, t / 0.72) if t < 0.72 else (middle, low, (t - 0.72) / 0.28)
            c = tuple(int(a[i] + (b[i] - a[i]) * u) for i in range(3))
        else:
            t = (y - horizon) / (SIZE - horizon)
            c = (int(30 - 22 * t), int(64 - 46 * t), int(80 - 52 * t))
        for x in range(SIZE):
            pixels[x, y] = (*c, 255)
    image.paste(background, (0, 0), mask)
    hull = (7, 15, 24, 255)
    # Facing right, as in the window; thicker than there, so it reads at 16 px.
    d.polygon([(90, 590), (760, 560), (950, 500), (880, 680), (150, 680)], fill=hull)
    d.polygon([(250, 590), (250, 490), (650, 490), (690, 580)], fill=hull)
    d.polygon([(440, 490), (460, 300), (560, 300), (590, 490)], fill=hull)
    d.rectangle((485, 230, 535, 300), fill=hull)
    d.polygon([(300, 490), (315, 360), (390, 360), (405, 490)], fill=hull)
    d.line((690, 545, 860, 500), fill=hull, width=26)
    d.rectangle((640, 520, 730, 570), fill=hull)
    d.line((270, 560, 130, 540), fill=hull, width=26)
    # The sea in front of the hull
    d.polygon([(0, 672), (SIZE, 672), (SIZE, SIZE), (0, SIZE)], fill=(10, 34, 50, 255))
    result = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    result.paste(image, (0, 0), mask)
    return result


if __name__ == "__main__":
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    draw().resize((256, 256), Image.LANCZOS).save(HERE / "icon.ico", sizes=sizes)
    print("icon.ico", (HERE / "icon.ico").stat().st_size, "bytes")

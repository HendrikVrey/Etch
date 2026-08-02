#!/usr/bin/env python3
"""Pack assets/etch.ico from the three SVG drawings in this folder.

Run from anywhere:  python assets/build-icon.py

Why this file exists at all
--------------------------
Windows asks for ten sizes and reads them at wildly different scales, so this is not
one drawing resized ten times. Padding and hairlines that read as detail at 256 px are
noise at 16 px, and a two-stop gradient bands once the plate is a dozen pixels tall.
There are therefore three drawings, and as the size falls the plate tightens, the mark
grows, and the gradient goes flat:

    etch-tiny.svg    16, 20, 24    flat plate, no hairline, mark at 1.20x
    etch-small.svg   32, 40, 48    gradient plate, no hairline, mark at 1.14x
    etch.svg         64+           gradient plate, 1 px inner hairline, mark at 1.00x

The 32-48 group is one drawing on purpose: that is the taskbar across 100-150% DPI,
and the mark must not change weight as somebody drags a window between monitors.

Every frame is rendered from vector at its own resolution. Downsampling one big raster
is the thing this script exists to avoid.

Two tool traps are worked around here rather than rediscovered
--------------------------------------------------------------
1.  cairosvg renders <mask> wrongly -- the masked element comes out displaced. The E is
    therefore a single path with fill-rule evenodd punching its own counters, which is
    also more portable than a mask. Nothing in the SVGs needs a mask; keep it that way.

2.  Pillow's ICO writer cannot take a different image per frame, and this file's whole
    point is a different image per frame. So the container is written by hand below.
    It is about forty lines and it is the only way to get what Windows actually wants.
"""

from __future__ import annotations

import struct
from io import BytesIO
from pathlib import Path

import cairosvg
from PIL import Image

HERE = Path(__file__).resolve().parent

# Size -> source drawing. Ordered smallest first, which is also the order Windows
# prefers to find them in.
FRAMES: list[tuple[int, str]] = [
    (16, "etch-tiny.svg"),
    (20, "etch-tiny.svg"),
    (24, "etch-tiny.svg"),
    (32, "etch-small.svg"),
    (40, "etch-small.svg"),
    (48, "etch-small.svg"),
    (64, "etch.svg"),
    (96, "etch.svg"),
    (128, "etch.svg"),
    (256, "etch.svg"),
]

# At and above this, frames are stored as PNG; below it, as BMP. 64 is the conventional
# split and the safe one: PNG-compressed frames are only reliably understood by Vista
# and later, and the small sizes are exactly the ones an old shell path might read.
PNG_FROM = 64

# Extra PNGs written alongside the .ico for the README and for anywhere a raster is
# easier to hand around than an .ico.
LOOSE_PNGS = (256, 512)


def render(svg: str, size: int) -> Image.Image:
    """Render one SVG to an RGBA image at exactly size x size."""
    png = cairosvg.svg2png(
        url=str(HERE / svg),
        output_width=size,
        output_height=size,
    )
    return Image.open(BytesIO(png)).convert("RGBA")


def as_png(image: Image.Image) -> bytes:
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def as_bmp(image: Image.Image) -> bytes:
    """Encode one frame as the headerless BMP an .ico expects.

    Three things here are easy to get wrong and silently produce an icon that renders
    as garbage or not at all:

      * biHeight is *twice* the real height. The field describes the colour rows plus
        the AND-mask rows that follow them, even when the mask is unused.
      * Rows run bottom-up, and BGRA rather than RGBA.
      * The 1bpp AND mask must still be present and its rows padded to 4 bytes, even
        when every bit is zero -- which it is here, because the alpha channel already
        carries the transparency.
    """
    width, height = image.size

    pixels = image.load()
    rows = bytearray()
    for y in reversed(range(height)):
        for x in range(width):
            r, g, b, a = pixels[x, y]
            rows += bytes((b, g, r, a))

    # AND mask: 1 bit per pixel, rows padded to a 4-byte boundary, all zero.
    mask_stride = ((width + 31) // 32) * 4
    mask = bytes(mask_stride * height)

    header = struct.pack(
        "<IiiHHIIiiII",
        40,                     # biSize
        width,                  # biWidth
        height * 2,             # biHeight -- colour rows + mask rows
        1,                      # biPlanes
        32,                     # biBitCount
        0,                      # biCompression (BI_RGB)
        len(rows) + len(mask),  # biSizeImage -- see below
        0, 0,                   # pixels-per-metre, unused
        0, 0,                   # colours used / important, unused
    )

    # biSizeImage is documented as permitted to be 0 for BI_RGB, and writing 0 does
    # produce an icon Windows renders correctly. It is written out in full anyway,
    # counting the mask as well as the colour rows, because some readers trust the
    # field rather than recomputing it, and a value that is right costs nothing.
    return bytes(header) + bytes(rows) + mask


def build() -> None:
    payloads: list[tuple[int, bytes]] = []
    for size, svg in FRAMES:
        image = render(svg, size)
        payloads.append((size, as_png(image) if size >= PNG_FROM else as_bmp(image)))

    count = len(payloads)
    directory = bytearray(struct.pack("<HHH", 0, 1, count))

    # Every payload sits after the header and the full directory, so offsets are known
    # only once all of them have been encoded.
    offset = 6 + 16 * count
    for size, payload in payloads:
        directory += struct.pack(
            "<BBBBHHII",
            size if size < 256 else 0,   # 0 means 256 -- the field is one byte
            size if size < 256 else 0,
            0,                           # palette size, 0 for truecolour
            0,                           # reserved
            1,                           # colour planes
            32,                          # bits per pixel
            len(payload),
            offset,
        )
        offset += len(payload)

    ico = HERE / "etch.ico"
    ico.write_bytes(bytes(directory) + b"".join(p for _, p in payloads))
    print(f"{ico.name}: {count} frames, {ico.stat().st_size:,} bytes")

    for size in LOOSE_PNGS:
        path = HERE / f"etch-{size}.png"
        path.write_bytes(as_png(render("etch.svg", size)))
        print(f"{path.name}: {path.stat().st_size:,} bytes")


if __name__ == "__main__":
    build()

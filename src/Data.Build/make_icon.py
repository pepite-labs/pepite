#!/usr/bin/env python3
"""Generate the icons of Pépite — the « pépite » cookie logo, own drawing (no third-party art).

The master drawing is ``branding/pepite.svg`` (100-unit grid): a bitten cookie (two tones), four chocolate
chips and an elongated chip standing for the text caret. This script redraws the same primitives with Pillow.
Up to 32 px (the tray sizes) the drawing is simplified so that it stays recognisable in the notification area: one rim, a
single deep bite, three larger chips and the caret chip, no fine detail.
Rendered at 8× and reduced (box filter); the .ico container is written with ``struct`` (32-bpp DIB entries ≤ 64 px
plus one PNG 256 px).

Outputs (committed): ../Predict.Keyboard/Assets/app.svg, app.ico (16 20 24 32 40 48 64 256) and app-paused.ico (grey
variant shown while Pépite is paused, D5). The tray loads the frame of the system small-icon size: 16 px at 100 %,
20 at 125 %, 24 at 150 %, 32 at 200 %.
Usage: python -I make_icon.py [--out <dir>] [--preview <dir>]
Deterministic: the same Pillow version gives byte-identical files.
"""
import argparse
import io
import os
import struct

from PIL import Image, ImageDraw

SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
SS = 8   # supersampling factor

# colours: rim, dough, chips
NORMAL = ("#E8A64B", "#F2BC66", "#4A2C1D")
PAUSED = ("#9CA3AF", "#C9CED6", "#4B5563")

# 100-unit geometry of pepite.svg
RIM = (50, 52, 44)
DOUGH = (50, 52, 36)
BITES = ((90, 16, 17), (97, 36, 9))
CHIPS = ((34, 40, 8.5), (58, 66, 9.5), (30, 70, 6), (60, 36, 5.5))
CARET = (70, 46, 6, 20, 3)   # x, y, w, h, r

# simplified geometry for 16–32 px (tray), in the same 100-unit grid: bigger chips, one bite, thicker caret
SMALL_RIM = (50, 50, 49)
SMALL_DOUGH = (50, 50, 38)
SMALL_BITES = ((96, 8, 26),)
SMALL_CHIPS = ((32, 38, 12), (52, 72, 12), (24, 70, 8))
SMALL_CARET = (66, 34, 13, 36, 6)


def render(px, colours):
    rim_c, dough_c, chip_c = colours
    big = px * SS
    k = big / 100.0
    small = px <= 32
    rim, dough, bites, chips, caret = ((SMALL_RIM, SMALL_DOUGH, SMALL_BITES, SMALL_CHIPS, SMALL_CARET) if small
                                       else (RIM, DOUGH, BITES, CHIPS, CARET))

    def circ(d, c, fill):
        cx, cy, r = c
        d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k - 1, (cy + r) * k - 1], fill=fill)

    body = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(body)
    circ(d, rim, rim_c)
    circ(d, dough, dough_c)
    # the bite: cut out of the cookie body only (the chips are drawn after, as in the SVG masks)
    mask = Image.new("L", (big, big), 255)
    md = ImageDraw.Draw(mask)
    for b in bites:
        circ(md, b, 0)
    alpha = body.getchannel("A")
    body.putalpha(Image.composite(alpha, Image.new("L", (big, big), 0), mask))
    d = ImageDraw.Draw(body)
    for c in chips:
        circ(d, c, chip_c)
    x, y, w, h, r = caret
    d.rounded_rectangle([x * k, y * k, (x + w) * k - 1, (y + h) * k - 1], r * k, fill=chip_c)
    return body.resize((px, px), Image.BOX)


def dib(im):
    """32-bpp BGRA DIB (bottom-up) + 1-bpp AND mask, as stored in an .ico entry."""
    w, h = im.size
    px = im.load()
    rows = []
    for y in range(h - 1, -1, -1):
        rows.append(b"".join(struct.pack("BBBB", px[x, y][2], px[x, y][1], px[x, y][0], px[x, y][3])
                             for x in range(w)))
    stride = ((w + 31) // 32) * 4
    mask = b""
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if px[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += bytes(row)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(rows) * w * 4 + len(mask), 0, 0, 0, 0)
    return header + b"".join(rows) + mask


def png_bytes(im):
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def write_ico(path, images):
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for im in images:
        w = im.size[0]
        data = png_bytes(im) if w >= 256 else dib(im)
        entries.append(struct.pack("<BBBBHHII", 0 if w >= 256 else w, 0 if w >= 256 else w, 0, 0, 1, 32,
                                   len(data), offset))
        blobs.append(data)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs))


def svg(colours):
    rim_c, dough_c, chip_c = colours

    def c(g, fill):
        return f'<circle cx="{g[0]}" cy="{g[1]}" r="{g[2]}" fill="{fill}"/>'
    masks = "".join(f'<mask id="m{i}"><rect width="100" height="100" fill="#fff"/>{c(b, "#000")}</mask>'
                    for i, b in enumerate(BITES, 1))
    body = c(RIM, rim_c) + c(DOUGH, dough_c)
    for i in range(len(BITES), 0, -1):
        body = f'<g mask="url(#m{i})">{body}</g>'
    x, y, w, h, r = CARET
    chips = "".join(c(g, chip_c) for g in CHIPS) + f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{chip_c}"/>'
    return ('<?xml version="1.0" encoding="UTF-8"?>\n'
            '<!-- Pépite — logo : dessin original, généré par Data.Build/make_icon.py (source : branding/pepite.svg) -->\n'
            f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="256" height="256"><defs>{masks}</defs>'
            f'{body}{chips}</svg>\n')


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(here, "..", "Predict.Keyboard", "Assets"))
    ap.add_argument("--preview", help="also write PNG preview strips (actual size and 8x nearest-neighbour) here")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    for name, colours in (("app", NORMAL), ("app-paused", PAUSED)):
        imgs = [render(s, colours) for s in SIZES]
        write_ico(os.path.join(a.out, name + ".ico"), imgs)
        if a.preview:
            os.makedirs(a.preview, exist_ok=True)
            for bg, tag in (((255, 255, 255, 255), "light"), ((32, 32, 32, 255), "dark")):
                strip = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES), 256), bg)
                x = 0
                for im in imgs:
                    strip.alpha_composite(im, (x, 0))
                    x += im.size[0] + 10
                strip.save(os.path.join(a.preview, f"{name}-{tag}-preview.png"))
                tray = [im for im in imgs if im.size[0] <= 32]
                zoom = Image.new("RGBA", (sum(i.size[0] * 8 + 16 for i in tray), 32 * 8), bg)
                x = 0
                for im in tray:
                    zoom.alpha_composite(im.resize((im.size[0] * 8, im.size[1] * 8), Image.NEAREST), (x, 0))
                    x += im.size[0] * 8 + 16
                zoom.save(os.path.join(a.preview, f"{name}-{tag}-tray-zoom.png"))
    with open(os.path.join(a.out, "app.svg"), "w", encoding="utf-8", newline="\n") as f:
        f.write(svg(NORMAL))
    print("icons ->", os.path.abspath(a.out))


if __name__ == "__main__":
    main()

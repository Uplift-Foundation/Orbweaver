#!/usr/bin/env python3
"""Checks the phone and Watch icons make-icons.py writes into docs/brand/mobile/ (CB-323).

    python3 tools/check-brand-icons.py              # check docs/brand/mobile; exit 1 on a failure
    python3 tools/check-brand-icons.py --dir DIR    # check another directory (the tests use this)
    python3 tools/check-brand-icons.py --no-regen   # skip the "matches the generator" step

What it asserts, per file:
  ios-light-1024.png    8-bit RGB, no alpha channel at all (the App Store refuses one), and the
                        corners are the tile colour: the tile runs edge to edge.
  ios-dark-1024.png     RGBA, corners fully transparent, an opaque orb at the centre.
  ios-tinted-1024.png   RGBA, corners fully transparent, r == g == b wherever there is any alpha
                        (iOS recolours it, so it has to be a true greyscale).
  watchos-1024.png      opaque, and nothing but tile colour outside WATCH_SAFE (0.47) of the size
                        from the centre, because watchOS masks the inscribed circle.
  mark-template-256.png RGBA with only alpha varying: every pixel the same colour, and an alpha
                        range that is neither empty nor solid.
and, for the three iOS 1024s and the Watch one, that when area-averaged down to 29 px (the
Settings row, the smallest size iOS draws) the mark still reads: the orb against the tile, and
the outer ring against the tile. Thresholds are in LIGHT_ORB_CONTRAST and friends below, each
with the measured value of the shipped file and of the negative control next to it.

Finally it decodes each file and compares the pixels with a fresh render from make-icons.py, so a
hand-edited PNG fails here. Pixels, not bytes: the encoder calls zlib, whose output differs between
the macOS and Windows images, and the decoded pixels do not.

Pure stdlib, like the generator.
"""

import argparse
import importlib.util
import math
import os
import struct
import sys
import zlib

_HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(_HERE)
DEFAULT_DIR = os.path.join(ROOT, "docs", "brand", "mobile")

_spec = importlib.util.spec_from_file_location("make_icons", os.path.join(_HERE, "make-icons.py"))
mk = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mk)

EDGE_SAG = 0.62  # _sagging_ring's default in make-icons.py
SMALL = 29  # pt of the Settings row, the smallest size iOS draws the app icon at

# Thresholds. Measured on the shipped files; see the test for the control they must reject.
LIGHT_ORB_CONTRAST = 3.0    # WCAG's bar for a non-text mark. Shipped: 4.67
RING_COVERAGE = 0.35        # ring strength (see legibility). Shipped, the 48 cut: 0.39. The 512 cut: 0.28


class PngError(Exception):
    pass


def read_png(path_or_bytes):
    """Returns (width, height, color_type, rows) for an 8-bit RGB/RGBA PNG written with filter 0
    (which is all make-icons.py writes). Anything else is refused with a message that says so,
    because a file this decoder cannot read was not made by the generator."""
    if isinstance(path_or_bytes, bytes):
        data = path_or_bytes
    else:
        with open(path_or_bytes, "rb") as fh:
            data = fh.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise PngError("not a PNG")
    pos, idat, ihdr = 8, b"", None
    while pos < len(data):
        n, tag = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + n]
        if tag == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat += body
        pos += 12 + n
    if ihdr is None:
        raise PngError("no IHDR")
    w, h, depth, ctype, _, _, interlace = ihdr
    if depth != 8 or ctype not in (2, 6) or interlace:
        raise PngError(f"need 8-bit non-interlaced RGB/RGBA, got depth {depth} type {ctype}")
    bpp = 3 if ctype == 2 else 4
    raw = zlib.decompress(idat)
    stride = 1 + w * bpp
    rows = []
    for y in range(h):
        if raw[y * stride] != 0:
            raise PngError("scanline filter is not 0: this PNG was not written by make-icons.py")
        rows.append(raw[y * stride + 1:(y + 1) * stride])
    return w, h, ctype, rows


def pixel(img, x, y):
    _, _, ctype, rows = img
    bpp = 3 if ctype == 2 else 4
    r, g, b = rows[y][x * bpp:x * bpp + 3]
    return (r, g, b, 255 if ctype == 2 else rows[y][x * bpp + 3])


def luminance(rgb):
    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * lin(rgb[0]) + 0.7152 * lin(rgb[1]) + 0.0722 * lin(rgb[2])


def contrast(a, b):
    la, lb = luminance(a), luminance(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def area_average(img, out):
    """Box-filters the image to out x out with exact fractional pixel weights, in premultiplied
    space so a transparent surround does not drag the colour toward black. Returns rows of
    (r, g, b, a) floats with r, g, b un-premultiplied and a in 0..255."""
    w, h, ctype, rows = img
    bpp = 3 if ctype == 2 else 4
    k = w / out

    def spans(i):
        lo, hi = i * k, (i + 1) * k
        return [(j, min(hi, j + 1) - max(lo, j)) for j in range(int(lo), min(w, math.ceil(hi)))]

    sp = [spans(i) for i in range(out)]
    # horizontal pass, per source row: premultiplied r, g, b and a per output column
    hz = []
    for y in range(h):
        row = rows[y]
        line = []
        for s in sp:
            r = g = b = a = tw = 0.0
            for j, wt in s:
                o = j * bpp
                al = (row[o + 3] if bpp == 4 else 255) / 255.0
                r += row[o] * al * wt
                g += row[o + 1] * al * wt
                b += row[o + 2] * al * wt
                a += al * wt
                tw += wt
            line.append((r / tw, g / tw, b / tw, a / tw))
        hz.append(line)
    res = []
    for sy in sp:
        line = []
        for x in range(out):
            r = g = b = a = tw = 0.0
            for j, wt in sy:
                p = hz[j][x]
                r += p[0] * wt
                g += p[1] * wt
                b += p[2] * wt
                a += p[3] * wt
                tw += wt
            r, g, b, a = r / tw, g / tw, b / tw, a / tw
            line.append((r / a, g / a, b / a, a * 255.0) if a > 1e-9 else (0.0, 0.0, 0.0, 0.0))
        res.append(line)
    return res


def legibility(img, tile=mk.TILE, web=mk.IDLE):
    """(orb contrast, outer-ring strength) of an opaque tile image averaged down to SMALL px.

    Orb contrast is the WCAG ratio between the hub pixel and the tile colour.

    Ring strength is how far the brightest pixel within one pixel of the middle of each outer-ring
    edge has moved from the tile towards the thread colour, in luminance, taking the weakest of the
    six edges so one lost edge counts. The middle of an edge is used on purpose: the ring's vertices
    sit on a spoke, where two strokes overlap and even a hairline reads as solid, which is the one
    place that cannot tell a heavy thread from a light one. 0 means the ring is gone, 1 means a
    full-strength thread."""
    small = area_average(img, SMALL)
    c = SMALL // 2
    orb = contrast(small[c][c][:3], tile)

    lt, lw = luminance(tile), luminance(web)
    span = lw - lt
    # The sagging edge is a quadratic curve whose control point sits at SAG of the edge
    # midpoint's radius, so its own midpoint is at (1 + SAG) / 2 of it.
    r = mk.WEB_REACH * math.cos(math.radians(30)) * (1 + EDGE_SAG) / 2 / 512 * SMALL
    worst = 1.0
    for i in range(6):
        a = math.radians(-60 + i * 60)
        vx, vy = SMALL / 2 + r * math.cos(a), SMALL / 2 + r * math.sin(a)
        best = 0.0
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                x, y = int(vx) + dx, int(vy) + dy
                if 0 <= x < SMALL and 0 <= y < SMALL:
                    best = max(best, (luminance(small[y][x][:3]) - lt) / span)
        worst = min(worst, best)
    return orb, worst


def _opaque_tile_corners(img, tile):
    w, h = img[0], img[1]
    return all(pixel(img, x, y)[:3] == tile for x, y in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)))


def _corners_transparent(img):
    w, h = img[0], img[1]
    return all(pixel(img, x, y)[3] == 0 for x, y in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)))


def check_light(img, name="ios-light-1024.png"):
    out = []
    if img[2] != 2:
        out.append(f"{name}: has an alpha channel; the App Store refuses it (need 8-bit RGB)")
    if not _opaque_tile_corners(img, mk.TILE):
        out.append(f"{name}: corners are not the tile colour; the tile must run edge to edge")
    out += _check_small(img, name)
    return out


def check_dark(img, name="ios-dark-1024.png"):
    out = []
    if img[2] != 6:
        out.append(f"{name}: needs an alpha channel")
    elif not _corners_transparent(img):
        out.append(f"{name}: corners are not transparent; iOS supplies the dark backdrop")
    mid = img[0] // 2
    if img[2] == 6 and pixel(img, mid, mid)[3] != 255:
        out.append(f"{name}: the orb at the centre is not opaque")
    return out


def check_tinted(img, name="ios-tinted-1024.png"):
    out = []
    if img[2] != 6:
        return [f"{name}: needs an alpha channel"]
    if not _corners_transparent(img):
        out.append(f"{name}: corners are not transparent")
    bad = 0
    for y in range(img[1]):
        row = img[3][y]
        for x in range(img[0]):
            r, g, b, a = row[x * 4:x * 4 + 4]
            if a and not (r == g == b):
                bad += 1
    if bad:
        out.append(f"{name}: {bad} pixels with alpha are not greyscale (r == g == b); iOS recolours this")
    return out


def check_watch(img, name="watchos-1024.png"):
    out = []
    if img[2] != 2:
        out.append(f"{name}: has an alpha channel (need 8-bit RGB, full bleed)")
    w = img[0]
    limit = mk.WATCH_SAFE * w
    c = w / 2.0
    stray = 0
    for y in range(img[1]):
        for x in range(w):
            if math.hypot(x + 0.5 - c, y + 0.5 - c) > limit and pixel(img, x, y)[:3] != mk.TILE:
                stray += 1
    if stray:
        out.append(f"{name}: {stray} pixels outside {mk.WATCH_SAFE} of the size from the centre are not "
                   "the tile colour; watchOS masks the inscribed circle and would clip the web")
    out += _check_small(img, name)
    return out


def check_template(img, name="mark-template-256.png"):
    out = []
    if img[2] != 6:
        return [f"{name}: needs an alpha channel"]
    colours, alphas = set(), set()
    for y in range(img[1]):
        row = img[3][y]
        for x in range(img[0]):
            r, g, b, a = row[x * 4:x * 4 + 4]
            alphas.add(a)
            if a:
                colours.add((r, g, b))
    if len(colours) != 1:
        out.append(f"{name}: {len(colours)} distinct colours where alpha is set; a template carries only alpha")
    if 0 not in alphas or 255 not in alphas:
        out.append(f"{name}: alpha runs {min(alphas)}..{max(alphas)}; a mark needs both clear and solid pixels")
    return out


def _check_small(img, name):
    orb, ring = legibility(img)
    out = []
    if orb < LIGHT_ORB_CONTRAST:
        out.append(f"{name}: at {SMALL} px the orb's contrast with the tile is {orb:.2f}:1, under {LIGHT_ORB_CONTRAST}:1")
    if ring < RING_COVERAGE:
        out.append(f"{name}: at {SMALL} px the weakest outer-ring vertex reaches {ring:.2f} of the thread colour, "
                   f"under {RING_COVERAGE}")
    return out


CHECKS = (
    ("ios-light-1024.png", check_light, mk.draw_ios_light, True),
    ("ios-dark-1024.png", check_dark, mk.draw_ios_dark, False),
    ("ios-tinted-1024.png", check_tinted, mk.draw_ios_tinted, False),
    ("watchos-1024.png", check_watch, mk.draw_watch, True),
    ("mark-template-256.png", check_template, mk.draw_mark_template, False),
)


def decoded_pixels(img):
    """Row bytes flattened to a list of (r, g, b, a), for comparing with a render."""
    out = []
    for y in range(img[1]):
        for x in range(img[0]):
            out.append(pixel(img, x, y))
    return out


def check_dir(directory, regen=True):
    failures = []
    for name, check, draw, opaque in CHECKS:
        path = os.path.join(directory, name)
        if not os.path.exists(path):
            failures.append(f"{name}: missing; run python3 tools/make-icons.py")
            continue
        try:
            img = read_png(path)
        except PngError as e:
            failures.append(f"{name}: {e}")
            continue
        failures += check(img)
        if regen:
            fresh = draw()
            if opaque:
                fresh = [(r, g, b, 255) for r, g, b, _ in fresh]
            if decoded_pixels(img) != fresh:
                failures.append(f"{name}: pixels differ from what make-icons.py draws; "
                                "regenerate it rather than editing the PNG")
    return failures


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--dir", default=DEFAULT_DIR)
    ap.add_argument("--no-regen", action="store_true")
    args = ap.parse_args(argv)
    failures = check_dir(args.dir, regen=not args.no_regen)
    for f in failures:
        print("FAIL", f)
    if failures:
        return 1
    print(f"ok: {len(CHECKS)} files in {mk._shown(args.dir)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

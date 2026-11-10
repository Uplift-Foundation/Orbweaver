#!/usr/bin/env python3
"""Draws Orbweaver's icons so the repo doesn't need binary art checked in
by hand. Regenerate with:

    python3 tools/make-icons.py

Outputs (PNG, RGBA, straight alpha — except the .ico, noted below):
  Assets/tray-idle.png        menu-bar / notification-area icon, one per state
  Assets/tray-generating.png
  Assets/tray-waiting.png
  Assets/tray-idle-muted.png  the same three with a slash through them, shown
  Assets/tray-generating-muted.png  while the global mute is on (CB-298)
  Assets/tray-waiting-muted.png
  Assets/appicon-1024.png     source art for the .app bundle's .icns
  Assets/Orbweaver.ico        multi-size Windows icon: the .exe's embedded icon
                              (csproj ApplicationIcon) and the installer's
                              SetupIconFile
  docs/brand/mobile/          the phone and Watch icons (CB-323), see "The mobile
                              set" below; tools/check-brand-icons.py checks them

The mark (CB-297) is a spiderweb with the orb at its hub: six spokes and two
sagging rings in slate, a violet orb, on a near-black rounded tile. Orbweaver
weaves a web of orbs, so the orb sits where a spider would.

Pure stdlib (zlib + struct) — no Pillow, so it runs on a stock macOS python3.
Everything is drawn by a small rasterizer below: distance-based antialiasing for
circles, rounded rectangles and round-capped strokes. That is plenty for these
shapes and, unlike the supersampled shader it replaced, only visits the pixels
a stroke can touch, which is what keeps a 1024px web fast in pure Python.
"""

import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(HERE, "Assets")
MOBILE = os.path.join(HERE, "docs", "brand", "mobile")

# The *default* three colors — OrbColors.DefaultIdle / DefaultGenerating /
# DefaultWaiting in OrbColors.cs. Keep them in sync by hand.
#
# They are only defaults now: the settings window lets you pick your own, and
# TrayController re-tints the tray PNGs at runtime to match. That works because
# draw_tray below paints every pixel one constant RGB and varies only alpha, which makes
# each tray-*.png a pure alpha mask over a single color — so what actually
# matters at runtime is the *alpha* channel, i.e. the web-and-orb geometry. Change the
# geometry here and a custom-colored icon follows; change these colors and only
# the shipped defaults move.
#
# appicon-1024.png and Orbweaver.ico stay on these colors whatever the user
# picks. Those are baked into the .app's .icns at build time and into the .exe's
# Win32 resources, and neither can follow a live setting.
IDLE = (0x5B, 0x7A, 0x94)
GENERATING = (0x8B, 0x6F, 0xD1)
WAITING = (0xE8, 0x98, 0x3B)

def png_bytes(size, pixels, opaque=False):
    """pixels: flat list of (r, g, b, a) tuples, row-major, length size*size.

    opaque=True writes 8-bit RGB with no alpha channel at all (the alpha is dropped,
    so the caller must have drawn every pixel at a == 255). iOS's 1024 App Store icon
    must not carry an alpha channel, and a fully opaque RGBA is not the same thing."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter type 0 (None) per scanline
        for x in range(size):
            r, g, b, a = pixels[y * size + x]
            raw += bytes((r, g, b)) if opaque else bytes((r, g, b, a))

    def chunk(tag, data):
        out = struct.pack(">I", len(data)) + tag + data
        return out + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 2 if opaque else 6, 0, 0, 0)  # 8-bit RGB / RGBA
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def write_png(path, size, pixels, opaque=False):
    with open(path, "wb") as fh:
        fh.write(png_bytes(size, pixels, opaque))


def write_ico(path, images):
    """images: list of (size, png_data). Writes a PNG-compressed .ico.

    An .ico is just a directory of images, and since Vista each entry may be a
    whole PNG rather than the old BMP-with-AND-mask layout — which is what lets
    this stay in stdlib, because the PNG encoder above already exists. Windows 10
    and 11 and Inno Setup both read this form.
    """
    count = len(images)
    # 6-byte ICONDIR, then one 16-byte ICONDIRENTRY per image, then the payloads.
    offset = 6 + 16 * count
    directory = bytearray()
    payloads = bytearray()

    for size, data in images:
        directory += struct.pack(
            "<BBBBHHII",
            # 0 means 256 in a single byte field, which is why 256 is the cap.
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0,  # palette size: 0 for truecolor
            0,  # reserved
            1,  # color planes
            32,  # bits per pixel
            len(data),
            offset,
        )
        payloads += data
        offset += len(data)

    with open(path, "wb") as fh:
        fh.write(struct.pack("<HHH", 0, 1, count))  # reserved, type 1 = icon
        fh.write(bytes(directory))
        fh.write(bytes(payloads))


class Canvas:
    """Premultiplied float RGBA, drawn with antialiased primitives."""

    def __init__(self, n):
        self.n = n
        self.r = [0.0] * (n * n)
        self.g = [0.0] * (n * n)
        self.b = [0.0] * (n * n)
        self.a = [0.0] * (n * n)

    def blend(self, i, color, cov):
        if cov <= 0.0:
            return
        k = cov * (color[3] if len(color) > 3 else 1.0)
        inv = 1.0 - k
        self.r[i] = color[0] * k + self.r[i] * inv
        self.g[i] = color[1] * k + self.g[i] * inv
        self.b[i] = color[2] * k + self.b[i] * inv
        self.a[i] = k + self.a[i] * inv

    def rounded_rect(self, cx, cy, half, radius, color):
        n = self.n
        x0, x1 = max(0, int(cx - half) - 1), min(n, int(cx + half) + 2)
        y0, y1 = max(0, int(cy - half) - 1), min(n, int(cy + half) + 2)
        inner = half - radius
        for y in range(y0, y1):
            dy = abs(y + 0.5 - cy) - inner
            for x in range(x0, x1):
                dx = abs(x + 0.5 - cx) - inner
                d = math.hypot(max(dx, 0.0), max(dy, 0.0)) + min(max(dx, dy), 0.0) - radius
                cov = 0.5 - d
                if cov > 0.0:
                    self.blend(y * n + x, color, min(1.0, cov))

    def disc(self, cx, cy, radius, color):
        n = self.n
        x0, x1 = max(0, int(cx - radius) - 1), min(n, int(cx + radius) + 2)
        y0, y1 = max(0, int(cy - radius) - 1), min(n, int(cy + radius) + 2)
        for y in range(y0, y1):
            for x in range(x0, x1):
                cov = radius + 0.5 - math.hypot(x + 0.5 - cx, y + 0.5 - cy)
                if cov > 0.0:
                    self.blend(y * n + x, color, min(1.0, cov))

    def strokes(self, polylines, width, color):
        """Round-capped, round-joined strokes. One mask for the whole group, so
        two strokes that cross, or one that doubles back, are painted once."""
        n = self.n
        half = width / 2.0
        mask = {}
        for pts in polylines:
            for (ax, ay), (bx, by) in zip(pts, pts[1:]):
                x0, x1 = max(0, int(min(ax, bx) - half) - 1), min(n, int(max(ax, bx) + half) + 2)
                y0, y1 = max(0, int(min(ay, by) - half) - 1), min(n, int(max(ay, by) + half) + 2)
                dx, dy = bx - ax, by - ay
                ll = dx * dx + dy * dy
                for y in range(y0, y1):
                    for x in range(x0, x1):
                        px, py = x + 0.5 - ax, y + 0.5 - ay
                        t = 0.0 if ll == 0 else max(0.0, min(1.0, (px * dx + py * dy) / ll))
                        cov = half + 0.5 - math.hypot(px - t * dx, py - t * dy)
                        if cov > 0.0:
                            i = y * n + x
                            if cov > mask.get(i, 0.0):
                                mask[i] = min(1.0, cov)
        for i, cov in mask.items():
            self.blend(i, color, cov)

    def pixels(self):
        out = []
        for i in range(self.n * self.n):
            a = self.a[i]
            if a <= 0.0005:
                out.append((0, 0, 0, 0))
            else:
                out.append((min(255, round(self.r[i] / a)), min(255, round(self.g[i] / a)),
                            min(255, round(self.b[i] / a)), min(255, round(a * 255))))
        return out


# --- the mark, in a 512-unit square -----------------------------------------
# Six spokes from the hub, and rings that sag toward it between spokes, which is
# what makes a hexagon read as a spiderweb and not as a nut (CB-297 drafts).

WEB_CENTER = 256.0
WEB_REACH = 185.0
TILE = (0x0F, 0x14, 0x19)  # near-black; also the ground the tray mask sits on


def _pt(r, deg):
    a = math.radians(deg)
    return WEB_CENTER + r * math.cos(a), WEB_CENTER + r * math.sin(a)


def _sagging_ring(radius, sag=0.62, steps=14):
    """A hexagon whose edges bow in toward the centre: quadratic curves, flattened."""
    v = [_pt(radius, -90 + i * 60) for i in range(6)]
    pts = [v[0]]
    for i in range(6):
        a, b = v[i], v[(i + 1) % 6]
        mx, my = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
        q = (WEB_CENTER + (mx - WEB_CENTER) * sag, WEB_CENTER + (my - WEB_CENTER) * sag)
        for s in range(1, steps + 1):
            t = s / steps
            u = 1 - t
            pts.append((u * u * a[0] + 2 * u * t * q[0] + t * t * b[0],
                        u * u * a[1] + 2 * u * t * q[1] + t * t * b[1]))
    return pts


def cut_for(size):
    """Thread weight, rings and orb radius, in 512 units, for a given pixel size.

    One drawing cannot serve 16px and 1024px: at 16px a 12-unit thread is under half
    a pixel and the web is gone. Small sizes get fewer, heavier threads and a bigger
    orb instead, so the silhouette (web with a lit hub) survives.
    """
    if size >= 128:
        return {"w": 12, "rings": (110, 185), "orb": 46}
    if size >= 48:
        return {"w": 16, "rings": (110, 185), "orb": 54}
    if size >= 24:
        return {"w": 26, "rings": (185,), "orb": 66}
    return {"w": 36, "rings": (185,), "orb": 80}


def draw_web(cv, cx, cy, scale, cut, web_color, orb_color):
    """Paints the web and its orb, with the 512-unit centre at (cx, cy)."""
    def px(p):
        return (cx + (p[0] - WEB_CENTER) * scale, cy + (p[1] - WEB_CENTER) * scale)

    lines = [[px((WEB_CENTER, WEB_CENTER)), px(_pt(WEB_REACH, -90 + i * 60))] for i in range(6)]
    lines += [[px(p) for p in _sagging_ring(r)] for r in cut["rings"]]
    cv.strokes(lines, cut["w"] * scale, web_color)
    cv.disc(cx, cy, cut["orb"] * scale, orb_color)


def draw_appicon(size, tile_fraction):
    """The web on its tile. macOS wants the tile inset in its canvas (Apple's
    template is 824 of 1024); Windows icons use nearly the whole square."""
    cv = Canvas(size)
    tile = size * tile_fraction
    cv.rounded_rect(size / 2, size / 2, tile / 2, tile * 112 / 512, (*TILE, 1.0))
    draw_web(cv, size / 2, size / 2, tile / 512, cut_for(size), (*IDLE, 1.0), (*GENERATING, 1.0))
    return cv.pixels()


# The tray mask: one colour, alpha only (see the comment on IDLE above). The orb is
# opaque and the threads a little fainter, so the hub, which carries the state colour
# you read at a glance, is the brightest thing at menu-bar size.
TRAY_CUT = {"w": 34, "rings": (185,), "orb": 78}
TRAY_WEB_ALPHA = 0.7


def draw_tray(size, color, muted=False):
    """muted=True adds the no-sound shape (CB-298): a diagonal bar from upper left
    to lower right over the web, with a transparent gap either side so it reads as
    a slash over the mark rather than merging into it. It is still one constant RGB
    with only alpha varying, which is the invariant TrayController.Tinted depends
    on: the re-tint reproduces the muted icon exactly, slash and all."""
    cv = Canvas(size)
    scale = 0.94 * size / (2 * (WEB_REACH + TRAY_CUT["w"] / 2))
    draw_web(cv, size / 2, size / 2, scale, TRAY_CUT, (*color, TRAY_WEB_ALPHA), (*color, 1.0))
    pixels = cv.pixels()
    if not muted:
        return pixels

    bar, gap, reach = 0.13, 0.22, 0.95  # in half-canvas units
    half = size / 2.0
    px_unit = 1.0 / half  # one pixel, for the antialiased edges
    out = []
    for i, (r, g, b, a) in enumerate(pixels):
        u = ((i % size) + 0.5 - half) / half
        v = ((i // size) + 0.5 - half) / half
        if math.hypot(u, v) <= reach:
            across = abs(u - v) / math.sqrt(2)
            bar_cov = max(0.0, min(1.0, (bar - across) / px_unit + 0.5))
            gap_cov = max(0.0, min(1.0, (gap - across) / px_unit + 0.5))
            af = a / 255.0 * (1.0 - gap_cov)  # clear the gap (bar included)
            af = max(af, bar_cov)             # then lay the bar on top
            out.append((0, 0, 0, 0) if af <= 0.0005 else (*color, min(255, round(af * 255))))
        else:
            out.append((r, g, b, a))
    return out


# --- the mobile set (CB-323) ---------------------------------------------------
# iOS and watchOS are given one 1024 each and downscale it themselves, all the way
# to the 29-pt Settings row, so the single drawing has to survive that. That is why
# every mobile 1024 uses cut_for's 48-px cut (heavier thread, bigger orb) and not
# the 512 cut the Mac icon gets: the lighter cut is what the Mac icon draws at 1024
# and at 29 px its thread is a third of a pixel. tools/check-brand-icons.py measures
# exactly that, with the lighter cut as its negative control.
#
# iOS draws its own corner mask, so these are square, full-bleed tiles. The watchOS
# one is also full-bleed, but watchOS masks the inscribed circle, so the web must sit
# inside WATCH_SAFE of the size from the centre (the 0.47 in the plan; a full circle
# is 0.5, and the margin is for the antialiased edge and the system's own inset).

MOBILE_SIZE = 1024
WATCH_SAFE = 0.47
TINT_WEB = (168, 168, 168)  # the tinted mark is grey: iOS maps luminance onto the tint
TINT_ORB = (255, 255, 255)  # and the orb stays the brightest thing, as it is in the others


def mobile_cut():
    return cut_for(48)


def _full_bleed_tile(size):
    cv = Canvas(size)
    cv.rounded_rect(size / 2, size / 2, size / 2, 0.0, (*TILE, 1.0))
    return cv


def draw_ios_light(size=MOBILE_SIZE, cut=None):
    """The tile as the Mac icon has it, edge to edge. Opaque: written without alpha."""
    cv = _full_bleed_tile(size)
    draw_web(cv, size / 2, size / 2, size / 512, cut or mobile_cut(), (*IDLE, 1.0), (*GENERATING, 1.0))
    return cv.pixels()


def draw_ios_dark(size=MOBILE_SIZE, cut=None):
    """Web and orb on transparent: iOS supplies the dark backdrop behind it."""
    cv = Canvas(size)
    draw_web(cv, size / 2, size / 2, size / 512, cut or mobile_cut(), (*IDLE, 1.0), (*GENERATING, 1.0))
    return cv.pixels()


def draw_ios_tinted(size=MOBILE_SIZE, cut=None):
    """Grey web and white orb on transparent. iOS recolours it, so only luminance and
    alpha matter, and r == g == b everywhere keeps it a true greyscale."""
    cv = Canvas(size)
    draw_web(cv, size / 2, size / 2, size / 512, cut or mobile_cut(), (*TINT_WEB, 1.0), (*TINT_ORB, 1.0))
    return cv.pixels()


def draw_watch(size=MOBILE_SIZE, cut=None):
    """Full-bleed tile with the web at the iOS scale. At the 48 cut the web's outer
    extent is (185 + 16/2)/512 = 0.377 of the size, comfortably inside WATCH_SAFE; the
    checker asserts that from the pixels so a heavier cut cannot quietly break it."""
    return draw_ios_light(size, cut)


def draw_mark_template(size=256):
    """Alpha-only mark for the Lock Screen accessory and complications, which tint it
    themselves. It is the tray mask's geometry (the one already tuned to read small),
    in black, so only the alpha channel carries anything."""
    return draw_tray(size, (0, 0, 0))


def main():
    os.makedirs(ASSETS, exist_ok=True)

    # 64px so the menu bar has retina pixels to downsample from.
    for name, color in (("idle", IDLE), ("generating", GENERATING), ("waiting", WAITING)):
        path = os.path.join(ASSETS, f"tray-{name}.png")
        write_png(path, 64, draw_tray(64, color))
        print("wrote", os.path.relpath(path, HERE))

        path = os.path.join(ASSETS, f"tray-{name}-muted.png")
        write_png(path, 64, draw_tray(64, color, muted=True))
        print("wrote", os.path.relpath(path, HERE))

    path = os.path.join(ASSETS, "appicon-1024.png")
    write_png(path, 1024, draw_appicon(1024, 824 / 1024))
    print("wrote", os.path.relpath(path, HERE))

    # Drawn at each size rather than downscaled from one bitmap, with the bolder cut
    # for the small ones (cut_for) — Explorer and the Start menu pick whichever size
    # they need, and 16px is the one that shows up most.
    ico = os.path.join(ASSETS, "Orbweaver.ico")
    write_ico(ico, [(s, png_bytes(s, draw_appicon(s, 0.94))) for s in (16, 32, 48, 64, 128, 256)])
    print("wrote", os.path.relpath(ico, HERE))

    os.makedirs(MOBILE, exist_ok=True)
    for name, size, pixels, opaque in (
        ("ios-light-1024.png", MOBILE_SIZE, draw_ios_light(), True),
        ("ios-dark-1024.png", MOBILE_SIZE, draw_ios_dark(), False),
        ("ios-tinted-1024.png", MOBILE_SIZE, draw_ios_tinted(), False),
        ("watchos-1024.png", MOBILE_SIZE, draw_watch(), True),
        ("mark-template-256.png", 256, draw_mark_template(), False),
    ):
        path = os.path.join(MOBILE, name)
        write_png(path, size, pixels, opaque)
        print("wrote", os.path.relpath(path, HERE))


if __name__ == "__main__":
    main()

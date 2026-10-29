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

Pure stdlib (zlib + struct) — no Pillow, so it runs on a stock macOS python3.
Everything is drawn by supersampling signed distance fields, which is plenty
for circles and gets us clean edges at any size.
"""

import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(HERE, "Assets")

# The *default* three colors — OrbColors.DefaultIdle / DefaultGenerating /
# DefaultWaiting in OrbColors.cs. Keep them in sync by hand.
#
# They are only defaults now: the settings window lets you pick your own, and
# TrayController re-tints the tray PNGs at runtime to match. That works because
# tray_shader below returns one constant RGB and varies only alpha, which makes
# each tray-*.png a pure alpha mask over a single color — so what actually
# matters at runtime is the *alpha* channel, i.e. the ring geometry. Change the
# geometry here and a custom-colored icon follows; change these colors and only
# the shipped defaults move.
#
# appicon-1024.png and Orbweaver.ico stay on these colors whatever the user
# picks. Those are baked into the .app's .icns at build time and into the .exe's
# Win32 resources, and neither can follow a live setting.
IDLE = (0x5B, 0x7A, 0x94)
GENERATING = (0x8B, 0x6F, 0xD1)
WAITING = (0xE8, 0x98, 0x3B)

SS = 4  # supersampling factor per axis


def png_bytes(size, pixels):
    """pixels: flat list of (r, g, b, a) tuples, row-major, length size*size."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter type 0 (None) per scanline
        for x in range(size):
            r, g, b, a = pixels[y * size + x]
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        out = struct.pack(">I", len(data)) + tag + data
        return out + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def write_png(path, size, pixels):
    with open(path, "wb") as fh:
        fh.write(png_bytes(size, pixels))


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


def render(size, shade):
    """shade(u, v) -> (r, g, b, alpha 0..1) in a -1..1 coordinate square."""
    pixels = []
    inv = 1.0 / size
    for y in range(size):
        for x in range(size):
            acc_r = acc_g = acc_b = acc_a = 0.0
            for sy in range(SS):
                for sx in range(SS):
                    u = ((x + (sx + 0.5) / SS) * inv) * 2.0 - 1.0
                    v = ((y + (sy + 0.5) / SS) * inv) * 2.0 - 1.0
                    r, g, b, a = shade(u, v)
                    acc_r += r * a
                    acc_g += g * a
                    acc_b += b * a
                    acc_a += a
            n = SS * SS
            a = acc_a / n
            if a <= 0.0005:
                pixels.append((0, 0, 0, 0))
            else:
                # un-premultiply back to straight alpha
                pixels.append(
                    (
                        min(255, max(0, round(acc_r / acc_a))),
                        min(255, max(0, round(acc_g / acc_a))),
                        min(255, max(0, round(acc_b / acc_a))),
                        min(255, max(0, round(a * 255))),
                    )
                )
    return pixels


def tray_shader(color, muted=False):
    """A ring, not a disc: reads as a distinct shape at 16-22pt on both light
    and dark menu bars, where a solid blob just looks like a smudge.

    muted=True draws the 🚫 shape — a diagonal bar from upper left to lower
    right, with a transparent gap either side of it so it reads as a slash over
    the ring rather than merging into it. It is still one constant RGB with
    only alpha varying, which is the invariant TrayController.Tinted depends
    on: the re-tint reproduces the muted icon exactly, slash and all."""
    r, g, b = color
    outer = 0.86
    inner = 0.44
    bar = 0.13
    gap = 0.22
    reach = 0.95

    def shade(u, v):
        d = math.hypot(u, v)
        if muted and d <= reach:
            across = abs(u - v) / math.sqrt(2)
            if across < bar:
                return (r, g, b, 1.0)
            if across < gap:
                return (0, 0, 0, 0.0)
        if d > outer or d < inner:
            # faint core so the middle isn't a hole at tiny sizes
            if d < inner:
                return (r, g, b, 0.30)
            return (0, 0, 0, 0.0)
        return (r, g, b, 1.0)

    return shade


def appicon_shader():
    """Orb with a glow, padded like a real macOS icon (art fills ~80%)."""
    core = 0.52
    glow = 0.82

    def shade(u, v):
        d = math.hypot(u, v)
        if d <= core:
            # vertical shade from violet toward slate for a little depth
            t = (v / core + 1.0) * 0.5
            r = GENERATING[0] + (IDLE[0] - GENERATING[0]) * t
            g = GENERATING[1] + (IDLE[1] - GENERATING[1]) * t
            b = GENERATING[2] + (IDLE[2] - GENERATING[2]) * t
            # specular highlight up and to the left
            h = max(0.0, 1.0 - math.hypot(u + 0.18, v + 0.20) / 0.34)
            k = h * h * 0.45
            return (r + (255 - r) * k, g + (255 - g) * k, b + (255 - b) * k, 1.0)
        if d <= glow:
            t = (d - core) / (glow - core)
            return (*GENERATING, (1.0 - t) ** 2 * 0.45)
        return (0, 0, 0, 0.0)

    return shade


def main():
    os.makedirs(ASSETS, exist_ok=True)

    # 64px so the menu bar has retina pixels to downsample from.
    for name, color in (("idle", IDLE), ("generating", GENERATING), ("waiting", WAITING)):
        path = os.path.join(ASSETS, f"tray-{name}.png")
        write_png(path, 64, render(64, tray_shader(color)))
        print("wrote", os.path.relpath(path, HERE))

        path = os.path.join(ASSETS, f"tray-{name}-muted.png")
        write_png(path, 64, render(64, tray_shader(color, muted=True)))
        print("wrote", os.path.relpath(path, HERE))

    path = os.path.join(ASSETS, "appicon-1024.png")
    write_png(path, 1024, render(1024, appicon_shader()))
    print("wrote", os.path.relpath(path, HERE))

    # Rendered at each size rather than downscaled from one bitmap, so the small
    # entries stay crisp — Explorer and the Start menu pick whichever size they
    # need, and 16px is the one that shows up most.
    shader = appicon_shader()
    ico = os.path.join(ASSETS, "Orbweaver.ico")
    write_ico(ico, [(s, png_bytes(s, render(s, shader))) for s in (16, 32, 48, 64, 128, 256)])
    print("wrote", os.path.relpath(ico, HERE))


if __name__ == "__main__":
    main()

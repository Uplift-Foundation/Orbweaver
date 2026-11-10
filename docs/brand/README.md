# Orbweaver brand assets

The mark is a spiderweb with the orb at its hub: six spokes and two sagging rings
in slate (`#5B7A94`), a violet orb (`#8B6FD1`), on a near-black tile (`#0F1419`).
(CB-297.)

| File | Use |
| --- | --- |
| `orbweaver-icon.svg` | The mark on its tile. Source of truth for anything vector. |
| `orbweaver-mark.svg` | The web and orb alone, transparent. For placing on your own background. |
| `orbweaver-icon-1024.png`, `-512.png` | The tile, rasterized. |
| `orbweaver-mark-512.png` | The transparent mark, rasterized. |
| `favicon.ico` | 16, 32 and 48 px, for the website. |
| `apple-touch-icon.png` | 180 px flat square (iOS rounds the corners itself). |

The app's own icons are **not** these files: `tools/make-icons.py` draws them
(`Assets/appicon-1024.png`, `Assets/Orbweaver.ico`, `Assets/tray-*.png`) with a
heavier cut at small sizes. The SVGs here are the same geometry at the large cut.
If you change the shape in one, change the other.

## The phone and Watch icons

`docs/brand/mobile/` holds the icons for the iOS app and the Watch, drawn by `tools/make-icons.py` (not by `cairosvg`) so they share the app's geometry and its small-size cut. Regenerate with `python3 tools/make-icons.py`; `python3 tools/check-brand-icons.py` checks them.

| File | What it is |
| --- | --- |
| `ios-light-1024.png` | The tile edge to edge, 8-bit RGB with no alpha channel (the App Store refuses one). iOS rounds the corners. |
| `ios-dark-1024.png` | The web and orb on transparent; iOS supplies the dark backdrop. |
| `ios-tinted-1024.png` | The mark in greyscale on transparent (grey web, white orb); iOS recolours it. |
| `watchos-1024.png` | The tile edge to edge, with every part of the web inside 0.47 of the size from the centre, because watchOS masks the inscribed circle. |
| `mark-template-256.png` | Alpha only, one colour: the tray mask's geometry, for the Lock Screen accessory and the complications, which tint it themselves. |

All four 1024s use `cut_for(48)` (thread 16, two rings, orb 54), not the 512 cut: iOS only ever downscales the one 1024, down to the 29-pt Settings row, and the lighter cut drops the outer ring below the checker's bar at that size. The checker area-averages each tile to 29 px and measures the orb's contrast against the tile and how strongly the outer ring survives; the 512 cut is its negative control (`tools/test-check-brand-icons.py`).

The Orbweaver-Mobile repository copies these files byte for byte into its asset catalogs and pins their SHA-256 in `app/Brand/SOURCE.md`. Regenerating here means re-copying there.

## Website

The site's source is not in this repository. To adopt the new mark, copy
`favicon.ico`, `apple-touch-icon.png` and `orbweaver-icon.svg` next to the page
and put this in `<head>`:

```html
<link rel="icon" href="/favicon.ico" sizes="any">
<link rel="icon" href="/orbweaver-icon.svg" type="image/svg+xml">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
```

## Regenerating the PNGs

The PNGs were rasterized from the SVGs with `cairosvg`
(`cairosvg.svg2png(url=..., output_width=N, output_height=N)`); nothing else in
the repository needs it.

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

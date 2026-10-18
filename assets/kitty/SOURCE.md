# Kitty sprite sheets — source

- **Origin:** "Just a Kitty" by bimlab, https://bimlab.itch.io/just-a-kitty. The owner supplied the sheet
  `kitty_1.png` (kept here unmodified) on 2026-10-18.
- **Licence:** third-party art. The terms are the ones on the itch.io page above; they were not readable from the
  implementation pipeline (no web access), so none are restated here. The owner supplying the file is taken as the
  go-ahead, but **the owner must confirm** that the page's terms allow redistributing the art (and recoloured
  versions of it) in this public repository, the installer and the site. Until then, treat sprite rights as open
  (code signing is already waiting on clear sprite rights, see `CLAUDE.md`).
- **Credit:** "kitties by bimlab (Just a Kitty)", with the link above, in the site footer (`site/index.html`), in
  `README.md` and `client/README.md`.
- **Date:** 2026-10-18.

## Files

All four colours are palette swaps of the one supplied sheet, made by `client/tools/Normalize-KittySheet.ps1`.

| File | Variant | Palette |
| --- | --- | --- |
| `kitty_1.png` | — | the original bimlab sheet, do not edit |
| `kittys_spritesheet_orange.png` | `kitty_orange` | orange fur, cream highlights, dark brown outline, green eyes |
| `kittys_spritesheet_black.png` | `kitty_black` | near-black fur with a lighter shade so the form reads, yellow-green eyes |
| `kittys_spritesheet_white.png` | `kitty_white` | white fur, light grey shade, mid-grey outline (reads on light desktops), blue eyes |
| `kittys_spritesheet_grey.png` | `kitty_grey` | blue-grey fur, amber eyes |

The `kittys_spritesheet_*.png` files are byte-identical to the working copies in `client/assets/sprites/cow/` and
to `site/assets/kitty_*.png`; the script writes all three. Do not edit any copy by hand.

## Source sheet

128×128, 4 columns × 4 rows of 32×32 cells, fully opaque or fully transparent pixels.

| Source row | Content |
| --- | --- |
| 0 | front view, walking toward the camera (4 frames) |
| 1 | side view facing **left**, walk (4 frames) |
| 2 | side view facing **right**, walk (4 frames); columns 0 and 2 are standing poses |
| 3 | back view, walking away (4 frames); the tail hangs below the feet |

There are no idle, sit, groom or meow frames.

## Recolour

Each opaque source colour gets a role: outline `#463239`, eye `#4B8271`, nose/cheek pink `#FFC2D7` and `#FABBD0`.
Every other colour is fur: the sheet anti-aliases its pale lavender-grey fur into about 50 tones, so instead of one
role per tone the fur is recoloured along a dark → mid → light ramp by its luminance (mid stop at 60% of the
source's fur luminance range). That keeps the shading, including the darker far legs. A source colour that is
neither listed nor low-saturation makes the script throw.

## Mapping (cow grid, 128×256, 4 columns × 8 rows of 32×32)

Source cells are written as (row, column). Missing columns repeat the last cell. No flip: source row 2 already faces
right. Scale k = 1 (k = 2 would make the side view 36 px wide).

| Cow row | Animation | Source cells |
| --- | --- | --- |
| 0 | idle (3) | (2,0) (2,2) (2,0) |
| 1 | graze (2) | (2,0) (2,2) — no head-down frame exists |
| 2 | idle2 (4) | (2,0) (2,0) (2,2) (2,2) |
| 3 | moo (4) | (2,0) (2,2) (2,2) (2,0) — no meow frame exists; no new pixels drawn |
| 4 | walk (4) | (2,0) (2,1) (2,2) (2,3) |
| 5 | lie / sleep (frame 0 used) | (0,0) (0,1) (0,2) (0,3) — the front view; frame 0 is the still pose facing the viewer |
| 6 | walk-away | empty, like the pugs (unused by the app, and the back view's hanging tail would lift its feet) |
| 7 | — | empty |

**Placement:** one fixed offset per source row, from the union of opaque pixels over all that row's mapped cells, so
each row's own motion is kept and frames do not jitter. The side view's feet are at cell y = 21 but the front view's
at y = 23, so a single offset for the whole sheet would leave one of them floating; per source row, the union's
lowest pixel lands on frame row 31 and the union is centred horizontally.

| Source row | Union box (cell px) | Offset (x, y) |
| --- | --- | --- |
| 2 (side) | x 6..23, y 8..21 | (+1, +10) |
| 0 (front) | x 10..21, y 5..23 | (0, +8) |

The side-view kitty is about 18×14 px in the 32×32 frame, smaller than a pug (~24×18) and well under a cow. At scale 3
it reads as a small cat next to the cows.

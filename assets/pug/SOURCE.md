# Pug sprite sheets — source

- **Origin:** original pixel art, drawn procedurally by `client/tools/Normalize-PugSheet.ps1` from the
  pixel maps at the top of that script. No third-party art is included.
- **Why not downloaded art:** ticket M-2 asked for pug sprites found online. During implementation
  (2026-10-18) the pipeline had no web access, so the plan's documented fallback was used: original art.
  The owner can swap in downloaded sheets later by dropping the originals here, adapting the frame map in
  the script (or replacing the script's drawing with a crop/scale of the originals) and re-running it; the
  app, manifest layout and protocol need no code change.
- **Licence:** same as the rest of this repository (owner's own work). Nothing to attribute.
- **Date:** 2026-10-18.

## Files

| File | Variant | Notes |
| --- | --- | --- |
| `pugs_spritesheet_fawn.png` | `pug_fawn` | fawn body, black mask and ears |
| `pugs_spritesheet_black.png` | `pug_black` | black body, slightly darker mask |
| `pugs_spritesheet_apricot.png` | `pug_apricot` | apricot body, black mask |
| `pugs_spritesheet_silver.png` | `pug_silver` | silver body, dark mask |

These are byte-identical to the working copies in `client/assets/sprites/cow/` and to `site/assets/pug_*.png`;
the script writes all three.

## Layout (cow grid, 128×256, 4 columns × 8 rows of 32×32)

| Row | Animation | Frames | Pose |
| --- | --- | --- | --- |
| 0 | idle | 3 | stand, head bob, tail wag |
| 1 | graze | 2 | nose to the ground (sniffing) |
| 2 | idle2 | 4 | look up, tail wag |
| 3 | moo (bark) | 4 | mouth open with tongue on frames 2–3 |
| 4 | walk | 4 | legs alternate, body bobs one pixel |
| 5 | lie / sleep | frame 0 | lying down, paws forward (frames 1–3 repeat it) |
| 6–7 | unused | — | transparent |

Art faces right; feet are on the bottom pixel row of each frame (bottom-center anchor). The pug is about
24 px wide and 18 px tall, so next to a 32 px cow it is a small dog, which is intended.

# Cowpanion — Implementation Decisions (2026-09-07)

Answers given by the project owner before implementation started. These refine, and where
noted amend, `cowpanion-protocol.md`, `cowpanion-server-plan.md` and `cowpanion-client-plan.md`.
Both tracks must follow them.

## Repository

- Monorepo, git root is this folder. Layout:
  ```
  docs/       the three plan documents + this file
  server/     Node.js pasture server (package.json lives here)
  server/deploy/  Apache vhost, systemd unit, logrotate, fail2ban, install.sh, RUNBOOK.md
  client/     .NET solution (Cowpanion.sln lives here)
  client/assets/sprites/cow/  the seven sprite sheets + manifest.json
  assets/     original sprite sheets (do not modify; client/assets is the working copy)
  ```
- The VPS is deployed by cloning this repo and using `server/` as the working directory
  (`/opt/cowpanion` is a clone; systemd `WorkingDirectory=/opt/cowpanion/server`).
  Update the systemd unit and runbook accordingly.
- Nobody in this session has SSH access to the VPS. The server track produces scripts and a
  runbook; the owner runs them. Anything that needs the real VPS is reported as
  "not verifiable here" rather than pretended.

## Toolchains available on the dev machine

- Windows 11, PowerShell 7, Git Bash.
- Node.js 24.16 (npm 11). Pin `engines.node` to `>=22` (Debian VPS will likely run 22 LTS;
  `node:sqlite` is available from 22.5, so prefer it over `better-sqlite3` and fall back
  only if it does not work on 22).
- .NET SDK 10.0.400 at `C:\Program Files\dotnet` (on PATH). No Visual Studio; build with the
  `dotnet` CLI only.
- WSL Ubuntu is available for running the server and its bash scripts on Linux. Do not
  install packages inside WSL without asking.

## Protocol clarification (still v1, no version bump)

- `variant` (in `hello`, `presence.members[]`) is a colour/sprite-sheet name, chosen by the
  user. Server validates it as `^[a-z0-9_]{1,16}$`, otherwise substitutes `"brown"`. Server
  does not know the list of colours; it is an opaque token. Clients that receive an unknown
  variant render `brown`.
- Known variants in v1 (client side): `black0`, `black1`, `brown`, `white0`, `white1`,
  `white_darkspots`, `white_pinkspots` — one per sprite sheet.
- Client config gains `"variant": ""`; empty means "derive deterministically from clientId"
  (hash into the list above) and persist the result on first run so it never changes.
  Tray menu offers a "Cow colour" submenu to change it.

## Sprite sheets (all seven share this layout)

- 128x256 PNG, frames 32x32, 4 columns x 8 rows, source art faces **right** (head on the right, tail on the left; corrected 2026-09-07 after the first run showed inverted walking). Anchor is
  bottom-center. Row 7 is empty.
- Row meanings (owner accepted this reading; the manifest is data and may be corrected later):

  | row | frames | animation | notes |
  | --- | --- | --- | --- |
  | 0 | 3 | `idle` | head bob |
  | 1 | 2 | `graze` | head down, only horns visible |
  | 2 | 4 | `idle2` | body bob; use as a second idle variant |
  | 3 | 4 | `moo` | frames 2–3 have the mouth open |
  | 4 | 4 | `walk` | clear leg motion |
  | 5 | 4 | `lie` | faces camera, tail wag; used for LieDown |
  | 6 | 4 | `sleep` | faces away, tail wag |

- Manifest format extends the plan's format with a `sheets` map, one entry per variant,
  and `defaultVariant`. `frameWidth`/`frameHeight`/`animations` are shared by all sheets:
  ```json
  { "name": "cow", "frameWidth": 32, "frameHeight": 32, "defaultScale": 3,
    "facing": "right", "anchor": "bottom-center", "defaultVariant": "brown",
    "sheets": { "brown": "cows_spritesheet_brown.png", "black0": "cows_spritesheet_black0.png", ... },
    "animations": { "walk": { "row": 4, "frames": 4, "fps": 8, "loop": true }, ... } }
  ```

## Client UX decisions

- First run with empty `displayName`: show one small, normal, focusable WPF dialog prefilled
  with the Windows username (truncated to 16). This is the only window in the app that may
  take focus, and only on first run. Cancel keeps the default name.
- All other hard rules in the client plan stand unchanged.

## Phase gating

- Build all phases (S0–S3, P0–P4) in this session with automated tests. Manual criteria that
  need real hardware or the real VPS are collected into `docs/ACCEPTANCE.md` as a checklist
  for the owner, each marked "automated: pass/fail" or "manual: to verify".
- P0 must remain runnable on its own as a separate executable (`client/spike/Cowpanion.Spike`)
  so the CPU measurement can still be done independently.

## Second round (after the server track reported)

- Repository: `https://github.com/capoupado/cowpanions.git`, branch `main`. `install.sh`
  defaults to it; the local repo has it as `origin` (nothing pushed yet).
- Per-IP connection cap stays at **4** (`COWPANION_IP_CAP` in the unit if it ever needs raising).
- **fail2ban is not installed.** Removed from `install.sh`, runbook and acceptance list.
- VPS Node version unknown; runbook has the `node -v` check and the sqlite flag note.

## Open questions from the client track (not yet decided)

- Sleep trigger: currently *user* inactivity (cursor/chat resets the timer). Alternative: herd
  stillness for N minutes.
- Filler cows all render `brown`; could instead be hashed per filler index for variety.
- No `moo.wav` exists; provide one or drop the moo feature.
- Chat-mode also drops `WS_EX_NOACTIVATE` while armed (required for the TextBox to receive keys);
  restored together with `WS_EX_TRANSPARENT`. Accepted as a necessary amendment to the hard rule.

## Third round (after the owner's first manual run, 2026-09-07)

- **Sprite art faces right**, not left. Manifest `facing` corrected; cows now walk head-first.
- **Leaving cows trot**: a cow that is no longer needed (filler count reduced, member gone)
  walks to the nearest edge at a fixed 3.5 × base speed (~63 DIPs/s, personality ignored), so an
  exit takes at most ~22 s on a 2560-DIP strip. The slow grazing-speed walk-out was what read as
  "the herd walks off screen". Present cows have always been hard-clamped to the strip.
- **Offline herd includes the user's own cow** (own colour, self marker) plus
  `offlineHerdSize − 1` fillers, so colour and name changes are visible without a server. The
  self cow stays when presence arrives. `offlineHerdSize: 0` means no cows at all.
- **Fillers get stable hashed colours** from the seven sheets (same colours every launch).
- Still open: sleep trigger (user inactivity vs herd stillness), no `moo.wav`.

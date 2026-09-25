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
  | 5 | 4 | (walk toward camera) | frames 1 and 3 step a leg; only frame 0 is used, as a still `lie`/`sleep` pose (see "Front/back rows", 2026-09-07) |
  | 6 | 4 | (walk away from camera) | unused |

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

## Reverse proxy is nginx, not Apache (2026-09-07)

The VPS runs nginx. `server/deploy/` now ships `nginx-cows.conf` (https site with the `/ws`
proxy, `proxy_read_timeout 300s`, HSTS, own logs under `/var/log/nginx/cows/`), an http-only
`nginx-cows-bootstrap.conf` used until certbot has produced the certificate, and
`logrotate-nginx-cows`. The server plan document still says Apache; this file supersedes it.

## Cloudflare in front of nginx (2026-09-07)

DNS for `cows.carlospoupado.com` resolves to Cloudflare. nginx maps `CF-Connecting-IP` into
`X-Forwarded-For` so the server's per-IP cap sees real client addresses. Cloudflare's 100 s
WebSocket idle timeout is comfortably above the 20 s heartbeat.

## Front/back rows are walk cycles, not rest poses (2026-09-07)

The owner saw cows "walking with their back turned to the screen" and "walking towards the
screen". Rows 5 and 6 are not tail wags: frames 1 and 3 step a leg, so they are walk cycles toward
and away from the camera, and the old manifest looped them for LieDown and Sleep. Decision: a cow
never walks toward or away from the viewer. `lie` and `sleep` both play a single still frame,
row 5 frame 0 (standing, facing the camera). Row 6 (back turned) is unused. Manifest only; no code
change. If distinct lie/sleep art is ever drawn, add it as new rows and repoint the manifest.

## No filler cows trot out at startup (2026-09-07)

Owner: "can we not show extra cows leaving when we start the app?" Before, the offline herd
(self + fillers) was spawned as soon as the strips existed, and the first presence a few seconds
later sent the fillers home. Now, while a freshly started pasture client is waiting for its first
presence, the offline herd is only the self cow. Fillers appear only if no presence arrives within
`Orchestrator.StartupGrace` (8 s) or the client stops. Same rule when multiplayer is toggled on or
the connection settings change. Multiplayer off keeps the immediate full offline herd. The 20 s
`FallbackGrace` for mid-session drops is unchanged.


## Fourth round: interaction quick wins (2026-09-07)

Owner asked for hover name tags, cursor reactions, emotes, floating emoji reactions, and a fix for
"my own cow barely moves from where it started". Decisions taken while implementing:

- **Root cause of the stuck cow**: the 1-D hard separation plus "blocked walkers turn around"
  meant a cow could never pass a neighbour, so every cow was confined to the slot between its two
  neighbours for the whole session; walk direction also kept the current facing most of the time,
  and cohesion pulled walkers back to the herd mean. Fix: (a) **wander targets** — a cow starting a
  walk picks a destination anywhere on the strip and walks until it gets there or the walk times
  out; (b) **passing lane** — a walker blocked by a stationary cow steps into a back lane
  (`Cow.Lane` 1, drawn a few DIPs higher and behind), passes, and returns to the front lane when
  clear. Resting cows always stand in the front lane, so the strip still reads as one row.
  Separation only applies between cows in the same lane. "Never overlap" now means never within
  the same lane.
- **Hover name tag**: the cursor resting on a cow for ~0.4 s shows a small name label above it
  (name for members, "cow" for fillers, "you" marker for self). Read-only; the window stays
  click-through.
- **Cursor reactions**: a hovered relaxed cow looks up (idle2 row) while the cursor stays; a fast
  cursor sweep (> ~1500 DIPs/s) startles cows within ~150 DIPs into a short spooked walk away,
  with a per-cow cooldown. Existing look/follow/spook behaviour stays.
- **Protocol v2** (`docs/cowpanion-protocol.md`): the `chat` frame gains optional `emote`
  (`moo` | `jump` | `spin`) and `reaction` (1–3 emoji graphemes, drawn as floating emoji above the
  cow, no bubble). `text` becomes optional; a frame needs at least one of the three. Same rate
  limit. **Server accepts hello v1 and v2**: v1 members receive text-only chat in the v1 shape and
  never receive emote-only or reaction-only frames. `welcome.protocolVersion` echoes the version
  the client sent. Rationale: friends on the old build keep working until they update.
- **Chat box shortcuts**: `/moo`, `/jump`, `/spin` send an emote; a message that is only 1–3 emoji
  is sent as a reaction; `/heart` `/love` (❤️), `/lol` (😂), `/wave` (👋), `/party` (🎉),
  `/wow` (😮), `/sad` (😢) expand to reactions. Global hotkey **Ctrl+Alt+H** sends a heart.
- **Emotes are visual only**: `moo` plays the moo row (and the sound hook); `jump` and `spin` are
  procedural (vertical hop; rapid facing flips rendered without a `Turn`). The simulator only
  times the emote and holds the cow still; the renderer draws it.
- Emotes and reactions obey the existing **Mute bubbles** toggle: muted means no bubbles, no emoji,
  no emotes from others.
- **Sim tuning that fell out of the roaming fix** (Core): wander destinations persist across
  rests (a cow ambles toward its target over several walks, pausing to graze) because a single
  4–14 s walk only covers ~150 DIPs; minimum wander distance is 30 % of the strip; the Laziness
  penalty on Idle/Graze→Walk was softened from `(1.3 − lazy)` to `(1.3 − 0.7·lazy)` so very lazy
  cows still cross the screen within about ten minutes; a blocked walker keeps its destination and
  only steps back briefly (up to three times per journey) before giving up. Cows in the back lane
  head for the nearest front gap rather than idling behind others.

## Fifth round: hotkeys, focus mode, settings window, chat history (2026-09-24)

Owner asked for configurable hotkeys, a "focus mode" that stops listening to keystrokes, a settings
window, and a client-side chat history; auto-update is being explored separately. Take control and
shared coordinates are parked. Decisions taken while implementing:

- **Every hotkey is rebindable** (`hotkeys` object in config.json, Settings → Hotkeys). Allowed:
  any modifiers + one key, except that a key with no Ctrl/Alt/Win must be F1–F24, Pause or Scroll
  Lock — a bare letter or Space through RegisterHotKey would stop the user typing it anywhere.
- **The kill hotkey can be rebound but never unbound**, and if its combination is taken the app
  falls back to Ctrl+Alt+Shift+K. This amends the hard rule "Ctrl+Alt+Shift+K exists from P0":
  a kill hotkey always exists; the combination is the user's choice. The other four can be unbound.
- **Focus mode = release every hotkey except Quit and the focus-mode toggle** (default Ctrl+Alt+F,
  can itself be unbound). It persists across restarts (`focusMode`). The app never hooked the
  keyboard to begin with — RegisterHotKey only reports its own combinations — so this is the whole
  of "not listening". It does not mute bubbles; that stays a separate toggle.
- **While a capture box in Settings has focus, all hotkeys are unregistered, Quit included**, so
  the box can record combinations the app itself owns. It is a normal focused window, not the
  overlay, so no escape hatch is needed; hotkeys return when focus leaves the box or the window
  closes.
- **Settings window** is an ordinary focusable top-level window (like the first-run name dialog;
  the overlay rules do not apply to it), opened only from the tray. It covers every key except
  `clientId`; `serverUrl` sits under "Advanced". It writes through the same path as the tray.
- **Chat history is client-side, memory only**, last 200 entries, gone on quit, never written to
  disk. The plan's "message history" exclusion was about a server backlog; the server still keeps
  nothing. History records while bubbles are muted or the overlay is paused — that is when it is
  useful.

## Sixth round: self-update through Velopack, unsigned (2026-09-24)

Owner chose Velopack over notify-only, and the unsigned path for now (SignPath Foundation needs an
open-source licence, a CI build and clear sprite-sheet rights; revisit later). This overrides the
client plan's "no auto-update" hard rule and "auto-update" in its permanently-out-of-scope list.

- **Distribution changes from a single-file zip to Velopack**: `Cowpanion-win-Setup.exe` (per-user,
  `%LOCALAPPDATA%\Cowpanion`, Start menu shortcut only, no admin) is the main download;
  `Cowpanion-win-Portable.zip` is the no-installer alternative and self-updates too. The old
  single-file `Cowpanion-win-x64.zip` cannot update; its users reinstall once.
- **Feed on our own domain**: `https://cows.carlospoupado.com/updates/` (nginx, `/var/www/cows-updates`,
  filled by hand from `client/release.ps1` output). GitHub Releases stays the download page. So the
  network rule becomes "pasture server plus the update feed on the same domain".
- **What a check sends**: one GET of `releases.win.json` with Velopack's query string `arch`, `os`,
  `rid`, `id`, `localVersion` (verified by pointing Velopack 1.2.158 at a local listener). The
  persistent per-install "staging id" Velopack keeps locally is **not** sent by its web source, so
  Velopack's own source is used as is. Documented in both privacy pages.
- **Integrity**: Velopack checks each package against the SHA-256 in the feed (tamper test: a
  corrupted delta and full package were both rejected with `ChecksumFailedException` and the install
  stayed on the old version). The feed itself is unsigned, so trust rests on HTTPS to our domain; a
  compromised VPS or Cloudflare account could publish a malicious release. Accepted for a friends-only
  hobby app; code signing is the fix when it matters.
- **When**: `autoCheckForUpdates` (default **true**): first check 45 s after start, then every 24 h
  while running; tray "Check for updates" any time. A found update downloads in the background;
  the tray offers "Restart to update", otherwise Velopack applies it on the next start. Balloons
  only for "update ready", or for any outcome of a manual check. Dev builds and the old zip report
  "Updates need the installed version" and never touch the network for updates.
- **Restart path**: `WaitExitThenApplyUpdates` then a normal quit (sends `bye`, releases the mutex),
  so Velopack never has to kill the process. `Program.Main` owns startup so `VelopackApp.Run()` is
  the first code; the uninstall hook removes the HKCU Run value. The Run value points at Velopack's
  stub launcher one level above `current\` (the stub is named after the pack title, so the title
  must stay "Cowpanion").
- **Release tooling**: `vpk` pinned as a local dotnet tool (`client/dotnet-tools.json`), not global.
  `release.ps1` publishes (self-contained, no longer single-file: Velopack packs a folder and makes
  ~90 KB deltas from it), fetches the previous release for deltas, packs, writes SHA256SUMS and prints
  the upload commands. It never uploads.

## 2026-09-25 — Jump hotkey (v1.1.0, first update pushed through the Velopack feed)

- **Ctrl+Alt+J makes your own cow jump**, rebindable/unbindable like the others (`hotkeys.jump`,
  Settings → Hotkeys, tray "Jump"). No protocol change: it sends the existing v2 `jump` emote
  (`{"t":"chat","emote":"jump"}`), so everyone in the pasture sees it and the server echo animates our own
  cow (never optimistically). Offline or multiplayer off, the own cow jumps locally only.
- **Clamp order**: Jump is checked *after* FocusMode, so on upgrade an existing binding that already uses
  Ctrl+Alt+J keeps it and jump starts unbound (with a warning) instead of stealing it.
- Released in focus mode like the other non-essential hotkeys.
- **Version**: the GitHub release tagged v1.0.0 actually ships Velopack package 0.2.0 (the feed and every
  install report 0.2.0). This release is **1.1.0**, which is above both, so installs update and the
  package version lines up with the tags from here on.

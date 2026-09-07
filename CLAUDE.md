# Cowpanion — guide for Claude sessions

Desktop toy: pixel-art cows graze along the bottom of the screen on top of every window; each
cow is a friend running the app in the same "pasture"; Ctrl+Alt+C sends a speech bubble to all.
Windows client (WPF, .NET 10) + tiny Node.js relay server on a Debian VPS behind nginx and
Cloudflare + a static announcement site. Owner: Carlos (`capoupado` on GitHub).

## Read these before changing anything

| Doc | What it settles |
| --- | --- |
| `docs/cowpanion-protocol.md` | Wire contract v1. Both sides implement it exactly. Any shape change = version bump. |
| `docs/cowpanion-client-plan.md` | Client hard rules, stack, phases P0–P4, known traps. |
| `docs/cowpanion-server-plan.md` | Server scope (under 600 lines, 1–2 deps), phases S0–S3. Says Apache; nginx superseded it. |
| `docs/DECISIONS.md` | **Owner decisions that override the plans**, in chronological sections. Append here, never rewrite. |
| `docs/ACCEPTANCE.md`, `server/ACCEPTANCE.md`, `client/ACCEPTANCE.md` | What is automated vs. what the owner verifies by hand. |

## Layout

```
server/            Node 22+ ESM, no build step. src/ (~590 lines), test/ (node:test), bin/cowpanion-ban.js
server/deploy/     install.sh (idempotent deploy), cowpanion.service, nginx-cows.conf,
                   nginx-cows-bootstrap.conf (pre-certificate), logrotate-nginx-cows, RUNBOOK.md
client/            Cowpanion.sln — src/Cowpanion.Core (pure sim, BCL only), src/Cowpanion.Net
                   (PastureClient, no UI), src/Cowpanion.App (WPF overlay, tray), spike/ (P0 CPU spike),
                   tests/ (xUnit). assets/sprites/cow/ = seven sheets + manifest.json
site/              Static HTML/CSS announcement page, served by nginx at the domain root
dist/              (gitignored) portable publish output + zip + RELEASE_NOTES.md
assets/            Original sprite sheets; do not edit. client/assets is the working copy.
```

## Commands

```powershell
# server (Windows or Linux)
cd server; npm test                       # 31 tests; 1 skipped on Windows (real SIGTERM)
npm start                                 # 127.0.0.1:8787, env COWPANION_PORT / COWPANION_DATA / COWPANION_IP_CAP

# client — dotnet is at "C:\Program Files\dotnet" (on the user PATH; not always in a fresh shell)
cd client; dotnet build Cowpanion.sln -c Release      # must be 0 warnings (TreatWarningsAsErrors)
dotnet test Cowpanion.sln -c Release                  # Core 44, Net 18 (Net takes ~35 s by design)
dotnet publish src/Cowpanion.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:BaseOutputPath=bin-publish/ -o ../dist/Cowpanion-win-x64
# Dev flags for the app: --exit-after N  --config PATH  --no-dialog  --inject-chat-exception

# deploy (on the VPS, as root): pulls, npm ci, installs unit + nginx site + static site, restarts
bash /opt/cowpanion/server/deploy/install.sh
```

## Invariants (release blockers — see client plan "Hard rules")

Overlay never takes focus, never in Alt-Tab, click-through except while chat mode is armed
(bounded, indicator shown, restored in `finally`). Kill hotkey Ctrl+Alt+Shift+K registered before
any window. Single instance (`Global\Cowpanion`). No telemetry, no network beyond the pasture
server. Server never persists or logs chat text; access logs keep IPs 7 days max. Present cows are
hard-clamped to the strip; arrivals walk in, departures trot out (never pop).

## Gotchas learned the hard way

- **Never `Stop-Process -Name Cowpanion*`.** Carlos runs the real client on this machine. A second
  launch exits silently on the mutex, so test copies must check `Get-Process Cowpanion` first and
  stop only the PID they started. Same for `node` servers.
- The running client locks `client/src/Cowpanion.App/bin/Release/...`; publish with
  `-p:BaseOutputPath=bin-publish/` when it is running.
- Sprite art faces **right** (head on the right). Manifest `facing: "right"`. Rows: 0 idle(3),
  1 graze(2), 2 idle2(4), 3 moo(4), 4 walk(4), 5 walk-toward-camera(4), 6 walk-away(4). `lie` and
  `sleep` show only row 5 frame 0 (still, facing the viewer); row 6 is unused. Cows never walk
  toward or away from the screen.
- `install.sh` re-executes itself if the pull changed it (bash keeps running the old copy
  otherwise). Keep that block if you edit the script.
- nginx on the VPS is Debian's 1.22: no `http2 on;`. Domain is behind Cloudflare: client IP comes
  from `CF-Connecting-IP`; a `525` on the client means Cloudflare could not TLS to the origin.
- `node:sqlite` prints an ExperimentalWarning on Node 22; harmless. Needs `--experimental-sqlite`
  only on 22.5–22.12.
- wscat clients get evicted after 60 s because they never send `{"t":"ping"}`. Real clients ping
  every 20 s. Not a bug.
- Line endings: repo has `.gitattributes` (`* text=auto`); the CRLF warnings on commit are noise.

## Ways of working with Carlos

Batch questions (AskUserQuestion, up to 4) before fanning out agents; one agent per side
(server / client / site) works well. He verifies hardware-level acceptance himself. No SSH to the
VPS from here: produce scripts + runbook, ask him to paste output. He decides UX trade-offs;
record each decision in `docs/DECISIONS.md`. Commit with his name/email; push only when asked.

## Open items

- Sleep trigger: user inactivity (current) vs. herd stillness.
- No `moo.wav`; the moo feature is a no-op hook and the site says "completely silent".
- First GitHub Release (`v0.1.0`, upload `dist/Cowpanion-win-x64.zip`) must be created by Carlos;
  the site's download button 404s until then.
- Manual acceptance items in the three ACCEPTANCE files.

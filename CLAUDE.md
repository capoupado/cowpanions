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
dotnet test Cowpanion.sln -c Release                  # Core 72, Net 63 (Net takes ~35 s by design)
.\release.ps1 -Version 0.1.0                          # Velopack release into ../dist/releases (never uploads)
# Dev flags for the app: --exit-after N  --config PATH  --no-dialog  --inject-chat-exception  --dump-emoji PATH
#                        --dump-windows DIR (settings + history windows to PNGs; before the mutex, nothing saved)
#                        --update-feed URL|DIR  --update-now (update test on an installed build, before the mutex)

# deploy (on the VPS, as root): pulls, npm ci, installs unit + nginx site + static site, restarts
bash /opt/cowpanion/server/deploy/install.sh
```

## Publishing a client release (build + upload a new version)

```powershell
cd client
.\release.ps1 -Version 0.2.0      # builds Release, vpk pack (full + delta), fetches the previous release
                                   # for the delta base, writes ..\dist\releases\ + ..\dist\RELEASE_NOTES.md:
                                   # Cowpanion-win-Setup.exe, Cowpanion-win-Portable.zip, full+delta .nupkg,
                                   # releases.win.json, assets.win.json, SHA256SUMS.txt. Never uploads anything.
```

Then upload by hand, packages before the feed file so no client ever sees a release whose package
is missing (full commands: `server/deploy/RUNBOOK.md` §7):

1. GitHub release — asset names must match exactly, the site's download buttons hardcode them:
   ```powershell
   gh release create v0.2.0 ..\dist\releases\Cowpanion-win-Setup.exe ..\dist\releases\Cowpanion-win-Portable.zip ..\dist\releases\SHA256SUMS.txt --title v0.2.0 --notes-file ..\dist\RELEASE_NOTES.md
   ```
2. Update feed on the VPS — new package(s) first, then the feed files:
   ```powershell
   scp ..\dist\releases\Cowpanion-0.2.0-*.nupkg root@<vps>:/var/www/cows-updates/
   scp ..\dist\releases\releases.win.json ..\dist\releases\assets.win.json root@<vps>:/var/www/cows-updates/
   ```
3. Verify: `curl -sI https://cows.carlospoupado.com/updates/releases.win.json` → `200`,
   `cache-control: no-cache`. Running clients pick it up within a day (or tray → Check for
   updates), download in the background, install on next restart.

Keep `dist\releases` between releases — it is the delta base; if lost, `release.ps1` re-downloads
the previous release from the feed. Rollback = publish the previous build under a *higher* version
number (Velopack never downgrades). Delete old `.nupkg` on the VPS once nobody runs that version;
the newest full package must stay. Test the whole update path with a throwaway pack id first
(client/README "Releases and updates") — never the real `Cowpanion` pack id on this machine.

## Restarting / redeploying the server

Quick restart, no code change, on the VPS as root:
```bash
systemctl restart cowpanion && sleep 2 && systemctl status cowpanion --no-pager
journalctl -u cowpanion -n 20 --no-pager      # confirm it came back up clean
```
Drops every connected client; they reconnect with jittered backoff by design — not a bug to
work around.

Full redeploy — pulls `main`, `npm ci --omit=dev`, reinstalls the systemd unit + nginx site +
static site + logrotate rule, restarts (idempotent, safe to re-run any time, including just to
pick up a config change with no code change):
```bash
sudo bash /opt/cowpanion/server/deploy/install.sh
```

## Invariants (release blockers — see client plan "Hard rules")

Overlay never takes focus, never in Alt-Tab, click-through except while chat mode is armed
(bounded, indicator shown, restored in `finally`). Kill hotkey (default Ctrl+Alt+Shift+K, rebindable,
never unbindable, falls back to the default if taken) registered before any window. Single instance
(`Global\Cowpanion`). No telemetry, no network beyond the pasture server and the update feed on the same
domain, no keyboard hook. Server never persists or logs chat text; access logs keep IPs 7 days max.
Present cows are hard-clamped to the strip; arrivals walk in, departures trot out (never pop).

## Gotchas learned the hard way

- Updates are Velopack. `VelopackApp.Run()` must stay the first line of `Program.Main`. Test updates
  only under a different pack id (`-PackId CowpanionTest`, see client/README "Releases and updates");
  never install the real `Cowpanion` pack id on this machine. The stub launcher is named after
  `--packTitle`, and `StartupRegistration` expects it to equal the exe name, so the title stays
  "Cowpanion". `release.ps1` must run in Windows PowerShell 5.1 too (no `?.` / `??`).
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
- WPF cannot draw colour emoji (no COLR/CPAL in its text stack; Segoe UI Emoji comes out as black
  outlines). `Overlay/EmojiRasterizer.cs` (Direct2D via `Vortice.Direct2D1`, `EnableColorFont`) is
  the path; `Cowpanion.exe --dump-emoji out.png` proves it without a display and runs before the
  mutex, so it works while the real client is up. Windows has no flag glyphs: 🇵🇹 renders as "PT".

## Ways of working with Carlos

Batch questions (AskUserQuestion, up to 4) before fanning out agents; one agent per side
(server / client / site) works well. He verifies hardware-level acceptance himself. No SSH to the
VPS from here: produce scripts + runbook, ask him to paste output. He decides UX trade-offs;
record each decision in `docs/DECISIONS.md`. Commit with his name/email; push only when asked.

## Open items

- Sleep trigger: user inactivity (current) vs. herd stillness.
- No `moo.wav`; the moo feature is a no-op hook and the site says "completely silent".
- First GitHub Release (`v0.1.0`: `Cowpanion-win-Setup.exe`, `Cowpanion-win-Portable.zip`,
  `SHA256SUMS.txt` from `release.ps1`) and the first feed upload to `/var/www/cows-updates` must be
  done by Carlos; the site's download buttons 404 until then. Redeploy the server first (nginx `/updates/`).
- Code signing (SignPath needs an OSS licence + CI + sprite rights) is deferred; builds are unsigned.
- Manual acceptance items in the three ACCEPTANCE files.

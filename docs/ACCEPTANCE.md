# Cowpanion — Acceptance Overview (2026-09-07)

Both tracks were built in one session. Detailed per-criterion checklists live next to the code:

- `server/ACCEPTANCE.md` — S0–S3 (27 automated pass, 16 manual)
- `client/ACCEPTANCE.md` — hard rules + P0–P4 (23 automated pass, 37 manual/observed)

## Verified in this session (reproduced by the coordinator, not just reported by the agents)

| Check | Result |
| --- | --- |
| `server`: `npm test` (Node 24, Windows) | 30 pass, 1 skipped (real SIGTERM test, Windows only) |
| `server`: `npm audit` | 0 vulnerabilities; one runtime dependency (`ws`) |
| `server`: `src/` size | 589 lines (plan budget 600) |
| `client`: `dotnet build -c Release` | 0 warnings, 0 errors, `TreatWarningsAsErrors` on |
| `client`: `dotnet test` | Core 35/35, Net 18/18 |
| `client` spike CPU (agent measured, 20 s, 2560×260 DIPs, ~32 fps) | ~0.09 % of total CPU, working set flat ~101 MB |
| End-to-end on localhost: real server + real client + a scripted second member | client joined, `presence` carried both members, second member's chat (with emoji) relayed by the server, client left with `bye` → close 1000 |

The end-to-end run confirms the wire path only. Whether the bubble was drawn was not observed
(the client deliberately never logs chat text).

## What only you can verify — in priority order

1. **Client hard rules on real hardware** (5 min): run the spike for 5 minutes
   (`dotnet run --project client/spike/Cowpanion.Spike -c Release -- --exit-after 300 --report`),
   click/drag through it onto desktop, taskbar and a browser; Alt+Tab; check CPU in Task Manager;
   press `Ctrl+Alt+Shift+K`.
2. **Deploy the server** following `server/deploy/RUNBOOK.md` (DNS → `install.sh` → certbot),
   then S0 checks: `npx wscat -c wss://cows.carlospoupado.com/ws`, port 8787 closed from outside,
   http→https redirect, 5-minute idle survival with pings (ProxyTimeout 300).
3. **Two real clients in one pasture** (needs a second machine or a colleague): cows walk in, a
   chat bubble appears on both incl. sender, closing one client walks its cow out within 60 s,
   `systemctl restart cowpanion` mid-session recovers without a dialog.
4. **Sprite rows**: watch a cow for a minute at scale 3 and confirm walk/graze/moo/lie/sleep look
   right; fix `client/assets/sprites/cow/manifest.json` if any row is wrong (data only, no rebuild).
5. **Soaks**: 1-hour offline, 8-hour connected; memory flat in Task Manager.
6. **DPI / multi-monitor**: second monitor at a different scale, `"monitors": "all"`.
7. **Server hygiene after a week**: `find /var/log/apache2/cows -mtime +7` prints nothing.

## Not built, by decision

- fail2ban jail (owner decision, per-IP cap in Node is the only throttle)
- Moo audio: hook exists, no `moo.wav` shipped, `mooEnabled` false
- Anything in the plans' "out of scope" lists

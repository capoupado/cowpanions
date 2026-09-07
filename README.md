# Cowpanion

A small herd of pixel-art cows grazes along the bottom of your screen, on top of every window,
never stealing focus and never blocking a click. Everyone who runs Cowpanion in the same
*pasture* gets a cow on everyone else's screen. Press **Ctrl+Alt+C**, type a short line, and it
appears as a speech bubble over your cow for all of them.

No accounts, no message history, no telemetry. Windows 10/11 only.

**Website and download:** <https://cows.carlospoupado.com/>

## What is in this repository

| Folder | Contents |
| --- | --- |
| `client/` | The Windows app: WPF on .NET 10. `Cowpanion.Core` (herd simulation, no UI), `Cowpanion.Net` (pasture client), `Cowpanion.App` (overlay, tray, chat). xUnit tests. |
| `server/` | The pasture server: ~600 lines of Node.js relaying presence and chat over WebSocket. One dependency (`ws`). `node:test` suite. |
| `server/deploy/` | Everything to run it on a Debian VPS: idempotent `install.sh`, systemd unit, nginx site, logrotate, and a step-by-step `RUNBOOK.md`. |
| `site/` | The static announcement page. |
| `docs/` | The wire protocol, the two build plans, the decision log, and the acceptance checklists. |
| `assets/` | The seven cow sprite sheets. |

## Quick start

**Use it:** download the zip from the website, extract it somewhere permanent, run
`Cowpanion.exe`, pick a name. Tray icon → *Start with Windows* if you like. Hotkeys:
`Ctrl+Alt+C` chat, `Ctrl+Alt+M` mute bubbles, `Ctrl+Alt+Shift+K` quit.

**Build the client** (needs the .NET 10 SDK, no Visual Studio):

```powershell
cd client
dotnet build Cowpanion.sln -c Release
dotnet test  Cowpanion.sln -c Release
```

**Run the server locally** (needs Node 22+):

```bash
cd server
npm ci
npm test
npm start          # listens on 127.0.0.1:8787; point the client's serverUrl at ws://127.0.0.1:8787/ws
```

**Deploy the server:** see `server/deploy/RUNBOOK.md`.

## Play with friends

The default pasture is `commons`. For a private herd, agree on a code (1–32 characters,
lowercase letters, digits, hyphens) and set `"pasture"` in `%APPDATA%\Cowpanion\config.json`.
The app picks the change up without a restart. A pasture holds up to 24 people and shows up to
12 cows; beyond that a small "+N" badge appears.

## Privacy

Chat text is never stored on the server, not even in logs. The only persisted server state is a
ban list of hashed client ids. Web-server access logs keep IP addresses for at most 7 days. The
client sends nothing beyond the fields in `docs/cowpanion-protocol.md`. Full statement:
`client/src/Cowpanion.App/PRIVACY.md`.

## Status

First release. The client and server are complete for the planned v1 scope; a list of what has
been verified automatically and what still needs a human is in `docs/ACCEPTANCE.md`.

Made by Carlos Poupado.

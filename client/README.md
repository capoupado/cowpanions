# Cowpanion — desktop client

A small herd of pixel-art cows grazes along the bottom of your screen, on top of every other window. The herd
size reflects who is in your pasture, and the cows talk via speech bubbles. WPF, .NET 10, x64, Windows 10/11.

Plan and contract: `../docs/cowpanion-client-plan.md`, `../docs/cowpanion-protocol.md`, `../docs/DECISIONS.md`.
Acceptance status: `ACCEPTANCE.md`. Privacy statement shipped next to the binary: `src/Cowpanion.App/PRIVACY.md`.

## Layout

```
Cowpanion.sln
Directory.Build.props            Nullable, LangVersion latest, TreatWarningsAsErrors, x64
src/Cowpanion.Core/              simulation, sprite manifest, config (BCL only — no UI, no network)
src/Cowpanion.Net/               PastureClient (ClientWebSocket), protocol codec (refs Core only)
src/Cowpanion.App/               WPF overlay, tray, interop (AssemblyName: Cowpanion)
spike/Cowpanion.Spike/           P0 feasibility spike: transparent strip + sliding rectangle + CPU sampler
tests/Cowpanion.Core.Tests/      xUnit, headless
tests/Cowpanion.Net.Tests/       xUnit, loopback HttpListener WebSocket server + manual clock
assets/sprites/cow/              seven sprite sheets + manifest.json (linked into the App output)
```

## Build, test, run

Requires the .NET 10 SDK (`dotnet --version` → 10.0.x). No Visual Studio needed.

```powershell
cd client
dotnet build -c Release          # must be warning-free: TreatWarningsAsErrors is on
dotnet test  -c Release          # Core + Net tests, headless, ~40 s
dotnet run --project src/Cowpanion.App -c Release
```

Releases (installer, portable zip, update feed) come from `.\release.ps1 -Version X.Y.Z`; see "Releases and
updates" below. A plain self-contained folder for local testing:

```powershell
dotnet publish src/Cowpanion.App -c Release -r win-x64 --self-contained -p:BaseOutputPath=bin-publish/ -o publish
```

`publish/` then holds `Cowpanion.exe` plus its DLLs, `assets/` and `PRIVACY.md`. Such a build is not a Velopack
install, so it never checks for updates. `bin/`, `obj/` and `publish/` are git-ignored.

### P0 spike (CPU measurement)

```powershell
dotnet run --project spike/Cowpanion.Spike -c Release -- --exit-after 300 --report
```

Prints CPU % of total every 5 s and an average at exit. Kill hotkey `Ctrl+Alt+Shift+K`.

### Development flags (all executables)

| Flag | Effect |
| --- | --- |
| `--exit-after N` | quit after N seconds (always use this when launching from a script) |
| `--config PATH` | App only: use this config file instead of `%APPDATA%\Cowpanion\config.json` |
| `--no-dialog` | App only: skip the first-run name dialog (implied by `--exit-after`) |
| `--inject-chat-exception` | App only: the first `Ctrl+Alt+C` throws after dropping click-through, to prove the finally path restores it |
| `--dump-emoji PATH` | App only: render sample colour emoji to a PNG and exit (runs before the mutex) |
| `--dump-windows DIR` | App only: render the settings window (one PNG per tab) and the chat history window with sample entries to DIR and exit (before the mutex, nothing saved) |
| `--update-feed URL\|DIR` | App only: use this Velopack feed instead of `https://cows.carlospoupado.com/updates` |
| `--update-now` | App only: check, download and apply an update with no UI, then exit (before the mutex; release testing on an installed build) |
| `--report` | Spike only: print average CPU at exit |

## Configuration

`%APPDATA%\Cowpanion\config.json`, created with defaults on first run, hot-reloaded on change (500 ms debounce).
Invalid values are clamped and logged; a malformed file is backed up to `config.bad.json` and replaced.
A small diagnostic log lives next to it in `cowpanion.log` (never contains chat text).

| Key | Default | Meaning |
| --- | --- | --- |
| `spritePack` | `"cow"` | folder under `assets/sprites/` (restart to change) |
| `scale` | `3` | integer sprite scale 1–6 |
| `monitors` | `"primary"` | `"primary"` or `"all"` |
| `activeFps` / `idleFps` | `30` / `5` | tick rate when cows move / when everything is still |
| `mooEnabled` | `false` | optional moo (no audio file is bundled yet; see `Audio/MooPlayer.cs`) |
| `sleepAfterIdleMinutes` | `20` | herd inactivity before cows may sleep |
| `pauseOnFullscreen` | `true` | hide during fullscreen games / presentations (socket stays up) |
| `startWithWindows` | `false` | HKCU Run key |
| `multiplayerEnabled` | `true` | connect to the pasture server; off = local herd only, zero network |
| `serverUrl` | `wss://cows.carlospoupado.com/ws` | the only network endpoint the app ever talks to |
| `pasture` | `"commons"` | room code, `[a-z0-9-]{1,32}` |
| `displayName` | `""` | asked once on first run (Windows user name prefilled), max 16 chars |
| `clientId` | `""` | generated once from a CSPRNG (32 hex chars); never derived from anything identifying |
| `bubblesEnabled` / `bubblesMuted` | `true` / `false` | mute is toggled by the mute hotkey and persists |
| `offlineHerdSize` | `4` | filler cows when multiplayer is off or unreachable (0–12) |
| `variant` | `""` | cow colour; empty = derived from `clientId` once and saved. Tray → Cow colour |
| `hotkeys` | see below | `kill`, `chat`, `mute`, `heart`, `focusMode`, each a combination like `"Ctrl+Alt+C"`; `""` unbinds (not allowed for `kill`) |
| `focusMode` | `false` | release every hotkey except `kill` and `focusMode`; persists until turned off |
| `autoCheckForUpdates` | `true` | installed builds: check the update feed 45 s after start and every 24 h |

Everything except `clientId` is also editable in **Settings…** (tray menu, or double-click the tray icon).

Variants: `black0`, `black1`, `brown`, `white0`, `white1`, `white_darkspots`, `white_pinkspots`.

## Hotkeys

Defaults below; every one can be rebound in Settings → Hotkeys or in `config.json`. Text form is modifiers (`Ctrl`,
`Alt`, `Shift`, `Win`) then one key: `A`–`Z`, `0`–`9`, `F1`–`F24`, `Num0`–`Num9`, `Space`, `Enter`, `Tab`, `Esc`,
arrows, `Home`/`End`/`PageUp`/`PageDown`/`Insert`/`Delete`, `Pause`, `ScrollLock`, `PrintScreen`, and the OEM keys by
name (`Minus`, `Equals`, `Comma`, `Period`, `Slash`, `Semicolon`, `Quote`, `Backtick`, `LeftBracket`, `RightBracket`,
`Backslash`). A key with no modifier or Shift only must be F1–F24, Pause or Scroll Lock, because anything else would
swallow ordinary typing in every app. A combination Windows or another app already owns is reported in Settings and
the log; if the quit combination is taken the app falls back to `Ctrl+Alt+Shift+K`.

| Default | Action |
| --- | --- |
| `Ctrl+Alt+Shift+K` | quit immediately (registered before any overlay window exists; can be rebound, never unbound) |
| `Ctrl+Alt+C` | arm chat mode: input appears near your cow; Enter sends, Esc cancels, 8 s idle or a click elsewhere disarms |
| `Ctrl+Alt+M` | mute/unmute speech bubbles (persisted) |
| `Ctrl+Alt+H` | send a heart reaction |
| `Ctrl+Alt+F` | focus mode on/off: while on, only quit and this toggle stay registered |

## Tray menu

Status line · Multiplayer on/off · Mute bubbles · Send a heart · Chat history… · Focus mode · Cow colour ▸
(seven variants) · Filler herd + / − · Start with Windows · Settings… · Check for updates (shows the version) ·
Restart to update (only when one is downloaded) · Open config.json · Reload config.json · Quit.
Labels show the current hotkey. Double-clicking the icon opens Settings.

**Chat history** lists the last 200 messages, emotes and reactions received while the app runs (also while bubbles
are muted or the overlay is paused for full screen). Memory only: nothing is written to disk and it is gone on quit.

## Releases and updates

Distribution is Velopack (`Updates/UpdateService.cs`, `Program.cs`). `.\release.ps1 -Version X.Y.Z` builds
`..\dist\releases\` with `Cowpanion-win-Setup.exe`, `Cowpanion-win-Portable.zip`, the full and delta `.nupkg`,
`releases.win.json` and `SHA256SUMS.txt`, then prints the upload commands (GitHub release + scp of the feed to the
VPS; see `server/deploy/RUNBOOK.md` section 7). `vpk` comes from the local tool manifest (`dotnet tool restore`).

Testing an update end to end without touching a real install: build two versions with
`-PackId CowpanionTest -PackTitle "Cowpanion Test" -OutDir <temp> -NoDownload`, install the first with
`CowpanionTest-win-Setup.exe --silent`, pack a newer one, serve `<temp>\releases` with
`python -m http.server 18788 --bind 127.0.0.1`, run
`%LOCALAPPDATA%\CowpanionTest\current\Cowpanion.exe --update-now --update-feed http://127.0.0.1:18788`, check
`current\sq.version`, then `%LOCALAPPDATA%\CowpanionTest\Update.exe --silent --uninstall`.

## Notes for maintainers

- The overlay is click-through (`WS_EX_TRANSPARENT`), never activates (`WS_EX_NOACTIVATE`) and stays out of
  alt-tab (`WS_EX_TOOLWINDOW`). Styles are applied in `SourceInitialized` (`Overlay/OverlayWindow.xaml.cs`),
  toggled only by `Overlay/ChatInputHost.cs`, and re-checked every 4 s by the topmost watchdog.
- `Cowpanion.Core` must stay free of UI/network references: it is what makes the herd testable.
- The tick path allocates nothing: no per-frame `CroppedBitmap`, no LINQ, no `ObservableCollection`.
- `DispatcherTimer` quantises to the ~15.6 ms system tick, so the interval is set a few ms under `1000/fps`.

## Installing on another PC

1. Run `Cowpanion-win-Setup.exe` (from the website / GitHub release). It installs per-user into
   `%LOCALAPPDATA%\Cowpanion` with a Start menu entry and starts the app; no admin rights. Or unzip
   `Cowpanion-win-Portable.zip` anywhere permanent and run `Cowpanion.exe` from it.
2. First run asks for a display name; everything else is defaults (pasture `commons`, server
   `wss://cows.carlospoupado.com/ws`).
3. Tray icon → **Start with Windows** to launch at logon (writes `HKCU\...\Run\Cowpanion`, pointing at the
   launcher). Untick it, or set `startWithWindows: false` in the config, to remove the entry.
4. Updates arrive by themselves (tray → Check for updates to force one). Uninstall from Windows Settings → Apps.

Nothing outside `%LOCALAPPDATA%\Cowpanion`, `%APPDATA%\Cowpanion` and that one Run value.

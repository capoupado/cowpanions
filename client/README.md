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

Publish a self-contained single-file x64 build:

```powershell
dotnet publish src/Cowpanion.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

`publish/` then holds `Cowpanion.exe` (plus the WPF native DLLs, `assets/`, `PRIVACY.md`). Copy the folder anywhere
and run `Cowpanion.exe`. `bin/`, `obj/` and `publish/` are git-ignored.

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
| `bubblesEnabled` / `bubblesMuted` | `true` / `false` | mute is toggled by `Ctrl+Alt+M` and persists |
| `offlineHerdSize` | `4` | filler cows when multiplayer is off or unreachable (0–12) |
| `variant` | `""` | cow colour; empty = derived from `clientId` once and saved. Tray → Cow colour |

Variants: `black0`, `black1`, `brown`, `white0`, `white1`, `white_darkspots`, `white_pinkspots`.

## Hotkeys

| Keys | Action |
| --- | --- |
| `Ctrl+Alt+Shift+K` | quit immediately (registered before any overlay window exists) |
| `Ctrl+Alt+C` | arm chat mode: input appears near your cow; Enter sends, Esc cancels, 8 s idle or a click elsewhere disarms |
| `Ctrl+Alt+M` | mute/unmute speech bubbles (persisted) |

## Tray menu

Status line · Multiplayer on/off · Mute bubbles · Cow colour ▸ (seven variants) · Filler herd + / − ·
Open config · Reload config · Quit.

## Notes for maintainers

- The overlay is click-through (`WS_EX_TRANSPARENT`), never activates (`WS_EX_NOACTIVATE`) and stays out of
  alt-tab (`WS_EX_TOOLWINDOW`). Styles are applied in `SourceInitialized` (`Overlay/OverlayWindow.xaml.cs`),
  toggled only by `Overlay/ChatInputHost.cs`, and re-checked every 4 s by the topmost watchdog.
- `Cowpanion.Core` must stay free of UI/network references: it is what makes the herd testable.
- The tick path allocates nothing: no per-frame `CroppedBitmap`, no LINQ, no `ObservableCollection`.
- `DispatcherTimer` quantises to the ~15.6 ms system tick, so the interval is set a few ms under `1000/fps`.

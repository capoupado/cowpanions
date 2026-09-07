# Cowpanion Desktop Client — Build Plan / Agent Brief

A small herd of cows grazes and wanders along the bottom of the screen, on top of every
other window. The herd size reflects how many people are currently in your pasture, and the
cows can talk to each other via speech bubbles.

**Target:** Windows 10/11. WPF, .NET 10 (LTS), C#, x64.
**Companion documents:** `cowpanion-protocol.md` (wire contract, authoritative),
`cowpanion-server-plan.md` (backend track).

> Supersedes the earlier `cowpanion-build-plan.md`. Changes: .NET 10 instead of 8, a setup
> phase, multiplayer promoted to P3, taller strip for speech bubbles, and one hard rule
> amended for chat input.

---

## How to use this document

You are implementing this app one phase at a time. Rules for the implementing agent:

1. Read this entire document, plus `cowpanion-protocol.md`, before writing any code.
2. Implement **exactly one phase** per task. Stop at the phase boundary and report.
3. Do not start P1 until P0's acceptance criteria pass on real hardware. P0 exists to kill
   an architectural risk; skipping it can invalidate every later phase.
4. Every acceptance criterion must be manually verified and reported as pass/fail. "It
   compiles" is not an acceptance criterion.
5. If a criterion fails, stop and report the failure with measurements. Do not work around it
   by loosening the criterion.
6. Prefer boring, explicit code. This runs for hours in the background on someone's machine;
   a clever allocation-heavy render path is a bug.

The server track (S0–S3) runs in parallel and can be built by a different agent. The only
coupling: **client P3 cannot be verified until server S2 is deployed.**

---

## Hard rules (apply to every phase)

Invariants. Breaking any of them is a release blocker.

- **Never steals focus.** `WS_EX_NOACTIVATE` from P0 onward. The overlay must never activate,
  never take keyboard focus, never raise itself over a modal dialog by activation.
- **Never appears in alt-tab or the taskbar.** `WS_EX_TOOLWINDOW` + `ShowInTaskbar=False`.
- **Input-transparent by default.** `WS_EX_TRANSPARENT`. A click anywhere over the overlay
  lands on the application underneath.
  *Amended in P3:* input transparency may be dropped **only** while chat-interaction mode is
  explicitly armed by hotkey, only for a bounded few seconds, only with a visible on-screen
  indicator, and it must re-arm automatically. There is no other exception, and no mode that
  persists across restarts.
- **A global kill hotkey exists from P0.** `Ctrl+Alt+Shift+K` quits immediately. This is the
  escape hatch if the interop flags are ever wrong and the overlay starts eating input.
  Register it *before* creating the overlay window.
- **A global bubble-mute hotkey exists from P3.** `Ctrl+Alt+M` hides all bubbles instantly
  and persists across restarts.
- **Single instance.** Named mutex (`Global\Cowpanion`). Second launch exits silently.
- **Silent by default.** `mooEnabled` defaults to `false`. No audio before P4.
- **Cows stay inside the monitor work area.** Above the taskbar, never behind it.
- **Multiplayer is never load-bearing.** Server down, no network, or opted out → cows graze
  locally with the configured fallback herd size. No error dialogs, no empty screen, no
  blocking on connect at startup.
- **No telemetry, no auto-update, no network calls beyond the pasture server.**

---

## Stack decisions (already made — do not relitigate)

| Concern | Decision |
| --- | --- |
| Framework | WPF, .NET 10, `net10.0-windows`, `UseWPF` + `UseWindowsForms`, x64 |
| Architecture | UI-free simulation core + UI-free network client, rendered by a thin WPF layer |
| Render surface | One transparent strip window per monitor, all cows on one `Canvas` (validated in P0) |
| Tick | `DispatcherTimer` at `DispatcherPriority.Render`, retunable interval, real delta from a `Stopwatch` |
| Sprites | Pre-sliced, frozen `CroppedBitmap` frames on `Image` elements |
| Networking | `System.Net.WebSockets.ClientWebSocket`, no third-party library |
| Tray | WinForms `NotifyIcon` |
| Config | JSON at `%APPDATA%\Cowpanion\config.json` |
| Tests | xUnit against core and net projects, headless |

**MVVM note:** MVVM for the tray/settings surface only. The herd overlay is a render loop
writing directly to `Canvas` child transforms. Do not put cow positions in an
`ObservableCollection` and bind them — the change-notification overhead is pointless in a
render loop and will cost frames.

---

## P-1 — Setup (do this first, it isn't a phase)

Prerequisites on a clean machine:

```powershell
winget install Git.Git
winget install Microsoft.DotNet.SDK.10
winget install -e --id Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.ManagedDesktop --includeRecommended --passive --norestart"
```

Scaffold:

```powershell
git init cowpanion; cd cowpanion
dotnet new gitignore
dotnet new sln -n Cowpanion
dotnet new classlib -o src/Cowpanion.Core
dotnet new classlib -o src/Cowpanion.Net
dotnet new wpf      -o src/Cowpanion.App
dotnet new xunit    -o tests/Cowpanion.Core.Tests
dotnet new xunit    -o tests/Cowpanion.Net.Tests
dotnet sln add (Get-ChildItem -Recurse *.csproj)
dotnet add src/Cowpanion.App reference src/Cowpanion.Core src/Cowpanion.Net
dotnet add src/Cowpanion.Net reference src/Cowpanion.Core
dotnet add tests/Cowpanion.Core.Tests reference src/Cowpanion.Core
dotnet add tests/Cowpanion.Net.Tests reference src/Cowpanion.Net
```

Then three edits before any code:

**`Directory.Build.props`** at the root — `Nullable=enable`, `LangVersion=latest`,
`TreatWarningsAsErrors=true`, `Platforms=x64`. The x64 pin matters because this uses
`GetWindowLongPtr`/`SetWindowLongPtr`; leaving AnyCPU invites a subtle interop bug on the day
you forget.

**`Cowpanion.App.csproj`** — add `<UseWindowsForms>true</UseWindowsForms>` alongside
`UseWPF` (needed for `Screen.WorkingArea` and `NotifyIcon`), and
`<ApplicationManifest>app.manifest</ApplicationManifest>`.

**`app.manifest`** — `PerMonitorV2` DPI awareness. Do this now, not in P2. Retrofitting DPI
awareness after placement code exists means re-deriving every coordinate.

---

## Project layout

```
Cowpanion.sln
docs/
  cowpanion-client-plan.md
  cowpanion-protocol.md
src/
  Cowpanion.Core/            # net10.0. NO WPF/WinForms/network reference. Enforce this.
    Simulation/  Cow.cs  CowState.cs  Personality.cs  HerdSimulator.cs
                 TransitionTable.cs  Vec2.cs  Member.cs
    Sprites/     SpriteManifest.cs
    Configuration/ CowpanionConfig.cs  ConfigStore.cs
  Cowpanion.Net/             # net10.0. NO UI reference.
    PastureClient.cs         # connect, hello, heartbeat, backoff, events
    Protocol/  Messages.cs  MessageCodec.cs
    ChatMessage.cs
  Cowpanion.App/             # net10.0-windows, WPF
    App.xaml(.cs)
    Interop/   NativeMethods.cs  OverlayWindowStyler.cs  MonitorInfo.cs
               UserNotificationState.cs  GlobalHotkey.cs
    Overlay/   OverlayWindow.xaml(.cs)  HerdRenderer.cs  SpriteLibrary.cs
               BubbleRenderer.cs  ChatInputHost.cs
    Tray/      TrayIconHost.cs
    app.manifest
tests/
assets/sprites/cow/
```

`Cowpanion.Core` having no UI and no network reference is the load-bearing constraint. It
means the entire behaviour model is unit-testable without a window or a server, which is the
only way to iterate on cow behaviour without babysitting both a running overlay and a live
socket.

---

## Sprite input

The sprites already exist. Frame layout is never hardcoded — the app reads a manifest at
`assets/sprites/cow/manifest.json`:

```json
{
  "name": "cow",
  "sheet": "cow.png",
  "frameWidth": 32,
  "frameHeight": 32,
  "defaultScale": 3,
  "facing": "right",
  "anchor": "bottom-center",
  "animations": {
    "walk":  { "row": 0, "frames": 8, "fps": 10, "loop": true },
    "idle":  { "row": 1, "frames": 4, "fps": 4,  "loop": true },
    "graze": { "row": 2, "frames": 6, "fps": 6,  "loop": true },
    "lie":   { "row": 3, "frames": 3, "fps": 6,  "loop": false },
    "sleep": { "row": 4, "frames": 2, "fps": 2,  "loop": true }
  }
}
```

Also support a per-file form for any animation, so loose PNGs work without repacking:

```json
"walk": { "files": ["walk_0.png", "walk_1.png"], "fps": 10, "loop": true }
```

Rules:

- `facing` states which way the source art points. Rendering flips via
  `ScaleTransform ScaleX=-1` with `RenderTransformOrigin="0.5,0.5"`.
- `anchor: bottom-center` means a cow's position is its feet. Scaling must not make it float
  or sink.
- Any missing animation falls back to `idle`. A missing `idle` is a fatal startup error
  naming the manifest path.
- Slice every frame once at load, `Freeze()` each, cache in a dictionary. Never slice or
  decode during the tick.
- `RenderOptions.BitmapScalingMode="NearestNeighbor"`, `UseLayoutRounding="True"`. Scale must
  be an integer multiple or pixel art smears.

> **FILL IN BEFORE P1:** replace the `animations` block with the real frame counts, rows, and
> frame size from the existing sprite files, and confirm `facing`. Nothing else in this
> document depends on those numbers.

---

## Configuration

`%APPDATA%\Cowpanion\config.json`, written with defaults on first run, hot-reloaded on change
(`FileSystemWatcher`, debounced 500ms).

```json
{
  "spritePack": "cow",
  "scale": 3,
  "monitors": "primary",
  "activeFps": 30,
  "idleFps": 5,
  "mooEnabled": false,
  "sleepAfterIdleMinutes": 20,
  "pauseOnFullscreen": true,
  "startWithWindows": false,

  "multiplayerEnabled": true,
  "serverUrl": "wss://cows.carlospoupado.com/ws",
  "pasture": "commons",
  "displayName": "",
  "clientId": "",
  "bubblesEnabled": true,
  "bubblesMuted": false,
  "offlineHerdSize": 4
}
```

- `clientId` is generated from a CSPRNG on first run if empty, then never changed. 32 hex
  chars. Not derived from hostname, username, or MAC.
- `displayName` empty → prompt once via the tray on first run, defaulting to the Windows
  username truncated to 16 chars. Never send an empty name.
- `offlineHerdSize` is the local fallback when multiplayer is off or unreachable.
- Invalid values are clamped with a logged warning, never fatal. A malformed file is backed
  up to `config.bad.json` and replaced with defaults.

---

## Win32 interop reference

Provided so you don't guess. Belongs in `Interop/NativeMethods.cs`.

```
GWL_EXSTYLE          = -20
WS_EX_TRANSPARENT    = 0x00000020
WS_EX_LAYERED        = 0x00080000
WS_EX_TOOLWINDOW     = 0x00000080
WS_EX_NOACTIVATE     = 0x08000000

HWND_TOPMOST         = (IntPtr)(-1)
SWP_NOSIZE           = 0x0001
SWP_NOMOVE           = 0x0002
SWP_NOACTIVATE       = 0x0010

WM_HOTKEY            = 0x0312
MOD_ALT|MOD_CONTROL|MOD_SHIFT = 0x0001|0x0002|0x0004
```

- Use `GetWindowLongPtr`/`SetWindowLongPtr`, applied in `SourceInitialized` — earlier and the
  HWND doesn't exist, later and the window has already shown with the wrong styles.
- Work area: `System.Windows.Forms.Screen.WorkingArea` returns **physical pixels**; divide by
  that monitor's scale factor for WPF DIPs. Factor from `GetDpiForWindow(hwnd) / 96.0`.
- Fullscreen detection: `SHQueryUserNotificationState` (shell32). Suspend on
  `QUNS_RUNNING_D3D_FULL_SCREEN` (3) and `QUNS_PRESENTATION_MODE` (4).
- Global hotkeys: `RegisterHotKey` + `WM_HOTKEY` through an `HwndSource` hook.
- Topmost is contested. Re-assert
  `SetWindowPos(hwnd, HWND_TOPMOST, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` on a
  4-second timer — no more often, it's rude to the compositor.

---

## Simulation model

Pure, deterministic given a seed, no UI types, no `DateTime.Now` (delta time is passed in).

```csharp
enum CowState { Walk, Idle, Graze, Turn, LieDown, Sleep, Moo }

record Personality(
    double SpeedMultiplier,   // 0.7 .. 1.4
    double Laziness,          // 0..1, biases toward Graze/Idle/LieDown
    double Sociability,       // 0..1, cohesion strength
    double ScaleJitter);      // 0.9 .. 1.1

class Cow {
    string? MemberId;         // null for local filler cows
    bool IsSelf;
    string? DisplayName;
    Vec2 Position;            // DIPs, relative to the strip; Y is the ground line
    int Facing;               // -1 or +1
    CowState State;
    double StateElapsed, StateDuration;
    int AnimFrame; double AnimElapsed;
    Personality Personality;
}

class HerdSimulator {
    HerdSimulator(int seed, HerdSettings settings);
    void Tick(double deltaSeconds);
    void SyncMembers(IReadOnlyList<Member> members, string selfId, int overflow);
    void SetFillerCount(int n);          // offline / multiplayer-off mode
    IReadOnlyList<Cow> Cows { get; }
    void SetBounds(double widthDips);
}
```

**Personality must be derived deterministically from `MemberId`** (hash the id into the
personality parameters). Your friend's cow is then recognisably the same cow every session
and on every client — same speed, same laziness, same size jitter. This is a small detail
that does a disproportionate amount of the work in making the thing feel alive rather than
random.

**State machine.** Weighted transition table, weights scaled by personality, randomised
per-state durations. `Turn` is a short state entered before reversing `Facing`, so cows don't
flip instantly. `Sleep` reachable only after `sleepAfterIdleMinutes` of herd inactivity.

**Movement.** `Walk` advances `Position.X` by `baseSpeed * SpeedMultiplier * delta`. Base
speed around 18 DIPs/sec — slow is correct, these are cows. On reaching a bound, enter `Turn`.

**Herd behaviour.** Two forces only: *separation* (hard, enforce a minimum gap) and *cohesion*
(weak, scaled by `Sociability`, drift toward the mean X of nearby cows). No alignment force.
Those two alone produce clustering and the occasional follow-the-leader line. Do not add more
forces before P4 is signed off — this is exactly the kind of system that gets over-tuned into
mush.

**Variety is the point.** Randomise initial animation frame, state, and phase at spawn.

---

## Phases

### P0 — Feasibility spike

The one thing that can invalidate this architecture: WPF's `AllowsTransparency="True"` forces
the window into software rendering. A wide transparent strip repainting at 30fps might cost
1% CPU or it might cost 15%. Find out now, before there's code worth throwing away.

Build the smallest possible thing:

- One borderless, transparent, topmost, click-through, no-taskbar strip anchored to the
  primary monitor's work-area bottom, full width, **260 DIPs tall**.
- One solid-colour rectangle sliding left and right at 30fps.
- The global kill hotkey.
- CPU usage logged to console every 5 seconds.

**260, not 140.** Speech bubbles need vertical room above the cows, which roughly doubles the
transparent-pixel count versus the original plan. Measuring at the final height is the whole
point of this phase — measuring at 140 and discovering the problem in P3 wastes the spike.

**Acceptance criteria**

- [ ] Clicks and drags anywhere over the strip reach the window underneath, including on the
      taskbar and the desktop.
- [ ] Does not appear in alt-tab or the taskbar.
- [ ] Typing focus is never stolen, including at launch.
- [ ] Stays above a maximised (not fullscreen) browser and File Explorer.
- [ ] `Ctrl+Alt+Shift+K` quits from any foreground app.
- [ ] Sustained CPU **below 2% of total** over 5 minutes at 260 DIPs tall.
- [ ] No memory growth over 5 minutes.

**If the CPU criterion fails,** stop and report the measurement. Fallbacks in order: (1)
bubbles get their own small windows and the strip shrinks back to 140, (2) one small window
per cow, (3) a Win32 layered window driven by `UpdateLayeredWindow`, (4) SkiaSharp onto a
plain HWND. Each changes only the render layer — which is why the simulation core is kept
UI-free.

---

### P1 — One cow

- `Cowpanion.Core` with `Cow`, `CowState`, `HerdSimulator` (walk + idle only).
- `SpriteManifest` loading, `SpriteLibrary` frame slicing.
- One cow on the strip: walking, turning at edges, occasionally idling.
- Tray icon with a single **Quit**.
- Unit tests: stays in bounds; states change over time; a fixed seed reproduces identically
  across runs.

**Acceptance criteria**

- [ ] Feet sit on the ground line at scale 2, 3, and 4 without floating or sinking.
- [ ] Pixel art crisp at every integer scale — no bilinear smearing.
- [ ] Walk cycle reads correctly in both directions after the horizontal flip.
- [ ] Turning at bounds doesn't snap the sprite instantly.
- [ ] All P0 criteria still pass.

---

### P2 — The herd

- Full state machine: `Graze`, `LieDown`, `Sleep`, `Turn`, plus per-cow `Personality`.
- Separation and cohesion.
- Config file with defaults, validation, debounced hot reload.
- Multi-monitor: one strip per monitor per the `monitors` setting, each with its own
  simulator and DPI handling.
- Tray menu: Quit, Open config, Reload config, filler herd size +/-.
- Idle throttle: drop to `idleFps` when every cow is stationary, back to `activeFps` on first
  movement.

**Acceptance criteria**

- [ ] Six cows never visually overlap.
- [ ] Cows sometimes cluster, sometimes spread out, with no input.
- [ ] Editing `offlineHerdSize` in config takes effect within ~1s, no restart.
- [ ] Correct placement and scale on a second monitor with a different DPI.
- [ ] Correct re-placement after resolution change, DPI change, and monitor unplug.
- [ ] CPU below 3% with six cows; measurably lower when idle.
- [ ] No memory growth over a 1-hour run.

---

### P3 — Multiplayer

Requires server **S2** deployed. Implement `cowpanion-protocol.md` exactly.

**`Cowpanion.Net` — `PastureClient`**

- `ClientWebSocket` to `serverUrl`; send `hello`; handle `welcome`, `presence`, `chat`,
  `error`, `pong`.
- `ping` every 20s. Reconnect with exponential backoff 2s→60s, **±25% jitter mandatory**.
- Close codes 4000 and 4003 are **terminal** — stop reconnecting, log once, surface a tray
  hint, run local-only. A client that reconnect-loops against a terminal close is the
  specific bug that turns a hobby server into an accidental DDoS target.
- Connect **asynchronously after** the overlay is already rendering. Startup never waits on
  the network.
- Exposes events only: `MembersChanged`, `ChatReceived`, `ConnectionStateChanged`. No UI
  types, no `Dispatcher` — the App layer marshals.
- Unit-testable against a local loopback `HttpListener` WebSocket, no live server needed.

**Herd from presence**

- `HerdSimulator.SyncMembers` reconciles: new members walk in from the nearest edge, departed
  members walk out and despawn. **Never pop in or out** — a cow appearing instantly reads as
  a glitch.
- Own cow flagged `IsSelf`, with a subtle persistent marker so it's identifiable at a glance
  without a label.
- `overflow > 0` renders a small unobtrusive "+N" indicator, not more cows.
- Disconnected → fall back to `offlineHerdSize` filler cows, silently, with the same walk-in
  transition.

**Bubbles — `BubbleRenderer`**

- Max **2 lines, ~28 chars per line**, ellipsis beyond. 6s dwell, then fade out over 400ms.
- **One bubble per cow at a time**; further messages from that cow queue behind it. Global
  max **4 visible bubbles** — beyond that, oldest fades early.
- Tail flips side near strip edges so bubbles never clip off-screen.
- Bubble follows its cow as it walks. A cow with an active bubble is heavily biased toward
  `Idle` so the text stays readable.
- Rendered as WPF shapes and `TextBlock`, not images. Text is never logged to disk.
- `bubblesMuted` or presentation/fullscreen state → bubbles suppressed entirely, cows keep
  grazing.

**Chat input — `ChatInputHost`**

- `Ctrl+Alt+C` arms interaction mode: drop `WS_EX_TRANSPARENT`, show a small input near your
  own cow, show a visible mode indicator.
- Enter sends `chat`; Esc cancels; **auto-disarm after 8s idle**, restoring
  `WS_EX_TRANSPARENT` unconditionally.
- While armed, clicking your own cow also focuses the input. Clicking elsewhere disarms.
- Disarm must be in a `finally`-equivalent path: **if anything throws while armed, input
  transparency is still restored.** An exception that leaves the overlay permanently
  click-swallowing is the worst bug this app can have.
- Local echo comes from the server's `chat` relay, not optimistically — the sender's own
  bubble confirms round-trip.

**Acceptance criteria**

- [ ] Two clients in one pasture each show two cows; a third client makes both show three.
- [ ] Closing one client removes its cow from the others within 60s, walking out not popping.
- [ ] A message typed on one client appears as a bubble on all clients including the sender.
- [ ] Server stopped mid-session: cows keep grazing, fall back to filler, no dialog, no
      freeze, no CPU spike. Server restarted: reconnects within backoff and herd re-syncs.
- [ ] Machine sleep/resume and Wi-Fi off/on both recover without a restart.
- [ ] Interaction mode restores click-through after Enter, after Esc, after timeout, and
      after a thrown exception (verify by injecting one).
- [ ] `Ctrl+Alt+M` hides bubbles instantly and the setting survives a restart.
- [ ] A 140-char message with emoji renders inside the bubble without clipping or breaking
      glyphs.
- [ ] 4 clients each sending at the rate limit for 2 minutes: no bubble pileup, CPU under 4%.
- [ ] The same friend's cow has identical personality across two sessions and on two machines.
- [ ] All P0 hard rules still pass **while multiplayer is connected**.

---

### P4 — Good citizen

- Fullscreen/presentation suspend via `SHQueryUserNotificationState`, polled every 2s. On
  suspend: stop the tick, hide windows, suppress bubbles, but **keep the socket alive** so
  presence stays accurate. On resume, restore without teleporting cows.
- Cursor awareness: poll global cursor position; nearby cows turn to look, small chance to
  follow or spook. Read-only, input transparency untouched.
- `startWithWindows` writes/removes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Optional muted-by-default moo, long cooldown, per-cow probability.
- Ship `PRIVACY.md` alongside the binary.
- Packaging: self-contained x64 publish, installer or portable zip.

**Acceptance criteria**

- [ ] No cows and no measurable CPU during a fullscreen game or a PowerPoint presentation —
      and your cow stays visible to other members throughout.
- [ ] Cows react to a nearby cursor without ever intercepting a click.
- [ ] Toggling `startWithWindows` twice leaves the registry exactly as it started.
- [ ] Fresh install on a clean machine: launch → grazing cows → connected pasture, no console
      window, no crash.
- [ ] 8-hour soak with multiplayer connected: flat memory, no socket leak.

---

### P5 — Later, separately

Not now. Listed so the architecture doesn't preclude them.

- Sprite packs switchable from the tray; `variant` already rides in the protocol.
- Day/night behaviour from local sunrise/sunset.
- Cows walking along the top edges of real window titlebars (`EnumWindows` + geometry
  polling). A project of its own, not a feature.
- Multiple pastures, pasture switching from the tray.

---

## Explicitly out of scope, permanently

Linux/macOS, accounts or logins, message history, cow position sync, telemetry, auto-update,
cloud sync, in-app purchases, voice, more than one species per sprite pack.

---

## Known traps

- Adding ex-styles after `Show()` instead of in `SourceInitialized` — the window flashes with
  wrong styles and sometimes keeps them.
- `Screen.WorkingArea` treated as DIPs. It isn't. Wrong placement under scaling.
- `CroppedBitmap` created per frame in the tick — allocation churn and GC pauses.
- Non-integer sprite scale — instantly makes the art look cheap.
- Binding cow positions through `ObservableCollection` — pointless notification overhead in a
  render loop.
- Blocking startup on the WebSocket connect, so a dead server means a dead app.
- Reconnect loop without jitter — every client returns in the same millisecond after a server
  restart, forever.
- Reconnecting after a terminal close code.
- Rendering bubbles optimistically before the server relay, so your own bubble appears even
  when the message never left the machine.
- Leaving interaction mode armed on an exception path, permanently swallowing clicks.
- Forgetting the kill hotkey while iterating on interop, then having to kill the process from
  a Task Manager you can't click on.

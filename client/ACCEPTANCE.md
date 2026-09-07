# Cowpanion client — acceptance checklist

Status legend:

- **automated: pass** — covered by a test that passed in `dotnet test` (test name given).
- **observed: …** — I launched the binary on this machine (always with `--exit-after`, offline unless stated) and saw this. Indicative only.
- **manual: to verify** — needs the owner, real hardware, a second machine or the deployed server. Steps given.

Everything below was run on 2026-09-07 on the dev machine (Windows 11, single 2560-wide monitor at 100 % DPI,
16 logical CPUs). The pasture server was not deployed at the time, so every multiplayer criterion that needs a
live server is marked manual.

Final run: `dotnet build -c Release` → 0 warnings, 0 errors. `dotnet test -c Release` → Core 35/35, Net 18/18.

Interaction round (2026-09-07, protocol v2): `dotnet build -c Release -p:BaseOutputPath=bin-publish/` → 0 warnings,
0 errors. `dotnet test` → Core 50/50, Net 54/54. Publish to `dist/Cowpanion-win-x64` succeeded; a 15 s smoke run of
the published exe against the (still v1) deployed server logged close 4000 once, ran local-only, no tick errors.

---

## Hard rules (every phase)

| Rule | Where | Status |
| --- | --- | --- |
| Never steals focus (`WS_EX_NOACTIVATE`) | `Interop/OverlayWindowStyler.ApplyOverlayStyles`, called from `OverlayWindow.OnSourceInitialized`; window has `ShowActivated=False`, `Focusable=False` | observed: launching the app while typing in this terminal never moved focus (commands kept executing). manual: to verify — launch, keep typing in Notepad, confirm no keystroke is lost at launch or during 60 s. |
| Never in alt-tab or taskbar (`WS_EX_TOOLWINDOW`, `ShowInTaskbar=False`) | same, plus `OverlayWindow.xaml` | manual: to verify — press Alt+Tab and look at the taskbar; "Cowpanion" must not appear (only the tray icon). |
| Input-transparent by default (`WS_EX_TRANSPARENT`); dropped only while chat mode is armed, bounded, with an indicator, auto-restoring | `OverlayWindowStyler.SetInteractive`; `Overlay/ChatInputHost.cs` (8 s idle, Enter, Esc, click elsewhere, `finally`), `Orchestrator.OnTopmostTimer` re-checks every 4 s | observed: `--inject-chat-exception` run logged `chat arm failed … injected` immediately followed by `chat mode disarmed (arm failed)`; a subsequent Ctrl+Alt+C armed normally and Esc disarmed. manual: to verify — click and drag on the desktop, the taskbar and a browser through the cows; everything must land underneath. |
| Global kill hotkey `Ctrl+Alt+Shift+K` from P0, registered before the overlay window | Spike: `App.RegisterKillHotkey()` runs before `new SpikeWindow()`. App: `Orchestrator.Start()` registers on `GlobalHotkeys` (message-only HWND) before `BuildStrips()` | observed: sending Ctrl+Alt+Shift+K via `SendKeys` quit the App within 1 s (`quit: kill hotkey` in log, process exited). |
| Bubble-mute hotkey `Ctrl+Alt+M`, persisted | `Orchestrator.ToggleMute` → `Mutate` → `ConfigStore.Save` | observed: after sending Ctrl+Alt+M the config file contained `"bubblesMuted": true`. |
| Single instance (`Global\Cowpanion`) | `App.TryAcquireMutex` | manual: to verify — launch twice; the second exits silently (Task Manager shows one `Cowpanion.exe`). |
| Silent by default (`mooEnabled=false`, no audio before P4) | `CowpanionConfig.MooEnabled = false`; `Audio/MooPlayer` is a no-op without `assets/audio/moo.wav` (none shipped) | automated: pass (`ConfigTests.Missing_file_is_created_with_defaults_and_a_client_id` asserts `MooEnabled` false). |
| Cows stay inside the monitor work area, above the taskbar | `OverlayWindow.Place()` uses `Screen.WorkingArea` (physical px) ÷ DPI scale; `HerdSimulator` clamps X to `[w/2, width−w/2]` | automated: pass (`HerdSimulatorTests.Cows_stay_inside_bounds_for_ten_minutes`). observed: screenshot of the bottom 300 px of the work area showed all six cows standing on the ground line just above the taskbar. |
| Multiplayer never load-bearing | `Orchestrator.StartClientIfEnabled` is queued at `ApplicationIdle` after the strips are shown; `ApplyOffline()` puts the self cow up at startup and holds the fillers back until the first presence or an 8 s `StartupGrace` (so no filler trots out when the pasture answers); `PastureClient` only raises events | observed: with the server unreachable the app started instantly with 3 filler cows, logged backoff 2.1 s → 4.0 s → 7.9 s → 17.8 s and exited cleanly; no dialog. automated: pass (`PastureClientTests.Unreachable_server_backs_off_instead_of_failing`). |
| No telemetry, no auto-update, no other network calls | Only `Cowpanion.Net.PastureClient` opens a socket; grep for `HttpClient`/`WebRequest` in `src/` finds nothing | observed by code review; `PRIVACY.md` documents it. |

---

## P0 — Feasibility spike (`spike/Cowpanion.Spike`)

- [ ] Clicks and drags anywhere over the strip reach the window underneath, incl. taskbar and desktop — **manual: to verify**: run `dotnet run --project spike/Cowpanion.Spike -c Release -- --exit-after 300`, then click desktop icons, drag a file, click taskbar buttons inside the bottom 260 DIPs.
- [ ] Not in alt-tab or taskbar — **manual: to verify**: Alt+Tab while the spike runs.
- [ ] Typing focus never stolen, including at launch — **observed**: launched from a terminal three times; the terminal kept focus and subsequent commands ran. **manual: to verify** with a text editor focused at launch.
- [ ] Stays above a maximised browser and File Explorer — **manual: to verify**: maximise Edge and Explorer; the rectangle must stay visible over them (topmost re-asserted every 4 s).
- [ ] `Ctrl+Alt+Shift+K` quits from any foreground app — **manual: to verify** for the spike (verified via `SendKeys` on the App, same interop code).
- [ ] Sustained CPU below 2 % of total over 5 minutes at 260 DIPs tall — **observed (20 s, indicative)**: `REPORT: average cpu 0.09% of total over 20s`, steady-state samples 0.04 % at ~32 fps on a 2560-DIP-wide strip. Note the process-side number excludes DWM composition cost. **manual: to verify** for 5 minutes: `--exit-after 300 --report`, and watch Task Manager → Details → `Cowpanion.Spike.exe` and `dwm.exe`.
- [ ] No memory growth over 5 minutes — **observed (20 s)**: working set 101 → 102 → 102 MB. **manual: to verify** over 5 minutes from the 5 s samples.

## P1 — One cow

- [ ] Feet on the ground line at scale 2, 3 and 4 — **observed at scale 3** (screenshot: feet at the strip bottom, 6 DIPs above the work-area edge). **manual: to verify** at 2 and 4: edit `"scale"` in config.json (hot-reloads) and look for floating/sinking.
- [ ] Pixel art crisp at every integer scale — **observed at scale 3**: hard pixel edges in the screenshot (`NearestNeighbor` + `EdgeMode.Aliased`, integer scale only). **manual: to verify** at other scales and at 125 %/150 % DPI.
- [ ] Walk cycle reads correctly in both directions after the flip — **manual: to verify**: watch a cow walk right (flipped via `ScaleTransform ScaleX=-1`) and left (native).
- [ ] Turning at bounds doesn't snap — **automated: pass** (`HerdSimulatorTests.Turn_state_precedes_every_facing_flip`: every facing change is preceded by a 0.45 s `Turn` state). **manual: to verify** visually.
- [ ] Stays in bounds — **automated: pass** (`Cows_stay_inside_bounds_for_ten_minutes`).
- [ ] States change over time — **automated: pass** (`States_change_over_time`).
- [ ] Fixed seed reproduces identically — **automated: pass** (`Fixed_seed_reproduces_identically`, `Different_seeds_differ`).
- [ ] All P0 criteria still pass — see P0.
- [ ] Manifest: missing animation falls back to idle; missing idle fatal naming the path — **automated: pass** (`SpriteManifestTests.Missing_animation_falls_back_to_idle`, `Missing_idle_is_fatal_and_names_the_path`, `Invalid_json_is_fatal_with_path`, `Real_manifest_in_repo_parses_and_covers_all_states`).

## P2 — The herd

- [ ] Six cows never visually overlap — **automated: pass** (`Six_cows_never_visually_overlap`: 10 simulated minutes, centre gap ≥ cow width every tick). **observed**: six cows, no overlap in two screenshots.
- [ ] Cows sometimes cluster, sometimes spread — **automated: pass** (`Cows_both_cluster_and_spread_over_time`: spread varies by > 300 DIPs over 20 simulated minutes).
- [ ] Editing `offlineHerdSize` takes effect within ~1 s — **automated: pass** for the mechanism (`ConfigTests.External_edit_raises_Changed_after_debounce_but_own_save_does_not`). **manual: to verify** end-to-end: edit config.json in Notepad, save; new cows walk in / surplus cows walk out within about a second.
- [ ] Correct placement and scale on a second monitor with different DPI — **manual: to verify**: set `"monitors": "all"`, confirm each strip hugs its own taskbar and cows are the same physical-ish size. (`MonitorInfo` uses `GetDpiForMonitor`; `OverlayWindow.Place` uses physical pixels.) Only one monitor was available here.
- [ ] Re-placement after resolution change, DPI change, monitor unplug — **manual: to verify**: change resolution / scaling / unplug; strips rebuild ~1 s after `DisplaySettingsChanged` (cows keep their simulator per device name).
- [ ] CPU below 3 % with six cows; lower when idle — **observed (5 s sample, six cows, indicative)**: 0.10 % of total, working set 127 MB. **manual: to verify** in Task Manager over minutes; idle FPS drops to 5 when all cows are still and no bubbles show (`Orchestrator.ChooseFps`).
- [ ] No memory growth over 1 hour — **manual: to verify**: run for an hour and compare the working set in Task Manager at 0, 30 and 60 min. Tick path is allocation-free by construction (`HerdSimulatorTests.Tick_does_not_allocate_in_steady_state` asserts 0 bytes over 3000 ticks).
- [ ] Config clamping and bad-file backup — **automated: pass** (`Invalid_values_are_clamped_not_fatal`, `Malformed_file_is_backed_up_and_replaced`, `Unknown_properties_are_ignored_and_known_ones_kept`).
- [ ] Personality identical for the same MemberId across simulator instances — **automated: pass** (`Personality_is_identical_for_the_same_member_across_simulators`).
- [ ] Sleep only after the idle period — **automated: pass** (`Sleep_is_only_reachable_after_the_idle_period`).

## P3 — Multiplayer

- [ ] Two clients in one pasture show two cows; a third makes three — **manual: to verify** (needs server S2 + two machines/VMs). Mechanism: **automated: pass** (`PastureClientTests.Welcome_presence_and_chat_raise_events`; `HerdSimulatorTests.SyncMembers_walks_new_members_in_and_departed_members_out_without_teleporting`).
- [ ] Closing one client removes its cow within 60 s, walking out — **manual: to verify**. Mechanism: **automated: pass** (`SyncMembers_walks_…`, `Member_rejoining_while_leaving_turns_back`; `Dispose_sends_bye_and_closes_cleanly` proves the courtesy `bye`).
- [ ] A message typed on one client appears on all clients incl. the sender — **manual: to verify**. Own bubble is rendered only from the server echo (`ChatInputHost` never draws locally; `Orchestrator.OnChat` handles the relay). Sending: **automated: pass** (`Chat_is_sent_as_a_chat_frame_and_refused_when_disconnected`).
- [ ] Server stopped mid-session: cows keep grazing, filler fallback, no dialog, no freeze, no CPU spike; restart re-syncs — **observed**: unreachable server → filler herd, jittered exponential backoff, clean run. Fallback to fillers happens after a 20 s grace (`Orchestrator.FallbackGrace`) so blips don't churn the herd. **automated: pass** (`Backoff_doubles_from_2s_to_60s_with_jitter_within_25_percent`, `Backoff_resets_after_a_welcomed_session`). **manual: to verify** with the real server.
- [ ] Sleep/resume and Wi-Fi off/on recover — **automated: pass** for the detection mechanism (`Silent_server_is_detected_via_receive_timeout`: 75 s without frames aborts the socket and backs off). **manual: to verify** on hardware.
- [ ] Interaction mode restores click-through after Enter, Esc, timeout and a thrown exception — **observed**: Esc path and injected-exception path (see hard rules). Enter and 8 s timeout paths share `Disarm()`. **manual: to verify** all four: Ctrl+Alt+C then (a) Enter, (b) Esc, (c) wait 8 s, (d) launch with `--inject-chat-exception` and press Ctrl+Alt+C; after each, click through the strip onto the desktop.
- [ ] `Ctrl+Alt+M` hides bubbles instantly and survives restart — **observed**: setting persisted to config.json. **manual: to verify** that visible bubbles vanish instantly (`Orchestrator.ApplyConfig` calls `BubbleRenderer.ClearAll`).
- [ ] 140-char message with emoji renders inside the bubble — **automated: pass** for the text layout (`BubbleTextTests.Max_length_message_with_emoji_fits_two_lines`, `Emoji_are_never_split`). **manual: to verify** glyph rendering (font stack `Segoe UI, Segoe UI Emoji`).
- [ ] 4 clients at the rate limit for 2 minutes: no pileup, CPU under 4 % — **manual: to verify** (needs server). Design: one bubble per cow, per-cow queue capped at 8, global max 4 visible, oldest fades early.
- [ ] Same friend's cow has identical personality across sessions and machines — **automated: pass** (`Personality_is_identical_for_the_same_member_across_simulators`; FNV-1a of the member id, no per-process hashing).
- [ ] All P0 hard rules pass while connected — **manual: to verify** with the server.
- [ ] Protocol conformance: hello first and well-formed — **automated: pass** (`Hello_is_the_first_frame_and_well_formed`, `MessageCodecTests.Hello_has_exactly_the_specified_fields`). Ping every 20 s — **automated: pass** (`Ping_is_sent_every_20_seconds_of_clock_time`). 4000/4003 terminal — **automated: pass** (`Terminal_close_codes_stop_reconnecting`, both codes). 4001/4002 long backoff — **automated: pass** (`Pasture_full_and_abuse_use_the_long_backoff`). Unknown `t` ignored, oversized/malformed frames survive — **automated: pass** (`Unknown_types_and_garbage_are_ignored_and_the_session_survives`, `MessageCodecTests.Malformed_frames_decode_to_null_without_throwing`).

## P4 — Good citizen

- [ ] No cows and no measurable CPU during a fullscreen game or PowerPoint; own cow stays visible to others — **manual: to verify**: start a fullscreen game or a slideshow; within 2 s the strip hides and the tick stops (`Orchestrator.OnFullscreenPoll`, `SHQueryUserNotificationState` states 3/4); the socket keeps pinging. Ask a friend whether your cow stayed.
- [ ] Cows react to a nearby cursor without intercepting a click — **manual: to verify**: hover near a cow; idle cows turn to face the cursor, occasionally follow or spook. Click at the same time; the click lands underneath (cursor is read via `GetCursorPos`, never via mouse events).
- [ ] Toggling `startWithWindows` twice leaves the registry as it started — **manual: to verify**: note `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, set true then false in config.json, compare (`Interop/StartupRegistration.cs` writes/deletes exactly the `Cowpanion` value).
- [ ] Fresh install on a clean machine: launch → cows → connected pasture, no console, no crash — **observed** on this machine with a fresh scratch config: launched, cows rendered, no console window (`WinExe`), exit 0; publish to `publish/` succeeded (single-file self-contained, 140 MB). **manual: to verify** on a clean machine with the server up; the first-run name dialog appears there (skipped here via `--exit-after`).
- [ ] 8-hour soak with multiplayer: flat memory, no socket leak — **manual: to verify**. One `ClientWebSocket` per attempt, disposed in a `finally` (`PastureClient.RunAsync`).
- [ ] `PRIVACY.md` next to the binary — **observed**: present in `bin/Release/.../` and in `publish/`.
- [ ] Optional moo, muted by default — **observed** by code: `MooPlayer` is a no-op without `assets/audio/moo.wav`; none is shipped (TODO in the file).

## P4 — Interaction quick wins (protocol v2)

Decisions in `docs/DECISIONS.md` "Fourth round". Everything below was built against the v2 contract in
`docs/cowpanion-protocol.md`; the deployed server must be on the v2 build for the network items.

- [ ] **Hover name tag** — resting the cursor on a cow for ~0.4 s shows a small label above it (member name, "cow"
  for fillers, "(you)" suffix on the own cow); it fades in/out over 120 ms, follows the cow, hides while that cow has a
  bubble or is leaving. `Overlay/HoverLabelRenderer.cs`; hit test in `Orchestrator.OnTick` → `HerdRenderer.HitTest`
  (front lane wins when rects overlap) → `HerdSimulator.SetHovered`. Cursor is read via `GetCursorPos`; the label is
  `IsHitTestVisible=false` and the window stays `WS_EX_TRANSPARENT`. **automated: pass** for the hover timer (Core
  `HerdSimulatorTests`, hover tests). **manual: to verify** — hover a cow: tag after ~0.4 s, gone when the cursor
  leaves; click through it onto the desktop while it shows; hover a cow whose bubble is up: no tag.
- [ ] **Cursor startle** — a fast cursor sweep (> ~1500 DIPs/s) over the strip startles cows within ~150 DIPs into a
  short spooked walk away; a hovered relaxed cow looks up (idle2 row). **automated: pass** (Core `HerdSimulatorTests`
  startle/hover tests). **manual: to verify** — flick the mouse across the herd: nearby cows trot off, and the same cow
  is not startled again for a few seconds.
- [ ] **Lanes and roaming** — cows pick wander destinations anywhere on the strip; a walker blocked by a stationary
  cow steps into the back lane (drawn 14 DIPs higher and behind: `HerdRenderer` z-index = 100 − depth), passes, and
  returns to the front lane. Resting cows are always in the front lane. **automated: pass** (Core lane / wander tests;
  `Six_cows_never_visually_overlap` now means "never within the same lane"). **manual: to verify** — watch for 5
  minutes: your own cow visits both halves of the screen; a passing cow is drawn behind and slightly higher, never
  overlapping a front cow in its own lane; bubbles, the chat input and the hover tag follow the raised cow.
- [ ] **Emotes** — `/moo`, `/jump`, `/spin` in the chat box (text after the command is sent alongside as a bubble).
  Moo plays the moo row (and the sound hook when `mooEnabled`); Jump is two 22-DIP hops over 1 s; Spin flips the
  rendered facing every 125 ms for 1 s. The simulator holds the cow still; `HerdRenderer.Update` draws the flourish.
  **automated: pass** — parsing: `ChatComposerTests.Slash_emotes_become_emotes_with_trailing_text`; wire:
  `MessageCodecTests.Chat_v2_writes_only_non_empty_fields`,
  `PastureClientTests.Emote_is_sent_as_a_chat_frame_without_text`; timing: Core `TriggerEmote` tests.
  **manual: to verify** — two clients: `/jump` on one, both screens show that cow hop twice; `/spin hello` shows the
  spin and a "hello" bubble; the own cow only reacts on the server echo (offline nothing happens and the log says
  "chat not sent: not connected").
- [ ] **Reactions** — emoji-only input (1–3 emoji, spaces allowed; more are cut to three) and `/heart` `/love` `/lol`
  `/wave` `/party` `/wow` `/sad` send a `reaction`; receivers spawn 4–6 emoji rising ~90 DIPs from the cow's head over
  2.2 s with a sine sway, growing 0.8→1.1 and fading in the last 0.6 s (`Overlay/ReactionRenderer.cs`, pooled
  Image glyphs, global cap 40). No bubble. **automated: pass** — `ChatComposerTests.Emoji_only_input_is_a_reaction`,
  `Slash_reaction_commands_expand_case_insensitively`, `More_than_three_emoji_are_truncated_to_three`,
  `Everything_else_is_plain_text`; `MessageCodecTests.Chat_v2_server_frames_decode_with_optional_fields`.
  **manual: to verify** — type `❤️` and Enter: floating hearts over your cow on every screen; `/party gg` shows both a
  bubble and 🎉; Ctrl+Alt+M stops reactions and emotes from others as well as bubbles.
- [ ] **Ctrl+Alt+H hotkey and tray "Send a heart (Ctrl+Alt+H)"** — sends a `❤️` reaction with no arming and no
  focus change (`Orchestrator.SendHeart`; registered after the kill hotkey). Logs `heart not sent: not connected`
  offline. **manual: to verify** — while typing in another app press Ctrl+Alt+H: hearts over your cow, focus
  unchanged, no keystroke lost. Same via the tray item.
- [ ] **Reactions render in colour** — glyphs are Direct2D/DirectWrite bitmaps (`Overlay/EmojiRasterizer.cs`,
  `DrawTextOptions.EnableColorFont`, cached per emoji and pixel size, 22 DIPs at the monitor's DPI scale) because WPF
  text draws Segoe UI Emoji as black outlines; a rasteriser failure logs once and falls back to the TextBlock path.
  **observed** — `Cowpanion.exe --dump-emoji out.png` (runs before the single-instance mutex) produced a red heart,
  yellow 😂, 🎉 and a medium-skin-tone 👍🏽; 🇵🇹 shows as "PT" because Windows ships no flag glyphs.
- [ ] **Chat-mode label** — the amber label now reads "Chat mode - Enter sends, Esc cancels, 8 s idle closes.
  /moo /jump /spin = emote, emoji-only = reaction, smiley button or Win+. = emoji picker". **manual: to verify** —
  Ctrl+Alt+C and read it; still one line on a 1080p-wide strip.
- [ ] **Colour preview while typing** — the chat panel (now 340 DIPs: text box, preview strip, smiley button) shows the
  emoji the draft will send as Direct2D bitmaps (`ChatInputHost.UpdatePreview`, pooled `Image`s, 20 DIPs at the
  monitor's DPI scale) next to a caption naming the mode: "reaction" for emoji-only lines and `/heart`-style commands,
  "emote: jump" for `/jump …`; a text line that contains emoji previews up to six of them then "…", so the black
  outlines in the TextBox are identified. Empty draft: nothing shown. **manual: to verify** — Ctrl+Alt+C, type `❤️`:
  a red heart and "reaction" appear right of the text; type `/jump hi 🎉`: "emote: jump" plus a coloured 🎉; type
  `hello`: no preview; send and confirm the bubble/reaction matches the preview.
- [ ] **Emoji picker button** — the 😊 button at the right end of the panel (colour bitmap; `Focusable=False` so the
  text box keeps focus) synthesises Win+. through `SendInput` (`NativeMethods.SendWinPeriod`) and opens the Windows
  emoji panel. Because that panel is a separate window, it deactivates ours; `ChatInputHost` sets a 15 s grace
  (`_emojiPanelExpected`) during which `Deactivated` does not disarm and the idle counter is held at 0; the grace clears
  on the next `TextChanged` (the picked emoji landing), on window re-activation, or when it expires. Click-through
  handling is unchanged: the window is interactive only while armed and every exit still runs `Disarm`'s `finally`.
  Log lines: `emoji button: sent Win+. (4/4 events)`, `window deactivated while the emoji panel is expected — staying
  armed`, `emoji panel grace cleared (text changed)`. **manual: to verify** — Ctrl+Alt+C, click the smiley: the
  Windows picker opens, chat mode stays armed (panel and amber bar still visible); pick an emoji: it lands in the box
  and the colour preview shows it; Esc closes the picker, Enter sends. Then verify the strip is click-through again
  after Enter, and that clicking elsewhere while the picker is closed still disarms.
- [ ] **v2 compatibility** — hello sends `protocolVersion: 2`; a `welcome` with version 1 or 2 is accepted and logged
  as `welcome: vN …`; v1-shaped `chat` frames (text only) still render as bubbles. **automated: pass** —
  `PastureClientTests.Hello_is_the_first_frame_and_well_formed` (asserts 2), `Welcome_with_protocol_version_1_is_accepted`,
  `MessageCodecTests.V1_shaped_chat_frame_still_decodes_with_empty_emote_and_reaction`,
  `MessageCodecTests.Hello_has_exactly_the_specified_fields`. **observed** — against the deployed v1 server the
  published client received close 4000 once, logged "terminal close 4000 — not reconnecting" and ran local-only.
  **manual: to verify** after the server deploy — a friend on the old build still sees your text bubbles and simply
  does not see emotes or reactions.
- [ ] **Hard rules unchanged** — no new focus paths (hotkey and tray send without arming), all new visuals live on the
  non-hit-testable `BubbleCanvas`, no new network calls beyond the existing socket. **observed** by code review.

---

## How I launched things (for reproducibility)

```powershell
# spike, 20 s, CPU report
spike\Cowpanion.Spike\bin\Release\net10.0-windows\win-x64\Cowpanion.Spike.exe --exit-after 20 --report
# app, offline, scratch config, 12 s
src\Cowpanion.App\bin\Release\net10.0-windows\win-x64\Cowpanion.exe --exit-after 12 --config <scratch>\config.json
# app, hotkeys driven with System.Windows.Forms.SendKeys: ^%c (twice), {ESC}, ^%m, ^%+k
```

Every launch was followed by `Stop-Process -Name Cowpanion*` and no process was left running.

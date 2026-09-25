using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Cowpanion.App.Audio;
using Cowpanion.App.FirstRun;
using Cowpanion.App.Interop;
using Cowpanion.App.History;
using Cowpanion.App.Overlay;
using Cowpanion.App.Settings;
using Cowpanion.App.Tray;
using Cowpanion.App.Updates;
using Cowpanion.Core.Configuration;
using Cowpanion.Core.Simulation;
using Cowpanion.Core.Sprites;
using Cowpanion.Net;
using Microsoft.Win32;

namespace Cowpanion.App;

/// <summary>
/// Owns the whole running app: config, sprites, hotkeys, one <see cref="Strip"/> per monitor, the tray icon, the
/// pasture client and the timers. Everything here runs on the UI thread; network and config-watcher callbacks are
/// marshalled through the Dispatcher.
/// </summary>
internal sealed class Orchestrator : ISettingsHost, IDisposable
{
    private const double GroundInsetDips = 6;
    private static readonly TimeSpan FallbackGrace = TimeSpan.FromSeconds(20);
    /// <summary>How long a freshly started client may take to deliver its first presence before the offline fillers appear.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(8);
    /// <summary>First automatic update check: late enough to stay out of startup, early enough to catch a fresh release.</summary>
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly Application _app;
    private readonly StartupOptions _options;
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Strip> _strips = new();
    private readonly Dictionary<string, HerdSimulator> _simulatorsByDevice = new(StringComparer.Ordinal);
    private readonly EmojiRasterizer _emoji = new();
    private readonly ChatHistory _history = new();
    /// <summary>Hotkey action name (as in config.json) → why it is not registered right now.</summary>
    private readonly Dictionary<string, string> _hotkeyErrors = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _topmost;
    private readonly DispatcherTimer _fullscreen;
    private readonly DispatcherTimer _fallback;
    private readonly DispatcherTimer _displayDebounce;
    private readonly DispatcherTimer _updateTimer;

    private ConfigStore _store = null!;
    private CowpanionConfig _config = null!;
    private SpriteManifest _manifest = null!;
    private SpriteLibrary _sprites = null!;
    private GlobalHotkeys _hotkeys = null!;
    private TrayIconHost _tray = null!;
    private MooPlayer _moo = null!;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private UpdateService _updates = null!;
    /// <summary>True while a check was started from the tray, so its outcome gets a balloon even when nothing changed.</summary>
    private bool _manualUpdateCheck;
    /// <summary>True while a hotkey capture box in the settings window has focus: nothing is registered, not even Quit.</summary>
    private bool _hotkeysPaused;
    private PastureClient? _client;
    private PresenceSnapshot? _lastPresence;
    private ConnectionState _connection = ConnectionState.Disconnected;
    private bool _online;
    /// <summary>
    /// True from client start until the first presence (or the startup grace) arrives. While set, the offline herd is
    /// only the self cow, so fillers never show up just to trot out again when the pasture answers.
    /// </summary>
    private bool _awaitingFirstPresence;
    private bool _suspended;
    private double _lastTickSeconds;
    private int _currentFps;
    private int _tickErrors;
    private bool _disposed;

    public Orchestrator(Application app, StartupOptions options)
    {
        _app = app;
        _options = options;
        _dispatcher = app.Dispatcher;
        _tick = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        _tick.Tick += (_, _) => OnTick();
        _topmost = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _topmost.Tick += (_, _) => OnTopmostTimer();
        _fullscreen = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _fullscreen.Tick += (_, _) => OnFullscreenPoll();
        _fallback = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = FallbackGrace };
        _fallback.Tick += (_, _) => OnFallbackElapsed();
        _displayDebounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _displayDebounce.Tick += (_, _) => OnDisplayDebounceElapsed();
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = FirstUpdateCheck };
        _updateTimer.Tick += (_, _) => OnUpdateTimer();
    }

    /// <summary>Startup order matters: config → manifest → name → kill hotkey → strips → tray → timers → (later) network.</summary>
    public void Start()
    {
        string configPath = _options.ConfigPath ?? ConfigStore.DefaultPath;
        AppLog.Initialize(Path.GetDirectoryName(configPath)!);
        AppLog.Info("---- Cowpanion starting (pid " + Environment.ProcessId + ") ----");

        _store = new ConfigStore(configPath);
        _config = _store.Load();
        foreach (var line in _store.Log)
        {
            AppLog.Info(line);
        }

        string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "sprites", _config.SpritePack, "manifest.json");
        _manifest = SpriteManifest.Load(manifestPath); // SpriteManifestException is fatal and names the path
        _sprites = new SpriteLibrary(_manifest);
        _moo = new MooPlayer(AppContext.BaseDirectory);

        bool dirty = false;
        if (string.IsNullOrEmpty(_config.Variant))
        {
            _config.Variant = ConfigValidator.DeriveVariant(_config.ClientId, _manifest.VariantNames);
            AppLog.Info("variant derived from clientId: " + _config.Variant);
            dirty = true;
        }
        if (_config.DisplayName.Length == 0)
        {
            _config.DisplayName = ChooseFirstRunName();
            dirty = true;
        }
        if (dirty)
        {
            _store.Save(_config);
        }

        // Kill hotkey before any overlay window exists (ApplyHotkeys registers it first).
        _hotkeys = new GlobalHotkeys();
        ApplyHotkeys();

        _awaitingFirstPresence = _config.MultiplayerEnabled;
        BuildStrips();

        _tray = new TrayIconHost(_manifest.VariantNames);
        _tray.QuitRequested += () => Quit("tray");
        _tray.OpenConfigRequested += OpenConfig;
        _tray.ReloadConfigRequested += () => ApplyConfig(_store.Load());
        _tray.FillerDelta += delta => Mutate(c => c.OfflineHerdSize = Math.Clamp(c.OfflineHerdSize + delta, 0, 12));
        _tray.VariantSelected += v => Mutate(c => c.Variant = v);
        _tray.MuteToggled += ToggleMute;
        _tray.HeartRequested += SendHeart;
        _tray.JumpRequested += SendJump;
        _tray.MultiplayerToggled += () => Mutate(c => c.MultiplayerEnabled = !c.MultiplayerEnabled);
        _tray.StartupToggled += () => Mutate(c => c.StartWithWindows = !c.StartWithWindows);
        _tray.FocusModeToggled += ToggleFocusMode;
        _tray.SettingsRequested += OpenSettings;
        _tray.HistoryRequested += OpenHistory;
        _tray.CheckUpdatesRequested += () =>
        {
            _manualUpdateCheck = true;
            _ = _updates.CheckAsync();
        };
        _tray.RestartToUpdateRequested += () => _updates.RestartToApply(() => Quit("update"));
        RefreshTray();

        _updates = new UpdateService(_options.UpdateFeed ?? UpdateService.DefaultFeed, _dispatcher, AppLog.Info);
        _updates.Changed += OnUpdateChanged;
        OnUpdateChanged();
        if (_config.AutoCheckForUpdates && _updates.Stage != UpdateStage.NotInstalled)
        {
            _updateTimer.Start();
        }

        StartupRegistration.Apply(_config.StartWithWindows, AppLog.Info);

        _store.Changed += cfg => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ApplyConfig(cfg));
        _store.StartWatching();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        ApplyFps(_config.ActiveFps);
        _tick.Start();
        _topmost.Start();
        _fullscreen.Start();

        // Network only after the overlay is rendering. Startup never waits on it.
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, StartClientIfEnabled);

        if (_options.ExitAfterSeconds > 0)
        {
            var exit = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(_options.ExitAfterSeconds) };
            exit.Tick += (_, _) =>
            {
                exit.Stop();
                Quit("--exit-after");
            };
            exit.Start();
        }
        if (_options.InjectChatException && _strips.Count > 0)
        {
            _strips[0].Chat.InjectExceptionOnNextArm();
        }
        AppLog.Info($"running: {_strips.Count} strip(s), scale {_config.Scale}, variant {_config.Variant}, multiplayer {(_config.MultiplayerEnabled ? "on" : "off")}");
    }

    // ---------------------------------------------------------------- strips

    private void BuildStrips()
    {
        foreach (var strip in _strips)
        {
            strip.Chat.Disarm("rebuild");
            strip.Bubbles.ClearAll(strip.Simulator);
            strip.Window.Close();
        }
        _strips.Clear();

        var monitors = MonitorInfo.Enumerate(_config.Monitors == "all");
        foreach (var monitor in monitors)
        {
            double cowWidth = _manifest.FrameWidth * _config.Scale;
            if (!_simulatorsByDevice.TryGetValue(monitor.DeviceName, out var sim))
            {
                int seed = (int)(StableHash.Fnv1a64(_config.ClientId + "|" + monitor.DeviceName) & 0x7FFFFFFF);
                sim = new HerdSimulator(seed, new HerdSettings
                {
                    CowWidthDips = cowWidth,
                    SleepAfterIdleSeconds = _config.SleepAfterIdleMinutes * 60.0,
                });
                _simulatorsByDevice[monitor.DeviceName] = sim;
            }
            sim.SetCowWidth(cowWidth);
            sim.SetSleepAfterIdleSeconds(_config.SleepAfterIdleMinutes * 60.0);
            sim.SetBounds(monitor.WorkAreaWidthDips);
            sim.SetFillerVariants(_manifest.VariantNames);

            var window = new OverlayWindow(monitor);
            var herd = new HerdRenderer(window.HerdCanvas, _sprites, _config.Scale, OverlayWindow.StripHeightDips - GroundInsetDips);
            var bubbles = new BubbleRenderer(window.BubbleCanvas, herd);
            var hoverLabel = new HoverLabelRenderer(window.BubbleCanvas, herd);
            var reactions = new ReactionRenderer(window.BubbleCanvas, herd, _emoji, monitor.Scale);
            var simRef = sim;
            var chat = new ChatInputHost(window, herd, () => simRef, SendChatAsync, AppLog.Info, _emoji, monitor.Scale);
            chat.StateChanged += () => ApplyFps(ChooseFps());
            var strip = new Strip(monitor, window, sim, herd, bubbles, hoverLabel, reactions, chat);
            _strips.Add(strip);
            window.Show();
            AppLog.Info($"strip on {monitor.DeviceName}: work area {monitor.WorkAreaPx.Width}x{monitor.WorkAreaPx.Height}px @ {monitor.Scale:F2} → {monitor.WorkAreaWidthDips:F0} DIPs wide");
        }

        // Herd contents: members if we have them, otherwise the offline fallback herd.
        if (_online && _lastPresence is not null)
        {
            ApplyPresence(_lastPresence);
        }
        else
        {
            ApplyOffline();
        }
        if (_suspended)
        {
            foreach (var s in _strips)
            {
                s.Window.Hide();
            }
        }
    }

    private void OnTick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTickSeconds;
        _lastTickSeconds = now;
        if (dt <= 0)
        {
            return;
        }
        if (_suspended)
        {
            return;
        }
        try
        {
            bool cursorOk = NativeMethods.GetCursorPos(out var cursor);
            bool bubblesAllowed = _config.BubblesEnabled && !_config.BubblesMuted;

            for (int i = 0; i < _strips.Count; i++)
            {
                var strip = _strips[i];
                var sim = strip.Simulator;

                Cow? hovered = null;
                if (cursorOk && strip.TryToStripDips(cursor.X, cursor.Y, out double cx, out double cy))
                {
                    sim.SetCursor(cx, present: cy > -160);
                    if (cy >= 0 && cy <= OverlayWindow.StripHeightDips)
                    {
                        hovered = strip.Herd.HitTest(new Point(cx, cy), sim);
                    }
                }
                else
                {
                    sim.SetCursor(0, present: false);
                }
                sim.SetHovered(hovered);

                sim.Tick(dt);
                strip.Herd.Render(sim);
                if (bubblesAllowed)
                {
                    strip.Bubbles.Update(sim, dt, strip.Window.StripWidthDips);
                }
                strip.HoverLabel.Update(sim, dt, strip.Window.StripWidthDips);
                strip.Reactions.Update(dt);
                strip.Chat.Tick(dt);
                strip.Window.SetOverflow(sim.Overflow);

                if (_config.MooEnabled)
                {
                    var cows = sim.Cows;
                    for (int c = 0; c < cows.Count; c++)
                    {
                        if (cows[c].MooTriggered)
                        {
                            _moo.TryPlay();
                        }
                    }
                }
            }

            int wanted = ChooseFps();
            if (wanted != _currentFps)
            {
                ApplyFps(wanted);
            }
        }
        catch (Exception ex)
        {
            if (_tickErrors++ < 5)
            {
                AppLog.Info("tick error: " + ex);
            }
        }
    }

    private int ChooseFps()
    {
        for (int i = 0; i < _strips.Count; i++)
        {
            var s = _strips[i];
            if (!s.Simulator.AllStationary || s.Bubbles.AnyVisible || s.Reactions.AnyActive || s.HoverLabel.IsVisible || s.Chat.IsArmed)
            {
                return _config.ActiveFps;
            }
        }
        return _config.IdleFps;
    }

    private void ApplyFps(int fps)
    {
        _currentFps = fps;
        // DispatcherTimer quantises to the ~15.6 ms system tick; shave a few ms so 30 fps lands on two ticks, not three.
        double ms = Math.Max(10, 1000.0 / Math.Max(1, fps) - 3);
        _tick.Interval = TimeSpan.FromMilliseconds(ms);
    }

    private void OnTopmostTimer()
    {
        for (int i = 0; i < _strips.Count; i++)
        {
            var s = _strips[i];
            OverlayWindowStyler.ReassertTopmost(s.Window.Handle);
            s.Chat.EnsureClickThroughIfDisarmed();
        }
    }

    private void OnFullscreenPoll()
    {
        bool fullscreen = _config.PauseOnFullscreen && UserNotificationState.IsFullscreenOrPresenting();
        if (fullscreen == _suspended)
        {
            return;
        }
        _suspended = fullscreen;
        if (fullscreen)
        {
            AppLog.Info("fullscreen/presentation detected: suspending overlay (socket stays up)");
            foreach (var s in _strips)
            {
                s.Chat.Disarm("fullscreen");
                s.Bubbles.ClearAll(s.Simulator);
                s.Reactions.ClearAll();
                s.HoverLabel.Clear();
                s.Window.Hide();
            }
        }
        else
        {
            AppLog.Info("fullscreen/presentation ended: resuming overlay");
            _lastTickSeconds = _clock.Elapsed.TotalSeconds;
            foreach (var s in _strips)
            {
                s.Window.Show();
            }
        }
    }

    // ---------------------------------------------------------------- hotkeys and tray actions

    private void ArmChat()
    {
        if (_suspended || _strips.Count == 0)
        {
            return;
        }
        Strip target = _strips[0];
        for (int i = 0; i < _strips.Count; i++)
        {
            if (_strips[i].Simulator.FindSelf() is not null)
            {
                target = _strips[i];
                break;
            }
        }
        for (int i = 0; i < _strips.Count; i++)
        {
            _strips[i].Simulator.NotifyActivity();
        }
        target.Chat.Arm();
    }

    /// <summary>
    /// Registers the configured hotkeys. Quit goes first; if its combination is taken it falls back to the default
    /// Ctrl+Alt+Shift+K so the escape hatch always exists. In focus mode only Quit and the focus-mode toggle are
    /// registered. Failures are kept in <see cref="_hotkeyErrors"/> for the settings window and the log.
    /// </summary>
    private void ApplyHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeyErrors.Clear();
        if (_hotkeysPaused)
        {
            return;
        }
        var h = _config.Hotkeys;
        if (!RegisterBinding("kill", h.Kill, () => Quit("kill hotkey")))
        {
            bool fallback = h.Kill != HotkeyBindings.DefaultKill && RegisterBinding("killFallback", HotkeyBindings.DefaultKill, () => Quit("kill hotkey"));
            _hotkeyErrors.Remove("killFallback");
            if (fallback)
            {
                _hotkeyErrors["kill"] += $"; using {HotkeyBindings.DefaultKill} instead";
            }
            AppLog.Info(fallback ? $"kill hotkey {h.Kill} taken; fell back to {HotkeyBindings.DefaultKill}" : "WARNING: no kill hotkey could be registered");
        }
        RegisterBinding("focusMode", h.FocusMode, ToggleFocusMode);
        if (_config.FocusMode)
        {
            AppLog.Info("focus mode: only the quit and focus-mode hotkeys are registered");
            return;
        }
        RegisterBinding("chat", h.Chat, ArmChat);
        RegisterBinding("mute", h.Mute, ToggleMute);
        RegisterBinding("heart", h.Heart, SendHeart);
        RegisterBinding("jump", h.Jump, SendJump);
    }

    /// <summary>Empty text = unbound, which counts as success.</summary>
    private bool RegisterBinding(string action, string text, Action callback)
    {
        if (text.Length == 0)
        {
            return true;
        }
        if (!Hotkey.TryParse(text, out var hotkey, out string error))
        {
            _hotkeyErrors[action] = error; // ConfigValidator canonicalises first, so this is belt and braces
            return false;
        }
        if (_hotkeys.Register((uint)hotkey.Modifiers, hotkey.VirtualKey, callback))
        {
            return true;
        }
        _hotkeyErrors[action] = "in use by Windows or another app";
        AppLog.Info($"hotkey {action} = {hotkey} could not be registered (taken)");
        return false;
    }

    private void ToggleFocusMode()
    {
        Mutate(c => c.FocusMode = !c.FocusMode);
    }

    private void ToggleMute()
    {
        Mutate(c => c.BubblesMuted = !c.BubblesMuted);
    }

    /// <summary>Ctrl+Alt+H / tray: a heart reaction from our cow. No arming, no focus change; the echo draws it.</summary>
    private void SendHeart()
    {
        if (_suspended)
        {
            return;
        }
        var client = _client;
        if (client is null || client.State != ConnectionState.Connected)
        {
            AppLog.Info("heart not sent: not connected");
            return;
        }
        _ = SendReactionSafelyAsync(client, "❤️");
    }

    /// <summary>
    /// Ctrl+Alt+J / tray: our cow jumps. Connected → a "jump" emote to the pasture; the server echo animates it on every
    /// screen, ours included (never optimistically). Offline or multiplayer off → our own cow jumps here only.
    /// </summary>
    private void SendJump()
    {
        if (_suspended)
        {
            return;
        }
        var client = _client;
        if (client is not null && client.State == ConnectionState.Connected)
        {
            _ = SendEmoteSafelyAsync(client, "jump");
            return;
        }
        for (int i = 0; i < _strips.Count; i++)
        {
            var sim = _strips[i].Simulator;
            var self = sim.FindSelf();
            if (self is not null)
            {
                sim.NotifyActivity();
                sim.TriggerEmote(self, CowEmote.Jump);
                return;
            }
        }
        AppLog.Info("jump: no own cow on screen");
    }

    private static async Task SendEmoteSafelyAsync(PastureClient client, string emote)
    {
        try
        {
            if (!await client.SendChatAsync("", emote, ""))
            {
                AppLog.Info("emote not sent: not connected");
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("emote send failed: " + ex.GetType().Name);
        }
    }

    private static async Task SendReactionSafelyAsync(PastureClient client, string reaction)
    {
        try
        {
            if (!await client.SendChatAsync("", "", reaction))
            {
                AppLog.Info("reaction not sent: not connected");
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("reaction send failed: " + ex.GetType().Name);
        }
    }

    private void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Info("open config failed: " + ex.Message);
        }
    }

    /// <summary>Changes config from the UI: mutate, save, apply. The watcher ignores our own write. Returns the clamp corrections.</summary>
    private List<string> Mutate(Action<CowpanionConfig> change)
    {
        var next = _config.Clone();
        change(next);
        var warnings = ConfigValidator.Clamp(next);
        _store.Save(next);
        ApplyConfig(next);
        return warnings;
    }

    // ---------------------------------------------------------------- updates

    private void OnUpdateTimer()
    {
        _updateTimer.Interval = UpdateCheckInterval;
        if (_config.AutoCheckForUpdates)
        {
            _ = _updates.CheckAsync();
        }
    }

    /// <summary>Tray labels follow the update stage; balloons only for news, or for any outcome of a manual check.</summary>
    private void OnUpdateChanged()
    {
        string version = _updates.CurrentVersion;
        switch (_updates.Stage)
        {
            case UpdateStage.NotInstalled:
                _tray.SetUpdateItems($"Updates need the installed version (v{version})", false, null);
                break;
            case UpdateStage.Checking:
                _tray.SetUpdateItems("Checking for updates…", false, null);
                break;
            case UpdateStage.Downloading:
                _tray.SetUpdateItems($"Downloading v{_updates.Detail}… {_updates.Percent}%", false, null);
                break;
            case UpdateStage.Ready:
                _tray.SetUpdateItems($"Check for updates (v{version})", false, $"Restart to update to v{_updates.Detail}");
                if (_updateTimer.IsEnabled || _manualUpdateCheck)
                {
                    _tray.ShowBalloon("Cowpanion update ready", $"Version {_updates.Detail} is downloaded. Choose \"Restart to update\" in the tray menu, or it installs the next time Cowpanion starts.");
                }
                _manualUpdateCheck = false;
                _updateTimer.Stop(); // nothing more to fetch until the restart
                break;
            case UpdateStage.UpToDate:
                _tray.SetUpdateItems($"Check for updates (v{version})", true, null);
                if (_manualUpdateCheck)
                {
                    _tray.ShowBalloon("Cowpanion is up to date", $"Version {version} is the latest.");
                }
                _manualUpdateCheck = false;
                break;
            case UpdateStage.Failed:
                _tray.SetUpdateItems($"Check for updates (v{version})", true, null);
                if (_manualUpdateCheck)
                {
                    _tray.ShowBalloon("Update check failed", "The update server could not be reached. Try again later; details are in cowpanion.log.");
                }
                _manualUpdateCheck = false;
                break;
            default:
                _tray.SetUpdateItems($"Check for updates (v{version})", true, null);
                break;
        }
    }

    // ---------------------------------------------------------------- settings and history windows

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow(this, _manifest.VariantNames, FindSpritePacks());
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            SetHotkeysPaused(false);
        };
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private void OpenHistory()
    {
        if (_historyWindow is not null)
        {
            _historyWindow.Activate();
            return;
        }
        var window = new HistoryWindow(_history, _emoji);
        window.Closed += (_, _) => _historyWindow = null;
        _historyWindow = window;
        window.Show();
        window.Activate();
    }

    private static string[] FindSpritePacks()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "assets", "sprites");
        try
        {
            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .Select(d => Path.GetFileName(d))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    CowpanionConfig ISettingsHost.CurrentConfig => _config.Clone();

    string ISettingsHost.ConfigPath => _store.Path;

    string ISettingsHost.AppVersion => _updates.CurrentVersion;

    IReadOnlyDictionary<string, string> ISettingsHost.HotkeyErrors => _hotkeyErrors;

    IReadOnlyList<string> ISettingsHost.Apply(Action<CowpanionConfig> change) => Mutate(change);

    void ISettingsHost.SetHotkeysPaused(bool paused) => SetHotkeysPaused(paused);

    void ISettingsHost.OpenConfigFile() => OpenConfig();

    private void SetHotkeysPaused(bool paused)
    {
        if (paused == _hotkeysPaused || _disposed)
        {
            return;
        }
        _hotkeysPaused = paused;
        AppLog.Info(paused ? "hotkeys paused for capture" : "hotkeys resumed");
        ApplyHotkeys();
    }

    private void ApplyConfig(CowpanionConfig next)
    {
        if (_disposed)
        {
            return;
        }
        var prev = _config;
        _config = next;

        if (prev.Scale != next.Scale)
        {
            foreach (var s in _strips)
            {
                s.Herd.SetScale(next.Scale);
                s.Simulator.SetCowWidth(_manifest.FrameWidth * next.Scale);
            }
        }
        if (prev.SleepAfterIdleMinutes != next.SleepAfterIdleMinutes)
        {
            foreach (var sim in _simulatorsByDevice.Values)
            {
                sim.SetSleepAfterIdleSeconds(next.SleepAfterIdleMinutes * 60.0);
            }
        }
        if (prev.Monitors != next.Monitors)
        {
            BuildStrips();
        }
        if (prev.ActiveFps != next.ActiveFps || prev.IdleFps != next.IdleFps)
        {
            ApplyFps(ChooseFps());
        }
        if (prev.OfflineHerdSize != next.OfflineHerdSize && !_online)
        {
            ApplyOffline();
        }
        if (prev.StartWithWindows != next.StartWithWindows)
        {
            StartupRegistration.Apply(next.StartWithWindows, AppLog.Info);
        }
        if ((next.BubblesMuted && !prev.BubblesMuted) || (!next.BubblesEnabled && prev.BubblesEnabled))
        {
            foreach (var s in _strips)
            {
                s.Bubbles.ClearAll(s.Simulator);
                s.Reactions.ClearAll();
            }
        }
        if (!next.PauseOnFullscreen && _suspended)
        {
            OnFullscreenPoll();
        }
        if (!prev.Hotkeys.SameAs(next.Hotkeys) || prev.FocusMode != next.FocusMode)
        {
            if (next.FocusMode && !prev.FocusMode)
            {
                foreach (var s in _strips)
                {
                    s.Chat.Disarm("focus mode");
                }
            }
            ApplyHotkeys();
        }
        if (prev.AutoCheckForUpdates != next.AutoCheckForUpdates && _updates.Stage != UpdateStage.NotInstalled)
        {
            _updateTimer.Stop();
            if (next.AutoCheckForUpdates && _updates.Stage != UpdateStage.Ready)
            {
                _updateTimer.Interval = FirstUpdateCheck;
                _updateTimer.Start();
            }
        }
        if (prev.SpritePack != next.SpritePack)
        {
            AppLog.Info("spritePack changed; takes effect on next start");
        }

        bool netChanged = prev.MultiplayerEnabled != next.MultiplayerEnabled
            || prev.ServerUrl != next.ServerUrl
            || prev.Pasture != next.Pasture
            || prev.DisplayName != next.DisplayName
            || prev.ClientId != next.ClientId
            || prev.Variant != next.Variant;
        if (netChanged)
        {
            RestartClient();
        }
        RefreshTray();
        AppLog.Info("config applied");
    }

    private void RefreshTray()
    {
        _tray.Update(_config, StatusLine());
    }

    private string StatusLine()
    {
        if (!_config.MultiplayerEnabled)
        {
            return "Multiplayer off — local herd of " + _config.OfflineHerdSize;
        }
        return _connection switch
        {
            ConnectionState.Connected => $"Connected to '{_config.Pasture}' as {_config.DisplayName}" + (_lastPresence is null ? "" : $" — {_lastPresence.Members.Count} in pasture" + (_lastPresence.Overflow > 0 ? $" (+{_lastPresence.Overflow})" : "")),
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.BackingOff => "Server unreachable — retrying, local herd meanwhile",
            ConnectionState.Stopped => _client?.StopReason ?? "Stopped — local herd only",
            _ => "Offline — local herd",
        };
    }

    // ---------------------------------------------------------------- network

    private void StartClientIfEnabled()
    {
        // Startup arms the flag before the strips are built so no filler is spawned; if no client starts after all,
        // release it and show the full offline herd.
        bool wasAwaiting = _awaitingFirstPresence;
        _awaitingFirstPresence = false;
        if (_disposed || _client is not null || !_config.MultiplayerEnabled)
        {
            if (wasAwaiting)
            {
                ApplyOffline();
            }
            return;
        }
        if (!Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var uri))
        {
            AppLog.Info("serverUrl invalid; multiplayer disabled for this run");
            if (wasAwaiting)
            {
                ApplyOffline();
            }
            return;
        }
        var client = new PastureClient(new PastureClientOptions
        {
            ServerUrl = uri,
            ClientId = _config.ClientId,
            Pasture = _config.Pasture,
            DisplayName = _config.DisplayName,
            Variant = _config.Variant,
        });
        client.Log += line => AppLog.Info("net: " + line);
        client.ConnectionStateChanged += state => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnConnectionState(client, state));
        client.MembersChanged += snapshot => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnPresence(client, snapshot));
        client.ChatReceived += chat => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnChat(client, chat));
        _client = client;
        _awaitingFirstPresence = true;
        _fallback.Stop();
        _fallback.Interval = StartupGrace;
        _fallback.Start();
        client.Start();
        AppLog.Info("pasture client started (after first render)");
    }

    private void RestartClient()
    {
        var old = _client;
        _client = null;
        if (old is not null)
        {
            _ = old.DisposeAsync();
        }
        _online = false;
        _lastPresence = null;
        _connection = ConnectionState.Disconnected;
        _fallback.Stop();
        _awaitingFirstPresence = false;
        StartClientIfEnabled(); // sets _awaitingFirstPresence when a client actually starts
        ApplyOffline();
        RefreshTray();
    }

    private void OnConnectionState(PastureClient sender, ConnectionState state)
    {
        if (!ReferenceEquals(sender, _client))
        {
            return;
        }
        _connection = state;
        if (state == ConnectionState.Connected)
        {
            _fallback.Stop();
        }
        else if (state == ConnectionState.Stopped)
        {
            _fallback.Stop();
            _online = false;
            _lastPresence = null;
            _awaitingFirstPresence = false;
            ApplyOffline();
        }
        else if (_online && !_fallback.IsEnabled)
        {
            // Brief blips should not send the whole herd home; wait a little before falling back.
            _fallback.Start();
        }
        else if (!_online)
        {
            ApplyOffline();
        }
        RefreshTray();
    }

    private void OnFallbackElapsed()
    {
        _fallback.Stop();
        _fallback.Interval = FallbackGrace;
        if (_awaitingFirstPresence)
        {
            // The pasture did not answer within the startup grace: show the offline herd after all.
            _awaitingFirstPresence = false;
            ApplyOffline();
            RefreshTray();
            return;
        }
        if (_connection != ConnectionState.Connected)
        {
            _online = false;
            _lastPresence = null;
            ApplyOffline();
            RefreshTray();
        }
    }

    private void OnPresence(PastureClient sender, PresenceSnapshot snapshot)
    {
        if (!ReferenceEquals(sender, _client))
        {
            return;
        }
        _online = true;
        _lastPresence = snapshot;
        _awaitingFirstPresence = false;
        _fallback.Stop();
        _fallback.Interval = FallbackGrace;
        ApplyPresence(snapshot);
        RefreshTray();
    }

    private void ApplyPresence(PresenceSnapshot snapshot)
    {
        string selfId = _client?.YourId ?? _config.ClientId;
        foreach (var s in _strips)
        {
            s.Simulator.SetFillerCount(0);
            s.Simulator.SyncMembers(snapshot.Members, selfId, snapshot.Overflow);
        }
    }

    /// <summary>
    /// Offline herd: the user's own cow (own colour, self marker) plus offlineHerdSize − 1 fillers, so colour and
    /// name changes are visible without a server. When presence arrives the self cow is already there and stays.
    /// offlineHerdSize 0 means no cows at all. While the first presence of a freshly started client is pending, only
    /// the self cow is shown: fillers that would trot out seconds later when the pasture answers are never spawned.
    /// </summary>
    private void ApplyOffline()
    {
        int herd = _awaitingFirstPresence ? Math.Min(1, _config.OfflineHerdSize) : _config.OfflineHerdSize;
        var members = herd >= 1
            ? new[] { new Member(_config.ClientId, _config.DisplayName, _config.Variant) }
            : Array.Empty<Member>();
        foreach (var s in _strips)
        {
            s.Simulator.SyncMembers(members, _config.ClientId, 0);
            s.Simulator.SetFillerCount(Math.Max(0, herd - 1));
        }
    }

    private void OnChat(PastureClient sender, ChatMessage chat)
    {
        if (!ReferenceEquals(sender, _client))
        {
            return;
        }
        // History records everything received, including while muted or paused for fullscreen: that is when it helps.
        _history.Add(chat, string.Equals(chat.FromId, sender.YourId ?? _config.ClientId, StringComparison.Ordinal), DateTimeOffset.Now);
        if (_suspended || !_config.BubblesEnabled || _config.BubblesMuted)
        {
            return;
        }
        bool hasText = chat.Text.Length > 0;
        bool hasReaction = chat.Reaction.Length > 0;
        CowEmote emote = ParseEmote(chat.Emote);
        if (!hasText && !hasReaction && emote == CowEmote.None)
        {
            return;
        }
        // The server echoes our own chat back to us, so the own cow is handled here too — never optimistically.
        foreach (var s in _strips)
        {
            var cow = s.Simulator.FindMember(chat.FromId);
            if (cow is null || cow.Lifecycle == CowLifecycle.Leaving)
            {
                continue;
            }
            if (hasText)
            {
                s.Bubbles.Enqueue(cow, chat.Text);
            }
            if (hasReaction)
            {
                s.Reactions.Emit(cow, chat.Reaction);
            }
            if (emote != CowEmote.None)
            {
                // Moo enters the Moo state and sets MooTriggered, so OnTick's existing path plays the sound if enabled.
                s.Simulator.TriggerEmote(cow, emote);
            }
        }
    }

    private static CowEmote ParseEmote(string emote)
    {
        switch (emote)
        {
            case "moo":
                return CowEmote.Moo;
            case "jump":
                return CowEmote.Jump;
            case "spin":
                return CowEmote.Spin;
            default:
                return CowEmote.None;
        }
    }

    private Task<bool> SendChatAsync(string text, string emote, string reaction)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(false);
        }
        return client.SendChatAsync(text, emote, reaction);
    }

    // ---------------------------------------------------------------- system events

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _displayDebounce.Stop();
        _displayDebounce.Start();
    }

    private void OnDisplayDebounceElapsed()
    {
        _displayDebounce.Stop();
        if (_disposed)
        {
            return;
        }
        AppLog.Info("display settings changed: rebuilding strips");
        BuildStrips();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _lastTickSeconds = _clock.Elapsed.TotalSeconds;
            AppLog.Info("resumed from sleep");
        }
    }

    // ---------------------------------------------------------------- first run

    private string ChooseFirstRunName()
    {
        string fallback = ConfigValidator.SanitizeDisplayName(Environment.UserName);
        if (fallback.Length == 0)
        {
            fallback = "cow";
        }
        if (_options.NoDialog)
        {
            return fallback;
        }
        try
        {
            var dialog = new NameDialog(fallback);
            dialog.ShowDialog();
            return dialog.ChosenName.Length > 0 ? dialog.ChosenName : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Info("name dialog failed: " + ex.Message);
            return fallback;
        }
    }

    // ---------------------------------------------------------------- shutdown

    private void Quit(string reason)
    {
        AppLog.Info("quit: " + reason);
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _tick.Stop();
        _topmost.Stop();
        _fullscreen.Stop();
        _fallback.Stop();
        _displayDebounce.Stop();
        _updateTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        foreach (var s in _strips)
        {
            try
            {
                s.Chat.Disarm("shutdown");
                s.Window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
        _strips.Clear();
        _settingsWindow?.Close();
        _historyWindow?.Close();

        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }
        }
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _store?.Dispose();
        _emoji.Dispose();
        AppLog.Info("---- Cowpanion stopped ----");
    }
}

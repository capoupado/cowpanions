using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Cowpanion.App.Audio;
using Cowpanion.App.FirstRun;
using Cowpanion.App.Interop;
using Cowpanion.App.Overlay;
using Cowpanion.App.Tray;
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
internal sealed class Orchestrator : IDisposable
{
    private const double GroundInsetDips = 6;
    private static readonly TimeSpan FallbackGrace = TimeSpan.FromSeconds(20);
    /// <summary>How long a freshly started client may take to deliver its first presence before the offline fillers appear.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(8);

    private readonly Application _app;
    private readonly StartupOptions _options;
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Strip> _strips = new();
    private readonly Dictionary<string, HerdSimulator> _simulatorsByDevice = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _topmost;
    private readonly DispatcherTimer _fullscreen;
    private readonly DispatcherTimer _fallback;
    private readonly DispatcherTimer _displayDebounce;

    private ConfigStore _store = null!;
    private CowpanionConfig _config = null!;
    private SpriteManifest _manifest = null!;
    private SpriteLibrary _sprites = null!;
    private GlobalHotkeys _hotkeys = null!;
    private TrayIconHost _tray = null!;
    private MooPlayer _moo = null!;
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

        // Kill hotkey before any overlay window exists.
        _hotkeys = new GlobalHotkeys();
        if (!_hotkeys.Register(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, NativeMethods.VK_K, () => Quit("kill hotkey")))
        {
            AppLog.Info("WARNING: kill hotkey Ctrl+Alt+Shift+K could not be registered");
        }
        _hotkeys.Register(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_C, ArmChat);
        _hotkeys.Register(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_M, ToggleMute);

        _awaitingFirstPresence = _config.MultiplayerEnabled;
        BuildStrips();

        _tray = new TrayIconHost(_manifest.VariantNames);
        _tray.QuitRequested += () => Quit("tray");
        _tray.OpenConfigRequested += OpenConfig;
        _tray.ReloadConfigRequested += () => ApplyConfig(_store.Load());
        _tray.FillerDelta += delta => Mutate(c => c.OfflineHerdSize = Math.Clamp(c.OfflineHerdSize + delta, 0, 12));
        _tray.VariantSelected += v => Mutate(c => c.Variant = v);
        _tray.MuteToggled += ToggleMute;
        _tray.MultiplayerToggled += () => Mutate(c => c.MultiplayerEnabled = !c.MultiplayerEnabled);
        _tray.StartupToggled += () => Mutate(c => c.StartWithWindows = !c.StartWithWindows);
        RefreshTray();

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
            var simRef = sim;
            var chat = new ChatInputHost(window, herd, () => simRef, SendChatAsync, AppLog.Info);
            chat.StateChanged += () => ApplyFps(ChooseFps());
            var strip = new Strip(monitor, window, sim, herd, bubbles, chat);
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

                if (cursorOk && strip.TryToStripDips(cursor.X, cursor.Y, out double cx, out double cy))
                {
                    sim.SetCursor(cx, present: cy > -160);
                }
                else
                {
                    sim.SetCursor(0, present: false);
                }

                sim.Tick(dt);
                strip.Herd.Render(sim);
                if (bubblesAllowed)
                {
                    strip.Bubbles.Update(sim, dt, strip.Window.StripWidthDips);
                }
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
            if (!s.Simulator.AllStationary || s.Bubbles.AnyVisible || s.Chat.IsArmed)
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

    private void ToggleMute()
    {
        Mutate(c => c.BubblesMuted = !c.BubblesMuted);
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

    /// <summary>Changes config from the UI: mutate, save, apply. The watcher ignores our own write.</summary>
    private void Mutate(Action<CowpanionConfig> change)
    {
        var next = _config.Clone();
        change(next);
        ConfigValidator.Clamp(next);
        _store.Save(next);
        ApplyConfig(next);
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
            }
        }
        if (!next.PauseOnFullscreen && _suspended)
        {
            OnFullscreenPoll();
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
        if (!ReferenceEquals(sender, _client) || _suspended || !_config.BubblesEnabled || _config.BubblesMuted)
        {
            return;
        }
        if (chat.Text.Length == 0)
        {
            return;
        }
        foreach (var s in _strips)
        {
            var cow = s.Simulator.FindMember(chat.FromId);
            if (cow is not null && cow.Lifecycle != CowLifecycle.Leaving)
            {
                s.Bubbles.Enqueue(cow, chat.Text);
            }
        }
    }

    private Task<bool> SendChatAsync(string text)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(false);
        }
        return client.SendChatAsync(text);
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
        AppLog.Info("---- Cowpanion stopped ----");
    }
}

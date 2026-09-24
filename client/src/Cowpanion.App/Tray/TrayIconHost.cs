using System.Drawing;
using System.Globalization;
using Cowpanion.App.Interop;
using Cowpanion.Core.Configuration;
using WinForms = System.Windows.Forms;

namespace Cowpanion.App.Tray;

/// <summary>WinForms NotifyIcon with the tray menu. Raises events; the Orchestrator does the work.</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu = new();
    private readonly WinForms.ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    private readonly WinForms.ToolStripMenuItem _multiplayer = new("Multiplayer") { CheckOnClick = false };
    private readonly WinForms.ToolStripMenuItem _mute = new("Mute bubbles");
    private readonly WinForms.ToolStripMenuItem _heart = new("Send a heart");
    private readonly WinForms.ToolStripMenuItem _focus = new("Focus mode");
    private readonly WinForms.ToolStripMenuItem _history = new("Chat history…");
    private readonly WinForms.ToolStripMenuItem _settings = new("Settings…");
    private readonly WinForms.ToolStripMenuItem _quit = new("Quit");
    private readonly WinForms.ToolStripMenuItem _startup = new("Start with Windows");
    private readonly WinForms.ToolStripMenuItem _colour = new("Cow colour");
    private readonly WinForms.ToolStripMenuItem _fillerPlus = new("Filler herd +");
    private readonly WinForms.ToolStripMenuItem _fillerMinus = new("Filler herd −");
    private readonly Icon _drawnIcon;
    private readonly IntPtr _hIcon;

    public TrayIconHost(IReadOnlyList<string> variants)
    {
        _hIcon = DrawIcon(out _drawnIcon);

        _menu.Items.Add(_status);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_multiplayer);
        _menu.Items.Add(_mute);
        _menu.Items.Add(_heart);
        _menu.Items.Add(_history);
        _menu.Items.Add(_focus);
        _menu.Items.Add(_colour);
        _menu.Items.Add(_fillerPlus);
        _menu.Items.Add(_fillerMinus);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_startup);
        _menu.Items.Add(_settings);
        var open = new WinForms.ToolStripMenuItem("Open config.json");
        var reload = new WinForms.ToolStripMenuItem("Reload config.json");
        _menu.Items.Add(open);
        _menu.Items.Add(reload);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_quit);

        foreach (var v in variants)
        {
            var item = new WinForms.ToolStripMenuItem(v) { Tag = v };
            item.Click += (_, _) => VariantSelected?.Invoke(v);
            _colour.DropDownItems.Add(item);
        }

        _multiplayer.Click += (_, _) => MultiplayerToggled?.Invoke();
        _mute.Click += (_, _) => MuteToggled?.Invoke();
        _heart.Click += (_, _) => HeartRequested?.Invoke();
        _startup.Click += (_, _) => StartupToggled?.Invoke();
        _fillerPlus.Click += (_, _) => FillerDelta?.Invoke(+1);
        _fillerMinus.Click += (_, _) => FillerDelta?.Invoke(-1);
        open.Click += (_, _) => OpenConfigRequested?.Invoke();
        reload.Click += (_, _) => ReloadConfigRequested?.Invoke();
        _quit.Click += (_, _) => QuitRequested?.Invoke();
        _focus.Click += (_, _) => FocusModeToggled?.Invoke();
        _history.Click += (_, _) => HistoryRequested?.Invoke();
        _settings.Click += (_, _) => SettingsRequested?.Invoke();

        _icon = new WinForms.NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Cowpanion",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action? QuitRequested;
    public event Action? OpenConfigRequested;
    public event Action? ReloadConfigRequested;
    public event Action<int>? FillerDelta;
    public event Action<string>? VariantSelected;
    public event Action? MuteToggled;
    public event Action? HeartRequested;
    public event Action? MultiplayerToggled;
    public event Action? StartupToggled;
    public event Action? FocusModeToggled;
    public event Action? SettingsRequested;
    public event Action? HistoryRequested;
    public void Update(CowpanionConfig config, string status)
    {
        _status.Text = status;
        _multiplayer.Checked = config.MultiplayerEnabled;
        _mute.Checked = config.BubblesMuted;
        _focus.Checked = config.FocusMode;
        var h = config.Hotkeys;
        // In focus mode only Quit and the focus toggle are registered, so the other labels drop their combination.
        _mute.Text = WithHotkey("Mute bubbles", config.FocusMode ? "" : h.Mute);
        _heart.Text = WithHotkey("Send a heart", config.FocusMode ? "" : h.Heart);
        _focus.Text = WithHotkey("Focus mode (no hotkeys)", h.FocusMode);
        _quit.Text = WithHotkey("Quit", h.Kill);
        _startup.Checked = config.StartWithWindows;
        _fillerPlus.Text = "Filler herd + (now " + config.OfflineHerdSize.ToString(CultureInfo.InvariantCulture) + ")";
        _fillerMinus.Text = "Filler herd −";
        _fillerPlus.Enabled = config.OfflineHerdSize < 12;
        _fillerMinus.Enabled = config.OfflineHerdSize > 0;
        foreach (WinForms.ToolStripItem item in _colour.DropDownItems)
        {
            if (item is WinForms.ToolStripMenuItem mi)
            {
                mi.Checked = string.Equals(mi.Tag as string, config.Variant, StringComparison.Ordinal);
            }
        }
        _icon.Text = "Cowpanion — " + (status.Length > 40 ? status.Substring(0, 40) : status);
    }

    private static string WithHotkey(string label, string hotkey) => hotkey.Length > 0 ? label + " (" + hotkey + ")" : label;

    private static IntPtr DrawIcon(out Icon icon)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var body = new SolidBrush(Color.FromArgb(0x8B, 0x5A, 0x2B));
            using var spot = new SolidBrush(Color.FromArgb(0xF5, 0xEF, 0xE0));
            using var dark = new SolidBrush(Color.FromArgb(0x33, 0x22, 0x11));
            g.FillEllipse(body, 3, 8, 26, 18);
            g.FillEllipse(spot, 8, 11, 8, 6);
            g.FillEllipse(spot, 18, 16, 7, 6);
            g.FillEllipse(body, 20, 3, 10, 10);
            g.FillEllipse(dark, 27, 6, 2, 2);
        }
        IntPtr h = bmp.GetHicon();
        icon = Icon.FromHandle(h);
        return h;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _drawnIcon.Dispose();
        NativeMethods.DestroyIcon(_hIcon);
    }
}

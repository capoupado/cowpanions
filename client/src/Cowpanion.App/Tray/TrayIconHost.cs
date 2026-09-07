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
    private readonly WinForms.ToolStripMenuItem _mute = new("Mute bubbles (Ctrl+Alt+M)");
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
        _menu.Items.Add(_colour);
        _menu.Items.Add(_fillerPlus);
        _menu.Items.Add(_fillerMinus);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_startup);
        var open = new WinForms.ToolStripMenuItem("Open config");
        var reload = new WinForms.ToolStripMenuItem("Reload config");
        var quit = new WinForms.ToolStripMenuItem("Quit (Ctrl+Alt+Shift+K)");
        _menu.Items.Add(open);
        _menu.Items.Add(reload);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(quit);

        foreach (var v in variants)
        {
            var item = new WinForms.ToolStripMenuItem(v) { Tag = v };
            item.Click += (_, _) => VariantSelected?.Invoke(v);
            _colour.DropDownItems.Add(item);
        }

        _multiplayer.Click += (_, _) => MultiplayerToggled?.Invoke();
        _mute.Click += (_, _) => MuteToggled?.Invoke();
        _startup.Click += (_, _) => StartupToggled?.Invoke();
        _fillerPlus.Click += (_, _) => FillerDelta?.Invoke(+1);
        _fillerMinus.Click += (_, _) => FillerDelta?.Invoke(-1);
        open.Click += (_, _) => OpenConfigRequested?.Invoke();
        reload.Click += (_, _) => ReloadConfigRequested?.Invoke();
        quit.Click += (_, _) => QuitRequested?.Invoke();

        _icon = new WinForms.NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Cowpanion",
            ContextMenuStrip = _menu,
            Visible = true,
        };
    }

    public event Action? QuitRequested;
    public event Action? OpenConfigRequested;
    public event Action? ReloadConfigRequested;
    public event Action<int>? FillerDelta;
    public event Action<string>? VariantSelected;
    public event Action? MuteToggled;
    public event Action? MultiplayerToggled;
    public event Action? StartupToggled;

    public void Update(CowpanionConfig config, string status)
    {
        _status.Text = status;
        _multiplayer.Checked = config.MultiplayerEnabled;
        _mute.Checked = config.BubblesMuted;
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

using System.Windows;
using System.Windows.Interop;
using Cowpanion.App.Interop;

namespace Cowpanion.App.Overlay;

/// <summary>
/// One transparent strip per monitor. Ex-styles are applied in SourceInitialized (before the window is shown), and
/// the window is placed in physical pixels from the monitor's work area.
/// </summary>
public partial class OverlayWindow : Window
{
    public const double StripHeightDips = 260;

    private readonly MonitorInfo _monitor;

    internal OverlayWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    internal MonitorInfo Monitor => _monitor;

    public IntPtr Handle { get; private set; }

    /// <summary>Strip width in DIPs for this monitor's DPI.</summary>
    public double StripWidthDips => _monitor.WorkAreaWidthDips;

    public void SetOverflow(int overflow)
    {
        if (overflow > 0)
        {
            OverflowText.Text = "+" + overflow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (OverflowBadge.Visibility != Visibility.Visible)
            {
                OverflowBadge.Visibility = Visibility.Visible;
            }
        }
        else if (OverflowBadge.Visibility != Visibility.Collapsed)
        {
            OverflowBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Handle = new WindowInteropHelper(this).Handle;
        OverlayWindowStyler.ApplyOverlayStyles(Handle);
        Place();
    }

    /// <summary>Physical-pixel placement: Screen.WorkingArea is physical, and the strip height is 260 DIPs × scale.</summary>
    public void Place()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }
        double scale = _monitor.Scale;
        uint dpi = NativeMethods.GetDpiForWindow(Handle);
        if (dpi > 0)
        {
            scale = dpi / 96.0;
        }
        var work = _monitor.WorkAreaPx;
        int heightPx = (int)Math.Round(StripHeightDips * scale);
        OverlayWindowStyler.PlacePhysical(Handle, work.Left, work.Bottom - heightPx, work.Width, heightPx);
        Width = work.Width / scale;
        Height = StripHeightDips;
    }
}

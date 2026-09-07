using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cowpanion.Spike;

public partial class SpikeWindow : Window
{
    private const double StripHeightDips = 260;
    private const double SpeedDipsPerSecond = 120;

    private readonly TranslateTransform _translate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _topmost;
    private double _lastSeconds;
    private double _x;
    private int _direction = 1;

    public SpikeWindow()
    {
        InitializeComponent();
        Slider.RenderTransform = _translate;
        // 30 ms rather than 33.3 ms: DispatcherTimer quantises to the 15.6 ms system tick, and 33 ms rounds up to 46 ms (21 fps).
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, OnTick, Dispatcher);
        _topmost = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, OnReassertTopmost, Dispatcher);
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            _tick.Start();
            _topmost.Start();
        };
    }

    public long Frames { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Ex-styles exactly here: the HWND exists, the window has not been shown yet.
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        // Placement in physical pixels: Screen.WorkingArea is physical; the strip height is DIPs × scale.
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        int heightPx = (int)Math.Round(StripHeightDips * scale);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, work.Left, work.Bottom - heightPx, work.Width, heightPx, NativeMethods.SWP_NOACTIVATE);
        Width = work.Width / scale;
        Height = StripHeightDips;

        _translate.Y = StripHeightDips - Slider.Height - 4;
        _lastSeconds = _clock.Elapsed.TotalSeconds;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastSeconds, 0.1);
        _lastSeconds = now;

        double max = Width - Slider.Width;
        _x += _direction * SpeedDipsPerSecond * dt;
        if (_x >= max)
        {
            _x = max;
            _direction = -1;
        }
        else if (_x <= 0)
        {
            _x = 0;
            _direction = 1;
        }
        _translate.X = Math.Round(_x);
        Frames++;
    }

    private void OnReassertTopmost(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }
}

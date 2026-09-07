using System.Drawing;

namespace Cowpanion.App.Interop;

/// <summary>One monitor: work area in physical pixels (as Screen reports it) and its effective DPI scale.</summary>
internal sealed record MonitorInfo(string DeviceName, bool IsPrimary, Rectangle WorkAreaPx, Rectangle BoundsPx, double Scale)
{
    public double WorkAreaWidthDips => WorkAreaPx.Width / Scale;

    public static IReadOnlyList<MonitorInfo> Enumerate(bool allMonitors)
    {
        var list = new List<MonitorInfo>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (!allMonitors && !screen.Primary)
            {
                continue;
            }
            list.Add(new MonitorInfo(screen.DeviceName, screen.Primary, screen.WorkingArea, screen.Bounds, ScaleFor(screen)));
        }
        if (list.Count == 0 && System.Windows.Forms.Screen.PrimaryScreen is { } primary)
        {
            list.Add(new MonitorInfo(primary.DeviceName, true, primary.WorkingArea, primary.Bounds, ScaleFor(primary)));
        }
        return list;
    }

    private static double ScaleFor(System.Windows.Forms.Screen screen)
    {
        var center = new NativeMethods.POINT
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2,
        };
        IntPtr monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;
        }
        return 1.0;
    }
}

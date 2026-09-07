namespace Cowpanion.App.Interop;

/// <summary>
/// Applies and re-asserts the overlay's extended window styles. The hard rules live here:
/// WS_EX_NOACTIVATE (never steals focus), WS_EX_TOOLWINDOW (no alt-tab/taskbar), WS_EX_TRANSPARENT (click-through),
/// WS_EX_LAYERED. Interaction mode temporarily removes TRANSPARENT and NOACTIVATE and nothing else.
/// </summary>
internal static class OverlayWindowStyler
{
    private const long OverlayStyles = NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
    private const long InteractiveMask = NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;

    /// <summary>Call from SourceInitialized: the HWND exists and the window has not been shown yet.</summary>
    public static void ApplyOverlayStyles(IntPtr hwnd)
    {
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= OverlayStyles;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>True when the window is click-through (WS_EX_TRANSPARENT set).</summary>
    public static bool IsClickThrough(IntPtr hwnd)
    {
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (ex & NativeMethods.WS_EX_TRANSPARENT) != 0;
    }

    /// <summary>interactive=true drops TRANSPARENT and NOACTIVATE (chat mode); false restores both.</summary>
    public static void SetInteractive(IntPtr hwnd, bool interactive)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long wanted = interactive ? (ex & ~InteractiveMask) : (ex | OverlayStyles);
        if (wanted != ex)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(wanted));
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
    }

    /// <summary>Topmost is contested; re-assert every 4 s with SWP_NOACTIVATE (never more often).</summary>
    public static void ReassertTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Places the window in physical pixels, bypassing WPF's DIP conversion.</summary>
    public static void PlacePhysical(IntPtr hwnd, int x, int y, int width, int height)
    {
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height, NativeMethods.SWP_NOACTIVATE);
    }
}

using System.Windows.Interop;

namespace Cowpanion.App.Interop;

/// <summary>
/// Global hotkeys through RegisterHotKey + WM_HOTKEY on a message-only window. Created before any overlay window so
/// the kill hotkey works even if the overlay's styles are wrong.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;

    public GlobalHotkeys()
    {
        var parameters = new HwndSourceParameters("CowpanionHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)NativeMethods.WS_EX_TOOLWINDOW,
            ParentWindow = NativeMethods.HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
    }

    /// <summary>Returns false if the combination is already taken by another app.</summary>
    public bool Register(uint modifiers, uint vk, Action callback)
    {
        int id = _nextId++;
        bool ok = NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, vk);
        if (ok)
        {
            _callbacks[id] = callback;
        }
        return ok;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            callback();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _callbacks.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }
        _callbacks.Clear();
        _source.Dispose();
    }
}

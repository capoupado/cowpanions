namespace Cowpanion.App.Interop;

/// <summary>SHQueryUserNotificationState wrapper for fullscreen / presentation detection.</summary>
internal static class UserNotificationState
{
    public const int QUNS_NOT_PRESENT = 1;
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    public const int QUNS_QUIET_TIME = 6;
    public const int QUNS_APP = 7;

    /// <summary>True when a fullscreen D3D app or presentation mode is active; false on any failure.</summary>
    public static bool IsFullscreenOrPresenting()
    {
        try
        {
            int hr = NativeMethods.SHQueryUserNotificationState(out int state);
            if (hr != 0)
            {
                return false;
            }
            return state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}

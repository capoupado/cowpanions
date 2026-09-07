using Microsoft.Win32;

namespace Cowpanion.App.Interop;

/// <summary>startWithWindows ↔ HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Cowpanion. Idempotent.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cowpanion";

    public static void Apply(bool enabled, Action<string> log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            string? existing = key.GetValue(ValueName) as string;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0)
                {
                    return;
                }
                string wanted = "\"" + exe + "\"";
                if (existing != wanted)
                {
                    key.SetValue(ValueName, wanted, RegistryValueKind.String);
                    log("startWithWindows: Run key written");
                }
            }
            else if (existing is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log("startWithWindows: Run key removed");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log("startWithWindows: registry access failed: " + ex.Message);
        }
    }
}

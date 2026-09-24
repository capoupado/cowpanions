using Cowpanion.App.Interop;
using Velopack;

namespace Cowpanion.App;

/// <summary>
/// Entry point. <see cref="VelopackApp.Run"/> must be the very first thing: during install, update and uninstall
/// Velopack starts the exe with hook arguments, runs the matching callback and exits the process from inside Run().
/// A normal launch returns immediately and applies an already-downloaded update first (auto-apply is on by default).
/// Everything else (dev flags, the single-instance mutex) stays in <see cref="App.OnStartup"/>, after this.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main()
    {
        VelopackApp.Build()
            // Uninstall removes the app folder; also drop our HKCU Run value so Windows does not try to start a ghost.
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Apply(false, _ => { }))
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

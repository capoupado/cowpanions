using System.Windows;
using System.Windows.Threading;
using Cowpanion.Core.Sprites;

namespace Cowpanion.App;

public partial class App : Application
{
    private const string MutexName = @"Global\Cowpanion";

    private Mutex? _mutex;
    private Orchestrator? _orchestrator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch exits silently.
        if (!TryAcquireMutex())
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var options = StartupOptions.Parse(e.Args);
        _orchestrator = new Orchestrator(this, options);
        try
        {
            _orchestrator.Start();
        }
        catch (SpriteManifestException ex)
        {
            AppLog.Info("FATAL: " + ex.Message);
            MessageBox.Show(ex.Message, "Cowpanion cannot start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
        catch (Exception ex)
        {
            AppLog.Info("FATAL: " + ex);
            MessageBox.Show(ex.GetType().Name + ": " + ex.Message, "Cowpanion cannot start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _orchestrator?.Dispose();
        _orchestrator = null;
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            _mutex.Dispose();
            _mutex = null;
        }
        base.OnExit(e);
    }

    private bool TryAcquireMutex()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Another session owns the Global name with restrictive ACLs; treat as "already running".
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A background toy must not die on a stray exception; log and keep going. The chat host's own finally
        // paths and the 4 s watchdog make sure click-through is restored regardless of where this came from.
        AppLog.Info("unhandled: " + e.Exception);
        e.Handled = true;
    }
}

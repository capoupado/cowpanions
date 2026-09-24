using System.Windows.Threading;
using Velopack;

namespace Cowpanion.App.Updates;

internal enum UpdateStage
{
    /// <summary>Not a Velopack install (dev build, or the old portable single-file zip): updates are unavailable.</summary>
    NotInstalled,
    Idle,
    Checking,
    Downloading,
    UpToDate,
    /// <summary>Downloaded; applied by "Restart to update", or automatically on the next start.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Check → download → restart, through Velopack. The feed is a static folder on our own domain (nginx /updates/), so
/// the only extra network traffic is one small JSON request per check plus the package when there is one; the request
/// carries os/arch/app id/current version in its query string and nothing else (verified against 1.2.158). Downloads
/// are checked against the SHA in the feed by Velopack. The build is unsigned: integrity rests on HTTPS to our domain.
/// Everything runs on the UI thread; <see cref="Changed"/> is raised there.
/// </summary>
internal sealed class UpdateService
{
    public const string DefaultFeed = "https://cows.carlospoupado.com/updates";

    private readonly UpdateManager? _manager;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private VelopackAsset? _pending;

    public UpdateService(string feed, Dispatcher dispatcher, Action<string> log)
    {
        _dispatcher = dispatcher;
        _log = log;
        try
        {
            var manager = new UpdateManager(feed);
            if (manager.IsInstalled)
            {
                _manager = manager;
                _pending = manager.UpdatePendingRestart;
                Stage = _pending is null ? UpdateStage.Idle : UpdateStage.Ready;
                Detail = _pending?.Version.ToString() ?? "";
                log($"updates: installed build {manager.CurrentVersion}{(manager.IsPortable ? " (portable)" : "")}, feed {feed}");
                return;
            }
        }
        catch (Exception ex)
        {
            log("updates: unavailable (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
        Stage = UpdateStage.NotInstalled;
        log("updates: not an installed build; update checks are off");
    }

    public UpdateStage Stage { get; private set; }

    /// <summary>Version being downloaded / ready, or the failure reason.</summary>
    public string Detail { get; private set; } = "";

    public int Percent { get; private set; }

    public string CurrentVersion => _manager?.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";

    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading;

    public event Action? Changed;

    /// <summary>Checks and, if there is a newer release, downloads it. Never throws.</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || IsBusy || Stage == UpdateStage.Ready)
        {
            return;
        }
        Set(UpdateStage.Checking, "");
        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info is null)
            {
                Set(UpdateStage.UpToDate, "");
                _log("updates: up to date (" + CurrentVersion + ")");
                return;
            }
            string version = info.TargetFullRelease.Version.ToString();
            Percent = 0;
            Set(UpdateStage.Downloading, version);
            _log("updates: downloading " + version);
            await _manager.DownloadUpdatesAsync(info, p => _dispatcher.BeginInvoke(() =>
            {
                if (Stage == UpdateStage.Downloading && p != Percent)
                {
                    Percent = p;
                    Changed?.Invoke();
                }
            })).ConfigureAwait(true);
            _pending = info.TargetFullRelease;
            Set(UpdateStage.Ready, version);
            _log("updates: " + version + " downloaded, ready to apply");
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Failed, ex.GetType().Name + ": " + ex.Message);
            _log("updates: check failed: " + Detail);
        }
    }

    /// <summary>
    /// Hands the downloaded release to Velopack's updater, which waits for this process to exit, swaps the files and
    /// starts the new version. <paramref name="shutdown"/> must exit the app normally (sends bye, releases the mutex).
    /// </summary>
    public bool RestartToApply(Action shutdown)
    {
        if (_manager is null || _pending is null)
        {
            return false;
        }
        try
        {
            _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: true, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Failed, ex.GetType().Name + ": " + ex.Message);
            _log("updates: apply failed: " + Detail);
            return false;
        }
        _log("updates: restarting into " + _pending.Version);
        shutdown();
        return true;
    }

    private void Set(UpdateStage stage, string detail)
    {
        Stage = stage;
        Detail = detail;
        Changed?.Invoke();
    }
}

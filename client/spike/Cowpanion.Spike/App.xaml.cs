using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cowpanion.Spike;

/// <summary>
/// P0 feasibility spike. One transparent, topmost, click-through, no-activate strip at the bottom of the primary
/// monitor's work area, 260 DIPs tall, one rectangle sliding at 30 fps, CPU sampled every 5 s.
///
/// Flags: --exit-after N (seconds), --report (print average CPU at exit).
/// Kill hotkey: Ctrl+Alt+Shift+K, registered before the window exists.
/// </summary>
public partial class App : Application
{
    private const int HotkeyKill = 1;

    private HwndSource? _hotkeySource;
    private SpikeWindow? _window;
    private DispatcherTimer? _cpuTimer;
    private DispatcherTimer? _exitTimer;
    private TimeSpan _lastCpu;
    private DateTime _lastSample;
    private readonly Stopwatch _run = Stopwatch.StartNew();
    private TimeSpan _cpuAtStart;
    private bool _report;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int exitAfter = 0;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--exit-after" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                exitAfter = s;
            }
            if (e.Args[i] == "--report")
            {
                _report = true;
            }
        }

        // 1. Kill hotkey first, before any overlay window exists.
        RegisterKillHotkey();

        // 2. The strip.
        _window = new SpikeWindow();
        _window.Show();

        // 3. CPU sampling every 5 s.
        var proc = Process.GetCurrentProcess();
        _lastCpu = proc.TotalProcessorTime;
        _cpuAtStart = _lastCpu;
        _lastSample = DateTime.UtcNow;
        _cpuTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => SampleCpu(), Dispatcher);
        _cpuTimer.Start();

        Console.WriteLine($"spike: strip {_window.Width:F0}x{_window.Height:F0} DIPs, {Environment.ProcessorCount} logical CPUs, exit-after={exitAfter}s");

        if (exitAfter > 0)
        {
            _exitTimer = new DispatcherTimer(TimeSpan.FromSeconds(exitAfter), DispatcherPriority.Normal, (_, _) => Quit("exit-after"), Dispatcher);
            _exitTimer.Start();
        }
    }

    private void SampleCpu()
    {
        var proc = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpu = proc.TotalProcessorTime;
        double wall = (now - _lastSample).TotalSeconds;
        double pct = wall > 0 ? (cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100.0 : 0;
        _lastCpu = cpu;
        _lastSample = now;
        Console.WriteLine($"[{_run.Elapsed:mm\\:ss}] cpu {pct:F2}% of total, working set {proc.WorkingSet64 / (1024 * 1024)} MB, frames {_window?.Frames ?? 0}");
    }

    private void RegisterKillHotkey()
    {
        var parameters = new HwndSourceParameters("CowpanionSpikeHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
            ParentWindow = NativeMethods.HWND_MESSAGE,
        };
        _hotkeySource = new HwndSource(parameters);
        _hotkeySource.AddHook(HotkeyHook);
        bool ok = NativeMethods.RegisterHotKey(_hotkeySource.Handle, HotkeyKill, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, NativeMethods.VK_K);
        Console.WriteLine(ok ? "kill hotkey Ctrl+Alt+Shift+K registered" : "WARNING: kill hotkey registration failed");
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyKill)
        {
            handled = true;
            Quit("kill hotkey");
        }
        return IntPtr.Zero;
    }

    private void Quit(string reason)
    {
        if (_report)
        {
            var proc = Process.GetCurrentProcess();
            double total = (proc.TotalProcessorTime - _cpuAtStart).TotalSeconds / _run.Elapsed.TotalSeconds / Environment.ProcessorCount * 100.0;
            Console.WriteLine($"REPORT: average cpu {total:F2}% of total over {_run.Elapsed.TotalSeconds:F0}s ({reason}), peak working set {proc.PeakWorkingSet64 / (1024 * 1024)} MB");
        }
        if (_hotkeySource is not null)
        {
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, HotkeyKill);
            _hotkeySource.Dispose();
        }
        Shutdown();
    }
}

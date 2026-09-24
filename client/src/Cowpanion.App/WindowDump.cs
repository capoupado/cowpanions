using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cowpanion.App.History;
using Cowpanion.App.Overlay;
using Cowpanion.App.Settings;
using Cowpanion.Core.Configuration;
using Cowpanion.Net;

namespace Cowpanion.App;

/// <summary>
/// <c>--dump-windows DIR</c>: renders the settings window (one PNG per tab) and the chat history window with sample
/// entries into DIR, off-screen and never activated, then exits. Like --dump-emoji it runs before the mutex, so the
/// windows can be checked while the real client is up. Nothing is saved: the settings host is an in-memory fake.
/// </summary>
internal static class WindowDump
{
    public static int Run(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            using var emoji = new EmojiRasterizer();

            var history = new ChatHistory();
            var now = DateTimeOffset.Now;
            history.Add(new ChatMessage("b", "Ana", "morning all ☕", 0), false, now.AddMinutes(-12));
            history.Add(new ChatMessage("a", "Carlos", "gg", 0, "", "❤️"), true, now.AddMinutes(-9));
            history.Add(new ChatMessage("c", "Rui", "", 0, "jump"), false, now.AddMinutes(-7));
            history.Add(new ChatMessage("b", "Ana", "", 0, "", "🎉👍🏽🇵🇹"), false, now.AddMinutes(-3));
            history.Add(new ChatMessage("c", "Rui", "lunch? 🍕", 0, "moo"), false, now.AddMinutes(-1));
            var historyWindow = new HistoryWindow(history, emoji);
            Save(historyWindow, Path.Combine(dir, "history.png"));

            var host = new FakeHost();
            var settings = new SettingsWindow(host, ["black0", "brown", "white0"], ["cow"]);
            var tabs = FindTabControl(settings);
            int count = tabs?.Items.Count ?? 1;
            Show(settings);
            for (int i = 0; i < count; i++)
            {
                if (tabs is not null)
                {
                    tabs.SelectedIndex = i;
                }
                Capture(settings, Path.Combine(dir, $"settings-{i}.png"));
            }
            settings.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void Save(Window window, string path)
    {
        Show(window);
        Capture(window, path);
        window.Close();
    }

    private static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
    }

    private static void Capture(Window window, string path)
    {
        Pump();
        var content = (FrameworkElement)window.Content;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int w = (int)Math.Ceiling(content.ActualWidth * scale);
        int h = (int)Math.Ceiling(content.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
    }

    private static TabControl? FindTabControl(DependencyObject root)
    {
        if (root is TabControl tc)
        {
            return tc;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject d && FindTabControl(d) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private sealed class FakeHost : ISettingsHost
    {
        private readonly CowpanionConfig _config = new() { DisplayName = "Carlos", Variant = "brown", ClientId = new string('0', 32) };

        public CowpanionConfig CurrentConfig => _config.Clone();

        public string ConfigPath => "config.json";

        public string AppVersion => "0.1.0";

        public IReadOnlyDictionary<string, string> HotkeyErrors { get; } = new Dictionary<string, string> { ["heart"] = "in use by Windows or another app" };

        public IReadOnlyList<string> Apply(Action<CowpanionConfig> change) => [];

        public void SetHotkeysPaused(bool paused)
        {
        }

        public void OpenConfigFile()
        {
        }
    }
}

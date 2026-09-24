using System.Globalization;

namespace Cowpanion.App;

/// <summary>Command-line flags. All are development aids; a normal launch passes none.</summary>
internal sealed record StartupOptions
{
    /// <summary>--exit-after N: quit after N seconds (so automated launches never leave an overlay behind).</summary>
    public int ExitAfterSeconds { get; init; }

    /// <summary>--config PATH: use this config file instead of %APPDATA%\Cowpanion\config.json.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>--inject-chat-exception: the first Ctrl+Alt+C throws after dropping click-through (proves the finally path).</summary>
    public bool InjectChatException { get; init; }

    /// <summary>--no-dialog: never show the first-run name dialog (implied by --exit-after).</summary>
    public bool NoDialog { get; init; }

    /// <summary>--dump-emoji PATH: render a sample emoji string through EmojiRasterizer to a PNG at PATH and exit (no windows, no mutex).</summary>
    public string? DumpEmojiPath { get; init; }

    /// <summary>--dump-windows DIR: render the settings and chat history windows to PNGs in DIR and exit (no mutex, nothing saved).</summary>
    public string? DumpWindowsDir { get; init; }

    /// <summary>--update-feed URL|DIR: use this Velopack feed instead of the production one (release testing).</summary>
    public string? UpdateFeed { get; init; }

    /// <summary>--update-now: check the feed, download and apply an update with no UI, then exit (before the mutex; release testing).</summary>
    public bool UpdateNow { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        int exitAfter = 0;
        string? config = null;
        bool inject = false;
        bool noDialog = false;
        string? dumpEmoji = null;
        string? dumpWindows = null;
        string? updateFeed = null;
        bool updateNow = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exit-after" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s):
                    exitAfter = Math.Max(1, s);
                    i++;
                    break;
                case "--config" when i + 1 < args.Length:
                    config = args[i + 1];
                    i++;
                    break;
                case "--inject-chat-exception":
                    inject = true;
                    break;
                case "--no-dialog":
                    noDialog = true;
                    break;
                case "--dump-emoji" when i + 1 < args.Length:
                    dumpEmoji = args[i + 1];
                    i++;
                    break;
                case "--update-now":
                    updateNow = true;
                    break;
                case "--update-feed" when i + 1 < args.Length:
                    updateFeed = args[i + 1];
                    i++;
                    break;
                case "--dump-windows" when i + 1 < args.Length:
                    dumpWindows = args[i + 1];
                    i++;
                    break;
            }
        }
        return new StartupOptions
        {
            ExitAfterSeconds = exitAfter,
            ConfigPath = config,
            InjectChatException = inject,
            NoDialog = noDialog || exitAfter > 0,
            DumpEmojiPath = dumpEmoji,
            DumpWindowsDir = dumpWindows,
            UpdateFeed = updateFeed,
            UpdateNow = updateNow,
        };
    }
}

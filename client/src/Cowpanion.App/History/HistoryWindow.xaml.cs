using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cowpanion.App.Overlay;
using Cowpanion.Net;

namespace Cowpanion.App.History;

/// <summary>
/// Shows the in-memory <see cref="ChatHistory"/> and follows it live. Emoji go through the Direct2D
/// <see cref="EmojiRasterizer"/> as inline images, because a WPF TextBlock draws them as black outlines; if the
/// rasteriser is unavailable they fall back to plain text.
/// </summary>
public partial class HistoryWindow : Window
{
    private const double EmojiDips = 16;
    private static readonly Brush TimeBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush SelfBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5A, 0x2B));

    private readonly ChatHistory _history;
    private readonly EmojiRasterizer _emoji;
    private double _dpiScale = 1.0;

    internal HistoryWindow(ChatHistory history, EmojiRasterizer emoji)
    {
        _history = history;
        _emoji = emoji;
        InitializeComponent();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection()));
        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Rebuild();
        };
        DpiChanged += (_, e) =>
        {
            // DpiChanged is routed: the inline emoji Images raise it during their own measure and it bubbles up here.
            // Only the window's own change counts, and the rebuild waits until layout is done.
            if (!ReferenceEquals(e.OriginalSource, this) || e.NewDpi.DpiScaleX == _dpiScale)
            {
                return;
            }
            _dpiScale = e.NewDpi.DpiScaleX;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Rebuild));
        };
        _history.Added += OnAdded;
        _history.Cleared += Rebuild;
        Closed += (_, _) =>
        {
            _history.Added -= OnAdded;
            _history.Cleared -= Rebuild;
        };
    }

    private void Rebuild()
    {
        List.Items.Clear();
        foreach (var entry in _history.Snapshot())
        {
            List.Items.Add(BuildItem(entry));
        }
        UpdateChrome();
        ScrollToNewest();
    }

    private void OnAdded(ChatHistoryEntry entry)
    {
        if (!IsLoaded)
        {
            return; // Loaded rebuilds from the snapshot
        }
        bool follow = List.SelectedItems.Count == 0;
        List.Items.Add(BuildItem(entry));
        while (List.Items.Count > _history.Capacity)
        {
            List.Items.RemoveAt(0);
        }
        UpdateChrome();
        if (follow)
        {
            ScrollToNewest();
        }
    }

    private void UpdateChrome()
    {
        int n = List.Items.Count;
        EmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " entry" : " entries") + " · memory only";
    }

    private void ScrollToNewest()
    {
        if (List.Items.Count > 0)
        {
            List.ScrollIntoView(List.Items[^1]);
        }
    }

    private ListBoxItem BuildItem(ChatHistoryEntry e)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(new Run(e.ReceivedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "  ") { Foreground = TimeBrush });
        var name = new Bold(new Run(e.Name));
        if (e.IsSelf)
        {
            name.Foreground = SelfBrush;
        }
        block.Inlines.Add(name);
        AppendWithEmoji(block.Inlines, ChatHistory.DescribeBody(e));
        return new ListBoxItem { Content = block, Tag = e, Padding = new Thickness(4, 3, 4, 3) };
    }

    /// <summary>Plain runs for text, inline colour bitmaps for emoji clusters.</summary>
    private void AppendWithEmoji(InlineCollection inlines, string text)
    {
        var plain = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string cluster = e.GetTextElement();
            BitmapSource? bitmap = ChatComposer.IsEmojiCluster(cluster) ? _emoji.Render(cluster, EmojiDips, _dpiScale) : null;
            if (bitmap is null)
            {
                plain.Append(cluster);
                continue;
            }
            if (plain.Length > 0)
            {
                inlines.Add(new Run(plain.ToString()));
                plain.Clear();
            }
            var image = new Image
            {
                Source = bitmap,
                Width = bitmap.PixelWidth / _dpiScale,
                Height = bitmap.PixelHeight / _dpiScale,
                Stretch = Stretch.Fill,
                Margin = new Thickness(1, 0, 1, 0),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }
        if (plain.Length > 0)
        {
            inlines.Add(new Run(plain.ToString()));
        }
    }

    private void CopySelection()
    {
        var entries = List.SelectedItems.Count > 0
            ? List.Items.Cast<ListBoxItem>().Where(i => i.IsSelected)
            : List.Items.Cast<ListBoxItem>();
        string text = string.Join(Environment.NewLine, entries.Select(i => ChatHistory.FormatPlain((ChatHistoryEntry)i.Tag)));
        if (text.Length == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Info("history copy failed: " + ex.Message); // clipboard held by another app
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopySelection();

    private void OnClear(object sender, RoutedEventArgs e) => _history.Clear();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

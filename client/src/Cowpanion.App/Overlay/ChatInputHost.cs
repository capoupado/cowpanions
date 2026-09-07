using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cowpanion.App.Interop;
using Cowpanion.Core.Simulation;
using Cowpanion.Net;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Interaction mode. Arming drops WS_EX_TRANSPARENT (and NOACTIVATE, so the TextBox can take keystrokes), shows the
/// input near the user's own cow plus a visible indicator. Enter sends, Esc cancels, 8 s idle auto-disarms,
/// clicking elsewhere disarms. Every path that leaves the armed state goes through <see cref="Disarm"/>, and
/// <see cref="Arm"/> itself is wrapped so an exception while arming restores click-through before it propagates
/// anywhere. The Orchestrator additionally re-checks click-through on its 4 s timer as a belt-and-braces watchdog.
/// <para>
/// The panel also shows a colour preview of the emoji that will be sent (WPF's TextBox draws them as black outlines)
/// and a button that opens the Windows emoji panel (Win+.). That panel is a separate window, so it deactivates ours;
/// for a bounded grace after the button is clicked, <c>Deactivated</c> does not disarm and idle does not count.
/// </para>
/// </summary>
internal sealed class ChatInputHost
{
    private const double IdleDisarmSeconds = 8.0;
    /// <summary>How long after clicking the emoji button a deactivation is attributed to the emoji panel.</summary>
    private const double EmojiPanelGraceSeconds = 15.0;
    private const double PreviewDips = 20.0;
    private const int MaxPreviewClusters = 6;
    private const string EmojiButtonGlyph = "😊";

    private readonly OverlayWindow _window;
    private readonly HerdRenderer _herd;
    private readonly Func<HerdSimulator> _sim;
    private readonly Func<string, string, string, Task<bool>> _send;
    private readonly Action<string> _log;
    private readonly EmojiRasterizer? _emoji;
    private readonly double _dpiScale;
    private readonly Image[] _previewImages = new Image[MaxPreviewClusters + 1];
    private readonly TextBlock _previewEllipsis;
    private readonly List<string> _previewClusters = new(MaxPreviewClusters + 1);
    private double _idle;
    private bool _armed;
    private bool _injectExceptionOnce;
    private bool _emojiPanelExpected;
    private double _emojiPanelGraceLeft;

    public ChatInputHost(OverlayWindow window, HerdRenderer herd, Func<HerdSimulator> sim, Func<string, string, string, Task<bool>> send, Action<string> log, EmojiRasterizer? emoji, double dpiScale)
    {
        _window = window;
        _herd = herd;
        _sim = sim;
        _send = send;
        _log = log;
        _emoji = emoji;
        _dpiScale = dpiScale > 0 ? dpiScale : 1.0;

        // Preview pool: MaxPreviewClusters images plus one spare, then an ellipsis; all created once and toggled.
        for (int i = 0; i < _previewImages.Length; i++)
        {
            var image = new Image
            {
                Stretch = Stretch.Fill,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _previewImages[i] = image;
            _window.EmojiPreview.Children.Add(image);
        }
        _previewEllipsis = new TextBlock
        {
            Text = "…",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x4A)),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _window.EmojiPreview.Children.Add(_previewEllipsis);
        SetupEmojiButton();

        _window.ChatBox.PreviewKeyDown += OnKeyDown;
        _window.ChatBox.TextChanged += OnTextChanged;
        _window.Deactivated += OnDeactivated;
        _window.Activated += OnActivated;
        _window.PreviewMouseDown += OnMouseDown;
        _window.EmojiButton.Click += OnEmojiButtonClick;
    }

    public bool IsArmed => _armed;

    public event Action? StateChanged;

    /// <summary>Dev flag: the next Arm throws after dropping click-through, to prove the finally path restores it.</summary>
    public void InjectExceptionOnNextArm()
    {
        _injectExceptionOnce = true;
    }

    public void Arm()
    {
        if (_armed)
        {
            _idle = 0;
            _window.ChatBox.Focus();
            return;
        }
        bool ok = false;
        try
        {
            _armed = true;
            _idle = 0;
            ClearEmojiPanelGrace(null);
            OverlayWindowStyler.SetInteractive(_window.Handle, true);
            _window.ModeIndicator.Visibility = Visibility.Visible;
            _window.ModeLabel.Visibility = Visibility.Visible;
            _window.ChatPanel.Visibility = Visibility.Visible;
            _window.ChatBox.Text = "";
            UpdatePreview("");
            PositionPanel();
            if (_injectExceptionOnce)
            {
                _injectExceptionOnce = false;
                throw new InvalidOperationException("injected chat-mode exception (dev flag --inject-chat-exception)");
            }
            NativeMethods.SetForegroundWindow(_window.Handle);
            _window.Activate();
            _window.ChatBox.Focus();
            Keyboard.Focus(_window.ChatBox);
            ok = true;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log("chat arm failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (!ok)
            {
                Disarm("arm failed");
            }
        }
    }

    public void Disarm(string reason)
    {
        bool wasArmed = _armed;
        _armed = false;
        _emojiPanelExpected = false;
        _emojiPanelGraceLeft = 0;
        try
        {
            _window.ChatPanel.Visibility = Visibility.Collapsed;
            _window.ModeIndicator.Visibility = Visibility.Collapsed;
            _window.ModeLabel.Visibility = Visibility.Collapsed;
            _window.ChatBox.Text = "";
        }
        finally
        {
            // Unconditional: this is the line that must always run.
            OverlayWindowStyler.SetInteractive(_window.Handle, false);
        }
        if (wasArmed)
        {
            _log("chat mode disarmed (" + reason + ")");
            StateChanged?.Invoke();
        }
    }

    /// <summary>Per tick: follow the own cow and count idle time (paused while the emoji panel is expected to be open).</summary>
    public void Tick(double dt)
    {
        if (!_armed)
        {
            return;
        }
        if (_emojiPanelExpected)
        {
            _emojiPanelGraceLeft -= dt;
            _idle = 0;
            if (_emojiPanelGraceLeft <= 0)
            {
                ClearEmojiPanelGrace("grace expired");
            }
        }
        else
        {
            _idle += dt;
            if (_idle >= IdleDisarmSeconds)
            {
                Disarm("8 s idle");
                return;
            }
        }
        PositionPanel();
    }

    /// <summary>Safety net for the 4 s watchdog: if we think we are disarmed, the window must be click-through.</summary>
    public void EnsureClickThroughIfDisarmed()
    {
        if (!_armed && _window.Handle != IntPtr.Zero && !OverlayWindowStyler.IsClickThrough(_window.Handle))
        {
            _log("watchdog: click-through was off while disarmed — restoring");
            OverlayWindowStyler.SetInteractive(_window.Handle, false);
        }
    }

    private void PositionPanel()
    {
        var sim = _sim();
        var self = sim.FindSelf();
        double w = _window.ChatPanel.Width;
        double x;
        double y;
        if (self is not null)
        {
            var rect = _herd.CowRect(self);
            x = rect.X + rect.Width / 2 - w / 2;
            y = rect.Y - 44;
        }
        else
        {
            x = _window.StripWidthDips / 2 - w / 2;
            y = OverlayWindow.StripHeightDips - _herd.CowHeightDips - 44;
        }
        x = Math.Clamp(x, 4, Math.Max(4, _window.StripWidthDips - w - 4));
        y = Math.Clamp(y, 4, OverlayWindow.StripHeightDips - 40);
        Canvas.SetLeft(_window.ChatPanel, Math.Round(x));
        Canvas.SetTop(_window.ChatPanel, Math.Round(y));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_armed)
        {
            return;
        }
        _idle = 0;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Disarm("escape");
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string text = _window.ChatBox.Text;
            Disarm("enter");
            // /moo /jump /spin → emote; /heart etc. and emoji-only lines → reaction; anything else → text.
            var draft = ChatComposer.Parse(text);
            if (!draft.IsEmpty)
            {
                // Own bubble/emote/reaction arrives via the server echo; nothing is rendered optimistically.
                _ = SendSafelyAsync(draft);
            }
        }
    }

    private async Task SendSafelyAsync(ChatDraft draft)
    {
        try
        {
            bool sent = await _send(draft.Text, draft.Emote, draft.Reaction);
            if (!sent)
            {
                _log("chat not sent: not connected");
            }
        }
        catch (Exception ex)
        {
            _log("chat send failed: " + ex.GetType().Name);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_armed)
        {
            return;
        }
        _idle = 0;
        var pos = e.GetPosition(_window.Root);
        var panelPos = e.GetPosition(_window.ChatPanel);
        // The emoji button and the preview live inside ChatPanel, so clicks on them are exempt from "click elsewhere".
        bool onPanel = panelPos.X >= 0 && panelPos.Y >= 0 && panelPos.X <= _window.ChatPanel.ActualWidth && panelPos.Y <= _window.ChatPanel.ActualHeight;
        if (onPanel)
        {
            return;
        }
        var cow = _herd.HitTest(pos, _sim());
        if (cow is not null && cow.IsSelf)
        {
            _window.ChatBox.Focus();
            e.Handled = true;
            return;
        }
        Disarm("click elsewhere");
    }

    // ---- Emoji panel (Win+.) ----

    private void OnEmojiButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_armed)
        {
            return;
        }
        _idle = 0;
        try
        {
            // The emoji panel inserts into whatever has keyboard focus; make sure that is the chat box.
            _window.ChatBox.Focus();
            Keyboard.Focus(_window.ChatBox);
            _emojiPanelExpected = true;
            _emojiPanelGraceLeft = EmojiPanelGraceSeconds;
            uint injected = NativeMethods.SendWinPeriod();
            _log($"emoji button: sent Win+. ({injected}/4 events); deactivation grace {EmojiPanelGraceSeconds:F0} s");
        }
        catch (Exception ex)
        {
            ClearEmojiPanelGrace(null);
            _log("emoji button failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_armed && _emojiPanelExpected)
        {
            _log("window deactivated while the emoji panel is expected — staying armed");
            return;
        }
        Disarm("window deactivated");
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (_armed)
        {
            ClearEmojiPanelGrace("window re-activated");
        }
    }

    private void ClearEmojiPanelGrace(string? reason)
    {
        if (_emojiPanelExpected && reason is not null)
        {
            _log("emoji panel grace cleared (" + reason + ")");
        }
        _emojiPanelExpected = false;
        _emojiPanelGraceLeft = 0;
    }

    private void SetupEmojiButton()
    {
        BitmapSource? bitmap = _emoji?.Render(EmojiButtonGlyph, 18, _dpiScale);
        if (bitmap is null)
        {
            return; // keep the XAML TextBlock fallback
        }
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            Width = bitmap.PixelWidth / _dpiScale,
            Height = bitmap.PixelHeight / _dpiScale,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _window.EmojiButton.Content = image;
    }

    // ---- Colour preview ----

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _idle = 0;
        // Text arrived (typed or inserted by the emoji panel): the panel is no longer the reason we might be deactivated.
        if (_emojiPanelExpected)
        {
            ClearEmojiPanelGrace("text changed");
        }
        UpdatePreview(_window.ChatBox.Text);
    }

    /// <summary>
    /// Shows what the draft will send: the reaction's clusters, or the emoji embedded in a text/emote line (so people
    /// can see what the black outlines in the TextBox are), plus a caption naming the mode.
    /// </summary>
    private void UpdatePreview(string text)
    {
        var draft = ChatComposer.Parse(text);
        _previewClusters.Clear();
        string caption = "";
        bool truncated = false;
        if (draft.Reaction.Length > 0)
        {
            caption = "reaction";
            foreach (string cluster in ChatComposer.Graphemes(draft.Reaction))
            {
                _previewClusters.Add(cluster);
            }
        }
        else
        {
            if (draft.Emote.Length > 0)
            {
                caption = "emote: " + draft.Emote;
            }
            truncated = CollectEmojiClusters(draft.Text, _previewClusters);
        }

        bool anyImage = false;
        for (int i = 0; i < _previewImages.Length; i++)
        {
            var image = _previewImages[i];
            if (i < _previewClusters.Count && i < MaxPreviewClusters)
            {
                BitmapSource? bitmap = _emoji?.Render(_previewClusters[i], PreviewDips, _dpiScale);
                if (bitmap is not null)
                {
                    image.Source = bitmap;
                    image.Width = bitmap.PixelWidth / _dpiScale;
                    image.Height = bitmap.PixelHeight / _dpiScale;
                    image.Visibility = Visibility.Visible;
                    anyImage = true;
                    continue;
                }
            }
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }
        _previewEllipsis.Visibility = anyImage && truncated ? Visibility.Visible : Visibility.Collapsed;
        _window.PreviewCaption.Text = caption;
        _window.PreviewCaption.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _window.PreviewArea.Visibility = anyImage || caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Appends up to <see cref="MaxPreviewClusters"/> emoji clusters of <paramref name="text"/>; true if more were cut.</summary>
    private static bool CollectEmojiClusters(string text, List<string> into)
    {
        if (text.Length == 0)
        {
            return false;
        }
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string cluster = e.GetTextElement();
            if (!ChatComposer.IsEmojiCluster(cluster))
            {
                continue;
            }
            if (into.Count >= MaxPreviewClusters)
            {
                return true;
            }
            into.Add(cluster);
        }
        return false;
    }
}

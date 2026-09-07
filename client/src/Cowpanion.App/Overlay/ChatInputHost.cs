using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// </summary>
internal sealed class ChatInputHost
{
    private const double IdleDisarmSeconds = 8.0;

    private readonly OverlayWindow _window;
    private readonly HerdRenderer _herd;
    private readonly Func<HerdSimulator> _sim;
    private readonly Func<string, string, string, Task<bool>> _send;
    private readonly Action<string> _log;
    private double _idle;
    private bool _armed;
    private bool _injectExceptionOnce;

    public ChatInputHost(OverlayWindow window, HerdRenderer herd, Func<HerdSimulator> sim, Func<string, string, string, Task<bool>> send, Action<string> log)
    {
        _window = window;
        _herd = herd;
        _sim = sim;
        _send = send;
        _log = log;

        _window.ChatBox.PreviewKeyDown += OnKeyDown;
        _window.ChatBox.TextChanged += (_, _) => _idle = 0;
        _window.Deactivated += (_, _) => Disarm("window deactivated");
        _window.PreviewMouseDown += OnMouseDown;
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
            OverlayWindowStyler.SetInteractive(_window.Handle, true);
            _window.ModeIndicator.Visibility = Visibility.Visible;
            _window.ModeLabel.Visibility = Visibility.Visible;
            _window.ChatPanel.Visibility = Visibility.Visible;
            _window.ChatBox.Text = "";
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

    /// <summary>Per tick: follow the own cow and count idle time.</summary>
    public void Tick(double dt)
    {
        if (!_armed)
        {
            return;
        }
        _idle += dt;
        if (_idle >= IdleDisarmSeconds)
        {
            Disarm("8 s idle");
            return;
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
}

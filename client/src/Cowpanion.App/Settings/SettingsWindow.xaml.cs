using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cowpanion.Core.Configuration;

namespace Cowpanion.App.Settings;

/// <summary>
/// Everything in config.json except clientId, as a normal focusable window opened from the tray (like the first-run
/// name dialog, it is not the overlay, so the focus rules do not apply to it). Apply/OK validate, then hand the values
/// to <see cref="ISettingsHost.Apply"/>, the same path the tray menu uses; the config watcher ignores that write.
/// Changes made elsewhere while the window is open are not pulled in, but Apply only overwrites the fields shown here.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x20, 0x20));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    private readonly ISettingsHost _host;
    private readonly List<HotkeyRow> _rows = new();

    internal SettingsWindow(ISettingsHost host, IReadOnlyList<string> variants, IReadOnlyList<string> spritePacks)
    {
        _host = host;
        InitializeComponent();

        var config = host.CurrentConfig;
        foreach (string v in variants)
        {
            VariantBox.Items.Add(v);
        }
        foreach (string p in spritePacks)
        {
            SpritePackBox.Items.Add(p);
        }
        for (int i = 1; i <= 6; i++)
        {
            ScaleBox.Items.Add(new ComboBoxItem { Content = i.ToString(CultureInfo.InvariantCulture) + "×", Tag = i });
        }
        MonitorsBox.Items.Add(new ComboBoxItem { Content = "Primary monitor only", Tag = "primary" });
        MonitorsBox.Items.Add(new ComboBoxItem { Content = "All monitors", Tag = "all" });

        AddRow("kill", "Quit", HotkeyBindings.DefaultKill, allowEmpty: false);
        AddRow("chat", "Chat", HotkeyBindings.DefaultChat, allowEmpty: true);
        AddRow("mute", "Mute bubbles", HotkeyBindings.DefaultMute, allowEmpty: true);
        AddRow("heart", "Send a heart", HotkeyBindings.DefaultHeart, allowEmpty: true);
        AddRow("focusMode", "Toggle focus mode", HotkeyBindings.DefaultFocusMode, allowEmpty: true);

        LoadFrom(config);
        ShowHotkeyErrors();
        VersionText.Text = "Version " + host.AppVersion + ". " + VersionText.Text + " Tray → Check for updates checks now.";
        Loaded += (_, _) => DisplayNameBox.Focus();
    }

    private void LoadFrom(CowpanionConfig c)
    {
        DisplayNameBox.Text = c.DisplayName;
        SelectText(VariantBox, c.Variant);
        SleepBox.Text = c.SleepAfterIdleMinutes.ToString(CultureInfo.InvariantCulture);
        StartWithWindowsBox.IsChecked = c.StartWithWindows;
        PauseOnFullscreenBox.IsChecked = c.PauseOnFullscreen;
        MooBox.IsChecked = c.MooEnabled;
        AutoUpdateBox.IsChecked = c.AutoCheckForUpdates;

        MultiplayerBox.IsChecked = c.MultiplayerEnabled;
        PastureBox.Text = c.Pasture;
        BubblesEnabledBox.IsChecked = c.BubblesEnabled;
        BubblesMutedBox.IsChecked = c.BubblesMuted;
        HerdSizeBox.Text = c.OfflineHerdSize.ToString(CultureInfo.InvariantCulture);
        ServerUrlBox.Text = c.ServerUrl;

        SelectTag(ScaleBox, c.Scale);
        SelectTag(MonitorsBox, c.Monitors);
        ActiveFpsBox.Text = c.ActiveFps.ToString(CultureInfo.InvariantCulture);
        IdleFpsBox.Text = c.IdleFps.ToString(CultureInfo.InvariantCulture);
        SelectText(SpritePackBox, c.SpritePack);

        _rows[0].Set(c.Hotkeys.Kill);
        _rows[1].Set(c.Hotkeys.Chat);
        _rows[2].Set(c.Hotkeys.Mute);
        _rows[3].Set(c.Hotkeys.Heart);
        _rows[4].Set(c.Hotkeys.FocusMode);
        FocusModeBox.IsChecked = c.FocusMode;
    }

    /// <summary>Validates and applies. Returns false (and shows why) when a field is wrong; nothing is saved then.</summary>
    private bool TryApply()
    {
        var errors = new List<string>();
        string name = ConfigValidator.SanitizeDisplayName(DisplayNameBox.Text);
        if (name.Length == 0)
        {
            errors.Add("Your name cannot be empty.");
        }
        int sleep = ParseInt(SleepBox.Text, "Sleep after idle", errors);
        int herd = ParseInt(HerdSizeBox.Text, "Offline herd size", errors);
        int activeFps = ParseInt(ActiveFpsBox.Text, "Frame rate while moving", errors);
        int idleFps = ParseInt(IdleFpsBox.Text, "Frame rate while still", errors);
        string pasture = PastureBox.Text.Trim().ToLowerInvariant();
        if (!ConfigValidator.IsValidPasture(pasture))
        {
            errors.Add("Pasture code: 1–32 letters, digits or dashes.");
        }
        string serverUrl = ServerUrlBox.Text.Trim();
        if (!ConfigValidator.IsValidServerUrl(serverUrl))
        {
            errors.Add("Server URL must start with wss:// (or ws://).");
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            row.ClearStatus();
            if (row.Value.Length == 0)
            {
                continue;
            }
            if (seen.TryGetValue(row.Value, out string? other))
            {
                row.ShowStatus("also used by " + other, ErrorBrush);
                errors.Add($"{row.Value} is bound to both {other} and {row.Label}.");
                continue;
            }
            seen[row.Value] = row.Label;
        }

        if (errors.Count > 0)
        {
            ShowMessage(string.Join(Environment.NewLine, errors), ErrorBrush);
            return false;
        }

        string variant = VariantBox.SelectedItem as string ?? "";
        string spritePack = SpritePackBox.SelectedItem as string ?? "";
        int scale = (ScaleBox.SelectedItem as ComboBoxItem)?.Tag as int? ?? 3;
        string monitors = (MonitorsBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "primary";
        string[] hotkeys = _rows.Select(r => r.Value).ToArray();

        var warnings = _host.Apply(c =>
        {
            c.DisplayName = name;
            if (variant.Length > 0)
            {
                c.Variant = variant;
            }
            c.SleepAfterIdleMinutes = sleep;
            c.StartWithWindows = StartWithWindowsBox.IsChecked == true;
            c.PauseOnFullscreen = PauseOnFullscreenBox.IsChecked == true;
            c.MooEnabled = MooBox.IsChecked == true;
            c.AutoCheckForUpdates = AutoUpdateBox.IsChecked == true;
            c.MultiplayerEnabled = MultiplayerBox.IsChecked == true;
            c.Pasture = pasture;
            c.BubblesEnabled = BubblesEnabledBox.IsChecked == true;
            c.BubblesMuted = BubblesMutedBox.IsChecked == true;
            c.OfflineHerdSize = herd;
            c.ServerUrl = serverUrl;
            c.Scale = scale;
            c.Monitors = monitors;
            c.ActiveFps = activeFps;
            c.IdleFps = idleFps;
            if (spritePack.Length > 0)
            {
                c.SpritePack = spritePack;
            }
            c.Hotkeys.Kill = hotkeys[0];
            c.Hotkeys.Chat = hotkeys[1];
            c.Hotkeys.Mute = hotkeys[2];
            c.Hotkeys.Heart = hotkeys[3];
            c.Hotkeys.FocusMode = hotkeys[4];
            c.FocusMode = FocusModeBox.IsChecked == true;
        });

        // Show what the clamp did (fps ranges, ...) and what could not be registered.
        LoadFrom(_host.CurrentConfig);
        bool hotkeyTrouble = ShowHotkeyErrors();
        if (warnings.Count > 0)
        {
            ShowMessage("Saved, with adjustments: " + string.Join("; ", warnings), InfoBrush);
        }
        else if (hotkeyTrouble)
        {
            ShowMessage("Saved, but some hotkeys could not be registered — see the Hotkeys tab.", ErrorBrush);
        }
        else
        {
            ShowMessage("Saved.", InfoBrush);
        }
        return !hotkeyTrouble;
    }

    private bool ShowHotkeyErrors()
    {
        bool any = false;
        foreach (var row in _rows)
        {
            if (_host.HotkeyErrors.TryGetValue(row.Action, out string? error))
            {
                row.ShowStatus(error, ErrorBrush);
                any = true;
            }
        }
        return any;
    }

    private void ShowMessage(string text, Brush brush)
    {
        MessageText.Text = text;
        MessageText.Foreground = brush;
        MessageText.Visibility = Visibility.Visible;
    }

    private static int ParseInt(string text, string field, List<string> errors)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }
        errors.Add(field + " must be a whole number.");
        return 0;
    }

    private static void SelectText(ComboBox box, string value)
    {
        if (value.Length > 0 && !box.Items.Contains(value))
        {
            box.Items.Add(value);
        }
        box.SelectedItem = value.Length > 0 ? value : null;
    }

    private static void SelectTag(ComboBox box, object value)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem cbi && Equals(cbi.Tag, value))
            {
                box.SelectedItem = cbi;
                return;
            }
        }
    }

    private void AddRow(string action, string label, string defaultValue, bool allowEmpty)
    {
        int r = HotkeyGrid.RowDefinitions.Count;
        HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = new HotkeyRow(this, action, label, defaultValue, allowEmpty);
        Place(row.LabelBlock, r, 0);
        Place(row.Box, r, 1);
        Place(row.ClearButton, r, 2);
        Place(row.Status, r, 3);
        _rows.Add(row);
    }

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        HotkeyGrid.Children.Add(element);
    }

    private void OnRestoreHotkeys(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.Set(row.Default);
            row.ClearStatus();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryApply())
        {
            Close();
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => TryApply();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnOpenConfigFile(object sender, RoutedEventArgs e) => _host.OpenConfigFile();

    /// <summary>
    /// One rebindable action: a read-only box that records the next combination pressed while it has focus. The host
    /// releases every global hotkey while any box has focus (otherwise RegisterHotKey would swallow the combination).
    /// </summary>
    private sealed class HotkeyRow
    {
        private const string Prompt = "Press a combination…";

        private readonly SettingsWindow _owner;
        private readonly bool _allowEmpty;

        public HotkeyRow(SettingsWindow owner, string action, string label, string defaultValue, bool allowEmpty)
        {
            _owner = owner;
            _allowEmpty = allowEmpty;
            Action = action;
            Label = label;
            Default = defaultValue;
            LabelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand };
            ClearButton = new Button
            {
                Content = allowEmpty ? "Unbind" : "Default",
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(6, 4, 0, 4),
                ToolTip = allowEmpty ? "No hotkey for this action" : "Quit always has a hotkey; this restores " + defaultValue,
            };
            Status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };

            Box.GotKeyboardFocus += (_, _) =>
            {
                _owner._host.SetHotkeysPaused(true);
                Box.Text = Prompt;
            };
            Box.LostKeyboardFocus += (_, _) =>
            {
                _owner._host.SetHotkeysPaused(false);
                Box.Text = Display;
            };
            Box.PreviewKeyDown += OnKeyDown;
            Box.PreviewKeyUp += (_, e) =>
            {
                if (Box.IsKeyboardFocused && Box.Text != Display)
                {
                    Box.Text = Keyboard.Modifiers == ModifierKeys.None ? Prompt : ModifierText(Keyboard.Modifiers) + "…";
                }
                e.Handled = true;
            };
            ClearButton.Click += (_, _) =>
            {
                Set(_allowEmpty ? "" : Default);
                ClearStatus();
            };
        }

        public string Action { get; }

        public string Label { get; }

        public string Default { get; }

        public string Value { get; private set; } = "";

        public TextBlock LabelBlock { get; }

        public TextBox Box { get; }

        public Button ClearButton { get; }

        public TextBlock Status { get; }

        private string Display => Value.Length > 0 ? Value : "(none)";

        public void Set(string value)
        {
            Value = value;
            Box.Text = Box.IsKeyboardFocused ? Prompt : Display;
        }

        public void ShowStatus(string text, Brush brush)
        {
            Status.Text = text;
            Status.Foreground = brush;
        }

        public void ClearStatus() => Status.Text = "";

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys mods = Keyboard.Modifiers;
            if (key == Key.Tab && (mods & ~ModifierKeys.Shift) == ModifierKeys.None)
            {
                return; // keep keyboard navigation working
            }
            e.Handled = true;
            switch (key)
            {
                case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                    Box.Text = ModifierText(mods) + "…";
                    return;
                case Key.Escape when mods == ModifierKeys.None:
                    Box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    return;
                case Key.Back or Key.Delete when mods == ModifierKeys.None:
                    Set(_allowEmpty ? "" : Default);
                    ClearStatus();
                    return;
                case Key.ImeProcessed or Key.DeadCharProcessed or Key.None:
                    return;
            }

            var hm = HotkeyModifiers.None;
            if (mods.HasFlag(ModifierKeys.Control))
            {
                hm |= HotkeyModifiers.Ctrl;
            }
            if (mods.HasFlag(ModifierKeys.Alt))
            {
                hm |= HotkeyModifiers.Alt;
            }
            if (mods.HasFlag(ModifierKeys.Shift))
            {
                hm |= HotkeyModifiers.Shift;
            }
            if (mods.HasFlag(ModifierKeys.Windows))
            {
                hm |= HotkeyModifiers.Win;
            }
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (Hotkey.TryCreate(hm, vk, out var hotkey, out string error))
            {
                Value = hotkey.ToString();
                Box.Text = Value;
                ClearStatus();
            }
            else
            {
                Box.Text = Prompt;
                ShowStatus(error, ErrorBrush);
            }
        }

        private static string ModifierText(ModifierKeys mods)
        {
            var parts = new List<string>(4);
            if (mods.HasFlag(ModifierKeys.Control))
            {
                parts.Add("Ctrl");
            }
            if (mods.HasFlag(ModifierKeys.Alt))
            {
                parts.Add("Alt");
            }
            if (mods.HasFlag(ModifierKeys.Shift))
            {
                parts.Add("Shift");
            }
            if (mods.HasFlag(ModifierKeys.Windows))
            {
                parts.Add("Win");
            }
            return string.Join("+", parts) + (parts.Count > 0 ? "+" : "");
        }
    }
}

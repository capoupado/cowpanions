using System.Text.Json;
using Cowpanion.Core.Configuration;

namespace Cowpanion.Core.Tests;

public class HotkeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cowpanion-tests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("Ctrl+Alt+C", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x43u, "Ctrl+Alt+C")]
    [InlineData(" control + shift + alt + k ", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4Bu, "Ctrl+Alt+Shift+K")]
    [InlineData("Win+F9", HotkeyModifiers.Win, 0x78u, "Win+F9")]
    [InlineData("F12", HotkeyModifiers.None, 0x7Bu, "F12")]
    [InlineData("Shift+Pause", HotkeyModifiers.Shift, 0x13u, "Shift+Pause")]
    [InlineData("Ctrl+Alt+Minus", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0xBDu, "Ctrl+Alt+Minus")]
    [InlineData("alt+num5", HotkeyModifiers.Alt, 0x65u, "Alt+Num5")]
    public void Parses_and_canonicalises(string text, HotkeyModifiers mods, uint vk, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out var hk, out string error), error);
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(vk, hk.VirtualKey);
        Assert.Equal(canonical, hk.ToString());
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again, out _));
        Assert.Equal(hk, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("C")]
    [InlineData("Shift+C")]
    [InlineData("Space")]
    [InlineData("Ctrl+Alt+C+D")]
    [InlineData("C+Ctrl")]
    [InlineData("Ctrl+Hyper+C")]
    [InlineData("Ctrl++C")]
    public void Rejects_typing_keys_and_malformed_text(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Standalone_keys_are_function_keys_pause_and_scroll_lock()
    {
        Assert.True(Hotkey.IsStandaloneKey(0x70));  // F1
        Assert.True(Hotkey.IsStandaloneKey(0x87));  // F24
        Assert.True(Hotkey.IsStandaloneKey(0x13));  // Pause
        Assert.True(Hotkey.IsStandaloneKey(0x91));  // Scroll Lock
        Assert.False(Hotkey.IsStandaloneKey(0x41)); // A
        Assert.False(Hotkey.IsStandaloneKey(0x20)); // Space
    }

    [Fact]
    public void Old_config_without_hotkeys_gets_the_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "scale": 2 }""");
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        Assert.Equal("Ctrl+Alt+Shift+K", cfg.Hotkeys.Kill);
        Assert.Equal("Ctrl+Alt+C", cfg.Hotkeys.Chat);
        Assert.Equal("Ctrl+Alt+M", cfg.Hotkeys.Mute);
        Assert.Equal("Ctrl+Alt+H", cfg.Hotkeys.Heart);
        Assert.Equal("Ctrl+Alt+F", cfg.Hotkeys.FocusMode);
        Assert.Equal("Ctrl+Alt+J", cfg.Hotkeys.Jump);
        Assert.False(cfg.FocusMode);
        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal("Ctrl+Alt+C", doc.RootElement.GetProperty("hotkeys").GetProperty("chat").GetString());
        Assert.False(doc.RootElement.GetProperty("focusMode").GetBoolean());
    }

    [Fact]
    public void Hotkeys_are_canonicalised_invalid_ones_reset_and_kill_never_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            { "hotkeys": { "kill": "", "chat": "ctrl + shift + f10", "mute": "Shift+M", "heart": "", "focusMode": "Nonsense+Q" } }
            """);
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        Assert.Equal("Ctrl+Alt+Shift+K", cfg.Hotkeys.Kill);
        Assert.Equal("Ctrl+Shift+F10", cfg.Hotkeys.Chat);
        Assert.Equal("Ctrl+Alt+M", cfg.Hotkeys.Mute);
        Assert.Equal("", cfg.Hotkeys.Heart);
        Assert.Equal("Ctrl+Alt+F", cfg.Hotkeys.FocusMode);
        Assert.Contains(store.Log, l => l.Contains("hotkeys.kill", StringComparison.Ordinal));
        Assert.Contains(store.Log, l => l.Contains("hotkeys.mute", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_binding_keeps_the_earlier_action_and_kill_always_wins()
    {
        var cfg = new CowpanionConfig();
        cfg.Hotkeys.Chat = "Ctrl+Alt+Shift+K";
        cfg.Hotkeys.Heart = "ctrl+alt+m";
        var warnings = ConfigValidator.Clamp(cfg);
        Assert.Equal("Ctrl+Alt+Shift+K", cfg.Hotkeys.Kill);
        Assert.Equal("", cfg.Hotkeys.Chat);
        Assert.Equal("Ctrl+Alt+M", cfg.Hotkeys.Mute);
        Assert.Equal("", cfg.Hotkeys.Heart);
        Assert.Equal(2, warnings.Count(w => w.Contains("already used", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pre_jump_config_gets_the_jump_default_but_never_steals_an_existing_binding()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            { "hotkeys": { "kill": "Ctrl+Alt+Shift+K", "chat": "Ctrl+Alt+C", "mute": "Ctrl+Alt+M", "heart": "Ctrl+Alt+H", "focusMode": "Ctrl+Alt+F" } }
            """);
        using (var store = new ConfigStore(ConfigPath))
        {
            Assert.Equal("Ctrl+Alt+J", store.Load().Hotkeys.Jump);
        }

        var cfg = new CowpanionConfig();
        cfg.Hotkeys.FocusMode = "ctrl+alt+j";
        var warnings = ConfigValidator.Clamp(cfg);
        Assert.Equal("Ctrl+Alt+J", cfg.Hotkeys.FocusMode);
        Assert.Equal("", cfg.Hotkeys.Jump);
        Assert.Contains(warnings, w => w.Contains("hotkeys.jump", StringComparison.Ordinal));
    }

    [Fact]
    public void Jump_can_be_rebound_and_unbound()
    {
        var cfg = new CowpanionConfig();
        cfg.Hotkeys.Jump = "shift + f9";
        ConfigValidator.Clamp(cfg);
        Assert.Equal("Shift+F9", cfg.Hotkeys.Jump);
        cfg.Hotkeys.Jump = "  ";
        ConfigValidator.Clamp(cfg);
        Assert.Equal("", cfg.Hotkeys.Jump);
        Assert.False(new HotkeyBindings().SameAs(cfg.Hotkeys));
    }

    [Fact]
    public void Null_hotkeys_object_is_replaced()
    {
        var cfg = new CowpanionConfig { Hotkeys = null! };
        ConfigValidator.Clamp(cfg);
        Assert.NotNull(cfg.Hotkeys);
        Assert.Equal(HotkeyBindings.DefaultChat, cfg.Hotkeys.Chat);
    }

    [Fact]
    public void Clone_copies_the_hotkeys_deeply()
    {
        var a = new CowpanionConfig();
        var b = a.Clone();
        b.Hotkeys.Chat = "F8";
        Assert.Equal(HotkeyBindings.DefaultChat, a.Hotkeys.Chat);
        Assert.False(a.Hotkeys.SameAs(b.Hotkeys));
    }
}

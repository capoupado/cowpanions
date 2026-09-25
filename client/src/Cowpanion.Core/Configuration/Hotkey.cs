using System.Text;

namespace Cowpanion.Core.Configuration;

/// <summary>Modifier flags. Values match the Win32 MOD_* constants RegisterHotKey takes.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A global hotkey: modifiers plus one Win32 virtual key. Text form is "Ctrl+Alt+C" (modifiers in the order
/// Ctrl, Alt, Shift, Win; key names from <see cref="KeyNames"/>). Pure; the App layer turns it into RegisterHotKey.
/// <para>
/// Any combination is allowed except ones that would swallow ordinary typing system-wide: a key with no modifier (or
/// Shift only) must be a function key, Pause or Scroll Lock. RegisterHotKey itself may still refuse a combination that
/// Windows or another app already owns; that is reported at registration time, not here.
/// </para>
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> ByName = BuildNames();
    private static readonly Dictionary<uint, string> ByKey = BuildReverse();

    /// <summary>Every accepted key name, for docs and the settings window.</summary>
    public static IReadOnlyCollection<string> KeyNames => ByName.Keys;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            sb.Append("Ctrl+");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            sb.Append("Alt+");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            sb.Append("Shift+");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            sb.Append("Win+");
        }
        sb.Append(ByKey.TryGetValue(VirtualKey, out var name) ? name : "0x" + VirtualKey.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Name for a virtual key, or null when the key is not one a hotkey may use.</summary>
    public static string? NameOf(uint virtualKey) => ByKey.TryGetValue(virtualKey, out var name) ? name : null;

    /// <summary>
    /// Parses "Ctrl+Alt+C" (case-insensitive, spaces ignored, "Control"/"Windows" accepted). On failure
    /// <paramref name="error"/> says why in words fit for the settings window.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey, out string error)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty";
            return false;
        }
        var mods = HotkeyModifiers.None;
        uint? key = null;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0)
            {
                error = $"'{text}' has an empty part";
                return false;
            }
            HotkeyModifiers mod = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (mod != HotkeyModifiers.None)
            {
                if (key is not null)
                {
                    error = "modifiers must come before the key";
                    return false;
                }
                mods |= mod;
                continue;
            }
            if (key is not null)
            {
                error = "only one non-modifier key is allowed";
                return false;
            }
            if (!ByName.TryGetValue(part, out uint vk))
            {
                error = $"unknown key '{part}'";
                return false;
            }
            key = vk;
        }
        if (key is null)
        {
            error = "needs a key besides the modifiers";
            return false;
        }
        return TryCreate(mods, key.Value, out hotkey, out error);
    }

    /// <summary>Validates a modifiers + key pair (used by the capture box, which gets raw keys).</summary>
    public static bool TryCreate(HotkeyModifiers modifiers, uint virtualKey, out Hotkey hotkey, out string error)
    {
        hotkey = default;
        if (!ByKey.ContainsKey(virtualKey))
        {
            error = "that key cannot be used for a hotkey";
            return false;
        }
        bool typingSafe = (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;
        if (!typingSafe && !IsStandaloneKey(virtualKey))
        {
            error = "needs Ctrl, Alt or Win (on its own it would block typing everywhere)";
            return false;
        }
        hotkey = new Hotkey(modifiers, virtualKey);
        error = "";
        return true;
    }

    /// <summary>Keys that never produce text, so they may be bound without Ctrl/Alt/Win: F1–F24, Pause, Scroll Lock.</summary>
    public static bool IsStandaloneKey(uint virtualKey)
    {
        return (virtualKey >= 0x70 && virtualKey <= 0x87) || virtualKey == 0x13 || virtualKey == 0x91;
    }

    private static Dictionary<string, uint> BuildNames()
    {
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            d[c.ToString()] = c;
        }
        for (char c = '0'; c <= '9'; c++)
        {
            d[c.ToString()] = c;
        }
        for (uint i = 0; i < 10; i++)
        {
            d["Num" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 0x60 + i;
        }
        for (uint i = 1; i <= 24; i++)
        {
            d["F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 0x6F + i;
        }
        d["Space"] = 0x20;
        d["Enter"] = 0x0D;
        d["Tab"] = 0x09;
        d["Esc"] = 0x1B;
        d["Backspace"] = 0x08;
        d["Insert"] = 0x2D;
        d["Delete"] = 0x2E;
        d["Home"] = 0x24;
        d["End"] = 0x23;
        d["PageUp"] = 0x21;
        d["PageDown"] = 0x22;
        d["Left"] = 0x25;
        d["Up"] = 0x26;
        d["Right"] = 0x27;
        d["Down"] = 0x28;
        d["Pause"] = 0x13;
        d["ScrollLock"] = 0x91;
        d["PrintScreen"] = 0x2C;
        d["NumMultiply"] = 0x6A;
        d["NumAdd"] = 0x6B;
        d["NumSubtract"] = 0x6D;
        d["NumDecimal"] = 0x6E;
        d["NumDivide"] = 0x6F;
        // OEM keys by their US-layout symbol, spelled out so '+' stays the separator.
        d["Semicolon"] = 0xBA;
        d["Equals"] = 0xBB;
        d["Comma"] = 0xBC;
        d["Minus"] = 0xBD;
        d["Period"] = 0xBE;
        d["Slash"] = 0xBF;
        d["Backtick"] = 0xC0;
        d["LeftBracket"] = 0xDB;
        d["Backslash"] = 0xDC;
        d["RightBracket"] = 0xDD;
        d["Quote"] = 0xDE;
        d["Oem102"] = 0xE2;
        return d;
    }

    private static Dictionary<uint, string> BuildReverse()
    {
        var d = new Dictionary<uint, string>();
        foreach (var (name, vk) in ByName)
        {
            d.TryAdd(vk, name);
        }
        return d;
    }
}

/// <summary>The rebindable global hotkeys, as text. Empty string = unbound (not allowed for <see cref="Kill"/>).</summary>
public sealed class HotkeyBindings
{
    public const string DefaultKill = "Ctrl+Alt+Shift+K";
    public const string DefaultChat = "Ctrl+Alt+C";
    public const string DefaultMute = "Ctrl+Alt+M";
    public const string DefaultHeart = "Ctrl+Alt+H";
    public const string DefaultFocusMode = "Ctrl+Alt+F";
    public const string DefaultJump = "Ctrl+Alt+J";

    /// <summary>Quit immediately. Can be rebound, never unbound.</summary>
    public string Kill { get; set; } = DefaultKill;
    public string Chat { get; set; } = DefaultChat;
    public string Mute { get; set; } = DefaultMute;
    public string Heart { get; set; } = DefaultHeart;
    public string FocusMode { get; set; } = DefaultFocusMode;
    /// <summary>Our cow jumps (a "jump" emote on every screen when connected, local only when offline).</summary>
    public string Jump { get; set; } = DefaultJump;

    public HotkeyBindings Clone() => (HotkeyBindings)MemberwiseClone();

    public bool SameAs(HotkeyBindings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kill == other.Kill && Chat == other.Chat && Mute == other.Mute && Heart == other.Heart && FocusMode == other.FocusMode
            && Jump == other.Jump;
    }
}

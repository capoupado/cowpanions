using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cowpanion.Core.Simulation;

namespace Cowpanion.Core.Configuration;

/// <summary>User configuration. Serialised as camelCase JSON at %APPDATA%\Cowpanion\config.json.</summary>
public sealed class CowpanionConfig
{
    public string SpritePack { get; set; } = "cow";
    public int Scale { get; set; } = 3;
    /// <summary>"primary" or "all".</summary>
    public string Monitors { get; set; } = "primary";
    public int ActiveFps { get; set; } = 30;
    public int IdleFps { get; set; } = 5;
    public bool MooEnabled { get; set; } = false;
    public int SleepAfterIdleMinutes { get; set; } = 20;
    public bool PauseOnFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    public bool MultiplayerEnabled { get; set; } = true;
    public string ServerUrl { get; set; } = "wss://cows.carlospoupado.com/ws";
    public string Pasture { get; set; } = "commons";
    public string DisplayName { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool BubblesEnabled { get; set; } = true;
    public bool BubblesMuted { get; set; } = false;
    public int OfflineHerdSize { get; set; } = 4;
    /// <summary>Cow colour. Empty = derive from clientId once and persist.</summary>
    public string Variant { get; set; } = "";

    /// <summary>Global hotkeys, rebindable (DECISIONS.md, fifth round).</summary>
    public HotkeyBindings Hotkeys { get; set; } = new();
    /// <summary>Focus mode: every global hotkey is released except Quit and the focus-mode toggle itself.</summary>
    public bool FocusMode { get; set; } = false;

    public CowpanionConfig Clone()
    {
        var copy = (CowpanionConfig)MemberwiseClone();
        copy.Hotkeys = (Hotkeys ?? new HotkeyBindings()).Clone();
        return copy;
    }
}

/// <summary>Clamps invalid values (never fatal) and reports what was changed.</summary>
public static partial class ConfigValidator
{
    public const int MaxDisplayNameLength = 16;

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex PastureRegex();

    [GeneratedRegex("^[a-z0-9_]{1,16}$")]
    private static partial Regex VariantRegex();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ClientIdRegex();

    public const string DefaultServerUrl = "wss://cows.carlospoupado.com/ws";

    public static bool IsValidVariant(string? v) => v is not null && VariantRegex().IsMatch(v);

    /// <summary>True for a pasture code as typed by the user (lowercased and trimmed first).</summary>
    public static bool IsValidPasture(string? v) => v is not null && PastureRegex().IsMatch(v.Trim().ToLowerInvariant());

    public static bool IsValidServerUrl(string? v) => Uri.TryCreate(v, UriKind.Absolute, out var uri) && (uri.Scheme == "wss" || uri.Scheme == "ws");

    public static bool IsValidClientId(string? v) => v is not null && ClientIdRegex().IsMatch(v);

    /// <summary>Returns the list of corrections applied (empty when the config was already valid).</summary>
    public static List<string> Clamp(CowpanionConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(c.SpritePack))
        {
            warnings.Add("spritePack empty → 'cow'");
            c.SpritePack = "cow";
        }
        c.Scale = ClampInt(c.Scale, 1, 6, "scale", warnings);
        string monitors = (c.Monitors ?? "").Trim().ToLowerInvariant();
        if (monitors != "primary" && monitors != "all")
        {
            warnings.Add($"monitors '{c.Monitors}' → 'primary'");
            monitors = "primary";
        }
        c.Monitors = monitors;
        c.ActiveFps = ClampInt(c.ActiveFps, 5, 60, "activeFps", warnings);
        c.IdleFps = ClampInt(c.IdleFps, 1, c.ActiveFps, "idleFps", warnings);
        c.SleepAfterIdleMinutes = ClampInt(c.SleepAfterIdleMinutes, 1, 24 * 60, "sleepAfterIdleMinutes", warnings);
        c.OfflineHerdSize = ClampInt(c.OfflineHerdSize, 0, 12, "offlineHerdSize", warnings);

        if (!IsValidServerUrl(c.ServerUrl))
        {
            warnings.Add($"serverUrl '{c.ServerUrl}' is not a ws/wss URL → default");
            c.ServerUrl = DefaultServerUrl;
        }

        string pasture = (c.Pasture ?? "").Trim().ToLowerInvariant();
        if (!PastureRegex().IsMatch(pasture))
        {
            warnings.Add($"pasture '{c.Pasture}' invalid → 'commons'");
            pasture = "commons";
        }
        c.Pasture = pasture;

        string name = SanitizeDisplayName(c.DisplayName);
        if (name != (c.DisplayName ?? ""))
        {
            if ((c.DisplayName ?? "").Length > 0)
            {
                warnings.Add("displayName sanitised");
            }
            c.DisplayName = name;
        }

        if (!string.IsNullOrEmpty(c.ClientId) && !IsValidClientId(c.ClientId))
        {
            warnings.Add("clientId is not 32 hex chars → regenerated");
            c.ClientId = "";
        }
        if (string.IsNullOrEmpty(c.ClientId))
        {
            c.ClientId = NewClientId();
        }

        if (!string.IsNullOrEmpty(c.Variant) && !IsValidVariant(c.Variant))
        {
            warnings.Add($"variant '{c.Variant}' invalid → derive from clientId");
            c.Variant = "";
        }

        ClampHotkeys(c, warnings);
        return warnings;
    }

    /// <summary>
    /// Canonicalises each binding ("ctrl + alt + c" → "Ctrl+Alt+C"). Unparseable → that action's default; Kill may not
    /// be empty. A combination used twice keeps the first action in the order Kill, Chat, Mute, Heart, FocusMode and
    /// unbinds the later one.
    /// </summary>
    private static void ClampHotkeys(CowpanionConfig c, List<string> warnings)
    {
        if (c.Hotkeys is null)
        {
            warnings.Add("hotkeys missing → defaults");
            c.Hotkeys = new HotkeyBindings();
        }
        var h = c.Hotkeys;
        var seen = new HashSet<Hotkey>();
        h.Kill = ClampBinding(h.Kill, HotkeyBindings.DefaultKill, "kill", allowEmpty: false, seen, warnings);
        h.Chat = ClampBinding(h.Chat, HotkeyBindings.DefaultChat, "chat", allowEmpty: true, seen, warnings);
        h.Mute = ClampBinding(h.Mute, HotkeyBindings.DefaultMute, "mute", allowEmpty: true, seen, warnings);
        h.Heart = ClampBinding(h.Heart, HotkeyBindings.DefaultHeart, "heart", allowEmpty: true, seen, warnings);
        h.FocusMode = ClampBinding(h.FocusMode, HotkeyBindings.DefaultFocusMode, "focusMode", allowEmpty: true, seen, warnings);
    }

    private static string ClampBinding(string? value, string fallback, string name, bool allowEmpty, HashSet<Hotkey> seen, List<string> warnings)
    {
        if (value is null)
        {
            warnings.Add($"hotkeys.{name} missing → '{fallback}'");
            value = fallback;
        }
        if (value.Trim().Length == 0)
        {
            if (allowEmpty)
            {
                return "";
            }
            warnings.Add($"hotkeys.{name} cannot be empty → '{fallback}'");
            value = fallback;
        }
        if (!Hotkey.TryParse(value, out var hotkey, out string error))
        {
            warnings.Add($"hotkeys.{name} '{value}' invalid ({error}) → '{fallback}'");
            Hotkey.TryParse(fallback, out hotkey, out _);
        }
        if (!seen.Add(hotkey))
        {
            if (!allowEmpty)
            {
                return hotkey.ToString(); // Kill is first, so it never loses a clash.
            }
            warnings.Add($"hotkeys.{name} '{hotkey}' is already used by another action → unbound");
            return "";
        }
        return hotkey.ToString();
    }

    /// <summary>Trims, collapses whitespace, strips control characters, truncates to 16 chars (never splits a surrogate pair).</summary>
    public static string SanitizeDisplayName(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }
        var sb = new System.Text.StringBuilder(raw.Length);
        bool lastSpace = true;
        foreach (char ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
                continue;
            }
            if (char.IsControl(ch))
            {
                continue;
            }
            sb.Append(ch);
            lastSpace = false;
        }
        string s = sb.ToString().TrimEnd();
        if (s.Length > MaxDisplayNameLength)
        {
            int cut = MaxDisplayNameLength;
            if (char.IsHighSurrogate(s[cut - 1]))
            {
                cut--;
            }
            s = s.Substring(0, cut).TrimEnd();
        }
        return s;
    }

    /// <summary>128 random bits from the OS CSPRNG as 32 lowercase hex chars. Never derived from anything identifying.</summary>
    public static string NewClientId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Deterministic variant for a clientId: hash into the (ordinal-sorted) known variant list.</summary>
    public static string DeriveVariant(string clientId, IReadOnlyList<string> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (variants.Count == 0)
        {
            return "";
        }
        ulong h = StableHash.Fnv1a64(clientId);
        return variants[(int)(h % (ulong)variants.Count)];
    }

    private static int ClampInt(int value, int min, int max, string name, List<string> warnings)
    {
        if (value < min)
        {
            warnings.Add($"{name} {value} < {min} → {min}");
            return min;
        }
        if (value > max)
        {
            warnings.Add($"{name} {value} > {max} → {max}");
            return max;
        }
        return value;
    }
}

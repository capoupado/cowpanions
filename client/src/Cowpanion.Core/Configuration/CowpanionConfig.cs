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

    public CowpanionConfig Clone()
    {
        return (CowpanionConfig)MemberwiseClone();
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

    public static bool IsValidVariant(string? v) => v is not null && VariantRegex().IsMatch(v);

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

        if (!Uri.TryCreate(c.ServerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
        {
            warnings.Add($"serverUrl '{c.ServerUrl}' is not a ws/wss URL → default");
            c.ServerUrl = "wss://cows.carlospoupado.com/ws";
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

        return warnings;
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

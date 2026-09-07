using System.Globalization;
using System.Text;
using Cowpanion.Net.Protocol;

namespace Cowpanion.Net;

/// <summary>What a typed line turns into on the wire. Empty strings mean "absent".</summary>
public sealed record ChatDraft(string Text, string Emote, string Reaction)
{
    public static readonly ChatDraft Empty = new("", "", "");

    public bool IsEmpty => Text.Length == 0 && Emote.Length == 0 && Reaction.Length == 0;
}

/// <summary>
/// Turns the chat box contents into a <see cref="ChatDraft"/>: <c>/moo</c> <c>/jump</c> <c>/spin</c> are emotes,
/// <c>/heart</c> <c>/love</c> <c>/lol</c> <c>/wave</c> <c>/party</c> <c>/wow</c> <c>/sad</c> expand to reactions,
/// a line that is only 1–3 emoji is a reaction, and everything else is text. Text after a slash command rides along
/// (<c>/jump hello</c>). Unknown slash commands are plain text. Pure; no UI types.
/// </summary>
public static class ChatComposer
{
    private static readonly Dictionary<string, string> ReactionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["heart"] = "❤️",
        ["love"] = "❤️",
        ["lol"] = "😂",
        ["wave"] = "👋",
        ["party"] = "🎉",
        ["wow"] = "😮",
        ["sad"] = "😢",
    };

    public static ChatDraft Parse(string? input)
    {
        if (input is null)
        {
            return ChatDraft.Empty;
        }
        string line = input.Trim();
        if (line.Length == 0)
        {
            return ChatDraft.Empty;
        }

        if (line[0] == '/')
        {
            int end = 1;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
            {
                end++;
            }
            string command = line.Substring(1, end - 1).ToLowerInvariant();
            string rest = line.Substring(end).Trim();
            if (ProtocolConstants.IsEmote(command))
            {
                return new ChatDraft(rest, command, "");
            }
            if (ReactionCommands.TryGetValue(command, out var emoji))
            {
                return new ChatDraft(rest, "", emoji);
            }
            return new ChatDraft(line, "", "");
        }

        string? reaction = TryEmojiOnly(line);
        return reaction is not null ? new ChatDraft("", "", reaction) : new ChatDraft(line, "", "");
    }

    /// <summary>Splits a reaction into its grapheme clusters, skipping whitespace. Used by the renderer.</summary>
    public static List<string> Graphemes(string reaction)
    {
        var list = new List<string>(ProtocolConstants.MaxReactionGraphemes);
        var e = StringInfo.GetTextElementEnumerator(reaction);
        while (e.MoveNext())
        {
            string cluster = e.GetTextElement();
            if (!IsWhitespace(cluster))
            {
                list.Add(cluster);
            }
        }
        return list;
    }

    /// <summary>
    /// Returns the first <see cref="ProtocolConstants.MaxReactionGraphemes"/> clusters if every non-whitespace grapheme
    /// cluster in <paramref name="line"/> is emoji-like; null otherwise.
    /// </summary>
    private static string? TryEmojiOnly(string line)
    {
        var sb = new StringBuilder();
        int kept = 0;
        var e = StringInfo.GetTextElementEnumerator(line);
        while (e.MoveNext())
        {
            string cluster = e.GetTextElement();
            if (IsWhitespace(cluster))
            {
                continue;
            }
            if (!IsEmojiCluster(cluster))
            {
                return null;
            }
            if (kept < ProtocolConstants.MaxReactionGraphemes)
            {
                sb.Append(cluster);
                kept++;
            }
        }
        return kept > 0 ? sb.ToString() : null;
    }

    private static bool IsWhitespace(string cluster)
    {
        foreach (char c in cluster)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A cluster is emoji-like when it contains an Extended_Pictographic code point (approximated by the Unicode
    /// emoji blocks), a Regional_Indicator, or the keycap enclosure U+20E3. Mirrors the server's rule closely
    /// enough; the server's own validation is the final word.
    /// </summary>
    public static bool IsEmojiCluster(string cluster)
    {
        foreach (Rune r in cluster.EnumerateRunes())
        {
            int cp = r.Value;
            if (cp == 0x20E3 || (cp >= 0x1F1E6 && cp <= 0x1F1FF) || IsPictographic(cp))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPictographic(int cp)
    {
        return (cp >= 0x1F000 && cp <= 0x1FAFF)   // emoticons, symbols & pictographs, transport, supplemental, extended-A
            || (cp >= 0x2600 && cp <= 0x27BF)     // misc symbols, dingbats (❤ ☀ ✨ ✅)
            || (cp >= 0x2B00 && cp <= 0x2BFF)     // arrows & misc (⭐ ⬆)
            || (cp >= 0x2300 && cp <= 0x23FF)     // misc technical (⌚ ⏰ ⏳)
            || (cp >= 0x25A0 && cp <= 0x25FF)     // geometric shapes (◽ ▶)
            || (cp >= 0x2190 && cp <= 0x21FF)     // arrows (↔ ↩)
            || (cp >= 0x2900 && cp <= 0x297F)     // supplemental arrows (⤴)
            || cp == 0x00A9 || cp == 0x00AE || cp == 0x203C || cp == 0x2049 || cp == 0x2122 || cp == 0x2139
            || cp == 0x24C2 || cp == 0x3030 || cp == 0x303D || cp == 0x3297 || cp == 0x3299;
    }
}

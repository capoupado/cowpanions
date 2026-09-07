using System.Globalization;
using System.Text;

namespace Cowpanion.Core.Text;

/// <summary>
/// Wraps chat text into at most N lines of at most M grapheme clusters, with an ellipsis when truncated.
/// Grapheme-aware so emoji and combining marks are never split. Pure; no UI types.
/// </summary>
public static class BubbleText
{
    public const int DefaultMaxCharsPerLine = 28;
    public const int DefaultMaxLines = 2;
    private const string Ellipsis = "…";

    public static string Wrap(string text, int maxCharsPerLine = DefaultMaxCharsPerLine, int maxLines = DefaultMaxLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxCharsPerLine < 2 || maxLines < 1)
        {
            return "";
        }

        var words = SplitWords(text);
        var lines = new List<string>();
        var current = new StringBuilder();
        int currentLen = 0;

        foreach (var word in words)
        {
            int wordLen = GraphemeCount(word);
            if (currentLen == 0)
            {
                AppendWord(word, wordLen, maxCharsPerLine, lines, current, ref currentLen);
                continue;
            }
            if (currentLen + 1 + wordLen <= maxCharsPerLine)
            {
                current.Append(' ').Append(word);
                currentLen += 1 + wordLen;
                continue;
            }
            lines.Add(current.ToString());
            current.Clear();
            currentLen = 0;
            AppendWord(word, wordLen, maxCharsPerLine, lines, current, ref currentLen);
        }
        if (currentLen > 0)
        {
            lines.Add(current.ToString());
        }

        if (lines.Count <= maxLines)
        {
            return string.Join('\n', lines);
        }

        var kept = lines.GetRange(0, maxLines);
        string last = kept[maxLines - 1];
        kept[maxLines - 1] = TruncateGraphemes(last, maxCharsPerLine - 1).TrimEnd() + Ellipsis;
        return string.Join('\n', kept);
    }

    private static void AppendWord(string word, int wordLen, int max, List<string> lines, StringBuilder current, ref int currentLen)
    {
        if (wordLen <= max)
        {
            current.Append(word);
            currentLen = wordLen;
            return;
        }
        // A single over-long word: hard-break on grapheme boundaries.
        var e = StringInfo.GetTextElementEnumerator(word);
        int n = 0;
        while (e.MoveNext())
        {
            if (n == max)
            {
                lines.Add(current.ToString());
                current.Clear();
                n = 0;
            }
            current.Append(e.GetTextElement());
            n++;
        }
        currentLen = n;
    }

    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (sb.Length > 0)
                {
                    words.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0)
        {
            words.Add(sb.ToString());
        }
        return words;
    }

    public static int GraphemeCount(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        int n = 0;
        while (e.MoveNext())
        {
            n++;
        }
        return n;
    }

    public static string TruncateGraphemes(string s, int maxGraphemes)
    {
        if (maxGraphemes <= 0)
        {
            return "";
        }
        var e = StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder();
        int n = 0;
        while (e.MoveNext() && n < maxGraphemes)
        {
            sb.Append(e.GetTextElement());
            n++;
        }
        return sb.ToString();
    }
}

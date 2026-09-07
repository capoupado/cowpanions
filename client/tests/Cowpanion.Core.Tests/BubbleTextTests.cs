using Cowpanion.Core.Text;

namespace Cowpanion.Core.Tests;

public class BubbleTextTests
{
    [Fact]
    public void Short_text_is_one_line()
    {
        Assert.Equal("morning", BubbleText.Wrap("morning"));
    }

    [Fact]
    public void Wraps_on_words_to_two_lines_and_ellipsises_the_rest()
    {
        string text = "the quick brown fox jumps over the lazy dog and keeps running far beyond the pasture fence";
        string wrapped = BubbleText.Wrap(text);
        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(BubbleText.GraphemeCount(l) <= 28, l));
        Assert.EndsWith("…", lines[1]);
        Assert.Equal("the quick brown fox jumps", lines[0]);
    }

    [Fact]
    public void Emoji_are_never_split()
    {
        string text = string.Concat(Enumerable.Repeat("👩‍🌾", 60)); // 60 graphemes (28 + 28 + 4), many UTF-16 units each
        string wrapped = BubbleText.Wrap(text);
        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(28, BubbleText.GraphemeCount(lines[0]));
        Assert.True(lines[1].EndsWith('…'));
        Assert.True(BubbleText.GraphemeCount(lines[1]) <= 28);
        // Every element except the ellipsis is the full farmer grapheme: no lone surrogates, no stray ZWJ.
        Assert.DoesNotContain("�", wrapped);
        foreach (var line in lines)
        {
            foreach (var element in line.Replace("…", "").Split("👩‍🌾"))
            {
                Assert.Equal("", element);
            }
        }

        // 40 graphemes fit exactly in two lines and need no ellipsis.
        string fits = BubbleText.Wrap(string.Concat(Enumerable.Repeat("👩‍🌾", 40)));
        Assert.DoesNotContain("…", fits);
        Assert.Equal(12, BubbleText.GraphemeCount(fits.Split('\n')[1]));
    }

    [Fact]
    public void Max_length_message_with_emoji_fits_two_lines()
    {
        string text = "Bom dia 🐄🐄 " + new string('a', 60) + " " + new string('b', 60) + " 😀";
        Assert.True(text.Length >= 130);
        string wrapped = BubbleText.Wrap(text);
        var lines = wrapped.Split('\n');
        Assert.True(lines.Length <= 2);
        Assert.All(lines, l => Assert.True(BubbleText.GraphemeCount(l) <= 28));
    }

    [Fact]
    public void Whitespace_runs_collapse()
    {
        Assert.Equal("a b c", BubbleText.Wrap("a   b\t\tc\n"));
    }
}

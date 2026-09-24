using Cowpanion.Net;

namespace Cowpanion.Net.Tests;

public class ChatHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 2, 0, TimeSpan.Zero);

    private static ChatMessage Msg(string text = "", string emote = "", string reaction = "", string from = "a") =>
        new(from, "Carlos", text, 0, emote, reaction);

    [Fact]
    public void Keeps_the_newest_entries_up_to_capacity_oldest_first()
    {
        var history = new ChatHistory(capacity: 3);
        for (int i = 0; i < 5; i++)
        {
            history.Add(Msg("m" + i), isSelf: false, T0.AddSeconds(i));
        }
        Assert.Equal(3, history.Count);
        Assert.Equal(["m2", "m3", "m4"], history.Snapshot().Select(e => e.Text));
    }

    [Fact]
    public void Empty_frames_are_ignored_and_events_fire()
    {
        var history = new ChatHistory();
        int added = 0, cleared = 0;
        history.Added += _ => added++;
        history.Cleared += () => cleared++;
        Assert.False(history.Add(Msg(), false, T0));
        Assert.True(history.Add(Msg(emote: "jump"), true, T0));
        Assert.Equal(1, added);
        Assert.True(history.Snapshot()[0].IsSelf);
        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(1, cleared);
    }

    [Theory]
    [InlineData("morning", "", "", ": morning")]
    [InlineData("gg", "", "❤️", ": gg ❤️")]
    [InlineData("hi", "jump", "", ": hi (jumped)")]
    [InlineData("", "moo", "", " mooed")]
    [InlineData("", "", "🎉", " reacted 🎉")]
    [InlineData("", "spin", "🎉", " reacted 🎉 and spun around")]
    public void Describes_each_kind_of_frame(string text, string emote, string reaction, string expected)
    {
        var history = new ChatHistory();
        history.Add(Msg(text, emote, reaction), false, T0);
        Assert.Equal(expected, ChatHistory.DescribeBody(history.Snapshot()[0]));
    }

    [Fact]
    public void Plain_format_has_time_and_name()
    {
        var entry = new ChatHistoryEntry(T0, "a", "Carlos", "morning", "", "", false);
        string line = ChatHistory.FormatPlain(entry);
        Assert.EndsWith(" Carlos: morning", line, StringComparison.Ordinal);
        Assert.Matches("^[0-2][0-9]:[0-5][0-9] ", line);
    }
}

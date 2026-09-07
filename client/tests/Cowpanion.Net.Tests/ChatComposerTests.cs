namespace Cowpanion.Net.Tests;

public class ChatComposerTests
{
    [Theory]
    [InlineData("/jump", "", "jump", "")]
    [InlineData("/jump hi", "hi", "jump", "")]
    [InlineData("/MOO", "", "moo", "")]
    [InlineData("  /spin   round  ", "round", "spin", "")]
    public void Slash_emotes_become_emotes_with_trailing_text(string input, string text, string emote, string reaction)
    {
        var d = ChatComposer.Parse(input);
        Assert.Equal(text, d.Text);
        Assert.Equal(emote, d.Emote);
        Assert.Equal(reaction, d.Reaction);
    }

    [Theory]
    [InlineData("/heart", "❤️")]
    [InlineData("/HEART", "❤️")]
    [InlineData("/love", "❤️")]
    [InlineData("/lol", "😂")]
    [InlineData("/wave", "👋")]
    [InlineData("/party", "🎉")]
    [InlineData("/wow", "😮")]
    [InlineData("/sad", "😢")]
    public void Slash_reaction_commands_expand_case_insensitively(string input, string reaction)
    {
        var d = ChatComposer.Parse(input);
        Assert.Equal("", d.Text);
        Assert.Equal("", d.Emote);
        Assert.Equal(reaction, d.Reaction);
    }

    [Fact]
    public void Slash_reaction_with_text_keeps_the_text()
    {
        var d = ChatComposer.Parse("/heart well played");
        Assert.Equal("well played", d.Text);
        Assert.Equal("❤️", d.Reaction);
        Assert.Equal("", d.Emote);
    }

    [Theory]
    [InlineData("❤️", "❤️")]
    [InlineData("😂 🎉", "😂🎉")]
    [InlineData(" 👍🏽 ", "👍🏽")]
    [InlineData("🇵🇹", "🇵🇹")]
    [InlineData("1️⃣", "1️⃣")]
    [InlineData("👨‍👩‍👧", "👨‍👩‍👧")]
    public void Emoji_only_input_is_a_reaction(string input, string reaction)
    {
        var d = ChatComposer.Parse(input);
        Assert.Equal("", d.Text);
        Assert.Equal("", d.Emote);
        Assert.Equal(reaction, d.Reaction);
    }

    [Fact]
    public void More_than_three_emoji_are_truncated_to_three()
    {
        var d = ChatComposer.Parse("❤️❤️❤️❤️");
        Assert.Equal("❤️❤️❤️", d.Reaction);
        Assert.Equal("", d.Text);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/nope")]
    [InlineData("/nope really")]
    [InlineData("❤️x")]
    [InlineData("hi 😀")]
    [InlineData("1")]
    [InlineData("*")]
    public void Everything_else_is_plain_text(string input)
    {
        var d = ChatComposer.Parse(input);
        Assert.Equal(input, d.Text);
        Assert.Equal("", d.Emote);
        Assert.Equal("", d.Reaction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_empty(string? input)
    {
        Assert.True(ChatComposer.Parse(input).IsEmpty);
    }

    [Fact]
    public void Graphemes_splits_a_reaction_and_skips_whitespace()
    {
        var g = ChatComposer.Graphemes("😂 🎉👍🏽");
        Assert.Equal(new[] { "😂", "🎉", "👍🏽" }, g);
    }
}

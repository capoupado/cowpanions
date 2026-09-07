using System.Text;
using System.Text.Json;
using Cowpanion.Net.Protocol;

namespace Cowpanion.Net.Tests;

public class MessageCodecTests
{
    [Fact]
    public void Hello_has_exactly_the_specified_fields()
    {
        var bytes = MessageCodec.EncodeHello("a3f1", "commons", "Carlos", "brown");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal("hello", root.GetProperty("t").GetString());
        Assert.Equal(2, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("a3f1", root.GetProperty("clientId").GetString());
        Assert.Equal("commons", root.GetProperty("pasture").GetString());
        Assert.Equal("Carlos", root.GetProperty("displayName").GetString());
        Assert.Equal("brown", root.GetProperty("variant").GetString());
        Assert.Equal(6, root.EnumerateObject().Count());
    }

    [Fact]
    public void Chat_ping_bye_encode()
    {
        Assert.Equal("{\"t\":\"ping\"}", Encoding.UTF8.GetString(MessageCodec.EncodePing()));
        Assert.Equal("{\"t\":\"bye\"}", Encoding.UTF8.GetString(MessageCodec.EncodeBye()));
        using var doc = JsonDocument.Parse(MessageCodec.EncodeChat("hi \"there\" 😀"));
        Assert.Equal("chat", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal("hi \"there\" 😀", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Chat_v2_writes_only_non_empty_fields()
    {
        using var emoteOnly = JsonDocument.Parse(MessageCodec.EncodeChat("", "jump", ""));
        Assert.Equal("jump", emoteOnly.RootElement.GetProperty("emote").GetString());
        Assert.False(emoteOnly.RootElement.TryGetProperty("text", out _));
        Assert.False(emoteOnly.RootElement.TryGetProperty("reaction", out _));
        Assert.Equal(2, emoteOnly.RootElement.EnumerateObject().Count());

        using var reactionOnly = JsonDocument.Parse(MessageCodec.EncodeChat("", "", "❤️"));
        Assert.Equal("❤️", reactionOnly.RootElement.GetProperty("reaction").GetString());
        Assert.False(reactionOnly.RootElement.TryGetProperty("text", out _));
        Assert.False(reactionOnly.RootElement.TryGetProperty("emote", out _));

        using var both = JsonDocument.Parse(MessageCodec.EncodeChat("gg", "", "🎉"));
        Assert.Equal("gg", both.RootElement.GetProperty("text").GetString());
        Assert.Equal("🎉", both.RootElement.GetProperty("reaction").GetString());
        Assert.False(both.RootElement.TryGetProperty("emote", out _));
        Assert.Equal(3, both.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Chat_v2_server_frames_decode_with_optional_fields()
    {
        var emote = Assert.IsType<ChatServerMessage>(MessageCodec.Decode("{\"t\":\"chat\",\"fromId\":\"a\",\"name\":\"A\",\"ts\":5,\"emote\":\"jump\"}"u8));
        Assert.Equal("", emote.Chat.Text);
        Assert.Equal("jump", emote.Chat.Emote);
        Assert.Equal("", emote.Chat.Reaction);

        var reaction = Assert.IsType<ChatServerMessage>(MessageCodec.Decode(Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"fromId\":\"a\",\"name\":\"A\",\"ts\":5,\"reaction\":\"❤️\"}")));
        Assert.Equal("", reaction.Chat.Text);
        Assert.Equal("❤️", reaction.Chat.Reaction);

        var both = Assert.IsType<ChatServerMessage>(MessageCodec.Decode(Encoding.UTF8.GetBytes("{\"t\":\"chat\",\"fromId\":\"a\",\"name\":\"A\",\"ts\":5,\"text\":\"gg\",\"reaction\":\"🎉\"}")));
        Assert.Equal("gg", both.Chat.Text);
        Assert.Equal("🎉", both.Chat.Reaction);
        Assert.Equal("", both.Chat.Emote);
    }

    [Fact]
    public void V1_shaped_chat_frame_still_decodes_with_empty_emote_and_reaction()
    {
        var c = Assert.IsType<ChatServerMessage>(MessageCodec.Decode("{\"t\":\"chat\",\"fromId\":\"a\",\"name\":\"A\",\"text\":\"moo\",\"ts\":5}"u8));
        Assert.Equal("moo", c.Chat.Text);
        Assert.Equal("", c.Chat.Emote);
        Assert.Equal("", c.Chat.Reaction);
        Assert.Equal(5, c.Chat.Ts);
    }

    [Fact]
    public void Decodes_every_server_message()
    {
        var w = Assert.IsType<WelcomeMessage>(MessageCodec.Decode("{\"t\":\"welcome\",\"protocolVersion\":1,\"yourId\":\"abc\",\"pasture\":\"commons\",\"visibleCap\":12,\"serverTime\":1757260800000}"u8));
        Assert.Equal("abc", w.YourId);
        Assert.Equal(1757260800000, w.ServerTime);

        var p = Assert.IsType<PresenceMessage>(MessageCodec.Decode("{\"t\":\"presence\",\"members\":[{\"id\":\"a\",\"name\":\"A\",\"variant\":\"brown\"},{\"id\":\"b\",\"name\":\"B\",\"variant\":\"black0\"}],\"overflow\":3}"u8));
        Assert.Equal(2, p.Members.Count);
        Assert.Equal(3, p.Overflow);
        Assert.Equal("black0", p.Members[1].Variant);

        var c = Assert.IsType<ChatServerMessage>(MessageCodec.Decode("{\"t\":\"chat\",\"fromId\":\"a\",\"name\":\"A\",\"text\":\"moo\",\"ts\":5}"u8));
        Assert.Equal("moo", c.Chat.Text);

        var e = Assert.IsType<ErrorMessage>(MessageCodec.Decode("{\"t\":\"error\",\"code\":\"version\",\"message\":\"x\"}"u8));
        Assert.Equal("version", e.Code);

        Assert.IsType<PongMessage>(MessageCodec.Decode("{\"t\":\"pong\"}"u8));
        var u = Assert.IsType<UnknownMessage>(MessageCodec.Decode("{\"t\":\"typing\",\"x\":1}"u8));
        Assert.Equal("typing", u.Type);
    }

    [Fact]
    public void Malformed_frames_decode_to_null_without_throwing()
    {
        Assert.Null(MessageCodec.Decode("{ nope"u8));
        Assert.Null(MessageCodec.Decode("[1,2,3]"u8));
        Assert.Null(MessageCodec.Decode("{\"noType\":1}"u8));
        Assert.Null(MessageCodec.Decode("{\"t\":5}"u8));
        Assert.Null(MessageCodec.Decode(""u8));
        Assert.Null(MessageCodec.Decode(new byte[] { 0xff, 0xfe, 0x00 }));
        // Wrong field types fall back to defaults rather than failing.
        var p = Assert.IsType<PresenceMessage>(MessageCodec.Decode("{\"t\":\"presence\",\"members\":\"nope\",\"overflow\":\"x\"}"u8));
        Assert.Empty(p.Members);
        Assert.Equal(0, p.Overflow);
    }
}

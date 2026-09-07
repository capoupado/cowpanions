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
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
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

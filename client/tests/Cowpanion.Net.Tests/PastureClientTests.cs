using System.Text.Json;
using Cowpanion.Net.Protocol;

namespace Cowpanion.Net.Tests;

public class PastureClientTests
{
    private static readonly TimeSpan T = TimeSpan.FromSeconds(10);

    private static PastureClientOptions Options(Uri uri, TimeSpan? ping = null) => new()
    {
        ServerUrl = uri,
        ClientId = "a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5",
        Pasture = "commons",
        DisplayName = "Tester",
        Variant = "brown",
        PingInterval = ping ?? TimeSpan.FromSeconds(20),
    };

    private static bool IsPing(TimeSpan d) => Math.Abs(d.TotalSeconds - 20) < 0.001;

    private static async Task<ServerSession> HandshakeAsync(LoopbackServer server)
    {
        var session = await server.NextSessionAsync();
        string? hello = await session.ReceiveTextAsync();
        Assert.NotNull(hello);
        return session;
    }

    [Fact]
    public async Task Hello_is_the_first_frame_and_well_formed()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        client.Start();

        var session = await server.NextSessionAsync();
        string? first = await session.ReceiveTextAsync();
        Assert.NotNull(first);
        Assert.True(first.Length <= ProtocolConstants.MaxFrameBytes);
        using var doc = JsonDocument.Parse(first);
        var root = doc.RootElement;
        Assert.Equal("hello", root.GetProperty("t").GetString());
        Assert.Equal(2, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5", root.GetProperty("clientId").GetString());
        Assert.Equal("commons", root.GetProperty("pasture").GetString());
        Assert.Equal("Tester", root.GetProperty("displayName").GetString());
        Assert.Equal("brown", root.GetProperty("variant").GetString());
    }

    [Fact]
    public async Task Welcome_presence_and_chat_raise_events()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        var states = new List<ConnectionState>();
        var presence = new TaskCompletionSource<PresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += s => { lock (states) { states.Add(s); } };
        client.MembersChanged += p => presence.TrySetResult(p);
        client.ChatReceived += c => chat.TrySetResult(c);
        client.Start();

        var session = await HandshakeAsync(server);
        await session.SendTextAsync("{\"t\":\"welcome\",\"protocolVersion\":1,\"yourId\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"pasture\":\"commons\",\"visibleCap\":12,\"serverTime\":1}");
        await session.SendTextAsync("{\"t\":\"presence\",\"members\":[{\"id\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"name\":\"Tester\",\"variant\":\"brown\"},{\"id\":\"other\",\"name\":\"Bob\",\"variant\":\"white0\"}],\"overflow\":1}");
        await session.SendTextAsync("{\"t\":\"chat\",\"fromId\":\"other\",\"name\":\"Bob\",\"text\":\"morning\",\"ts\":1757260800000}");

        var p = await presence.Task.WaitAsync(T);
        Assert.Equal(2, p.Members.Count);
        Assert.Equal(1, p.Overflow);
        var c = await chat.Task.WaitAsync(T);
        Assert.Equal("Bob", c.Name);
        Assert.Equal("morning", c.Text);
        Assert.Equal("a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5", client.YourId);
        Assert.Equal(ConnectionState.Connected, client.State);
        lock (states)
        {
            Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Connected }, states);
        }
    }

    [Fact]
    public async Task Ping_is_sent_every_20_seconds_of_clock_time()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        client.Start();
        var session = await HandshakeAsync(server);

        for (int i = 0; i < 3; i++)
        {
            var wait = await clock.NextDelayAsync(IsPing);
            Assert.Equal(TimeSpan.FromSeconds(20), wait.Duration);
            clock.Advance(TimeSpan.FromSeconds(20));
            wait.Release();
            string? frame = await session.ReceiveTextAsync();
            Assert.Equal("{\"t\":\"ping\"}", frame);
            await session.SendTextAsync("{\"t\":\"pong\"}");
        }
    }

    [Fact]
    public async Task Silent_server_is_detected_via_receive_timeout()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        var options = Options(server.Uri) with { ReceiveTimeout = TimeSpan.FromSeconds(75) };
        await using var client = new PastureClient(options, clock, new Random(1));
        client.Start();
        var session = await HandshakeAsync(server);

        // Three unanswered pings are fine (60s < 75s)...
        for (int i = 0; i < 3; i++)
        {
            var wait = await clock.NextDelayAsync(IsPing);
            clock.Advance(TimeSpan.FromSeconds(20));
            wait.Release();
            Assert.Equal("{\"t\":\"ping\"}", await session.ReceiveTextAsync());
        }
        // ...but at 80s of silence the client must give up on the socket and back off instead of pinging again.
        var fourth = await clock.NextDelayAsync(IsPing);
        clock.Advance(TimeSpan.FromSeconds(20));
        fourth.Release();
        var backoff = await clock.NextDelayAsync(d => !IsPing(d));
        Assert.InRange(backoff.Duration.TotalSeconds, 1.5, 2.5);
        Assert.Equal(ConnectionState.BackingOff, client.State);
    }

    [Fact]
    public async Task Backoff_doubles_from_2s_to_60s_with_jitter_within_25_percent()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        var options = Options(server.Uri, ping: TimeSpan.FromHours(1));
        await using var client = new PastureClient(options, clock, new Random(1));
        client.Start();

        double[] expected = { 2, 4, 8, 16, 32, 60, 60 };
        var observed = new List<double>();
        for (int i = 0; i < expected.Length; i++)
        {
            var session = await HandshakeAsync(server);
            await session.CloseAsync(1001, "restart");
            var wait = await clock.NextDelayAsync(d => d < TimeSpan.FromMinutes(30));
            observed.Add(wait.Duration.TotalSeconds);
            Assert.InRange(wait.Duration.TotalSeconds, expected[i] * 0.75 - 0.001, expected[i] * 1.25 + 0.001);
            Assert.Equal(ConnectionState.BackingOff, client.State);
            wait.Release();
        }
        // Jitter must actually be applied: at least one value differs from its nominal base.
        bool anyJittered = false;
        for (int i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(observed[i] - expected[i]) > 0.01)
            {
                anyJittered = true;
            }
        }
        Assert.True(anyJittered, "backoff values were exactly nominal: " + string.Join(", ", observed));
    }

    [Fact]
    public async Task Backoff_resets_after_a_welcomed_session()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        var options = Options(server.Uri, ping: TimeSpan.FromHours(1));
        await using var client = new PastureClient(options, clock, new Random(1));
        client.Start();

        for (int i = 0; i < 3; i++)
        {
            var s = await HandshakeAsync(server);
            await s.CloseAsync(1001);
            (await clock.NextDelayAsync(d => d < TimeSpan.FromMinutes(30))).Release();
        }
        var good = await HandshakeAsync(server);
        await good.SendTextAsync("{\"t\":\"welcome\",\"protocolVersion\":1,\"yourId\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"pasture\":\"commons\",\"visibleCap\":12,\"serverTime\":1}");
        await Task.Delay(100);
        await good.CloseAsync(1000);
        var wait = await clock.NextDelayAsync(d => d < TimeSpan.FromMinutes(30));
        Assert.InRange(wait.Duration.TotalSeconds, 1.5, 2.5);
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(4003)]
    public async Task Terminal_close_codes_stop_reconnecting(int code)
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        var options = Options(server.Uri, ping: TimeSpan.FromHours(1));
        await using var client = new PastureClient(options, clock, new Random(1));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += s => { if (s == ConnectionState.Stopped) { stopped.TrySetResult(); } };
        client.Start();

        var session = await HandshakeAsync(server);
        if (code == 4000)
        {
            await session.SendTextAsync("{\"t\":\"error\",\"code\":\"version\",\"message\":\"protocol version mismatch\"}");
        }
        await session.CloseAsync(code, "terminal");

        await stopped.Task.WaitAsync(T);
        Assert.Equal(ConnectionState.Stopped, client.State);
        Assert.NotNull(client.StopReason);
        Assert.False(await clock.DelayRequestedWithinAsync(TimeSpan.FromMilliseconds(700), d => d < TimeSpan.FromMinutes(30)), "client scheduled a reconnect after a terminal close");
        Assert.False(await server.SessionArrivesWithinAsync(TimeSpan.FromMilliseconds(500)), "client reconnected after a terminal close");
        Assert.Equal(1, server.AcceptedCount);
    }

    [Theory]
    [InlineData(4001)]
    [InlineData(4002)]
    public async Task Pasture_full_and_abuse_use_the_long_backoff(int code)
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        var options = Options(server.Uri, ping: TimeSpan.FromHours(1));
        await using var client = new PastureClient(options, clock, new Random(1));
        client.Start();

        var session = await HandshakeAsync(server);
        await session.CloseAsync(code);
        var wait = await clock.NextDelayAsync(d => d < TimeSpan.FromMinutes(30));
        Assert.InRange(wait.Duration.TotalMinutes, 5 * 0.75 - 0.001, 5 * 1.25 + 0.001);
        Assert.Equal(ConnectionState.BackingOff, client.State);
    }

    [Fact]
    public async Task Unknown_types_and_garbage_are_ignored_and_the_session_survives()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        var presence = new TaskCompletionSource<PresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int emptyPresences = 0;
        client.MembersChanged += p =>
        {
            if (p.Members.Count == 0)
            {
                Interlocked.Increment(ref emptyPresences);
            }
            else
            {
                presence.TrySetResult(p);
            }
        };
        client.Start();

        var session = await HandshakeAsync(server);
        await session.SendTextAsync("{\"t\":\"typing\",\"who\":\"x\"}");
        await session.SendTextAsync("{ definitely not json");
        await session.SendTextAsync("[1,2,3]");
        await session.SendTextAsync("{\"t\":\"chat\"}");
        await session.SendTextAsync("{\"t\":\"presence\",\"members\":\"wrong\"}");
        await session.SendBinaryAsync(new byte[] { 1, 2, 3, 4 });
        await session.SendTextAsync("{\"t\":\"junk\",\"pad\":\"" + new string('x', 20_000) + "\"}");
        await session.SendTextAsync("{\"t\":\"presence\",\"members\":[{\"id\":\"z\",\"name\":\"Z\",\"variant\":\"brown\"}],\"overflow\":0}");

        var p = await presence.Task.WaitAsync(T);
        Assert.Single(p.Members);
        Assert.Equal("z", p.Members[0].Id);
        Assert.Equal(1, emptyPresences); // the wrong-typed members list decoded to an empty (harmless) presence
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task Chat_is_sent_as_a_chat_frame_and_refused_when_disconnected()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        Assert.False(await client.SendChatAsync("too early"));
        client.Start();
        var session = await HandshakeAsync(server);
        await session.SendTextAsync("{\"t\":\"welcome\",\"protocolVersion\":1,\"yourId\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"pasture\":\"commons\",\"visibleCap\":12,\"serverTime\":1}");
        await Task.Delay(50);
        Assert.True(await client.SendChatAsync("morning 😀"));
        string? frame = await session.ReceiveTextAsync();
        Assert.NotNull(frame);
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("chat", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal("morning 😀", doc.RootElement.GetProperty("text").GetString());
        Assert.False(await client.SendChatAsync("   "));
    }

    [Fact]
    public async Task Emote_is_sent_as_a_chat_frame_without_text()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        Assert.False(await client.SendChatAsync("", "", ""));
        client.Start();
        var session = await HandshakeAsync(server);
        await session.SendTextAsync("{\"t\":\"welcome\",\"protocolVersion\":2,\"yourId\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"pasture\":\"commons\",\"visibleCap\":12,\"serverTime\":1}");
        await Task.Delay(50);
        Assert.True(await client.SendChatAsync("", "jump", ""));
        string? frame = await session.ReceiveTextAsync();
        Assert.NotNull(frame);
        Assert.Contains("\"emote\":\"jump\"", frame);
        Assert.DoesNotContain("\"text\"", frame);
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("chat", doc.RootElement.GetProperty("t").GetString());

        Assert.True(await client.SendChatAsync("", "", "❤️"));
        string? reactionFrame = await session.ReceiveTextAsync();
        Assert.NotNull(reactionFrame);
        using var doc2 = JsonDocument.Parse(reactionFrame);
        Assert.Equal("❤️", doc2.RootElement.GetProperty("reaction").GetString());
        Assert.False(doc2.RootElement.TryGetProperty("text", out _));

        // An unknown emote name is dropped; with nothing else to send the call refuses.
        Assert.False(await client.SendChatAsync("", "dance", ""));
    }

    [Fact]
    public async Task Welcome_with_protocol_version_1_is_accepted()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        await using var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        var lines = new List<string>();
        client.Log += l => { lock (lines) { lines.Add(l); } };
        var presence = new TaskCompletionSource<PresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MembersChanged += p => presence.TrySetResult(p);
        client.Start();
        var session = await HandshakeAsync(server);
        await session.SendTextAsync("{\"t\":\"welcome\",\"protocolVersion\":1,\"yourId\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"pasture\":\"commons\",\"visibleCap\":12,\"serverTime\":1}");
        await session.SendTextAsync("{\"t\":\"presence\",\"members\":[{\"id\":\"a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5\",\"name\":\"Tester\",\"variant\":\"brown\"}],\"overflow\":0}");
        await presence.Task.WaitAsync(T);
        Assert.Equal(ConnectionState.Connected, client.State);
        lock (lines)
        {
            Assert.Contains(lines, l => l.StartsWith("welcome: v1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Dispose_sends_bye_and_closes_cleanly()
    {
        await using var server = new LoopbackServer();
        var clock = new ManualClock();
        var client = new PastureClient(Options(server.Uri), clock, new Random(1));
        client.Start();
        var session = await HandshakeAsync(server);
        await client.DisposeAsync();
        string? bye = await session.ReceiveTextAsync();
        Assert.Equal("{\"t\":\"bye\"}", bye);
        string? closed = await session.ReceiveTextAsync();
        Assert.Null(closed);
    }

    [Fact]
    public async Task Unreachable_server_backs_off_instead_of_failing()
    {
        var clock = new ManualClock();
        var options = Options(new Uri("ws://127.0.0.1:1/ws"), ping: TimeSpan.FromHours(1));
        await using var client = new PastureClient(options, clock, new Random(1));
        client.Start();
        var wait = await clock.NextDelayAsync(d => d < TimeSpan.FromMinutes(30));
        Assert.InRange(wait.Duration.TotalSeconds, 1.5, 2.5);
        Assert.Equal(ConnectionState.BackingOff, client.State);
    }
}

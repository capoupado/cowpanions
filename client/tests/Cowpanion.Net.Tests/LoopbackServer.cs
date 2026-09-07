using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Cowpanion.Net.Tests;

/// <summary>Minimal WebSocket server on HttpListener for driving PastureClient on 127.0.0.1.</summary>
public sealed class LoopbackServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ServerSession> _sessions = Channel.CreateUnbounded<ServerSession>();
    private readonly Task _acceptLoop;

    public LoopbackServer()
    {
        int port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Uri = new Uri($"ws://127.0.0.1:{port}/ws");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri Uri { get; }

    public int AcceptedCount { get; private set; }

    /// <summary>Waits for the next client connection to be upgraded.</summary>
    public async Task<ServerSession> NextSessionAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await _sessions.Reader.ReadAsync(cts.Token);
    }

    /// <summary>True if a session arrives within the window (used to prove the client is NOT reconnecting).</summary>
    public async Task<bool> SessionArrivesWithinAsync(TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        try
        {
            await _sessions.Reader.WaitToReadAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                AcceptedCount++;
                await _sessions.Writer.WriteAsync(new ServerSession(wsCtx.WebSocket));
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }
}

public sealed class ServerSession
{
    private readonly byte[] _buffer = new byte[64 * 1024];

    public ServerSession(WebSocket socket)
    {
        Socket = socket;
    }

    public WebSocket Socket { get; }

    /// <summary>Next complete text frame from the client, or null if the client closed.</summary>
    public async Task<string?> ReceiveTextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        int used = 0;
        while (true)
        {
            var r = await Socket.ReceiveAsync(_buffer.AsMemory(used), cts.Token);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            used += r.Count;
            if (r.EndOfMessage)
            {
                return Encoding.UTF8.GetString(_buffer, 0, used);
            }
        }
    }

    public Task SendTextAsync(string json)
    {
        return Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public Task SendBinaryAsync(byte[] bytes)
    {
        return Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    public async Task CloseAsync(int code, string reason = "")
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, cts.Token);
        }
        catch (WebSocketException)
        {
        }
    }
}

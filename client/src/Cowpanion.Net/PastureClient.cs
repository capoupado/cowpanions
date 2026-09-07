using System.Net.WebSockets;
using System.Text;
using Cowpanion.Core.Simulation;
using Cowpanion.Net.Protocol;

namespace Cowpanion.Net;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    BackingOff,
    /// <summary>Terminal close (4000/4003). Stays here until the client is disposed and recreated with new config.</summary>
    Stopped,
}

/// <summary>Members as last reported by the server. The list always includes ourselves.</summary>
public sealed record PresenceSnapshot(IReadOnlyList<Member> Members, int Overflow);

/// <summary>
/// Pasture connection: connect, hello, heartbeat, backoff, events. No UI types, no Dispatcher; every event is
/// raised on a thread-pool thread and the App layer marshals. Startup never waits on the network: Start()
/// returns immediately.
/// </summary>
public sealed class PastureClient : IAsyncDisposable
{
    private readonly PastureClientOptions _options;
    private readonly IPastureClock _clock;
    private readonly Random _rng;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    // ClientWebSocket allows one outstanding SendAsync at a time; the heartbeat and chat sends must not overlap.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Task? _loop;
    private ClientWebSocket? _socket;
    private ConnectionState _state = ConnectionState.Disconnected;
    private long _lastReceiveMs;
    private int _attempt;
    private volatile bool _closing;

    public PastureClient(PastureClientOptions options, IPastureClock? clock = null, Random? rng = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _clock = clock ?? SystemPastureClock.Instance;
        _rng = rng ?? new Random();
    }

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Why the client stopped for good (4000/4003), for the tray hint. Null otherwise.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Our id as confirmed by the server's welcome (null before the first welcome).</summary>
    public string? YourId { get; private set; }

    public event Action<PresenceSnapshot>? MembersChanged;
    public event Action<ChatMessage>? ChatReceived;
    public event Action<ConnectionState>? ConnectionStateChanged;
    /// <summary>Diagnostic lines. Never contains chat text.</summary>
    public event Action<string>? Log;

    /// <summary>Starts the connection loop in the background and returns immediately.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Sends a text chat frame if connected. Returns false when not connected (nothing is queued).</summary>
    public Task<bool> SendChatAsync(string text)
    {
        return SendChatAsync(text, "", "");
    }

    /// <summary>
    /// Sends a v2 chat frame carrying any combination of text, emote (<c>moo</c>/<c>jump</c>/<c>spin</c>) and
    /// reaction (1–3 emoji). Returns false when nothing survives normalisation or when not connected; nothing is
    /// queued. Sends never overlap the heartbeat (one outstanding send at a time).
    /// </summary>
    public async Task<bool> SendChatAsync(string text, string emote, string reaction)
    {
        text = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        emote = ProtocolConstants.IsEmote(emote) ? emote : "";
        reaction = string.IsNullOrWhiteSpace(reaction) ? "" : reaction.Trim();
        if (text.Length == 0 && emote.Length == 0 && reaction.Length == 0)
        {
            return false;
        }
        if (text.Length > ProtocolConstants.MaxChatChars * 2)
        {
            // The server truncates grapheme-safely at 140; we only guard the frame size here.
            text = text.Substring(0, ProtocolConstants.MaxChatChars * 2);
        }
        if (reaction.Length > 64)
        {
            reaction = reaction.Substring(0, 64);
        }
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || State != ConnectionState.Connected)
        {
            return false;
        }
        try
        {
            await SendAsync(socket, MessageCodec.EncodeChat(text, emote, reaction), _stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Courtesy bye + clean close if connected, then stops the loop. Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        var socket = _socket;
        var loop = _loop;
        if (socket is not null && socket.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync(socket, MessageCodec.EncodeBye(), cts.Token).ConfigureAwait(false);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
            // Give the server a moment to answer the close handshake before we tear the socket down.
            if (loop is not null)
            {
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                }
            }
        }
        _stop.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
        _socket?.Dispose();
        _stop.Dispose();
    }

    // ------------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested && !_closing)
        {
            int? closeCode = null;
            bool welcomed = false;
            SetState(ConnectionState.Connecting);
            var socket = new ClientWebSocket();
            _socket = socket;
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(_options.ConnectTimeout);
                    await socket.ConnectAsync(_options.ServerUrl, connectCts.Token).ConfigureAwait(false);
                }

                // hello must be the first frame.
                await SendAsync(socket, MessageCodec.EncodeHello(_options.ClientId, _options.Pasture, _options.DisplayName, _options.Variant), ct).ConfigureAwait(false);
                _lastReceiveMs = _clock.NowMs;
                SetState(ConnectionState.Connected);
                Emit("connected to " + _options.ServerUrl);

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receive = ReceiveLoopAsync(socket, sessionCts.Token, () => welcomed = true);
                var heartbeat = HeartbeatLoopAsync(socket, sessionCts.Token);

                var finished = await Task.WhenAny(receive, heartbeat).ConfigureAwait(false);
                sessionCts.Cancel();
                try
                {
                    await Task.WhenAll(receive, heartbeat).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Individual loop failures are reported below via the finished task.
                }
                if (finished.IsFaulted && finished.Exception is not null)
                {
                    Emit("session ended: " + Describe(finished.Exception.GetBaseException()));
                }
                closeCode = (int?)socket.CloseStatus;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException)
            {
                Emit("connect failed: " + Describe(ex));
            }
            finally
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                    {
                    }
                }
                _socket = null;
                socket.Dispose();
            }

            if (ct.IsCancellationRequested || _closing)
            {
                break;
            }

            if (closeCode == ProtocolConstants.CloseVersionMismatch || closeCode == ProtocolConstants.CloseBanned)
            {
                StopReason = closeCode == ProtocolConstants.CloseVersionMismatch
                    ? "Protocol version mismatch (4000). Update Cowpanion. Running local-only."
                    : "This pasture has banned this client (4003). Running local-only.";
                Emit("terminal close " + closeCode + " — not reconnecting");
                SetState(ConnectionState.Stopped);
                return;
            }

            TimeSpan wait;
            if (closeCode == ProtocolConstants.ClosePastureFull || closeCode == ProtocolConstants.CloseRateLimitAbuse)
            {
                wait = Jitter(_options.LongBackoff);
                Emit($"close {closeCode} — long backoff {wait.TotalSeconds:F0}s");
            }
            else
            {
                if (welcomed)
                {
                    _attempt = 0;
                }
                wait = Jitter(BackoffFor(_attempt));
                _attempt++;
                Emit($"reconnecting in {wait.TotalSeconds:F1}s (attempt {_attempt}, close {closeCode?.ToString() ?? "none"})");
            }

            SetState(ConnectionState.BackingOff);
            try
            {
                await _clock.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        SetState(ConnectionState.Disconnected);
    }

    private TimeSpan BackoffFor(int attempt)
    {
        double ms = _options.MinBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20));
        return TimeSpan.FromMilliseconds(Math.Min(ms, _options.MaxBackoff.TotalMilliseconds));
    }

    private TimeSpan Jitter(TimeSpan baseValue)
    {
        double f = 1.0 + (_rng.NextDouble() * 2.0 - 1.0) * _options.JitterFraction;
        return TimeSpan.FromMilliseconds(baseValue.TotalMilliseconds * f);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _clock.Delay(_options.PingInterval, ct).ConfigureAwait(false);
            if (_clock.NowMs - _lastReceiveMs > _options.ReceiveTimeout.TotalMilliseconds)
            {
                throw new WebSocketException("no frames received for " + _options.ReceiveTimeout.TotalSeconds + "s; presuming the connection is dead");
            }
            await SendAsync(socket, MessageCodec.EncodePing(), ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct, Action onWelcome)
    {
        // Frames are at most 4096 bytes by contract; anything bigger is drained and dropped.
        var buffer = new byte[ProtocolConstants.MaxFrameBytes * 2];
        int used = 0;
        bool oversized = false;

        while (!ct.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            }
            catch (WebSocketException) when (socket.CloseStatus.HasValue)
            {
                return;
            }

            _lastReceiveMs = _clock.NowMs;

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Emit($"server closed: {(int?)socket.CloseStatus} {socket.CloseStatusDescription}");
                return;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Not part of the protocol; drain and ignore.
                used = 0;
                oversized = !result.EndOfMessage;
                continue;
            }

            used += result.Count;
            if (!result.EndOfMessage)
            {
                if (used >= buffer.Length)
                {
                    oversized = true;
                    used = 0;
                }
                continue;
            }

            if (oversized)
            {
                Emit("dropped oversized server frame");
                oversized = false;
                used = 0;
                continue;
            }

            Dispatch(buffer.AsSpan(0, used), onWelcome);
            used = 0;
        }
    }

    private void Dispatch(ReadOnlySpan<byte> frame, Action onWelcome)
    {
        ServerMessage? message = MessageCodec.Decode(frame);
        switch (message)
        {
            case null:
                Emit("ignored malformed server frame (" + frame.Length + " bytes)");
                break;
            case WelcomeMessage w:
                YourId = w.YourId;
                onWelcome();
                if (w.ProtocolVersion != 1 && w.ProtocolVersion != 2)
                {
                    Emit($"welcome: unexpected protocolVersion {w.ProtocolVersion} (continuing)");
                }
                Emit($"welcome: v{w.ProtocolVersion} pasture={w.Pasture} visibleCap={w.VisibleCap}");
                break;
            case PresenceMessage p:
                MembersChanged?.Invoke(new PresenceSnapshot(p.Members, p.Overflow));
                break;
            case ChatServerMessage c:
                ChatReceived?.Invoke(c.Chat);
                break;
            case ErrorMessage e:
                Emit($"server error: {e.Code} ({e.Message})");
                break;
            case PongMessage:
                break;
            case UnknownMessage u:
                Emit("ignored unknown message type '" + u.Type + "'");
                break;
        }
    }

    private async Task SendAsync(ClientWebSocket socket, byte[] payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetState(ConnectionState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }
        ConnectionStateChanged?.Invoke(state);
    }

    private void Emit(string line)
    {
        Log?.Invoke(line);
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        return sb.ToString();
    }
}

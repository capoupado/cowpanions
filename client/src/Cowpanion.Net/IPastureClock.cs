using System.Diagnostics;

namespace Cowpanion.Net;

/// <summary>
/// Time source for <see cref="PastureClient"/>: every wait (ping cadence, backoff, receive watchdog) goes
/// through here so tests can drive the client without waiting real seconds.
/// </summary>
public interface IPastureClock
{
    /// <summary>Monotonic milliseconds.</summary>
    long NowMs { get; }

    /// <summary>Completes after <paramref name="delay"/>; cancels with the token.</summary>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Real time.</summary>
public sealed class SystemPastureClock : IPastureClock
{
    public static SystemPastureClock Instance { get; } = new();

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public long NowMs => _stopwatch.ElapsedMilliseconds;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

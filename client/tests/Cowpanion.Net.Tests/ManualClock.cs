using System.Threading.Channels;

namespace Cowpanion.Net.Tests;

/// <summary>Clock whose delays only complete when the test releases them. Records every requested duration.</summary>
public sealed class ManualClock : IPastureClock
{
    private readonly Channel<PendingDelay> _requests = Channel.CreateUnbounded<PendingDelay>();
    private long _nowMs;

    public long NowMs => Interlocked.Read(ref _nowMs);

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _nowMs, (long)by.TotalMilliseconds);
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        var pending = new PendingDelay(delay);
        cancellationToken.Register(() => pending.Tcs.TrySetCanceled(cancellationToken));
        _requests.Writer.TryWrite(pending);
        return pending.Tcs.Task;
    }

    /// <summary>Waits for the next Delay request matching the filter (others are left pending, never completed).</summary>
    public async Task<PendingDelay> NextDelayAsync(Func<TimeSpan, bool>? filter = null, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var p = await _requests.Reader.ReadAsync(cts.Token);
            if (filter is null || filter(p.Duration))
            {
                return p;
            }
        }
    }

    /// <summary>True if a matching Delay is requested within the window.</summary>
    public async Task<bool> DelayRequestedWithinAsync(TimeSpan window, Func<TimeSpan, bool>? filter = null)
    {
        try
        {
            await NextDelayAsync(filter, window);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class PendingDelay
{
    public PendingDelay(TimeSpan duration)
    {
        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => Tcs.TrySetResult();
}

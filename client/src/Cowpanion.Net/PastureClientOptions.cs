namespace Cowpanion.Net;

public sealed record PastureClientOptions
{
    public required Uri ServerUrl { get; init; }
    public required string ClientId { get; init; }
    public required string Pasture { get; init; }
    public required string DisplayName { get; init; }
    public required string Variant { get; init; }

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>No frame from the server for this long → the socket is presumed dead (sleep/resume, Wi-Fi off).</summary>
    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan LongBackoff { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>±fraction applied to every backoff. Mandatory per protocol; tests can pin it.</summary>
    public double JitterFraction { get; init; } = 0.25;
}

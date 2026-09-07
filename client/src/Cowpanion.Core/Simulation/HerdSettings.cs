namespace Cowpanion.Core.Simulation;

/// <summary>Tunables for a <see cref="HerdSimulator"/>. All distances in DIPs, all times in seconds.</summary>
public sealed record HerdSettings
{
    /// <summary>Walk speed before the personality multiplier. Slow is correct, these are cows.</summary>
    public double BaseSpeedDips { get; init; } = 18.0;

    /// <summary>Rendered width of one cow (frame width × scale). Used for bounds and the separation gap.</summary>
    public double CowWidthDips { get; init; } = 96.0;

    /// <summary>Extra centre-to-centre distance beyond the cow width that separation enforces.</summary>
    public double SeparationMarginDips { get; init; } = 10.0;

    /// <summary>How fast separation may push a cow apart per second. Bounded so nothing ever jumps.</summary>
    public double MaxSeparationPushDipsPerSecond { get; init; } = 80.0;

    /// <summary>Radius within which cows count as "nearby" for cohesion.</summary>
    public double CohesionRadiusDips { get; init; } = 260.0;

    /// <summary>Herd idle time before cows may enter Sleep.</summary>
    public double SleepAfterIdleSeconds { get; init; } = 20 * 60;

    /// <summary>Cursor distance at which cows notice it.</summary>
    public double CursorNoticeRadiusDips { get; init; } = 90.0;

    /// <summary>Depth offset (Position.Y) of the back passing lane (<see cref="Cow.Lane"/> 1) relative to the front lane.</summary>
    public double LaneDepthDips { get; init; } = 14.0;

    /// <summary>How fast a cow slides between lanes. Bounded so a lane change is a glide, never a pop.</summary>
    public double LaneChangeDipsPerSecond { get; init; } = 40.0;

    /// <summary>Cursor speed above which nearby cows get startled into a short spooked walk.</summary>
    public double StartleCursorSpeedDipsPerSecond { get; init; } = 1500.0;

    /// <summary>Cows within this distance of a fast cursor are startled.</summary>
    public double StartleRadiusDips { get; init; } = 150.0;

    public double MinGapDips => CowWidthDips + SeparationMarginDips;
}

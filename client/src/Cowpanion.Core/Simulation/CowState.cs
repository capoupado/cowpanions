namespace Cowpanion.Core.Simulation;

public enum CowState
{
    Walk,
    Idle,
    Graze,
    Turn,
    LieDown,
    Sleep,
    Moo,
}

/// <summary>Where a cow is in its life on this strip. Cows never pop: they walk in and walk out.</summary>
public enum CowLifecycle
{
    /// <summary>Walking in from an edge; not yet subject to bounds or herd forces.</summary>
    Arriving,
    /// <summary>Normal grazing member of the herd.</summary>
    Present,
    /// <summary>Walking towards the nearest edge; removed once fully off-strip.</summary>
    Leaving,
}

/// <summary>
/// A short visual flourish. The simulator only times it and holds the cow still; the renderer draws the hop,
/// the flips or the moo row.
/// </summary>
public enum CowEmote
{
    None,
    Moo,
    Jump,
    Spin,
}

/// <summary>Emote durations in seconds, shared by the simulator (timing) and the renderer (animation curves).</summary>
public static class EmoteTiming
{
    public const double MooSeconds = 1.2;
    public const double JumpSeconds = 1.0;
    public const double SpinSeconds = 1.0;

    public static double Seconds(CowEmote emote) => emote switch
    {
        CowEmote.Moo => MooSeconds,
        CowEmote.Jump => JumpSeconds,
        CowEmote.Spin => SpinSeconds,
        _ => 0,
    };
}

namespace Cowpanion.Core.Simulation;

/// <summary>
/// Mutable simulation state for one cow. Plain fields on purpose: the renderer reads these every tick and
/// the simulator writes them every tick; property change notification would be pure overhead.
/// </summary>
public sealed class Cow
{
    /// <summary>Server member id, or null for local filler cows.</summary>
    public string? MemberId;

    public bool IsSelf;

    public string? DisplayName;

    /// <summary>Sprite variant (colour). Unknown variants are resolved to the manifest default by the renderer.</summary>
    public string Variant = "";

    /// <summary>Feet position in DIPs relative to the strip. Y is the ground line (0 for now).</summary>
    public Vec2 Position;

    /// <summary>-1 faces left (the source art's native direction), +1 faces right (rendered flipped).</summary>
    public int Facing = -1;

    public CowState State = CowState.Idle;

    public CowLifecycle Lifecycle = CowLifecycle.Present;

    public double StateElapsed;

    public double StateDuration;

    /// <summary>Seconds since the current animation started (renderer maps this to a frame via the manifest fps).</summary>
    public double AnimElapsed;

    /// <summary>0 or 1: which idle animation variant plays while idle.</summary>
    public int IdleVariant;

    public Personality Personality = new(1.0, 0.5, 0.5, 1.0);

    /// <summary>True while a speech bubble is showing for this cow. Biases the state machine toward Idle.</summary>
    public bool HasBubble;

    /// <summary>Set by the simulator for one tick when the cow enters Moo, so the app can play a sound.</summary>
    public bool MooTriggered;

    // --- internal bookkeeping (public fields for simplicity; not for renderer use) ---

    /// <summary>Speed multiplier applied to the current walk (spook = faster).</summary>
    public double WalkBoost = 1.0;

    /// <summary>Seconds this walking cow has been pushed back against its heading.</summary>
    public double BlockedTime;

    /// <summary>State to enter after Turn completes.</summary>
    public CowState AfterTurn = CowState.Walk;

    /// <summary>Cool-down before the cow can turn toward the cursor again.</summary>
    public double CursorTurnCooldown;

    /// <summary>Accumulates time toward the next once-a-second cursor reaction roll.</summary>
    public double CursorRollAccumulator;

    /// <summary>X after movement but before the separation pass this tick (push budget and blocked detection).</summary>
    public double PreSeparationX;

    public bool IsStationary => State != CowState.Walk && Lifecycle == CowLifecycle.Present;

    public override string ToString() => $"{DisplayName ?? "filler"} {State} x={Position.X:F0} f={Facing}";
}

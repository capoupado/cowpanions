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

    /// <summary>
    /// Feet position in DIPs relative to the strip. Only X moves; Y stays 0 (every cow stands on the same ground line).
    /// </summary>
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

    /// <summary>Set by the app's hit test via <see cref="HerdSimulator.SetHovered"/>: the cursor rests on this cow.</summary>
    public bool Hovered;

    /// <summary>Seconds the cursor has rested on this cow (0 when not hovered).</summary>
    public double HoverSeconds;

    /// <summary>Emote currently playing (visual only; the simulator holds the cow still for its duration).</summary>
    public CowEmote Emote;

    /// <summary>Seconds since the current emote started.</summary>
    public double EmoteElapsed;

    // --- internal bookkeeping (public fields for simplicity; not for renderer use) ---

    /// <summary>Speed multiplier applied to the current walk (spook = faster).</summary>
    public double WalkBoost = 1.0;

    /// <summary>State to enter after Turn completes.</summary>
    public CowState AfterTurn = CowState.Walk;

    /// <summary>Cool-down before the cow can turn toward the cursor again.</summary>
    public double CursorTurnCooldown;

    /// <summary>Accumulates time toward the next once-a-second cursor reaction roll.</summary>
    public double CursorRollAccumulator;

    /// <summary>X after movement but before the separation pass this tick (push budget).</summary>
    public double PreSeparationX;

    /// <summary>Destination X of the current wander; valid when <see cref="HasTarget"/>.</summary>
    public double TargetX;

    /// <summary>True while the walk (or the Turn leading into it) is heading for <see cref="TargetX"/>.</summary>
    public bool HasTarget;

    /// <summary>
    /// Seconds spent, this walk, standing at spots that turned out to be taken (or with no gap in sight) after the
    /// walk wanted to end on top of a resting cow. Past the budget the cow rests there and separation eases them apart.
    /// </summary>
    public double GapSeekSeconds;

    /// <summary>True while <see cref="TargetX"/> is the nearest free resting spot rather than a wander destination.</summary>
    public bool SeekingGap;

    /// <summary>Cool-down before a fast cursor can startle this cow again.</summary>
    public double StartleCooldown;

    /// <summary>Walk boost and duration to apply when the pending Turn resolves into Walk (0 duration = use the table).</summary>
    public double PendingWalkBoost = 1.0;

    public double PendingWalkDuration;

    /// <summary>
    /// A cow on the move (walking, or turning to walk on). It walks straight through other cows at ground level;
    /// separation only holds between cows that are not passing.
    /// </summary>
    public bool IsPassing => Lifecycle == CowLifecycle.Present
        && (State == CowState.Walk || (State == CowState.Turn && AfterTurn == CowState.Walk));

    public bool IsStationary => State != CowState.Walk && Lifecycle == CowLifecycle.Present;

    public override string ToString() => $"{DisplayName ?? "filler"} {State} x={Position.X:F0} f={Facing}";
}

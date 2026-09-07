namespace Cowpanion.Core.Simulation;

/// <summary>
/// Pure herd simulation for one strip. Deterministic given the seed and the sequence of calls; never reads the
/// clock. The tick path allocates nothing: cows live in a <see cref="List{T}"/> that only changes on spawn or
/// despawn, and scratch arrays are grown only when the herd grows.
/// </summary>
public sealed class HerdSimulator
{
    private readonly Random _rng;
    private readonly HerdSettings _settings;
    private readonly List<Cow> _cows = new();
    private readonly double[] _weights = new double[8];

    private int[] _order = new int[16];
    private double _width = 1000;
    private double _cowWidth;
    private double _sleepAfterIdleSeconds;
    private double _idleSeconds;
    private bool _everTicked;
    private int _overflow;

    private bool _cursorPresent;
    private double _cursorX;
    private bool _prevCursorPresent;
    private double _prevCursorX;

    /// <summary>
    /// Leaving cows trot at a fixed multiple of base speed, personality ignored, so an exit reads as intentional
    /// rather than a cow wandering off: 3.5 × 18 ≈ 63 DIPs/s, at most ~22 s from the middle of a 2560-DIP strip.
    /// </summary>
    private const double LeaveSpeedFactor = 3.5;

    /// <summary>A wander target counts as reached within this distance.</summary>
    private const double TargetReachDips = 8.0;

    /// <summary>Minimum wander distance as a fraction of the walkable width.</summary>
    private const double MinWanderFraction = 0.3;

    /// <summary>Extra clearance (beyond MinGap) before a back-lane cow drops back to the front lane; avoids flapping.</summary>
    private const double LaneReturnMarginDips = 8.0;

    /// <summary>
    /// Budget of extra walking a back-lane cow may spend standing at front-lane spots that turned out to be taken
    /// (or with no gap at all) before it gives up and rests in the back lane until the front clears. Walking toward
    /// an unfinished destination or a known gap does not count against it.
    /// </summary>
    private const double LaneExtendMaxSeconds = 6.0;

    private const double BlockedTurnSeconds = 0.35;
    private const int MaxBlockedTurnsPerJourney = 3;
    private const double HoverLookUpSeconds = 0.4;
    private const double StartleCooldownSeconds = 5.0;
    private const double SpookBoost = 2.2;

    private int _fillersCreated;
    private IReadOnlyList<string> _fillerVariants = Array.Empty<string>();

    public HerdSimulator(int seed, HerdSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _rng = new Random(seed);
        _settings = settings;
        _cowWidth = settings.CowWidthDips;
        _sleepAfterIdleSeconds = settings.SleepAfterIdleSeconds;
    }

    /// <summary>Rendered cow width in DIPs (frame width × scale). Changing it re-clamps positions; nothing teleports far.</summary>
    public double CowWidthDips => _cowWidth;

    public void SetCowWidth(double widthDips)
    {
        if (widthDips <= 0)
        {
            return;
        }
        _cowWidth = widthDips;
        SetBounds(_width);
    }

    /// <summary>Colour names filler cows may use. Each filler slot picks one deterministically from the seed and its index.</summary>
    public void SetFillerVariants(IReadOnlyList<string> variantNames)
    {
        ArgumentNullException.ThrowIfNull(variantNames);
        _fillerVariants = variantNames;
    }

    public void SetSleepAfterIdleSeconds(double seconds)
    {
        _sleepAfterIdleSeconds = Math.Max(1, seconds);
    }

    public IReadOnlyList<Cow> Cows => _cows;

    public HerdSettings Settings => _settings;

    public double WidthDips => _width;

    /// <summary>Members beyond the server's visible cap; rendered as a "+N" indicator, not as cows.</summary>
    public int Overflow => _overflow;

    /// <summary>Seconds of herd inactivity (no user activity notified). Drives Sleep eligibility.</summary>
    public double IdleSeconds => _idleSeconds;

    /// <summary>True when nothing is moving: no walkers, no arrivals, no departures.</summary>
    public bool AllStationary
    {
        get
        {
            for (int i = 0; i < _cows.Count; i++)
            {
                if (!_cows[i].IsStationary)
                {
                    return false;
                }
            }
            return true;
        }
    }

    public void SetBounds(double widthDips)
    {
        if (widthDips < _cowWidth * 2)
        {
            widthDips = _cowWidth * 2;
        }
        _width = widthDips;
        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            if (cow.Lifecycle == CowLifecycle.Present)
            {
                cow.Position.X = Math.Clamp(cow.Position.X, MinX, MaxX);
                if (cow.HasTarget)
                {
                    cow.TargetX = Math.Clamp(cow.TargetX, MinX, MaxX);
                }
            }
        }
    }

    /// <summary>Something happened on the machine (cursor moved, chat typed). Resets the sleep timer and wakes sleepers.</summary>
    public void NotifyActivity()
    {
        _idleSeconds = 0;
    }

    /// <summary>Cursor position in strip DIPs, or absent. Read-only awareness: cows look, follow or spook.</summary>
    public void SetCursor(double xDips, bool present)
    {
        if (present && (!_cursorPresent || Math.Abs(xDips - _cursorX) > 0.5))
        {
            _idleSeconds = 0;
        }
        _cursorPresent = present;
        _cursorX = xDips;
    }

    /// <summary>
    /// The cow the cursor rests on (the app does the hit test), or null. A relaxed cow that stays hovered looks up
    /// (idle2 row) until the cursor leaves; a walking cow finishes its walk first.
    /// </summary>
    public void SetHovered(Cow? cow)
    {
        for (int i = 0; i < _cows.Count; i++)
        {
            var c = _cows[i];
            bool hovered = ReferenceEquals(c, cow);
            if (!hovered)
            {
                c.HoverSeconds = 0;
            }
            c.Hovered = hovered;
        }
    }

    /// <summary>
    /// Starts an emote. Moo enters the Moo state (sets <see cref="Cow.MooTriggered"/>); Jump and Spin stop a walking
    /// cow and hold it still while the renderer draws the flourish. A new emote replaces the current one. Ignored
    /// for leaving cows.
    /// </summary>
    public void TriggerEmote(Cow cow, CowEmote emote)
    {
        ArgumentNullException.ThrowIfNull(cow);
        if (emote == CowEmote.None || cow.Lifecycle == CowLifecycle.Leaving)
        {
            return;
        }

        if (cow.Lifecycle == CowLifecycle.Present)
        {
            if (emote == CowEmote.Moo)
            {
                EnterState(cow, CowState.Moo);
            }
            else if (cow.State == CowState.Walk || cow.State == CowState.Turn)
            {
                EnterState(cow, CowState.Idle);
            }
        }
        // Arriving cows keep walking in; the emote is drawn on top of the walk.

        cow.Emote = emote;
        cow.EmoteElapsed = 0;
    }

    public void SetBubble(Cow cow, bool active)
    {
        ArgumentNullException.ThrowIfNull(cow);
        if (active && !cow.HasBubble && cow.State == CowState.Walk)
        {
            // Finish the current step soon so the bubble can be read.
            double remaining = cow.StateDuration - cow.StateElapsed;
            if (remaining > 0.6)
            {
                cow.StateDuration = cow.StateElapsed + 0.6;
            }
        }
        cow.HasBubble = active;
    }

    /// <summary>Finds the cow for a member id (null if absent).</summary>
    public Cow? FindMember(string memberId)
    {
        for (int i = 0; i < _cows.Count; i++)
        {
            if (_cows[i].MemberId == memberId)
            {
                return _cows[i];
            }
        }
        return null;
    }

    public Cow? FindSelf()
    {
        for (int i = 0; i < _cows.Count; i++)
        {
            if (_cows[i].IsSelf && _cows[i].Lifecycle != CowLifecycle.Leaving)
            {
                return _cows[i];
            }
        }
        return null;
    }

    /// <summary>Reconciles member cows with the full presence list. Filler cows are untouched (see <see cref="SetFillerCount"/>).</summary>
    public void SyncMembers(IReadOnlyList<Member> members, string selfId, int overflow)
    {
        ArgumentNullException.ThrowIfNull(members);
        _overflow = Math.Max(0, overflow);

        // Departures: member cows not in the list walk out.
        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            if (cow.MemberId is null)
            {
                continue;
            }
            bool stillHere = false;
            for (int j = 0; j < members.Count; j++)
            {
                if (members[j].Id == cow.MemberId)
                {
                    stillHere = true;
                    break;
                }
            }
            if (!stillHere && cow.Lifecycle != CowLifecycle.Leaving)
            {
                BeginLeaving(cow);
            }
        }

        // Arrivals and updates.
        for (int j = 0; j < members.Count; j++)
        {
            var m = members[j];
            var cow = FindMember(m.Id);
            if (cow is null)
            {
                cow = CreateCow(m.Id, Personality.FromMemberId(m.Id));
                Spawn(cow, walkIn: _everTicked);
            }
            else if (cow.Lifecycle == CowLifecycle.Leaving)
            {
                Recall(cow);
            }
            cow.DisplayName = m.Name;
            cow.Variant = m.Variant;
            cow.IsSelf = m.Id == selfId;
        }
    }

    /// <summary>Sets how many local filler cows (no member id) are on the strip. Member cows are untouched.</summary>
    public void SetFillerCount(int n)
    {
        if (n < 0)
        {
            n = 0;
        }

        int present = 0;
        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            if (cow.MemberId is null && cow.Lifecycle != CowLifecycle.Leaving)
            {
                present++;
            }
        }

        // Too many: send the surplus home, newest first.
        for (int i = _cows.Count - 1; i >= 0 && present > n; i--)
        {
            var cow = _cows[i];
            if (cow.MemberId is null && cow.Lifecycle != CowLifecycle.Leaving)
            {
                BeginLeaving(cow);
                present--;
            }
        }

        // Too few: first recall any that were on their way out, then spawn new ones.
        for (int i = 0; i < _cows.Count && present < n; i++)
        {
            var cow = _cows[i];
            if (cow.MemberId is null && cow.Lifecycle == CowLifecycle.Leaving)
            {
                Recall(cow);
                present++;
            }
        }

        while (present < n)
        {
            var cow = CreateCow(null, Personality.FromRandom(_rng));
            if (_fillerVariants.Count > 0)
            {
                ulong h = StableHash.Fnv1a64("filler|" + _fillersCreated.ToString(System.Globalization.CultureInfo.InvariantCulture));
                cow.Variant = _fillerVariants[(int)(h % (ulong)_fillerVariants.Count)];
            }
            _fillersCreated++;
            Spawn(cow, walkIn: _everTicked);
            present++;
        }
    }

    public void Tick(double deltaSeconds)
    {
        if (deltaSeconds <= 0)
        {
            return;
        }
        if (deltaSeconds > 0.25)
        {
            deltaSeconds = 0.25; // a long stall (sleep, debugger) must not fling cows across the screen
        }

        _everTicked = true;
        _idleSeconds += deltaSeconds;
        bool sleepAllowed = _idleSeconds >= _sleepAfterIdleSeconds;

        // Cursor speed from tick to tick; a fast sweep startles nearby cows.
        bool startle = false;
        if (_cursorPresent && _prevCursorPresent)
        {
            double speed = Math.Abs(_cursorX - _prevCursorX) / deltaSeconds;
            startle = speed > _settings.StartleCursorSpeedDipsPerSecond;
        }
        _prevCursorPresent = _cursorPresent;
        _prevCursorX = _cursorX;

        double meanX = 0;
        int presentCount = 0;
        for (int i = 0; i < _cows.Count; i++)
        {
            if (_cows[i].Lifecycle == CowLifecycle.Present)
            {
                meanX += _cows[i].Position.X;
                presentCount++;
            }
        }
        if (presentCount > 0)
        {
            meanX /= presentCount;
        }

        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            cow.MooTriggered = false;
            cow.AnimElapsed += deltaSeconds;
            cow.StateElapsed += deltaSeconds;
            if (cow.CursorTurnCooldown > 0)
            {
                cow.CursorTurnCooldown -= deltaSeconds;
            }
            if (cow.StartleCooldown > 0)
            {
                cow.StartleCooldown -= deltaSeconds;
            }
            if (cow.Emote != CowEmote.None)
            {
                cow.EmoteElapsed += deltaSeconds;
                if (cow.EmoteElapsed >= EmoteTiming.Seconds(cow.Emote))
                {
                    cow.Emote = CowEmote.None;
                    cow.EmoteElapsed = 0;
                }
            }
            cow.HoverSeconds = cow.Hovered ? cow.HoverSeconds + deltaSeconds : 0;

            if (!sleepAllowed && cow.State == CowState.Sleep)
            {
                EnterState(cow, CowState.LieDown);
            }

            switch (cow.Lifecycle)
            {
                case CowLifecycle.Arriving:
                    TickArriving(cow, deltaSeconds);
                    break;
                case CowLifecycle.Leaving:
                    TickLeaving(cow, deltaSeconds);
                    break;
                default:
                    if (startle)
                    {
                        Startle(cow);
                    }
                    TickPresent(cow, deltaSeconds, sleepAllowed, meanX);
                    break;
            }
        }

        for (int i = 0; i < _cows.Count; i++)
        {
            _cows[i].PreSeparationX = _cows[i].Position.X;
        }
        Separate(deltaSeconds, lane: 0);
        Separate(deltaSeconds, lane: 1);

        // Back-lane cows drop to the front lane as soon as it is clear around them (resting cows belong in front).
        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            if (cow.Lifecycle == CowLifecycle.Present && cow.Lane == 1 && LaneClearAround(cow, 0, _settings.MinGapDips + LaneReturnMarginDips))
            {
                cow.Lane = 0;
                cow.LaneExtendSeconds = 0;
                cow.LaneExtending = false;
            }
        }

        // Blocked walkers step into the back lane to pass; if that lane is taken, they turn around instead of moonwalking.
        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            if (cow.Lifecycle != CowLifecycle.Present || cow.State != CowState.Walk)
            {
                cow.BlockedTime = 0;
                continue;
            }
            double pushed = cow.Position.X - cow.PreSeparationX;
            bool againstHeading = (cow.Facing > 0 && pushed < -0.01) || (cow.Facing < 0 && pushed > 0.01);
            if (!againstHeading)
            {
                cow.BlockedTime = 0;
                continue;
            }
            cow.BlockedTime += deltaSeconds;
            if (cow.BlockedTime <= BlockedTurnSeconds)
            {
                continue;
            }
            cow.BlockedTime = 0;
            if (cow.Lane == 0 && LaneClearAround(cow, 1, _settings.MinGapDips))
            {
                cow.Lane = 1;
                cow.LaneExtendSeconds = 0;
                cow.LaneExtending = false;
            }
            else
            {
                bool retry = cow.HasTarget && !cow.LaneExtending && cow.TargetBlockedTurns < MaxBlockedTurnsPerJourney;
                cow.LaneExtending = false;
                cow.AfterTurn = CowState.Walk;
                EnterState(cow, CowState.Turn);
                if (retry)
                {
                    // Keep the destination: step back briefly, rest, and try again once the way has cleared.
                    cow.TargetBlockedTurns++;
                    cow.PendingWalkBoost = 1.0;
                    cow.PendingWalkDuration = 0.8 + _rng.NextDouble() * 0.8;
                }
                else
                {
                    cow.HasTarget = false;
                }
            }
        }

        // Depth follows the lane at a bounded rate: a lane change is a glide, never a pop.
        double maxStepY = _settings.LaneChangeDipsPerSecond * deltaSeconds;
        for (int i = 0; i < _cows.Count; i++)
        {
            var cow = _cows[i];
            double targetY = cow.Lane * _settings.LaneDepthDips;
            double dy = targetY - cow.Position.Y;
            if (dy > maxStepY)
            {
                dy = maxStepY;
            }
            else if (dy < -maxStepY)
            {
                dy = -maxStepY;
            }
            cow.Position.Y += dy;
        }

        // Despawn cows that have fully left. Iterate backwards; RemoveAt allocates nothing.
        for (int i = _cows.Count - 1; i >= 0; i--)
        {
            var cow = _cows[i];
            if (cow.Lifecycle == CowLifecycle.Leaving)
            {
                double w = _cowWidth;
                if (cow.Position.X < -w || cow.Position.X > _width + w)
                {
                    _cows.RemoveAt(i);
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------

    private double MinX => _cowWidth / 2;

    private double MaxX => _width - _cowWidth / 2;

    private Cow CreateCow(string? memberId, Personality personality)
    {
        return new Cow
        {
            MemberId = memberId,
            Personality = personality,
        };
    }

    private void Spawn(Cow cow, bool walkIn)
    {
        double w = _cowWidth;
        if (walkIn)
        {
            bool fromLeft = _rng.NextDouble() < 0.5;
            cow.Lifecycle = CowLifecycle.Arriving;
            cow.Position.X = fromLeft ? -w * 0.6 : _width + w * 0.6;
            cow.Facing = fromLeft ? 1 : -1;
            cow.WalkBoost = 1.0;
            EnterState(cow, CowState.Walk);
            cow.StateDuration = 1e9; // walk until inside; TickArriving hands over to the normal machine
        }
        else
        {
            // Startup: already there, with randomised state, phase and frame so the herd never looks stamped out.
            cow.Lifecycle = CowLifecycle.Present;
            cow.Position.X = MinX + _rng.NextDouble() * (MaxX - MinX);
            cow.Facing = _rng.NextDouble() < 0.5 ? -1 : 1;
            CowState initial = _rng.NextDouble() switch
            {
                < 0.35 => CowState.Idle,
                < 0.65 => CowState.Graze,
                < 0.9 => CowState.Walk,
                _ => CowState.LieDown,
            };
            EnterState(cow, initial);
            if (initial == CowState.Walk && PickWanderTarget(cow, cow.Position.X, social: false))
            {
                // Spawned in place: no Turn needed, the cow simply starts out facing its destination.
                cow.Facing = cow.TargetX < cow.Position.X ? -1 : 1;
            }
            cow.StateElapsed = _rng.NextDouble() * cow.StateDuration * 0.8;
            cow.AnimElapsed = _rng.NextDouble() * 4.0;
        }
        _cows.Add(cow);
        if (_order.Length < _cows.Count)
        {
            _order = new int[_cows.Count * 2];
        }
    }

    private void BeginLeaving(Cow cow)
    {
        cow.Lifecycle = CowLifecycle.Leaving;
        cow.HasBubble = false;
        cow.HasTarget = false;
        cow.Lane = 0;
        cow.LaneExtendSeconds = 0;
        cow.Emote = CowEmote.None;
        cow.EmoteElapsed = 0;
        bool leftIsNearer = cow.Position.X < _width / 2;
        int wanted = leftIsNearer ? -1 : 1;
        if (cow.Facing != wanted)
        {
            cow.AfterTurn = CowState.Walk;
            EnterState(cow, CowState.Turn);
        }
        else
        {
            EnterState(cow, CowState.Walk);
        }
        cow.WalkBoost = 1.0;
    }

    /// <summary>A leaving cow that came back before making it off-screen: turn around.</summary>
    private void Recall(Cow cow)
    {
        cow.Lifecycle = CowLifecycle.Present;
        cow.Position.X = Math.Clamp(cow.Position.X, MinX, MaxX);
        cow.AfterTurn = CowState.Walk;
        EnterState(cow, CowState.Turn);
    }

    private void EnterState(Cow cow, CowState state)
    {
        cow.State = state;
        cow.StateElapsed = 0;
        cow.AnimElapsed = 0;
        cow.StateDuration = TransitionTable.Duration(state, cow.Personality, _rng);
        if (state == CowState.Idle)
        {
            cow.IdleVariant = _rng.NextDouble() < 0.5 ? 0 : 1;
        }
        if (state == CowState.Moo)
        {
            cow.MooTriggered = true;
        }
        if (state != CowState.Walk)
        {
            cow.WalkBoost = 1.0;
        }
        if (state != CowState.Walk && state != CowState.Turn)
        {
            // A rest cancels a pending spooked walk. The wander destination survives: the cow ambles there over
            // several walks, pausing to graze on the way, which is what carries it across the whole strip.
            cow.PendingWalkDuration = 0;
            cow.PendingWalkBoost = 1.0;
        }
    }

    /// <summary>Enters Walk immediately with an explicit boost and duration (follow, spook, startle). No wander target.</summary>
    private void StartWalk(Cow cow, double boost, double duration)
    {
        EnterState(cow, CowState.Walk);
        cow.HasTarget = false;
        cow.WalkBoost = boost;
        cow.StateDuration = duration;
    }

    /// <summary>
    /// Picks a wander destination: anywhere on the strip, or (sociable cows, sometimes) near the herd, re-rolled a
    /// few times so it is at least <see cref="MinWanderFraction"/> of the strip away. Returns false when nothing sensible came out.
    /// </summary>
    private bool PickWanderTarget(Cow cow, double meanX, bool social)
    {
        double lo = MinX;
        double hi = MaxX;
        double span = hi - lo;
        if (span <= TargetReachDips)
        {
            cow.HasTarget = false;
            return false;
        }

        bool nearHerd = social && _rng.NextDouble() < 0.35 * cow.Personality.Sociability;
        if (nearHerd)
        {
            lo = Math.Max(MinX, meanX - _settings.CohesionRadiusDips);
            hi = Math.Min(MaxX, meanX + _settings.CohesionRadiusDips);
            if (hi <= lo)
            {
                lo = MinX;
                hi = MaxX;
            }
        }

        double minDistance = span * MinWanderFraction;
        double target = cow.Position.X;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            target = lo + _rng.NextDouble() * (hi - lo);
            if (Math.Abs(target - cow.Position.X) >= minDistance)
            {
                break;
            }
        }

        if (Math.Abs(target - cow.Position.X) <= TargetReachDips)
        {
            cow.HasTarget = false;
            return false;
        }
        cow.TargetX = target;
        cow.HasTarget = true;
        cow.LaneExtending = false;
        cow.TargetBlockedTurns = 0;
        return true;
    }

    private double WalkSpeed(Cow cow) => _settings.BaseSpeedDips * cow.Personality.SpeedMultiplier * cow.WalkBoost;

    /// <summary>True when no other present cow in <paramref name="lane"/> is within <paramref name="clearance"/> of this cow's X.</summary>
    private bool LaneClearAround(Cow cow, int lane, double clearance)
    {
        for (int i = 0; i < _cows.Count; i++)
        {
            var other = _cows[i];
            if (ReferenceEquals(other, cow) || other.Lifecycle != CowLifecycle.Present || other.Lane != lane)
            {
                continue;
            }
            if (Math.Abs(other.Position.X - cow.Position.X) < clearance)
            {
                return false;
            }
        }
        return true;
    }

    private void Startle(Cow cow)
    {
        if (cow.State == CowState.Sleep || cow.StartleCooldown > 0)
        {
            return;
        }
        double dx = _cursorX - cow.Position.X;
        if (Math.Abs(dx) >= _settings.StartleRadiusDips)
        {
            return;
        }
        cow.StartleCooldown = StartleCooldownSeconds;
        int away = dx < 0 ? 1 : -1;
        double duration = 0.8 + _rng.NextDouble() * 0.6;
        if (cow.Facing != away)
        {
            // Turn first, then bolt: the Turn resolves into a walk with these parameters.
            cow.HasTarget = false;
            cow.AfterTurn = CowState.Walk;
            EnterState(cow, CowState.Turn);
            cow.PendingWalkBoost = SpookBoost;
            cow.PendingWalkDuration = duration;
        }
        else
        {
            StartWalk(cow, SpookBoost, duration);
        }
    }

    private void TickArriving(Cow cow, double dt)
    {
        if (cow.State == CowState.Turn)
        {
            if (cow.StateElapsed >= cow.StateDuration)
            {
                cow.Facing = -cow.Facing;
                EnterState(cow, CowState.Walk);
                cow.StateDuration = 1e9;
            }
            return;
        }
        cow.Position.X += cow.Facing * WalkSpeed(cow) * dt;
        if (cow.Position.X >= MinX && cow.Position.X <= MaxX)
        {
            cow.Lifecycle = CowLifecycle.Present;
            // Hand over to the normal state machine with a short remaining walk.
            cow.StateDuration = 1.0 + _rng.NextDouble() * 3.0;
            cow.StateElapsed = 0;
        }
    }

    private void TickLeaving(Cow cow, double dt)
    {
        if (cow.State == CowState.Turn)
        {
            if (cow.StateElapsed >= cow.StateDuration)
            {
                cow.Facing = -cow.Facing;
                EnterState(cow, CowState.Walk);
            }
            return;
        }
        if (cow.State != CowState.Walk)
        {
            EnterState(cow, CowState.Walk);
        }
        cow.StateDuration = 1e9;
        cow.Position.X += cow.Facing * _settings.BaseSpeedDips * LeaveSpeedFactor * dt;
    }

    private void TickPresent(Cow cow, double dt, bool sleepAllowed, double meanX)
    {
        bool relaxed = cow.State == CowState.Idle || cow.State == CowState.Graze;
        bool lookingUp = cow.Hovered && cow.HoverSeconds >= HoverLookUpSeconds && relaxed;

        // Cursor awareness: look, occasionally follow or spook. Read-only; never touches input.
        if (_cursorPresent)
        {
            double dx = _cursorX - cow.Position.X;
            if (Math.Abs(dx) < _settings.CursorNoticeRadiusDips)
            {
                int toward = dx < 0 ? -1 : 1;
                if (relaxed && cow.Facing != toward && cow.CursorTurnCooldown <= 0)
                {
                    cow.AfterTurn = cow.State;
                    cow.CursorTurnCooldown = 6.0;
                    EnterState(cow, CowState.Turn);
                    relaxed = false;
                    lookingUp = false;
                }
                cow.CursorRollAccumulator += dt;
                if (cow.CursorRollAccumulator >= 1.0)
                {
                    cow.CursorRollAccumulator = 0;
                    double roll = _rng.NextDouble();
                    if (lookingUp)
                    {
                        // A cow being looked at holds the pose; no follow or spook.
                    }
                    else if (relaxed && roll < 0.03)
                    {
                        // follow
                        cow.Facing = toward;
                        StartWalk(cow, 1.0, 1.0 + _rng.NextDouble() * 1.5);
                    }
                    else if (roll < 0.05)
                    {
                        // spook
                        cow.Facing = -toward;
                        StartWalk(cow, SpookBoost, 1.2 + _rng.NextDouble());
                    }
                }
            }
        }

        // Hover: a relaxed cow looks up (idle2 row) and holds it while the cursor stays.
        if (lookingUp)
        {
            if (cow.State != CowState.Idle || cow.IdleVariant != 1)
            {
                EnterState(cow, CowState.Idle);
                cow.IdleVariant = 1;
            }
            if (cow.StateDuration < cow.StateElapsed + 0.5)
            {
                cow.StateDuration = cow.StateElapsed + 0.5;
            }
        }

        switch (cow.State)
        {
            case CowState.Walk:
            {
                double v = cow.Facing * WalkSpeed(cow);

                // Cohesion: weak drift toward the mean of the herd, scaled by sociability. Walkers only, so idle
                // cows never slide; capped so it never overrides the cow's own heading.
                double toMean = meanX - cow.Position.X;
                if (Math.Abs(toMean) < _settings.CohesionRadiusDips)
                {
                    double drift = Math.Clamp(toMean * 0.04, -2.0, 2.0) * cow.Personality.Sociability;
                    v += drift;
                }

                cow.Position.X += v * dt;

                if (cow.Position.X <= MinX)
                {
                    cow.Position.X = MinX;
                    if (cow.Facing < 0)
                    {
                        cow.HasTarget = false;
                        cow.AfterTurn = CowState.Walk;
                        EnterState(cow, CowState.Turn);
                        return;
                    }
                }
                else if (cow.Position.X >= MaxX)
                {
                    cow.Position.X = MaxX;
                    if (cow.Facing > 0)
                    {
                        cow.HasTarget = false;
                        cow.AfterTurn = CowState.Walk;
                        EnterState(cow, CowState.Turn);
                        return;
                    }
                }

                bool reached = cow.HasTarget && Math.Abs(cow.TargetX - cow.Position.X) <= TargetReachDips;
                if (reached)
                {
                    cow.HasTarget = false;
                }
                if (reached || cow.StateElapsed >= cow.StateDuration)
                {
                    bool overSomebody = cow.Lane == 1 && !LaneClearAround(cow, 0, _settings.MinGapDips + LaneReturnMarginDips);
                    if (overSomebody && cow.HasTarget && !cow.LaneExtending)
                    {
                        // The walk timed out mid-pass with the journey unfinished: carry on toward the destination.
                        // The drop back to lane 0 happens the tick the blocker is behind.
                        cow.StateDuration = cow.StateElapsed + 0.25;
                    }
                    else if (overSomebody && (cow.LaneExtendSeconds < LaneExtendMaxSeconds || (cow.LaneExtending && !reached)))
                    {
                        // Still in the passing lane over somebody: head for the nearest clear spot in front instead of
                        // stopping here (the drop back to lane 0 happens the tick it is clear). Arriving at a spot
                        // that got taken meanwhile, or finding none, burns a bounded budget of short extensions
                        // before the cow gives up and rests in the back lane until the front clears.
                        if (!(cow.LaneExtending && reached) && FindNearestFrontGap(cow, out double gapX)
                            && Math.Abs(gapX - cow.Position.X) > TargetReachDips)
                        {
                            cow.TargetX = gapX;
                            cow.HasTarget = true;
                            cow.LaneExtending = true;
                            int desired = gapX < cow.Position.X ? -1 : 1;
                            if (desired != cow.Facing)
                            {
                                cow.AfterTurn = CowState.Walk;
                                EnterState(cow, CowState.Turn);
                                return;
                            }
                            cow.StateDuration = cow.StateElapsed + Math.Abs(gapX - cow.Position.X) / WalkSpeed(cow) + 0.5;
                            break;
                        }
                        const double extend = 0.25;
                        cow.LaneExtending = false;
                        cow.StateDuration = cow.StateElapsed + extend;
                        cow.LaneExtendSeconds += extend;
                    }
                    else
                    {
                        Transition(cow, sleepAllowed, meanX);
                    }
                }
                break;
            }
            case CowState.Turn:
                if (cow.StateElapsed >= cow.StateDuration)
                {
                    cow.Facing = -cow.Facing;
                    CowState after = cow.AfterTurn;
                    cow.AfterTurn = CowState.Walk;
                    EnterState(cow, after);
                    if (after == CowState.Walk && cow.PendingWalkDuration > 0)
                    {
                        cow.WalkBoost = cow.PendingWalkBoost;
                        cow.StateDuration = cow.PendingWalkDuration;
                        cow.PendingWalkDuration = 0;
                        cow.PendingWalkBoost = 1.0;
                    }
                }
                break;
            default:
                if (cow.StateElapsed >= cow.StateDuration)
                {
                    if (cow.Emote != CowEmote.None)
                    {
                        // Hold still until the emote has played out.
                        cow.StateDuration = cow.StateElapsed + 0.1;
                    }
                    else
                    {
                        Transition(cow, sleepAllowed, meanX);
                    }
                }
                break;
        }
    }

    private void Transition(Cow cow, bool sleepAllowed, double meanX)
    {
        CowState next = TransitionTable.Next(cow, sleepAllowed, _rng, _weights);
        if (next == CowState.Turn)
        {
            // With a journey unfinished the turn is a glance around: the next walk turns back toward the destination.
            bool journey = cow.HasTarget && Math.Abs(cow.TargetX - cow.Position.X) > TargetReachDips;
            cow.AfterTurn = !journey && _rng.NextDouble() < 0.7 ? CowState.Walk : CowState.Idle;
            EnterState(cow, CowState.Turn);
            return;
        }
        if (next == CowState.Walk)
        {
            // Wander: keep an unfinished destination, otherwise pick one well away from here (sociable cows
            // sometimes aim near the herd), and face it. A Turn precedes the walk when it is behind the cow.
            cow.LaneExtendSeconds = 0;
            cow.LaneExtending = false;
            bool haveTarget = cow.HasTarget && Math.Abs(cow.TargetX - cow.Position.X) > TargetReachDips;
            if (cow.Lane == 1 && !LaneClearAround(cow, 0, _settings.MinGapDips + LaneReturnMarginDips)
                && FindNearestFrontGap(cow, out double frontX) && Math.Abs(frontX - cow.Position.X) > TargetReachDips)
            {
                // A cow that rested in the back lane comes forward first.
                cow.TargetX = frontX;
                cow.HasTarget = true;
                cow.LaneExtending = true;
                haveTarget = true;
            }
            if (!haveTarget)
            {
                haveTarget = PickWanderTarget(cow, meanX, social: true);
            }
            if (haveTarget)
            {
                int desired = cow.TargetX < cow.Position.X ? -1 : 1;
                if (desired != cow.Facing)
                {
                    cow.AfterTurn = CowState.Walk;
                    EnterState(cow, CowState.Turn);
                    return;
                }
            }
            else
            {
                bool nearLeft = cow.Position.X - MinX < _cowWidth;
                bool nearRight = MaxX - cow.Position.X < _cowWidth;
                if ((nearLeft && cow.Facing < 0) || (nearRight && cow.Facing > 0))
                {
                    cow.AfterTurn = CowState.Walk;
                    EnterState(cow, CowState.Turn);
                    return;
                }
            }
            EnterState(cow, CowState.Walk);
            return;
        }
        EnterState(cow, next);
    }

    /// <summary>Fills <see cref="_order"/> with the present cows of one lane sorted by X (insertion sort, no allocation). Returns the count.</summary>
    private int SortLane(int lane)
    {
        int n = 0;
        for (int i = 0; i < _cows.Count; i++)
        {
            if (_cows[i].Lifecycle == CowLifecycle.Present && _cows[i].Lane == lane)
            {
                _order[n++] = i;
            }
        }
        for (int i = 1; i < n; i++)
        {
            int key = _order[i];
            double kx = _cows[key].Position.X;
            int j = i - 1;
            while (j >= 0 && _cows[_order[j]].Position.X > kx)
            {
                _order[j + 1] = _order[j];
                j--;
            }
            _order[j + 1] = key;
        }
        return n;
    }

    /// <summary>
    /// Nearest X at which <paramref name="cow"/> could stand in the front lane without another front-lane cow
    /// within the return clearance. Prefers a spot ahead (no U-turn) unless the one behind is much closer.
    /// False only when the front lane is packed solid.
    /// </summary>
    private bool FindNearestFrontGap(Cow cow, out double gapX)
    {
        double clearance = _settings.MinGapDips + LaneReturnMarginDips;
        int n = SortLane(0);
        double x = cow.Position.X;
        double ahead = double.NaN, behind = double.NaN;
        double aheadDistance = double.MaxValue, behindDistance = double.MaxValue;

        // Free intervals for the cow's centre: before the first cow, between neighbours, after the last cow.
        double lo = MinX;
        for (int i = 0; i <= n; i++)
        {
            double hi = i < n ? _cows[_order[i]].Position.X - clearance : MaxX;
            if (hi >= lo)
            {
                double candidate = Math.Clamp(x, lo, hi);
                double distance = Math.Abs(candidate - x);
                bool isAhead = distance == 0 || (candidate > x) == (cow.Facing > 0);
                if (isAhead && distance < aheadDistance)
                {
                    aheadDistance = distance;
                    ahead = candidate;
                }
                else if (!isAhead && distance < behindDistance)
                {
                    behindDistance = distance;
                    behind = candidate;
                }
            }
            if (i < n)
            {
                lo = _cows[_order[i]].Position.X + clearance;
            }
        }

        bool useAhead = !double.IsNaN(ahead) && (double.IsNaN(behind) || aheadDistance <= behindDistance * 2.0 + _cowWidth);
        gapX = useAhead ? ahead : behind;
        return !double.IsNaN(gapX);
    }

    /// <summary>
    /// Hard separation for present cows in one lane: sort by X (insertion sort into a preallocated index array),
    /// sweep so neighbours are at least MinGap apart, keep everything inside the bounds. Displacement per tick is
    /// capped so no cow ever jumps; the constraint converges within a fraction of a second.
    /// </summary>
    private void Separate(double dt, int lane)
    {
        int n = SortLane(lane);
        if (n < 2)
        {
            return;
        }

        double gap = _cowWidth + _settings.SeparationMarginDips;
        double maxPush = _settings.MaxSeparationPushDipsPerSecond * dt;

        // Left to right: push right neighbours.
        for (int i = 1; i < n; i++)
        {
            var left = _cows[_order[i - 1]];
            var right = _cows[_order[i]];
            double need = left.Position.X + gap - right.Position.X;
            if (need > 0)
            {
                double half = Math.Min(need * 0.5, maxPush);
                right.Position.X += half;
                left.Position.X -= half;
            }
        }
        // Right to left: honour the right bound and push left neighbours.
        var last = _cows[_order[n - 1]];
        if (last.Position.X > MaxX)
        {
            last.Position.X = MaxX;
        }
        for (int i = n - 2; i >= 0; i--)
        {
            var left = _cows[_order[i]];
            var right = _cows[_order[i + 1]];
            double need = left.Position.X + gap - right.Position.X;
            if (need > 0)
            {
                left.Position.X -= Math.Min(need, maxPush);
            }
        }
        var first = _cows[_order[0]];
        if (first.Position.X < MinX)
        {
            first.Position.X = MinX;
        }
        for (int i = 1; i < n; i++)
        {
            var left = _cows[_order[i - 1]];
            var right = _cows[_order[i]];
            double need = left.Position.X + gap - right.Position.X;
            if (need > 0)
            {
                right.Position.X += Math.Min(need, maxPush);
            }
        }
        // One displacement budget per cow per tick, whatever the sweeps asked for.
        for (int i = 0; i < n; i++)
        {
            var c = _cows[_order[i]];
            double pre = c.PreSeparationX;
            c.Position.X = Math.Clamp(c.Position.X, pre - maxPush, pre + maxPush);
            c.Position.X = Math.Clamp(c.Position.X, MinX, MaxX);
        }
    }
}

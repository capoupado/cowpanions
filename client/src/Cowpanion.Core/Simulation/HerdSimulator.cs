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

    /// <summary>
    /// Leaving cows trot at a fixed multiple of base speed, personality ignored, so an exit reads as intentional
    /// rather than a cow wandering off: 3.5 × 18 ≈ 63 DIPs/s, at most ~22 s from the middle of a 2560-DIP strip.
    /// </summary>
    private const double LeaveSpeedFactor = 3.5;

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
                // Came back before making it off-screen: turn around.
                cow.Lifecycle = CowLifecycle.Present;
                cow.Position.X = Math.Clamp(cow.Position.X, MinX, MaxX);
                EnterState(cow, CowState.Turn);
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
                cow.Lifecycle = CowLifecycle.Present;
                cow.Position.X = Math.Clamp(cow.Position.X, MinX, MaxX);
                EnterState(cow, CowState.Turn);
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
                    TickPresent(cow, deltaSeconds, sleepAllowed, meanX);
                    break;
            }
        }

        for (int i = 0; i < _cows.Count; i++)
        {
            _cows[i].PreSeparationX = _cows[i].Position.X;
        }
        Separate(deltaSeconds);

        // Blocked walkers turn around instead of moonwalking against a neighbour.
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
            if (againstHeading)
            {
                cow.BlockedTime += deltaSeconds;
                if (cow.BlockedTime > 0.35)
                {
                    cow.BlockedTime = 0;
                    cow.AfterTurn = CowState.Walk;
                    EnterState(cow, CowState.Turn);
                }
            }
            else
            {
                cow.BlockedTime = 0;
            }
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
    }

    private double WalkSpeed(Cow cow) => _settings.BaseSpeedDips * cow.Personality.SpeedMultiplier * cow.WalkBoost;

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
        // Cursor awareness: look, occasionally follow or spook. Read-only; never touches input.
        if (_cursorPresent)
        {
            double dx = _cursorX - cow.Position.X;
            if (Math.Abs(dx) < _settings.CursorNoticeRadiusDips)
            {
                int toward = dx < 0 ? -1 : 1;
                bool relaxed = cow.State == CowState.Idle || cow.State == CowState.Graze;
                if (relaxed && cow.Facing != toward && cow.CursorTurnCooldown <= 0)
                {
                    cow.AfterTurn = cow.State;
                    cow.CursorTurnCooldown = 6.0;
                    EnterState(cow, CowState.Turn);
                }
                cow.CursorRollAccumulator += dt;
                if (cow.CursorRollAccumulator >= 1.0)
                {
                    cow.CursorRollAccumulator = 0;
                    double roll = _rng.NextDouble();
                    if (relaxed && roll < 0.03)
                    {
                        // follow
                        cow.Facing = toward;
                        EnterState(cow, CowState.Walk);
                        cow.StateDuration = 1.0 + _rng.NextDouble() * 1.5;
                    }
                    else if (roll < 0.05)
                    {
                        // spook
                        cow.Facing = -toward;
                        EnterState(cow, CowState.Walk);
                        cow.WalkBoost = 2.2;
                        cow.StateDuration = 1.2 + _rng.NextDouble();
                    }
                }
            }
        }

        switch (cow.State)
        {
            case CowState.Walk:
            {
                double v = cow.Facing * WalkSpeed(cow);

                // Cohesion: weak drift toward the mean of the herd, scaled by sociability. Walkers only, so idle
                // cows never slide.
                double toMean = meanX - cow.Position.X;
                if (Math.Abs(toMean) < _settings.CohesionRadiusDips)
                {
                    double drift = Math.Clamp(toMean * 0.04, -5.0, 5.0) * cow.Personality.Sociability;
                    v += drift;
                }

                cow.Position.X += v * dt;

                if (cow.Position.X <= MinX)
                {
                    cow.Position.X = MinX;
                    if (cow.Facing < 0)
                    {
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
                        cow.AfterTurn = CowState.Walk;
                        EnterState(cow, CowState.Turn);
                        return;
                    }
                }

                if (cow.StateElapsed >= cow.StateDuration)
                {
                    Transition(cow, sleepAllowed, meanX);
                }
                break;
            }
            case CowState.Turn:
                if (cow.StateElapsed >= cow.StateDuration)
                {
                    cow.Facing = -cow.Facing;
                    EnterState(cow, cow.AfterTurn);
                    cow.AfterTurn = CowState.Walk;
                }
                break;
            default:
                if (cow.StateElapsed >= cow.StateDuration)
                {
                    Transition(cow, sleepAllowed, meanX);
                }
                break;
        }
    }

    private void Transition(Cow cow, bool sleepAllowed, double meanX)
    {
        CowState next = TransitionTable.Next(cow, sleepAllowed, _rng, _weights);
        if (next == CowState.Turn)
        {
            cow.AfterTurn = _rng.NextDouble() < 0.7 ? CowState.Walk : CowState.Idle;
            EnterState(cow, CowState.Turn);
            return;
        }
        if (next == CowState.Walk && cow.State != CowState.Walk)
        {
            // Sociable cows tend to head toward the herd; a wall ahead means turn first.
            int toward = meanX < cow.Position.X ? -1 : 1;
            bool wantToward = _rng.NextDouble() < 0.5 * cow.Personality.Sociability;
            bool nearLeft = cow.Position.X - MinX < _cowWidth;
            bool nearRight = MaxX - cow.Position.X < _cowWidth;
            int desired = wantToward ? toward : cow.Facing;
            if (nearLeft && desired < 0)
            {
                desired = 1;
            }
            if (nearRight && desired > 0)
            {
                desired = -1;
            }
            if (desired != cow.Facing)
            {
                cow.AfterTurn = CowState.Walk;
                EnterState(cow, CowState.Turn);
                return;
            }
        }
        EnterState(cow, next);
    }

    /// <summary>
    /// Hard separation for present cows: sort by X (insertion sort into a preallocated index array), sweep so
    /// neighbours are at least MinGap apart, keep everything inside the bounds. Displacement per tick is
    /// capped so no cow ever jumps; the constraint converges within a fraction of a second.
    /// </summary>
    private void Separate(double dt)
    {
        int n = 0;
        for (int i = 0; i < _cows.Count; i++)
        {
            if (_cows[i].Lifecycle == CowLifecycle.Present)
            {
                _order[n++] = i;
            }
        }
        if (n < 2)
        {
            return;
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

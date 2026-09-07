using Cowpanion.Core.Simulation;

namespace Cowpanion.Core.Tests;

public class HerdSimulatorTests
{
    private const double Dt = 1.0 / 30.0;

    private static HerdSimulator NewSim(int seed, double width = 1600, int fillers = 4)
    {
        var sim = new HerdSimulator(seed, new HerdSettings());
        sim.SetBounds(width);
        sim.SetFillerCount(fillers);
        return sim;
    }

    [Fact]
    public void Cows_stay_inside_bounds_for_ten_minutes()
    {
        var sim = NewSim(42, width: 1200, fillers: 6);
        double half = sim.Settings.CowWidthDips / 2;
        int ticks = (int)(600 / Dt);
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick(Dt);
            foreach (var cow in sim.Cows)
            {
                if (cow.Lifecycle != CowLifecycle.Present)
                {
                    continue;
                }
                Assert.InRange(cow.Position.X, half - 0.001, 1200 - half + 0.001);
            }
        }
    }

    [Fact]
    public void States_change_over_time()
    {
        var sim = NewSim(7, fillers: 3);
        var seen = new HashSet<CowState>();
        for (int t = 0; t < 30 * 300; t++)
        {
            sim.Tick(Dt);
            foreach (var cow in sim.Cows)
            {
                seen.Add(cow.State);
            }
        }
        Assert.Contains(CowState.Walk, seen);
        Assert.Contains(CowState.Idle, seen);
        Assert.Contains(CowState.Graze, seen);
        Assert.Contains(CowState.Turn, seen);
        Assert.True(seen.Count >= 5, $"only saw {string.Join(",", seen)}");
    }

    [Fact]
    public void Sleep_is_only_reachable_after_the_idle_period()
    {
        var settings = new HerdSettings { SleepAfterIdleSeconds = 60 };
        var sim = new HerdSimulator(3, settings);
        sim.SetBounds(1600);
        sim.SetFillerCount(6);

        for (int t = 0; t < (int)(59 / Dt); t++)
        {
            sim.Tick(Dt);
            foreach (var cow in sim.Cows)
            {
                Assert.NotEqual(CowState.Sleep, cow.State);
            }
        }

        bool slept = false;
        for (int t = 0; t < (int)(900 / Dt) && !slept; t++)
        {
            sim.Tick(Dt);
            foreach (var cow in sim.Cows)
            {
                if (cow.State == CowState.Sleep)
                {
                    slept = true;
                }
            }
        }
        Assert.True(slept, "no cow ever slept once the herd was idle long enough");

        sim.NotifyActivity();
        sim.Tick(Dt);
        Assert.DoesNotContain(sim.Cows, c => c.State == CowState.Sleep);
    }

    [Fact]
    public void Fixed_seed_reproduces_identically()
    {
        var a = NewSim(1234, fillers: 5);
        var b = NewSim(1234, fillers: 5);
        for (int t = 0; t < 30 * 120; t++)
        {
            a.Tick(Dt);
            b.Tick(Dt);
            if (t == 900)
            {
                a.SetFillerCount(7);
                b.SetFillerCount(7);
            }
        }
        Assert.Equal(a.Cows.Count, b.Cows.Count);
        for (int i = 0; i < a.Cows.Count; i++)
        {
            Assert.Equal(a.Cows[i].Position.X, b.Cows[i].Position.X);
            Assert.Equal(a.Cows[i].Position.Y, b.Cows[i].Position.Y);
            Assert.Equal(a.Cows[i].Lane, b.Cows[i].Lane);
            Assert.Equal(a.Cows[i].State, b.Cows[i].State);
            Assert.Equal(a.Cows[i].Facing, b.Cows[i].Facing);
            Assert.Equal(a.Cows[i].AnimElapsed, b.Cows[i].AnimElapsed);
            Assert.Equal(a.Cows[i].Personality, b.Cows[i].Personality);
        }
    }

    [Fact]
    public void Different_seeds_differ()
    {
        var a = NewSim(1, fillers: 4);
        var b = NewSim(2, fillers: 4);
        bool differ = false;
        for (int i = 0; i < a.Cows.Count && !differ; i++)
        {
            differ = a.Cows[i].Position.X != b.Cows[i].Position.X;
        }
        Assert.True(differ);
    }

    [Fact]
    public void Personality_is_identical_for_the_same_member_across_simulators()
    {
        var members = new[] { new Member("a3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5", "Carlos", "brown") };
        var a = new HerdSimulator(10, new HerdSettings());
        var b = new HerdSimulator(9999, new HerdSettings());
        a.SetBounds(1000);
        b.SetBounds(2500);
        a.SyncMembers(members, "x", 0);
        b.SyncMembers(members, "x", 0);
        var ca = a.FindMember(members[0].Id)!;
        var cb = b.FindMember(members[0].Id)!;
        Assert.Equal(ca.Personality, cb.Personality);
        Assert.Equal(Personality.FromMemberId(members[0].Id), ca.Personality);

        var other = Personality.FromMemberId("b3f1c9e2b4d6f8a0c1e3b5d7f9a1c3e5");
        Assert.NotEqual(ca.Personality, other);
        Assert.InRange(ca.Personality.SpeedMultiplier, 0.7, 1.4);
        Assert.InRange(ca.Personality.Laziness, 0, 1);
        Assert.InRange(ca.Personality.Sociability, 0, 1);
        Assert.InRange(ca.Personality.ScaleJitter, 0.9, 1.1);
    }

    [Fact]
    public void SyncMembers_walks_new_members_in_and_departed_members_out_without_teleporting()
    {
        var sim = new HerdSimulator(5, new HerdSettings());
        sim.SetBounds(1400);
        var m1 = new Member("m1", "One", "brown");
        var m2 = new Member("m2", "Two", "black0");
        var m3 = new Member("m3", "Three", "white0");
        sim.SyncMembers(new[] { m1, m2 }, "m1", 0);
        Assert.Equal(2, sim.Cows.Count);
        Assert.True(sim.FindMember("m1")!.IsSelf);
        Assert.False(sim.FindMember("m2")!.IsSelf);

        for (int t = 0; t < 90; t++)
        {
            sim.Tick(Dt);
        }

        double maxStep = (sim.Settings.BaseSpeedDips * 1.4 * 2.2 + sim.Settings.MaxSeparationPushDipsPerSecond) * Dt + 0.01;

        // Arrival: the new cow exists immediately but starts off-strip and walks in.
        sim.SyncMembers(new[] { m1, m2, m3 }, "m1", 2);
        Assert.Equal(2, sim.Overflow);
        var c3 = sim.FindMember("m3")!;
        Assert.Equal(CowLifecycle.Arriving, c3.Lifecycle);
        Assert.True(c3.Position.X < 0 || c3.Position.X > 1400, $"arriving cow spawned inside the strip at {c3.Position.X}");

        var prev = new Dictionary<Cow, double>();
        bool arrived = false;
        for (int t = 0; t < 30 * 60 && !arrived; t++)
        {
            foreach (var c in sim.Cows)
            {
                prev[c] = c.Position.X;
            }
            sim.Tick(Dt);
            foreach (var c in sim.Cows)
            {
                Assert.True(Math.Abs(c.Position.X - prev[c]) <= maxStep, $"cow {c} jumped {Math.Abs(c.Position.X - prev[c]):F2} DIPs in one tick");
            }
            arrived = c3.Lifecycle == CowLifecycle.Present;
        }
        Assert.True(arrived, "arriving cow never reached the strip");

        // Departure: the cow remains, marked Leaving, and walks off before being removed.
        sim.SyncMembers(new[] { m1, m3 }, "m1", 0);
        var c2 = sim.FindMember("m2")!;
        Assert.Equal(CowLifecycle.Leaving, c2.Lifecycle);
        Assert.Equal(3, sim.Cows.Count);
        prev.Clear();
        bool removed = false;
        for (int t = 0; t < 30 * 120 && !removed; t++)
        {
            prev.Clear();
            foreach (var c in sim.Cows)
            {
                prev[c] = c.Position.X;
            }
            sim.Tick(Dt);
            foreach (var c in sim.Cows)
            {
                Assert.True(Math.Abs(c.Position.X - prev[c]) <= maxStep, $"cow {c} jumped during departure");
            }
            removed = sim.FindMember("m2") is null;
            if (!removed)
            {
                Assert.Equal(CowLifecycle.Leaving, c2.Lifecycle);
            }
        }
        Assert.True(removed, "departed cow never left the strip");
        Assert.Equal(2, sim.Cows.Count);
    }

    [Fact]
    public void Member_rejoining_while_leaving_turns_back()
    {
        var sim = new HerdSimulator(8, new HerdSettings());
        sim.SetBounds(1400);
        var m1 = new Member("m1", "One", "brown");
        sim.SyncMembers(new[] { m1 }, "self", 0);
        for (int t = 0; t < 30; t++)
        {
            sim.Tick(Dt);
        }
        sim.SyncMembers(Array.Empty<Member>(), "self", 0);
        for (int t = 0; t < 15; t++)
        {
            sim.Tick(Dt);
        }
        sim.SyncMembers(new[] { m1 }, "self", 0);
        Assert.Single(sim.Cows);
        Assert.Equal(CowLifecycle.Present, sim.Cows[0].Lifecycle);
    }

    [Fact]
    public void Filler_count_changes_walk_in_and_out()
    {
        var sim = NewSim(11, fillers: 2);
        for (int t = 0; t < 30; t++)
        {
            sim.Tick(Dt);
        }
        sim.SetFillerCount(4);
        Assert.Equal(4, sim.Cows.Count);
        Assert.Equal(2, sim.Cows.Count(c => c.Lifecycle == CowLifecycle.Arriving));
        sim.SetFillerCount(1);
        Assert.Equal(4, sim.Cows.Count);
        Assert.Equal(3, sim.Cows.Count(c => c.Lifecycle == CowLifecycle.Leaving));
        for (int t = 0; t < 30 * 120; t++)
        {
            sim.Tick(Dt);
        }
        Assert.Single(sim.Cows);
        Assert.Equal(CowLifecycle.Present, sim.Cows[0].Lifecycle);
    }

    [Fact]
    public void Six_cows_never_visually_overlap()
    {
        var sim = NewSim(21, width: 1920, fillers: 6);
        double cowWidth = sim.Settings.CowWidthDips;
        // Let the initial random placement settle (separation converges within a fraction of a second).
        for (int t = 0; t < 60; t++)
        {
            sim.Tick(Dt);
        }
        int restingTicks = 0;
        int restingFrontTicks = 0;
        for (int t = 0; t < 30 * 600; t++)
        {
            sim.Tick(Dt);
            var cows = sim.Cows;
            for (int i = 0; i < cows.Count; i++)
            {
                if (cows[i].Lifecycle == CowLifecycle.Present && cows[i].State != CowState.Walk)
                {
                    restingTicks++;
                    if (cows[i].Lane == 0)
                    {
                        restingFrontTicks++;
                    }
                }
                for (int j = i + 1; j < cows.Count; j++)
                {
                    if (cows[i].Lifecycle != CowLifecycle.Present || cows[j].Lifecycle != CowLifecycle.Present)
                    {
                        continue;
                    }
                    if (cows[i].Lane != cows[j].Lane)
                    {
                        continue; // the back lane passes behind the front lane by design
                    }
                    double gap = Math.Abs(cows[i].Position.X - cows[j].Position.X);
                    Assert.True(gap >= cowWidth - 0.001, $"tick {t}: cows {i} and {j} overlap in lane {cows[i].Lane} (gap {gap:F1} < {cowWidth})");
                }
            }
        }
        Assert.True(restingFrontTicks >= restingTicks * 0.95,
            $"resting cows were in the front lane only {restingFrontTicks}/{restingTicks} cow-ticks");
    }

    private static void AssertRoams(HerdSimulator sim, IReadOnlyList<Cow> tracked, double width, int seconds, bool requireReorder)
    {
        var minX = tracked.ToDictionary(c => c, c => c.Position.X);
        var maxX = tracked.ToDictionary(c => c, c => c.Position.X);
        var order = tracked.OrderBy(c => c.Position.X).ToList();
        bool reordered = false;
        for (int t = 0; t < (int)(seconds / Dt); t++)
        {
            sim.Tick(Dt);
            foreach (var c in tracked)
            {
                minX[c] = Math.Min(minX[c], c.Position.X);
                maxX[c] = Math.Max(maxX[c], c.Position.X);
            }
            if (!reordered && t % 30 == 0)
            {
                var now = tracked.OrderBy(c => c.Position.X).ToList();
                reordered = !now.SequenceEqual(order);
            }
        }
        foreach (var c in tracked)
        {
            double range = maxX[c] - minX[c];
            Assert.True(range > width * 0.5, $"cow {c} only covered {range:F0} of {width} DIPs ({minX[c]:F0}..{maxX[c]:F0})");
        }
        if (requireReorder)
        {
            Assert.True(reordered, "the left-to-right ordering of the cows never changed");
        }
    }

    [Fact]
    public void Cows_roam_the_whole_strip()
    {
        var sim = NewSim(10, width: 2560, fillers: 5);
        AssertRoams(sim, sim.Cows.ToList(), 2560, 600, requireReorder: true);
    }

    [Fact]
    public void Own_cow_roams_even_with_neighbours()
    {
        var sim = new HerdSimulator(31, new HerdSettings());
        sim.SetBounds(2560);
        var self = new Member("0123456789abcdef0123456789abcdef", "Me", "brown");
        sim.SyncMembers(new[] { self }, self.Id, 0);
        sim.SetFillerCount(4);
        var me = sim.FindSelf()!;
        AssertRoams(sim, new[] { me }, 2560, 600, requireReorder: false);
    }

    [Fact]
    public void Blocked_walker_passes_via_back_lane_and_returns()
    {
        var sim = NewSim(21, width: 1400, fillers: 6);
        double maxStepY = sim.Settings.LaneChangeDipsPerSecond * Dt + 0.01;
        var prevY = sim.Cows.ToDictionary(c => c, c => c.Position.Y);
        var reachedBack = new HashSet<Cow>();
        bool returned = false;
        for (int t = 0; t < 30 * 600; t++)
        {
            sim.Tick(Dt);
            foreach (var c in sim.Cows)
            {
                if (prevY.TryGetValue(c, out double y))
                {
                    Assert.True(Math.Abs(c.Position.Y - y) <= maxStepY, $"tick {t}: cow {c} moved {Math.Abs(c.Position.Y - y):F2} DIPs in depth in one tick");
                }
                prevY[c] = c.Position.Y;
                if (c.Lane == 1)
                {
                    reachedBack.Add(c);
                }
                else if (reachedBack.Contains(c))
                {
                    returned = true;
                }
                Assert.InRange(c.Position.Y, -0.001, sim.Settings.LaneDepthDips + 0.001);
            }
        }
        Assert.NotEmpty(reachedBack);
        Assert.True(returned, "a cow reached the back lane but never came back to the front");
    }

    [Fact]
    public void Hovered_cow_looks_up()
    {
        var sim = NewSim(12, width: 1600, fillers: 1);
        var cow = sim.Cows[0];
        sim.Tick(Dt);
        for (int t = 0; t < 30 * 120 && cow.State != CowState.Idle && cow.State != CowState.Graze; t++)
        {
            sim.Tick(Dt);
        }
        Assert.True(cow.State == CowState.Idle || cow.State == CowState.Graze, $"never relaxed: {cow}");

        sim.SetHovered(cow);
        Assert.True(cow.Hovered);
        for (int t = 0; t < 30; t++)
        {
            sim.Tick(Dt);
        }
        Assert.Equal(CowState.Idle, cow.State);
        Assert.Equal(1, cow.IdleVariant);

        // Holds the pose for as long as the cursor stays.
        for (int t = 0; t < 30 * 30; t++)
        {
            sim.Tick(Dt);
            Assert.Equal(CowState.Idle, cow.State);
            Assert.Equal(1, cow.IdleVariant);
        }

        sim.SetHovered(null);
        Assert.False(cow.Hovered);
        Assert.Equal(0, cow.HoverSeconds);
        bool moved = false;
        for (int t = 0; t < 30 * 60 && !moved; t++)
        {
            sim.Tick(Dt);
            moved = cow.State != CowState.Idle || cow.IdleVariant != 1;
        }
        Assert.True(moved, "cow kept looking up after the cursor left");
    }

    [Fact]
    public void Fast_cursor_startles_nearby_cows()
    {
        var sim = NewSim(9, width: 2560, fillers: 2);
        var near = sim.Cows[0];
        var far = sim.Cows[1];
        near.Position.X = 600;
        far.Position.X = 1400;
        near.State = CowState.Graze;
        far.State = CowState.Graze;
        near.StateElapsed = 0;
        far.StateElapsed = 0;
        near.StateDuration = 100;
        far.StateDuration = 100;
        near.Facing = -1;
        sim.Tick(Dt);

        // 3000 DIPs/s sweep from well left of the near cow to just short of it.
        double x = 100;
        sim.SetCursor(x, true);
        sim.Tick(Dt);
        for (int t = 0; t < 4; t++)
        {
            x += 3000 * Dt;
            sim.SetCursor(x, true);
            sim.Tick(Dt);
        }
        Assert.True(x < near.Position.X, "test sweep overshot the cow");
        Assert.True(near.StartleCooldown > 0, $"near cow was not startled: {near}");
        sim.SetCursor(x, false);

        bool bolted = false;
        for (int t = 0; t < 30 && !bolted; t++)
        {
            sim.Tick(Dt);
            bolted = near.State == CowState.Walk && near.WalkBoost > 1.0 && near.Facing == 1;
        }
        Assert.True(bolted, $"near cow did not bolt away from the cursor within a second: {near}");

        Assert.Equal(CowState.Graze, far.State);
        Assert.Equal(1.0, far.WalkBoost);
        Assert.Equal(0, far.StartleCooldown);
    }

    [Fact]
    public void Emote_times_out_and_holds_cow_still()
    {
        var sim = NewSim(3, width: 1600, fillers: 1);
        var cow = sim.Cows[0];
        for (int t = 0; t < 30 * 300 && cow.State != CowState.Walk; t++)
        {
            sim.Tick(Dt);
        }
        Assert.Equal(CowState.Walk, cow.State);

        sim.TriggerEmote(cow, CowEmote.Jump);
        Assert.Equal(CowEmote.Jump, cow.Emote);
        Assert.NotEqual(CowState.Walk, cow.State);
        double x = cow.Position.X;
        for (int t = 0; t < 30; t++)
        {
            sim.Tick(Dt);
            Assert.NotEqual(CowState.Walk, cow.State);
            Assert.Equal(CowEmote.Jump, cow.Emote);
        }
        Assert.InRange(cow.Position.X, x - 1, x + 1);
        for (int t = 0; t < 4; t++)
        {
            sim.Tick(Dt);
        }
        Assert.Equal(CowEmote.None, cow.Emote);

        sim.TriggerEmote(cow, CowEmote.Moo);
        Assert.Equal(CowState.Moo, cow.State);
        Assert.True(cow.MooTriggered);
        Assert.Equal(CowEmote.Moo, cow.Emote);
        sim.Tick(Dt);
        Assert.False(cow.MooTriggered);
        Assert.Equal(CowEmote.Moo, cow.Emote);

        // Spin replaces Moo; leaving cows ignore emotes.
        sim.TriggerEmote(cow, CowEmote.Spin);
        Assert.Equal(CowEmote.Spin, cow.Emote);
        Assert.Equal(0, cow.EmoteElapsed);
        sim.SetFillerCount(0);
        Assert.Equal(CowLifecycle.Leaving, cow.Lifecycle);
        Assert.Equal(CowEmote.None, cow.Emote);
        sim.TriggerEmote(cow, CowEmote.Jump);
        Assert.Equal(CowEmote.None, cow.Emote);
    }

    [Fact]
    public void Cows_both_cluster_and_spread_over_time()
    {
        var sim = NewSim(33, width: 1920, fillers: 6);
        double minSpread = double.MaxValue;
        double maxSpread = 0;
        for (int t = 0; t < 30 * 1200; t++)
        {
            sim.Tick(Dt);
            if (t % 30 != 0)
            {
                continue;
            }
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var c in sim.Cows)
            {
                lo = Math.Min(lo, c.Position.X);
                hi = Math.Max(hi, c.Position.X);
            }
            minSpread = Math.Min(minSpread, hi - lo);
            maxSpread = Math.Max(maxSpread, hi - lo);
        }
        Assert.True(maxSpread - minSpread > 300, $"spread never varied much: {minSpread:F0}..{maxSpread:F0}");
    }

    [Fact]
    public void Turn_state_precedes_every_facing_flip()
    {
        var sim = NewSim(77, width: 800, fillers: 3);
        var lastFacing = sim.Cows.ToDictionary(c => c, c => c.Facing);
        var lastState = sim.Cows.ToDictionary(c => c, c => c.State);
        for (int t = 0; t < 30 * 300; t++)
        {
            sim.Tick(Dt);
            foreach (var c in sim.Cows)
            {
                if (lastFacing.TryGetValue(c, out int f) && f != c.Facing)
                {
                    Assert.Equal(CowState.Turn, lastState[c]);
                }
                lastFacing[c] = c.Facing;
                lastState[c] = c.State;
            }
        }
    }

    [Fact]
    public void Bubble_biases_toward_idle()
    {
        var sim = NewSim(5, fillers: 1);
        var cow = sim.Cows[0];
        sim.SetBubble(cow, true);
        int walkTicks = 0;
        int total = 30 * 120;
        for (int t = 0; t < total; t++)
        {
            sim.Tick(Dt);
            if (cow.State == CowState.Walk)
            {
                walkTicks++;
            }
        }
        Assert.True(walkTicks < total / 5, $"cow with bubble walked {walkTicks}/{total} ticks");
    }

    [Fact]
    public void Large_delta_is_capped_so_nothing_flies()
    {
        var sim = NewSim(2, fillers: 3);
        var before = sim.Cows.Select(c => c.Position.X).ToArray();
        sim.Tick(30.0);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.True(Math.Abs(sim.Cows[i].Position.X - before[i]) < 40);
        }
    }

    [Fact]
    public void Tick_does_not_allocate_in_steady_state()
    {
        var sim = NewSim(99, fillers: 8);
        for (int t = 0; t < 300; t++)
        {
            sim.Tick(Dt);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 3000; t++)
        {
            sim.Tick(Dt);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(after - before == 0, $"Tick allocated {after - before} bytes over 3000 ticks");
    }
}

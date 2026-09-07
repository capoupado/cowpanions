using Cowpanion.Core.Simulation;

namespace Cowpanion.Core.Tests;

/// <summary>Replays the orchestrator's real call sequence and checks no cow ever strays far from the strip.</summary>
public class HerdLifecycleTests
{
    private const double Dt = 1.0 / 30.0;
    private const double Width = 2560;

    private static readonly Member Self = new("0123456789abcdef0123456789abcdef", "Me", "brown");
    private static readonly Member A = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", "black0");
    private static readonly Member B = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", "white0");

    private static HerdSimulator NewSim(int seed)
    {
        var sim = new HerdSimulator(seed, new HerdSettings { CowWidthDips = 96 });
        sim.SetCowWidth(96);
        sim.SetBounds(Width);
        return sim;
    }

    private static void RunAndCheck(HerdSimulator sim, double seconds, string phase)
    {
        double w = sim.Settings.CowWidthDips;
        int ticks = (int)(seconds / Dt);
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick(Dt);
            foreach (var cow in sim.Cows)
            {
                if (cow.Lifecycle == CowLifecycle.Present)
                {
                    Assert.True(cow.Position.X >= w / 2 - 0.001 && cow.Position.X <= Width - w / 2 + 0.001,
                        $"{phase}: present cow at X={cow.Position.X:F1} outside strip at t={t * Dt:F1}s");
                }
                else
                {
                    Assert.True(cow.Position.X >= -1.5 * w && cow.Position.X <= Width + 1.5 * w,
                        $"{phase}: {cow.Lifecycle} cow at X={cow.Position.X:F1} far off strip at t={t * Dt:F1}s (facing {cow.Facing}, state {cow.State})");
                }
            }
        }
    }

    private static void AssertSettled(HerdSimulator sim, int expectedCount, string phase)
    {
        Assert.Equal(expectedCount, sim.Cows.Count);
        foreach (var cow in sim.Cows)
        {
            Assert.True(cow.Lifecycle == CowLifecycle.Present, $"{phase}: cow still {cow.Lifecycle} at X={cow.Position.X:F1}, facing {cow.Facing}, state {cow.State}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Offline_online_offline_sequence_keeps_every_cow_near_the_strip(int seed)
    {
        var sim = NewSim(seed);

        // Startup: offline fallback herd before the first tick (spawned in place).
        sim.SyncMembers(Array.Empty<Member>(), "", 0);
        sim.SetFillerCount(4);
        RunAndCheck(sim, 10, "startup");
        AssertSettled(sim, 4, "startup");

        // Presence arrives: fillers walk out, three members walk in.
        sim.SetFillerCount(0);
        sim.SyncMembers(new[] { Self, A, B }, Self.Id, 0);
        RunAndCheck(sim, 180, "presence");
        AssertSettled(sim, 3, "presence");

        // One member leaves.
        sim.SyncMembers(new[] { Self, B }, Self.Id, 0);
        RunAndCheck(sim, 180, "leave");
        AssertSettled(sim, 2, "leave");

        // Connection lost: back to fillers.
        sim.SyncMembers(Array.Empty<Member>(), "", 0);
        sim.SetFillerCount(4);
        RunAndCheck(sim, 180, "offline");
        AssertSettled(sim, 4, "offline");
    }

    [Fact]
    public void Filler_count_changes_while_running_settle()
    {
        var sim = NewSim(9);
        sim.SetFillerCount(4);
        RunAndCheck(sim, 5, "start");
        sim.SetFillerCount(8);
        RunAndCheck(sim, 120, "grow");
        AssertSettled(sim, 8, "grow");
        sim.SetFillerCount(2);
        RunAndCheck(sim, 120, "shrink");
        AssertSettled(sim, 2, "shrink");
        sim.SetFillerCount(6);
        RunAndCheck(sim, 120, "regrow");
        AssertSettled(sim, 6, "regrow");
    }
}

namespace Cowpanion.Core.Simulation;

/// <summary>
/// Per-cow behaviour parameters. For members this is derived deterministically from the MemberId so a
/// friend's cow is recognisably the same cow every session and on every client.
/// </summary>
/// <param name="SpeedMultiplier">0.7 .. 1.4</param>
/// <param name="Laziness">0..1, biases toward Graze/Idle/LieDown</param>
/// <param name="Sociability">0..1, cohesion strength</param>
/// <param name="ScaleJitter">0.9 .. 1.1 (model only; the renderer keeps integer scale so pixel art stays crisp)</param>
public sealed record Personality(
    double SpeedMultiplier,
    double Laziness,
    double Sociability,
    double ScaleJitter)
{
    public static Personality FromMemberId(string memberId)
    {
        ulong h = StableHash.Fnv1a64(memberId);
        return FromUnits(
            StableHash.Unit16(h, 0),
            StableHash.Unit16(h, 16),
            StableHash.Unit16(h, 32),
            StableHash.Unit16(h, 48));
    }

    public static Personality FromRandom(Random rng)
    {
        return FromUnits(rng.NextDouble(), rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
    }

    private static Personality FromUnits(double u1, double u2, double u3, double u4)
    {
        return new Personality(
            SpeedMultiplier: 0.7 + 0.7 * u1,
            Laziness: u2,
            Sociability: u3,
            ScaleJitter: 0.9 + 0.2 * u4);
    }
}

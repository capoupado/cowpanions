namespace Cowpanion.Core.Simulation;

/// <summary>
/// Weighted next-state selection and randomised state durations. Weights are scaled by personality and by
/// context (bubble showing, herd allowed to sleep). Kept as explicit arithmetic so it can be read and tuned.
/// </summary>
public static class TransitionTable
{
    private const int StateCount = 7;

    /// <summary>Picks the next state. <paramref name="weights"/> is scratch space of length ≥ 7 (no allocation).</summary>
    public static CowState Next(Cow cow, bool sleepAllowed, Random rng, double[] weights)
    {
        for (int i = 0; i < StateCount; i++)
        {
            weights[i] = 0;
        }

        double lazy = cow.Personality.Laziness;

        switch (cow.State)
        {
            case CowState.Walk:
                weights[(int)CowState.Idle] = 1.0 + lazy;
                weights[(int)CowState.Graze] = 0.8 + lazy;
                weights[(int)CowState.Walk] = 0.5 * (1.4 - lazy);
                weights[(int)CowState.Turn] = 0.25;
                weights[(int)CowState.Moo] = 0.05;
                break;
            case CowState.Idle:
                weights[(int)CowState.Walk] = 1.2 * (1.3 - lazy);
                weights[(int)CowState.Graze] = 0.7 + lazy;
                weights[(int)CowState.LieDown] = 0.15 + 0.5 * lazy;
                weights[(int)CowState.Turn] = 0.2;
                weights[(int)CowState.Moo] = 0.12;
                weights[(int)CowState.Idle] = 0.3;
                break;
            case CowState.Graze:
                weights[(int)CowState.Idle] = 1.0;
                weights[(int)CowState.Walk] = 0.9 * (1.3 - lazy);
                weights[(int)CowState.Graze] = 0.4 + lazy * 0.6;
                weights[(int)CowState.LieDown] = 0.1 + 0.3 * lazy;
                break;
            case CowState.LieDown:
                weights[(int)CowState.Idle] = 1.0;
                weights[(int)CowState.LieDown] = 0.3 + lazy;
                if (sleepAllowed)
                {
                    weights[(int)CowState.Sleep] = 2.0 + lazy;
                }
                break;
            case CowState.Sleep:
                weights[(int)CowState.Sleep] = sleepAllowed ? 4.0 : 0.0;
                weights[(int)CowState.LieDown] = 1.0;
                break;
            case CowState.Moo:
                weights[(int)CowState.Idle] = 1.0;
                break;
            case CowState.Turn:
                // Turn is resolved by the simulator (it flips Facing and enters AfterTurn); this is a safety default.
                weights[(int)CowState.Walk] = 1.0;
                break;
        }

        if (cow.HasBubble)
        {
            // Keep the text readable: strongly prefer standing still.
            weights[(int)CowState.Idle] += 10.0;
            weights[(int)CowState.Walk] *= 0.05;
            weights[(int)CowState.Turn] *= 0.2;
            weights[(int)CowState.LieDown] = 0;
            weights[(int)CowState.Sleep] = 0;
        }

        double total = 0;
        for (int i = 0; i < StateCount; i++)
        {
            total += weights[i];
        }

        if (total <= 0)
        {
            return CowState.Idle;
        }

        double roll = rng.NextDouble() * total;
        for (int i = 0; i < StateCount; i++)
        {
            roll -= weights[i];
            if (roll <= 0 && weights[i] > 0)
            {
                return (CowState)i;
            }
        }

        return CowState.Idle;
    }

    public static double Duration(CowState state, Personality p, Random rng)
    {
        double u = rng.NextDouble();
        switch (state)
        {
            case CowState.Walk:
                return 2.0 + u * 7.0;
            case CowState.Idle:
                return 2.0 + u * 5.0 * (0.6 + p.Laziness);
            case CowState.Graze:
                return 4.0 + u * 10.0 * (0.6 + p.Laziness);
            case CowState.Turn:
                return 0.45;
            case CowState.LieDown:
                return 10.0 + u * 25.0;
            case CowState.Sleep:
                return 30.0 + u * 60.0;
            case CowState.Moo:
                return 1.2;
            default:
                return 2.0;
        }
    }
}

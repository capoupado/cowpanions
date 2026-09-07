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

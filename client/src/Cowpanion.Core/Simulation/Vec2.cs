namespace Cowpanion.Core.Simulation;

/// <summary>Two-component vector in DIPs. X runs left to right along the strip; Y is the ground line offset.</summary>
public struct Vec2
{
    public double X;
    public double Y;

    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public override string ToString() => $"({X:F1}, {Y:F1})";
}

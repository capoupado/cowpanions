namespace Cowpanion.Core.Simulation;

/// <summary>A pasture member as reported by the server's <c>presence</c> message.</summary>
public sealed record Member(string Id, string Name, string Variant);

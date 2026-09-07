namespace Cowpanion.Net;

/// <summary>A relayed chat message from the server. Never written to disk by this client.</summary>
public sealed record ChatMessage(string FromId, string Name, string Text, long Ts);

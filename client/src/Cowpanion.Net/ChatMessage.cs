namespace Cowpanion.Net;

/// <summary>
/// A relayed chat message from the server (protocol v2). <see cref="Text"/>, <see cref="Emote"/> and
/// <see cref="Reaction"/> are empty strings when the frame did not carry them. Never written to disk by this client.
/// </summary>
public sealed record ChatMessage(string FromId, string Name, string Text, long Ts, string Emote = "", string Reaction = "");

using Cowpanion.Core.Simulation;

namespace Cowpanion.Net.Protocol;

/// <summary>Protocol constants from cowpanion-protocol.md v2.</summary>
public static class ProtocolConstants
{
    public const int ProtocolVersion = 2;
    public const int MaxFrameBytes = 4096;
    public const int MaxChatChars = 140;
    public const int VisibleCap = 12;
    /// <summary>A reaction is 1–3 emoji grapheme clusters; the server truncates longer ones.</summary>
    public const int MaxReactionGraphemes = 3;
    /// <summary>The only emote names the wire accepts.</summary>
    public static readonly string[] Emotes = ["moo", "jump", "spin"];

    public static bool IsEmote(string? emote)
    {
        return emote is not null && Array.IndexOf(Emotes, emote) >= 0;
    }

    public const int CloseVersionMismatch = 4000;
    public const int ClosePastureFull = 4001;
    public const int CloseRateLimitAbuse = 4002;
    public const int CloseBanned = 4003;
    public const int CloseMalformed = 4004;
}

/// <summary>Base for every decoded server → client message.</summary>
public abstract record ServerMessage;

public sealed record WelcomeMessage(int ProtocolVersion, string YourId, string Pasture, int VisibleCap, long ServerTime) : ServerMessage;

public sealed record PresenceMessage(IReadOnlyList<Member> Members, int Overflow) : ServerMessage;

public sealed record ChatServerMessage(ChatMessage Chat) : ServerMessage;

public sealed record ErrorMessage(string Code, string Message) : ServerMessage;

public sealed record PongMessage : ServerMessage;

/// <summary>A frame whose <c>t</c> was not recognised. Ignored, never fatal.</summary>
public sealed record UnknownMessage(string Type) : ServerMessage;

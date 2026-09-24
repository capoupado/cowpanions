namespace Cowpanion.Net;

/// <summary>One received chat frame as the history window shows it. <see cref="ReceivedAt"/> is local time.</summary>
public sealed record ChatHistoryEntry(DateTimeOffset ReceivedAt, string FromId, string Name, string Text, string Emote, string Reaction, bool IsSelf);

/// <summary>
/// Client-side chat history: the last <see cref="Capacity"/> chat frames received while the app runs, oldest first.
/// Memory only — never written to disk and gone on quit (DECISIONS.md, fifth round); the server keeps no history at all.
/// Not thread-safe: the Orchestrator adds on the UI thread and the history window reads there too.
/// </summary>
public sealed class ChatHistory
{
    public const int DefaultCapacity = 200;

    private readonly Queue<ChatHistoryEntry> _entries;

    public ChatHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _entries = new Queue<ChatHistoryEntry>(capacity);
    }

    public int Capacity { get; }

    public int Count => _entries.Count;

    /// <summary>Raised after an entry is added (the oldest may have been dropped first).</summary>
    public event Action<ChatHistoryEntry>? Added;

    public event Action? Cleared;

    /// <summary>Records a frame. Frames with no text, emote or reaction are ignored; returns whether it was kept.</summary>
    public bool Add(ChatMessage chat, bool isSelf, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (chat.Text.Length == 0 && chat.Emote.Length == 0 && chat.Reaction.Length == 0)
        {
            return false;
        }
        var entry = new ChatHistoryEntry(receivedAt, chat.FromId, chat.Name, chat.Text, chat.Emote, chat.Reaction, isSelf);
        if (_entries.Count >= Capacity)
        {
            _entries.Dequeue();
        }
        _entries.Enqueue(entry);
        Added?.Invoke(entry);
        return true;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<ChatHistoryEntry> Snapshot() => _entries.ToArray();

    public void Clear()
    {
        _entries.Clear();
        Cleared?.Invoke();
    }

    /// <summary>Plain-text line for copying: "14:02 Carlos: morning", "14:02 Carlos jumped", "14:02 Carlos: gg ❤️".</summary>
    public static string FormatPlain(ChatHistoryEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        string time = e.ReceivedAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return time + " " + e.Name + DescribeBody(e);
    }

    /// <summary>Everything after the name: ": gg ❤️", ": hi (jumped)", " jumped", " reacted ❤️ and spun around".</summary>
    public static string DescribeBody(ChatHistoryEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var parts = new List<string>(3);
        if (e.Text.Length > 0)
        {
            parts.Add(": " + e.Text + (e.Reaction.Length > 0 ? " " + e.Reaction : ""));
        }
        else if (e.Reaction.Length > 0)
        {
            parts.Add(" reacted " + e.Reaction);
        }
        if (e.Emote.Length > 0)
        {
            string verb = EmoteVerb(e.Emote);
            parts.Add(e.Text.Length > 0 ? " (" + verb + ")" : (parts.Count > 0 ? " and " : " ") + verb);
        }
        return string.Concat(parts);
    }

    public static string EmoteVerb(string emote) => emote switch
    {
        "moo" => "mooed",
        "jump" => "jumped",
        "spin" => "spun around",
        _ => "did " + emote,
    };
}

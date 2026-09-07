using System.Text;
using System.Text.Json;
using Cowpanion.Core.Simulation;

namespace Cowpanion.Net.Protocol;

/// <summary>
/// Encodes client → server frames and decodes server → client frames. Exactly one JSON object per frame.
/// Decoding is tolerant: unknown <c>t</c> becomes <see cref="UnknownMessage"/>, anything unparseable
/// returns null. Nothing here throws on bad input.
/// </summary>
public static class MessageCodec
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    public static byte[] EncodeHello(string clientId, string pasture, string displayName, string variant)
    {
        using var stream = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("t", "hello");
            w.WriteNumber("protocolVersion", ProtocolConstants.ProtocolVersion);
            w.WriteString("clientId", clientId);
            w.WriteString("pasture", pasture);
            w.WriteString("displayName", displayName);
            w.WriteString("variant", variant);
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Encodes a v2 chat frame. Only non-empty fields are written; the caller guarantees at least one of them is
    /// non-empty (the server drops an empty frame silently and still charges a rate-limit token).
    /// </summary>
    public static byte[] EncodeChat(string text, string emote = "", string reaction = "")
    {
        using var stream = new MemoryStream(text.Length * 3 + reaction.Length * 3 + 48);
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("t", "chat");
            if (text.Length > 0)
            {
                w.WriteString("text", text);
            }
            if (emote.Length > 0)
            {
                w.WriteString("emote", emote);
            }
            if (reaction.Length > 0)
            {
                w.WriteString("reaction", reaction);
            }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] EncodePing() => Encoding.UTF8.GetBytes("{\"t\":\"ping\"}");

    public static byte[] EncodeBye() => Encoding.UTF8.GetBytes("{\"t\":\"bye\"}");

    /// <summary>Decodes one server frame. Returns null for malformed frames (caller logs and continues).</summary>
    public static ServerMessage? Decode(ReadOnlySpan<byte> utf8)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!root.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string t = tEl.GetString()!;
            try
            {
                switch (t)
                {
                    case "welcome":
                        return new WelcomeMessage(
                            GetInt(root, "protocolVersion", 0),
                            GetString(root, "yourId", ""),
                            GetString(root, "pasture", ""),
                            GetInt(root, "visibleCap", ProtocolConstants.VisibleCap),
                            GetLong(root, "serverTime", 0));
                    case "presence":
                        return DecodePresence(root);
                    case "chat":
                        return new ChatServerMessage(new ChatMessage(
                            GetString(root, "fromId", ""),
                            GetString(root, "name", "cow"),
                            GetString(root, "text", ""),
                            GetLong(root, "ts", 0),
                            GetString(root, "emote", ""),
                            GetString(root, "reaction", "")));
                    case "error":
                        return new ErrorMessage(GetString(root, "code", ""), GetString(root, "message", ""));
                    case "pong":
                        return new PongMessage();
                    default:
                        return new UnknownMessage(t);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }

    private static PresenceMessage DecodePresence(JsonElement root)
    {
        var members = new List<Member>();
        if (root.TryGetProperty("members", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string id = GetString(m, "id", "");
                if (id.Length == 0)
                {
                    continue;
                }
                members.Add(new Member(id, GetString(m, "name", "cow"), GetString(m, "variant", "")));
            }
        }
        return new PresenceMessage(members, Math.Max(0, GetInt(root, "overflow", 0)));
    }

    private static string GetString(JsonElement el, string name, string fallback)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    }

    private static int GetInt(JsonElement el, string name, int fallback)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
    }

    private static long GetLong(JsonElement el, string name, long fallback)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : fallback;
    }
}

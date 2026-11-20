using System.Text.Json;

namespace TgCli;

// The message field carries the exact native history anchor or search continuation.
// For a forum topic list it is one component of the native offset tuple.
internal sealed record McpCursor(string Fingerprint, long Message, int Date = 0, int Topic = 0)
{
    internal string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    internal static McpCursor? Decode(string? text, string fingerprint)
    {
        if (text is null) return null;
        try
        {
            if (text.Length > 2048) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<McpCursor>(Convert.FromBase64String(text));
            if (cursor is null || cursor.Fingerprint != fingerprint || cursor.Message < 0 || cursor.Date < 0 || cursor.Topic < 0)
                throw new FormatException();
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw InvalidCursor();
        }
    }

    internal void Validate(string tool)
    {
        if (tool == "forum_topics" ? (Message, Date, Topic) == default : Message <= 0 || Date != 0 || Topic != 0)
            throw InvalidCursor();
    }

    private static ArgumentException InvalidCursor() => new("Invalid cursor, or filters changed. Start a new query without cursor.");
}

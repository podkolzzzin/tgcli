using Newtonsoft.Json;

namespace TgCli;

internal static class TranscriptionCommands
{
    public static MediaTranscription? Create(bool enabled, string language, string? command = null, IReadOnlyList<string>? commandArguments = null)
    {
        var options = TranscriptionOptions.Create(enabled, language, command, commandArguments);
        return options is null ? null : new(options, () => new WhisperMediaTranscriber());
    }

    internal static string[]? ParseArguments(string? value)
    {
        if (value is null) return null;
        try
        {
            var arguments = System.Text.Json.JsonSerializer.Deserialize<string[]>(value);
            if (arguments is not null && arguments.All(argument => argument is not null && !argument.Contains('\0')))
                return arguments;
        }
        catch (System.Text.Json.JsonException)
        {
            // Untrusted input can contain shell or credential text. Return a fixed usage description.
        }
        throw new ArgumentException("Transcription command arguments must be a JSON array of strings.");
    }

    public static void ValidateFormat(string format)
    {
        if (format.Trim().ToLowerInvariant() is not ("json" or "jsonl" or "plain" or "text"))
            throw new ArgumentException("Transcription format must be json, jsonl or plain.");
    }

    public static void Write(MediaTranscript transcript, string format, long? chatId = null, long? messageId = null)
    {
        if (format.Trim().ToLowerInvariant() is "plain" or "text") Console.WriteLine(transcript.Text);
        else
        {
            var json = transcript.ToJson();
            if (chatId is not null) json["chat_id"] = Wire.NullableId(chatId);
            if (messageId is not null) json["message_id"] = Wire.NullableId(messageId);
            Console.WriteLine(json.ToString(Formatting.None));
        }
    }
}

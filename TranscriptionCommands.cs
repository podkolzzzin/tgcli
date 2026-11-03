using Newtonsoft.Json;
using TdLib;

namespace TgCli;

internal static class TranscriptionCommands
{
    public static MediaTranscription? Create(bool enabled, string language, string? command = null)
    {
        var options = TranscriptionOptions.Create(enabled, language, command);
        return options is null ? null : new(options, () => new WhisperMediaTranscriber());
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
            if (chatId is not null) json["chat_id"] = chatId;
            if (messageId is not null) json["message_id"] = messageId;
            Console.WriteLine(json.ToString(Formatting.None));
        }
    }
}

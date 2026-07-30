using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed record BatchDownloadRequest(
    int Index,
    long ChatId,
    long MessageId,
    string? Type);

internal sealed record BatchDownloadResult(
    int Index,
    long ChatId,
    long MessageId,
    int? FileId,
    string? Path,
    string? Error)
{
    public bool Ok => Error is null;
}

internal static class BatchDownloads
{
    public static IReadOnlyList<BatchDownloadRequest> Read(TextReader reader, string? defaultType)
    {
        var requests = new List<BatchDownloadRequest>();
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JObject value;
            try
            {
                value = JObject.Parse(line);
            }
            catch (Exception ex)
            {
                throw new ValidationException($"Batch input line {lineNumber} is not valid JSON.", ex);
            }

            var chatId = ReadRequiredInt64(value, "chat_id", lineNumber);
            var messageId = ReadRequiredInt64(value, "message_id", lineNumber);
            var rowType = value.Value<string>("type");
            var type = string.IsNullOrWhiteSpace(defaultType) ? rowType : defaultType;
            if (string.IsNullOrWhiteSpace(type))
            {
                type = null;
            }
            else
            {
                AttachmentKinds.Parse(type);
                type = type.Trim();
            }

            requests.Add(new BatchDownloadRequest(requests.Count, chatId, messageId, type));
        }

        if (requests.Count == 0)
        {
            throw new ValidationException("Batch input contains no download records.");
        }

        var duplicate = requests
            .GroupBy(x => (x.ChatId, x.MessageId, Type: x.Type?.ToLowerInvariant()))
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            throw new ValidationException(
                $"Batch input contains duplicate record {duplicate.Key.ChatId}/{duplicate.Key.MessageId} ({duplicate.Key.Type ?? "file"}).");
        }

        return requests;
    }

    public static int ValidateParallelism(int parallel)
    {
        if (parallel is < 1 or > 32)
        {
            throw new ValidationException("--parallel must be between 1 and 32.");
        }

        return parallel;
    }

    public static string ResolveDestination(string outputDirectory, BatchDownloadRequest request, TdApi.File file)
    {
        var directory = Path.GetFullPath(outputDirectory);
        var sourceName = Path.GetFileName(file.Local?.Path);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = $"telegram-file-{file.Id}";
        }

        return Path.Combine(directory, $"{request.ChatId}_{request.MessageId}_{sourceName}");
    }

    public static JObject ToJson(BatchDownloadResult result)
    {
        return new JObject
        {
            ["index"] = result.Index,
            ["chat_id"] = result.ChatId,
            ["message_id"] = result.MessageId,
            ["ok"] = result.Ok,
            ["file_id"] = result.FileId is null ? JValue.CreateNull() : result.FileId.Value,
            ["path"] = result.Path is null ? JValue.CreateNull() : result.Path,
            ["error"] = result.Error is null ? JValue.CreateNull() : result.Error
        };
    }

    private static long ReadRequiredInt64(JObject value, string property, int lineNumber)
    {
        var token = value[property];
        if (token?.Type != JTokenType.Integer)
        {
            throw new ValidationException($"Batch input line {lineNumber} must contain integer {property}.");
        }

        return token.Value<long>();
    }
}

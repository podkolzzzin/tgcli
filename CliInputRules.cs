using System.Globalization;

namespace TgCli;

internal static class CliInputRules
{
    private const string StandardFormats = "json jsonl tsv plain md";
    private const string FourFormats = "json jsonl tsv plain";

    internal static void Validate(string command, IReadOnlyDictionary<string, string> options)
    {
        string? Value(string name) => options.GetValueOrDefault(name);
        long Number(string name, long fallback = 0) => Value(name) is { } value
            ? long.Parse(value, CultureInfo.InvariantCulture) : fallback;
        bool Has(string name) => options.ContainsKey(name);
        void Range(string name, long minimum, long maximum)
        {
            if (Value(name) is not null && (Number(name) < minimum || Number(name) > maximum))
                throw new CliUsageException(maximum == long.MaxValue
                    ? $"{name} must be at least {minimum}."
                    : $"{name} must be between {minimum} and {maximum}.");
        }

        if (Value("--format") is { } format) ValidateFormat(command, format);
        if (Has("--chat-id") && Number("--chat-id") == 0)
            throw new CliUsageException("--chat-id must be nonzero.");

        Range("--limit", command == "chat export" ? 0 : 1, 100);
        Range("--max-pages", 1, long.MaxValue);
        Range("--lock-timeout", 0, long.MaxValue);
        Range("--topic-id", 0, long.MaxValue);
        Range("--from-message-id", 0, long.MaxValue);
        Range("--since-message-id", 0, long.MaxValue);
        Range("--message-id", 1, long.MaxValue);
        Range("--post-id", 1, long.MaxValue);
        Range("--from-post-id", 0, long.MaxValue);
        Range("--to-post-id", 0, long.MaxValue);
        Range("--before", 0, 99);
        Range("--after", 0, 99);
        Range("--offset", -99, 0);
        Range("--api-id", 1, int.MaxValue);

        foreach (var name in new[] { "--input", "--output", "--session", "--queries" })
            if (Value(name) is { } path)
            {
                if (string.IsNullOrWhiteSpace(path)) throw new CliUsageException($"{name} requires a path.");
                _ = Path.GetFullPath(path);
            }

        switch (command)
        {
            case "channel comments":
                if (Has("--post-id") == Has("--all"))
                    throw new CliUsageException("Choose one of --post-id or --all.");
                break;
            case "channel metrics":
                if (Number("--to-post-id") != 0 && Number("--from-post-id") > Number("--to-post-id"))
                    throw new CliUsageException("--from-post-id must be at most --to-post-id.");
                if (Date("--since-date") > Date("--until-date"))
                    throw new CliUsageException("--since-date must be at or before --until-date.");
                break;
            case "chat context":
                if (Number("--before", 5) + Number("--after", 5) > 99)
                    throw new CliUsageException("--before and --after can request at most 99 surrounding messages in total.");
                break;
            case "chat messages":
                ValidateTopicAndLocal();
                // Apply only when this option still exists on the registered command.
                if (Value("--schema") is { } schema)
                {
                    if (!schema.Equals("simple", StringComparison.OrdinalIgnoreCase)
                        && !schema.Equals("rich", StringComparison.OrdinalIgnoreCase))
                        throw new CliUsageException("--schema must be simple or rich.");
                    if (schema.Equals("rich", StringComparison.OrdinalIgnoreCase)
                        && Value("--format")?.Trim().ToLowerInvariant() is not ("json" or "jsonl"))
                        throw new CliUsageException("--schema rich requires --format json or jsonl.");
                }
                break;
            case "chat search":
                ValidateTopicAndLocal();
                if (Has("--query") && Has("--queries"))
                    throw new CliUsageException("Choose one of --query or --queries.");
                if (Value("--type") is { } searchType) _ = AttachmentKinds.ToSearchFilter(searchType);
                break;
            case "chat stats":
                ValidateTopicAndLocal();
                if (Value("--type") is { } statsType) _ = AttachmentKinds.Parse(statsType);
                break;
            case "download":
                _ = AttachmentDownloads.Validate(Value("--type"), Value("--attachment-id"),
                    Number("--chat-id"), Number("--message-id"));
                break;
            case "download-batch":
                if (Value("--type") is { } batchType) _ = AttachmentKinds.Parse(batchType);
                if (Has("--parallel")) _ = BatchDownloads.ValidateParallelism((int)Number("--parallel"));
                break;
            case "transcribe" or "message transcribe":
                if (Value("--language") is { } language) _ = TranscriptionOptions.Create(true, language, null);
                break;
            case "session unlock":
                if (!Has("--stale-only")) throw new CliUsageException("Pass --stale-only to confirm safe lock repair.");
                break;
        }
        return;

        DateTimeOffset? Date(string name)
        {
            if (Value(name) is not { } value) return null;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return date;
            throw new CliUsageException($"{name} requires a date or timestamp.");
        }

        void ValidateTopicAndLocal()
        {
            if (Number("--topic-id") > 0 && Has("--local"))
                throw new CliUsageException("--local is not supported with --topic-id.");
        }
    }

    private static void ValidateFormat(string command, string value)
    {
        var formats = command switch
        {
            "channel metrics" => "json jsonl csv",
            "channel comments" => "json jsonl",
            "download-batch" => "jsonl plain",
            "diagnostics" => "json jsonl plain",
            "transcribe" or "message transcribe" => "json jsonl plain text",
            "chat migrations" or "chat stats" or "forum topics" or "session status" => FourFormats,
            "message get" or "link message" => FourFormats,
            _ when command.StartsWith("bot ", StringComparison.Ordinal) => FourFormats,
            _ => StandardFormats + " text markdown"
        };
        var allowed = formats.Split(' ');
        if (!allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new CliUsageException($"--format for '{command}' must be one of {string.Join(", ", allowed)}.");
    }
}

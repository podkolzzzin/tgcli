using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TgCli;

internal sealed record ExportRequest
{
    private const string CheckpointPrefix = "tgcli-export-v1.";
    private static readonly HashSet<string> FieldNames = new(StringComparer.Ordinal)
    {
        "schema", "schema_version", "chat_id", "message_id", "short_message_id", "tg_url",
        "source_chat_id", "original_chat_id", "original_message_id", "query", "topic_id", "date", "edit_date", "sender_type", "sender_user_id", "sender_chat_id",
        "sender_display_name", "sender_username", "kind", "text", "entities", "reply_to_chat_id",
        "reply_to_message_id", "reply_target", "forward_origin", "interaction_info", "interaction_info_available", "view_count",
        "forward_count", "reply_count", "reaction_counts", "paid_reaction_count", "has_comments",
        "poll", "service_event", "attachments", "is_deleted", "tombstone", "links"
    };

    private ExportRequest() { }

    private sealed record ParsedCheckpoint(long PrincipalUserId, IReadOnlyDictionary<long, long> Cursors);

    public long ChatId { get; private init; }
    public string? Path { get; private init; }
    public OutputFormat Format { get; private init; }
    public bool All { get; private init; }
    public int Limit { get; private init; }
    public bool Local { get; private init; }
    public bool IncludeLinks { get; private init; }
    public bool Incremental { get; private init; }
    public bool UpdatesSnapshot { get; private init; }
    public bool RequireComplete { get; private init; }
    public bool HasRowIdentity { get; private init; }
    public bool HasRowDate { get; private init; }
    public string? Fields { get; private init; }
    public string? CheckpointToken { get; private init; }
    public IReadOnlyDictionary<long, long>? Checkpoint { get; private init; }
    public long SinceMessageId { get; private init; }
    public DateTimeOffset? Since { get; private init; }
    public DateTimeOffset? ExpectSince { get; private init; }
    public int ExpectCountMin { get; private init; }
    public JObject Scope { get; private init; } = null!;
    private long? PrincipalUserId { get; init; }
    private long? CheckpointPrincipalUserId { get; init; }

    public static ExportRequest Create(long chatId, string output, string format, bool all = true, bool local = false,
        int maxPages = 1000, bool includeLinks = false, bool resume = false, bool incremental = false,
        long sinceMessageId = 0, string? sinceDate = null, string? resumeToken = null, string? fields = null,
        bool transcribe = false, string transcribeLanguage = "auto", string? transcribeCommand = null,
        string? expectSince = null, int expectCountMin = 0, bool failIncomplete = false, int limit = 0,
        IReadOnlyList<string>? transcribeCommandArguments = null)
    {
        if (limit is < 0 or > 100) throw new ArgumentException("Export limit must be from 0 to 100.", nameof(limit));
        all &= limit == 0;
        if (chatId == 0) throw new ArgumentException("Chat ID must be nonzero.", nameof(chatId));
        if (sinceMessageId < 0) throw new ArgumentException("--since-message-id must be a positive message ID.");
        if (maxPages <= 0) throw new ArgumentException("--max-pages must be positive.");
        if (expectCountMin < 0) throw new ArgumentException("--expect-count-min cannot be negative.");
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output requires a path or '-'.");
        if (resume && incremental) throw new ArgumentException("Choose either --resume or --incremental.");
        if (incremental && resumeToken is not null)
            throw new ArgumentException("--incremental requires a fresh scan of its scope. Use --resume with a checkpoint.");

        var path = output == "-" ? null : System.IO.Path.GetFullPath(output);
        if (path is not null) ExportSnapshotStore.ValidatePath(path);
        var parsedFormat = Output.ParseFormat(format);
        var names = ParseFields(fields, parsedFormat);
        var hasRowIdentity = parsedFormat == OutputFormat.Jsonl &&
            (names is null || names.Contains("chat_id") && names.Contains("message_id"));
        var updates = resume || incremental;
        if (updates && (path is null || parsedFormat != OutputFormat.Jsonl || !hasRowIdentity))
            throw new ArgumentException("--resume and --incremental require a JSONL file with chat_id and message_id fields.");
        if ((updates || resumeToken is not null) && (!all || local))
            throw new ArgumentException("Snapshot updates and checkpoints require a complete online export.");
        if (resumeToken is not null && !hasRowIdentity)
            throw new ArgumentException("A checkpoint requires JSONL with chat_id and message_id fields.");
        if (local && (transcribe || !string.IsNullOrWhiteSpace(transcribeCommand)))
            throw new ArgumentException("Export transcription requires online attachment access.");

        // A projected JSONL file can remain resumable when its keys and terminal state are retained.
        if (hasRowIdentity && names is not null) names.Add("is_deleted");
        var normalizedFields = names is null ? null : string.Join(',', names.Order(StringComparer.Ordinal));
        var (since, _) = ReadBounds.ParseDates(sinceDate, null);
        var (expected, _) = ReadBounds.ParseDates(expectSince, null);
        var scope = new JObject
        {
            ["chat_id"] = FormatId(chatId),
            ["schema"] = ExportSchema.Name,
            ["schema_version"] = ExportSchema.Version,
            ["format"] = parsedFormat.ToString().ToLowerInvariant(),
            ["follow_migrations"] = all && !local,
            ["limit"] = limit,
            ["since_message_id"] = sinceMessageId == 0 ? JValue.CreateNull() : FormatId(sinceMessageId),
            ["since_date"] = since is null ? JValue.CreateNull() : since.Value.ToString("O", CultureInfo.InvariantCulture),
            ["fields"] = normalizedFields is null ? JValue.CreateNull() : normalizedFields,
            ["include_links"] = includeLinks,
            ["transcription"] = !string.IsNullOrWhiteSpace(transcribeCommand)
                ? "external:" + Hash(new JArray(transcribeCommand, new JArray(transcribeCommandArguments ?? [])).ToString(Formatting.None))
                : transcribe ? "built-in:" + transcribeLanguage.Trim().ToLowerInvariant() : "disabled"
        };
        var checkpoint = resumeToken is null ? null : ParseCheckpoint(resumeToken, scope, chatId);
        return new ExportRequest
        {
            ChatId = chatId,
            Path = path,
            Format = parsedFormat,
            All = all,
            Limit = limit,
            Local = local,
            IncludeLinks = includeLinks,
            Incremental = incremental,
            UpdatesSnapshot = updates,
            RequireComplete = failIncomplete || all && !local,
            HasRowIdentity = hasRowIdentity,
            HasRowDate = names is null || names.Contains("date"),
            Fields = normalizedFields,
            CheckpointToken = resumeToken,
            Checkpoint = checkpoint?.Cursors,
            CheckpointPrincipalUserId = checkpoint?.PrincipalUserId,
            SinceMessageId = sinceMessageId,
            Since = since,
            ExpectSince = expected,
            ExpectCountMin = expectCountMin,
            Scope = scope
        };
    }

    public ExportRequest BindPrincipal(long userId)
    {
        if (userId <= 0)
            throw new InvalidOperationException("The authenticated Telegram account has no valid user ID.");
        if (CheckpointPrincipalUserId is long checkpointUserId && checkpointUserId != userId)
            throw new ArgumentException("The export checkpoint belongs to a different Telegram account.");
        if (PrincipalUserId is long boundUserId && boundUserId != userId)
            throw new InvalidOperationException("The export scope is already bound to a different Telegram account.");

        return this with { PrincipalUserId = userId, Scope = ScopeForPrincipal(Scope, userId) };
    }

    public long RequirePrincipal() =>
        PrincipalUserId ?? throw new InvalidOperationException("The export scope must be bound to an authenticated Telegram account.");

    public bool Includes(ExportedMessage row)
    {
        var message = row.Message;
        return (message.ChatId != ChatId || message.Id > SinceMessageId)
            && (Since is null || message.Date > 0 && DateTimeOffset.FromUnixTimeSeconds(message.Date) >= Since)
            && (Checkpoint is null || message.Id > Checkpoint[message.ChatId]);
    }

    public string CreateCheckpoint(IReadOnlySet<long> sources, IEnumerable<ExportRowKey> activeKeys, bool keepInputCursor = true)
    {
        var principalId = RequirePrincipal();
        var cursors = sources.ToDictionary(chat => chat,
            chat => keepInputCursor ? Checkpoint?.GetValueOrDefault(chat) ?? 0 : 0);
        foreach (var key in activeKeys) cursors[key.ChatId] = Math.Max(cursors[key.ChatId], key.MessageId);

        var encoded = new JObject
        {
            ["scope"] = Hash(Scope.ToString(Formatting.None)),
            ["principal_user_id"] = FormatId(principalId),
            ["cursors"] = new JObject(cursors.OrderBy(x => x.Key).Select(x =>
                new JProperty(FormatId(x.Key), FormatId(x.Value))))
        };
        return CheckpointPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(encoded.ToString(Formatting.None)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ParsedCheckpoint ParseCheckpoint(string token, JObject scope, long chatId)
    {
        const int maxTokenLength = 65536;
        if (!token.StartsWith(CheckpointPrefix, StringComparison.Ordinal) || token.Length > maxTokenLength)
            throw new ArgumentException("Invalid export checkpoint. Use a resume_token from a matching export manifest.");
        var encoded = token[CheckpointPrefix.Length..].Replace('-', '+').Replace('_', '/');
        try
        {
            var payload = ExportSnapshotStore.ParseObject(Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))));
            if (payload["principal_user_id"]?.Type != JTokenType.String ||
                !TryParseId(payload["principal_user_id"], out var principalId) || principalId <= 0 ||
                payload.Value<string>("scope") != Hash(ScopeForPrincipal(scope, principalId).ToString(Formatting.None)) ||
                payload["cursors"] is not JObject cursors)
                throw new FormatException();
            var parsed = new Dictionary<long, long>();
            foreach (var cursor in cursors.Properties())
            {
                if (!TryParseId(new JValue(cursor.Name), out var source) || source == 0 ||
                    !TryParseId(cursor.Value, out var messageId) || messageId < 0 || !parsed.TryAdd(source, messageId))
                    throw new FormatException();
            }
            if (!parsed.ContainsKey(chatId)) throw new FormatException();
            return new ParsedCheckpoint(principalId, parsed);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException)
        {
            throw new ArgumentException("The export checkpoint is invalid or belongs to another chat, schema or filter.", ex);
        }
    }

    private static JObject ScopeForPrincipal(JObject scope, long principalId)
    {
        var bound = (JObject)scope.DeepClone();
        bound["principal_user_id"] = FormatId(principalId);
        return bound;
    }

    private static HashSet<string>? ParseFields(string? fields, OutputFormat format)
    {
        if (fields is null) return null;
        if (format is not (OutputFormat.Json or OutputFormat.Jsonl))
            throw new ArgumentException("--fields requires JSON or JSONL.");
        var names = fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0) throw new ArgumentException("--fields must list at least one export field.");
        var unknown = names.Except(FieldNames).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"Unknown export field(s): {string.Join(", ", unknown)}.");
        names.UnionWith(["schema", "schema_version"]);
        return names;
    }

    internal static string FormatId(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static bool TryParseId(JToken? token, out long id)
    {
        id = 0;
        if (token?.Type is not (JTokenType.Integer or JTokenType.String)) return false;
        var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)
            && text == FormatId(id);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

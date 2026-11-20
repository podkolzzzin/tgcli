using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;

namespace TgCli;

internal interface IMcpTelegramBackend : IAsyncDisposable
{
    Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken cancellationToken);
}

internal sealed class McpTools(IMcpTelegramBackend backend)
{
    internal const int MaxResponseBytes = 512 * 1024;
    internal static readonly IReadOnlyList<Tool> Catalog = BuildCatalog();

    internal static ListToolsResult List(string? cursor = null, bool modern = true)
    {
        if (cursor is not null) throw new McpProtocolException("Invalid tools catalog cursor.", McpErrorCode.InvalidParams);
        return new() { Tools = Catalog.ToList(), TimeToLive = modern ? TimeSpan.FromMinutes(5) : null, CacheScope = modern ? CacheScope.Private : null };
    }

    internal async Task<CallToolResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var tool = Catalog.FirstOrDefault(x => x.Name == name)
            ?? throw new McpProtocolException("Unknown tool. Use tools/list to see available tools.", McpErrorCode.InvalidParams);
        var args = new McpArguments(arguments);
        try { args.Validate(tool.InputSchema); }
        catch (ArgumentException ex) { return Error("invalid_arguments", ex.Message); }

        try
        {
            var result = await backend.ExecuteAsync(name, args, token);
            var json = result.ToJsonString();
            var response = new CallToolResult
            {
                IsError = result["error_code"]?.GetValue<string>() == "partial_result" ? true : null,
                Content = [new TextContentBlock { Text = json }],
                StructuredContent = JsonSerializer.SerializeToElement(result)
            };
            // Include both structuredContent and the escaped text copy. Leave room
            // for the SDK's JSON-RPC envelope, resultType and server metadata.
            if (JsonSerializer.SerializeToUtf8Bytes(response).Length > MaxResponseBytes - 1024)
                return Error("response_too_large", "Response exceeds 512 KiB. Request a smaller page or narrower filters.");
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only validation generated from our schema can echo an argument name.
            var failure = CliFailure.FromException(ex);
            return failure.ExitCode switch
            {
                130 => Error("request_cancelled", "Telegram request was cancelled."),
                5 => Error("request_timeout", "Telegram request timed out. Wait for outstanding work to finish before retrying."),
                4 => Error("login_required", "Telegram authorization is unavailable. Check diagnostics or run tgcli login."),
                3 => Error("partial_result", "Telegram returned partial results. Retry with a narrower scope."),
                2 => Error("invalid_arguments", "Check the tool arguments and start a new query if its cursor is no longer valid."),
                _ => ProjectOperationalError(ex)
            };
        }
    }

    private static CallToolResult ProjectOperationalError(Exception error) => error switch
    {
        UnauthorizedAccessException => Error("access_denied", "Check access to the Telegram session and the requested operation."),
        IOException => Error("io_error", "Check the Telegram session and file access. Another process may hold the session lock."),
        _ when TelegramRpcPolicy.Classify(error).Kind == TelegramRpcFailureKind.FloodWait =>
            Error("flood_wait", "Telegram limited requests. Wait before retrying."),
        _ => Error("operation_failed", "The Telegram operation failed. Check session status and diagnostics.")
    };

    private static CallToolResult Error(string code, string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = $"{code}: {message}" }] };

    private static IReadOnlyList<Tool> BuildCatalog()
    {
        JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        JsonObject Id(string description, bool signed = false, bool allowZero = false) => new()
        {
            ["type"] = "string",
            ["pattern"] = signed ? "^-?0*[1-9][0-9]*$" : allowZero ? "^[0-9]+$" : "^0*[1-9][0-9]*$",
            ["description"] = description
        };
        JsonObject TopicId()
        {
            var rule = Id("Nonnegative 32-bit decimal TDLib forum topic ID. Omit or use 0 for the whole chat.", allowZero: true);
            rule["default"] = "0";
            return rule;
        }
        JsonObject Int(int min, int max, int value) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["default"] = value };
        JsonObject Bool(bool value) => new() { ["type"] = "boolean", ["default"] = value };
        JsonObject Limit() => new() { ["limit"] = Int(1, 100, 30) };
        JsonObject Page() => new()
        {
            ["limit"] = Int(1, 100, 30),
            ["max_pages"] = Int(1, 1000, 100),
            ["cursor"] = Str("Continuation returned by this tool. Keep all scope filters unchanged.")
        };
        JsonObject ChatPage()
        {
            var page = Page();
            page["chat_id"] = Id("Decimal TDLib chat ID.", signed: true);
            page["topic_id"] = TopicId();
            page["kind"] = Str("Message kind filter.");
            page["service_only"] = Bool(false);
            return page;
        }
        JsonObject MessageArgs() => new()
        {
            ["chat_id"] = Id("Decimal TDLib chat ID.", signed: true),
            ["message_id"] = Id("Positive decimal TDLib message ID. Use the full ID from a message row.")
        };
        var tools = new List<Tool>();
        void Add(string name, string description, JsonObject properties, string[] required, bool writes = false)
        {
            properties["request_timeout"] = Int(1, 300, 60);
            var output = McpSchemas.Output(name);
            tools.Add(new Tool
            {
                Name = name,
                Description = description,
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["$schema"] = McpSchemas.Dialect,
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                    ["additionalProperties"] = false
                }),
                OutputSchema = JsonSerializer.SerializeToElement(output),
                Annotations = new() { ReadOnlyHint = !writes, DestructiveHint = writes, IdempotentHint = !writes, OpenWorldHint = true }
            });
        }
        Add("chat_list", "List Telegram chats from the main chat list. No continuation is available.", Limit(), []);
        Add("chat_resolve", "Resolve a public Telegram username or t.me profile link.",
            new() { ["username"] = Str("Public username or profile link.") }, ["username"]);
        var search = Limit();
        search["query"] = Str("Chat title or username.");
        search["server"] = Bool(true);
        Add("chat_search", "Find known chats by title or username. Set server=false to query the local cache.", search, ["query"]);

        var messages = ChatPage();
        messages["local"] = Bool(false);
        Add("chat_messages", "Read up to a requested number of matching chat or forum messages in chronological order. Use the returned cursor to continue.", messages, ["chat_id"]);

        search = ChatPage();
        search["query"] = Str("Message text, or empty for attachment search.");
        search["type"] = Str("all, voice, document, audio, video, photo, animation, video-note, file.");
        search["since_date"] = Str("First included UTC date or ISO 8601 timestamp.");
        search["until_date"] = Str("Last included UTC date or ISO 8601 timestamp.");
        Add("message_search", "Search message text or attachments in a chat or forum topic. Results are chronological.", search, ["chat_id"]);

        Add("message_get", "Read one full message, attachment metadata and Telegram links.", MessageArgs(), ["chat_id", "message_id"]);
        var context = MessageArgs();
        context["before"] = Int(0, 99, 5);
        context["after"] = Int(0, 99, 5);
        context["follow_reply_chain"] = Bool(true);
        Add("chat_context", "Read context around a message, optionally following replies. The before and after counts may total up to 99.", context, ["chat_id", "message_id"]);

        var stats = new JsonObject
        {
            ["chat_id"] = Id("Decimal TDLib chat ID.", signed: true),
            ["topic_id"] = TopicId(),
            ["type"] = Str("Attachment kind, default all."),
            ["max_pages"] = Int(1, 1000, 100),
            ["local"] = Bool(false)
        };
        Add("chat_stats", "Count messages and attachment sizes with acquisition coverage.", stats, ["chat_id"]);

        var topics = Page();
        topics["chat_id"] = Id("Decimal TDLib forum supergroup chat ID.", signed: true);
        topics["query"] = Str("Topic name filter.");
        Add("forum_topics", "List forum topics with a continuation containing all TDLib offsets.", topics, ["chat_id"]);

        Add("message_link", "Build Telegram deep links and available HTTPS links for a message.", MessageArgs(), ["chat_id", "message_id"]);
        var download = MessageArgs();
        download["output"] = Str("Explicit absolute destination file or directory. Existing files may be overwritten.");
        download["type"] = Str("Optional attachment kind.");
        download["file_id"] = Id("Optional positive 32-bit decimal TDLib file ID in the source message.");
        Add("attachment_download", "Download a message attachment to an absolute path. Returns file metadata.", download, ["chat_id", "message_id", "output"], writes: true);
        Add("diagnostics", "Report Telegram authorization, lock, and connection health.", new(), []);
        return tools.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}

internal sealed class McpArguments(IDictionary<string, JsonElement>? values)
{
    private readonly IDictionary<string, JsonElement> _values = values ?? new Dictionary<string, JsonElement>();
    internal string? Text(string name, string? fallback = null) => _values.TryGetValue(name, out var v) ? v.GetString() : fallback;
    internal int Int(string name, int fallback = 0) => _values.TryGetValue(name, out var v) ? v.GetInt32() : fallback;
    internal bool Bool(string name, bool fallback = false) => _values.TryGetValue(name, out var v) ? v.GetBoolean() : fallback;
    internal long Id(string name) => ParseId(name, Text(name));
    internal int TopicId() => checked((int)ParseId("topic_id", Text("topic_id", "0")));
    internal string? FileId() => Text("file_id") is { } id ? Wire.Id(ParseId("file_id", id)) : null;
    internal string? Kind() => string.IsNullOrWhiteSpace(Text("kind")) ? null : ChatCommands.NormalizeKind(Text("kind")!);
    internal (DateTimeOffset? Since, DateTimeOffset? Until) Dates() =>
        ReadBounds.ParseDates(Text("since_date"), Text("until_date"));

    private static long ParseId(string name, string? text)
    {
        var digits = text.AsSpan();
        if (name == "chat_id" && !digits.IsEmpty && digits[0] == '-') digits = digits[1..];
        if (digits.IsEmpty || digits.IndexOfAnyExceptInRange('0', '9') >= 0 ||
            !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ||
            name == "chat_id" && id == 0 ||
            name == "message_id" && id <= 0 ||
            name == "topic_id" && id > int.MaxValue ||
            name == "file_id" && (id <= 0 || id > int.MaxValue))
        {
            var description = name is "topic_id" or "file_id" ? "a 32-bit decimal string within range" : "a valid nonzero 64-bit decimal string";
            throw new ArgumentException($"{name} must be {description}.");
        }

        return id;
    }

    internal void Validate(JsonElement schema)
    {
        var properties = schema.GetProperty("properties");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!_values.ContainsKey(required.GetString()!)) throw new ArgumentException($"Missing argument: {required.GetString()}.");
        foreach (var (key, value) in _values)
        {
            if (!properties.TryGetProperty(key, out var rule)) throw new ArgumentException("Unknown argument. Use tools/list to check the tool input schema.");
            var valid = rule.GetProperty("type").GetString() switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= rule.GetProperty("minimum").GetInt32() && n <= rule.GetProperty("maximum").GetInt32(),
                _ => false
            };
            if (!valid) throw new ArgumentException($"Invalid value for {key}.");
            if (rule.TryGetProperty("pattern", out _)) _ = ParseId(key, value.GetString());
        }
    }

    internal void Preflight(string tool)
    {
        switch (tool)
        {
            case "chat_context":
                ReadBounds.ValidateContext(Id("message_id"), Int("before", 5), Int("after", 5));
                break;
            case "chat_resolve":
                _ = TelegramOperations.NormalizeUsername(Text("username")!);
                break;
            case "chat_search" when string.IsNullOrWhiteSpace(Text("query")):
                throw new ArgumentException("query must not be empty.");
            case "chat_messages" or "chat_stats" when TopicId() != 0 && Bool("local"):
                throw new ArgumentException("local cannot be used with topic_id.");
            case "chat_stats":
                _ = AttachmentKinds.Parse(Text("type", "all")!);
                break;
            case "attachment_download":
                var output = Text("output")!;
                if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("output must be an absolute path.");
                _ = Path.GetFullPath(output);
                _ = AttachmentDownloads.Validate(Text("type"), FileId(), Id("chat_id"), Id("message_id"));
                break;
        }
    }

    internal JObject CursorScope(string tool)
    {
        var scope = new JObject { ["chat_id"] = Wire.Id(Id("chat_id")) };
        if (tool == "forum_topics")
        {
            scope["query"] = Text("query", "");
            return scope;
        }

        var topicId = TopicId();
        scope["topic_id"] = Wire.NullableId(topicId == 0 ? null : topicId);
        scope["service_only"] = Bool("service_only");
        scope["kind"] = Kind();
        if (tool == "message_search")
        {
            var (since, until) = Dates();
            scope["query"] = Text("query", "");
            scope["type"] = AttachmentKinds.CanonicalName(AttachmentKinds.Parse(Text("type", "all")!));
            scope["since_date"] = since is { } from ? Wire.Timestamp(from) : null;
            scope["until_date"] = until is { } through ? Wire.Timestamp(through) : null;
        }
        else
        {
            scope["local"] = Bool("local");
        }
        return scope;
    }

    internal string Fingerprint(string tool) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(tool + "\n" + CursorScope(tool).ToString(Newtonsoft.Json.Formatting.None))));
}

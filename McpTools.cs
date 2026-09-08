using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

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
            ?? throw new McpProtocolException($"Unknown tool: {name}", McpErrorCode.InvalidParams);
        try
        {
            var args = new McpArguments(arguments);
            args.Validate(tool.InputSchema);
            var result = await backend.ExecuteAsync(name, args, token);
            var json = result.ToJsonString();
            var response = new CallToolResult { Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.SerializeToElement(result) };
            // Include both structuredContent and the escaped text copy. Leave room
            // for the SDK's JSON-RPC envelope, resultType and server metadata.
            if (JsonSerializer.SerializeToUtf8Bytes(response).Length > MaxResponseBytes - 1024)
                throw new InvalidOperationException("Response exceeds 512 KiB. Request a smaller page or narrower filters. No partial response was returned.");
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Do not log message bodies, arguments or credentials. These errors are private tool results.
            return new() { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
    }

    private static IReadOnlyList<Tool> BuildCatalog()
    {
        JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        JsonObject Id(string description) => new() { ["type"] = "string", ["pattern"] = "^-?[0-9]+$", ["description"] = description + " Decimal TDLib ID; do not use the short link ID." };
        JsonObject Int(int min, int max, int value) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["default"] = value };
        JsonObject Bool(bool value) => new() { ["type"] = "boolean", ["default"] = value };
        JsonObject Limit() => new() { ["limit"] = Int(1, 100, 30) };
        JsonObject Page() => new() { ["limit"] = Int(1, 100, 30), ["cursor"] = Str("Continuation returned by this tool. Keep all filters unchanged.") };
        JsonObject ChatPage()
        {
            var p = Page(); p["chat_id"] = Id("Chat to read."); p["topic_id"] = Int(0, int.MaxValue, 0); return p;
        }
        JsonObject MessageArgs() => new() { ["chat_id"] = Id("Source chat."), ["message_id"] = Id("Message in that chat.") };
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
        Add("chat_list", "List Telegram chats from the main chat list. This TDLib operation has no continuation; reports its limit explicitly.", Limit(), []);
        Add("chat_resolve", "Resolve a public Telegram username, with or without @.", new() { ["username"] = Str("Public username.") }, ["username"]);
        var search = Limit(); search["query"] = Str("Chat title or username."); search["server"] = Bool(false);
        Add("chat_search", "Find chats by title or username. Not message search. No TDLib continuation is available.", search, ["query"]);
        var messages = ChatPage(); messages["local"] = Bool(false); messages["kind"] = Str("Message kind filter."); messages["service_only"] = Bool(false);
        Add("chat_messages", "Read one page of chat or forum-topic messages, newest first. Use returned cursor even when filtered items are empty.", messages, ["chat_id"]);
        search = ChatPage(); search["query"] = Str("Message text, or empty for attachment search."); search["type"] = Str("all, voice, document, audio, video, photo, animation, video-note, file.");
        Add("message_search", "Search message text or attachments in a chat or forum topic.", search, ["chat_id"]);
        Add("message_get", "Read one full message, attachment metadata and Telegram links.", MessageArgs(), ["chat_id", "message_id"]);
        var context = MessageArgs(); context["before"] = Int(0, 49, 5); context["after"] = Int(0, 49, 5); context["follow_reply_chain"] = Bool(true);
        Add("chat_context", "Read context around a message, optionally following replies. Reports a bounded context, not complete chat history.", context, ["chat_id", "message_id"]);
        var stats = new JsonObject { ["chat_id"] = Id("Chat to count."), ["topic_id"] = Int(0, int.MaxValue, 0), ["type"] = Str("Attachment kind, default all."), ["max_pages"] = Int(1, 1000, 100), ["local"] = Bool(false) };
        Add("chat_stats", "Count messages and attachment sizes, retaining exact/estimated completeness and termination reason.", stats, ["chat_id"]);
        var topics = Page(); topics["chat_id"] = Id("Forum supergroup."); topics["query"] = Str("Topic name filter.");
        Add("forum_topics", "List forum topics with a continuation containing all TDLib offsets.", topics, ["chat_id"]);
        Add("message_link", "Build Telegram deep links and available HTTPS links for a message.", MessageArgs(), ["chat_id", "message_id"]);
        var download = MessageArgs(); download["output"] = Str("Explicit absolute destination file or directory. Existing files may be overwritten."); download["type"] = Str("Optional attachment kind.");
        Add("attachment_download", "Download a message attachment to a local absolute path. Writes a file and may overwrite it; returns metadata, never file bytes.", download, ["chat_id", "message_id", "output"], writes: true);
        Add("diagnostics", "Check Telegram authorization and connection. Does not return credentials or session secrets.", new(), []);
        return tools.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}

internal sealed class McpArguments(IDictionary<string, JsonElement>? values)
{
    private readonly IDictionary<string, JsonElement> _values = values ?? new Dictionary<string, JsonElement>();
    internal string? Text(string name, string? fallback = null) => _values.TryGetValue(name, out var v) ? v.GetString() : fallback;
    internal int Int(string name, int fallback = 0) => _values.TryGetValue(name, out var v) ? v.GetInt32() : fallback;
    internal bool Bool(string name, bool fallback = false) => _values.TryGetValue(name, out var v) ? v.GetBoolean() : fallback;
    internal long Id(string name) => long.TryParse(Text(name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0
        ? id : throw new ArgumentException($"{name} must be a nonzero 64-bit decimal string.");

    internal void Validate(JsonElement schema)
    {
        var properties = schema.GetProperty("properties");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!_values.ContainsKey(required.GetString()!)) throw new ArgumentException($"Missing argument: {required.GetString()}.");
        foreach (var (key, value) in _values)
        {
            if (!properties.TryGetProperty(key, out var rule)) throw new ArgumentException($"Unknown argument: {key}.");
            var valid = rule.GetProperty("type").GetString() switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= rule.GetProperty("minimum").GetInt32() && n <= rule.GetProperty("maximum").GetInt32(),
                _ => false
            };
            if (!valid) throw new ArgumentException($"Invalid value for {key}.");
            if (rule.TryGetProperty("pattern", out _) && (value.GetString()!.StartsWith('+') || !long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id == 0))
                throw new ArgumentException($"{key} must be a 64-bit decimal string.");
        }
    }

    internal string Fingerprint(string tool) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(tool + "\n" + string.Join('\n', _values.Where(x => x.Key is not ("cursor" or "limit" or "request_timeout"))
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value.GetRawText())))));
}

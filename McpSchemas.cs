using System.Text.Json.Nodes;

namespace TgCli;

internal static class McpSchemas
{
    internal const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private static JsonObject Type(string name) => new() { ["type"] = name };
    private static JsonObject Nullable(string name) => new() { ["type"] = new JsonArray(name, "null") };
    private static JsonObject Id() => new() { ["type"] = "string", ["pattern"] = "^-?[0-9]+$" };
    private static JsonObject Array(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray())
    };
    private static JsonObject Chat() => Object(("chat_id", Id()), ("title", Type("string")),
        ("type", Type("string")), ("username", Nullable("string")), ("unread", Type("integer")), ("last_message", Nullable("string")));
    // Additional fields retain the existing versioned export schema (sender, replies,
    // entities, service events and native metadata), while the stable envelope is explicit.
    private static JsonObject Message() => Object(("schema", Type("string")), ("schema_version", Type("string")),
        ("chat_id", Id()), ("message_id", Id()), ("source_chat_id", Id()), ("date", Type("string")),
        ("kind", Type("string")), ("text", Type("string")), ("attachments", Array(Type("object"))), ("is_deleted", Type("boolean")));
    private static JsonObject Topic() => Object(("chat_id", Id()), ("topic_id", Type("integer")),
        ("name", Type("string")), ("is_general", Type("boolean")), ("is_closed", Type("boolean")),
        ("is_hidden", Type("boolean")), ("is_pinned", Type("boolean")), ("unread_count", Type("integer")), ("last_message_id", Id()));
    private static JsonObject Page(JsonObject item) => Object(("items", Array(item)), ("next_cursor", Nullable("string")),
        ("has_more", Type("boolean")), ("complete", Type("boolean")), ("termination_reason", Type("string")));

    internal static JsonObject Output(string name)
    {
        var schema = name switch
        {
            "chat_list" or "chat_search" => Page(Chat()),
            "chat_resolve" => Chat(),
            "chat_messages" or "message_search" or "chat_context" => Page(Message()),
            "message_get" => Message(),
            "forum_topics" => Page(Topic()),
            "chat_stats" => Object(("chat_id", Id()), ("topic_id", Nullable("integer")), ("count", Type("integer")),
                ("count_kind", Type("string")), ("complete", Type("boolean")), ("termination_reason", Type("string")),
                ("pages_fetched", Type("integer")), ("first_timestamp", Nullable("string")), ("last_timestamp", Nullable("string")),
                ("participant_count", Nullable("integer")), ("attachments", Type("object")), ("migrations", Array(Id()))),
            "message_link" => Object(("chat_id", Id()), ("message_id", Id()), ("short_message_id", Id()),
                ("tg_url", Type("string")), ("https_url", Nullable("string")), ("https_fallback", Nullable("string"))),
            "attachment_download" => Object(("path", Type("string")), ("file_id", Type("integer")),
                ("size_bytes", Type("integer")), ("status", Type("string"))),
            "diagnostics" => Object(("healthy", Type("boolean")), ("user_id", Id()), ("display_name", Type("string")),
                ("session_directory", Type("string")), ("checked_at", Type("string"))),
            _ => throw new ArgumentException("Unknown tool schema.", nameof(name))
        };
        schema["$schema"] = Dialect;
        return schema;
    }
}

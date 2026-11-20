using System.Text.Json.Nodes;

namespace TgCli;

internal static class McpSchemas
{
    internal const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private static JsonObject Type(string name) => new() { ["type"] = name };
    private static JsonObject Nullable(string name) => new() { ["type"] = new JsonArray(name, "null") };
    private static JsonObject Id(bool nullable = false)
    {
        var schema = nullable ? Nullable("string") : Type("string");
        schema["pattern"] = "^-?[0-9]+$";
        return schema;
    }
    private static JsonObject Fixed(string value) => new() { ["type"] = "string", ["const"] = value };
    private static JsonObject Timestamp(bool nullable = false)
    {
        var schema = nullable ? Nullable("string") : Type("string");
        schema["format"] = "date-time";
        return schema;
    }
    private static JsonObject Array(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray())
    };
    private static JsonObject OrNull(JsonObject schema)
    {
        schema["type"] = new JsonArray("object", "null");
        return schema;
    }

    private static JsonObject Chat() => Object(("chat_id", Id()), ("title", Type("string")),
        ("type", Type("string")), ("username", Nullable("string")), ("unread", Type("integer")), ("last_message", Nullable("string")));
    private static JsonObject Attachment() => Object(("kind", Type("string")), ("role", Type("string")), ("file_id", Id()),
        ("remote_id", Nullable("string")), ("unique_id", Nullable("string")), ("size", Nullable("integer")),
        ("file_name", Nullable("string")), ("mime_type", Nullable("string")), ("width", Nullable("integer")),
        ("height", Nullable("integer")), ("duration", Nullable("integer")), ("property_path", Type("string")),
        ("caption", Type("string")), ("download_state", Type("string")), ("local_path", Nullable("string")),
        ("generated_text", Nullable("object")));
    private static JsonObject Link() => Object(("chat_id", Id()), ("message_id", Id()), ("short_message_id", Id(nullable: true)),
        ("tg_url", Nullable("string")), ("tg_url_scope", Nullable("string")),
        ("https_url", Nullable("string")), ("https_fallback", Nullable("string")));
    private static JsonObject Message() => Object(("schema", Fixed(ExportSchema.Name)), ("schema_version", Fixed(ExportSchema.Version)),
        ("chat_id", Id()), ("message_id", Id()), ("source_chat_id", Id()), ("original_chat_id", Id()), ("original_message_id", Id()),
        ("short_message_id", Id(nullable: true)), ("date", Timestamp()), ("edit_date", Timestamp(nullable: true)),
        ("query", Nullable("string")), ("sender_user_id", Id(nullable: true)), ("sender_chat_id", Id(nullable: true)),
        ("reply_to_chat_id", Id(nullable: true)), ("reply_to_message_id", Id(nullable: true)),
        ("kind", Type("string")), ("text", Type("string")), ("attachments", Array(Attachment())),
        ("links", OrNull(Link())), ("is_deleted", Type("boolean")));
    private static JsonObject Topic() => Object(("chat_id", Id()), ("topic_id", Id()),
        ("name", Type("string")), ("is_general", Type("boolean")), ("is_closed", Type("boolean")),
        ("is_hidden", Type("boolean")), ("is_pinned", Type("boolean")), ("unread_count", Type("integer")), ("last_message_id", Id(nullable: true)));
    private static JsonObject TopicOffset() => Object(("date", Type("integer")), ("message_id", Id()), ("topic_id", Id()));
    private static JsonObject Context() => Object(("window_complete", Type("boolean")),
        ("older_complete", Type("boolean")), ("newer_complete", Type("boolean")),
        ("older_count", Type("integer")), ("newer_count", Type("integer")), ("reply_chain_status", Type("string")),
        ("unavailable_ancestor", OrNull(Object(("chat_id", Id()), ("message_id", Id())))));
    private static JsonObject Page(JsonObject item, params (string Name, JsonObject Schema)[] extra) => Object([
        ("items", Array(item)), ("source", Type("string")), ("freshness", Type("string")), ("requested_scope", Type("object")),
        ("next_cursor", Nullable("string")), ("continuation", Nullable("string")), ("has_more", Type("boolean")),
        ("complete", Type("boolean")), ("limit_satisfied", Type("boolean")), ("termination_reason", Type("string")),
        ("pages_fetched", Nullable("integer")), ("rows_fetched", Type("integer")), ..extra]);

    private static JsonObject Diagnostics() => Object(("healthy", Type("boolean")), ("checked_at", Timestamp()),
        ("latency_ms", Nullable("number")), ("authorized", Nullable("boolean")), ("authorization_state", Type("string")),
        ("connection_state", Type("string")), ("network_available", Nullable("boolean")), ("session_directory", Type("string")),
        ("database_directory", Type("string")), ("database_exists", Type("boolean")), ("database_size_bytes", Nullable("integer")),
        ("tdlib_version", Nullable("string")), ("authenticated_account", OrNull(Object(
            ("user_id", Id()), ("display_name", Type("string")), ("username", Nullable("string"))))),
        ("lock_status", OrNull(Object(("session_directory", Type("string")), ("locked", Type("boolean")),
            ("owner", Nullable("string")), ("owner_metadata_stale", Type("boolean")), ("lock_path", Type("string"))))),
        ("error_code", Nullable("string")), ("diagnostic_message", Nullable("string")));

    internal static JsonObject Output(string name)
    {
        var schema = name switch
        {
            "chat_list" or "chat_search" => Page(Chat()),
            "chat_resolve" => Chat(),
            "chat_messages" or "message_search" => Page(Message(), ("next_message_id", Id(nullable: true))),
            "chat_context" => Page(Message(), ("context", Context())),
            "message_get" => Message(),
            "forum_topics" => Page(Topic(), ("next_offset", OrNull(TopicOffset()))),
            "chat_stats" => Object(("chat_id", Id()), ("topic_id", Id(nullable: true)), ("count", Type("integer")),
                ("count_kind", Type("string")), ("source", Type("string")), ("freshness", Type("string")),
                ("requested_scope", Type("object")), ("complete", Type("boolean")), ("termination_reason", Type("string")),
                ("pages_fetched", Type("integer")), ("first_timestamp", Timestamp(nullable: true)), ("last_timestamp", Timestamp(nullable: true)),
                ("participant_count", Nullable("integer")), ("attachments", Type("object")), ("migrations", Array(Id()))),
            "message_link" => Link(),
            "attachment_download" => Object(("path", Type("string")), ("file_id", Id()),
                ("size_bytes", Type("integer")), ("status", Type("string"))),
            "diagnostics" => Diagnostics(),
            _ => throw new ArgumentException("Unknown tool schema.", nameof(name))
        };
        schema["$schema"] = Dialect;
        return schema;
    }
}

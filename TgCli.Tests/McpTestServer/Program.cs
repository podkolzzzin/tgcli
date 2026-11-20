using System.Text.Json.Nodes;
using TgCli;

await McpServerHost.RunAsync(new FakeBackend(), CancellationToken.None);

sealed class FakeBackend : IMcpTelegramBackend
{
    public async Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken token)
    {
        if (arguments.Text("query") == "delay") await Task.Delay(TimeSpan.FromSeconds(30), token);
        if (arguments.Text("query") == "error") throw new InvalidOperationException("Simulated Telegram failure.");
        if (name == "message_get")
        {
            var chatId = arguments.Text("chat_id");
            var messageId = arguments.Text("message_id");
            return new()
            {
                ["schema"] = ExportSchema.Name,
                ["schema_version"] = ExportSchema.Version,
                ["chat_id"] = chatId,
                ["message_id"] = messageId,
                ["source_chat_id"] = chatId,
                ["original_chat_id"] = chatId,
                ["original_message_id"] = messageId,
                ["short_message_id"] = null,
                ["date"] = "2026-01-01T00:00:00.0000000Z",
                ["edit_date"] = null,
                ["query"] = null,
                ["sender_user_id"] = null,
                ["sender_chat_id"] = null,
                ["reply_to_chat_id"] = null,
                ["reply_to_message_id"] = null,
                ["kind"] = "text",
                ["text"] = "fixture",
                ["attachments"] = new JsonArray(),
                ["links"] = null,
                ["is_deleted"] = false
            };
        }
        if (name == "diagnostics")
        {
            return new()
            {
                ["healthy"] = false,
                ["checked_at"] = "2026-01-01T00:00:00.0000000Z",
                ["latency_ms"] = null,
                ["authorized"] = false,
                ["authorization_state"] = "AuthorizationStateWaitPhoneNumber",
                ["connection_state"] = "Unknown",
                ["network_available"] = null,
                ["session_directory"] = "fixture",
                ["database_directory"] = "fixture/tdlib-db",
                ["database_exists"] = false,
                ["database_size_bytes"] = 0,
                ["tdlib_version"] = null,
                ["authenticated_account"] = null,
                ["lock_status"] = null,
                ["error_code"] = "login_required",
                ["diagnostic_message"] = "Fixture session requires authorization."
            };
        }
        throw new InvalidOperationException("Unsupported protocol fixture operation.");
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

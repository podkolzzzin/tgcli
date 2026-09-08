using System.Text.Json.Nodes;
using TgCli;

await McpServerHost.RunAsync(new FakeBackend(), CancellationToken.None);

sealed class FakeBackend : IMcpTelegramBackend
{
    public async Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken token)
    {
        if (arguments.Text("query") == "delay") await Task.Delay(TimeSpan.FromSeconds(30), token);
        if (arguments.Text("query") == "error") throw new InvalidOperationException("Simulated Telegram failure.");
        return new() { ["tool"] = name, ["chat_id"] = arguments.Text("chat_id"), ["query"] = arguments.Text("query") };
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

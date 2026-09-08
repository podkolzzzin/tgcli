using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Protocol;

namespace TgCli;

// SDK 2.2.0 checks the session version before checking supported versions, and always
// advertises logging. Keep these compatibility corrections at the transport boundary.
// The SDK still owns framing, dispatch, cancellation, metadata projection and serialization.
internal sealed class McpCompatibilityTransport : ITransport
{
    internal const string ModernVersion = "2026-07-28";
    private readonly ITransport _inner;
    private readonly Channel<JsonRpcMessage> _incoming = Channel.CreateBounded<JsonRpcMessage>(64);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    // 0 = not negotiated, 1 = per-request metadata, 2 = initialize handshake.
    private int _mode;
    private int _disposed;

    internal McpCompatibilityTransport(ITransport inner) { _inner = inner; _pump = PumpAsync(); }
    public string? SessionId => _inner.SessionId;
    public ChannelReader<JsonRpcMessage> MessageReader => _incoming.Reader;

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var message in _inner.MessageReader.ReadAllAsync(_stop.Token))
            {
                if (message is JsonRpcRequest { Method: "initialize" })
                {
                    if (_mode == 0) _mode = 2;
                }
                else if (message is JsonRpcRequest request)
                {
                    var meta = (request.Params as JsonObject)?["_meta"] as JsonObject;
                    var versionNode = meta?["io.modelcontextprotocol/protocolVersion"];
                    var version = versionNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (version is not null && version != ModernVersion)
                    {
                        await RejectAsync(request, -32022, "Unsupported protocol version.", new JsonObject
                        { ["requested"] = version, ["supported"] = new JsonArray(ModernVersion) });
                        continue;
                    }
                    if (_mode == 0 && version is null)
                    {
                        await RejectAsync(request, -32602, "Before a legacy initialize handshake, each request requires protocolVersion and clientCapabilities in _meta.");
                        continue;
                    }
                    if (_mode == 1 || _mode == 0 && version == ModernVersion)
                    {
                        if (version != ModernVersion || meta?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                        {
                            await RejectAsync(request, -32602, "Each modern request requires protocolVersion and clientCapabilities in _meta.");
                            continue;
                        }
                        if (_mode == 0) _mode = 1;
                    }
                }
                await _incoming.Writer.WriteAsync(message, _stop.Token);
            }
            _incoming.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _incoming.Writer.TryComplete(); }
        catch (Exception ex) { _incoming.Writer.TryComplete(ex); }
    }

    private Task RejectAsync(JsonRpcRequest request, int code, string message, JsonObject? data = null) =>
        _inner.SendMessageAsync(new JsonRpcError
        { Id = request.Id, Error = new() { Code = code, Message = message, Data = data } }, _stop.Token);

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (message is JsonRpcResponse { Result: JsonObject result } &&
            (result.ContainsKey("supportedVersions") || result.ContainsKey("protocolVersion")) &&
            result["capabilities"] is JsonObject capabilities)
            capabilities.Remove("logging");
        return _inner.SendMessageAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        await _pump;
        await _inner.DisposeAsync();
        // The host and SDK can both dispose their shared transport.
    }
}

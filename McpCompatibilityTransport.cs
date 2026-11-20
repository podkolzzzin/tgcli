using ModelContextProtocol;
using System.Globalization;
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
    private enum ProtocolMode { Unnegotiated, Modern, Legacy }
    private sealed record PendingInitialize(RequestId Id)
    {
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private ProtocolMode _mode;
    private PendingInitialize? _pendingInitialize;
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
                if (message is JsonRpcRequest request)
                {
                    if (request.Method == "initialize")
                    {
                        await InitializeAsync(request);
                        continue;
                    }
                    if (!await AcceptRequestAsync(request)) continue;
                }
                await _incoming.Writer.WriteAsync(message, _stop.Token);
            }
            _incoming.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _incoming.Writer.TryComplete(); }
        catch (Exception ex) { _incoming.Writer.TryComplete(ex); }
    }

    private async Task InitializeAsync(JsonRpcRequest request)
    {
        if (_mode != ProtocolMode.Unnegotiated)
        {
            await RejectAsync(request, -32600, "This connection has already selected an MCP protocol mode.");
            return;
        }
        if (request.Params is not JsonObject parameters ||
            ReadString(parameters["protocolVersion"]) is not { } version || !IsVersion(version) ||
            parameters["capabilities"] is not JsonObject ||
            parameters["clientInfo"] is not JsonObject clientInfo ||
            ReadString(clientInfo["name"]) is null || ReadString(clientInfo["version"]) is null)
        {
            await RejectAsync(request, -32602, "Initialize requires protocolVersion, capabilities and clientInfo.");
            return;
        }
        if ((parameters["_meta"] as JsonObject)?.ContainsKey("io.modelcontextprotocol/protocolVersion") == true)
        {
            await RejectAsync(request, -32602, "Initialize uses legacy handshake parameters. Use server/discover with modern request metadata.");
            return;
        }

        // Route only one initialize at a time, and make pipelined requests wait
        // until its response has determined whether the handshake succeeded.
        var pending = new PendingInitialize(request.Id);
        Volatile.Write(ref _pendingInitialize, pending);
        try
        {
            await _incoming.Writer.WriteAsync(request, _stop.Token);
            if (await pending.Completion.Task.WaitAsync(_stop.Token))
                _mode = ProtocolMode.Legacy;
        }
        finally { Volatile.Write(ref _pendingInitialize, null); }
    }

    private async Task<bool> AcceptRequestAsync(JsonRpcRequest request)
    {
        var meta = (request.Params as JsonObject)?["_meta"] as JsonObject;
        var hasVersion = meta?.ContainsKey("io.modelcontextprotocol/protocolVersion") == true;
        var version = ReadString(meta?["io.modelcontextprotocol/protocolVersion"]);
        if (hasVersion && (version is null || !IsVersion(version)))
        {
            await RejectAsync(request, -32602, "Protocol version must be a date in YYYY-MM-DD format.");
            return false;
        }
        if (version is not null && version != ModernVersion)
        {
            await RejectAsync(request, -32022, "Unsupported protocol version.", new JsonObject
            { ["requested"] = version, ["supported"] = new JsonArray(ModernVersion) });
            return false;
        }
        if (_mode == ProtocolMode.Legacy)
        {
            if (version is null) return true;
            await RejectAsync(request, -32602, "This connection uses the legacy initialize handshake.");
            return false;
        }
        if (version is null || meta?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
        {
            await RejectAsync(request, -32602, "Modern requests require protocolVersion and clientCapabilities in _meta. Legacy clients must initialize first.");
            return false;
        }

        _mode = ProtocolMode.Modern;
        return true;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static bool IsVersion(string version) =>
        version.Length == 10 && DateOnly.TryParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private Task RejectAsync(JsonRpcRequest request, int code, string message, JsonObject? data = null) =>
        _inner.SendMessageAsync(new JsonRpcError
        { Id = request.Id, Error = new() { Code = code, Message = message, Data = data } }, _stop.Token);

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        var pending = Volatile.Read(ref _pendingInitialize);
        var completesInitialize = pending is not null && (message switch
        {
            JsonRpcResponse response => response.Id == pending.Id,
            JsonRpcError error => error.Id == pending.Id,
            _ => false
        });
        var initialized = false;
        try
        {
            if (message is JsonRpcResponse { Result: JsonObject result } &&
                (result.ContainsKey("supportedVersions") || result.ContainsKey("protocolVersion")) &&
                result["capabilities"] is JsonObject capabilities)
                capabilities.Remove("logging");

            // SDK method-not-found text echoes the caller's method name.
            if (message is JsonRpcError { Error.Code: (int)McpErrorCode.MethodNotFound } error)
                error.Error = new JsonRpcErrorDetail { Code = error.Error.Code, Message = "Method not found." };

            await _inner.SendMessageAsync(message, cancellationToken);
            initialized = message is JsonRpcResponse { Result: JsonObject responseResult } &&
                ReadString(responseResult["protocolVersion"]) is { } version && version != ModernVersion;
        }
        finally
        {
            if (completesInitialize) pending!.Completion.TrySetResult(initialized);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        await _pump;
        await _inner.DisposeAsync();
        _stop.Dispose();
        // The host and SDK can both dispose their shared transport.
    }
}

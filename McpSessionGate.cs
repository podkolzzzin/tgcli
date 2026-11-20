namespace TgCli;

// Request cancellation ends the caller's wait. The session gate also waits for
// pending native work from a canceled startup or readiness check before reuse.
internal sealed class McpSessionGate<T>(Func<CancellationToken, Task<T>> create, TimeSpan? drainTimeout = null)
    : IAsyncDisposable where T : class, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _shutdownGate = new();
    private T? _session;
    private Task? _retirement;
    private Task? _shutdownTask;

    internal McpSessionGate(Func<Task<T>> create, TimeSpan? drainTimeout = null)
        : this(_ => create(), drainTimeout) { }

    internal async Task<TResult> ExecuteAsync<TResult>(Func<T, CancellationToken, Task<TResult>> action,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        return await OperationDeadline.RunAsync(token => RunAsync(action, token), timeout, request.Token, "Telegram request");
    }

    private async Task<TResult> RunAsync<TResult>(Func<T, CancellationToken, Task<TResult>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await OperationDeadline.RetainAbandonedWorkAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (_retirement is not null)
                {
                    await _retirement.WaitAsync(token);
                    _retirement = null;
                }

                _session ??= await create(token);
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await action(_session, token);
                    token.ThrowIfCancellationRequested();
                    if (_session is TelegramSession telegram) telegram.CheckActive();
                    return result;
                }
                catch (Exception error) when (TelegramRpcPolicy.Classify(error).Kind == TelegramRpcFailureKind.Authorization)
                {
                    _retirement = CloseAsync(_session);
                    _session = null;
                    OperationDeadline.Observe(_retirement);
                    if (error is TdLib.TdException nativeError) throw TelegramAuthorizationException.FromNative(nativeError);
                    throw;
                }
            });
        }
        finally { _gate.Release(); }
    }

    private static async Task CloseAsync(T session)
    {
        if (session is TelegramSession telegram) await telegram.DisposeWhenDrainedAsync();
        else await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Task shutdown;
        lock (_shutdownGate) shutdown = _shutdownTask ??= ShutdownAsync();
        try { await shutdown.WaitAsync(drainTimeout ?? TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Telegram work is still draining. Its session lock remains held until shutdown or process exit.");
            OperationDeadline.Observe(shutdown);
        }
    }

    private async Task ShutdownAsync()
    {
        try { await _shutdown.CancelAsync(); }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (_retirement is not null) await _retirement;
                if (_session is not null) await CloseAsync(_session);
            }
            finally { _gate.Release(); }
        }
    }
}

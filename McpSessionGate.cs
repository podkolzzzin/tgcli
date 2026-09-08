namespace TgCli;

// Cancellation stops the caller's wait, but native TDLib work must finish before
// another operation acquires the session. Never detach work from this gate.
internal sealed class McpSessionGate<T>(Func<Task<T>> create, TimeSpan? drainTimeout = null) : IAsyncDisposable where T : class, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private T? _session;
    private int _disposed;

    internal async Task<TResult> ExecuteAsync<TResult>(Func<T, CancellationToken, Task<TResult>> action,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        request.CancelAfter(timeout);
        var operation = RunAsync(action, request.Token);
        try { return await operation.WaitAsync(request.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            Observe(operation);
            throw new TimeoutException("Telegram request timed out. Outstanding native work retains the session gate until it finishes; retry later.");
        }
        catch { Observe(operation); throw; }
    }

    private async Task<TResult> RunAsync<TResult>(Func<T, CancellationToken, Task<TResult>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            _session ??= await create();
            token.ThrowIfCancellationRequested();
            return await action(_session, token);
        }
        finally { _gate.Release(); }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync();
        if (!await _gate.WaitAsync(drainTimeout ?? TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine("Telegram operation did not drain during shutdown; process termination will release its session lock.");
            return;
        }
        try { if (_session is not null) await _session.DisposeAsync(); }
        finally { _gate.Release(); }
    }
}

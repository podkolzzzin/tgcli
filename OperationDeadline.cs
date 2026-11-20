namespace TgCli;

internal static class OperationDeadline
{
    private static readonly AsyncLocal<RetainedWork?> ActiveRetention = new();

    // The action owns its resources. A timeout ends the caller's wait; the action
    // must keep any native session or gate until its underlying work finishes.
    internal static async Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> action,
        TimeSpan timeout, CancellationToken cancellationToken = default, string operation = "Telegram operation",
        Func<TResult, Task>? onAbandoned = null)
    {
        using var timer = new CancellationTokenSource(timeout);
        if (timeout == TimeSpan.Zero) timer.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var token = request.Token;
        Task<TResult>? work = null;
        try
        {
            token.ThrowIfCancellationRequested();
            work = action(token);
            var result = await work.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timer.IsCancellationRequested)
        {
            if (work is not null) Retain(onAbandoned is null ? work : ReleaseAbandonedResult(work, onAbandoned));
            throw new TimeoutException($"{operation} exceeded its deadline.", ex);
        }
        catch
        {
            if (work is not null) Retain(onAbandoned is null ? work : ReleaseAbandonedResult(work, onAbandoned));
            throw;
        }
    }

    internal static Task RunAsync(Func<CancellationToken, Task> action,
        TimeSpan timeout, CancellationToken cancellationToken = default, string operation = "Telegram operation") =>
        RunAsync(async token =>
        {
            await action(token);
            return true;
        }, timeout, cancellationToken, operation);

    internal static async Task<TResult> RetainAbandonedWorkAsync<TResult>(Func<Task<TResult>> action)
    {
        var previous = ActiveRetention.Value;
        var retained = new RetainedWork();
        ActiveRetention.Value = retained;
        try { return await action(); }
        finally
        {
            ActiveRetention.Value = previous;
            await retained.DrainAsync();
        }
    }

    internal static void Retain(Task task)
    {
        Observe(task);
        if (!task.IsCompleted) ActiveRetention.Value?.Add(task);
    }

    internal static void Observe(Task task) =>
        _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task ReleaseAbandonedResult<TResult>(Task<TResult> work, Func<TResult, Task> release)
    {
        TResult result;
        try { result = await work; }
        catch { return; }
        await release(result);
    }

    private sealed class RetainedWork
    {
        private readonly List<Task> _tasks = [];

        internal void Add(Task task)
        {
            lock (_tasks) _tasks.Add(task);
        }

        internal async Task DrainAsync()
        {
            for (var index = 0; ; index++)
            {
                Task task;
                lock (_tasks)
                {
                    if (index == _tasks.Count) return;
                    task = _tasks[index];
                }
                try { await task; }
                catch { /* The caller already received the operation failure or cancellation. */ }
            }
        }
    }
}

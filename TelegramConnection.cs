using System.Diagnostics;
using TdLib;

namespace TgCli;

// Authorization can be restored from disk before TDLib connects and catches up.
// Observe connection updates independently, including reconnects in long-lived clients.
internal sealed class TelegramConnection
{
    private readonly object _gate = new();
    private TdApi.ConnectionState? _state;
    private TaskCompletionSource _changed = NewSource();

    public bool IsReady { get { lock (_gate) return _state is TdApi.ConnectionState.ConnectionStateReady; } }
    public string StateName { get { lock (_gate) return _state?.GetType().Name ?? "Unknown"; } }

    public void Update(TdApi.ConnectionState state)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _state = state;
            changed = _changed;
            _changed = NewSource();
        }
        changed.TrySetResult();
    }

    public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                lock (_gate)
                {
                    if (_state is TdApi.ConnectionState.ConnectionStateReady) return;
                    changed = _changed.Task;
                }
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await changed.WaitAsync(remaining, cancellationToken);
            }
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for Telegram connection and update synchronization (state: {StateName}). Cached data has not been verified as current. Retry online, or explicitly use --local where supported.", ex);
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

using System.Diagnostics;

namespace TgCli;

internal static class CliExecution
{
    private static readonly AsyncLocal<Invocation?> Current = new();

    internal static CancellationToken CancellationToken => Current.Value?.Token ?? CancellationToken.None;
    internal static CliDiagnosticOutput? Diagnostics => Current.Value?.Diagnostics;

    internal static TimeSpan Remaining
    {
        get
        {
            var invocation = Current.Value;
            if (invocation is null || invocation.Budget == Timeout.InfiniteTimeSpan)
                return Timeout.InfiniteTimeSpan;
            if (invocation.Token.IsCancellationRequested) return TimeSpan.Zero;
            var remaining = invocation.Budget - Stopwatch.GetElapsedTime(invocation.Started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal static async Task<Exception?> RunAsync(Func<CancellationToken, Task> command, TimeSpan budget,
        CancellationToken cancellationToken, CliDiagnosticOutput? diagnostics = null)
    {
        var started = Stopwatch.GetTimestamp();
        Exception? failure;
        try
        {
            failure = await OperationDeadline.RunAsync(token => Task.Run(async () =>
            {
                var previous = Current.Value;
                var invocation = new Invocation(token, budget, started, diagnostics);
                Current.Value = invocation;
                try
                {
                    token.ThrowIfCancellationRequested();
                    await command(token);
                    return invocation.Failure;
                }
                finally { Current.Value = previous; }
            }, CancellationToken.None), budget, cancellationToken, "Command");
        }
        catch (TimeoutException) when (diagnostics is not null)
        {
            diagnostics.WriteDeadline(Stopwatch.GetElapsedTime(started));
            throw;
        }
        diagnostics?.WriteResult();
        return failure;
    }

    internal static void RecordFailure(Exception error)
    {
        if (Current.Value is not { } invocation) throw new InvalidOperationException("No CLI invocation is active.", error);
        Interlocked.CompareExchange(ref invocation.Failure, error, null);
    }

    private sealed class Invocation(CancellationToken token, TimeSpan budget, long started, CliDiagnosticOutput? diagnostics)
    {
        internal CancellationToken Token { get; } = token;
        internal TimeSpan Budget { get; } = budget;
        internal long Started { get; } = started;
        internal CliDiagnosticOutput? Diagnostics { get; } = diagnostics;
        internal Exception? Failure;
    }
}

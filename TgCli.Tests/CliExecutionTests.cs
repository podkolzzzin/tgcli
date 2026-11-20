using System;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using TgCli;
using Xunit;

public sealed class CliExecutionTests
{
    [Fact]
    public async Task One_budget_survives_awaits_and_cancels_late_work()
    {
        var testToken = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new StringWriter();
        using var guardedOutput = new CliOutputWriter(output);

        var command = CliExecution.RunAsync(async token =>
        {
            Assert.Equal(token, CliExecution.CancellationToken);
            Assert.InRange(CliExecution.Remaining, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
            await release.Task;
            Assert.True(CliExecution.CancellationToken.IsCancellationRequested);
            Assert.Equal(TimeSpan.Zero, CliExecution.Remaining);
            Assert.Throws<OperationCanceledException>(() => guardedOutput.WriteLine("late success"));
            drained.SetResult();
        }, TimeSpan.FromMilliseconds(200), testToken);

        try { await Assert.ThrowsAsync<TimeoutException>(() => command); }
        finally { release.SetResult(); }
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
        Assert.Empty(output.ToString());
        Assert.False(CliExecution.CancellationToken.CanBeCanceled);
        Assert.Equal(Timeout.InfiniteTimeSpan, CliExecution.Remaining);
    }

    [Fact]
    public async Task Handler_failure_keeps_its_type_across_dispatch()
    {
        var error = new PartialResultException("The requested range is incomplete.");
        var result = await CliExecution.RunAsync(_ =>
        {
            CliExecution.RecordFailure(error);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Same(error, result);
        Assert.Equal(3, CliFailure.FromException(error).ExitCode);
        Assert.Equal(4, CliFailure.FromException(new TelegramAuthorizationException(
            TelegramAuthorizationFailure.Revoked, "Log in again.")).ExitCode);
        Assert.Equal(2, CliFailure.FromException(new CliUsageException("Invalid selector.")).ExitCode);
        Assert.Equal(5, CliFailure.FromException(new TimeoutException()).ExitCode);
        Assert.Equal(130, CliFailure.FromException(new OperationCanceledException()).ExitCode);
        Assert.Equal(1, CliFailure.FromException(new InvalidOperationException()).ExitCode);
    }
}

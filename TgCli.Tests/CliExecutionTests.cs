using System;
using System.Threading;
using System.IO;
using Newtonsoft.Json.Linq;
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
    public async Task Diagnostic_deadline_writes_observed_lock_once_even_if_handler_returns_late()
    {
        var testToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("tgcli-diagnostic-deadline-");
        try
        {
            using var lease = SessionLock.Acquire(root.FullName);
            using var output = new StringWriter();
            using var guardedOutput = new CliOutputWriter(output);
            var diagnostics = new CliDiagnosticOutput(root.FullName, guardedOutput);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var command = CliExecution.RunAsync(async _ =>
            {
                CliExecution.Diagnostics!.Observe(SessionDiagnostic.ObserveStorage(root.FullName));
                started.SetResult();
                await release.Task;
                CliExecution.Diagnostics.Complete(new JObject { ["healthy"] = true });
                drained.SetResult();
            }, TimeSpan.FromSeconds(1), testToken, diagnostics);

            string published;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
                var error = await Assert.ThrowsAsync<TimeoutException>(() => command);
                Assert.Equal(5, CliFailure.FromException(error).ExitCode);
                published = output.ToString();
                var result = JObject.Parse(Assert.Single(published.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
                Assert.False(result.Value<bool>("healthy"));
                Assert.Equal("request_timeout", result.Value<string>("error_code"));
                Assert.True(result.Value<double>("latency_ms") > 0);
                Assert.True(result["lock_status"]!.Value<bool>("locked"));
                Assert.False(result.Value<bool>("database_exists"));
                Assert.Equal(JTokenType.Null, result["authorized"]!.Type);
            }
            finally { release.TrySetResult(); }
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            Assert.Equal(published, output.ToString());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Diagnostic_deadline_before_dispatch_does_not_invent_lock_or_database_evidence()
    {
        using var output = new StringWriter();
        var root = Path.GetFullPath("synthetic-unobserved-session");
        var diagnostics = new CliDiagnosticOutput(root, output);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => CliExecution.RunAsync(
            _ => throw new InvalidOperationException("The expired command must never dispatch."),
            TimeSpan.Zero, TestContext.Current.CancellationToken, diagnostics));

        Assert.Equal(5, CliFailure.FromException(error).ExitCode);
        var result = JObject.Parse(output.ToString());
        Assert.Equal(root, result.Value<string>("session_directory"));
        Assert.Equal("request_timeout", result.Value<string>("error_code"));
        Assert.Equal(JTokenType.Null, result["lock_status"]!.Type);
        Assert.Equal(JTokenType.Null, result["database_exists"]!.Type);
    }

    [Fact]
    public async Task Diagnostic_interrupt_does_not_publish_a_timeout_record()
    {
        using var output = new StringWriter();
        using var interrupt = new CancellationTokenSource();
        interrupt.Cancel();
        var diagnostics = new CliDiagnosticOutput(Path.GetFullPath("synthetic-canceled-session"), output);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliExecution.RunAsync(
            _ => throw new InvalidOperationException("The interrupted command must never dispatch."),
            TimeSpan.FromSeconds(5), interrupt.Token, diagnostics));

        Assert.Equal(130, CliFailure.FromException(error).ExitCode);
        Assert.Empty(output.ToString());
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

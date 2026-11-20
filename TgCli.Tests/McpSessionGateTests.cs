using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpSessionGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ParallelFirstCallsCreateOneSessionAndSerializeOperations()
    {
        var creates = 0;
        var resource = new Resource();
        await using var gate = new McpSessionGate<Resource>(() => { creates++; return Task.FromResult(resource); });
        Assert.Equal(0, creates);
        var entered = NewSignal(); var release = NewSignal(); var secondEntered = false;
        var first = gate.ExecuteAsync(async (r, _) => { entered.SetResult(); await release.Task; return r; }, Timeout, Ct);
        await entered.Task.WaitAsync(Ct);
        var second = gate.ExecuteAsync((r, _) => { secondEntered = true; return Task.FromResult(r); }, Timeout, Ct);
        Assert.False(secondEntered);
        release.SetResult();
        Assert.Same(resource, await first);
        Assert.Same(resource, await second);
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task CanceledNativeWorkKeepsGateAndQueuedCancellationDoesNotExecute()
    {
        var resource = new Resource();
        await using var gate = new McpSessionGate<Resource>(() => Task.FromResult(resource));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = NewSignal(); var nativeComplete = NewSignal();
        var first = gate.ExecuteAsync(async (_, _) => { entered.SetResult(); await nativeComplete.Task; return 1; }, Timeout, cancel.Token);
        await entered.Task.WaitAsync(Ct);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var ran = false;
        await Assert.ThrowsAsync<TimeoutException>(() => gate.ExecuteAsync((_, _) => { ran = true; return Task.FromResult(2); }, TimeSpan.FromMilliseconds(50), Ct));
        Assert.False(ran);
        Assert.False(resource.Disposed);
        nativeComplete.SetResult();
        Assert.Equal(3, await gate.ExecuteAsync((_, _) => Task.FromResult(3), Timeout, Ct));
    }

    [Fact]
    public async Task FailedInitializationCanBeRetriedAndToolFailurePreservesSession()
    {
        var creates = 0;
        var resource = new Resource();
        await using var gate = new McpSessionGate<Resource>(() => ++creates == 1
            ? Task.FromException<Resource>(new InvalidOperationException("Login required")) : Task.FromResult(resource));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ExecuteAsync((_, _) => Task.FromResult(1), Timeout, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gate.ExecuteAsync<int>((_, _) => throw new ArgumentException("Bad operation"), Timeout, Ct));
        Assert.Equal(1, await gate.ExecuteAsync((_, _) => Task.FromResult(1), Timeout, Ct));
        Assert.Equal(2, creates);
        await gate.DisposeAsync();
        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task ShutdownCancelsActiveWorkAndRejectsFurtherCalls()
    {
        var resource = new Resource(); var entered = NewSignal();
        await using var gate = new McpSessionGate<Resource>(() => Task.FromResult(resource));
        var active = gate.ExecuteAsync(async (_, token) => { entered.SetResult(); await Task.Delay(System.Threading.Timeout.Infinite, token); return 0; }, Timeout, Ct);
        await entered.Task.WaitAsync(Ct);
        await gate.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ExecuteAsync((_, _) => Task.FromResult(0), Timeout, Ct));
        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task ShutdownDeadlineStillDisposesAfterOutstandingNativeWorkFinishes()
    {
        var resource = new Resource();
        var entered = NewSignal();
        var nativeComplete = NewSignal();
        await using var gate = new McpSessionGate<Resource>(() => Task.FromResult(resource), TimeSpan.FromMilliseconds(50));
        var active = gate.ExecuteAsync(async (_, _) =>
        {
            entered.SetResult();
            await nativeComplete.Task;
            return 1;
        }, Timeout, Ct);
        await entered.Task.WaitAsync(Ct);

        try
        {
            await gate.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            Assert.False(resource.Disposed);
        }
        finally { nativeComplete.TrySetResult(); }

        await resource.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task TerminalAuthorizationFailureRetiresSessionAndNextRequestRecreatesIt()
    {
        var first = new Resource();
        var second = new Resource();
        var creations = 0;
        await using var gate = new McpSessionGate<Resource>(() => Task.FromResult(++creations == 1 ? first : second));
        await Assert.ThrowsAsync<TelegramAuthorizationException>(() => gate.ExecuteAsync<int>((_, _) =>
            throw new TelegramAuthorizationException(TelegramAuthorizationFailure.Revoked, "Synthetic revocation"), Timeout, Ct));
        Assert.Same(second, await gate.ExecuteAsync((resource, _) => Task.FromResult(resource), Timeout, Ct));
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task CancellationDuringSynchronousCompletionCannotReturnSuccess()
    {
        var resource = new Resource();
        await using var gate = new McpSessionGate<Resource>(() => Task.FromResult(resource));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ExecuteAsync((_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(123);
        }, Timeout, cancel.Token));
    }

    private sealed class Resource : IAsyncDisposable
    {
        internal bool Disposed;
        internal TaskCompletionSource Disposal { get; } = NewSignal();
        public ValueTask DisposeAsync()
        {
            Assert.False(Disposed);
            Disposed = true;
            Disposal.SetResult();
            return ValueTask.CompletedTask;
        }
    }
}

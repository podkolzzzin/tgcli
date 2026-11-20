using System;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class TelegramConnectionTests
{
    [Fact]
    public async Task CachedAuthorizationDoesNotImplySyncedConnection()
    {
        var connection = new TelegramConnection();
        var wait = connection.WaitReadyAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        connection.Update(new TdApi.ConnectionState.ConnectionStateConnecting());
        Assert.False(wait.IsCompleted);
        connection.Update(new TdApi.ConnectionState.ConnectionStateUpdating());
        Assert.False(wait.IsCompleted);
        connection.Update(new TdApi.ConnectionState.ConnectionStateReady());
        await wait;
        Assert.True(connection.IsReady);
    }

    [Fact]
    public async Task ReadyUpdateBeforeWaitIsNotLost()
    {
        var connection = new TelegramConnection();
        connection.Update(new TdApi.ConnectionState.ConnectionStateReady());
        await connection.WaitReadyAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReusedSessionMustWaitAgainAfterDisconnect()
    {
        var connection = new TelegramConnection();
        connection.Update(new TdApi.ConnectionState.ConnectionStateReady());
        await connection.WaitReadyAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);
        connection.Update(new TdApi.ConnectionState.ConnectionStateWaitingForNetwork());
        Assert.False(connection.IsReady);
        var wait = connection.WaitReadyAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        connection.Update(new TdApi.ConnectionState.ConnectionStateReady());
        await wait;
    }

    [Fact]
    public async Task TimeoutReportsUnverifiedCacheInsteadOfSuccess()
    {
        var connection = new TelegramConnection();
        connection.Update(new TdApi.ConnectionState.ConnectionStateUpdating());
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => connection.WaitReadyAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Contains("ConnectionStateUpdating", ex.Message);
        Assert.Contains("Cached data has not been verified as current", ex.Message);
    }

    [Fact]
    public async Task WaitingCanBeCancelled()
    {
        var connection = new TelegramConnection();
        using var cancel = new CancellationTokenSource();
        var wait = connection.WaitReadyAsync(TimeSpan.FromSeconds(2), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        connection.Update(new TdApi.ConnectionState.ConnectionStateReady());
        await connection.WaitReadyAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);
    }
}

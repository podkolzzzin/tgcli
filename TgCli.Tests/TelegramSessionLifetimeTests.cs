#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class TelegramSessionLifetimeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tgcli-lifetime-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task CanceledStartupRetainsItsLeaseUntilPendingNativeInitializationEnds()
    {
        var entered = NewSignal();
        var nativeComplete = NewSignal();
        var native = new SyntheticClient { SetVerbosity = () => { entered.SetResult(); return nativeComplete.Task; } };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var startup = OpenAsync(native, cancel.Token);
        await entered.Task.WaitAsync(TestLimit, Ct);

        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TestLimit, Ct));
            Assert.True(SessionLock.Inspect(_root).locked);
            Assert.False(native.Closed.Task.IsCompleted);
            Assert.Throws<IOException>(() => SessionLock.Acquire(_root));
        }
        finally { nativeComplete.TrySetResult(); }

        await native.Closed.Task.WaitAsync(TestLimit, Ct);
        using var next = await SessionLock.AcquireAsync(_root, TestLimit, Ct);
    }

    [Fact]
    public async Task McpRetainsGateAcrossCanceledSessionCreationAndNativeShutdown()
    {
        var entered = NewSignal();
        var nativeComplete = NewSignal();
        var native = new SyntheticClient { SetVerbosity = () => { entered.SetResult(); return nativeComplete.Task; } };
        var creations = 0;
        await using var gate = new McpSessionGate<TelegramSession>(ct =>
            OpenAsync(++creations == 1 ? native : new SyntheticClient(), ct));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var startup = gate.ExecuteAsync((_, _) => Task.FromResult(1), TestLimit, cancel.Token);
        await entered.Task.WaitAsync(TestLimit, Ct);
        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                gate.ExecuteAsync((_, _) => Task.FromResult(2), TimeSpan.FromMilliseconds(100), Ct));
            Assert.Equal(1, creations);
            Assert.True(SessionLock.Inspect(_root).locked);
        }
        finally { nativeComplete.TrySetResult(); }

        Assert.Equal(3, await gate.ExecuteAsync((_, _) => Task.FromResult(3), TestLimit, Ct));
        Assert.Equal(2, creations);
    }

    [Fact]
    public async Task StartupDeadlineDoesNotTreatUnresponsiveInitializationAsSuccess()
    {
        var entered = NewSignal();
        var nativeComplete = NewSignal();
        var native = new SyntheticClient
        {
            GetAuthorization = () => Task.FromResult<TdApi.AuthorizationState>(new TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters()),
            SetParameters = parameters =>
            {
                Assert.Equal(1, parameters.ApiId);
                Assert.Equal("synthetic", parameters.ApiHash);
                Assert.Equal(Path.Combine(_root, "tdlib-db"), parameters.DatabaseDirectory);
                entered.SetResult();
                return nativeComplete.Task;
            }
        };
        var startup = OpenAsync(native, Ct, startupTimeout: TimeSpan.FromMilliseconds(500));
        await entered.Task.WaitAsync(TestLimit, Ct);
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => startup.WaitAsync(TestLimit, Ct));
            Assert.Contains("startup", error.Message);
            Assert.True(SessionLock.Inspect(_root).locked);
        }
        finally { nativeComplete.TrySetResult(); }

        await native.Closed.Task.WaitAsync(TestLimit, Ct);
        using var next = await SessionLock.AcquireAsync(_root, TestLimit, Ct);
    }

    [Fact]
    public async Task RevocationUpdateWinsOverAnOlderReadyReply()
    {
        var native = new SyntheticClient();
        await using var session = await OpenAsync(native, Ct);
        var entered = NewSignal();
        var oldReply = new TaskCompletionSource<TdApi.AuthorizationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        native.GetAuthorization = () => { entered.SetResult(); return oldReply.Task; };
        var readiness = session.EnsureReadyAsync(Ct);
        await entered.Task.WaitAsync(TestLimit, Ct);
        native.Update(new TdApi.AuthorizationState.AuthorizationStateLoggingOut());
        oldReply.SetResult(new TdApi.AuthorizationState.AuthorizationStateReady());

        var error = await Assert.ThrowsAsync<TelegramAuthorizationException>(() => readiness);
        Assert.Equal(TelegramAuthorizationFailure.Revoked, error.Reason);
        Assert.False(session.IsAuthorized);
        Assert.Equal("AuthorizationStateLoggingOut", session.AuthorizationState);
        await Assert.ThrowsAsync<TelegramAuthorizationException>(() => session.EnsureReadyAsync(Ct));
    }

    [Fact]
    public async Task ReadyUpdateWinsOverAnOlderLoginRequiredReply()
    {
        var native = new SyntheticClient();
        await using var session = await OpenAsync(native, Ct);
        var entered = NewSignal();
        var oldReply = new TaskCompletionSource<TdApi.AuthorizationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        native.GetAuthorization = () => { entered.SetResult(); return oldReply.Task; };
        var readiness = session.EnsureReadyAsync(Ct);
        await entered.Task.WaitAsync(TestLimit, Ct);
        native.Update(new TdApi.AuthorizationState.AuthorizationStateReady());
        oldReply.SetResult(new TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber());

        await readiness;
        Assert.True(session.IsAuthorized);
    }

    [Fact]
    public async Task RevocationDuringMcpWorkCannotReturnAnUnqualifiedSuccess()
    {
        var native = new SyntheticClient();
        var created = 0;
        await using var gate = new McpSessionGate<TelegramSession>(ct =>
            OpenAsync(++created == 1 ? native : new SyntheticClient(), ct));
        var error = await Assert.ThrowsAsync<TelegramAuthorizationException>(() => gate.ExecuteAsync((_, _) =>
        {
            native.Update(new TdApi.AuthorizationState.AuthorizationStateLoggingOut());
            return Task.FromResult(1);
        }, TestLimit, Ct));

        Assert.Equal(TelegramAuthorizationFailure.Revoked, error.Reason);
        Assert.Equal(2, await gate.ExecuteAsync((_, _) => Task.FromResult(2), TestLimit, Ct));
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task RevocationInterruptsOnlineWaitAndAReconnectCannotHideIt()
    {
        var native = new SyntheticClient();
        await using var session = await OpenAsync(native, Ct);
        var connection = session.EnsureConnectedAsync(Ct);
        native.Update(new TdApi.AuthorizationState.AuthorizationStateLoggingOut());
        native.Publish(new TdApi.Update.UpdateConnectionState { State = new TdApi.ConnectionState.ConnectionStateReady() });

        var error = await Assert.ThrowsAsync<TelegramAuthorizationException>(() => connection.WaitAsync(TestLimit, Ct));
        Assert.Equal(TelegramAuthorizationFailure.Revoked, error.Reason);
    }

    [Fact]
    public async Task ReusedSessionWaitsForReconnectBeforeOnlineReadiness()
    {
        var native = new SyntheticClient();
        await using var session = await OpenAsync(native, Ct);
        native.Publish(new TdApi.Update.UpdateConnectionState { State = new TdApi.ConnectionState.ConnectionStateReady() });
        await session.EnsureConnectedAsync(Ct);

        native.Publish(new TdApi.Update.UpdateConnectionState { State = new TdApi.ConnectionState.ConnectionStateWaitingForNetwork() });
        Assert.False(session.IsConnected);
        var reconnect = session.EnsureConnectedAsync(Ct);
        Assert.False(reconnect.IsCompleted);
        native.Publish(new TdApi.Update.UpdateConnectionState { State = new TdApi.ConnectionState.ConnectionStateReady() });
        await reconnect.WaitAsync(TestLimit, Ct);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task LocalReadinessReportsLoginRequiredWithoutWaitingForConnection()
    {
        var native = new SyntheticClient
        {
            GetAuthorization = () => Task.FromResult<TdApi.AuthorizationState>(new TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber())
        };
        await using var session = await OpenAsync(native, Ct);

        var error = await Assert.ThrowsAsync<TelegramAuthorizationException>(() => session.EnsureReadyAsync(Ct));
        Assert.Equal(TelegramAuthorizationFailure.LoginRequired, error.Reason);
        Assert.Equal("AuthorizationStateWaitPhoneNumber", session.AuthorizationState);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task CanceledReadinessLookupDoesNotStartAnotherNativeLookupUntilTheFirstFinishes()
    {
        var native = new SyntheticClient();
        await using var session = await OpenAsync(native, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = NewSignal();
        var oldReply = new TaskCompletionSource<TdApi.AuthorizationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        native.GetAuthorization = () => { entered.SetResult(); return oldReply.Task; };
        var first = session.EnsureReadyAsync(cancel.Token);
        await entered.Task.WaitAsync(TestLimit, Ct);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestLimit, Ct));

        var secondEntered = NewSignal();
        native.GetAuthorization = () => { secondEntered.SetResult(); return Task.FromResult<TdApi.AuthorizationState>(new TdApi.AuthorizationState.AuthorizationStateReady()); };
        var second = session.EnsureReadyAsync(Ct);
        try
        {
            Assert.False(secondEntered.Task.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { oldReply.TrySetResult(new TdApi.AuthorizationState.AuthorizationStateReady()); }
        await second.WaitAsync(TestLimit, Ct);
        Assert.True(secondEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task BoundedDisposalRetainsLeaseUntilNativeCloseReturns()
    {
        var close = NewSignal();
        var closeEntered = NewSignal();
        var native = new SyntheticClient { Close = () => { closeEntered.SetResult(); close.Task.GetAwaiter().GetResult(); } };
        var session = await OpenAsync(native, Ct, shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var disposing = session.DisposeAsync().AsTask();
        await closeEntered.Task.WaitAsync(TestLimit, Ct);

        try
        {
            await disposing.WaitAsync(TestLimit, Ct);
            Assert.True(SessionLock.Inspect(_root).locked);
        }
        finally { close.TrySetResult(); }

        await session.DisposeWhenDrainedAsync().WaitAsync(TestLimit, Ct);
        using var next = SessionLock.Acquire(_root);
    }

    private Task<TelegramSession> OpenAsync(SyntheticClient native, CancellationToken ct,
        TimeSpan? startupTimeout = null, TimeSpan? shutdownTimeout = null) =>
        TelegramSession.CreateCoreAsync(_root, apiId: 1, apiHash: "synthetic", saveConfig: false, lockTimeout: 0,
            noWait: true, () => native, startupTimeout ?? TestLimit, shutdownTimeout ?? TestLimit, ct);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SyntheticClient : ITelegramSessionNative
    {
        public event EventHandler<TdApi.Update>? UpdateReceived;
        public TdClient? Client => null;
        public Func<Task> SetVerbosity { get; init; } = () => Task.CompletedTask;
        public Func<Task<TdApi.AuthorizationState>> GetAuthorization { get; set; } =
            () => Task.FromResult<TdApi.AuthorizationState>(new TdApi.AuthorizationState.AuthorizationStateReady());
        public Func<TdApi.SetTdlibParameters, Task> SetParameters { get; init; } = _ => Task.CompletedTask;
        public Action? Close { get; init; }
        public TaskCompletionSource Closed { get; } = NewSignal();
        public Task SetLogVerbosityAsync() => SetVerbosity();
        public Task<TdApi.AuthorizationState> GetAuthorizationStateAsync() => GetAuthorization();
        public Task SetParametersAsync(TdApi.SetTdlibParameters parameters) => SetParameters(parameters);
        public void Update(TdApi.AuthorizationState state) =>
            Publish(new TdApi.Update.UpdateAuthorizationState { AuthorizationState = state });
        public void Publish(TdApi.Update update) => UpdateReceived?.Invoke(this, update);
        public void Dispose()
        {
            Close?.Invoke();
            Closed.SetResult();
        }
    }
}

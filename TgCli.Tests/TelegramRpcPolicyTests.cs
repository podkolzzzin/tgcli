using System;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class TelegramRpcPolicyTests
{
    [Theory]
    [InlineData(401, "SESSION_REVOKED", "Authorization", null)]
    [InlineData(429, "FLOOD_WAIT_17", "FloodWait", 17)]
    [InlineData(429, "Too Many Requests: retry after 5", "FloodWait", 5)]
    [InlineData(429, "Too Many Requests", "FloodWait", null)]
    [InlineData(503, "Service temporarily unavailable", "TransientRead", null)]
    [InlineData(403, "CHAT_WRITE_FORBIDDEN", "Permanent", null)]
    public void NativeErrorsKeepTheirRetryClassification(int code, string message, string kind, int? retryAfter)
    {
        var error = TelegramRpcPolicy.Classify(new TdException(new TdApi.Error { Code = code, Message = message }));
        Assert.Equal(kind, error.Kind.ToString());
        Assert.Equal(retryAfter, error.RetryAfter?.TotalSeconds);
    }

    [Fact]
    public async Task CancellationAtHandoffDisposesAnUnclaimedResult()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = OperationDeadline.RunAsync(_ =>
        {
            cancel.Cancel();
            return Task.FromResult("unclaimed");
        }, TimeSpan.FromSeconds(10), cancel.Token, onAbandoned: value =>
        {
            released.SetResult(value);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.Equal("unclaimed", await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeadlineRejectsLateSuccess()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = OperationDeadline.RunAsync(_ => release.Task, TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);
        try { await Assert.ThrowsAsync<TimeoutException>(() => result); }
        finally { release.TrySetResult(123); }
    }
}

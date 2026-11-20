#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class CliNativeIntegrationTests : IDisposable
{
    private const long ChatId = -1000000000123L;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tgcli-native-wire-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CacheOnlyRendererResolvesReplyWithoutAnyRemoteEnrichment()
    {
        var calls = new List<TdApi.Object>();
        var reply = Message(1L << 20, 1, "cached reply");
        var message = Message(2L << 20, 2, "main");
        message.SenderId = new TdApi.MessageSender.MessageSenderUser { UserId = 9007199254740993 };
        message.ReplyTo = new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = ChatId, MessageId = reply.Id };
        using var stdout = new StringWriter();
        await using var session = await OpenAsync(new SyntheticNative(function =>
        {
            calls.Add(function);
            return function is TdApi.GetMessageLocally local && local.ChatId == ChatId && local.MessageId == reply.Id
                ? Task.FromResult<TdApi.Object>(reply)
                : throw new InvalidOperationException("Unexpected enrichment: " + function.GetType().Name);
        }), TestToken);

        await Output.WriteExportedMessagesAsync(stdout, session, [Output.ToExportedMessage(message)], OutputFormat.Jsonl,
            includeLinks: true, cancellationToken: TestToken, local: true);
        var row = ExportSnapshotStore.ParseObject(stdout.ToString());
        Assert.Equal("8.0", row.Value<string>("schema_version"));
        Assert.Equal("9007199254740993", row.Value<string>("sender_user_id"));
        Assert.Equal(JTokenType.Null, row["sender_display_name"]!.Type);
        Assert.Equal("cached reply", row["reply_target"]!.Value<string>("text"));
        Assert.Equal(JTokenType.String, row["reply_target"]!["message_id"]!.Type);
        Assert.Equal("tg://privatepost?channel=123&post=2", row["links"]!.Value<string>("tg_url"));
        Assert.Equal(JTokenType.Null, row["links"]!["https_url"]!.Type);
        Assert.IsType<TdApi.GetMessageLocally>(Assert.Single(calls));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("jsonl")]
    [InlineData("tsv")]
    [InlineData("plain")]
    [InlineData("md")]
    public async Task FormatsKeepDateChatAndMessageOrdering(string format)
    {
        var messages = new[] { Message(99, 2, "late"), Message(5, 1, "tied", chatId: ChatId + 1), Message(125, 1, "first\tline") };
        using var stdout = new StringWriter();
        await using var session = await OpenAsync(new SyntheticNative(function =>
            throw new InvalidOperationException("Unexpected enrichment: " + function.GetType().Name)), TestToken);
        await Output.WriteExportedMessagesAsync(stdout, session, messages.Select(Output.ToExportedMessage), Output.ParseFormat(format),
            cancellationToken: TestToken, local: true);
        var value = stdout.ToString();
        Assert.True(value.IndexOf("first", StringComparison.Ordinal) < value.IndexOf("tied", StringComparison.Ordinal));
        Assert.True(value.IndexOf("tied", StringComparison.Ordinal) < value.IndexOf("late", StringComparison.Ordinal));
        if (format == "tsv")
        {
            Assert.Contains("first\\tline", value);
            Assert.Contains("1970-01-01T00:00:01.0000000Z", value);
        }
    }

    [Fact]
    public async Task DeadlineDuringEnrichmentRetainsSessionAndSuppressesLateOutput()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<TdApi.Object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new SyntheticNative(function =>
        {
            Assert.IsType<TdApi.GetMessage>(function);
            requested.SetResult();
            return reply.Task;
        });
        var message = Message(50, 2, "after deadline");
        message.ReplyTo = new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = ChatId, MessageId = 49 };
        using var stdout = new StringWriter();
        var run = CliExecution.RunAsync(async token =>
        {
            try
            {
                await using var session = await OpenAsync(native, token);
                await Output.WriteExportedMessagesAsync(stdout, session, [Output.ToExportedMessage(message)], OutputFormat.Json,
                    cancellationToken: token);
            }
            finally { settled.TrySetResult(); }
        }, TimeSpan.FromSeconds(1), TestToken);
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
            await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.True(SessionLock.Inspect(_root).locked);
            Assert.Empty(stdout.ToString());
        }
        finally { reply.TrySetResult(Message(49, 1, "parent")); }
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        Assert.False(SessionLock.Inspect(_root).locked);
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task TransitionalAuthorizationDoesNotClaimLoginIsRequired()
    {
        await using var session = await OpenAsync(new SyntheticNative(function => throw new InvalidOperationException(),
            new TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters()), TestToken);
        var result = await SessionDiagnostic.InspectAsync(session, TestToken);
        Assert.False(result.Healthy);
        Assert.Equal("authorization_unknown", result.Data.Value<string>("error_code"));
        Assert.Equal("AuthorizationStateWaitTdlibParameters", result.Data.Value<string>("authorization_state"));
        Assert.Equal(JTokenType.Null, result.Data["authorized"]!.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlySupergroupZeroMembersMeansUnknown(bool supergroup)
    {
        await using var session = await OpenAsync(new SyntheticNative(function => Task.FromResult<TdApi.Object>(function switch
        {
            TdApi.GetChat => new TdApi.Chat { Id = ChatId, Type = supergroup
                ? new TdApi.ChatType.ChatTypeSupergroup { SupergroupId = 7 }
                : new TdApi.ChatType.ChatTypeBasicGroup { BasicGroupId = 7 } },
            TdApi.GetSupergroupFullInfo => new TdApi.SupergroupFullInfo { MemberCount = 0 },
            TdApi.GetBasicGroup => new TdApi.BasicGroup { MemberCount = 0 },
            _ => throw new InvalidOperationException(function.GetType().Name)
        })), TestToken);
        var count = await ChatCommands.TryGetParticipantCountAsync(session, ChatId, TestToken);
        Assert.Equal(supergroup ? JTokenType.Null : JTokenType.Integer, count.Type);
    }

    private Task<TelegramSession> OpenAsync(SyntheticNative native, CancellationToken token) =>
        TelegramSession.CreateCoreAsync(_root, 123, "synthetic", saveConfig: false, lockTimeout: 0, noWait: true,
            () => native, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), token);

    private static TdApi.Message Message(long id, int date, string text, long chatId = ChatId) => new()
    {
        Id = id,
        ChatId = chatId,
        Date = date,
        Content = new TdApi.MessageContent.MessageText { Text = new TdApi.FormattedText { Text = text, Entities = [] } }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SyntheticNative(Func<TdApi.Object, Task<TdApi.Object>> resolve,
        TdApi.AuthorizationState? initialState = null) : ITelegramSessionNative, TdApi.IClient
    {
        public TdApi.IClient Client => this;
        public event EventHandler<TdApi.Update>? UpdateReceived { add { } remove { } }
        public Task SetLogVerbosityAsync() => Task.CompletedTask;
        public Task SetParametersAsync(TdApi.SetTdlibParameters parameters) => Task.CompletedTask;
        public Task<TdApi.AuthorizationState> GetAuthorizationStateAsync() => Task.FromResult(initialState ??
            new TdApi.AuthorizationState.AuthorizationStateReady());
        public void Send<TResult>(TdApi.Function<TResult> function) => throw new NotSupportedException();
        public TResult Execute<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object =>
            throw new NotSupportedException();
        public async Task<TResult> ExecuteAsync<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object =>
            (TResult)await resolve(function);
        public void Dispose() { }
    }
}

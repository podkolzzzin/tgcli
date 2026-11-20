#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class McpBackendParityTests : IDisposable
{
    private const long ChatId = -1001234567890;
    private const long MessageId = 9007199255789568;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tgcli-mcp-backend-");

    [Theory]
    [InlineData(50, -10001)]
    [InlineData(100, -10002)]
    [InlineData(50, -10003)]
    [InlineData(100, -10004)]
    [InlineData(50, -10005)]
    [InlineData(100, -10006)]
    [InlineData(50, -10007)]
    public async Task ResumedHistoryUsesRealNativeIdsAndReturnsChronologicalRows(int limit, long chatId)
    {
        var messages = Enumerable.Range(1, 2 * limit + 4).Reverse()
            .Select(index => Message((long)index << 20, index, chatId)).ToArray();
        var starts = new List<long>();
        var client = new Native(request => request switch
        {
            TdApi.GetChat get when get.ChatId == chatId => new TdApi.Chat { Id = chatId, LastMessage = messages[0] },
            TdApi.GetChatHistory history when history.ChatId == chatId => Fetch(history),
            TdApi.GetMessageLink link when link.ChatId == chatId => Link(),
            _ => throw Unexpected(request)
        });
        TdApi.Messages Fetch(TdApi.GetChatHistory request)
        {
            starts.Add(request.FromMessageId);
            var at = request.FromMessageId == 0 ? 0 : Array.FindIndex(messages, m => m.Id == request.FromMessageId);
            if (at < 0) throw NativeError(400, "Invalid native message ID.");
            return Page(messages.Skip(at).Take(request.Limit).ToArray());
        }

        await using var backend = Backend(client);
        var tools = new McpTools(backend);
        var arguments = new JsonObject { ["chat_id"] = Decimal(chatId), ["limit"] = limit };
        var first = Result(await Call(tools, "chat_messages", arguments));
        arguments["cursor"] = first.GetProperty("next_cursor").GetString();
        var second = Result(await Call(tools, "chat_messages", arguments));

        Assert.Equal(messages.Take(limit).Reverse().Select(m => Decimal(m.Id)), MessageIds(first));
        Assert.Equal(messages.Skip(limit).Take(limit).Reverse().Select(m => Decimal(m.Id)), MessageIds(second));
        Assert.Equal(messages[limit - 1].Id, starts[1]);
        Assert.False(second.GetProperty("complete").GetBoolean());
        Assert.True(second.GetProperty("limit_satisfied").GetBoolean());
        Assert.Equal("tdlib_update_state", first.GetProperty("freshness").GetString());
    }

    [Fact]
    public async Task HistoryRetriesStaleHeadAndFillsMatchingRowsAcrossShortPages()
    {
        var head = Message(10485760, 10);
        var photo = Message(9437184, 12);
        photo.Content = new TdApi.MessageContent.MessagePhoto();
        var old = Message(8388608, 12);
        var pages = new Queue<TdApi.Messages>([Page(), Page(head), Page(head, photo), Page(photo, old)]);
        var starts = new List<long>();
        var native = new Native(request => request switch
        {
            TdApi.GetChat => new TdApi.Chat { Id = ChatId, LastMessage = head },
            TdApi.GetChatHistory history => Next(history.FromMessageId),
            TdApi.GetMessageLink => Link(),
            _ => throw Unexpected(request)
        });
        TdApi.Messages Next(long from)
        {
            starts.Add(from);
            return pages.Dequeue();
        }

        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "chat_messages", new()
        {
            ["chat_id"] = Decimal(ChatId), ["limit"] = 2, ["kind"] = "text"
        }));
        Assert.Equal(["10485760", "8388608"], MessageIds(result));
        Assert.Equal([0L, 0L, 10485760L, 9437184L], starts);
        Assert.Equal(4, result.GetProperty("pages_fetched").GetInt32());
        Assert.True(result.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task TopicHistoryRetainsTheTopicScopeAcrossItsNativeAnchor()
    {
        var starts = new List<(int, long)>();
        var native = new Native(request => request switch
        {
            TdApi.GetForumTopicHistory history when history.ChatId == ChatId => Fetch(history),
            TdApi.GetMessageLink => Link(),
            _ => throw Unexpected(request)
        });
        TdApi.Messages Fetch(TdApi.GetForumTopicHistory request)
        {
            starts.Add((request.ForumTopicId, request.FromMessageId));
            return request.FromMessageId switch
            {
                0 => Page(Message(4194304, 4)),
                4194304 => Page(Message(4194304, 4), Message(3145728, 3)),
                _ => throw Unexpected(request)
            };
        }
        await using var backend = Backend(native);
        var tools = new McpTools(backend);
        var arguments = new JsonObject { ["chat_id"] = Decimal(ChatId), ["topic_id"] = "17", ["limit"] = 1 };
        var first = Result(await Call(tools, "chat_messages", arguments));
        Assert.Equal("17", first.GetProperty("requested_scope").GetProperty("topic_id").GetString());
        arguments["cursor"] = first.GetProperty("next_cursor").GetString();
        arguments["topic_id"] = "18";
        AssertError(await Call(tools, "chat_messages", arguments), "invalid_arguments");
        arguments["topic_id"] = "017";
        var second = Result(await Call(tools, "chat_messages", arguments));
        Assert.Equal(["3145728"], MessageIds(second));
        Assert.Equal([(17, 0L), (17, 4194304L)], starts);
    }

    [Fact]
    public async Task SearchUsesExactNativeOffsetsThroughEmptyPagesAndBindsSemanticFilters()
    {
        var starts = new List<long>();
        var native = new Native(request => request switch
        {
            TdApi.SearchChatMessages search => Search(search),
            TdApi.GetMessageLink => Link(),
            _ => throw Unexpected(request)
        });
        TdApi.FoundChatMessages Search(TdApi.SearchChatMessages request)
        {
            starts.Add(request.FromMessageId);
            Assert.IsType<TdApi.SearchMessagesFilter.SearchMessagesFilterDocument>(request.Filter);
            return request.FromMessageId switch
            {
                0 => SearchPage(7654321),
                7654321 => SearchPage(6543210, Message(7340032, 7)),
                6543210 => SearchPage(0, Message(6291456, 6)),
                _ => throw Unexpected(request)
            };
        }

        await using var backend = Backend(native);
        var tools = new McpTools(backend);
        var args = new JsonObject { ["chat_id"] = Decimal(ChatId), ["type"] = "doc", ["limit"] = 1 };
        var first = Result(await Call(tools, "message_search", args));
        Assert.Equal(["7340032"], MessageIds(first));
        args["cursor"] = first.GetProperty("next_cursor").GetString();
        args["type"] = "video";
        AssertError(await Call(tools, "message_search", args), "invalid_arguments");
        args["type"] = " DOCUMENT ";
        args["query"] = "";
        args["chat_id"] = "-01001234567890";
        args["topic_id"] = "0";
        var second = Result(await Call(tools, "message_search", args));
        Assert.Equal([0L, 7654321L, 6543210L], starts);
        Assert.Equal(["6291456"], MessageIds(second));
        Assert.True(second.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task LocalHistoryUsesOnlyLocalEnrichmentAndCannotClaimRemoteExhaustion()
    {
        var target = Message(MessageId, 1);
        target.SenderId = new TdApi.MessageSender.MessageSenderUser { UserId = 9007199254740993 };
        target.ReplyTo = new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = ChatId, MessageId = 1048576 };
        var requests = new List<string>();
        var native = new Native(request =>
        {
            requests.Add(request.GetType().Name);
            return request switch
            {
                TdApi.GetChat => new TdApi.Chat { Id = ChatId, LastMessage = target },
                TdApi.GetChatHistory { OnlyLocal: true, FromMessageId: 0 } => Page(target),
                TdApi.GetChatHistory { OnlyLocal: true, FromMessageId: MessageId } => Page(),
                TdApi.GetMessageLocally { ChatId: ChatId, MessageId: 1048576 } => Message(1048576, 0),
                _ => throw Unexpected(request)
            };
        }) { Connected = false };

        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "chat_messages", new()
        {
            ["chat_id"] = Decimal(ChatId), ["local"] = true, ["limit"] = 2
        }));
        var row = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal("8.0", row.GetProperty("schema_version").GetString());
        Assert.Equal("9007199254740993", row.GetProperty("sender_user_id").GetString());
        Assert.Equal("1048576", row.GetProperty("reply_target").GetProperty("message_id").GetString());
        Assert.Equal(["GetChat", "GetChatHistory", "GetChatHistory", "GetMessageLocally"], requests);
        Assert.False(result.GetProperty("complete").GetBoolean());
        Assert.Equal("local_cache", result.GetProperty("source").GetString());
        Assert.Equal("local_cache_exhausted", result.GetProperty("termination_reason").GetString());
    }

    [Fact]
    public async Task ForumTopicsCarryAnEmptyPageContinuationAndResumeAllNativeOffsets()
    {
        var starts = new List<(int, long, int)>();
        var pages = new Queue<TdApi.ForumTopics>(
        [
            new() { Topics = [], NextOffsetDate = 11, NextOffsetMessageId = 22, NextOffsetForumTopicId = 33 },
            new() { Topics = [Topic(5)], NextOffsetDate = 44, NextOffsetMessageId = 55, NextOffsetForumTopicId = 66 },
            new() { Topics = [Topic(7)] }
        ]);
        var native = new Native(request =>
        {
            var get = Assert.IsType<TdApi.GetForumTopics>(request);
            starts.Add((get.OffsetDate, get.OffsetMessageId, get.OffsetForumTopicId));
            return pages.Dequeue();
        });

        await using var backend = Backend(native);
        var tools = new McpTools(backend);
        var args = new JsonObject { ["chat_id"] = Decimal(ChatId), ["limit"] = 1 };
        var first = Result(await Call(tools, "forum_topics", args));
        Assert.Equal("5", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("topic_id").GetString());
        args["cursor"] = first.GetProperty("next_cursor").GetString();
        var last = Result(await Call(tools, "forum_topics", args));
        Assert.Equal("7", Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("topic_id").GetString());
        Assert.Equal([(0, 0L, 0), (11, 22L, 33), (44, 55L, 66)], starts);
        Assert.True(last.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task ForumTopicLimitFillsThroughShortAndEmptyPages()
    {
        var starts = new List<(int, long, int, int)>();
        var pages = new Queue<TdApi.ForumTopics>(
        [
            new() { Topics = [Topic(3)], NextOffsetDate = 11, NextOffsetMessageId = 22, NextOffsetForumTopicId = 33 },
            new() { Topics = [], NextOffsetDate = 44, NextOffsetMessageId = 55, NextOffsetForumTopicId = 66 },
            new() { Topics = [Topic(4)] }
        ]);
        var native = new Native(request =>
        {
            var get = Assert.IsType<TdApi.GetForumTopics>(request);
            starts.Add((get.OffsetDate, get.OffsetMessageId, get.OffsetForumTopicId, get.Limit));
            return pages.Dequeue();
        });
        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "forum_topics", new()
        {
            ["chat_id"] = Decimal(ChatId), ["query"] = "fixture", ["limit"] = 2
        }));
        Assert.Equal([(0, 0L, 0, 2), (11, 22L, 33, 1), (44, 55L, 66, 1)], starts);
        Assert.Equal(["3", "4"], result.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("topic_id").GetString()));
        Assert.True(result.GetProperty("limit_satisfied").GetBoolean());
        Assert.True(result.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task FilteredEmptyBudgetReturnsAnExplicitPartialResultWithContinuation()
    {
        var native = new Native(request => request switch
        {
            TdApi.SearchChatMessages => SearchPage(1234567),
            _ => throw Unexpected(request)
        });
        await using var backend = Backend(native);
        var call = await Call(new McpTools(backend), "message_search", new()
        {
            ["chat_id"] = Decimal(ChatId), ["limit"] = 2, ["max_pages"] = 1
        });
        Assert.True(call.IsError);
        var result = Assert.IsType<JsonElement>(call.StructuredContent);
        Assert.Equal("partial_result", result.GetProperty("error_code").GetString());
        Assert.Equal("max_pages_reached", result.GetProperty("termination_reason").GetString());
        Assert.Equal("1234567", result.GetProperty("next_message_id").GetString());
        Assert.NotEmpty(result.GetProperty("next_cursor").GetString()!);
        Assert.Empty(MessageIds(result));
    }

    [Fact]
    public async Task MainListReportsObservedExhaustion()
    {
        var native = new Native(request => request switch
        {
            TdApi.LoadChats => throw NativeError(404, "No further chats."),
            TdApi.GetChats => new TdApi.Chats { ChatIds = [ChatId] },
            TdApi.GetChat => new TdApi.Chat { Id = ChatId, Title = "Fixture", Type = new TdApi.ChatType.ChatTypeBasicGroup() },
            _ => throw Unexpected(request)
        });
        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "chat_list"));
        Assert.True(result.GetProperty("complete").GetBoolean());
        Assert.Equal("chat_list_exhausted", result.GetProperty("termination_reason").GetString());
        Assert.False(result.GetProperty("has_more").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatDiscoveryDefaultsToServerAndHonorsTheExplicitLocalPath(bool server)
    {
        var native = new Native(request => request switch
        {
            TdApi.SearchChatsOnServer { Query: "Fixture" } when server => new TdApi.Chats { ChatIds = [ChatId] },
            TdApi.SearchChats { Query: "Fixture" } when !server => new TdApi.Chats { ChatIds = [ChatId] },
            TdApi.GetChat => new TdApi.Chat { Id = ChatId, Title = "Fixture", Type = new TdApi.ChatType.ChatTypeBasicGroup() },
            _ => throw Unexpected(request)
        }) { Connected = server };
        await using var backend = Backend(native);
        var args = new JsonObject { ["query"] = "Fixture" };
        if (!server) args["server"] = false;
        var result = Result(await Call(new McpTools(backend), "chat_search", args));
        Assert.Equal(Decimal(ChatId), Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("chat_id").GetString());
        Assert.Equal(server ? "telegram" : "local_cache", result.GetProperty("source").GetString());
        Assert.False(result.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task LocalStatsDoesNotFetchRemoteParticipantCounts()
    {
        var native = new Native(request => request switch
        {
            TdApi.GetChatHistory { FromMessageId: 0, OnlyLocal: true } => Page(Message(MessageId, 1)),
            TdApi.GetChatHistory { FromMessageId: MessageId, OnlyLocal: true } => Page(),
            TdApi.GetChat => new TdApi.Chat { Id = ChatId },
            _ => throw Unexpected(request)
        }) { Connected = false };
        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "chat_stats", new()
        {
            ["chat_id"] = Decimal(ChatId), ["local"] = true
        }));
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        Assert.False(result.GetProperty("complete").GetBoolean());
        Assert.Equal("local_cache", result.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("participant_count").ValueKind);
        Assert.Equal(Decimal(ChatId), Assert.Single(result.GetProperty("migrations").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("chat_context", """{"chat_id":"1","message_id":"1","before":60,"after":40}""")]
    [InlineData("chat_messages", """{"chat_id":"1","topic_id":"2147483648"}""")]
    [InlineData("chat_messages", """{"chat_id":"1","topic_id":42}""")]
    [InlineData("attachment_download", """{"chat_id":"1","message_id":"1","file_id":"2147483648","output":"/unused.out"}""")]
    public async Task InvalidContextAndIdentifierBoundsNeverOpenASession(string name, string json)
    {
        var starts = 0;
        await using var backend = new McpTelegramBackend(_ =>
        {
            starts++;
            throw new InvalidOperationException("Factory must not be called.");
        });
        AssertError(await Call(new McpTools(backend), name, JsonNode.Parse(json)!.AsObject()), "invalid_arguments");
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task PreflightAcceptsTheExactNativeContextWindow()
    {
        var target = Message(MessageId, 1);
        var native = new Native(request => request switch
        {
            TdApi.GetMessage { ChatId: ChatId, MessageId: MessageId } => target,
            TdApi.GetChatHistory { FromMessageId: MessageId, Offset: 0, Limit: 100 } => Page(),
            TdApi.GetMessageLink => Link(),
            _ => throw Unexpected(request)
        });
        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "chat_context", new()
        {
            ["chat_id"] = Decimal(ChatId), ["message_id"] = Decimal(MessageId), ["before"] = 99, ["after"] = 0
        }));
        Assert.Equal([Decimal(MessageId)], MessageIds(result));
        Assert.True(result.GetProperty("context").GetProperty("window_complete").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsExposeOfflineOrUnauthorizedStateWithoutWaitingForNetwork(bool authorized)
    {
        var native = new Native(request => request switch
        {
            TdApi.GetMe when authorized => new TdApi.User { Id = 9007199254740993, FirstName = "Fixture" },
            TdApi.GetOption { Name: "version" } when authorized => new TdApi.OptionValue.OptionValueString { Value = "synthetic" },
            _ => throw Unexpected(request)
        }) { Connected = false };
        if (!authorized) native.Authorization = new TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber();
        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "diagnostics", new() { ["request_timeout"] = 1 }));
        Assert.False(result.GetProperty("healthy").GetBoolean());
        Assert.False(result.GetProperty("network_available").GetBoolean());
        Assert.Equal(authorized, result.GetProperty("authorized").GetBoolean());
        var account = result.GetProperty("authenticated_account");
        if (authorized) Assert.Equal("9007199254740993", account.GetProperty("user_id").GetString());
        else Assert.Equal(JsonValueKind.Null, account.ValueKind);
    }

    [Fact]
    public async Task UnauthorizedDiagnosticRetiresTheSessionSoTheNextReadCanReopen()
    {
        var opened = 0;
        var first = new Native(request => throw Unexpected(request))
        {
            Authorization = new TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber(),
            Connected = false
        };
        var second = new Native(request => request switch
        {
            TdApi.GetChatHistory { OnlyLocal: true } => Page(),
            TdApi.GetChat => new TdApi.Chat { Id = ChatId },
            _ => throw Unexpected(request)
        });
        await using var backend = new McpTelegramBackend(ct => OpenAsync(++opened == 1 ? first : second, ct));
        var tools = new McpTools(backend);
        var diagnostic = Result(await Call(tools, "diagnostics"));
        Assert.Equal("AuthorizationStateWaitPhoneNumber", diagnostic.GetProperty("authorization_state").GetString());
        Assert.Equal("login_required", diagnostic.GetProperty("error_code").GetString());

        Result(await Call(tools, "chat_messages", new() { ["chat_id"] = Decimal(ChatId), ["local"] = true }));
        Assert.Equal(2, opened);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task MissingSessionConfigIsStructuredDiagnosticsAndLoginRequiredForReads()
    {
        await using var backend = new McpTelegramBackend(_ => throw new TelegramAuthorizationException(
            TelegramAuthorizationFailure.LoginRequired, "Synthetic configuration has no credentials."), _root.FullName);
        var tools = new McpTools(backend);
        var health = Result(await Call(tools, "diagnostics"));
        Assert.False(health.GetProperty("healthy").GetBoolean());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("authenticated_account").ValueKind);
        AssertError(await Call(tools, "chat_list"), "login_required");
    }

    [Fact]
    public async Task LocalRequestRevalidatesAuthorizationAndRetiresTerminalSession()
    {
        var opened = 0;
        var first = new Native(request => request switch
        {
            TdApi.GetMe => new TdApi.User { Id = 1, FirstName = "Fixture" },
            TdApi.GetOption { Name: "version" } => new TdApi.OptionValue.OptionValueString { Value = "synthetic" },
            _ => throw Unexpected(request)
        });
        var second = new Native(request => request switch
        {
            TdApi.GetChatHistory { OnlyLocal: true } => Page(),
            TdApi.GetChat => new TdApi.Chat { Id = ChatId },
            _ => throw Unexpected(request)
        });
        await using var backend = new McpTelegramBackend(ct => OpenAsync(++opened == 1 ? first : second, ct));
        var tools = new McpTools(backend);
        Result(await Call(tools, "diagnostics"));
        first.Authorization = new TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber();
        var args = new JsonObject { ["chat_id"] = Decimal(ChatId), ["local"] = true };
        AssertError(await Call(tools, "chat_messages", args), "login_required");
        Assert.Equal(1, opened);
        Result(await Call(tools, "chat_messages", args));
        Assert.Equal(2, opened);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task CancelledNativeReadHoldsTheLeaseAndCancelledQueuedReadNeverReachesTdlib()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<TdApi.Object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new Native(request => request switch
        {
            TdApi.GetMessageLink => Link(),
            _ => throw Unexpected(request)
        })
        {
            AsyncResponse = request =>
            {
                if (request is TdApi.GetMessage { MessageId: MessageId })
                {
                    entered.TrySetResult();
                    return complete.Task;
                }
                if (request is TdApi.GetMessage { MessageId: 2097152 })
                {
                    thirdEntered.TrySetResult();
                    return Task.FromResult<TdApi.Object>(Message(2097152, 2));
                }
                if (request is TdApi.GetMessage) throw Unexpected(request);
                return null;
            }
        };
        await using var backend = Backend(native);
        var tools = new McpTools(backend);
        var timeLimit = TimeSpan.FromSeconds(5);
        var firstArgs = new JsonObject { ["chat_id"] = Decimal(ChatId), ["message_id"] = Decimal(MessageId) };
        var queuedArgs = new JsonObject { ["chat_id"] = Decimal(ChatId), ["message_id"] = "1048576" };
        var thirdArgs = new JsonObject { ["chat_id"] = Decimal(ChatId), ["message_id"] = "2097152" };
        using var runningCancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var queuedCancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = Call(tools, "message_get", firstArgs, runningCancel.Token);
        try
        {
            await entered.Task.WaitAsync(timeLimit, Ct);
            var queued = Call(tools, "message_get", queuedArgs, queuedCancel.Token);
            queuedCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(timeLimit, Ct));
            runningCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(timeLimit, Ct));

            var third = Call(tools, "message_get", thirdArgs);
            Assert.True(SessionLock.Inspect(_root.FullName).locked);
            Assert.False(thirdEntered.Task.IsCompleted);
            complete.SetResult(Message(MessageId, 1));
            var next = Result(await third.WaitAsync(timeLimit, Ct));
            Assert.Equal("2097152", next.GetProperty("message_id").GetString());
        }
        finally { complete.TrySetResult(Message(MessageId, 1)); }
    }

    [Fact]
    public async Task AttachmentDownloadRejectsPartialNativeFileAndPreservesDestination()
    {
        var source = Path.Combine(_root.FullName, "partial.bin");
        var destination = Path.Combine(_root.FullName, "destination.bin");
        await File.WriteAllTextAsync(source, "part", Ct);
        await File.WriteAllTextAsync(destination, "existing", Ct);
        var selected = new TdApi.File { Id = 15, Size = 8 };
        var partial = new TdApi.File { Id = 15, Size = 8, Local = new TdApi.LocalFile { Path = source, IsDownloadingCompleted = false } };
        var native = new Native(request => request switch
        {
            TdApi.GetMessage => Document(selected),
            TdApi.DownloadFile { FileId: 15 } => partial,
            _ => throw Unexpected(request)
        });
        await using var backend = Backend(native);
        AssertError(await Call(new McpTools(backend), "attachment_download", new()
        {
            ["chat_id"] = Decimal(ChatId), ["message_id"] = Decimal(MessageId), ["output"] = destination
        }), "io_error");
        Assert.Equal("existing", await File.ReadAllTextAsync(destination, Ct));
    }

    [Fact]
    public async Task DownloadSelectsExactFileIdAndReturnsADecimalString()
    {
        var source = Path.Combine(_root.FullName, "downloaded.bin");
        var destination = Path.Combine(_root.FullName, "destination.bin");
        await File.WriteAllTextAsync(source, "complete", Ct);
        var file = new TdApi.File { Id = 15, Size = 8, Local = new TdApi.LocalFile { Path = source, IsDownloadingCompleted = true } };
        var message = Message(MessageId, 1);
        message.Content = new TdApi.MessageContent.MessageVideo
        {
            Caption = new TdApi.FormattedText { Text = "fixture", Entities = [] },
            Video = new TdApi.Video
            {
                Video_ = new TdApi.File { Id = 20, Size = 99 },
                FileName = "primary.mp4",
                Thumbnail = new TdApi.Thumbnail
                {
                    File = file,
                    Format = new TdApi.ThumbnailFormat.ThumbnailFormatJpeg()
                }
            }
        };
        var native = new Native(request => request switch
        {
            TdApi.GetMessage { ChatId: ChatId, MessageId: MessageId } => message,
            _ => throw Unexpected(request)
        });
        await using var backend = Backend(native);
        var result = Result(await Call(new McpTools(backend), "attachment_download", new()
        {
            ["chat_id"] = Decimal(ChatId), ["message_id"] = Decimal(MessageId), ["output"] = destination, ["file_id"] = "15"
        }));
        Assert.Equal("15", result.GetProperty("file_id").GetString());
        Assert.Equal(8, result.GetProperty("size_bytes").GetInt64());
        Assert.Equal("complete", await File.ReadAllTextAsync(destination, Ct));
    }

    private static Task<CallToolResult> Call(McpTools tools, string name, JsonObject? arguments = null,
        CancellationToken? cancellationToken = null) =>
        tools.CallAsync(name, arguments?.Deserialize<Dictionary<string, JsonElement>>(), cancellationToken ?? Ct);

    private static JsonElement Result(CallToolResult result)
    {
        Assert.False(result.IsError == true, (Assert.Single(result.Content) as TextContentBlock)?.Text);
        return Assert.IsType<JsonElement>(result.StructuredContent);
    }

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        Assert.StartsWith(code + ":", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static IEnumerable<string?> MessageIds(JsonElement result) =>
        result.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("message_id").GetString());

    private McpTelegramBackend Backend(Native client) => new(ct => OpenAsync(client, ct));

    private Task<TelegramSession> OpenAsync(Native client, CancellationToken ct) =>
        TelegramSession.CreateCoreAsync(_root.FullName, 1, "synthetic", false, 0, true,
            () => client, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), ct);

    private static string Decimal(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static TdApi.Messages Page(params TdApi.Message[] messages) => new() { Messages_ = messages };
    private static TdApi.FoundChatMessages SearchPage(long next, params TdApi.Message[] messages) =>
        new() { Messages = messages, NextFromMessageId = next };
    private static TdApi.ForumTopic Topic(int id) => new()
    {
        Info = new TdApi.ForumTopicInfo { ChatId = ChatId, ForumTopicId = id, Name = "fixture" }
    };
    private static TdApi.MessageLink Link() => new() { Link = "https://t.me/synthetic/8" };
    private static TdApi.Message Message(long id, int date, long chatId = ChatId) => new()
    {
        Id = id, ChatId = chatId, Date = date,
        Content = new TdApi.MessageContent.MessageText { Text = new TdApi.FormattedText { Text = "fixture", Entities = [] } }
    };
    private static TdApi.Message Document(TdApi.File file) => new()
    {
        Id = MessageId, ChatId = ChatId, Date = 1,
        Content = new TdApi.MessageContent.MessageDocument
        {
            Caption = new TdApi.FormattedText { Text = "fixture", Entities = [] },
            Document = new TdApi.Document { Document_ = file, FileName = "fixture.bin" }
        }
    };
    private static TdException NativeError(int code, string message) => new(new TdApi.Error { Code = code, Message = message });
    private static Exception Unexpected(TdApi.Object request) => new InvalidOperationException($"Unexpected {request.GetType().Name}.");

    public void Dispose() => _root.Delete(recursive: true);

    private sealed class Native(Func<TdApi.Object, TdApi.Object> response) : ITelegramSessionNative, TdApi.IClient
    {
        public TdApi.IClient Client => this;
        public event EventHandler<TdApi.Update>? UpdateReceived;
        public Func<TdApi.Object, Task<TdApi.Object>?>? AsyncResponse { get; init; }
        public bool Connected { get; set; } = true;
        public bool Disposed { get; private set; }
        public TdApi.AuthorizationState Authorization { get; set; } = new TdApi.AuthorizationState.AuthorizationStateReady();
        public Task SetLogVerbosityAsync() => Task.CompletedTask;
        public Task SetParametersAsync(TdApi.SetTdlibParameters parameters) => Task.CompletedTask;
        public Task<TdApi.AuthorizationState> GetAuthorizationStateAsync()
        {
            UpdateReceived?.Invoke(this, new TdApi.Update.UpdateConnectionState
            {
                State = Connected
                    ? new TdApi.ConnectionState.ConnectionStateReady()
                    : new TdApi.ConnectionState.ConnectionStateWaitingForNetwork()
            });
            return Task.FromResult(Authorization);
        }
        public void Send<TResult>(TdApi.Function<TResult> function) => throw Unexpected(function);
        public TResult Execute<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object => (TResult)response(function);
        public async Task<TResult> ExecuteAsync<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object
        {
            var pending = AsyncResponse?.Invoke(function);
            return pending is null ? Execute(function) : (TResult)await pending;
        }
        public void Dispose() => Disposed = true;
    }
}

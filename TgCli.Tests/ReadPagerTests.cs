#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class ReadPagerTests
{
    private static TdApi.Message Message(long id, int date = 0) => new() { ChatId = 1, Id = id, Date = date };
    private static TdApi.Messages Page(params long[] ids) => new() { Messages_ = ids.Select(id => Message(id)).ToArray() };

    [Fact]
    public async Task BoundedHistoryFillsMatchingLimitUsingTheOriginalCursorAndOffset()
    {
        var pages = new Queue<TdApi.Messages>([Page(110, 100), Page(100, 80), Page(80, 70)]);
        var requests = new List<TdApi.GetChatHistory>();
        var result = await ChatHistory.FetchPagesAsync([1], false, false, 10,
            request =>
            {
                requests.Add(request);
                return Task.FromResult(pages.Dequeue());
            },
            id => Task.FromResult(new TdApi.Chat { Id = id }),
            TestContext.Current.CancellationToken, fromMessageId: 100, offset: -1, limit: 2,
            match: message => message.Id < 100);

        Assert.Equal([100L, 100, 80], requests.Select(x => x.FromMessageId));
        Assert.Equal([-1, 0, 0], requests.Select(x => x.Offset));
        Assert.Equal([70L, 80], result.Messages.Select(x => x.Message.Id));
        Assert.True(result.LimitSatisfied);
        Assert.False(result.Complete);
        Assert.Equal(70, result.NextMessageId);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public async Task ResumedHistoryFillsItsLimitUsingUnmodifiedNativeAnchorIds(int limit)
    {
        var ids = Enumerable.Range(1, 2 * limit + 4).Reverse().Select(id => (long)id << 20).ToArray();
        var cursors = new List<long>();
        Task<TdApi.Messages> Fetch(TdApi.GetChatHistory request)
        {
            cursors.Add(request.FromMessageId);
            var start = request.FromMessageId == 0 ? 0 : Array.IndexOf(ids, request.FromMessageId);
            Assert.True(start >= 0, "TDLib only accepts the exact native anchor in this fixture.");
            return Task.FromResult(Page(ids.Skip(start).Take(request.Limit).ToArray()));
        }
        Task<TdApi.Chat> Chat(long id) => Task.FromResult(new TdApi.Chat { Id = id });
        var first = await ChatHistory.FetchPagesAsync([1], false, false, 5, Fetch, Chat,
            TestContext.Current.CancellationToken, limit: limit);
        var second = await ChatHistory.FetchPagesAsync([1], false, false, 5, Fetch, Chat,
            TestContext.Current.CancellationToken, fromMessageId: first.NextMessageId!.Value, limit: limit);

        Assert.Equal(ids.Take(limit).Reverse(), first.Messages.Select(x => x.Message.Id));
        Assert.Equal(ids.Skip(limit).Take(limit).Reverse(), second.Messages.Select(x => x.Message.Id));
        Assert.Equal(ids[limit - 1], cursors[1]);
        Assert.True(first.LimitSatisfied);
        Assert.True(second.LimitSatisfied);
        Assert.False(second.Complete);
    }

    [Fact]
    public async Task BoundedMigratedHistoryKeepsTheBudgetLocalToEachSource()
    {
        Task<TdApi.Messages> Fetch(TdApi.GetChatHistory request)
        {
            Assert.Equal(1, request.Limit);
            return Task.FromResult(new TdApi.Messages
            {
                Messages_ = [new TdApi.Message { ChatId = request.ChatId, Id = 100 }]
            });
        }

        var result = await ChatHistory.FetchPagesAsync([1, 2], false, false, 10, Fetch,
            id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken, limit: 1);
        Assert.Equal([1L, 2L], result.Messages.Select(x => x.SourceChatId));
        Assert.True(result.LimitSatisfied);
        Assert.Null(result.NextMessageId);
    }

    [Fact]
    public async Task StaleHistoryPrefixCannotSatisfyTheLatestRowLimit()
    {
        var chat = new TdApi.Chat { Id = 1, LastMessage = Message(120), Type = new TdApi.ChatType.ChatTypePrivate() };
        var result = await ChatHistory.FetchPagesAsync([1], false, false, 5, _ => Task.FromResult(Page(100)),
            _ => Task.FromResult(chat), TestContext.Current.CancellationToken, limit: 1);
        Assert.Empty(result.Messages);
        Assert.False(result.LimitSatisfied);
        Assert.Equal("newest_message_unavailable", result.TerminationReason);
        Assert.Equal(2, result.PagesFetched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialEmptyPageWithAKnownRecentMessageRequiresAnotherRead(bool channel)
    {
        var pages = new Queue<TdApi.Messages>([Page(), Page(100), Page()]);
        var chat = new TdApi.Chat
        {
            Id = 1,
            LastMessage = Message(100),
            Type = new TdApi.ChatType.ChatTypeSupergroup { IsChannel = channel }
        };
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 5,
            _ => Task.FromResult(pages.Dequeue()), _ => Task.FromResult(chat), TestContext.Current.CancellationToken);
        Assert.Equal(100, Assert.Single(result.Messages).Message.Id);
        Assert.True(result.Complete);
        Assert.Equal(3, result.PagesFetched);
    }

    [Fact]
    public async Task PersistentEmptyPageCannotOverrideKnownNewestMessage()
    {
        var chat = new TdApi.Chat { Id = 1, LastMessage = Message(100), Type = new TdApi.ChatType.ChatTypePrivate() };
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 10,
            _ => Task.FromResult(Page()), _ => Task.FromResult(chat), TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.Equal("newest_message_unavailable", result.TerminationReason);
        Assert.Equal(2, result.PagesFetched);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TypedMigrationLookupFindsTheSameChainFromBothEnds(long start)
    {
        var basic = new TdApi.Chat { Id = 1, Type = new TdApi.ChatType.ChatTypeBasicGroup { BasicGroupId = 11 } };
        var supergroup = new TdApi.Chat { Id = 2, Type = new TdApi.ChatType.ChatTypeSupergroup { SupergroupId = 22 } };
        var client = new FixtureClient(request => request switch
        {
            TdApi.GetChat { ChatId: 1 } => basic,
            TdApi.GetChat { ChatId: 2 } => supergroup,
            TdApi.GetBasicGroup { BasicGroupId: 11 } => new TdApi.BasicGroup { UpgradedToSupergroupId = 22 },
            TdApi.GetSupergroupFullInfo { SupergroupId: 22 } => new TdApi.SupergroupFullInfo { UpgradedFromBasicGroupId = 11 },
            TdApi.CreateSupergroupChat { SupergroupId: 22, Force: false } => supergroup,
            TdApi.CreateBasicGroupChat { BasicGroupId: 11, Force: false } => basic,
            _ => throw new InvalidOperationException($"Unexpected request {request.GetType().Name}.")
        });
        var result = await ChatHistory.GetMigrationChainAsync(client, start, TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L], result);
    }

    [Fact]
    public async Task JoinedDialogTitleSearchUsesTheServerByDefault()
    {
        var client = new FixtureClient(request => request switch
        {
            TdApi.SearchChatsOnServer { Query: "fixture chat", Limit: 20 } => new TdApi.Chats { ChatIds = [4] },
            _ => throw new InvalidOperationException($"Unexpected request {request.GetType().Name}.")
        });
        var result = await TelegramOperations.SearchChatsAsync(client, "fixture chat", 20,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([4L], result.ChatIds);
    }

    [Fact]
    public async Task ColdChatListFillsItsLimitAcrossShortLoads()
    {
        var loaded = 0;
        var client = new FixtureClient(request => request switch
        {
            TdApi.LoadChats { ChatList: TdApi.ChatList.ChatListMain } => Load(),
            TdApi.GetChats { Limit: 3, ChatList: TdApi.ChatList.ChatListMain } =>
                new TdApi.Chats { ChatIds = Enumerable.Range(1, loaded).Select(id => (long)id).ToArray() },
            _ => throw new InvalidOperationException($"Unexpected request {request.GetType().Name}.")
        });

        TdApi.Ok Load()
        {
            loaded++;
            return new TdApi.Ok();
        }

        var chats = await TelegramOperations.ListChatsAsync(client, 3, TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 3L], chats.ChatIds);
    }

    private sealed class FixtureClient(Func<TdApi.Object, TdApi.Object> respond) : TdApi.IClient
    {
        public event EventHandler<TdApi.Update>? UpdateReceived { add { } remove { } }
        public void Send<TResult>(TdApi.Function<TResult> function) => throw new NotSupportedException();
        public TResult Execute<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object =>
            (TResult)respond(function);
        public Task<TResult> ExecuteAsync<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object =>
            Task.FromResult(Execute(function));
    }

    [Fact]
    public async Task SearchUsesNativeContinuationAcrossShortAndEmptyFilteredPages()
    {
        var requests = new List<(long Cursor, int Offset)>();
        var pages = new Queue<TdApi.FoundChatMessages>(
        [
            new() { Messages = [Message(100)], NextFromMessageId = 90 },
            new() { Messages = [], NextFromMessageId = 60 },
            new() { Messages = [Message(59, 30), Message(58, 20), Message(57, 10)], NextFromMessageId = 56 },
            new() { Messages = [], NextFromMessageId = 0 }
        ]);
        var result = await TelegramOperations.SearchMessagePagesAsync(120, -1, 30, true, 10,
            (cursor, offset, _) =>
            {
                requests.Add((cursor, offset));
                return Task.FromResult(pages.Dequeue());
            }, message => message.Date is >= 15 and <= 30, TestContext.Current.CancellationToken);
        Assert.Equal([(120L, -1), (90L, 0), (60L, 0), (56L, 0)], requests);
        Assert.Equal([59L, 58], result.Messages.Select(x => x.Id));
        Assert.True(result.Complete);
        Assert.Null(result.NextMessageId);
    }

    [Fact]
    public async Task SearchCapReturnsTheServerCursorEvenWhenNoRowsMatch()
    {
        var result = await TelegramOperations.SearchMessagePagesAsync(0, 0, 1, false, 1,
            (_, _, _) => Task.FromResult(new TdApi.FoundChatMessages { Messages = [Message(100)], NextFromMessageId = 90 }),
            _ => false, TestContext.Current.CancellationToken);
        Assert.Empty(result.Messages);
        Assert.False(result.Complete);
        Assert.Equal("max_pages_reached", result.TerminationReason);
        Assert.Equal(90, result.NextMessageId);
    }

    [Fact]
    public async Task TopicListingCarriesAllThreeOffsetsAcrossAnEmptyPage()
    {
        var requests = new List<TdApi.GetForumTopics>();
        var pages = new Queue<TdApi.ForumTopics>(
        [
            new() { Topics = [Topic(3)], NextOffsetDate = 11, NextOffsetMessageId = 22, NextOffsetForumTopicId = 33 },
            new() { Topics = [], NextOffsetDate = 44, NextOffsetMessageId = 55, NextOffsetForumTopicId = 66 },
            new() { Topics = [Topic(4)], NextOffsetDate = 77, NextOffsetMessageId = 88, NextOffsetForumTopicId = 99 }
        ]);
        var result = await ForumCommands.FetchTopicsAsync(1, "term", 100, true, 3, request =>
        {
            requests.Add(request);
            return Task.FromResult(pages.Dequeue());
        }, TestContext.Current.CancellationToken);

        Assert.Equal([(0, 0L, 0), (11, 22L, 33), (44, 55L, 66)],
            requests.Select(x => (x.OffsetDate, x.OffsetMessageId, x.OffsetForumTopicId)));
        Assert.All(requests, request => Assert.Equal("term", request.Query));
        Assert.Equal([3, 4], result.Topics.Select(x => x.Info.ForumTopicId));
        Assert.False(result.Complete);
        Assert.Equal("max_pages_reached", result.TerminationReason);
        Assert.Equal((77, 88L, 99), result.Next);
    }

    private static TdApi.ForumTopic Topic(int id) => new() { Info = new TdApi.ForumTopicInfo { ForumTopicId = id } };

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task ContextRejectsInvalidWindowBeforeOpeningTheSession(int before, int after)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            new ChatCommands().Context(1, 100, before, after, session: "/dev/null", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContextKeepsTargetAndBoundsAndReportsAnUnavailableAncestor()
    {
        var target = Message(100);
        target.ReplyTo = new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = 2, MessageId = 55 };
        var requests = new List<TdApi.GetChatHistory>();
        var result = await TelegramOperations.FetchContextAsync(1, 100, 2, 1, true, request =>
        {
            requests.Add(request);
            return Task.FromResult(request.Offset == -1 ? Page(110, 90) : Page(85, 80, 70));
        }, (chat, id) =>
        {
            if (chat == 1) return Task.FromResult<TdApi.Message?>(target);
            Assert.Equal(55, id);
            return Task.FromResult<TdApi.Message?>(null);
        }, TestContext.Current.CancellationToken);

        Assert.Equal([85L, 90, 100, 110], result.Messages.Select(x => x.Id));
        Assert.Equal(90, requests[1].FromMessageId);
        Assert.True(result.WindowComplete);
        Assert.Equal("ancestor_unavailable", result.ReplyChainStatus);
        Assert.Equal((2L, 55L), result.UnavailableAncestor);
    }

    [Fact]
    public async Task CommentPagingStartsAtNewestAndRetainsEveryAlbumRoot()
    {
        var requests = new List<TdApi.GetMessageThreadHistory>();
        var pages = new Queue<TdApi.Messages>([Page(120, 110), Page(110, 91, 90), Page()]);
        var thread = new TdApi.MessageThreadInfo { ChatId = 1, MessageThreadId = 90, Messages = [Message(91), Message(90)] };
        var rows = await ChannelCommands.FetchCommentRowsAsync(8, 42, 10,
            _ => Task.FromResult<TdApi.MessageThreadInfo?>(thread),
            request =>
            {
                requests.Add(request);
                return Task.FromResult(pages.Dequeue());
            }, TestContext.Current.CancellationToken);

        Assert.Equal([0L, 110, 90], requests.Select(x => x.FromMessageId));
        Assert.Equal([90L, 91, 110, 120], rows.Where(x => x["message_id"] is not null).Select(x => x.Value<long>("message_id")));
        Assert.Equal([90L, 91], rows.Where(x => x.Value<bool?>("is_thread_root") == true).Select(x => x.Value<long>("message_id")));
        Assert.All(requests, request => Assert.Equal(90, request.MessageId));
        Assert.True(Assert.Single(rows, x => x.Value<string>("record_type") == "coverage").Value<bool>("complete"));
    }

    [Fact]
    public async Task CommentThreadReadErrorDoesNotBecomeAnInaccessibleResult()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => ChannelCommands.FetchCommentRowsAsync(8, 42, 5,
            _ => Task.FromException<TdApi.MessageThreadInfo?>(new TimeoutException("synthetic timeout")),
            _ => throw new InvalidOperationException("History must not start."), TestContext.Current.CancellationToken));
        var rows = await ChannelCommands.FetchCommentRowsAsync(8, 42, 5,
            _ => Task.FromResult<TdApi.MessageThreadInfo?>(null),
            _ => throw new InvalidOperationException("History must not start."), TestContext.Current.CancellationToken);

        var result = Assert.Single(rows);
        Assert.True(result.Value<bool>("is_inaccessible"));
        Assert.False(result.Value<bool>("complete"));
        Assert.Equal("thread_unavailable", result.Value<string>("termination_reason"));
    }

    [Fact]
    public async Task EmptyDiscussionHistoryCannotOverrideKnownReplies()
    {
        var thread = new TdApi.MessageThreadInfo
        {
            ChatId = 1, MessageThreadId = 90, Messages = [Message(90)],
            ReplyInfo = new TdApi.MessageReplyInfo { ReplyCount = 2, LastMessageId = 120 }
        };
        var rows = await ChannelCommands.FetchCommentRowsAsync(8, 42, 5, _ => Task.FromResult<TdApi.MessageThreadInfo?>(thread),
            _ => Task.FromResult(Page()), TestContext.Current.CancellationToken);
        var coverage = Assert.Single(rows, x => x.Value<string>("record_type") == "coverage");
        Assert.False(coverage.Value<bool>("complete"));
        Assert.Equal("newest_reply_unavailable", coverage.Value<string>("termination_reason"));
    }

    [Fact]
    public async Task ReplyCycleStopsAndShortNewerWindowIsExplicit()
    {
        var target = Message(100);
        target.ReplyTo = new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = 1, MessageId = 90 };
        var parent = Message(90);
        parent.ReplyTo = new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = 1, MessageId = 100 };
        var result = await TelegramOperations.FetchContextAsync(1, 100, 0, 2, true,
            _ => Task.FromResult(Page(110)), (_, id) => Task.FromResult<TdApi.Message?>(id == 100 ? target : parent),
            TestContext.Current.CancellationToken);
        Assert.False(result.WindowComplete);
        Assert.False(result.NewerComplete);
        Assert.Equal(1, result.NewerCount);
        Assert.Equal("cycle", result.ReplyChainStatus);
        Assert.Equal([90L, 100L, 110L], result.Messages.Select(x => x.Id));
    }

    [Fact]
    public void DatesUseUtcForDateOnlyAndZoneLessInputs()
    {
        var (since, until) = ReadBounds.ParseDates("2026-01-01T01:00:00+01:00", "2026-01-01");
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), since);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), until);
        Assert.Equal(TimeSpan.Zero, ReadBounds.ParseDates("2026-01-01T01:00:00", null).Since!.Value.Offset);
        Assert.Throws<ArgumentException>(() => ReadBounds.ParseDates("2026-01-02", "2026-01-01"));
    }

    [Fact]
    public void QueryResultsUseOneDocumentAndRetainEachQueryCoverage()
    {
        var reports = new JArray
        {
            new JObject { ["query"] = "alpha", ["items"] = new JArray(1), ["complete"] = true },
            new JObject { ["query"] = "beta", ["items"] = new JArray(2), ["complete"] = false }
        };
        var document = JObject.Parse(ChatCommands.BuildSearchDocument(reports).ToString());
        Assert.Equal(["alpha", "beta"], document["queries"]!.Select(x => x.Value<string>("query")));
        Assert.Equal([1, 2], document["queries"]!.Select(x => x["items"]!.Single().Value<int>()));
        Assert.False(document.Value<bool>("complete"));
    }

    [Fact]
    public void ChannelMetricsKeepUnknownCountersAndCountFormattedLinksAndPaidTotals()
    {
        var text = new TdApi.MessageContent.MessageText
        {
            Text = new TdApi.FormattedText
            {
                Text = "go https://example.com/now naked.example/path",
                Entities =
                [
                    new TdApi.TextEntity { Offset = 0, Length = 2, Type = new TdApi.TextEntityType.TextEntityTypeTextUrl { Url = "https://alternate.example/page" } },
                    new TdApi.TextEntity { Offset = 27, Length = 18, Type = new TdApi.TextEntityType.TextEntityTypeUrl() }
                ]
            }
        };
        var message = Message(42L << 20);
        message.Content = text;
        message.InteractionInfo = new TdApi.MessageInteractionInfo
        {
            ViewCount = 1_500_000_000,
            ReplyInfo = new TdApi.MessageReplyInfo { ReplyCount = 0 },
            Reactions = new TdApi.MessageReactions
            {
                Reactions = [new TdApi.MessageReaction { TotalCount = 123, Type = new TdApi.ReactionType.ReactionTypePaid() }]
            }
        };
        var row = ChannelCommands.ToMetricRow(message);
        var missing = ChannelCommands.ToMetricRow(Message(43L << 20));
        var aggregates = ChannelCommands.BuildMetricAggregates([row, row, missing]);
        Assert.Equal(["alternate.example", "example.com", "naked.example"], row["link_domains"]!.Values<string>().Order());
        Assert.True(row.Value<bool>("has_comments"));
        Assert.Equal(123, row.Value<long>("paid_reaction_count"));
        Assert.Equal(JTokenType.Null, missing["view_count"]!.Type);
        Assert.Equal(3_000_000_000L, aggregates.Value<long>("view_count"));
        Assert.Equal(1, aggregates.Value<int>("posts_with_unknown_interaction_info"));
    }
}

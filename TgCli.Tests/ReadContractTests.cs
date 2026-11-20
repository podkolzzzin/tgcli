using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class ReadContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TdApi.Message Message(long id, int date = 0) => new() { ChatId = 1, Id = id, Date = date };
    private static TdApi.Messages Page(params long[] ids) => new() { Messages_ = ids.Select(id => Message(id)).ToArray() };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheExhaustionCannotEstablishServerHistory(bool hasMessages)
    {
        var pages = new Queue<TdApi.Messages>(hasMessages ? [Page(100), Page()] : [Page()]);
        var result = await ChatHistory.FetchPagesAsync([1], true, true, 10,
            request => Task.FromResult(pages.Dequeue()), id => Task.FromResult(new TdApi.Chat { Id = id }), Token);

        Assert.False(result.Complete);
        Assert.Equal("local_cache_exhausted", result.TerminationReason);
        Assert.Equal(hasMessages ? 1 : 0, result.Messages.Count);
    }

    [Fact]
    public async Task CancellationWhilePageIsPendingDoesNotBecomeEmptySuccess()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = new TaskCompletionSource<TdApi.Messages>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = ChatHistory.FetchPagesAsync([1], true, false, 10,
            request => pending.Task, id => Task.FromResult(new TdApi.Chat { Id = id }), cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2), Token));
        }
        finally
        {
            pending.TrySetResult(Page());
        }
    }

    [Fact]
    public async Task ChannelContinuationKeepsExactNativeIdsAndDoesNotJumpPastEmptyBoundary()
    {
        var newest = 90L << 20;
        var older = 50L << 20;
        var pages = new Queue<TdApi.Messages>([Page(newest), Page(newest, older), Page()]);
        var cursors = new List<long>();
        var result = await ChatHistory.FetchPagesAsync([-1000000000001L], true, false, 10,
            request => { cursors.Add(request.FromMessageId); return Task.FromResult(pages.Dequeue()); },
            id => Task.FromResult(new TdApi.Chat { Id = id, Type = new TdApi.ChatType.ChatTypeSupergroup { IsChannel = true } }), Token);

        Assert.Equal([0L, newest, older], cursors);
        Assert.Equal([older, newest], result.Messages.Select(x => x.Message.Id));
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task ForumHistoryFollowsShortPagesAndDiscardsAnOverlappingAnchor()
    {
        var pages = new Queue<TdApi.Messages>([Page(100), Page(100, 80), Page()]);
        var requests = new List<TdApi.GetForumTopicHistory>();
        var result = await ForumTopicHistory.FetchAsync(1, 2, 110, -1, 100, true, 10,
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(pages.Dequeue());
            }, Token);
        Assert.Equal([110L, 100, 80], requests.Select(x => x.FromMessageId));
        Assert.Equal([-1, 0, 0], requests.Select(x => x.Offset));
        Assert.All(requests, request => Assert.Equal(2, request.ForumTopicId));
        Assert.Equal([100L, 80], result.Messages.Select(x => x.Id));
        Assert.True(result.Complete);
    }

    [Theory]
    [InlineData(false, "page_limit")]
    [InlineData(true, "max_pages_reached")]
    public async Task AForumPageWithRowsDoesNotProveExhaustion(bool all, string reason)
    {
        var result = await ForumTopicHistory.FetchAsync(1, 2, 0, 0, all ? 100 : 1, all, 1,
            (_, _) => Task.FromResult(Page(100)), Token);
        Assert.False(result.Complete);
        Assert.Equal(reason, result.TerminationReason);
    }

    [Fact]
    public void ServiceOnlySelectsServiceEventsRegardlessOfDisplayText()
    {
        TdApi.MessageContent[] contents =
        [
            new TdApi.MessageContent.MessagePhoto(),
            new TdApi.MessageContent.MessagePoll(),
            new TdApi.MessageContent.MessageVideoNote(),
            new TdApi.MessageContent.MessageVoiceNote(),
            new TdApi.MessageContent.MessageLocation(),
            new TdApi.MessageContent.MessageSticker(),
            new TdApi.MessageContent.MessageText(),
            new TdApi.MessageContent.MessageChatAddMembers(),
            new TdApi.MessageContent.MessagePinMessage()
        ];
        var messages = contents.Select((content, index) => new TdApi.Message { Id = index + 1, Content = content });
        Assert.Equal([8L, 9], ChatCommands.FilterMessages(messages, true, null).Select(x => x.Id));
    }

    [Fact]
    public void GroupMessageLinkUsesTheChannelPeerAndPublicPostId()
    {
        Assert.Equal("tg://privatepost?channel=1234567890&post=42",
            MessageLinks.GetTgUrl(-1001234567890L, 42L << 20));
        Assert.Equal("tg://openmessage?chat_id=1234567890&message_id=42",
            MessageLinks.GetTgUrl(-1234567890L, 42L << 20));
        Assert.Equal("tg://openmessage?user_id=1234567890&message_id=42",
            MessageLinks.GetTgUrl(1234567890L, 42L << 20));
        Assert.Null(MessageLinks.GetTgUrl(-2_000_000_000_000L, 42L << 20));
    }
    [Fact]
    public void ExportMetricsRetainMissingInfoAndUseFullNativePaidReactionCount()
    {
        var metrics = ExportSchema.ToMetricsJson(new TdApi.MessageInteractionInfo
        {
            ReplyInfo = new TdApi.MessageReplyInfo(),
            Reactions = new TdApi.MessageReactions
            {
                Reactions = [new TdApi.MessageReaction { Type = new TdApi.ReactionType.ReactionTypePaid(), TotalCount = 7 }],
                PaidReactors = [new TdApi.PaidReactor { StarCount = 2 }]
            }
        });
        Assert.True(metrics.Value<bool>("has_comments"));
        Assert.Equal(7, metrics.Value<long>("paid_reaction_count"));
        var missing = ExportSchema.ToMetricsJson(null);
        Assert.Equal(JTokenType.Null, missing["view_count"]!.Type);
        Assert.Equal(JTokenType.Null, missing["has_comments"]!.Type);
    }
}

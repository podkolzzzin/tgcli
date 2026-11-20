using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class ChatHistoryTests
{
    private static TdApi.Messages Page(params long[] ids) => new()
    {
        Messages_ = ids.Select(id => new TdApi.Message { ChatId = 1, Id = id, Date = (int)id }).ToArray()
    };

    [Fact]
    public async Task OneCachedMessageDoesNotTruncatePrivateChatExport()
    {
        var cursors = new List<long>();
        var pages = new Queue<TdApi.Messages>([Page(100), Page(90, 80), Page(70), Page()]);
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 10,
            request => { cursors.Add(request.FromMessageId); return Task.FromResult(pages.Dequeue()); },
            id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken);
        Assert.Equal([0L, 100, 80, 70], cursors);
        Assert.Equal([70L, 80, 90, 100], result.Messages.Select(x => x.Message.Id));
        Assert.True(result.Complete);
        Assert.Equal(4, result.PagesFetched);
    }

    [Fact]
    public async Task ShortPageAtBudgetLimitIsIncomplete()
    {
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 1,
            request => Task.FromResult(Page(100)), id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.Equal("max_pages_reached", result.TerminationReason);
    }

    [Fact]
    public async Task NonAdvancingPageDoesNotLoopOrClaimCompleteness()
    {
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 10,
            request => Task.FromResult(Page(100)), id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.Equal("cursor_not_advanced", result.TerminationReason);
        Assert.Equal(2, result.PagesFetched);
    }

    [Fact]
    public async Task BoundedReadDoesNotClaimCompleteHistory()
    {
        var result = await ChatHistory.FetchPagesAsync([1], false, false, 10,
            request => Task.FromResult(Page(100)), id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken, limit: 1);
        Assert.False(result.Complete);
        Assert.Equal("page_limit", result.TerminationReason);
    }

    [Fact]
    public async Task EmptyChatCompletesWithoutInventingMessages()
    {
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 10,
            request => Task.FromResult(Page()), id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken);
        Assert.True(result.Complete);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task PrivateMessageIdsAreNotChannelGapEvidence()
    {
        var pages = new Queue<TdApi.Messages>([Page(900000L << 20), Page()]);
        var result = await ChatHistory.FetchPagesAsync([1], true, false, 10,
            request => Task.FromResult(pages.Dequeue()), id => Task.FromResult(new TdApi.Chat { Id = id }), TestContext.Current.CancellationToken);
        var manifest = ChatHistory.BuildManifest(result);
        Assert.Null(manifest["public_id_range"]);
        Assert.Empty(result.Warnings);
    }
}

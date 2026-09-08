using TdLib;

namespace TgCli;

internal sealed record McpMessagePage(TdApi.Message[] Messages, long Next, bool HasMore)
{
    internal static McpMessagePage From(TdApi.Message[] raw, long? previous, long? searchNext = null)
    {
        var messages = raw.DistinctBy(x => x.Id).OrderByDescending(x => x.Id).ToArray();
        var next = searchNext ?? (messages.Length == 0 ? 0 : messages[^1].Id - 1);
        var more = next > 0;
        if (more && previous is > 0 && next >= previous)
            throw new InvalidOperationException("Telegram pagination cursor did not advance.");
        return new(messages, next, more);
    }
}

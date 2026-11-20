using TdLib;

namespace TgCli;

internal enum HistoryVisibility
{
    Unverified,
    NeedsOlderAnchor,
    Full
}

internal static class ExportHistoryVisibility
{
    public static async Task<IReadOnlyDictionary<long, HistoryVisibility>> ReadAsync(
        TelegramSession tg, IEnumerable<long> sources, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, HistoryVisibility>();
        foreach (var chatId in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var chat = await tg.Client.GetChatAsync(chatId);
                cancellationToken.ThrowIfCancellationRequested();
                result[chatId] = chat.Type switch
                {
                    TdApi.ChatType.ChatTypePrivate => HistoryVisibility.Full,
                    TdApi.ChatType.ChatTypeSupergroup group => await ReadSupergroupAsync(tg, group.SupergroupId, cancellationToken),
                    _ => HistoryVisibility.Unverified
                };
            }
            catch (TdException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[chatId] = HistoryVisibility.Unverified;
            }
        }
        return result;
    }

    private static async Task<HistoryVisibility> ReadSupergroupAsync(
        TelegramSession tg, long supergroupId, CancellationToken cancellationToken)
    {
        var group = await tg.Client.GetSupergroupAsync(supergroupId);
        cancellationToken.ThrowIfCancellationRequested();
        var initial = ClassifySupergroup(group, allHistoryAvailable: false);
        if (initial == HistoryVisibility.Full || !IsMember(group.Status)) return initial;

        var full = await tg.Client.GetSupergroupFullInfoAsync(supergroupId);
        cancellationToken.ThrowIfCancellationRequested();
        return ClassifySupergroup(group, full.IsAllHistoryAvailable);
    }

    internal static HistoryVisibility ClassifySupergroup(TdApi.Supergroup group, bool allHistoryAvailable)
    {
        if (!IsMember(group.Status)) return HistoryVisibility.Unverified;
        if (group.IsChannel || group.IsForum || group.HasLinkedChat || group.Usernames?.ActiveUsernames is { Length: > 0 })
            return HistoryVisibility.Full;

        // TDLib describes availability to new members. For the current account, a
        // surviving older message must also establish coverage of the affected key.
        return allHistoryAvailable ? HistoryVisibility.NeedsOlderAnchor : HistoryVisibility.Unverified;
    }

    private static bool IsMember(TdApi.ChatMemberStatus? status) => status is
        TdApi.ChatMemberStatus.ChatMemberStatusMember or
        TdApi.ChatMemberStatus.ChatMemberStatusAdministrator or
        TdApi.ChatMemberStatus.ChatMemberStatusCreator { IsMember: true } or
        TdApi.ChatMemberStatus.ChatMemberStatusRestricted { IsMember: true };
}

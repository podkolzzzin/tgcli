using TdLib;

namespace TgCli;

internal interface IBotTransport : IAsyncDisposable
{
    event Action<TdApi.Update>? SendUpdated;

    Task<TdApi.Chat> SearchAsync(string username);
    Task<TdApi.User> UserAsync(long userId);
    Task<TdApi.UserFullInfo> UserFullInfoAsync(long userId);
    Task<long[]> OwnedBotsAsync();
    Task<TdApi.Message> MessageAsync(long chatId, long messageId);
    Task<IReadOnlyList<TdApi.Message>> HistoryAsync(long chatId, int limit);
    Task<TdApi.Message> SendAsync(long chatId, string text);
    Task<TdApi.CallbackQueryAnswer> ClickAsync(long chatId, long messageId, byte[] data);
}

internal sealed class TdBotTransport : IBotTransport
{
    private readonly TelegramSession _session;
    private TdApi.IClient Client => _session.Client;

    private TdBotTransport(TelegramSession session)
    {
        _session = session;
        Client.UpdateReceived += DispatchSendUpdate;
    }

    internal static async Task<IBotTransport> OpenAsync(string? session) =>
        new TdBotTransport(await TelegramSession.CreateReadyAsync(session));

    public event Action<TdApi.Update>? SendUpdated;

    private void DispatchSendUpdate(object? sender, TdApi.Update update)
    {
        if (update is TdApi.Update.UpdateMessageSendSucceeded or TdApi.Update.UpdateMessageSendFailed)
            SendUpdated?.Invoke(update);
    }

    public Task<TdApi.Chat> SearchAsync(string username) => Client.SearchPublicChatAsync(username);
    public Task<TdApi.User> UserAsync(long userId) => Client.GetUserAsync(userId);
    public Task<TdApi.UserFullInfo> UserFullInfoAsync(long userId) => Client.GetUserFullInfoAsync(userId);
    public async Task<long[]> OwnedBotsAsync() => (await Client.GetOwnedBotsAsync()).UserIds;
    public Task<TdApi.Message> MessageAsync(long chatId, long messageId) => Client.GetMessageAsync(chatId, messageId);
    public async Task<IReadOnlyList<TdApi.Message>> HistoryAsync(long chatId, int limit) =>
        (await Client.GetChatHistoryAsync(chatId, 0, 0, limit, onlyLocal: false)).Messages_;

    public Task<TdApi.Message> SendAsync(long chatId, string text) =>
        Client.SendMessageAsync(
            chatId,
            topicId: null!,
            replyTo: null!,
            options: new TdApi.MessageSendOptions(),
            replyMarkup: null!,
            inputMessageContent: new TdApi.InputMessageContent.InputMessageText
            {
                Text = new TdApi.FormattedText { Text = text, Entities = [] }
            });

    public Task<TdApi.CallbackQueryAnswer> ClickAsync(long chatId, long messageId, byte[] data) =>
        Client.GetCallbackQueryAnswerAsync(chatId, messageId, new TdApi.CallbackQueryPayload.CallbackQueryPayloadData { Data = data });

    public ValueTask DisposeAsync()
    {
        Client.UpdateReceived -= DispatchSendUpdate;
        return _session.DisposeAsync();
    }
}

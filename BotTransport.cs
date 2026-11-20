using TdLib;

namespace TgCli;

internal interface IBotTransport : IAsyncDisposable
{
    event Action<TdApi.Update>? SendUpdated;
    void CheckActive() { }

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
    private readonly List<Task> _pending = [];
    private TdApi.IClient Client => _session.Client;

    private TdBotTransport(TelegramSession session)
    {
        _session = session;
        Client.UpdateReceived += DispatchSendUpdate;
    }

    internal static async Task<IBotTransport> OpenAsync(string? session, CancellationToken cancellationToken) =>
        new TdBotTransport(await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken));

    public void CheckActive() => _session.CheckActive();

    public event Action<TdApi.Update>? SendUpdated;

    private void DispatchSendUpdate(object? sender, TdApi.Update update)
    {
        if (update is TdApi.Update.UpdateMessageSendSucceeded or TdApi.Update.UpdateMessageSendFailed)
            SendUpdated?.Invoke(update);
    }

    public Task<TdApi.Chat> SearchAsync(string username) => Track(Client.SearchPublicChatAsync(username));
    public Task<TdApi.User> UserAsync(long userId) => Track(Client.GetUserAsync(userId));
    public Task<TdApi.UserFullInfo> UserFullInfoAsync(long userId) => Track(Client.GetUserFullInfoAsync(userId));
    public async Task<long[]> OwnedBotsAsync() => (await Track(Client.GetOwnedBotsAsync())).UserIds;
    public Task<TdApi.Message> MessageAsync(long chatId, long messageId) => Track(Client.GetMessageAsync(chatId, messageId));
    public async Task<IReadOnlyList<TdApi.Message>> HistoryAsync(long chatId, int limit) =>
        (await Track(Client.GetChatHistoryAsync(chatId, 0, 0, limit, onlyLocal: false))).Messages_;

    public Task<TdApi.Message> SendAsync(long chatId, string text) =>
        Track(Client.SendMessageAsync(
            chatId,
            topicId: null!,
            replyTo: null!,
            options: new TdApi.MessageSendOptions(),
            replyMarkup: null!,
            inputMessageContent: new TdApi.InputMessageContent.InputMessageText
            {
                Text = new TdApi.FormattedText { Text = text, Entities = [] }
            }));

    public Task<TdApi.CallbackQueryAnswer> ClickAsync(long chatId, long messageId, byte[] data) =>
        Track(Client.GetCallbackQueryAnswerAsync(chatId, messageId, new TdApi.CallbackQueryPayload.CallbackQueryPayloadData { Data = data }));

    private Task<T> Track<T>(Task<T> work)
    {
        _pending.RemoveAll(task => task.IsCompleted);
        OperationDeadline.Observe(work);
        if (!work.IsCompleted) _pending.Add(work);
        return work;
    }

    public async ValueTask DisposeAsync()
    {
        Client.UpdateReceived -= DispatchSendUpdate;
        // A conversation wait can end before native work resolves. Keep the underlying lease until it drains.
        try { await Task.WhenAll(_pending); }
        catch { /* The workflow already received the RPC result or stopped waiting. */ }
        await _session.DisposeAsync();
    }
}

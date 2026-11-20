using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed class BotWorkflowException(string status, string message) : Exception(message)
{
    internal string Status { get; } = status;
}

internal sealed class BotConversation(
    IBotTransport transport, long chatId, long userId, CancellationToken deadline, TimeSpan pollInterval)
{
    private static readonly Regex TokenPattern = new(
        @"(?<![A-Za-z0-9_:-])[0-9]{5,}:[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant);

    internal List<JObject> Receipts { get; } = [];
    internal IReadOnlyList<TdApi.Message> Recent { get; private set; } = [];
    internal TdApi.Message? LastReply { get; private set; }

    internal bool IsFromBot(TdApi.Message message) =>
        message.ChatId == chatId && !message.IsOutgoing && message.SendingState is null
        && message.SenderId is TdApi.MessageSender.MessageSenderUser sender && sender.UserId == userId;

    internal async Task<IReadOnlyList<TdApi.Message>> HistoryAsync(int limit)
    {
        deadline.ThrowIfCancellationRequested();
        Recent = (await transport.HistoryAsync(chatId, limit).WaitAsync(deadline))
            .Where(message => message.ChatId == chatId)
            .OrderBy(message => message.Id)
            .ToArray();
        return Recent;
    }

    internal async Task<Snapshot> CaptureAsync()
    {
        var messages = await HistoryAsync(50);
        return new Snapshot(
            messages.Where(message => message.SendingState is null).Select(message => message.Id).DefaultIfEmpty().Max(),
            messages.Where(IsFromBot).ToDictionary(message => message.Id, Revision));
    }

    internal async Task<long> SendAsync(string step, string text)
    {
        // The terminal update can arrive before the send request returns its temporary ID.
        var updates = Channel.CreateBounded<TdApi.Update>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        transport.SendUpdated += OnSendUpdate;
        var receipt = new JObject { ["step"] = step, ["type"] = "send" };
        try
        {
            deadline.ThrowIfCancellationRequested();
            Receipts.Add(receipt);
            var message = await transport.SendAsync(chatId, text).WaitAsync(deadline);
            if (message.ChatId != chatId || !message.IsOutgoing || message.Id == 0)
                throw Unknown(receipt, "Telegram returned an invalid send receipt.");

            if (message.SendingState is null)
                return message.Id > 0
                    ? ConfirmSend(receipt, message.Id)
                    : throw Unknown(receipt, "Telegram returned an invalid final send receipt.");

            receipt["temporary_message_id"] = message.Id;
            if (message.SendingState is TdApi.MessageSendingState.MessageSendingStateFailed)
                throw Failure(receipt, "Telegram rejected the message.");

            while (true)
            {
                var update = await updates.Reader.ReadAsync(deadline);
                switch (update)
                {
                    case TdApi.Update.UpdateMessageSendSucceeded sent when sent.OldMessageId == message.Id:
                        if (sent.Message.Id <= 0 || !sent.Message.IsOutgoing || sent.Message.SendingState is not null)
                            throw Unknown(receipt, "Telegram returned an invalid final send receipt.");
                        return ConfirmSend(receipt, sent.Message.Id);
                    case TdApi.Update.UpdateMessageSendFailed failed when failed.OldMessageId == message.Id:
                        throw Failure(receipt, "Telegram reported that the message failed to send.", failed.Error?.Code);
                }
            }
        }
        catch (Exception error) when (IsTransportError(error) && Receipts.Contains(receipt))
        {
            throw MutationError(receipt, error, "send");
        }
        finally
        {
            transport.SendUpdated -= OnSendUpdate;
        }

        void OnSendUpdate(TdApi.Update update)
        {
            var updateChat = update switch
            {
                TdApi.Update.UpdateMessageSendSucceeded sent => sent.Message.ChatId,
                TdApi.Update.UpdateMessageSendFailed failed => failed.Message.ChatId,
                _ => 0
            };
            if (updateChat == chatId)
                updates.Writer.TryWrite(update);
        }
    }

    internal async Task<JObject> ClickAsync(string step, long targetId, TdApi.InlineKeyboardButton button)
    {
        deadline.ThrowIfCancellationRequested();
        var callback = (TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeCallback)button.Type;
        var receipt = new JObject
        {
            ["step"] = step,
            ["type"] = "callback",
            ["message_id"] = targetId,
            ["button"] = BotCommands.ToInlineButtonJson(button)
        };
        Receipts.Add(receipt);

        try
        {
            var answer = await transport.ClickAsync(chatId, targetId, callback.Data).WaitAsync(deadline);
            receipt["status"] = "confirmed";
            receipt["answer_text"] = answer.Text;
            receipt["show_alert"] = answer.ShowAlert;
            receipt["url"] = string.IsNullOrWhiteSpace(answer.Url) ? JValue.CreateNull() : answer.Url;
            return receipt;
        }
        catch (Exception error) when (IsTransportError(error))
        {
            throw MutationError(receipt, error, "callback");
        }
    }

    internal async Task<TdApi.Message> WaitForAsync(
        Snapshot snapshot, ActionTarget action, Func<TdApi.Message, bool> matches, string expected)
    {
        var seen = new Dictionary<long, string>();
        try
        {
            while (true)
            {
                foreach (var message in await HistoryAsync(50))
                {
                    if (!IsFromBot(message))
                        continue;

                    var revision = Revision(message);
                    if (!IsCurrentReply(message, revision, snapshot, action))
                        continue;
                    if (seen.TryGetValue(message.Id, out var previous) && revision == previous)
                        continue;

                    seen[message.Id] = revision;
                    LastReply = message;
                    if (matches(message))
                        return message;
                }
                await Task.Delay(pollInterval, deadline);
            }
        }
        catch (Exception error) when (IsTransportError(error))
        {
            throw new BotWorkflowException("outcome_unknown", $"Could not establish {expected} after the request. Check the chat before retrying.");
        }
    }

    private bool IsCurrentReply(TdApi.Message message, string revision, Snapshot snapshot, ActionTarget action)
    {
        var reply = message.ReplyTo as TdApi.MessageReplyTo.MessageReplyToMessage;
        var directReply = reply is not null
            && (reply.ChatId == 0 || reply.ChatId == chatId) && reply.MessageId == action.MessageId;

        if (snapshot.Revisions.TryGetValue(message.Id, out var baseline))
            return revision != baseline && (message.Id == action.EditableMessageId || directReply);

        return message.Id > snapshot.NewestId
            && (!action.IsSend || message.Id > action.MessageId)
            && (reply is null || directReply);
    }

    private static string Revision(TdApi.Message message) => new JObject
    {
        ["edited"] = message.EditDate,
        ["text"] = MessageFiles.GetMessageText(message.Content),
        ["ui"] = BotCommands.ExtractUi(message)
    }.ToString(Formatting.None);

    private static long ConfirmSend(JObject receipt, long id)
    {
        receipt["status"] = "confirmed";
        receipt["sent_message_id"] = id;
        return id;
    }

    private static BotWorkflowException MutationError(JObject receipt, Exception error, string kind)
    {
        if (error is TdException rejected && rejected.Message is
            "CHAT_WRITE_FORBIDDEN" or "USER_IS_BLOCKED" or "BOT_INVALID" or "CHAT_ID_INVALID"
            or "MESSAGE_ID_INVALID" or "BUTTON_DATA_INVALID" or "MESSAGE_NOT_FOUND")
            return Failure(receipt, $"Telegram rejected the {kind}.");

        return Unknown(receipt, $"Telegram did not confirm the {kind}. Check the chat before retrying.");
    }

    private static BotWorkflowException Failure(JObject receipt, string message, int? code = null)
    {
        receipt["status"] = "failed";
        if (code is not null)
            receipt["telegram_error_code"] = code;
        return new BotWorkflowException("failed", message);
    }

    private static BotWorkflowException Unknown(JObject receipt, string message)
    {
        receipt["status"] = "outcome_unknown";
        return new BotWorkflowException("outcome_unknown", message);
    }

    internal static bool IsTransportError(Exception error) =>
        error is TdException or TimeoutException or OperationCanceledException or IOException or ObjectDisposedException;

    internal static string? ExtractToken(string text)
    {
        var match = TokenPattern.Match(text);
        return match.Success ? match.Value : null;
    }

    internal static string RedactTokens(string text) => TokenPattern.Replace(text, "[redacted token]");

    internal sealed record Snapshot(long NewestId, Dictionary<long, string> Revisions);

    internal readonly record struct ActionTarget(long MessageId, bool IsSend, long EditableMessageId = 0);
}

internal readonly record struct BotButtonSelector(string Text, int? Row = null, int? Column = null)
{
    internal static BotButtonSelector Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Button selector must contain a label or a zero-based row,column.", "click");

        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',');
        var coordinates = comma == 0 || (comma > 0 && char.IsAsciiDigit(trimmed.TrimStart('+', '-')[0]));
        if (!coordinates)
            return new BotButtonSelector(value);

        if (int.TryParse(trimmed.AsSpan(0, comma).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            && int.TryParse(trimmed.AsSpan(comma + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var column))
            return new BotButtonSelector(value, row, column);

        throw new ArgumentException("Button coordinates must be two nonnegative integers, row,column.", "click");
    }

    internal TdApi.InlineKeyboardButton Resolve(TdApi.Message message) =>
        Find(message) ?? throw new ArgumentException("No inline button on the message matches the selector.", "click");

    internal TdApi.InlineKeyboardButton? Find(TdApi.Message message)
    {
        if (message.ReplyMarkup is not TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard keyboard)
            return null;

        if (Row is int row && Column is int column)
            return row < keyboard.Rows.Length && column < keyboard.Rows[row].Length
                ? RequireCallback(keyboard.Rows[row][column])
                : null;

        var text = Text;
        var matches = keyboard.Rows.SelectMany(buttons => buttons)
            .Where(button => string.Equals(button.Text, text, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length switch
        {
            1 => RequireCallback(matches[0]),
            0 => null,
            _ => throw new ArgumentException("Multiple inline buttons match the supplied label. Use row,column.", "click")
        };
    }

    internal static TdApi.InlineKeyboardButton RequireCallback(TdApi.InlineKeyboardButton button)
    {
        if (button.Type is not TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeCallback { Data.Length: > 0 })
            throw new ArgumentException("The selected button is not an actionable inline callback.", "click");
        return button;
    }
}

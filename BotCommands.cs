using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

public sealed class BotCommands
{
    private readonly Func<string?, Task<IBotTransport>> _open;
    private readonly TextWriter? _output;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    public BotCommands() : this(TdBotTransport.OpenAsync, null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(300)) { }

    internal BotCommands(Func<string?, Task<IBotTransport>> open, TextWriter? output, TimeSpan timeout, TimeSpan pollInterval)
    {
        _open = open;
        _output = output;
        _timeout = timeout;
        _pollInterval = pollInterval;
    }

    private TextWriter Writer => _output ?? Console.Out;

    /// <summary>
    /// Create a bot managed by BotFather.
    /// </summary>
    /// <param name="name">Display name for the bot.</param>
    /// <param name="username">Bot username, with or without @. Must end in bot.</param>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Create(string name, string username, string format = "json", string? session = null)
    {
        format = ValidateFormat(format);
        username = NormalizeUsername(username, creating: true);
        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.StartsWith('/') || name.EnumerateRunes().Count() > 64)
            throw new ArgumentException("Bot name must be 1–64 characters and cannot be a command.", nameof(name));

        await RunBotFatherAsync(BotFatherOperation.Create, username, format, session, name);
    }

    /// <summary>
    /// Remove a bot through BotFather's delete flow.
    /// </summary>
    /// <param name="username">Bot username, with or without @.</param>
    /// <param name="confirm">Required to execute deletion.</param>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Remove(string username, bool confirm = false, string format = "json", string? session = null)
    {
        format = ValidateFormat(format);
        username = NormalizeUsername(username);
        if (!confirm)
            throw new ArgumentException("Pass --confirm to remove a bot through BotFather.");

        await RunBotFatherAsync(BotFatherOperation.Remove, username, format, session);
    }

    /// <summary>
    /// List bots owned by the current account.
    /// </summary>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task List(string format = "tsv", string? session = null)
    {
        format = ValidateFormat(format);
        await using var tg = await _open(session);
        using var timeout = new CancellationTokenSource(_timeout);
        await PrintBotRowsAsync(tg, await tg.OwnedBotsAsync().WaitAsync(timeout.Token), format, timeout.Token);
    }

    /// <summary>
    /// Print or revoke the token for a bot managed by the current account.
    /// </summary>
    /// <param name="username">Bot username, with or without @.</param>
    /// <param name="revoke">Revoke the old token and print the new token.</param>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Token(string username, bool revoke = false, string format = "plain", string? session = null)
    {
        format = ValidateFormat(format);
        username = NormalizeUsername(username);
        await RunBotFatherAsync(revoke ? BotFatherOperation.Revoke : BotFatherOperation.Token, username, format, session);
    }

    /// <summary>
    /// Send a message to a bot and optionally press an inline UI button.
    /// </summary>
    /// <param name="username">Bot username, with or without @.</param>
    /// <param name="text">Text to send. Use /start to start a bot.</param>
    /// <param name="click">Inline callback selector: button text or zero-based row,column such as 0,1.</param>
    /// <param name="messageId">Message id whose inline button should be pressed. With text and no id, waits for a fresh keyboard.</param>
    /// <param name="limit">Recent messages to print after the interaction (1–100).</param>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Write(
        string username,
        string? text = null,
        string? click = null,
        long messageId = 0,
        int limit = 10,
        string format = "json",
        string? session = null)
    {
        format = ValidateFormat(format);
        username = NormalizeUsername(username);
        if (text is not null && (string.IsNullOrWhiteSpace(text) || text.EnumerateRunes().Count() > 4096))
            throw new ArgumentException("Text must contain a message of at most 4096 characters.", nameof(text));
        var selector = click is null ? (BotButtonSelector?)null : BotButtonSelector.Parse(click);
        if (messageId < 0 || messageId != 0 && selector is null)
            throw new ArgumentException("Message id must be positive and used with --click.", nameof(messageId));
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");

        await using var tg = await _open(session);
        using var timeout = new CancellationTokenSource(_timeout);
        var deadline = timeout.Token;
        var (chatId, user) = await ResolveBotAsync(tg, username, deadline);
        var bot = await ToBotJsonAsync(tg, user, deadline);
        var dialog = new BotConversation(tg, chatId, user.Id, deadline, _pollInterval);

        TdApi.Message? target = null;
        TdApi.InlineKeyboardButton? button = null;
        if (selector is { } preflight && (messageId != 0 || text is null))
        {
            target = messageId == 0
                ? (await dialog.HistoryAsync(50)).LastOrDefault(message => dialog.IsFromBot(message)
                    && message.ReplyMarkup is TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard)
                : await tg.MessageAsync(chatId, messageId).WaitAsync(deadline);
            if (target is null || !dialog.IsFromBot(target) || messageId != 0 && target.Id != messageId)
                throw new ArgumentException("No accessible inline keyboard from the selected bot matched the request.", "click");

            button = preflight.Resolve(target);
        }

        long? sentId = null;
        long? replyId = null;
        JObject? clickResult = null;
        BotWorkflowException? failure = null;
        string? reason = null;
        var status = "confirmed";
        var historyStatus = "observed";
        try
        {
            if (text is not null)
            {
                var snapshot = await dialog.CaptureAsync();
                sentId = await dialog.SendAsync("send_text", text);

                if (selector is null || target is null)
                {
                    var expectsKeyboard = selector is not null;
                    TdApi.Message? reply = null;
                    try
                    {
                        reply = await dialog.WaitForAsync(snapshot, new BotConversation.ActionTarget(sentId.Value, IsSend: true),
                            message => !expectsKeyboard || (button = selector!.Value.Find(message)) is not null,
                            expectsKeyboard ? "a fresh inline keyboard from the bot" : "a fresh reply from the bot");
                    }
                    catch (BotWorkflowException error) when (!expectsKeyboard)
                    {
                        status = "requested";
                        reason = error.Message;
                    }

                    replyId = reply?.Id;
                    if (expectsKeyboard)
                        target = reply;
                }
                else
                {
                    // Sending the text may have changed the explicit keyboard. Verify that
                    // the already validated callback is still available before pressing it.
                    var current = await tg.MessageAsync(chatId, target.Id).WaitAsync(deadline);
                    if (current is null || current.Id != target.Id || !dialog.IsFromBot(current)
                        || !JToken.DeepEquals(ToInlineButtonJson(button!), ToInlineButtonJson(selector.Value.Resolve(current))))
                        throw new ArgumentException("The inline callback changed after the message was sent.", "click");
                }
            }

            if (target is not null)
                clickResult = await dialog.ClickAsync("press_button", target.Id, button!);
        }
        catch (BotWorkflowException error)
        {
            failure = error;
        }
        catch (ArgumentException error) when (sentId is not null)
        {
            failure = new BotWorkflowException("failed", error.Message);
        }
        catch (Exception error) when (dialog.Receipts.Count > 0 && BotConversation.IsTransportError(error))
        {
            failure = new BotWorkflowException("outcome_unknown", "Could not finish the interaction. Check the chat before retrying.");
        }

        try
        {
            if (status == "requested" || failure is not null)
                historyStatus = "last_observed";
            else
                await dialog.HistoryAsync(limit);
        }
        catch (Exception error) when (dialog.Receipts.Count > 0 && BotConversation.IsTransportError(error))
        {
            historyStatus = "last_observed";
        }

        var history = dialog.Recent.TakeLast(limit).ToArray();
        var payload = new JObject
        {
            ["bot"] = bot,
            ["chat_id"] = chatId,
            ["sent_message_id"] = IdValue(sentId),
            ["reply_message_id"] = IdValue(replyId),
            ["click"] = clickResult is null ? JValue.CreateNull() : clickResult,
            ["messages"] = new JArray(history.Select(ToMessageJson)),
            ["history_status"] = historyStatus
        };
        var plain = string.Join(Environment.NewLine, history.Reverse()
            .Select(message => MessageFiles.GetMessageText(message.Content)).Where(value => !string.IsNullOrWhiteSpace(value)));
        PrintOutcome(payload, dialog, format, status, plain, failure, reason);
    }

    private async Task RunBotFatherAsync(BotFatherOperation operation, string username, string format, string? session, string? name = null)
    {
        await using var tg = await _open(session);
        using var timeout = new CancellationTokenSource(_timeout);
        var (chatId, user) = await ResolveBotAsync(tg, "BotFather", timeout.Token);
        var dialog = new BotConversation(tg, chatId, user.Id, timeout.Token, _pollInterval);
        string? token = null;
        BotWorkflowException? failure = null;
        try
        {
            token = await new BotFatherFlow(dialog, username).RunAsync(operation, name);
        }
        catch (BotWorkflowException error)
        {
            failure = error;
        }
        catch (Exception error) when (dialog.Receipts.Count > 0 && BotConversation.IsTransportError(error))
        {
            failure = new BotWorkflowException("outcome_unknown", "Could not finish the BotFather exchange. Check the chat before retrying.");
        }

        var reply = dialog.LastReply;
        var response = reply is null ? "" : MessageFiles.GetMessageText(reply.Content);
        var payload = new JObject
        {
            ["username"] = username,
            ["botfather_chat_id"] = chatId,
            ["message_id"] = IdValue(reply?.Id),
            ["botfather_reply"] = BotConversation.RedactTokens(response),
            ["ui"] = reply is null ? JValue.CreateNull() : ExtractUi(reply)
        };
        if (operation != BotFatherOperation.Remove)
            payload["token"] = token is null ? JValue.CreateNull() : token;
        if (operation == BotFatherOperation.Token)
            payload["revoked"] = false;
        if (operation == BotFatherOperation.Revoke)
            payload["revoked"] = failure is null ? new JValue(true) : JValue.CreateNull();

        // Only the confirmed token field and confirmed plain output expose credentials.
        foreach (var receipt in dialog.Receipts)
        {
            if (receipt.Value<string>("answer_text") is { } answer)
                receipt["answer_text"] = BotConversation.RedactTokens(answer);
        }

        PrintOutcome(payload, dialog, format, "confirmed", token ?? response, failure);
    }

    private void PrintOutcome(JObject payload, BotConversation dialog, string format, string status, string plain,
        BotWorkflowException? failure, string? reason = null)
    {
        status = failure?.Status ?? status;
        reason = failure?.Message ?? reason;
        payload["status"] = status;
        payload["reason"] = reason;
        payload["receipts"] = new JArray(dialog.Receipts);
        PrintObject(payload, format, reason is null ? plain : $"{status} — {reason}");
        if (failure is not null)
            throw failure;
    }

    private static async Task<(long ChatId, TdApi.User User)> ResolveBotAsync(IBotTransport tg, string username, CancellationToken deadline)
    {
        var chat = await tg.SearchAsync(username).WaitAsync(deadline);
        if (chat.Type is not TdApi.ChatType.ChatTypePrivate privateChat)
            throw new InvalidOperationException($"@{username} did not resolve to a private bot chat.");

        var user = await tg.UserAsync(privateChat.UserId).WaitAsync(deadline);
        if (user.Id != privateChat.UserId || user.Type is not TdApi.UserType.UserTypeBot)
            throw new InvalidOperationException($"@{username} did not resolve to a bot.");

        return (chat.Id, user);
    }

    private static JValue IdValue(long? id) => id is long value ? new JValue(value) : JValue.CreateNull();

    private static string ValidateFormat(string? format)
    {
        var value = format?.Trim().ToLowerInvariant();
        return value is "json" or "jsonl" or "tsv" or "plain"
            ? value : throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
    }

    private static string NormalizeUsername(string? username, bool creating = false)
    {
        var normalized = username?.Trim() ?? "";
        if (normalized.StartsWith('@'))
            normalized = normalized[1..];
        if (!Regex.IsMatch(normalized, @"\A[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Username must contain only ASCII letters, digits and underscores, with an optional leading @.", nameof(username));
        if (creating && (normalized.Length < 5 || !normalized.EndsWith("bot", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A new bot username must be 5–32 characters and end in bot.", nameof(username));
        return normalized;
    }

    internal static JToken ExtractUi(TdApi.Message message)
    {
        return message.ReplyMarkup switch
        {
            TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard inlineKeyboard => new JObject
            {
                ["type"] = "inline",
                ["buttons"] = new JArray(inlineKeyboard.Rows.Select((row, rowIndex) =>
                    new JArray(row.Select((button, columnIndex) => ToInlineButtonJson(button, rowIndex, columnIndex)))))
            },
            TdApi.ReplyMarkup.ReplyMarkupShowKeyboard keyboard => new JObject
            {
                ["type"] = "reply",
                ["buttons"] = new JArray(keyboard.Rows.Select((row, rowIndex) =>
                    new JArray(row.Select((button, columnIndex) => new JObject
                    {
                        ["row"] = rowIndex,
                        ["column"] = columnIndex,
                        ["text"] = button.Text,
                        ["type"] = button.Type?.GetType().Name ?? "unknown"
                    }))))
            },
            _ => JValue.CreateNull()
        };
    }

    private static JObject ToMessageJson(TdApi.Message message)
    {
        return new JObject
        {
            ["chat_id"] = message.ChatId,
            ["message_id"] = message.Id,
            ["date"] = DateTimeOffset.FromUnixTimeSeconds(message.Date).ToString("O", CultureInfo.InvariantCulture),
            ["sender"] = message.SenderId switch
            {
                TdApi.MessageSender.MessageSenderUser user => $"user:{user.UserId}",
                TdApi.MessageSender.MessageSenderChat chat => $"chat:{chat.ChatId}",
                _ => string.Empty
            },
            ["kind"] = MessageFiles.GetKind(message.Content),
            ["text"] = MessageFiles.GetMessageText(message.Content),
            ["ui"] = ExtractUi(message)
        };
    }

    internal static JObject ToInlineButtonJson(TdApi.InlineKeyboardButton button, int? row = null, int? column = null)
    {
        var result = new JObject
        {
            ["text"] = button.Text,
            ["type"] = button.Type?.GetType().Name.Replace("InlineKeyboardButtonType", string.Empty).ToLowerInvariant() ?? "unknown"
        };
        if (row is not null) result["row"] = row;
        if (column is not null) result["column"] = column;

        switch (button.Type)
        {
            case TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeCallback callback:
                result["callback_data_base64"] = Convert.ToBase64String(callback.Data ?? []);
                break;
            case TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeUrl url:
                result["url"] = url.Url;
                break;
            case TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeWebApp webApp:
                result["url"] = webApp.Url;
                break;
        }

        return result;
    }

    private async Task PrintBotRowsAsync(IBotTransport tg, IEnumerable<long> userIds, string format, CancellationToken deadline)
    {
        var rows = new JArray();
        foreach (var userId in userIds)
        {
            rows.Add(await ToBotJsonAsync(tg, await tg.UserAsync(userId).WaitAsync(deadline), deadline));
        }

        PrintRows(rows, format);
    }

    private static async Task<JObject> ToBotJsonAsync(IBotTransport tg, TdApi.User user, CancellationToken deadline)
    {
        var username = user.Usernames?.ActiveUsernames?.FirstOrDefault() ?? string.Empty;
        var name = $"{user.FirstName} {user.LastName}".Trim();
        TdApi.BotInfo? info = null;
        try
        {
            var fullInfo = await tg.UserFullInfoAsync(user.Id).WaitAsync(deadline);
            info = fullInfo.BotInfo;
        }
        catch (TdException)
        {
            // Bot info is optional context; listing should still work if Telegram refuses it.
        }

        return new JObject
        {
            ["user_id"] = user.Id,
            ["name"] = name,
            ["username"] = username,
            ["is_bot"] = user.Type is TdApi.UserType.UserTypeBot,
            ["can_be_edited"] = user.Type is TdApi.UserType.UserTypeBot botType && botType.CanBeEdited,
            ["description"] = info?.Description ?? string.Empty,
            ["short_description"] = info?.ShortDescription ?? string.Empty
        };
    }

    private void PrintRows(JArray rows, string format)
    {
        switch (format)
        {
            case "json":
                Writer.WriteLine(rows.ToString(Formatting.None));
                break;
            case "jsonl":
                foreach (var row in rows) Writer.WriteLine(row.ToString(Formatting.None));
                break;
            case "tsv":
                Writer.WriteLine("user_id\tusername\tname\tcan_be_edited\tshort_description");
                foreach (var row in rows.OfType<JObject>())
                {
                    Writer.WriteLine(string.Join('\t',
                        row.Value<long>("user_id"),
                        Clean(row.Value<string>("username")),
                        Clean(row.Value<string>("name")),
                        row.Value<bool>("can_be_edited"),
                        Clean(row.Value<string>("short_description"))));
                }
                break;
            case "plain":
                foreach (var row in rows.OfType<JObject>())
                {
                    Writer.WriteLine("@" + row.Value<string>("username"));
                }
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    private void PrintObject(JObject value, string format, string plainValue)
    {
        switch (format)
        {
            case "json":
            case "jsonl":
                Writer.WriteLine(value.ToString(Formatting.None));
                break;
            case "tsv":
                Writer.WriteLine(string.Join('\t', value.Properties().Select(x => x.Name)));
                Writer.WriteLine(string.Join('\t', value.Properties().Select(x => Clean(x.Value.Type is JTokenType.Null ? string.Empty : x.Value.ToString(Formatting.None)))));
                break;
            case "plain":
                Writer.WriteLine(plainValue);
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    private static string Clean(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}

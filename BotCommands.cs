using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

public sealed class BotCommands
{
    /// <summary>
    /// Create a bot managed by BotFather.
    /// </summary>
    /// <param name="name">Display name for the bot.</param>
    /// <param name="username">Bot username, with or without @. Must end in bot.</param>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Create(string name, string username, string format = "json", string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var botFatherChat = await ResolveBotFatherChatAsync(tg);
        var normalized = NormalizeUsername(username);
        var command = await SendTextAsync(tg, botFatherChat.Id, "/newbot");
        var namePrompt = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: command.Id);
        var nameMessage = await SendTextAsync(tg, botFatherChat.Id, name);
        var usernamePrompt = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: Math.Max(namePrompt.Id, nameMessage.Id));
        var usernameMessage = await SendTextAsync(tg, botFatherChat.Id, normalized);
        var finalReply = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: Math.Max(usernamePrompt.Id, usernameMessage.Id), seconds: 20);
        var text = MessageFiles.GetMessageText(finalReply.Content);
        var token = ExtractToken(text);

        PrintObject(new JObject
        {
            ["username"] = normalized,
            ["botfather_chat_id"] = botFatherChat.Id,
            ["message_id"] = finalReply.Id,
            ["status"] = token is null ? "unknown" : "created",
            ["token"] = token is null ? JValue.CreateNull() : token,
            ["botfather_reply"] = text,
            ["ui"] = ExtractUi(finalReply)
        }, format, plainValue: token ?? text);
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
        if (!confirm)
        {
            throw new ArgumentException("Pass --confirm to remove a bot through BotFather.");
        }

        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var botFatherChat = await ResolveBotFatherChatAsync(tg);
        var normalized = NormalizeUsername(username);
        var command = await SendTextAsync(tg, botFatherChat.Id, "/deletebot");
        var firstReply = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: command.Id);
        if (!await PressButtonIfAvailableAsync(tg, firstReply, "@" + normalized))
        {
            var usernameMessage = await SendTextAsync(tg, botFatherChat.Id, "@" + normalized);
            firstReply = firstReply.Id > usernameMessage.Id ? firstReply : usernameMessage;
        }

        var confirmReply = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: firstReply.Id);
        if (!await PressButtonIfAvailableAsync(tg, confirmReply, "Yes, I am totally sure."))
        {
            var confirmMessage = await SendTextAsync(tg, botFatherChat.Id, "Yes, I am totally sure.");
            confirmReply = confirmReply.Id > confirmMessage.Id ? confirmReply : confirmMessage;
        }

        var finalReply = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: confirmReply.Id);

        PrintObject(new JObject
        {
            ["username"] = normalized,
            ["botfather_chat_id"] = botFatherChat.Id,
            ["message_id"] = finalReply.Id,
            ["status"] = "requested",
            ["botfather_reply"] = MessageFiles.GetMessageText(finalReply.Content),
            ["ui"] = ExtractUi(finalReply)
        }, format, plainValue: MessageFiles.GetMessageText(finalReply.Content));
    }

    /// <summary>
    /// List bots owned by the current account.
    /// </summary>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task List(string format = "tsv", string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var bots = await tg.Client.GetOwnedBotsAsync();
        await PrintBotRowsAsync(tg, bots.UserIds, format);
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
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var botFatherChat = await ResolveBotFatherChatAsync(tg);
        var normalized = NormalizeUsername(username);
        var command = await SendTextAsync(tg, botFatherChat.Id, revoke ? "/revoke" : "/token");
        var firstReply = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: command.Id);
        if (!await PressButtonIfAvailableAsync(tg, firstReply, "@" + normalized))
        {
            var usernameMessage = await SendTextAsync(tg, botFatherChat.Id, "@" + normalized);
            firstReply = firstReply.Id > usernameMessage.Id ? firstReply : usernameMessage;
        }

        var finalReply = await WaitForBotReplyAsync(tg, botFatherChat.Id, afterMessageId: firstReply.Id, seconds: 20);
        var text = MessageFiles.GetMessageText(finalReply.Content);
        var token = ExtractToken(text);
        PrintObject(new JObject
        {
            ["username"] = normalized,
            ["token"] = token is null ? JValue.CreateNull() : token,
            ["revoked"] = revoke,
            ["botfather_chat_id"] = botFatherChat.Id,
            ["message_id"] = finalReply.Id,
            ["botfather_reply"] = text,
            ["ui"] = ExtractUi(finalReply)
        }, format, plainValue: token ?? text);
    }

    /// <summary>
    /// Send a message to a bot and optionally press an inline UI button.
    /// </summary>
    /// <param name="username">Bot username, with or without @.</param>
    /// <param name="text">Text to send. Use /start to start a bot.</param>
    /// <param name="click">Inline button selector: button text or zero-based row,column such as 0,1.</param>
    /// <param name="messageId">Message id whose inline button should be pressed. Defaults to the latest bot message.</param>
    /// <param name="limit">Recent messages to print after the interaction.</param>
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
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var chat = await ResolveBotChatAsync(tg, username);

        TdApi.Message? sent = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            sent = await SendTextAsync(tg, chat.Id, text);
        }

        JObject? clickResult = null;
        if (!string.IsNullOrWhiteSpace(click))
        {
            var target = messageId == 0
                ? await FindLatestBotUiMessageAsync(tg, chat.Id)
                : await tg.Client.GetMessageAsync(chat.Id, messageId);
            clickResult = await ClickInlineButtonAsync(tg, target, click);
        }

        var history = (await tg.Client.GetChatHistoryAsync(chat.Id, 0, 0, ClampLimit(limit, 100), onlyLocal: false)).Messages_;
        var payload = new JObject
        {
            ["bot"] = await ToBotJsonAsync(tg, await ResolveBotUserAsync(tg, username)),
            ["chat_id"] = chat.Id,
            ["sent_message_id"] = sent?.Id == null ? JValue.CreateNull() : sent.Id,
            ["click"] = clickResult is null ? JValue.CreateNull() : clickResult,
            ["messages"] = new JArray(history.OrderBy(x => x.Id).Select(ToMessageJson))
        };

        PrintObject(payload, format, plainValue: string.Join(Environment.NewLine, history.OrderByDescending(x => x.Id).Select(x => MessageFiles.GetMessageText(x.Content)).Where(x => !string.IsNullOrWhiteSpace(x))));
    }

    private static async Task<TdApi.Chat> ResolveBotFatherChatAsync(TelegramSession tg)
    {
        return await tg.Client.SearchPublicChatAsync("BotFather");
    }

    private static async Task<TdApi.Chat> ResolveBotChatAsync(TelegramSession tg, string username)
    {
        var chat = await tg.Client.SearchPublicChatAsync(NormalizeUsername(username));
        if (chat.Type is not TdApi.ChatType.ChatTypePrivate)
        {
            throw new InvalidOperationException($"@{NormalizeUsername(username)} did not resolve to a private bot chat.");
        }

        return chat;
    }

    private static async Task<TdApi.User> ResolveBotUserAsync(TelegramSession tg, string username)
    {
        var chat = await ResolveBotChatAsync(tg, username);
        var userId = chat.Type is TdApi.ChatType.ChatTypePrivate privateChat
            ? privateChat.UserId
            : throw new InvalidOperationException($"@{NormalizeUsername(username)} did not resolve to a user.");
        var user = await tg.Client.GetUserAsync(userId);
        if (user.Type is not TdApi.UserType.UserTypeBot)
        {
            throw new InvalidOperationException($"@{NormalizeUsername(username)} is not a bot.");
        }

        return user;
    }

    private static async Task<TdApi.Message> SendTextAsync(TelegramSession tg, long chatId, string text)
    {
        return await tg.Client.SendMessageAsync(
            chatId,
            topicId: null!,
            replyTo: null!,
            options: new TdApi.MessageSendOptions(),
            replyMarkup: null!,
            inputMessageContent: new TdApi.InputMessageContent.InputMessageText
            {
                Text = new TdApi.FormattedText { Text = text, Entities = [] },
                ClearDraft = true
            });
    }

    private static async Task<TdApi.Message> WaitForBotReplyAsync(TelegramSession tg, long chatId, long afterMessageId = 0, int seconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var latest = (await tg.Client.GetChatHistoryAsync(chatId, 0, 0, 20, onlyLocal: false)).Messages_
                .Where(x => x.Id > afterMessageId)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.SenderId is TdApi.MessageSender.MessageSenderUser && !x.IsOutgoing);
            if (latest is not null)
            {
                return latest;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Timed out waiting for BotFather reply.");
    }

    private static async Task<TdApi.Message> FindLatestBotUiMessageAsync(TelegramSession tg, long chatId)
    {
        var message = (await tg.Client.GetChatHistoryAsync(chatId, 0, 0, 50, onlyLocal: false)).Messages_
            .OrderByDescending(x => x.Id)
            .FirstOrDefault(x => x.ReplyMarkup is TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard);
        return message ?? throw new InvalidOperationException("No recent inline keyboard message found. Pass --message-id or send a bot command first.");
    }

    private static async Task<bool> PressButtonIfAvailableAsync(TelegramSession tg, TdApi.Message message, string selector)
    {
        if (message.ReplyMarkup is TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard)
        {
            try
            {
                var result = await ClickInlineButtonAsync(tg, message, selector);
                return result.Value<string>("status") == "clicked";
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<JObject> ClickInlineButtonAsync(TelegramSession tg, TdApi.Message message, string selector)
    {
        var button = FindInlineButton(message, selector);
        if (button.Type is not TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeCallback callback)
        {
            return new JObject
            {
                ["message_id"] = message.Id,
                ["button"] = ToInlineButtonJson(button),
                ["status"] = "not-clickable",
                ["reason"] = "Only callback inline buttons can be pressed by tgcli."
            };
        }

        var answer = await tg.Client.GetCallbackQueryAnswerAsync(
            message.ChatId,
            message.Id,
            new TdApi.CallbackQueryPayload.CallbackQueryPayloadData { Data = callback.Data });

        return new JObject
        {
            ["message_id"] = message.Id,
            ["button"] = ToInlineButtonJson(button),
            ["status"] = "clicked",
            ["answer_text"] = answer.Text,
            ["show_alert"] = answer.ShowAlert,
            ["url"] = string.IsNullOrWhiteSpace(answer.Url) ? JValue.CreateNull() : answer.Url
        };
    }

    private static TdApi.InlineKeyboardButton FindInlineButton(TdApi.Message message, string selector)
    {
        if (message.ReplyMarkup is not TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard keyboard)
        {
            throw new InvalidOperationException($"Message {message.ChatId}/{message.Id} has no inline keyboard.");
        }

        if (TryParsePosition(selector, out var row, out var column))
        {
            if (row >= 0 && row < keyboard.Rows.Length && column >= 0 && column < keyboard.Rows[row].Length)
            {
                return keyboard.Rows[row][column];
            }

            throw new ArgumentException($"Button selector '{selector}' is outside the inline keyboard.");
        }

        var matches = keyboard.Rows.SelectMany(x => x).Where(x => x.Text.Equals(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No inline button with text '{selector}' was found."),
            _ => throw new ArgumentException($"Multiple inline buttons with text '{selector}' were found. Use row,column.")
        };
    }

    private static JToken ExtractUi(TdApi.Message message)
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

    private static JObject ToInlineButtonJson(TdApi.InlineKeyboardButton button, int? row = null, int? column = null)
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

    private static async Task PrintBotRowsAsync(TelegramSession tg, IEnumerable<long> userIds, string format)
    {
        var rows = new JArray();
        foreach (var userId in userIds)
        {
            rows.Add(await ToBotJsonAsync(tg, await tg.Client.GetUserAsync(userId)));
        }

        PrintRows(rows, format);
    }

    private static async Task<JObject> ToBotJsonAsync(TelegramSession tg, TdApi.User user)
    {
        var username = user.Usernames?.ActiveUsernames?.FirstOrDefault() ?? string.Empty;
        var name = $"{user.FirstName} {user.LastName}".Trim();
        TdApi.BotInfo? info = null;
        try
        {
            var fullInfo = await tg.Client.GetUserFullInfoAsync(user.Id);
            info = fullInfo.BotInfo;
        }
        catch
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

    private static void PrintRows(JArray rows, string format)
    {
        switch (format.Trim().ToLowerInvariant())
        {
            case "json":
                Console.WriteLine(rows.ToString(Formatting.None));
                break;
            case "jsonl":
                foreach (var row in rows) Console.WriteLine(row.ToString(Formatting.None));
                break;
            case "tsv":
                Console.WriteLine("user_id\tusername\tname\tcan_be_edited\tshort_description");
                foreach (var row in rows.OfType<JObject>())
                {
                    Console.WriteLine(string.Join('\t',
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
                    Console.WriteLine("@" + row.Value<string>("username"));
                }
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    private static void PrintObject(JObject value, string format, string plainValue)
    {
        switch (format.Trim().ToLowerInvariant())
        {
            case "json":
            case "jsonl":
                Console.WriteLine(value.ToString(Formatting.None));
                break;
            case "tsv":
                Console.WriteLine(string.Join('\t', value.Properties().Select(x => x.Name)));
                Console.WriteLine(string.Join('\t', value.Properties().Select(x => Clean(x.Value.Type is JTokenType.Null ? string.Empty : x.Value.ToString(Formatting.None)))));
                break;
            case "plain":
                Console.WriteLine(plainValue);
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    private static bool TryParsePosition(string value, out int row, out int column)
    {
        row = 0;
        column = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out row)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out column);
    }

    private static string? ExtractToken(string text)
    {
        var match = Regex.Match(text, @"\b\d{5,}:[A-Za-z0-9_-]{20,}\b");
        return match.Success ? match.Value : null;
    }

    private static int ClampLimit(int value, int max)
    {
        if (value <= 0)
        {
            return 1;
        }

        return Math.Min(value, max);
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Username must not be empty.", nameof(username));
        }

        return normalized;
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

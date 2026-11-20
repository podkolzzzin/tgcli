using System.Text.RegularExpressions;
using TdLib;

namespace TgCli;

internal enum BotFatherOperation { Create, Remove, Token, Revoke }

internal sealed class BotFatherFlow(BotConversation dialog, string username)
{
    private static readonly Regex Mention = new(@"@([A-Za-z0-9_]+)", RegexOptions.CultureInvariant);
    private static readonly Regex TokenAccount = new(
        @"\bfor\s+(?:(?:the\s+)?bot\s+)?@([A-Za-z0-9_]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CreatePromptName = new(
        @"\b(?:choose|pick|send|enter|give)\b[^\r\n?!]*\bname\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CreatePromptUsername = new(
        @"\b(?:choose|pick|send|enter|give)\b[^\r\n?!]*\busername\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RotationAcknowledgement = new(
        @"\btoken (?:was |has been |is )?(?:successfully )?(?:revoked|replaced)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RotationDenied = new(
        @"\b(?:no token|token (?:was |has been |is )?(?:not|never))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string DeleteConfirmation = "Yes, I am totally sure.";

    internal async Task<string?> RunAsync(BotFatherOperation operation, string? name)
    {
        switch (operation)
        {
            case BotFatherOperation.Create:
                return await CreateAsync(name!);
            case BotFatherOperation.Remove:
                await RemoveAsync();
                return null;
            default:
                return await GetTokenAsync(operation == BotFatherOperation.Revoke);
        }
    }

    private async Task<string> CreateAsync(string name)
    {
        var namePrompt = await SendAndWaitAsync("start_create", "/newbot", "BotFather's name prompt",
            message => CreatePromptName.IsMatch(Text(message)) && !Has(Text(message), "username"));
        var usernamePrompt = await SendAndWaitAsync("submit_name", name, "BotFather's username prompt",
            message => CreatePromptUsername.IsMatch(Text(message)), namePrompt.Id);

        var created = false;
        string? token = null;
        await SendAndWaitAsync("submit_username", username, "BotFather's creation confirmation and token", message =>
        {
            var text = Text(message);
            if (Has(text, "congratulations on your new bot") && ContainsAccount(text))
                created = true;
            if (RecognizedToken(text) is { } received && IsExpectedTokenAccount(text))
                token = received;
            return created && token is not null;
        }, usernamePrompt.Id);
        return token!;
    }

    private async Task RemoveAsync()
    {
        var selection = await SelectBotAsync("/deletebot", BotFatherOperation.Remove);
        var confirmation = await WaitAsync(selection, "BotFather's delete confirmation prompt", message =>
            IsDeletePrompt(Text(message)) && FindChoice(message, DeleteConfirmation, IsTextDeleteConfirmation) is not null);

        var confirmed = await ActivateAsync("confirm_delete", confirmation,
            FindChoice(confirmation, DeleteConfirmation, IsTextDeleteConfirmation)!);
        await WaitAsync(confirmed, "BotFather's deletion result", message =>
        {
            var text = Text(message);
            return MentionsOnlyExpectedAccount(text)
                && (Has(text, "the bot is gone") ||
                    Regex.IsMatch(text, @"\b(?:the\s+)?bot(?:\s+@[a-z0-9_]+)?\s+(?:has been|was|is)\s+(?:successfully\s+)?deleted\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        });
    }

    private async Task<string> GetTokenAsync(bool revoke)
    {
        var selection = await SelectBotAsync(revoke ? "/revoke" : "/token",
            revoke ? BotFatherOperation.Revoke : BotFatherOperation.Token);
        string? token = null;
        var rotated = !revoke;
        await WaitAsync(selection, revoke ? "BotFather's rotation confirmation and new token" : "BotFather's token response", message =>
        {
            var text = Text(message);
            if (!IsExpectedTokenAccount(text))
                return false;
            if (RotationAcknowledgement.IsMatch(text) && !RotationDenied.IsMatch(text))
                rotated = true;
            if (RecognizedToken(text) is { } received)
                token = received;
            return rotated && token is not null;
        });
        return token!;
    }

    private async Task<PendingReply> SelectBotAsync(string command, BotFatherOperation operation)
    {
        var label = "@" + username;
        var selection = await SendAndWaitAsync("start_" + operation.ToString().ToLowerInvariant(), command,
            $"BotFather's selection prompt for {label}",
            message => IsSelectionPrompt(Text(message), operation) &&
                FindChoice(message, label, IsTextSelectionPrompt) is not null);
        return await ActivateAsync("select_bot", selection, FindChoice(selection, label, IsTextSelectionPrompt)!);
    }

    private async Task<PendingReply> ActivateAsync(string step, TdApi.Message prompt, Choice choice)
    {
        var snapshot = await dialog.CaptureAsync();
        if (choice.Button is not null)
        {
            await dialog.ClickAsync(step, prompt.Id, choice.Button);
            return new PendingReply(snapshot, new BotConversation.ActionTarget(prompt.Id, IsSend: false, prompt.Id));
        }

        var sentId = await dialog.SendAsync(step, choice.Label);
        return new PendingReply(snapshot, new BotConversation.ActionTarget(sentId, IsSend: true, prompt.Id));
    }

    private async Task<TdApi.Message> SendAndWaitAsync(
        string step, string input, string expected, Func<TdApi.Message, bool> matches, long editableMessageId = 0)
    {
        var snapshot = await dialog.CaptureAsync();
        var sentId = await dialog.SendAsync(step, input);
        return await WaitAsync(new PendingReply(snapshot, new BotConversation.ActionTarget(sentId, true, editableMessageId)),
            expected, matches);
    }

    private Task<TdApi.Message> WaitAsync(PendingReply pending, string expected, Func<TdApi.Message, bool> matches) =>
        dialog.WaitForAsync(pending.Snapshot, pending.Target, message =>
        {
            if (IsRejection(Text(message)))
                throw new BotWorkflowException("failed", "BotFather rejected the request.");
            return matches(message);
        }, expected);

    private static Choice? FindChoice(TdApi.Message prompt, string label, Func<string, bool> permitsText)
    {
        var markup = prompt.ReplyMarkup;
        if (markup is TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard inline)
        {
            var matching = inline.Rows.SelectMany(row => row)
                .Where(button => SameLabel(button.Text, label)).Take(2).ToArray();
            if (matching.Length > 1)
                throw new BotWorkflowException("failed", "BotFather offered multiple matching choices.");
            if (matching.Length == 1)
            {
                if (matching[0].Type is not TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeCallback { Data.Length: > 0 })
                    throw new BotWorkflowException("failed", "The BotFather choice is not an actionable inline callback.");
                return new Choice(label, matching[0]);
            }
            return null;
        }

        if (markup is TdApi.ReplyMarkup.ReplyMarkupShowKeyboard keyboard)
        {
            var matching = keyboard.Rows.SelectMany(row => row)
                .Where(button => SameLabel(button.Text, label)).Take(2).ToArray();
            if (matching.Length > 1)
                throw new BotWorkflowException("failed", "BotFather offered multiple matching choices.");
            if (matching.Length == 1 && matching[0].Type is TdApi.KeyboardButtonType.KeyboardButtonTypeText)
                return new Choice(matching[0].Text);
            return null;
        }

        return permitsText(Text(prompt)) ? new Choice(label) : null;
    }

    private static bool SameLabel(string button, string label) =>
        string.Equals(button, label, StringComparison.OrdinalIgnoreCase)
        || label.StartsWith('@') && string.Equals(button, label[1..], StringComparison.OrdinalIgnoreCase);

    private bool IsDeletePrompt(string text) =>
        Has(text, "delete") && (Has(text, "are you sure") || Has(text, "confirm"))
        && MentionsOnlyExpectedAccount(text);

    private static bool IsTextDeleteConfirmation(string text) =>
        Has(text, DeleteConfirmation) && (Has(text, "send") || Has(text, "enter") || Has(text, "type"));

    private static bool IsSelectionPrompt(string text, BotFatherOperation operation)
    {
        var asksToChoose = Has(text, "bot") && (Has(text, "choose") || Has(text, "select") || Has(text, "which"));
        if (!asksToChoose && !IsTextSelectionPrompt(text))
            return false;
        if (Has(text, "delete"))
            return operation == BotFatherOperation.Remove;
        if (Has(text, "revoke"))
            return operation == BotFatherOperation.Revoke;
        return !Has(text, "token") || operation == BotFatherOperation.Token;
    }

    private static bool IsTextSelectionPrompt(string text) =>
        Has(text, "username") && (Has(text, "send") || Has(text, "enter") || Has(text, "type"));

    private static bool IsRejection(string text) =>
        Has(text, "username is already taken") || Has(text, "username is taken") || Has(text, "username is invalid")
        || Has(text, "invalid username") || Has(text, "inaccessible") || Has(text, "you don't own")
        || Has(text, "you do not own") || Has(text, "bot not found") || Has(text, "you have no bots")
        || Has(text, "you don't have any bots") || Has(text, "too many bots") || Has(text, "unknown command");

    private static string? RecognizedToken(string text)
    {
        var token = BotConversation.ExtractToken(text);
        return token is not null && (text.Trim() == token
            || Has(text, "token to access the http api") || Has(text, "here is the token")
            || Has(text, "here is your token") || Has(text, "your new token is"))
            ? token : null;
    }

    private bool IsExpectedTokenAccount(string text) =>
        TokenAccount.Matches(text).All(match => SameLabel(match.Groups[1].Value, "@" + username));

    private bool MentionsOnlyExpectedAccount(string text) =>
        Mention.Matches(text).All(match => SameLabel(match.Groups[1].Value, "@" + username));

    private bool ContainsAccount(string text) =>
        Regex.IsMatch(text, $@"(?:t\.me/|@){Regex.Escape(username)}(?![a-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Text(TdApi.Message message) => MessageFiles.GetMessageText(message.Content);
    private static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private sealed record Choice(string Label, TdApi.InlineKeyboardButton? Button = null);
    private sealed record PendingReply(BotConversation.Snapshot Snapshot, BotConversation.ActionTarget Target);
}

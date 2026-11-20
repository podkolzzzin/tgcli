using System.Text.RegularExpressions;
using TdLib;

namespace TgCli;

internal enum BotFatherOperation { Create, Remove, Token, Revoke }

internal sealed class BotFatherFlow(BotConversation dialog, string username, Func<Task<long>> resolveTarget)
{
    private static readonly Regex AccountReference = new(
        @"(?:@|(?<![A-Za-z0-9_.])(?:www\.)?t\.me/)([A-Za-z0-9_]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DeleteAcknowledgement = new(
        @"\b(?:the\s+)?bot(?:\s+(?:@|(?:https?://)?t\.me/)(?<account>[A-Za-z0-9_]+))?\s+(?:has been|was|is)\s+(?:successfully\s+)?deleted\b",
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

    private readonly HashSet<long> _sentIds = [];
    private long? _firstSentId;

    internal async Task<string?> RunAsync(BotFatherOperation operation, string? name)
    {
        switch (operation)
        {
            case BotFatherOperation.Create:
                return await CreateAsync(name!);
            case BotFatherOperation.Remove:
                await resolveTarget();
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
            message => CreatePromptUsername.IsMatch(Text(message)), namePrompt);

        var created = false;
        string? token = null;
        await SendAndWaitAsync("submit_username", username, "BotFather's creation confirmation and token", message =>
        {
            var text = Text(message);
            if (!HasNoConflictingAccount(text))
                return false;
            if (Has(text, "congratulations on your new bot") && ContainsAccount(text))
                created = true;
            if (created && RecognizedToken(text) is { } received)
                token = received;
            return token is not null;
        }, usernamePrompt);

        // Resolve the newly created exact username before placing any credential on stdout.
        try
        {
            if (BotConversation.TokenBelongsTo(token!, await resolveTarget()))
                return token!;
        }
        catch (Exception error) when (BotConversation.IsTransportError(error) || error is InvalidOperationException)
        {
            throw new BotWorkflowException("outcome_unknown", "Could not verify the identity of the created bot.");
        }

        throw new BotWorkflowException("outcome_unknown", "The returned token does not match the created bot.");
    }

    private async Task RemoveAsync()
    {
        var selection = await SelectBotAsync("/deletebot", BotFatherOperation.Remove);
        var confirmation = await WaitAsync(selection, "BotFather's delete confirmation prompt", message =>
        {
            var text = Text(message);
            return IsDeletePrompt(text) && HasNoConflictingAccount(text)
                && (ContainsAccount(text) || dialog.IsActionBound(message, selection.Target))
                && FindChoice(message, DeleteConfirmation, IsTextDeleteConfirmation) is not null;
        });

        var confirmed = await ActivateAsync("confirm_delete", confirmation,
            FindChoice(confirmation, DeleteConfirmation, IsTextDeleteConfirmation)!);
        await WaitAsync(confirmed, "BotFather's deletion result", message =>
        {
            var text = Text(message);
            if (!HasNoConflictingAccount(text))
                return false;

            var match = DeleteAcknowledgement.Match(text);
            var account = match.Groups["account"];
            return account.Success && IsExpectedAccount(account.Value)
                || dialog.IsActionBound(message, confirmed.Target) && (match.Success || Has(text, "the bot is gone"));
        });
    }

    private async Task<string> GetTokenAsync(bool revoke)
    {
        var targetId = await resolveTarget();
        var selection = await SelectBotAsync(revoke ? "/revoke" : "/token",
            revoke ? BotFatherOperation.Revoke : BotFatherOperation.Token);
        string? token = null;
        var linkedRotation = false;
        await WaitAsync(selection, revoke ? "BotFather's rotation confirmation and new token" : "BotFather's token response", message =>
        {
            var text = Text(message);
            if (!HasNoConflictingAccount(text))
                return false;

            var received = RecognizedToken(text);
            if (received is not null && !BotConversation.TokenBelongsTo(received, targetId))
                return false;
            if (!revoke)
            {
                token = received;
                return token is not null;
            }

            var acknowledged = RotationAcknowledgement.IsMatch(text) && !RotationDenied.IsMatch(text);
            if (acknowledged && received is not null)
            {
                token = received;
                return true;
            }

            // Independent unthreaded acknowledgements and ordinary token replies do
            // not establish that a returned credential is the result of this rotation.
            if (dialog.IsActionBound(message, selection.Target))
            {
                linkedRotation |= acknowledged;
                if (linkedRotation)
                    token = received;
            }
            return token is not null;
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
        var snapshot = await CaptureAsync(prompt);
        if (choice.Button is not null)
        {
            await dialog.ClickAsync(step, prompt.Id, choice.Button);
            return new PendingReply(snapshot, new BotConversation.ActionTarget(prompt.Id, IsSend: false, prompt.Id));
        }

        var sentId = await SendAsync(step, choice.Label);
        return new PendingReply(snapshot, new BotConversation.ActionTarget(sentId, IsSend: true, prompt.Id));
    }

    private async Task<TdApi.Message> SendAndWaitAsync(
        string step, string input, string expected, Func<TdApi.Message, bool> matches, TdApi.Message? previousPrompt = null)
    {
        var snapshot = await CaptureAsync(previousPrompt);
        var sentId = await SendAsync(step, input);
        return await WaitAsync(new PendingReply(snapshot, new BotConversation.ActionTarget(sentId, true, previousPrompt?.Id ?? 0)),
            expected, matches);
    }

    private async Task<long> SendAsync(string step, string input)
    {
        var id = await dialog.SendAsync(step, input);
        _sentIds.Add(id);
        _firstSentId ??= id;
        return id;
    }

    private async Task<BotConversation.Snapshot> CaptureAsync(TdApi.Message? prompt)
    {
        var snapshot = await dialog.CaptureAsync();
        RequireExclusiveDialog(dialog.Recent);
        if (prompt is not null)
        {
            if (dialog.Recent.Any(message => message.Id > prompt.Id && dialog.IsFromBot(message)
                && IsConversationPrompt(Text(message))))
                throw ConflictingDialog();
            BotConversation.RequireUnchangedPrompt(snapshot, prompt);
        }
        return snapshot;
    }

    private Task<TdApi.Message> WaitAsync(PendingReply pending, string expected, Func<TdApi.Message, bool> matches) =>
        dialog.WaitForAsync(pending.Snapshot, pending.Target, message =>
        {
            var text = Text(message);
            if (IsRejection(text) && HasNoConflictingAccount(text)
                && (dialog.IsActionBound(message, pending.Target) || ContainsAccount(text)))
                throw new BotWorkflowException("failed", "BotFather rejected the request.");
            return matches(message);
        }, expected, history =>
        {
            RequireExclusiveDialog(history);
            if (history.Any(message => message.Id > Math.Max(pending.Target.MessageId, pending.Snapshot.NewestId)
                && dialog.IsFromBot(message) && IsDeletePrompt(Text(message)) && !HasNoConflictingAccount(Text(message))))
                throw ConflictingDialog();
        });

    private void RequireExclusiveDialog(IReadOnlyList<TdApi.Message> history)
    {
        if (_firstSentId is long start && history.Any(message =>
            message.Id > start && message.IsOutgoing && message.SendingState is null && !_sentIds.Contains(message.Id)))
            throw ConflictingDialog();
    }

    private static BotWorkflowException ConflictingDialog() => new(
        "outcome_unknown", "Another BotFather interaction was observed. Check the chat before retrying.");

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

    private static bool IsDeletePrompt(string text) =>
        Has(text, "delete") && (Has(text, "are you sure") || Has(text, "confirm"));

    private static bool IsConversationPrompt(string text) =>
        IsDeletePrompt(text) || CreatePromptName.IsMatch(text) || CreatePromptUsername.IsMatch(text)
        || IsSelectionPrompt(text, BotFatherOperation.Remove) || IsSelectionPrompt(text, BotFatherOperation.Revoke)
        || IsSelectionPrompt(text, BotFatherOperation.Token);

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

    private bool IsExpectedAccount(string account) =>
        string.Equals(username, account, StringComparison.OrdinalIgnoreCase);

    private bool HasNoConflictingAccount(string text) => AccountReference.Matches(text).All(match =>
    {
        var account = match.Groups[1].Value;
        return IsExpectedAccount(account) || SameLabel(account, "BotSupport") || SameLabel(account, "BotFather");
    });

    private bool ContainsAccount(string text) =>
        AccountReference.Matches(text).Any(match => IsExpectedAccount(match.Groups[1].Value));

    private static string Text(TdApi.Message message) => MessageFiles.GetMessageText(message.Content);
    private static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private sealed record Choice(string Label, TdApi.InlineKeyboardButton? Button = null);
    private sealed record PendingReply(BotConversation.Snapshot Snapshot, BotConversation.ActionTarget Target);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class BotCommandsTests
{
    private const long BotUserId = 81;
    private const long ChatId = 91;
    private const long ManagedUserId = 12345;
    private const long ManagedChatId = 95;
    private const string TokenBody = "12345:abcdefghijklmnopqrstu";

    [Theory]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("list")]
    [InlineData("token")]
    [InlineData("write")]
    public async Task InvalidFormatDoesNotOpenTransport(string command)
    {
        using var fixture = new Fixture();
        Func<Task> run = command switch
        {
            "create" => () => fixture.Commands.Create("Example", "example_bot", format: "yaml"),
            "remove" => () => fixture.Commands.Remove("example_bot", confirm: true, format: "yaml"),
            "list" => () => fixture.Commands.List(format: "yaml"),
            "token" => () => fixture.Commands.Token("example_bot", revoke: true, format: "yaml"),
            "write" => () => fixture.Commands.Write("example_bot", text: "/start", format: "yaml"),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        await Assert.ThrowsAsync<ArgumentException>(run);
        Assert.Equal(0, fixture.Opens);
        Assert.Empty(fixture.Transport.Writes);
    }

    [Theory]
    [InlineData("example_bot", "/start", "-1,0", 0, 10)]
    [InlineData("example_bot", "/start", "0,,1", 0, 10)]
    [InlineData("example_bot", "/start", "0,x", 0, 10)]
    [InlineData("example_bot", "/start", "99999999999,0", 0, 10)]
    [InlineData("example_bot", "/start", " ", 0, 10)]
    [InlineData("example_bot", "/start", null, 10, 10)]
    [InlineData("example_bot", "/start", null, 0, 0)]
    [InlineData("example_bot", "", null, 0, 10)]
    [InlineData("@@example_bot", "/start", null, 0, 10)]
    [InlineData("example bot", "/start", null, 0, 10)]
    public async Task InvalidWriteOptionsDoNotOpenTransport(string username, string text, string click, long messageId, int limit)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Commands.Write(username, text, click, messageId, limit));
        Assert.Equal(0, fixture.Opens);
    }

    [Fact]
    public async Task InvalidNameOrCreateUsernameAndMissingDeleteConfirmationDoNotOpenTransport()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Commands.Create(" ", "example_bot"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Commands.Create("/cancel", "example_bot"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Commands.Create("Example", "example_user"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Commands.Remove("example_bot"));
        Assert.Equal(0, fixture.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanIdentityIsRejectedBeforeAnyWrite(bool isBotFather)
    {
        using var fixture = new Fixture();
        fixture.Transport.User.Type = new TdApi.UserType.UserTypeRegular();
        if (isBotFather)
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Commands.Create("Example", "example_bot"));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Commands.Write("human_user", "/start", "0,0"));
        Assert.Empty(fixture.Transport.Writes);
    }

    [Fact]
    public async Task OwnedBotsAreListedFromTransport()
    {
        using var fixture = new Fixture();
        await fixture.Commands.List(format: "json");
        var bot = Assert.Single(fixture.Result["items"]);
        Assert.True(fixture.Result.Value<bool>("complete"));
        Assert.Equal("telegram", fixture.Result.Value<string>("source"));
        Assert.Equal(JTokenType.String, bot["user_id"].Type);
        Assert.Equal("example_bot", bot.Value<string>("username"));
        Assert.True(bot.Value<bool>("is_bot"));
    }

    [Fact]
    public async Task TransportThatFinishesOpeningAfterDeadlineCannotPrintBotList()
    {
        using var stdout = new StringWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptTransport { OnDispose = () => disposed.TrySetResult() };
        var commands = new BotCommands(async _ =>
        {
            started.SetResult();
            await release.Task;
            return transport;
        }, stdout, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));
        var run = CliExecution.RunAsync(_ => commands.List(format: "json"), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => run);
        }
        finally { release.TrySetResult(); }
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task BotHistoryUsesSchemaEightAndDecimalReceiptIdentifiers()
    {
        using var fixture = new Fixture();
        fixture.Transport.Messages.Add(Reply(9007199254740993, "Choose", Inline(Button("Run"))));
        await fixture.Commands.Write("example_bot", click: "Run", messageId: 9007199254740993);
        Assert.Equal(JTokenType.String, fixture.Result["chat_id"].Type);
        Assert.Equal(JTokenType.String, fixture.Result["click"]["message_id"].Type);
        var record = Assert.Single(fixture.Result["messages"]);
        Assert.Equal("8.0", record.Value<string>("schema_version"));
        Assert.Equal("9007199254740993", record.Value<string>("message_id"));
        Assert.Equal(JTokenType.String, record["message_id"].Type);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("noncallback")]
    [InlineData("outgoing")]
    [InlineData("wrongsender")]
    [InlineData("inaccessible")]
    public async Task ExplicitTargetIsValidatedBeforeSendingText(string target)
    {
        using var fixture = new Fixture();
        fixture.Transport.Messages.Add(target switch
        {
            "duplicate" => Reply(10, "Choose", Inline(Button("Run"), Button("Run"))),
            "noncallback" => Reply(10, "Choose", Inline(new TdApi.InlineKeyboardButton
            {
                Text = "Run",
                Type = new TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeUrl { Url = "https://example.test" }
            })),
            "outgoing" => Reply(10, "Choose", Inline(Button("Run")), outgoing: true),
            "wrongsender" => Reply(10, "Choose", Inline(Button("Run")), userId: 99),
            _ => Reply(9, "Choose", Inline(Button("Run")))
        });

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Commands.Write("example_bot", "/start", "Run", messageId: 10));
        Assert.Empty(fixture.Transport.Writes);
        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task ExplicitClickUsesTheSelectedCallbackAndAcknowledgesIt()
    {
        using var fixture = new Fixture();
        fixture.Transport.Messages.Add(Reply(10, "Choose", Inline(Button("Other", 5), Button("Run", 9))));
        fixture.Transport.Click = (id, data) =>
        {
            Assert.Equal(10, id);
            Assert.Equal(new byte[] { 9 }, data);
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = "Done" });
        };
        await fixture.Commands.Write("example_bot", click: "0,1", messageId: 10);
        Assert.Equal(new[] { "click:10" }, fixture.Transport.Writes);
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.Equal("Done", fixture.Result["click"].Value<string>("answer_text"));
    }

    [Fact]
    public async Task WriteWaitsForDelayedKeyboardAndUsesFinalSendId()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Messages.Add(Reply(10, "Old menu", Inline(Button("Run", 1))));
        var polls = 0;
        transport.History = () =>
        {
            if (transport.Writes.Count == 1 && ++polls == 1)
            {
                transport.Messages.Add(Reply(31, "Loading", Inline(Button("Cancel"))));
                transport.Messages.Add(Reply(32, "Wrong sender", Inline(Button("Run", 5)), userId: 99));
            }
            if (transport.Writes.Count == 1 && polls == 2)
                transport.Messages[1] = Reply(31, "Ready", Inline(Button("Run", 7)), editDate: 3);
            return transport.Messages;
        };
        transport.Send = _ =>
        {
            transport.SendSuccess(temporaryId: 7000, finalId: 30);
            return Task.FromResult(Pending(7000));
        };
        transport.Click = (id, data) =>
        {
            Assert.Equal(31, id);
            Assert.Equal(new byte[] { 7 }, data);
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = "Done" });
        };

        await fixture.Commands.Write("example_bot", "/start", "Run");
        Assert.Equal(new[] { "send:/start", "click:31" }, transport.Writes);
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.Equal(30, fixture.Result.Value<long>("sent_message_id"));
        Assert.Equal(7000, fixture.Result["receipts"][0].Value<long>("temporary_message_id"));
    }

    [Fact]
    public async Task NoFreshKeyboardNeverFallsBackToAnOldKeyboard()
    {
        using var fixture = new Fixture();
        fixture.Transport.Messages.Add(Reply(10, "Old menu", Inline(Button("Run"))));
        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Write("example_bot", "/start", "Run"));
        Assert.Equal(new[] { "send:/start" }, fixture.Transport.Writes);
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(100, fixture.Result.Value<long>("sent_message_id"));
    }

    [Fact]
    public async Task OutgoingMessageWithoutReplyIsReportedAsRequested()
    {
        using var fixture = new Fixture();
        await fixture.Commands.Write("example_bot", "/start");
        Assert.Equal("requested", fixture.Result.Value<string>("status"));
        Assert.Equal(100, fixture.Result.Value<long>("sent_message_id"));
        Assert.Equal(new[] { "send:/start" }, fixture.Transport.Writes);
    }

    [Fact]
    public async Task NoSendConfirmationRetainsTemporaryReceiptWithoutRetry()
    {
        using var fixture = new Fixture();
        fixture.Transport.Send = _ =>
        {
            fixture.Transport.SendSuccess(temporaryId: 2001, finalId: 500);
            return Task.FromResult(Pending(2000));
        };
        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Write("example_bot", "/start"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(JTokenType.Null, fixture.Result["sent_message_id"].Type);
        Assert.Equal(2000, fixture.Result["receipts"][0].Value<long>("temporary_message_id"));
        Assert.Equal(new[] { "send:/start" }, fixture.Transport.Writes);
    }

    [Fact]
    public async Task FailedSendUpdateProducesFailedOutcome()
    {
        using var fixture = new Fixture();
        fixture.Transport.Send = _ =>
        {
            fixture.Transport.SendFailed(temporaryId: 2000);
            return Task.FromResult(Pending(2000));
        };
        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Write("example_bot", "/start"));
        Assert.Equal("failed", fixture.Result.Value<string>("status"));
        Assert.Equal(2000, fixture.Result["receipts"][0].Value<long>("temporary_message_id"));
        Assert.Equal(new[] { "send:/start" }, fixture.Transport.Writes);
    }

    [Fact]
    public async Task CallbackTimeoutRetainsTargetWithoutRetry()
    {
        using var fixture = new Fixture();
        fixture.Transport.Messages.Add(Reply(10, "Choose", Inline(Button("Run"))));
        fixture.Transport.Click = (_, _) => Task.FromException<TdApi.CallbackQueryAnswer>(new TimeoutException("uncertain"));
        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Write("example_bot", click: "Run"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(10, fixture.Result["receipts"][0].Value<long>("message_id"));
        Assert.Equal(new[] { "click:10" }, fixture.Transport.Writes);
    }

    [Fact]
    public async Task CreateWaitsForMatchingPromptsUsesEditedReplyAndPreservesFullToken()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Messages.Add(Reply(12, "Choose a name for your bot."));
        transport.Send = text =>
        {
            switch (text)
            {
                case "/newbot":
                    transport.Messages.Add(Reply(101, "Choose a name for your bot."));
                    transport.Messages.Add(Reply(102, "Unrelated information."));
                    transport.SendSuccess(temporaryId: 9000, finalId: 100);
                    return Task.FromResult(Pending(9000));
                case "Example":
                    transport.Messages[1] = Reply(101, "Now let's choose a username for your bot.", editDate: 4);
                    return Task.FromResult(Outgoing(103));
                case "example_bot":
                    transport.Messages.Add(Reply(105, "Done! Congratulations on your new bot at t.me/example_bot."));
                    transport.Messages.Add(Reply(106, $"Use this token to access the HTTP API: ({TokenBody}-_)"));
                    return Task.FromResult(Outgoing(104));
                default:
                    throw new InvalidOperationException("Unexpected script send.");
            }
        };

        await fixture.Commands.Create("Example", "@example_bot");
        Assert.Equal(new[] { "send:/newbot", "send:Example", "send:example_bot" }, transport.Writes);
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.Equal(TokenBody + "-_", fixture.Result.Value<string>("token"));
        Assert.Equal(106, fixture.Result.Value<long>("message_id"));
    }

    [Fact]
    public async Task RemovalRequiresAnExplicitDeletePromptAndResult()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = text =>
        {
            Assert.Equal("/deletebot", text);
            transport.Messages.Add(Reply(101, "Choose a bot to delete:", Inline(Button("@example_bot", 5))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (id, data) =>
        {
            Assert.Equal(101, id);
            if (data.Single() == 5)
                transport.Messages[0] = Reply(id, "Are you sure you want to delete @example_bot?",
                    Inline(Button("Yes, I am totally sure.", 7)), editDate: 3);
            else
            {
                Assert.Equal(7, data.Single());
                transport.Messages.Add(Reply(102, "Done! The bot @example_bot was deleted."));
            }
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = string.Empty });
        };

        await fixture.Commands.Remove("example_bot", confirm: true);
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.Equal(new[] { "send:/deletebot", "click:101", "click:101" }, transport.Writes);
    }

    [Fact]
    public async Task RemovalWaitsForAnEditedConfirmationKeyboardBeforeClicking()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = text =>
        {
            Assert.Equal("/deletebot", text);
            transport.Messages.Add(Reply(101, "Choose a bot to delete:", Inline(Button("@example_bot", 5))));
            return Task.FromResult(Outgoing(100));
        };
        var reads = 0;
        transport.History = () =>
        {
            if (transport.Writes.Count == 2 && ++reads == 2)
                transport.Messages[0] = Reply(101, "Are you sure you want to delete @example_bot?",
                    Inline(Button("Yes, I am totally sure.", 7)), editDate: 4);
            return transport.Messages;
        };
        transport.Click = (id, data) =>
        {
            if (data.Single() == 5)
                transport.Messages[0] = Reply(id, "Are you sure you want to delete @example_bot?", editDate: 3);
            else
                transport.Messages.Add(Reply(102, "Done! The bot @example_bot was deleted."));
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = string.Empty });
        };

        await fixture.Commands.Remove("example_bot", confirm: true);
        Assert.Equal(new[] { "send:/deletebot", "click:101", "click:101" }, transport.Writes);
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
    }

    [Fact]
    public async Task AChangedBotFatherPromptIsNeverActivated()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to delete:", Inline(Button("@example_bot"))));
            return Task.FromResult(Outgoing(100));
        };
        var reads = 0;
        transport.History = () =>
        {
            if (transport.Writes.Count == 1 && ++reads == 2)
                transport.Messages[0] = Reply(101, "Choose a bot to delete:", Inline(Button("@other_bot")), editDate: 3);
            return transport.Messages;
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Remove("example_bot", confirm: true));
        Assert.Equal(new[] { "send:/deletebot" }, transport.Writes);
        Assert.Equal("failed", fixture.Result.Value<string>("status"));
    }

    [Fact]
    public void TokenParserAcceptsColonAsDelimitingPunctuation() =>
        Assert.Equal(TokenBody + "-", BotConversation.ExtractToken($"New token:{TokenBody}-."));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SupersededDeletePromptDoesNotSendAnUnboundConfirmation(bool observesOutgoingSelection, bool editedInPlace)
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        if (editedInPlace)
            transport.Messages.Add(Reply(99, "An old BotFather message."));
        transport.Send = text =>
        {
            if (text == "/deletebot")
                transport.Messages.Add(Reply(101, "Choose a bot to delete:", Inline(Button("@example_bot"))));
            else
                transport.Messages.Add(Reply(106, "The bot is gone."));
            return Task.FromResult(Outgoing(text == "/deletebot" ? 100 : 105));
        };
        transport.Click = (_, _) =>
        {
            transport.Messages.Add(Reply(102, "Are you sure you want to delete @example_bot? Type Yes, I am totally sure."));
            return Task.FromResult(new TdApi.CallbackQueryAnswer());
        };
        var reads = 0;
        transport.History = () =>
        {
            if (transport.Writes.Count == 2 && ++reads == 2)
            {
                if (observesOutgoingSelection)
                    transport.Messages.Add(Reply(103, "@other_bot", outgoing: true, userId: 1));
                const string otherPrompt = "Are you sure you want to delete @other_bot? Type Yes, I am totally sure.";
                if (editedInPlace)
                    transport.Messages[0] = Reply(99, otherPrompt, editDate: 3);
                else
                    transport.Messages.Add(Reply(104, otherPrompt));
            }
            return transport.Messages;
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Remove("example_bot", confirm: true));
        Assert.Equal(new[] { "send:/deletebot", "click:101" }, transport.Writes);
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousDeleteReceiptRequiresADirectLinkToTheAction(bool hasReplyLink)
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to delete:", Inline(Button("@example_bot", 5))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (_, data) =>
        {
            transport.Messages.Add(data.Single() == 5
                ? Reply(102, "Are you sure you want to delete @example_bot?", Inline(Button("Yes, I am totally sure.", 7)))
                : Reply(103, "The bot is gone.", replyToId: hasReplyLink ? 102 : null));
            return Task.FromResult(new TdApi.CallbackQueryAnswer());
        };

        if (hasReplyLink)
            await fixture.Commands.Remove("example_bot", confirm: true);
        else
            await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Remove("example_bot", confirm: true));
        Assert.Equal(hasReplyLink ? "confirmed" : "outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(new[] { "send:/deletebot", "click:101", "click:102" }, transport.Writes);
    }

    [Theory]
    [InlineData(" for t.me/other_bot", TokenBody, false)]
    [InlineData("", "67890:abcdefghijklmnopqrstu", false)]
    [InlineData("", TokenBody, true)]
    public async Task CreateDoesNotReturnTokensUnboundToItsCreationReceipt(string account, string token, bool tokenFirst)
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = text =>
        {
            var id = 100 * transport.Writes.Count;
            if (id < 300)
                transport.Messages.Add(Reply(id + 1, id == 100 ? "Choose a name for your bot." : "Choose a username for your bot."));
            else
            {
                transport.Messages.Add(Reply(tokenFirst ? 302 : 301, "Done! Congratulations on your new bot at t.me/example_bot."));
                transport.Messages.Add(Reply(tokenFirst ? 301 : 302, $"Use this token to access the HTTP API{account}: {token}."));
            }
            return Task.FromResult(Outgoing(id));
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Create("Example", "example_bot"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(JTokenType.Null, fixture.Result["token"].Type);
        Assert.DoesNotContain(token, fixture.Output.ToString());
    }

    [Fact]
    public async Task RevokeCannotCombineAnAcknowledgementWithAnUnthreadedOrdinaryTokenForTheSameBot()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to revoke its token:", Inline(Button("@example_bot"))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (_, _) =>
        {
            transport.Messages.Add(Reply(102, "Your old token was revoked for @example_bot."));
            transport.Messages.Add(Reply(103, $"Here is the token for @example_bot: {TokenBody}."));
            return Task.FromResult(new TdApi.CallbackQueryAnswer());
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Token("example_bot", revoke: true, format: "json"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(JTokenType.Null, fixture.Result["token"].Type);
        Assert.Equal(JTokenType.Null, fixture.Result["revoked"].Type);
    }

    [Fact]
    public async Task RevokeAcceptsAnExplicitRotationAndVerifiedNewTokenInOneResponse()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to revoke its token:", Inline(Button("@example_bot"))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (_, _) =>
        {
            transport.Messages.Add(Reply(102, $"Your old token was revoked. Your new token is: {TokenBody}-."));
            return Task.FromResult(new TdApi.CallbackQueryAnswer());
        };

        await fixture.Commands.Token("example_bot", revoke: true, format: "json");
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.Equal(TokenBody + "-", fixture.Result.Value<string>("token"));
        Assert.True(fixture.Result.Value<bool>("revoked"));
    }

    [Theory]
    [InlineData(" for t.me/other_bot", TokenBody)]
    [InlineData("", "67890:abcdefghijklmnopqrstu")]
    public async Task TokenFetchDoesNotReturnCredentialsForAnotherBot(string account, string token)
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to get its token:", Inline(Button("@example_bot"))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (_, _) =>
        {
            transport.Messages.Add(Reply(102, $"Here is the token{account}: {token}."));
            return Task.FromResult(new TdApi.CallbackQueryAnswer());
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Token("example_bot", format: "json"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(JTokenType.Null, fixture.Result["token"].Type);
        Assert.DoesNotContain(token, fixture.Output.ToString());
    }

    [Fact]
    public async Task DifferentActiveUsernameIsRejectedBeforeRevocation()
    {
        using var fixture = new Fixture();
        fixture.Transport.ManagedUser.Usernames.ActiveUsernames = ["unrelated_bot"];

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Commands.Token("example_bot", revoke: true));
        Assert.Empty(fixture.Transport.Writes);
    }

    [Fact]
    public async Task ConcurrentBotFatherCommandDoesNotTurnUnthreadedRejectionIntoFailure()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "/other", outgoing: true, userId: 1));
            transport.Messages.Add(Reply(102, "Unknown command."));
            transport.Messages.Add(Reply(103, "Choose a name for your bot."));
            return Task.FromResult(Outgoing(100));
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Create("Example", "example_bot"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(new[] { "send:/newbot" }, transport.Writes);
    }

    [Fact]
    public async Task TimeoutWithoutAnySendReceiptStillReportsUnknownAndDoesNotRetry()
    {
        using var fixture = new Fixture();
        fixture.Transport.Send = _ => Task.FromException<TdApi.Message>(new TimeoutException());
        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Write("example_bot", "/start"));
        Assert.Equal("outcome_unknown", fixture.Result.Value<string>("status"));
        Assert.Equal(JTokenType.Null, fixture.Result["sent_message_id"].Type);
        Assert.Null(fixture.Result["receipts"][0]["temporary_message_id"]);
        Assert.Equal(new[] { "send:/start" }, fixture.Transport.Writes);
    }

    [Fact]
    public async Task TokenSelectionWaitsForDelayedKeyboardOnEditedPrompt()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = text =>
        {
            Assert.Equal("/token", text);
            transport.Messages.Add(Reply(101, "Choose a bot to get its token:"));
            return Task.FromResult(Outgoing(100));
        };
        var polls = 0;
        transport.History = () =>
        {
            if (transport.Writes.Count == 1 && ++polls == 2)
                transport.Messages[0] = Reply(101, "Choose a bot to get its token:", Inline(Button("@example_bot")));
            return transport.Messages;
        };
        transport.Click = (id, _) =>
        {
            transport.Messages.Add(Reply(102, $"Here is the token for @example_bot: {TokenBody}-."));
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = string.Empty });
        };

        await fixture.Commands.Token("example_bot", format: "json");
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.Equal(TokenBody + "-", fixture.Result.Value<string>("token"));
        Assert.False(fixture.Result.Value<bool>("revoked"));
        Assert.Equal(new[] { "send:/token", "click:101" }, transport.Writes);
    }

    [Theory]
    [InlineData("This bot is inaccessible.", "failed")]
    [InlineData("Old token revoked. Your new token is:", "outcome_unknown")]
    [InlineData("Use this token to access the HTTP API:", "outcome_unknown")]
    [InlineData("Your token was not revoked. Your new token is:", "outcome_unknown")]
    [InlineData(null, "outcome_unknown")]
    public async Task RevokeWithoutMatchingConfirmationNeverClaimsRotation(string response, string expected)
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to revoke its token:", Inline(Button("@example_bot"))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (_, _) =>
        {
            if (response is not null)
            {
                var text = response.Contains("API:") || response.Contains("was not revoked") ? response + " " + TokenBody : response;
                transport.Messages.Add(Reply(103, text, replyToId: 101));
            }
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = string.Empty });
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Token("example_bot", revoke: true, format: "json"));
        Assert.Equal(expected, fixture.Result.Value<string>("status"));
        Assert.NotEqual(true, fixture.Result.Value<bool?>("revoked"));
        Assert.Equal(JTokenType.Null, fixture.Result["token"].Type);
        Assert.Equal(new[] { "send:/revoke", "click:101" }, transport.Writes);
    }

    [Fact]
    public async Task RevokeAcknowledgementAndNewTokenMayArriveInSeparateMessages()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = _ =>
        {
            transport.Messages.Add(Reply(101, "Choose a bot to revoke its token:", Inline(Button("@example_bot"))));
            return Task.FromResult(Outgoing(100));
        };
        transport.Click = (_, _) =>
        {
            transport.Messages.Add(Reply(102, "Your old token was revoked.", replyToId: 101));
            transport.Messages.Add(Reply(103, $"Use this token to access the HTTP API: {TokenBody}-", replyToId: 101));
            return Task.FromResult(new TdApi.CallbackQueryAnswer { Text = string.Empty });
        };
        await fixture.Commands.Token("example_bot", revoke: true, format: "json");
        Assert.Equal("confirmed", fixture.Result.Value<string>("status"));
        Assert.True(fixture.Result.Value<bool>("revoked"));
        Assert.Equal(TokenBody + "-", fixture.Result.Value<string>("token"));
    }

    [Fact]
    public async Task BotFatherRejectsUnavailableUsernameWithoutRepeatingMutation()
    {
        using var fixture = new Fixture();
        var transport = fixture.Transport;
        transport.Send = text =>
        {
            var id = 100 * transport.Writes.Count;
            transport.Messages.Add(Reply(id + 1, text switch
            {
                "/newbot" => "Choose a name for your bot.",
                "Example" => "Choose a username for your bot.",
                _ => "Sorry, this username is already taken. Please try something different."
            }, replyToId: id));
            return Task.FromResult(Outgoing(id));
        };

        await Assert.ThrowsAsync<BotWorkflowException>(() => fixture.Commands.Create("Example", "example_bot"));
        Assert.Equal("failed", fixture.Result.Value<string>("status"));
        Assert.Equal(301, fixture.Result.Value<long>("message_id"));
        Assert.Equal(new[] { "send:/newbot", "send:Example", "send:example_bot" }, transport.Writes);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("z")]
    [InlineData("9")]
    [InlineData("_")]
    [InlineData("-")]
    [InlineData("--_--")]
    public void TokenParserKeepsEveryAllowedTrailingCharacter(string suffix)
    {
        Assert.Equal(TokenBody + suffix, BotConversation.ExtractToken($"Your token is ({TokenBody}{suffix})."));
        Assert.Null(BotConversation.ExtractToken($"x{TokenBody}{suffix}"));
    }

    private static TdApi.Message Reply(long id, string text, TdApi.ReplyMarkup markup = null,
        int editDate = 0, bool outgoing = false, long userId = BotUserId, long? replyToId = null) => new()
    {
        Id = id,
        ChatId = ChatId,
        SenderId = new TdApi.MessageSender.MessageSenderUser { UserId = userId },
        Content = new TdApi.MessageContent.MessageText { Text = new TdApi.FormattedText { Text = text, Entities = [] } },
        ReplyMarkup = markup,
        ReplyTo = replyToId is long target
            ? new TdApi.MessageReplyTo.MessageReplyToMessage { ChatId = ChatId, MessageId = target }
            : null,
        Date = 2,
        EditDate = editDate,
        IsOutgoing = outgoing
    };

    private static TdApi.Message Outgoing(long id) => Reply(id, "", outgoing: true, userId: 1);
    private static TdApi.Message Pending(long id)
    {
        var message = Outgoing(id);
        message.SendingState = new TdApi.MessageSendingState.MessageSendingStatePending();
        return message;
    }

    private static TdApi.ReplyMarkup Inline(params TdApi.InlineKeyboardButton[] buttons) =>
        new TdApi.ReplyMarkup.ReplyMarkupInlineKeyboard { Rows = [buttons] };

    private static TdApi.InlineKeyboardButton Button(string text, byte data = 1) => new()
    {
        Text = text,
        Type = new TdApi.InlineKeyboardButtonType.InlineKeyboardButtonTypeCallback { Data = [data] }
    };

    private sealed class Fixture : IDisposable
    {
        internal readonly ScriptTransport Transport = new();
        internal readonly StringWriter Output = new();
        internal readonly BotCommands Commands;
        internal int Opens;

        internal Fixture()
        {
            Commands = new BotCommands(_ =>
            {
                Opens++;
                return Task.FromResult<IBotTransport>(Transport);
            }, Output, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1));
        }

        internal JObject Result => JObject.Parse(Output.ToString());
        public void Dispose() => Output.Dispose();
    }

    private sealed class ScriptTransport : IBotTransport
    {
        internal TdApi.User User = new()
        {
            Id = BotUserId,
            Type = new TdApi.UserType.UserTypeBot { CanBeEdited = true },
            Usernames = new TdApi.Usernames { ActiveUsernames = ["example_bot"] },
            FirstName = "Example"
        };
        internal List<TdApi.Message> Messages { get; } = [];
        internal List<string> Writes { get; } = [];
        internal Func<string, Task<TdApi.Message>> Send { get; set; } = _ => Task.FromResult(Outgoing(100));
        internal Func<long, byte[], Task<TdApi.CallbackQueryAnswer>> Click { get; set; } =
            (_, _) => Task.FromResult(new TdApi.CallbackQueryAnswer { Text = "" });
        internal Func<IReadOnlyList<TdApi.Message>> History { get; set; }

        public event Action<TdApi.Update> SendUpdated;

        internal void SendSuccess(long temporaryId, long finalId) =>
            SendUpdated?.Invoke(new TdApi.Update.UpdateMessageSendSucceeded { OldMessageId = temporaryId, Message = Outgoing(finalId) });

        internal void SendFailed(long temporaryId) =>
            SendUpdated?.Invoke(new TdApi.Update.UpdateMessageSendFailed
            {
                OldMessageId = temporaryId, Message = Outgoing(500), Error = new TdApi.Error { Code = 400, Message = "Rejected" }
            });

        private bool _botFatherDialog;
        internal TdApi.User ManagedUser = new()
        {
            Id = ManagedUserId,
            Type = new TdApi.UserType.UserTypeBot { CanBeEdited = true },
            Usernames = new TdApi.Usernames { ActiveUsernames = ["example_bot"] },
            FirstName = "Managed"
        };
        public Task<TdApi.Chat> SearchAsync(string username)
        {
            var managed = _botFatherDialog && username != "BotFather";
            _botFatherDialog |= username == "BotFather";
            return Task.FromResult(new TdApi.Chat
            {
                Id = managed ? ManagedChatId : ChatId,
                Type = new TdApi.ChatType.ChatTypePrivate { UserId = managed ? ManagedUser.Id : User.Id }
            });
        }

        public Task<TdApi.User> UserAsync(long userId) => Task.FromResult(userId == ManagedUser.Id ? ManagedUser : User);
        public Task<TdApi.UserFullInfo> UserFullInfoAsync(long userId) => Task.FromResult(new TdApi.UserFullInfo());
        public Task<long[]> OwnedBotsAsync() => Task.FromResult(new[] { User.Id });
        public Task<TdApi.Message> MessageAsync(long chatId, long messageId) =>
            Task.FromResult(Messages.SingleOrDefault(x => x.Id == messageId));

        public Task<IReadOnlyList<TdApi.Message>> HistoryAsync(long chatId, int limit) =>
            Task.FromResult<IReadOnlyList<TdApi.Message>>(
                (History?.Invoke() ?? Messages).OrderByDescending(x => x.Id).Take(limit).ToArray());

        public Task<TdApi.Message> SendAsync(long chatId, string text)
        {
            Writes.Add("send:" + text);
            return Send(text);
        }

        public Task<TdApi.CallbackQueryAnswer> ClickAsync(long chatId, long messageId, byte[] data)
        {
            Writes.Add("click:" + messageId);
            return Click(messageId, data);
        }

        internal Action OnDispose { get; init; }
        public ValueTask DisposeAsync()
        {
            OnDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}

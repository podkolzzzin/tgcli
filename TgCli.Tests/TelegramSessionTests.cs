using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class TelegramSessionTests
{
    [Fact]
    public void DescribesCodeSentToAnotherTelegramSession()
    {
        var description = TelegramSession.DescribeCodeDelivery(
            new TdApi.AuthenticationCodeType.AuthenticationCodeTypeTelegramMessage { Length = 5 },
            "+380000000000");

        Assert.Equal("in the Telegram service chat on another logged-in device", description);
    }

    [Fact]
    public void DescribesSmsFallbackWithPhoneNumber()
    {
        var description = TelegramSession.DescribeCodeDelivery(
            new TdApi.AuthenticationCodeType.AuthenticationCodeTypeSms { Length = 5 },
            "+380000000000");

        Assert.Equal("by SMS to +380000000000", description);
    }

    [Fact]
    public void DescribesMissedCallCodeDigits()
    {
        var description = TelegramSession.DescribeCodeDelivery(
            new TdApi.AuthenticationCodeType.AuthenticationCodeTypeMissedCall
            {
                PhoneNumberPrefix = "+380",
                Length = 4
            },
            "+380000000000");

        Assert.Contains("last 4 digits", description);
    }
}

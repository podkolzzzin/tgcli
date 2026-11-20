using System.ComponentModel.DataAnnotations;
using System.Globalization;
using TdLib;

namespace TgCli;

internal enum TelegramAuthorizationFailure
{
    LoginRequired,
    Revoked,
    Closed
}

internal sealed class TelegramAuthorizationException(
    TelegramAuthorizationFailure reason, string message, Exception? innerException = null)
    : ValidationException(message, innerException)
{
    internal TelegramAuthorizationFailure Reason { get; } = reason;

    internal static TelegramAuthorizationException FromNative(TdException error) =>
        new(TelegramAuthorizationFailure.Revoked,
            "Telegram authorization has expired or been revoked. Run `tgcli login` first.", error);
}

internal enum TelegramRpcFailureKind
{
    Canceled,
    Timeout,
    Authorization,
    FloodWait,
    TransientRead,
    Permanent
}

internal readonly record struct TelegramRpcFailure(TelegramRpcFailureKind Kind, TimeSpan? RetryAfter = null);

internal static class TelegramRpcPolicy
{
    internal static TelegramRpcFailure Classify(Exception error) => error switch
    {
        OperationCanceledException => new(TelegramRpcFailureKind.Canceled),
        TimeoutException => new(TelegramRpcFailureKind.Timeout),
        TelegramAuthorizationException => new(TelegramRpcFailureKind.Authorization),
        TdException td => Classify(td.Error),
        _ => new(TelegramRpcFailureKind.Permanent)
    };

    private static TelegramRpcFailure Classify(TdApi.Error error)
    {
        var message = error.Message?.Trim() ?? string.Empty;
        if (error.Code == 401 || message is "AUTH_KEY_UNREGISTERED" or "AUTH_KEY_INVALID"
            or "AUTH_KEY_DUPLICATED" or "SESSION_REVOKED" or "SESSION_EXPIRED" or "USER_DEACTIVATED")
            return new(TelegramRpcFailureKind.Authorization);
        if (error.Code == 429 || message.StartsWith("FLOOD_WAIT", StringComparison.Ordinal))
            return new(TelegramRpcFailureKind.FloodWait, GetRetryAfter(message));
        if (error.Code is 408 or 500 or 502 or 503 or 504)
            return new(TelegramRpcFailureKind.TransientRead);
        return new(TelegramRpcFailureKind.Permanent);
    }

    private static TimeSpan? GetRetryAfter(string message)
    {
        const string floodWait = "FLOOD_WAIT_";
        const string retryAfter = "retry after ";
        var phrase = message.LastIndexOf(retryAfter, StringComparison.OrdinalIgnoreCase);
        var seconds = message.StartsWith(floodWait, StringComparison.Ordinal)
            ? message.AsSpan(floodWait.Length)
            : phrase >= 0 ? message.AsSpan(phrase + retryAfter.Length) : [];
        var length = 0;
        while (length < seconds.Length && char.IsAsciiDigit(seconds[length])) length++;
        return int.TryParse(seconds[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? TimeSpan.FromSeconds(value) : null;
    }
}

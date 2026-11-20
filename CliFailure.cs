using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ConsoleAppFramework;
using TdLib;

namespace TgCli;

internal sealed class CliUsageException(string message, Exception? innerException = null) : Exception(message, innerException);

internal sealed class PartialResultException(string message) : Exception(message);

internal readonly record struct CliFailure(int ExitCode, string? Message)
{
    internal static CliFailure FromException(Exception error)
    {
        while (true)
        {
            if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
            else if (error is AggregateException { InnerExceptions.Count: 1 } aggregate) error = aggregate.InnerExceptions[0];
            else break;
        }

        return error switch
        {
            CliClosedOutputException => new(130, null),
            OperationCanceledException => new(130, "Operation canceled."),
            TimeoutException => new(5, BoundedMessage(error.Message)),
            TelegramAuthorizationException => new(4, BoundedMessage(error.Message)),
            TdException native when TelegramRpcPolicy.Classify(native).Kind == TelegramRpcFailureKind.Authorization =>
                new(4, TelegramAuthorizationException.FromNative(native).Message),
            PartialResultException or BotWorkflowException { Status: "outcome_unknown" } => new(3, BoundedMessage(error.Message)),
            CliUsageException or ArgumentParseFailedException or ArgumentException or FormatException or ValidationException =>
                new(2, BoundedMessage(error.Message)),
            _ => new(1, BoundedMessage(string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message))
        };
    }

    internal static string BoundedMessage(string message)
    {
        const int maxLength = 1024;
        var singleLine = message.AsSpan(0, Math.Min(message.Length, maxLength));
        var end = singleLine.IndexOfAny('\r', '\n');
        if (end >= 0) singleLine = singleLine[..end];
        var chars = singleLine.ToArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i])) chars[i] = ' ';
        return new string(chars).Trim() + (end < 0 && message.Length > maxLength ? "…" : "");
    }
}

internal sealed class CliClosedOutputException(IOException innerException) : IOException("The output pipe was closed.", innerException);

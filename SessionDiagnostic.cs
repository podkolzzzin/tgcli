using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed record SessionDiagnosticResult(JObject Data, bool Healthy, Exception? Error);

internal static class SessionDiagnostic
{
    internal static async Task<SessionDiagnosticResult> ProbeAsync(string? sessionDirectory, int lockTimeout, bool noWait,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        SessionDiagnosticResult result;
        try
        {
            if (!Directory.Exists(root)) throw LoginRequired();
            var initialLock = SessionLock.Inspect(root);
            if (initialLock.locked && (noWait || lockTimeout == 0))
                throw new IOException("The TDLib session is locked by another process.");

            result = await OperationDeadline.RunAsync(async token =>
            {
                await using var session = await TelegramSession.CreateAsync(root, apiId: 0, apiHash: null,
                    saveConfig: false, lockTimeout, noWait, cancellationToken: token);
                return await InspectAsync(session, token, initialLock);
            }, timeout ?? TimeSpan.FromSeconds(60), cancellationToken, "Session diagnostics");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            result = Failure(root, error);
        }
        result.Data["latency_ms"] = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    internal static Task<SessionDiagnosticResult> InspectAsync(TelegramSession session, CancellationToken cancellationToken) =>
        InspectAsync(session, cancellationToken, TryInspectLock(session.SessionDirectory));

    private static async Task<SessionDiagnosticResult> InspectAsync(TelegramSession session,
        CancellationToken cancellationToken, SessionLockStatus? lockStatus)
    {
        var timer = Stopwatch.StartNew();
        var data = DescribeStorage(session.SessionDirectory, lockStatus);
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.CheckActive();
            if (!session.IsAuthorized && session.AuthorizationState != "Unknown") throw LoginRequired();
            if (session.IsAuthorized)
            {
                try
                {
                    var me = await TelegramOperations.AwaitReadAsync(session.Client.GetMeAsync(), cancellationToken,
                        retainNativeOperation: true);
                    data["authenticated_account"] = new JObject
                    {
                        ["user_id"] = Wire.Id(me.Id),
                        ["display_name"] = $"{me.FirstName} {me.LastName}".Trim(),
                        ["username"] = me.Usernames?.ActiveUsernames?.FirstOrDefault()
                    };
                    var version = await TelegramOperations.AwaitReadAsync(session.Client.GetOptionAsync("version"), cancellationToken,
                        retainNativeOperation: true);
                    data["tdlib_version"] = (version as TdApi.OptionValue.OptionValueString)?.Value;
                }
                catch (TdException error) when (error.Error.Code is 400 or 403 or 404)
                {
                    // Optional cached account/version details are unavailable.
                }
            }
            session.CheckActive();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failure = error;
        }

        cancellationToken.ThrowIfCancellationRequested();
        data["latency_ms"] = timer.Elapsed.TotalMilliseconds;
        data["authorization_state"] = session.AuthorizationState;
        data["authorized"] = session.AuthorizationState == "Unknown" ? null : session.IsAuthorized;
        data["connection_state"] = session.ConnectionState;
        data["network_available"] = session.ConnectionState == "Unknown" ? null : session.IsConnected;
        if (failure is not null) return Unhealthy(data, failure);
        if (!session.IsAuthorized)
            return Unhealthy(data, new IOException("TDLib authorization state is not yet known."), "authorization_unknown");
        if (!session.IsConnected)
            return Unhealthy(data, new IOException($"Telegram is not synchronized (state: {session.ConnectionState})."), "connection_unavailable");
        data["healthy"] = true;
        return new(data, true, null);
    }

    internal static SessionDiagnosticResult Failure(string? sessionDirectory, Exception error)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        var lockStatus = TryInspectLock(root);
        var data = DescribeStorage(root, lockStatus);
        return Unhealthy(data, error, lockStatus?.locked == true && error is IOException ? "session_locked" : null);
    }

    private static JObject DescribeStorage(string root, SessionLockStatus? lockStatus)
    {
        var database = Path.Combine(root, "tdlib-db");
        var exists = Directory.Exists(database);
        return new JObject
        {
            ["healthy"] = false,
            ["checked_at"] = Wire.Timestamp(DateTimeOffset.UtcNow),
            ["latency_ms"] = JValue.CreateNull(),
            ["authorized"] = JValue.CreateNull(),
            ["authorization_state"] = "Unknown",
            ["connection_state"] = "Unknown",
            ["network_available"] = JValue.CreateNull(),
            ["session_directory"] = root,
            ["database_directory"] = database,
            ["database_exists"] = exists,
            ["database_size_bytes"] = exists ? JValue.CreateNull() : new JValue(0),
            ["tdlib_version"] = JValue.CreateNull(),
            ["authenticated_account"] = JValue.CreateNull(),
            ["lock_status"] = lockStatus is null ? JValue.CreateNull() : JObject.FromObject(lockStatus),
            ["error_code"] = JValue.CreateNull(),
            ["diagnostic_message"] = JValue.CreateNull()
        };
    }

    private static SessionDiagnosticResult Unhealthy(JObject data, Exception error, string? errorCode = null)
    {
        if (error is TdException native && TelegramRpcPolicy.Classify(native).Kind == TelegramRpcFailureKind.Authorization)
            error = TelegramAuthorizationException.FromNative(native);
        var (code, safeError) = error switch
        {
            TelegramAuthorizationException authorization => ("login_required", (Exception)authorization),
            TimeoutException => ("request_timeout", new TimeoutException("Timed out observing TDLib session state.")),
            OperationCanceledException => ("cancelled", new OperationCanceledException("Session diagnostic canceled.")),
            _ when errorCode is not null && error is IOException => (errorCode, error),
            _ => ("session_unavailable", new IOException("TDLib session state is unavailable."))
        };
        data["error_code"] = code;
        data["diagnostic_message"] = safeError.Message;
        return new(data, false, safeError);
    }

    private static SessionLockStatus? TryInspectLock(string root)
    {
        try { return SessionLock.Inspect(root); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static TelegramAuthorizationException LoginRequired() =>
        new(TelegramAuthorizationFailure.LoginRequired, "Telegram session is not authorized. Run `tgcli login` first.");
}

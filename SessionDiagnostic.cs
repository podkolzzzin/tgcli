using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed record SessionDiagnosticResult(JObject Data, bool Healthy, Exception? Error);
internal sealed record SessionDiagnosticObservation(string Root, bool? SessionDirectoryExists, bool? DatabaseExists, SessionLockStatus? LockStatus);

internal static class SessionDiagnostic
{
    internal static async Task<SessionDiagnosticResult> ProbeAsync(string? sessionDirectory, int lockTimeout, bool noWait,
        CancellationToken cancellationToken, Action<SessionDiagnosticObservation>? observe = null)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        SessionDiagnosticResult result;
        try
        {
            var observation = ObserveStorage(root);
            observe?.Invoke(observation);
            if (observation.SessionDirectoryExists == false) throw LoginRequired();
            if (observation.LockStatus?.locked == true && (noWait || lockTimeout == 0))
                throw new IOException("The TDLib session is locked by another process.");

            await using var session = await TelegramSession.CreateAsync(root, apiId: 0, apiHash: null,
                saveConfig: false, lockTimeout, noWait, cancellationToken);
            observation = observation with { DatabaseExists = Directory.Exists(session.DatabaseDirectory) };
            observe?.Invoke(observation);
            await ObserveStartupAsync(session, cancellationToken);
            result = await InspectAsync(session, cancellationToken, observation);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            result = Failure(root, error);
        }
        result.Data["latency_ms"] = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    private static async Task ObserveStartupAsync(TelegramSession session, CancellationToken cancellationToken)
    {
        if (session.IsLoginRequired || session.IsAuthorized && session.IsConnected) return;
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(TimeSpan.FromMilliseconds(650));
        try { await session.EnsureConnectedAsync(grace.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (TelegramAuthorizationException) { }
        catch (TimeoutException) { }
    }

    internal static Task<SessionDiagnosticResult> InspectAsync(TelegramSession session, CancellationToken cancellationToken) =>
        InspectAsync(session, cancellationToken, ObserveStorage(session.SessionDirectory));

    private static async Task<SessionDiagnosticResult> InspectAsync(TelegramSession session,
        CancellationToken cancellationToken, SessionDiagnosticObservation observation)
    {
        var timer = Stopwatch.StartNew();
        var data = DescribeStorage(observation);
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.CheckActive();
            if (session.IsLoginRequired) throw LoginRequired();
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
        var authorized = session.IsAuthorized;
        var connection = session.ConnectionState;
        var connected = connection == "ConnectionStateReady";
        data["authorization_state"] = session.AuthorizationState;
        data["authorized"] = authorized || session.IsLoginRequired || failure is TelegramAuthorizationException ? authorized : null;
        data["connection_state"] = connection;
        data["network_available"] = connection == "Unknown" ? null : connected;
        if (failure is not null) return Unhealthy(data, failure);
        if (!authorized)
            return Unhealthy(data, new IOException("TDLib authorization state is not yet known."), "authorization_unknown");
        if (!connected)
            return Unhealthy(data, new IOException($"Telegram is not synchronized (state: {connection})."), "connection_unavailable");
        data["healthy"] = true;
        return new(data, true, null);
    }

    internal static SessionDiagnosticResult Failure(string? sessionDirectory, Exception error)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        var observation = ObserveStorage(root);
        var data = DescribeStorage(observation);
        return Unhealthy(data, error, observation.LockStatus?.locked == true && error is IOException ? "session_locked" : null);
    }

    internal static SessionDiagnosticObservation ObserveStorage(string root) =>
        new(root, Directory.Exists(root), Directory.Exists(Path.Combine(root, "tdlib-db")), TryInspectLock(root));

    internal static JObject Deadline(SessionDiagnosticObservation observation, TimeSpan elapsed)
    {
        var result = Unhealthy(DescribeStorage(observation), new TimeoutException());
        result.Data["latency_ms"] = elapsed.TotalMilliseconds;
        return result.Data;
    }

    private static JObject DescribeStorage(SessionDiagnosticObservation observation)
    {
        var database = Path.Combine(observation.Root, "tdlib-db");
        return new JObject
        {
            ["healthy"] = false,
            ["checked_at"] = Wire.Timestamp(DateTimeOffset.UtcNow),
            ["latency_ms"] = JValue.CreateNull(),
            ["authorized"] = JValue.CreateNull(),
            ["authorization_state"] = "Unknown",
            ["connection_state"] = "Unknown",
            ["network_available"] = JValue.CreateNull(),
            ["session_directory"] = observation.Root,
            ["database_directory"] = database,
            ["database_exists"] = observation.DatabaseExists,
            ["database_size_bytes"] = observation.DatabaseExists == false ? new JValue(0) : JValue.CreateNull(),
            ["tdlib_version"] = JValue.CreateNull(),
            ["authenticated_account"] = JValue.CreateNull(),
            ["lock_status"] = observation.LockStatus is { } lockStatus ? JObject.FromObject(lockStatus) : JValue.CreateNull(),
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

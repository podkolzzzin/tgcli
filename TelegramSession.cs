using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Text.Json;
using TdLib;

namespace TgCli;

internal sealed class TelegramSession : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LoginInputTimeout = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentBag<TelegramSession> FailedNativeShutdowns = new();

    private readonly ITelegramSessionNative _native;
    private readonly SessionConfig _config;
    private readonly SessionLockLease _lease;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _authorizationLost = new();
    private readonly TelegramConnection _connection = new();
    private TdApi.AuthorizationState? _authorizationState;
    private TaskCompletionSource _authorizationChanged = NewAuthorizationSignal();
    private TelegramAuthorizationFailure? _terminalReason;
    private bool _wasAuthorized;
    private Task? _disposeTask;

    private TelegramSession(ITelegramSessionNative native, SessionConfig config, SessionLockLease lease,
        TimeSpan shutdownTimeout)
    {
        _native = native;
        _config = config;
        _lease = lease;
        _shutdownTimeout = shutdownTimeout;
        _native.UpdateReceived += OnUpdateReceived;
    }

    public TdApi.IClient Client => _native.Client ?? throw new InvalidOperationException("No TDLib client is available.");
    public string SessionDirectory => _config.RootDirectory;
    public string DatabaseDirectory => _config.DatabaseDirectory;
    public string FilesDirectory => _config.FilesDirectory;
    public bool IsConnected => _connection.IsReady;
    public string ConnectionState => _connection.StateName;
    public bool IsAuthorized
    {
        get
        {
            lock (_stateGate)
                return _terminalReason is null && _authorizationState is TdApi.AuthorizationState.AuthorizationStateReady;
        }
    }
    internal bool IsLoginRequired
    {
        get { lock (_stateGate) return NeedsLogin(_authorizationState); }
    }
    public string AuthorizationState
    {
        get { lock (_stateGate) return _authorizationState?.GetType().Name ?? "Unknown"; }
    }

    internal static string ResolveSessionDirectory(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? SessionConfig.DefaultRootDirectory() : path);

    public static Task<TelegramSession> CreateAsync(string? sessionDirectory, int apiId, string? apiHash, bool saveConfig,
        int lockTimeout = 30, bool noWait = false, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(sessionDirectory, apiId, apiHash, saveConfig, lockTimeout, noWait, TelegramSessionNative.Open,
            StartupTimeout, ShutdownTimeout, cancellationToken);

    internal static Task<TelegramSession> CreateCoreAsync(string? sessionDirectory, int apiId, string? apiHash, bool saveConfig,
        int lockTimeout, bool noWait, Func<ITelegramSessionNative> openNative, TimeSpan startupTimeout, TimeSpan shutdownTimeout,
        CancellationToken cancellationToken) =>
        OperationDeadline.RunAsync(ct => Task.Run(() => StartAsync(sessionDirectory, apiId, apiHash, saveConfig,
            lockTimeout, noWait, openNative, shutdownTimeout, ct), ct), startupTimeout, cancellationToken, "TDLib startup",
            onAbandoned: static session => session.DisposeWhenDrainedAsync());

    public static Task<TelegramSession> CreateReadyAsync(string? sessionDirectory, int lockTimeout = 30, bool noWait = false,
        bool local = false, CancellationToken cancellationToken = default) =>
        OperationDeadline.RunAsync(async ct =>
        {
            var session = await CreateAsync(sessionDirectory, apiId: 0, apiHash: null, saveConfig: false,
                lockTimeout, noWait, ct);
            try
            {
                if (local) await session.EnsureReadyAsync(ct);
                else await session.EnsureConnectedAsync(ct);
                return session;
            }
            catch
            {
                OperationDeadline.Retain(session.DisposeWhenDrainedAsync());
                throw;
            }
        }, StartupTimeout, cancellationToken, "Telegram session readiness",
            onAbandoned: static session => session.DisposeWhenDrainedAsync());

    private static async Task<TelegramSession> StartAsync(string? sessionDirectory, int apiId, string? apiHash, bool saveConfig,
        int lockTimeout, bool noWait, Func<ITelegramSessionNative> openNative, TimeSpan shutdownTimeout, CancellationToken ct)
    {
        var root = ResolveSessionDirectory(sessionDirectory);
        ct.ThrowIfCancellationRequested();
        SessionConfig.CreatePrivateDirectory(root);
        var lease = await SessionLock.AcquireAsync(root, TimeSpan.FromSeconds(noWait ? 0 : Math.Max(0, lockTimeout)), ct);
        ITelegramSessionNative? native = null;
        TelegramSession? session = null;
        try
        {
            SessionSecret.RecoverImport(root);
            var config = SessionConfig.Load(root);
            config.ApplyOverrides(apiId, apiHash);
            if (!config.HasCredentials)
                throw new TelegramAuthorizationException(TelegramAuthorizationFailure.LoginRequired,
                    "Telegram API credentials are missing. Run `tgcli login` with --api-id and --api-hash or set TGCLI_API_ID/TGCLI_API_HASH.");
            ct.ThrowIfCancellationRequested();
            config.EnsureDirectories();
            if (saveConfig) config.Save();

            ct.ThrowIfCancellationRequested();
            native = openNative();
            session = new TelegramSession(native, config, lease, shutdownTimeout);
            await session.InitializeAsync(ct);
            ct.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            if (session is not null)
                OperationDeadline.Retain(session.DisposeWhenDrainedAsync());
            else
            {
                // The client owns no database until TDLib parameters have been applied.
                try { native?.Dispose(); }
                finally { lease.Dispose(); }
            }
            throw;
        }
    }

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
        OperationDeadline.RunAsync(WaitForAuthorizationAsync, AuthorizationTimeout, cancellationToken, "Telegram authorization");

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        using var connectionWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authorizationLost.Token);
        try
        {
            await _connection.WaitReadyAsync(ConnectionTimeout, connectionWait.Token);
            cancellationToken.ThrowIfCancellationRequested();
            CheckActive();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CheckActive();
            throw;
        }
    }

    private async Task WaitForAuthorizationAsync(CancellationToken token)
    {
        await RefreshAuthorizationAsync(token);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            AuthorizationSnapshot state;
            lock (_stateGate)
            {
                ThrowIfSessionUnusable();
                state = ReadAuthorization();
                if (state.State is TdApi.AuthorizationState.AuthorizationStateReady) return;
                if (NeedsLogin(state.State))
                    throw new TelegramAuthorizationException(TelegramAuthorizationFailure.LoginRequired,
                        "Telegram session is not authorized. Run `tgcli login` first.");
            }
            await state.Changed.WaitAsync(token);
        }
    }

    private static bool NeedsLogin(TdApi.AuthorizationState? state) => state is
        TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber
        or TdApi.AuthorizationState.AuthorizationStateWaitCode
        or TdApi.AuthorizationState.AuthorizationStateWaitPassword
        or TdApi.AuthorizationState.AuthorizationStateWaitEmailAddress
        or TdApi.AuthorizationState.AuthorizationStateWaitEmailCode
        or TdApi.AuthorizationState.AuthorizationStateWaitRegistration
        or TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation
        or TdApi.AuthorizationState.AuthorizationStateWaitPremiumPurchase;

    public async Task LoginAsync(string? phone, CancellationToken cancellationToken = default)
    {
        await OperationDeadline.RunAsync(async ct => await RefreshAuthorizationAsync(ct),
            AuthorizationTimeout, cancellationToken, "Reading Telegram login state");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthorizationSnapshot state;
            lock (_stateGate)
            {
                ThrowIfSessionUnusable();
                state = ReadAuthorization();
            }

            switch (state.State)
            {
                case TdApi.AuthorizationState.AuthorizationStateReady:
                    cancellationToken.ThrowIfCancellationRequested();
                    Console.WriteLine("Logged in.");
                    return;

                case TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber:
                    phone ??= Prompt("Phone");
                    await RunLoginRequest(() => Client.SetAuthenticationPhoneNumberAsync(phone, settings: null!), cancellationToken);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitCode waitCode:
                    await HandleAuthenticationCodeAsync(waitCode.CodeInfo, cancellationToken);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitPassword waitPassword:
                    var passwordPrompt = string.IsNullOrWhiteSpace(waitPassword.PasswordHint)
                        ? "Password"
                        : $"Password (hint: {waitPassword.PasswordHint})";
                    var password = PromptSecret(passwordPrompt);
                    await RunLoginRequest(() => Client.CheckAuthenticationPasswordAsync(password), cancellationToken);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitEmailAddress:
                    var email = Prompt("Email");
                    await RunLoginRequest(() => Client.SetAuthenticationEmailAddressAsync(email), cancellationToken);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitEmailCode:
                    var code = Prompt("Email code");
                    await RunLoginRequest(() => Client.CheckAuthenticationEmailCodeAsync(
                        new TdApi.EmailAddressAuthentication.EmailAddressAuthenticationCode { Code = code }), cancellationToken);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitRegistration:
                    var firstName = Prompt("First name");
                    var lastName = Prompt("Last name", allowEmpty: true);
                    await RunLoginRequest(() => Client.RegisterUserAsync(firstName, lastName, disableNotification: true),
                        cancellationToken);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation confirmation:
                    Console.WriteLine("Confirm login on another Telegram client:");
                    Console.WriteLine(confirmation.Link);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitPremiumPurchase:
                    throw new TelegramAuthorizationException(TelegramAuthorizationFailure.LoginRequired,
                        "Telegram requires Premium purchase for this login; tgcli can't complete that flow.");
            }

            await state.Changed.WaitAsync(LoginInputTimeout, cancellationToken);
        }
    }

    private Task RunLoginRequest(Func<Task<TdApi.Ok>> request, CancellationToken cancellationToken) =>
        OperationDeadline.RunAsync(ct => RunNativeAsync(request, ct), AuthorizationTimeout,
            cancellationToken, "Telegram login request");

    public async ValueTask DisposeAsync()
    {
        var shutdown = DisposeWhenDrainedAsync();
        try { await shutdown.WaitAsync(_shutdownTimeout); }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("TDLib shutdown is still in progress. Its session lock remains held until shutdown or process exit.");
            OperationDeadline.Observe(shutdown);
        }
    }

    internal Task DisposeWhenDrainedAsync()
    {
        lock (_stateGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            _disposeTask = Task.Run(CloseAndReleaseAsync);
            _authorizationChanged.TrySetResult();
            OperationDeadline.Observe(_authorizationLost.CancelAsync());
            return _disposeTask;
        }
    }

    private async Task CloseAndReleaseAsync()
    {
        await _nativeGate.WaitAsync();
        try { _native.Dispose(); }
        catch
        {
            // A failed native close does not prove that the database is no longer in use.
            FailedNativeShutdowns.Add(this);
            throw;
        }
        finally { _nativeGate.Release(); }

        try { _native.UpdateReceived -= OnUpdateReceived; }
        finally { _lease.Dispose(); }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        await RunNativeAsync(async () =>
        {
            await _native.SetLogVerbosityAsync();
            return true;
        }, token);
        var state = await RefreshAuthorizationAsync(token);
        if (state.State is TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters)
        {
            var parameters = new TdApi.SetTdlibParameters
            {
                UseTestDc = false,
                DatabaseDirectory = _config.DatabaseDirectory,
                FilesDirectory = _config.FilesDirectory,
                DatabaseEncryptionKey = [],
                UseFileDatabase = true,
                UseChatInfoDatabase = true,
                UseMessageDatabase = true,
                UseSecretChats = false,
                ApiId = _config.ApiId,
                ApiHash = _config.ApiHash,
                SystemLanguageCode = "en",
                DeviceModel = Environment.MachineName,
                SystemVersion = RuntimeInformation.OSDescription,
                ApplicationVersion = typeof(TelegramSession).Assembly.GetName().Version?.ToString(3) ?? string.Empty
            };
            await RunNativeAsync(async () =>
            {
                await _native.SetParametersAsync(parameters);
                return true;
            }, token);
        }
    }

    private Task<AuthorizationSnapshot> RefreshAuthorizationAsync(CancellationToken token) =>
        RunNativeAsync(async () =>
        {
            AuthorizationSnapshot before;
            lock (_stateGate) before = ReadAuthorization();
            var response = await _native.GetAuthorizationStateAsync();
            lock (_stateGate)
            {
                // An update received since the query began is newer than the response.
                if (ReferenceEquals(before.Changed, _authorizationChanged.Task)) RecordAuthorization(response);
                return ReadAuthorization();
            }
        }, token);

    private async Task<T> RunNativeAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await _nativeGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_stateGate) ThrowIfSessionUnusable();
            var result = await action();
            token.ThrowIfCancellationRequested();
            lock (_stateGate) ThrowIfSessionUnusable();
            return result;
        }
        catch (TdException error) when (TelegramRpcPolicy.Classify(error).Kind == TelegramRpcFailureKind.Authorization)
        {
            lock (_stateGate) MarkTerminal(TelegramAuthorizationFailure.Revoked);
            token.ThrowIfCancellationRequested();
            throw TelegramAuthorizationException.FromNative(error);
        }
        finally { _nativeGate.Release(); }
    }

    private void OnUpdateReceived(object? sender, TdApi.Update update)
    {
        if (update is TdApi.Update.UpdateConnectionState connectionUpdate)
            _connection.Update(connectionUpdate.State);
        if (update is TdApi.Update.UpdateAuthorizationState authUpdate)
            lock (_stateGate) RecordAuthorization(authUpdate.AuthorizationState);
    }

    private AuthorizationSnapshot ReadAuthorization() => new(_authorizationState, _authorizationChanged.Task);

    private void RecordAuthorization(TdApi.AuthorizationState state)
    {
        if (ReferenceEquals(_authorizationState, state)) return;
        _authorizationState = state;
        if (state is TdApi.AuthorizationState.AuthorizationStateReady) _wasAuthorized = true;
        if (state is TdApi.AuthorizationState.AuthorizationStateLoggingOut || (_wasAuthorized && NeedsLogin(state)))
            MarkTerminal(TelegramAuthorizationFailure.Revoked);
        else if (state is TdApi.AuthorizationState.AuthorizationStateClosing or TdApi.AuthorizationState.AuthorizationStateClosed)
            MarkTerminal(TelegramAuthorizationFailure.Closed);

        var changed = _authorizationChanged;
        _authorizationChanged = NewAuthorizationSignal();
        changed.TrySetResult();
    }

    private void MarkTerminal(TelegramAuthorizationFailure reason)
    {
        if (_terminalReason is not null) return;
        _terminalReason = reason;
        OperationDeadline.Observe(_authorizationLost.CancelAsync());
    }

    internal void CheckActive()
    {
        lock (_stateGate) ThrowIfSessionUnusable();
    }

    private void ThrowIfSessionUnusable()
    {
        if (_terminalReason is { } reason)
            throw new TelegramAuthorizationException(reason, reason == TelegramAuthorizationFailure.Revoked
                ? "Telegram authorization has expired or been revoked. Run `tgcli login` first."
                : "TDLib closed this session. Open a new session before retrying.");
        if (_disposeTask is not null) throw new ObjectDisposedException(nameof(TelegramSession));
    }

    private static TaskCompletionSource NewAuthorizationSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly record struct AuthorizationSnapshot(TdApi.AuthorizationState? State, Task Changed);

    private async Task HandleAuthenticationCodeAsync(TdApi.AuthenticationCodeInfo codeInfo, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Telegram sent the login code {DescribeCodeDelivery(codeInfo.Type, codeInfo.PhoneNumber)}.");

        var resendAvailableAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, codeInfo.Timeout));
        if (codeInfo.NextType is not null)
        {
            var delay = codeInfo.Timeout > 0 ? $" after {codeInfo.Timeout} seconds" : string.Empty;
            Console.WriteLine($"If it doesn't arrive, enter 'resend' to request it {DescribeCodeDelivery(codeInfo.NextType, codeInfo.PhoneNumber)}{delay}.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = Prompt(codeInfo.NextType is null ? "Code" : "Code (or 'resend')");
            if (!string.Equals(code, "resend", StringComparison.OrdinalIgnoreCase))
            {
                await RunLoginRequest(() => Client.CheckAuthenticationCodeAsync(code), cancellationToken);
                return;
            }

            if (codeInfo.NextType is null)
            {
                Console.WriteLine("Telegram did not provide another delivery method for this login.");
                continue;
            }

            var remaining = resendAvailableAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                Console.WriteLine($"Waiting {Math.Ceiling(remaining.TotalSeconds):0} seconds before requesting another code...");
                await Task.Delay(remaining, cancellationToken);
            }

            await RunLoginRequest(() => Client.ResendAuthenticationCodeAsync(
                new TdApi.ResendCodeReason.ResendCodeReasonUserRequest()), cancellationToken);
            return;
        }
    }

    internal static string DescribeCodeDelivery(
        TdApi.AuthenticationCodeType type,
        string phoneNumber)
    {
        return type switch
        {
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeTelegramMessage =>
                "in the Telegram service chat on another logged-in device",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeSms =>
                $"by SMS to {phoneNumber}",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeSmsWord =>
                $"as a word by SMS to {phoneNumber}",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeSmsPhrase =>
                $"as a phrase by SMS to {phoneNumber}",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeCall =>
                $"by a phone call to {phoneNumber}",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeMissedCall missedCall =>
                $"through a missed call to {phoneNumber}; enter the last {missedCall.Length} digits of the caller's number",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeFlashCall flashCall =>
                $"through a flash call to {phoneNumber} from a number matching {flashCall.Pattern}",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeFragment fragment =>
                $"through Fragment at {fragment.Url}",
            TdApi.AuthenticationCodeType.AuthenticationCodeTypeFirebaseAndroid or
                TdApi.AuthenticationCodeType.AuthenticationCodeTypeFirebaseIos =>
                "through a Telegram verification notification",
            _ => $"to {phoneNumber}"
        };
    }

    private static string Prompt(string label, bool allowEmpty = false)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()
                ?? throw new TelegramAuthorizationException(TelegramAuthorizationFailure.LoginRequired,
                    "Telegram login requires input, but stdin ended. Run `tgcli login` with the required input.");
            if (allowEmpty || !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
    }

    private static string PromptSecret(string label)
    {
        Console.Write($"{label}: ");
        if (Console.IsInputRedirected)
        {
            // Preserve the supplied password exactly and never print it.
            return Console.ReadLine()
                ?? throw new TelegramAuthorizationException(TelegramAuthorizationFailure.LoginRequired,
                    "Telegram login requires a password, but stdin ended.");
        }

        var result = new List<char>();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(result.ToArray());
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                if (result.Count > 0)
                {
                    result.RemoveAt(result.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                result.Add(key.KeyChar);
            }
        }
    }

    internal static void ValidatePortableConfig(byte[] bytes)
    {
        var stored = SessionConfig.ParseStoredConfig(bytes);
        new SessionConfig { ApiId = stored.ApiId, ApiHash = stored.ApiHash }.Validate();
    }

    internal static void CreatePrivateSessionDirectory(string path) => SessionConfig.CreatePrivateDirectory(path);

    private sealed class SessionConfig
    {
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = string.Empty;
        public bool HasCredentials => ApiId > 0 && !string.IsNullOrWhiteSpace(ApiHash);
        public string RootDirectory { get; init; } = string.Empty;
        public string DatabaseDirectory => Path.Combine(RootDirectory, "tdlib-db");
        public string FilesDirectory => Path.Combine(RootDirectory, "tdlib-files");
        private string ConfigPath => Path.Combine(RootDirectory, "config.json");

        public static SessionConfig Load(string? sessionDirectory)
        {
            var root = ResolveSessionDirectory(sessionDirectory);
            var config = new SessionConfig { RootDirectory = root };

            config.LoadFromFile(SystemConfigPath());
            config.LoadFromFile(SharedConfigPath());
            config.LoadFromFile(config.ConfigPath);

            return config;
        }

        public void ApplyOverrides(int apiId, string? apiHash)
        {
            if (apiId > 0)
            {
                ApiId = apiId;
            }
            else if (int.TryParse(Environment.GetEnvironmentVariable("TGCLI_API_ID"), out var envApiId))
            {
                ApiId = envApiId;
            }

            if (!string.IsNullOrWhiteSpace(apiHash))
            {
                ApiHash = apiHash.Trim();
            }
            else
            {
                var envApiHash = Environment.GetEnvironmentVariable("TGCLI_API_HASH");
                if (!string.IsNullOrWhiteSpace(envApiHash))
                {
                    ApiHash = envApiHash.Trim();
                }
            }
        }

        public void Validate()
        {
            if (!HasCredentials)
            {
                throw new ValidationException("Telegram api id/hash are required. Pass --api-id/--api-hash or set TGCLI_API_ID/TGCLI_API_HASH.");
            }
        }

        public void EnsureDirectories()
        {
            CreatePrivateDirectory(RootDirectory);
            CreatePrivateDirectory(DatabaseDirectory);
            CreatePrivateDirectory(FilesDirectory);
        }

        public void Save()
        {
            EnsureDirectories();
            var json = JsonSerializer.Serialize(new StoredConfig(ApiId, ApiHash), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            SetPrivateFileMode(ConfigPath);
        }

        private void LoadFromFile(string configPath)
        {
            if (!File.Exists(configPath))
            {
                return;
            }

            using var stream = File.OpenRead(configPath);
            if (stream.Length > SessionSecret.MaxConfigBytes)
                throw new ValidationException("Telegram session config exceeds the size limit.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new ValidationException("Telegram session config changed while being read.");
            var loaded = ParseStoredConfig(bytes);
            if (loaded.ApiId > 0)
            {
                ApiId = loaded.ApiId;
            }

            if (!string.IsNullOrWhiteSpace(loaded.ApiHash))
            {
                ApiHash = loaded.ApiHash;
            }
        }

        internal static string DefaultRootDirectory()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(Environment.CurrentDirectory, ".tgcli");
            }

            return Path.Combine(home, ".local", "share", "tgcli");
        }

        private static string SharedConfigPath()
        {
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = !string.IsNullOrWhiteSpace(xdgConfigHome)
                ? xdgConfigHome
                : Path.Combine(home, ".config");

            return Path.Combine(root, "tgcli", "config.json");
        }

        private static string SystemConfigPath()
        {
            return Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", "tgcli", "config.json");
        }

        internal static void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(path);
            else
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SetPrivateDirectoryMode(path);
        }

        private static void SetPrivateDirectoryMode(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best effort only; some filesystems do not support Unix modes.
            }
        }

        private static void SetPrivateFileMode(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort only; some filesystems do not support Unix modes.
            }
        }

        internal static StoredConfig ParseStoredConfig(ReadOnlyMemory<byte> bytes)
        {
            ReadOnlyMemory<byte> bom = new byte[] { 0xef, 0xbb, 0xbf };
            if (bytes.Span.StartsWith(bom.Span)) bytes = bytes[bom.Length..];
            try
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ValidationException("Telegram session config must be a JSON object.");

                int? apiId = null;
                string? apiHash = null;
                foreach (var field in json.RootElement.EnumerateObject())
                {
                    if (field.Name.Equals("ApiId", StringComparison.OrdinalIgnoreCase))
                    {
                        if (apiId is not null || field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var id))
                            throw new ValidationException("Telegram session config has an invalid or duplicate API ID.");
                        apiId = id;
                    }
                    else if (field.Name.Equals("ApiHash", StringComparison.OrdinalIgnoreCase))
                    {
                        if (apiHash is not null || field.Value.ValueKind != JsonValueKind.String)
                            throw new ValidationException("Telegram session config has an invalid or duplicate API hash.");
                        apiHash = field.Value.GetString()!.Trim();
                    }
                }

                return new StoredConfig(apiId ?? 0, apiHash ?? string.Empty);
            }
            catch (JsonException ex)
            {
                throw new ValidationException("Telegram session config is not valid JSON.", ex);
            }
        }

        internal sealed record StoredConfig(int ApiId, string ApiHash);
    }
}

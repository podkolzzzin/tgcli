using System.Runtime.InteropServices;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using TdLib;

namespace TgCli;

internal sealed class TelegramSession : IAsyncDisposable
{
    private readonly SessionConfig _config;
    private SessionLockLease? _lease;
    private TdApi.AuthorizationState? _authorizationState;
    private TaskCompletionSource<TdApi.AuthorizationState> _authorizationChanged = NewAuthorizationChangedSource();
    private readonly TelegramConnection _connection = new();

    private TelegramSession(TdClient client, SessionConfig config)
    {
        Client = client;
        _config = config;
        Client.UpdateReceived += OnUpdateReceived;
    }

    public TdClient Client { get; }
    public string SessionDirectory => _config.RootDirectory;
    public string DatabaseDirectory => _config.DatabaseDirectory;
    public string FilesDirectory => _config.FilesDirectory;
    public bool IsConnected => _connection.IsReady;
    public string ConnectionState => _connection.StateName;

    public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        _connection.WaitReadyAsync(TimeSpan.FromSeconds(30), cancellationToken);

    internal static string ResolveSessionDirectory(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? SessionConfig.DefaultRootDirectory() : path);

    public static async Task<TelegramSession> CreateAsync(string? sessionDirectory, int apiId, string? apiHash, bool saveConfig, int lockTimeout = 30, bool noWait = false)
    {
        var root = ResolveSessionDirectory(sessionDirectory);
        SessionConfig.CreatePrivateDirectory(root);
        var acquiredLock = await SessionLock.AcquireAsync(root, TimeSpan.FromSeconds(noWait ? 0 : Math.Max(0, lockTimeout)));
        TelegramSession? session = null;
        try
        {
            SessionSecret.RecoverImport(root);
            var config = SessionConfig.Load(root);
            config.ApplyOverrides(apiId, apiHash);
            config.Validate();
            config.EnsureDirectories();
            if (saveConfig)
            {
                config.Save();
            }

            DisableTdLibLogging();
            session = new TelegramSession(new TdClient(), config)
            {
                _lease = acquiredLock
            };
            await session.InitializeAsync();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                acquiredLock.Dispose();
            }

            throw;
        }
    }

    public static async Task<TelegramSession> CreateReadyAsync(string? sessionDirectory, int lockTimeout = 30, bool noWait = false, bool local = false)
    {
        var session = await CreateAsync(sessionDirectory, apiId: 0, apiHash: null, saveConfig: false, lockTimeout, noWait);
        try
        {
            await session.EnsureReadyAsync();
            if (!local) await session.EnsureConnectedAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task LoginAsync(string? phone)
    {
        while (true)
        {
            var state = await GetAuthorizationStateAsync();

            switch (state)
            {
                case TdApi.AuthorizationState.AuthorizationStateReady:
                    Console.WriteLine("Logged in.");
                    return;

                case TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber:
                    phone ??= Prompt("Phone");
                    await Client.SetAuthenticationPhoneNumberAsync(phone, settings: null!);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitCode waitCode:
                    await HandleAuthenticationCodeAsync(waitCode.CodeInfo);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitPassword waitPassword:
                    var passwordPrompt = string.IsNullOrWhiteSpace(waitPassword.PasswordHint)
                        ? "Password"
                        : $"Password (hint: {waitPassword.PasswordHint})";
                    await Client.CheckAuthenticationPasswordAsync(PromptSecret(passwordPrompt));
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitEmailAddress:
                    await Client.SetAuthenticationEmailAddressAsync(Prompt("Email"));
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitEmailCode:
                    await Client.CheckAuthenticationEmailCodeAsync(
                        new TdApi.EmailAddressAuthentication.EmailAddressAuthenticationCode { Code = Prompt("Email code") });
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitRegistration:
                    var firstName = Prompt("First name");
                    var lastName = Prompt("Last name", allowEmpty: true);
                    await Client.RegisterUserAsync(firstName, lastName, disableNotification: true);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation confirmation:
                    Console.WriteLine("Confirm login on another Telegram client:");
                    Console.WriteLine(confirmation.Link);
                    await WaitForAuthorizationChangeAsync(state);
                    break;

                case TdApi.AuthorizationState.AuthorizationStateWaitPremiumPurchase:
                    throw new ValidationException("Telegram requires Premium purchase for this login; tgcli can't complete that flow.");

                case TdApi.AuthorizationState.AuthorizationStateClosing:
                case TdApi.AuthorizationState.AuthorizationStateClosed:
                    throw new ValidationException("TDLib session is closing or closed.");

                default:
                    await WaitForAuthorizationChangeAsync(state);
                    break;
            }
        }
    }

    public async Task EnsureReadyAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await GetAuthorizationStateAsync();
            if (state is TdApi.AuthorizationState.AuthorizationStateReady)
            {
                return;
            }

            if (state is TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber
                or TdApi.AuthorizationState.AuthorizationStateWaitCode
                or TdApi.AuthorizationState.AuthorizationStateWaitPassword
                or TdApi.AuthorizationState.AuthorizationStateWaitEmailAddress
                or TdApi.AuthorizationState.AuthorizationStateWaitEmailCode
                or TdApi.AuthorizationState.AuthorizationStateWaitRegistration
                or TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation)
            {
                throw new ValidationException("Telegram session is not authorized. Run `tgcli login` first.");
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await WaitForAuthorizationChangeAsync(state, remaining);
        }

        throw new TimeoutException("Timed out while waiting for TDLib authorization state.");
    }

    private Task? _disposeTask;

    public async ValueTask DisposeAsync()
    {
        Client.UpdateReceived -= OnUpdateReceived;
        _disposeTask ??= CloseAndReleaseAsync();
        if (await Task.WhenAny(_disposeTask, Task.Delay(TimeSpan.FromSeconds(10))) == _disposeTask)
            await _disposeTask;
        else
        {
            // The native client may still be using its database. Retain the file lock
            // until native disposal actually completes, or until this process exits.
            Console.Error.WriteLine("Warning: TDLib shutdown exceeded 10 seconds; retaining its session lock until native shutdown or process exit.");
            _ = _disposeTask.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task CloseAndReleaseAsync()
    {
        await Task.Run(Client.Dispose);
        _lease?.Dispose();
    }

    private async Task InitializeAsync()
    {
        await Client.SetLogVerbosityLevelAsync(0);
        var state = await Client.GetAuthorizationStateAsync();
        await HandleAuthorizationStateAsync(state);
    }

    private async Task<TdApi.AuthorizationState> GetAuthorizationStateAsync()
    {
        var state = await Client.GetAuthorizationStateAsync();
        await HandleAuthorizationStateAsync(state);
        return _authorizationState ?? state;
    }

    private async Task HandleAuthorizationStateAsync(TdApi.AuthorizationState state)
    {
        _authorizationState = state;

        if (state is TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters)
        {
            await Client.SetTdlibParametersAsync(
                useTestDc: false,
                databaseDirectory: _config.DatabaseDirectory,
                filesDirectory: _config.FilesDirectory,
                databaseEncryptionKey: [],
                useFileDatabase: true,
                useChatInfoDatabase: true,
                useMessageDatabase: true,
                useSecretChats: false,
                apiId: _config.ApiId,
                apiHash: _config.ApiHash,
                systemLanguageCode: "en",
                deviceModel: Environment.MachineName,
                systemVersion: RuntimeInformation.OSDescription,
                applicationVersion: "7.0.1");
        }
    }

    private async Task HandleAuthenticationCodeAsync(TdApi.AuthenticationCodeInfo codeInfo)
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
            var code = Prompt(codeInfo.NextType is null ? "Code" : "Code (or 'resend')");
            if (!string.Equals(code, "resend", StringComparison.OrdinalIgnoreCase))
            {
                await Client.CheckAuthenticationCodeAsync(code);
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
                await Task.Delay(remaining);
            }

            await Client.ResendAuthenticationCodeAsync(
                new TdApi.ResendCodeReason.ResendCodeReasonUserRequest());
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

    private async Task WaitForAuthorizationChangeAsync(
        TdApi.AuthorizationState previousState,
        TimeSpan? timeout = null)
    {
        var source = _authorizationChanged;
        if (!ReferenceEquals(_authorizationState, previousState))
        {
            return;
        }

        var state = await source.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(5));
        await HandleAuthorizationStateAsync(state);
    }

    private void OnUpdateReceived(object? sender, TdApi.Update update)
    {
        if (update is TdApi.Update.UpdateConnectionState connectionUpdate)
        {
            _connection.Update(connectionUpdate.State);
            return;
        }
        if (update is not TdApi.Update.UpdateAuthorizationState authUpdate)
        {
            return;
        }

        _authorizationState = authUpdate.AuthorizationState;
        var oldSource = _authorizationChanged;
        _authorizationChanged = NewAuthorizationChangedSource();
        oldSource.TrySetResult(authUpdate.AuthorizationState);
    }

    private static TaskCompletionSource<TdApi.AuthorizationState> NewAuthorizationChangedSource()
    {
        return new TaskCompletionSource<TdApi.AuthorizationState>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void DisableTdLibLogging()
    {
        TdJsonClient.GlobalExecute("""{"@type":"setLogVerbosityLevel","new_verbosity_level":0}""");
    }

    private static string Prompt(string label, bool allowEmpty = false)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()
                ?? throw new ValidationException("Telegram login requires input, but stdin ended. Run `tgcli login` with the required input.");
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
                ?? throw new ValidationException("Telegram login requires a password, but stdin ended.");
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
            if (ApiId <= 0 || string.IsNullOrWhiteSpace(ApiHash))
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

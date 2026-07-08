using System.Runtime.InteropServices;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using TdLib;

namespace TgCli;

internal sealed class TelegramSession : IAsyncDisposable
{
    private readonly SessionConfig _config;
    private TdApi.AuthorizationState? _authorizationState;
    private TaskCompletionSource<TdApi.AuthorizationState> _authorizationChanged = NewAuthorizationChangedSource();

    private TelegramSession(TdClient client, SessionConfig config)
    {
        Client = client;
        _config = config;
        Client.UpdateReceived += OnUpdateReceived;
    }

    public TdClient Client { get; }

    public static async Task<TelegramSession> CreateAsync(string? sessionDirectory, int apiId, string? apiHash, bool saveConfig)
    {
        var config = SessionConfig.Load(sessionDirectory);
        config.ApplyOverrides(apiId, apiHash);
        config.Validate();
        config.EnsureDirectories();

        if (saveConfig)
        {
            config.Save();
        }

        DisableTdLibLogging();
        var session = new TelegramSession(new TdClient(), config);
        await session.InitializeAsync();
        return session;
    }

    public static async Task<TelegramSession> CreateReadyAsync(string? sessionDirectory)
    {
        var session = await CreateAsync(sessionDirectory, apiId: 0, apiHash: null, saveConfig: false);
        await session.EnsureReadyAsync();
        return session;
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

                case TdApi.AuthorizationState.AuthorizationStateWaitCode:
                    await Client.CheckAuthenticationCodeAsync(Prompt("Code"));
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

            await WaitForAuthorizationChangeAsync(state);
        }

        throw new TimeoutException("Timed out while waiting for TDLib authorization state.");
    }

    public async ValueTask DisposeAsync()
    {
        Client.UpdateReceived -= OnUpdateReceived;
        await Task.Run(Client.Dispose);
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
                applicationVersion: "2.0.0");
        }
    }

    private async Task WaitForAuthorizationChangeAsync(TdApi.AuthorizationState previousState)
    {
        var source = _authorizationChanged;
        if (!ReferenceEquals(_authorizationState, previousState))
        {
            return;
        }

        var state = await source.Task.WaitAsync(TimeSpan.FromMinutes(5));
        await HandleAuthorizationStateAsync(state);
    }

    private void OnUpdateReceived(object? sender, TdApi.Update update)
    {
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
            var value = Console.ReadLine() ?? string.Empty;
            if (allowEmpty || !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
    }

    private static string PromptSecret(string label)
    {
        Console.Write($"{label}: ");
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
            var root = Path.GetFullPath(sessionDirectory ?? DefaultRootDirectory());
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

            var loaded = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(configPath));
            if (loaded?.ApiId > 0)
            {
                ApiId = loaded.ApiId;
            }

            if (!string.IsNullOrWhiteSpace(loaded?.ApiHash))
            {
                ApiHash = loaded.ApiHash;
            }
        }

        private static string DefaultRootDirectory()
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

        private static void CreatePrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
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

        private sealed record StoredConfig(int ApiId, string ApiHash);
    }
}

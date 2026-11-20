using TdLib;

namespace TgCli;

internal interface ITelegramSessionNative : IDisposable
{
    TdClient? Client { get; }
    event EventHandler<TdApi.Update>? UpdateReceived;
    Task SetLogVerbosityAsync();
    Task<TdApi.AuthorizationState> GetAuthorizationStateAsync();
    Task SetParametersAsync(TdApi.SetTdlibParameters parameters);
}

internal sealed class TelegramSessionNative : ITelegramSessionNative
{
    private TelegramSessionNative(TdClient client) => Client = client;

    public TdClient Client { get; }

    public event EventHandler<TdApi.Update>? UpdateReceived
    {
        add => Client.UpdateReceived += value;
        remove => Client.UpdateReceived -= value;
    }

    public static ITelegramSessionNative Open()
    {
        TdJsonClient.GlobalExecute("""{"@type":"setLogVerbosityLevel","new_verbosity_level":0}""");
        return new TelegramSessionNative(new TdClient { TimeoutToClose = TimeSpan.FromSeconds(10) });
    }

    public Task SetLogVerbosityAsync() => Client.SetLogVerbosityLevelAsync(0);
    public Task<TdApi.AuthorizationState> GetAuthorizationStateAsync() => Client.GetAuthorizationStateAsync();
    public Task SetParametersAsync(TdApi.SetTdlibParameters parameters) => Client.ExecuteAsync(parameters);
    public void Dispose() => Client.Dispose();
}

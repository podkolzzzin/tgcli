using ConsoleAppFramework;

namespace TgCli;

internal static class Program
{
    public static void Main(string[] args)
    {
        var app = ConsoleApp.Create();

        app.Add<TgCommands>();
        app.Add<ChatCommands>("chat");
        app.Add<LinkCommands>("link");
        app.Add<MessageCommands>("message");

        app.Run(args);
    }
}

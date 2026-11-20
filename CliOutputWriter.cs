using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TgCli;

internal sealed class CliOutputWriter(TextWriter writer) : TextWriter
{
    internal static TextWriter Open()
    {
        if (!Console.IsOutputRedirected) return new CliOutputWriter(Console.Out);
        // The standard Console stream suppresses broken-pipe errors. Open the same
        // handle without owning it so a disconnected output reader can stop a command.
        var handle = OperatingSystem.IsWindows() ? GetStdHandle(-11) : (nint)1;
        if (handle == 0 || handle == -1) return new CliOutputWriter(Console.Out);
        var output = new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write,
            bufferSize: 1, isAsync: false);
        var encoding = Console.OutputEncoding.CodePage == 65001 ? new UTF8Encoding(false) : Console.OutputEncoding;
        return new CliOutputWriter(new StreamWriter(output, encoding) { AutoFlush = true });
    }

    public override Encoding Encoding => writer.Encoding;

    public override void Write(char value) => WriteToOutput(() => writer.Write(value));
    public override void Write(string? value) => WriteToOutput(() => writer.Write(value));
    public override void Write(char[] buffer, int index, int count) => WriteToOutput(() => writer.Write(buffer, index, count));
    public override void WriteLine(string? value) => WriteToOutput(() => writer.WriteLine(value));
    public override void Flush() => WriteToOutput(writer.Flush);

    public override void Write(ReadOnlySpan<char> buffer)
    {
        CliExecution.CancellationToken.ThrowIfCancellationRequested();
        try { writer.Write(buffer); }
        catch (IOException error) when (IsBrokenPipe(error)) { throw new CliClosedOutputException(error); }
    }

    private static bool IsBrokenPipe(IOException error)
    {
        var code = error.HResult & 0xffff;
        return OperatingSystem.IsWindows() ? code is 109 or 232 : code == 32;
    }

    private static void WriteToOutput(Action write)
    {
        CliExecution.CancellationToken.ThrowIfCancellationRequested();
        try { write(); }
        catch (IOException error) when (IsBrokenPipe(error)) { throw new CliClosedOutputException(error); }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);
}

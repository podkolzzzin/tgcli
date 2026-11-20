using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TgCli;

// The handler only records evidence. The host owns the one stdout write, even if native work outlives its deadline.
internal sealed class CliDiagnosticOutput(string root, TextWriter output)
{
    private SessionDiagnosticObservation _observation = new(root, null, null, null);
    private string? _result;

    internal void Observe(SessionDiagnosticObservation observation) => Volatile.Write(ref _observation, observation);

    internal void Complete(JObject data) => Volatile.Write(ref _result, data.ToString(Formatting.None));

    internal void WriteResult()
    {
        if (Volatile.Read(ref _result) is { } result) output.WriteLine(result);
    }

    internal void WriteDeadline(TimeSpan elapsed) => output.WriteLine(
        SessionDiagnostic.Deadline(Volatile.Read(ref _observation), elapsed).ToString(Formatting.None));
}

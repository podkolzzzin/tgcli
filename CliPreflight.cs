using System.Globalization;
using System.Reflection;
using System.Text;
using ConsoleAppFramework;

namespace TgCli;

internal sealed class CliPreflight(params (Type Type, string Prefix)[] registrations)
{
    private const string TimeoutOption = "--request-timeout";
    private const int DefaultTimeout = 60;
    private readonly Command[] _commands = registrations.SelectMany(Discover)
        .OrderByDescending(command => command.Path.Length).ToArray();

    internal CliInvocation Prepare(string[] args)
    {
        var start = 0;
        string? leadingTimeout = null;
        if (args.Length > 0 && Name(args[0]).Equals(TimeoutOption, StringComparison.OrdinalIgnoreCase))
            leadingTimeout = ReadValue(args, ref start, TimeoutOption, InlineValue(args[0]));

        var remaining = args.AsSpan(start);
        if ((remaining.Length == 0 && leadingTimeout is null) || (remaining.Length == 1 && IsHelpOrVersion(remaining[0])))
            return Help(remaining.ToArray());

        var command = _commands.FirstOrDefault(candidate => candidate.Matches(args, start));
        if (command is null)
        {
            var group = remaining.Length > 0 ? remaining[0] : "";
            if (_commands.Any(candidate => candidate.Path.Length > 1 && candidate.Path[0] == group)
                && (remaining.Length == 1 || remaining.Length == 2 && IsHelpOrVersion(remaining[1])))
                return Help(remaining.ToArray());

            var knownGroup = _commands.Any(candidate => candidate.Path[0] == group);
            var unknown = knownGroup && remaining.Length > 1 ? remaining[1] : group;
            throw new CliUsageException(unknown.Length == 0
                ? "Specify a command. Run `tgcli --help` to list commands."
                : $"Unknown command '{unknown}'. Run `tgcli --help` to list commands.");
        }

        var normalized = new List<string>(command.Path);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        var index = start + command.Path.Length;
        var ownsTimeout = command.Options.ContainsKey(TimeoutOption);
        var timeout = DefaultTimeout;
        if (leadingTimeout is not null) AddTimeout(leadingTimeout);

        while (index < args.Length)
        {
            var token = args[index];
            if (IsHelpOrVersion(token)) return Help([..command.Path, token], ownsTimeout || command.Name == "mcp");
            if (!LooksLikeOption(token))
            {
                if (position >= command.Positional.Length)
                    throw new CliUsageException($"Unexpected argument for '{command.Name}'.");
                var argument = command.Positional[position++];
                ValidateType(argument, token);
                normalized.Add(token);
                values.Add(argument.Name, token);
                index++;
                continue;
            }

            var name = Name(token);
            var inline = InlineValue(token);
            if (name.Equals(TimeoutOption, StringComparison.OrdinalIgnoreCase))
            {
                AddTimeout(ReadValue(args, ref index, TimeoutOption, inline));
                continue;
            }
            if (!command.Options.TryGetValue(name, out var option))
                throw new CliUsageException($"Unknown option '{name}' for '{command.Name}'.");

            if (option.Info.ParameterType == typeof(bool))
            {
                if (inline is not null)
                    throw new CliUsageException($"{option.Name} is a flag and takes no value.");
                AddValue(values, option, "true");
                normalized.Add(option.Name);
                index++;
                continue;
            }

            var value = ReadValue(args, ref index, option.Name, inline);
            ValidateType(option, value);
            AddValue(values, option, value);
            normalized.Add(option.Name);
            normalized.Add(value);
        }

        foreach (var parameter in command.Parameters)
        {
            if (values.ContainsKey(parameter.Name)) continue;
            if (!parameter.Info.HasDefaultValue && !parameter.Repeatable)
                throw new CliUsageException($"Missing required {(parameter.Positional ? "argument" : "option")} '{parameter.Name}'.");
        }

        CliInputRules.Validate(command.Name, values);
        return new([..normalized], command.Name == "mcp" ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(timeout), false);

        void AddTimeout(string value)
        {
            if (command.Name == "mcp")
                throw new CliUsageException("The MCP server has no CLI request timeout. Set request_timeout on individual tool calls.");
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) || timeout is < 1 or > 3600)
                throw new CliUsageException("--request-timeout must be between 1 and 3600 seconds.");
            if (!values.TryAdd(TimeoutOption, value))
                throw new CliUsageException($"Specify {TimeoutOption} only once.");
            if (ownsTimeout) normalized.AddRange([TimeoutOption, value]);
        }
    }

    private static CliInvocation Help(string[] args, bool ownsTimeout = false) =>
        new(args, Timeout.InfiniteTimeSpan, !ownsTimeout && !args.Contains("--version", StringComparer.Ordinal));

    private static void AddValue(Dictionary<string, string> values, Parameter option, string value)
    {
        if (!values.TryAdd(option.Name, value))
            throw new CliUsageException($"Specify {option.Name} only once.");
    }

    private static string ReadValue(string[] args, ref int index, string name, string? inline)
    {
        index++;
        if (inline == "--") throw new CliUsageException($"{name} cannot use the reserved '--' delimiter as its value.");
        if (inline is not null) return inline;
        if (index < args.Length && !LooksLikeOption(args[index])) return args[index++];
        throw new CliUsageException($"{name} requires a value. Use {name}=<value> for a value beginning with '-'.");
    }

    private static void ValidateType(Parameter parameter, string value)
    {
        var type = Nullable.GetUnderlyingType(parameter.Info.ParameterType) ?? parameter.Info.ParameterType;
        if ((type == typeof(int) && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            || (type == typeof(long) && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new CliUsageException($"{parameter.Name} must be an integer in range.");
    }

    private static string Name(string token)
    {
        var split = token.IndexOf('=');
        return split < 0 ? token : token[..split];
    }

    private static string? InlineValue(string token)
    {
        var split = token.IndexOf('=');
        return split < 0 ? null : token[(split + 1)..];
    }

    private static bool LooksLikeOption(string value) => value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1]);
    private static bool IsHelpOrVersion(string value) => value is "--help" or "-h" or "--version";

    private static IEnumerable<Command> Discover((Type Type, string Prefix) registration)
    {
        foreach (var method in registration.Type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.IsSpecialName) continue;
            var methodName = method.GetCustomAttribute<CommandAttribute>()?.Command ?? KebabCase(method.Name);
            var command = string.Join(' ', registration.Prefix, methodName).Trim();
            var parameters = method.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken)
                    && parameter.ParameterType != typeof(ConsoleAppContext)
                    && parameter.GetCustomAttribute<FromServicesAttribute>() is null)
                .Select(parameter => new Parameter(parameter, parameter.GetCustomAttribute<ArgumentAttribute>() is not null))
                .ToArray();
            yield return new(command, parameters);
        }
    }

    private static string KebabCase(string name)
    {
        var result = new StringBuilder();
        foreach (var character in name)
        {
            if (char.IsUpper(character) && result.Length > 0) result.Append('-');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    private sealed class Command(string name, Parameter[] parameters)
    {
        internal string Name { get; } = name;
        internal string[] Path { get; } = name.Split(' ');
        internal Parameter[] Parameters { get; } = parameters;
        internal Parameter[] Positional { get; } = parameters.Where(parameter => parameter.Positional).ToArray();
        internal Dictionary<string, Parameter> Options { get; } = parameters.Where(parameter => !parameter.Positional)
            .ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
        internal bool Matches(string[] args, int start) => args.AsSpan(start).StartsWith(Path.AsSpan());
    }

    private sealed class Parameter(ParameterInfo parameter, bool positional)
    {
        internal ParameterInfo Info { get; } = parameter;
        internal bool Positional { get; } = positional;
        internal bool Repeatable { get; } = parameter.GetCustomAttribute<ParamArrayAttribute>() is not null;
        internal string Name { get; } = (positional ? "" : "--") + KebabCase(parameter.Name!);
    }
}

internal sealed record CliInvocation(string[] Arguments, TimeSpan Budget, bool ShowGlobalTimeoutHelp);

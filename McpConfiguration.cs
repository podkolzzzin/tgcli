using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace TgCli;

internal enum McpConfigFormat { Json, Toml, EclipsePreferences }

internal sealed record McpLaunch(string Command, string[] Arguments)
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Assembly.Location is used only when the process is the dotnet host; single-file executables use Environment.ProcessPath.")]
    internal static McpLaunch Current(string session)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = typeof(Program).Assembly.Location;
            if (string.IsNullOrEmpty(assembly)) throw new InvalidOperationException("Cannot locate tgcli.dll.");
            args.Add(Path.GetFullPath(assembly));
        }
        args.AddRange(["mcp", "--session", Path.GetFullPath(session)]);
        return new(Path.GetFullPath(executable), args.ToArray());
    }
    internal JsonObject ToJson(bool copilot = false) => new()
    {
        ["type"] = "stdio",
        ["command"] = Command,
        ["args"] = new JsonArray(Arguments.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
    };
    internal bool Matches(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["command"]?.GetValue<string>() != Command || obj["args"] is not JsonArray args) return false;
        if (!args.Select(x => x?.GetValue<string>()).SequenceEqual(Arguments)) return false;
        return obj["url"] is null && (obj["type"] is null || obj["type"]!.GetValue<string>() is "stdio" or "local");
    }
}

internal static class McpConfiguration
{
    internal static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    internal static JsonObject ReadJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Configuration is empty or invalid JSON.");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text.TrimStart('\uFEFF')), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var stack = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) stack.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !stack.Peek().Add(reader.GetString()!)) throw new InvalidDataException("Configuration contains duplicate JSON keys.");
        }
        return JsonNode.Parse(text.TrimStart('\uFEFF'), documentOptions: JsonOptions) as JsonObject
            ?? throw new InvalidDataException("Configuration must be a JSON object.");
    }

    internal static JsonNode? ReadEntry(string text, McpConfigFormat format, string rootKey)
    {
        if (format == McpConfigFormat.EclipsePreferences)
        {
            var value = JavaProperties.Get(text, "mcp");
            return value is null or "" ? null : Entry(ReadJson(value), rootKey);
        }
        if (format == McpConfigFormat.Json) return Entry(ReadJson(text), rootKey);
        var model = Toml.ToModel(text.TrimStart('\uFEFF'));
        if (!model.TryGetValue("mcp_servers", out var servers)) return null;
        if (servers is not TomlTable table) throw new InvalidDataException("mcp_servers must be a TOML table.");
        if (!table.TryGetValue("tgcli", out var entry)) return null;
        return JsonSerializer.SerializeToNode(entry);
    }

    private static JsonNode? Entry(JsonObject root, string key)
    {
        if (!root.TryGetPropertyValue(key, out var node)) return null;
        if (node is not JsonObject table) throw new InvalidDataException($"{key} must be an object.");
        return table["tgcli"];
    }

    internal static string Merge(string? original, McpConfigFormat format, string rootKey, McpLaunch launch, bool copilot = false)
    {
        if (original is not null)
        {
            var entry = ReadEntry(original, format, rootKey);
            if (entry is not null)
            {
                if (launch.Matches(entry)) return original;
                throw new InvalidOperationException("A different tgcli MCP entry already exists. Remove or rename it in this client's MCP settings before reinstalling.");
            }
        }
        if (format == McpConfigFormat.EclipsePreferences)
        {
            var prefs = original ?? "eclipse.preferences.version=1\n";
            var json = JavaProperties.Get(prefs, "mcp");
            var merged = Merge(string.IsNullOrWhiteSpace(json) ? null : json, McpConfigFormat.Json, rootKey, launch);
            return JavaProperties.Set(prefs, "mcp", merged);
        }
        if (format == McpConfigFormat.Toml)
        {
            // Appending a new table leaves every existing byte (including comments) intact.
            var result = (original ?? "") + "\n[mcp_servers.tgcli]\ncommand = " + JsonSerializer.Serialize(launch.Command)
                + "\nargs = " + JsonSerializer.Serialize(launch.Arguments) + "\n";
            _ = Toml.ToModel(result.TrimStart('\uFEFF'));
            return result;
        }
        var root = original is null ? new JsonObject() : ReadJson(original);
        var entryJson = launch.ToJson();
        if (copilot) entryJson["tools"] = new JsonArray("*");
        var text = original ?? "{}\n";
        var entryText = JsonSerializer.Serialize("tgcli") + ": " + entryJson.ToJsonString();
        if (!root.ContainsKey(rootKey))
            return InsertIntoObject(text, [], JsonSerializer.Serialize(rootKey) + ": {" + entryText + "}");
        return InsertIntoObject(text, [rootKey], entryText);
    }

    private static string InsertIntoObject(string text, string[] path, string property)
    {
        // Work on token offsets rather than reserializing: preserve JSONC comments and formatting.
        var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : "";
        var bytes = Encoding.UTF8.GetBytes(text.TrimStart('\uFEFF'));
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        reader.Read();
        foreach (var key in path)
        {
            var depth = reader.CurrentDepth;
            var found = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == depth + 1 && reader.GetString() == key)
                { reader.Read(); found = true; break; }
            }
            if (!found || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Expected configuration object.");
        }
        var insertion = (int)reader.BytesConsumed;
        reader.Read();
        var comma = reader.TokenType == JsonTokenType.EndObject ? "" : ",";
        var result = bom + Encoding.UTF8.GetString(bytes[..insertion]) + "\n  " + property + comma + "\n" + Encoding.UTF8.GetString(bytes[insertion..]);
        _ = ReadJson(result);
        return result;
    }

    internal static async Task WriteAtomicAsync(string path, string? original, string updated, CancellationToken ct)
    {
        if (original == updated) return;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".tgcli-mcp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await WritePrivateAsync(temp, updated, ct);
            var current = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            if (current != original) throw new IOException("Configuration changed during installation. Retry after the client finishes saving settings.");
            if (original is not null)
            {
                await BackupAsync(path, original, ct);
            }
            File.Move(temp, path, overwrite: original is not null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static Task BackupAsync(string path, string original, CancellationToken ct) =>
        WritePrivateAsync(path + ".tgcli-backup-" + Guid.NewGuid().ToString("N"), original, ct);

    private static async Task WritePrivateAsync(string path, string text, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await stream.FlushAsync(ct);
    }
}

internal static class JavaProperties
{
    internal static string? Get(string text, string key)
    {
        var matches = Lines(text).Where(x => x.Key == key).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Duplicate Eclipse preference: {key}.");
        return matches.Length == 0 ? null : Unescape(matches[0].Value);
    }
    internal static string Set(string text, string key, string value)
    {
        var lines = Lines(text).Where(x => x.Key == key).ToArray();
        if (lines.Length > 1) throw new InvalidDataException($"Duplicate Eclipse preference: {key}.");
        var newLine = key + "=" + Escape(value) + "\n";
        return lines.Length == 0 ? text.TrimEnd('\r', '\n') + "\n" + newLine
            : text[..lines[0].Start] + newLine + text[lines[0].End..];
    }
    private static IEnumerable<(string Key, string Value, int Start, int End)> Lines(string text)
    {
        var offset = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r'); var start = offset; offset = Math.Min(text.Length, offset + raw.Length + 1);
            if (line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith('!')) continue;
            var sep = -1; var escaped = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (!escaped && line[i] is '=' or ':') { sep = i; break; }
                escaped = !escaped && line[i] == '\\';
            }
            if (sep < 0) continue;
            var key = Unescape(line[..sep].Trim());
            var value = line[(sep + 1)..].TrimStart();
            if (value.EndsWith('\\') && value.Reverse().TakeWhile(c => c == '\\').Count() % 2 != 0)
                throw new InvalidDataException("Continued Eclipse property lines are not supported; save preferences from Eclipse first.");
            yield return (key, value, start, offset);
        }
    }
    internal static string Unescape(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\') { result.Append(c); continue; }
            if (++i == value.Length) throw new InvalidDataException("Invalid property escape.");
            c = value[i];
            if (c == 'u')
            {
                if (i + 4 >= value.Length || !ushort.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code)) throw new InvalidDataException("Invalid Unicode escape.");
                result.Append((char)code); i += 4;
            }
            else result.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'f' => '\f', _ => c });
        }
        return result.ToString();
    }
    private static string Escape(string value) => string.Concat(value.Select(c => c switch
    {
        '\\' => "\\\\",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '=' => "\\=",
        ':' => "\\:",
        > '\u007e' or < ' ' => "\\u" + ((int)c).ToString("x4"),
        _ => c.ToString()
    }));
}

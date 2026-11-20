using System.Globalization;
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
    internal JsonObject ToJson() => new()
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
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
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

    internal static async Task<string?> ReadAsync(string path, McpConfigFormat format, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return ConfigEncoding(format).GetString(bytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (DecoderFallbackException) { throw new InvalidDataException("Configuration is not valid UTF-8. Save it in the client before retrying."); }
    }

    private static Encoding ConfigEncoding(McpConfigFormat format) =>
        format == McpConfigFormat.EclipsePreferences ? Encoding.Latin1 : StrictUtf8;

    internal static FileStream AcquireWriteLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Keep the coordination inode in place across installs, even after releasing it.
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(path + ".tgcli-lock", options); }
        catch (IOException ex) { throw new IOException("Could not lock the configuration. Another tgcli installer may be writing it.", ex); }
    }

    internal static async Task EnsureUnchangedAsync(string path, string? original, McpConfigFormat format, CancellationToken ct)
    {
        if (await ReadAsync(path, format, ct) != original)
            throw new IOException("Configuration changed during installation. Retry after the client finishes saving settings.");
    }

    internal static async Task WriteAtomicAsync(string path, string? original, string updated, CancellationToken ct,
        McpConfigFormat format = McpConfigFormat.Json)
    {
        if (original == updated) return;
        ct.ThrowIfCancellationRequested();
        using var writeLock = AcquireWriteLock(path);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".tgcli-mcp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await WritePrivateAsync(temp, updated, format, ct);
            await EnsureUnchangedAsync(path, original, format, ct);
            if (original is not null)
            {
                await BackupAsync(path, original, format, ct);
                await EnsureUnchangedAsync(path, original, format, ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: original is not null);
            if (await ReadAsync(path, format, ct) != updated)
                throw new IOException("Client configuration verification failed: the saved file changed after replacement.");
        }
        finally { File.Delete(temp); }
    }

    internal static async Task BackupAsync(string path, string original, McpConfigFormat format, CancellationToken ct)
    {
        var backup = path + ".tgcli-backup-" + Guid.NewGuid().ToString("N");
        var temp = backup + ".tmp";
        try
        {
            await WritePrivateAsync(temp, original, format, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, backup);
        }
        finally { File.Delete(temp); }
    }

    internal static bool PreservesSettings(string original, string saved, McpConfigFormat format)
    {
        if (format == McpConfigFormat.Json) return ContainsOriginal(ReadJson(original), ReadJson(saved));
        var before = JsonSerializer.SerializeToNode(Toml.ToModel(original.TrimStart('\uFEFF')));
        var after = JsonSerializer.SerializeToNode(Toml.ToModel(saved.TrimStart('\uFEFF')));
        return ContainsOriginal(before, after);
    }

    // Native clients may reformat settings or add other keys. Report if the previous
    // values were removed or changed, leaving any recovery to the user and their backup.
    private static bool ContainsOriginal(JsonNode? original, JsonNode? saved)
    {
        if (original is JsonObject before && saved is JsonObject after)
            return before.All(property => after.TryGetPropertyValue(property.Key, out var value) && ContainsOriginal(property.Value, value));
        return JsonNode.DeepEquals(original, saved);
    }

    private static async Task WritePrivateAsync(string path, string text, McpConfigFormat format, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(ConfigEncoding(format).GetBytes(text), ct);
        await stream.FlushAsync(ct);
    }
}

internal static class JavaProperties
{
    private sealed record Property(string Value, int Start, int End, string Newline);

    internal static string? Get(string text, string key) => Find(text, key)?.Value;

    internal static string Set(string text, string key, string value)
    {
        var property = Find(text, key);
        var replacement = key + "=" + (value.StartsWith(' ') ? "\\" : "") + Escape(value);
        if (property is not null)
            return text[..property.Start] + replacement + property.Newline + text[property.End..];

        var firstTerminator = text.AsSpan().IndexOfAny('\r', '\n');
        var newline = firstTerminator < 0 ? "\n" : LineEnding(text, firstTerminator);
        var separator = text.Length == 0 || text[^1] is '\r' or '\n' ? "" : newline;
        return text + separator + replacement + newline;
    }

    private static Property? Find(string text, string key)
    {
        Property? match = null;
        var offset = 0;
        while (offset < text.Length)
        {
            var start = offset;
            var line = ReadLine(text, ref offset, out var newline);
            if (line.IsEmpty || line[0] is '#' or '!') continue;

            var logical = new StringBuilder();
            while (HasContinuation(line))
            {
                // Appending a new property to an unfinished continuation could absorb it.
                if (offset == text.Length)
                    throw new InvalidDataException("Eclipse preferences end with an unfinished continuation.");
                logical.Append(line[..^1]);
                line = ReadLine(text, ref offset, out newline);
                if (logical.Length == 0 && (line.IsEmpty || line[0] is '#' or '!')) break;
            }
            if (logical.Length == 0 && (line.IsEmpty || line[0] is '#' or '!')) continue;
            logical.Append(line);
            var (name, value) = SplitProperty(logical.ToString());
            if (name != key) continue;
            if (match is not null) throw new InvalidDataException($"Duplicate Eclipse preference: {key}.");
            match = new(value, start, offset, newline);
        }
        return match;
    }

    private static ReadOnlySpan<char> ReadLine(string text, ref int offset, out string newline)
    {
        var remaining = text.AsSpan(offset);
        var length = remaining.IndexOfAny('\r', '\n');
        if (length < 0) length = remaining.Length;
        newline = LineEnding(text, offset + length);
        offset += length + newline.Length;
        return remaining[..length].TrimStart(" \t\f".AsSpan());
    }

    private static string LineEnding(string text, int index)
    {
        if (index == text.Length) return "";
        if (text[index] == '\n') return "\n";
        return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }

    private static bool HasContinuation(ReadOnlySpan<char> line)
    {
        var backslashes = 0;
        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) backslashes++;
        return backslashes % 2 != 0;
    }

    private static (string Key, string Value) SplitProperty(string line)
    {
        var keyEnd = 0;
        while (keyEnd < line.Length)
        {
            var c = line[keyEnd];
            if (c is ' ' or '\t' or '\f' or '=' or ':') break;
            keyEnd += c == '\\' && keyEnd + 1 < line.Length ? 2 : 1;
        }
        var value = line.AsSpan(keyEnd).TrimStart(" \t\f".AsSpan());
        if (!value.IsEmpty && value[0] is '=' or ':')
            value = value[1..].TrimStart(" \t\f".AsSpan());
        return (Unescape(line.AsSpan(0, keyEnd)), Unescape(value));
    }

    private static string Unescape(ReadOnlySpan<char> value)
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
                if (i + 4 >= value.Length ||
                    value.Slice(i + 1, 4).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0 ||
                    !ushort.TryParse(value.Slice(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    throw new InvalidDataException("Invalid Unicode escape in Eclipse preferences.");
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
        > '\u007e' or < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
        _ => c.ToString()
    }));
}

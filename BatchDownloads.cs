using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed record BatchDownloadRequest(
    int Index,
    long ChatId,
    long MessageId,
    string? Type,
    string? AttachmentId = null);

internal sealed record BatchDownloadResult(
    int Index,
    long ChatId,
    long MessageId,
    int? FileId,
    string? Path,
    string? Error,
    string Status = "downloaded",
    long? SizeBytes = null)
{
    public bool Ok => Error is null;
}

internal static class BatchDownloads
{
    private const string ReceiptDirectory = ".tgcli-downloads";
    private const string StagePrefix = ".tgcli-batch-";
    private const int MaximumReceiptBytes = 16 * 1024;

    private sealed record Receipt(string Name, long Size, string Sha256, string? UniqueIdentity, string? RemoteIdentity);

    public static IReadOnlyList<BatchDownloadRequest> Read(TextReader reader, string? defaultType)
    {
        var requests = new List<BatchDownloadRequest>();
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JObject value;
            try
            {
                value = JObject.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new ValidationException($"Batch input line {lineNumber} is not valid JSON.", ex);
            }

            var chatId = ReadRequiredInt64(value, "chat_id", lineNumber);
            var messageId = ReadRequiredInt64(value, "message_id", lineNumber);
            var rowType = ReadOptionalText(value, "type", lineNumber);
            var attachmentId = NormalizeAttachmentId(ReadOptionalText(value, "attachment_id", lineNumber, allowInteger: true));
            var fileId = NormalizeAttachmentId(ReadOptionalText(value, "file_id", lineNumber, allowInteger: true));
            if (attachmentId is not null && fileId is not null && attachmentId != fileId)
                throw new ValidationException($"Batch input line {lineNumber} has conflicting attachment identifiers.");
            attachmentId ??= fileId;
            var type = NormalizeType(string.IsNullOrWhiteSpace(defaultType) ? rowType : defaultType);
            AttachmentDownloads.Validate(type, attachmentId, chatId, messageId);
            requests.Add(new BatchDownloadRequest(requests.Count, chatId, messageId, type, attachmentId));
        }

        if (requests.Count == 0)
        {
            throw new ValidationException("Batch input contains no download records.");
        }

        return requests;
    }

    public static async Task<IReadOnlyList<BatchDownloadResult>> RunAsync(
        IReadOnlyList<BatchDownloadRequest> requests,
        string outputDirectory,
        int parallel,
        Func<BatchDownloadRequest, CancellationToken, Task<TdApi.File>> resolve,
        Func<BatchDownloadRequest, TdApi.File, CancellationToken, Task<TdApi.File>> complete,
        CancellationToken cancellationToken)
    {
        var receiptsDirectory = Directory.CreateDirectory(Path.Combine(outputDirectory, ReceiptDirectory)).FullName;
        using var lease = new FileStream(Path.Combine(receiptsDirectory, "batch.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var path in Directory.EnumerateFiles(outputDirectory, StagePrefix + "*.tmp")
            .Concat(Directory.EnumerateFiles(receiptsDirectory, StagePrefix + "*.tmp")))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (Guid.TryParseExact(name.AsSpan(StagePrefix.Length), "N", out _))
                DeleteTemporary(path);
        }

        var results = new BatchDownloadResult[requests.Count];
        var downloads = new ConcurrentDictionary<string, Lazy<Task<BatchDownloadResult>>>(StringComparer.Ordinal);
        var selections = requests.GroupBy(request => (request.ChatId, request.MessageId,
            Type: NormalizeType(request.Type), Attachment: NormalizeAttachmentId(request.AttachmentId)));
        try
        {
            await Parallel.ForEachAsync(
                selections,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = ValidateParallelism(parallel),
                    CancellationToken = cancellationToken
                },
                async (selection, token) =>
                {
                    var request = selection.First();
                    var result = await RunSelectionAsync(request, outputDirectory, receiptsDirectory, resolve, complete, downloads, token);
                    foreach (var row in selection)
                        results[row.Index] = result with { Index = row.Index };
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Already completed items keep their receipts. Unscheduled items receive a canceled outcome.
        }

        var published = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var result = results[request.Index] ?? new(request.Index, request.ChatId, request.MessageId,
                null, null, "Canceled before download started.", "canceled");
            if (result.Status == "downloaded" && !published.Add(result.Path!))
                result = result with { Status = "skipped" };
            results[request.Index] = result;
        }

        return results;
    }

    private static async Task<BatchDownloadResult> RunSelectionAsync(
        BatchDownloadRequest request,
        string outputDirectory,
        string receiptsDirectory,
        Func<BatchDownloadRequest, CancellationToken, Task<TdApi.File>> resolve,
        Func<BatchDownloadRequest, TdApi.File, CancellationToken, Task<TdApi.File>> complete,
        ConcurrentDictionary<string, Lazy<Task<BatchDownloadResult>>> downloads,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await resolve(request, cancellationToken);
            var prefix = AttachmentPrefix(request, file);
            var download = downloads.GetOrAdd(prefix, _ => new Lazy<Task<BatchDownloadResult>>(() =>
                DownloadAsync(request, file, prefix, outputDirectory, receiptsDirectory, complete, cancellationToken)));
            return await download.Value;
        }
        catch (OperationCanceledException)
        {
            return new(request.Index, request.ChatId, request.MessageId, null, null, "Download canceled.", "canceled");
        }
        catch (Exception ex)
        {
            return new(request.Index, request.ChatId, request.MessageId, null, null, ex.Message, "error");
        }
    }

    private static async Task<BatchDownloadResult> DownloadAsync(
        BatchDownloadRequest request,
        TdApi.File file,
        string prefix,
        string outputDirectory,
        string receiptsDirectory,
        Func<BatchDownloadRequest, TdApi.File, CancellationToken, Task<TdApi.File>> complete,
        CancellationToken cancellationToken)
    {
        string? destination = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(file.Local?.Path))
                destination = ResolveDestination(outputDirectory, prefix, file);

            var receiptPath = Path.Combine(receiptsDirectory, prefix + ".json");
            var receipt = await VerifyReceiptAsync(receiptPath, outputDirectory, prefix, file, cancellationToken);
            if (receipt is not null)
            {
                return new(request.Index, request.ChatId, request.MessageId, file.Id,
                    Path.Combine(outputDirectory, receipt.Name), null, "skipped", receipt.Size);
            }

            file = await complete(request, file, cancellationToken);
            destination = ResolveDestination(outputDirectory, prefix, file);
            using var stage = await AtomicDownload.StageAsync(file, destination, cancellationToken, StagePrefix);
            var next = new Receipt(Path.GetFileName(destination), stage.Size, stage.Sha256,
                HashIdentity(file.Remote?.UniqueId), HashIdentity(file.Remote?.Id));

            // The durable intent can be reused after a crash only when the final file matches it.
            await WriteReceiptAsync(receiptPath, next, cancellationToken);
            stage.Promote(cancellationToken);
            return new(request.Index, request.ChatId, request.MessageId, file.Id, destination, null, "downloaded", stage.Size);
        }
        catch (OperationCanceledException)
        {
            return new(request.Index, request.ChatId, request.MessageId, file.Id, destination, "Download canceled.", "canceled");
        }
        catch (Exception ex)
        {
            return new(request.Index, request.ChatId, request.MessageId, file.Id, destination, ex.Message, "error");
        }
    }

    private static async Task<Receipt?> VerifyReceiptAsync(
        string path, string outputDirectory, string prefix, TdApi.File file, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumReceiptBytes)
                return null;
            var receipt = JsonConvert.DeserializeObject<Receipt>(await File.ReadAllTextAsync(path, cancellationToken));
            if (receipt is null
                || string.IsNullOrWhiteSpace(receipt.Name)
                || Path.GetFileName(receipt.Name) != receipt.Name
                || !receipt.Name.StartsWith(prefix + "_", StringComparison.Ordinal)
                || !MatchesIdentity(receipt, file)
                || file.Size > 0 && receipt.Size != file.Size)
            {
                return null;
            }

            var destination = Path.Combine(outputDirectory, receipt.Name);
            await using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            if (stream.Length != receipt.Size)
                return null;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            return hash.Equals(receipt.Sha256, StringComparison.Ordinal) ? receipt : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteReceiptAsync(string path, Receipt receipt, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"{StagePrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(receipt, Formatting.None));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private static bool MatchesIdentity(Receipt receipt, TdApi.File file)
    {
        var unique = HashIdentity(file.Remote?.UniqueId);
        if (unique is not null && receipt.UniqueIdentity is not null)
            return unique == receipt.UniqueIdentity;
        var remote = HashIdentity(file.Remote?.Id);
        return remote is not null && remote == receipt.RemoteIdentity;
    }

    private static string? HashIdentity(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string AttachmentPrefix(BatchDownloadRequest request, TdApi.File file)
    {
        var unique = HashIdentity(file.Remote?.UniqueId);
        var remote = HashIdentity(file.Remote?.Id);
        var identity = unique is not null ? "u" + unique
            : remote is not null ? "r" + remote
            : FormattableString.Invariant($"i{file.Id}");
        return FormattableString.Invariant($"{request.ChatId}_{request.MessageId}_{identity}");
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static int ValidateParallelism(int parallel)
    {
        if (parallel is < 1 or > 32)
        {
            throw new ValidationException("--parallel must be between 1 and 32.");
        }

        return parallel;
    }

    public static string ResolveDestination(string outputDirectory, BatchDownloadRequest request, TdApi.File file) =>
        ResolveDestination(outputDirectory, AttachmentPrefix(request, file), file);

    private static string ResolveDestination(string outputDirectory, string prefix, TdApi.File file)
    {
        var sourceName = Path.GetFileName(file.Local?.Path);
        if (string.IsNullOrWhiteSpace(sourceName))
            sourceName = $"telegram-file-{file.Id}";

        const int maximumNameBytes = 240;
        var remaining = maximumNameBytes - prefix.Length - 1;
        if (Encoding.UTF8.GetByteCount(sourceName) > remaining)
        {
            var extension = Path.GetExtension(sourceName);
            if (Encoding.UTF8.GetByteCount(extension) > 16)
                extension = string.Empty;
            remaining -= Encoding.UTF8.GetByteCount(extension);
            var name = new StringBuilder();
            foreach (var character in Path.GetFileNameWithoutExtension(sourceName).EnumerateRunes())
            {
                remaining -= character.Utf8SequenceLength;
                if (remaining < 0)
                    break;
                name.Append(character.ToString());
            }
            sourceName = name.Append(extension).ToString();
        }

        return Path.Combine(Path.GetFullPath(outputDirectory), $"{prefix}_{sourceName}");
    }

    private static string? NormalizeType(string? type)
    {
        var kind = AttachmentKinds.Parse(type ?? "file", allowThumbnail: true);
        return kind is AttachmentKind.All or AttachmentKind.File ? null : AttachmentKinds.CanonicalName(kind);
    }

    private static string? NormalizeAttachmentId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return int.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var fileId)
            ? fileId.ToString(CultureInfo.InvariantCulture)
            : id;
    }

    public static JObject ToJson(BatchDownloadResult result)
    {
        return new JObject
        {
            ["index"] = result.Index,
            ["chat_id"] = Wire.Id(result.ChatId),
            ["message_id"] = Wire.Id(result.MessageId),
            ["ok"] = result.Ok,
            ["status"] = result.Status,
            ["size_bytes"] = result.SizeBytes,
            ["file_id"] = Wire.NullableId(result.FileId),
            ["path"] = result.Path is null ? JValue.CreateNull() : result.Path,
            ["error"] = result.Error is null ? JValue.CreateNull() : result.Error
        };
    }

    private static string? ReadOptionalText(JObject value, string property, int lineNumber, bool allowInteger = false)
    {
        var token = value[property];
        return token?.Type switch
        {
            null or JTokenType.Null => null,
            JTokenType.String => token.Value<string>()?.Trim(),
            JTokenType.Integer when allowInteger => token.ToString(Formatting.None),
            _ => throw new ValidationException($"Batch input line {lineNumber} has an invalid {property}.")
        };
    }

    private static long ReadRequiredInt64(JObject value, string property, int lineNumber)
    {
        var token = value[property];
        var text = token?.Type switch
        {
            JTokenType.Integer => token.ToString(Newtonsoft.Json.Formatting.None),
            JTokenType.String => token.Value<string>(),
            _ => null
        };
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            throw new ValidationException($"Batch input line {lineNumber} must contain nonzero integer {property}.");
        }

        return id;
    }
}

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
            var attachmentId = ReadOptionalText(value, "attachment_id", lineNumber, allowInteger: true);
            var fileId = ReadOptionalText(value, "file_id", lineNumber, allowInteger: true);
            if (attachmentId is not null && fileId is not null && attachmentId != fileId)
                throw new ValidationException($"Batch input line {lineNumber} has conflicting attachment identifiers.");
            attachmentId ??= fileId;
            var type = string.IsNullOrWhiteSpace(defaultType) ? rowType : defaultType;
            if (string.IsNullOrWhiteSpace(type))
            {
                type = null;
            }
            else
            {
                type = AttachmentKinds.CanonicalName(AttachmentKinds.Parse(type, allowThumbnail: true));
            }

            AttachmentDownloads.Validate(type, attachmentId, chatId, messageId);
            requests.Add(new BatchDownloadRequest(requests.Count, chatId, messageId, type, attachmentId));
        }

        if (requests.Count == 0)
        {
            throw new ValidationException("Batch input contains no download records.");
        }

        var duplicate = requests
            .GroupBy(x => (x.ChatId, x.MessageId))
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            throw new ValidationException(
                $"Batch input contains duplicate record {duplicate.Key.ChatId}/{duplicate.Key.MessageId}; use one attachment selector per message.");
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
        try
        {
            await Parallel.ForEachAsync(
                requests,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = ValidateParallelism(parallel),
                    CancellationToken = cancellationToken
                },
                async (request, token) =>
                {
                    results[request.Index] = await RunItemAsync(request, outputDirectory, receiptsDirectory, resolve, complete, token);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Already completed items keep their receipts. Unscheduled items receive a canceled outcome.
        }

        foreach (var request in requests)
        {
            results[request.Index] ??= new(request.Index, request.ChatId, request.MessageId,
                null, null, "Canceled before download started.", "canceled");
        }

        return results;
    }

    private static async Task<BatchDownloadResult> RunItemAsync(
        BatchDownloadRequest request,
        string outputDirectory,
        string receiptsDirectory,
        Func<BatchDownloadRequest, CancellationToken, Task<TdApi.File>> resolve,
        Func<BatchDownloadRequest, TdApi.File, CancellationToken, Task<TdApi.File>> complete,
        CancellationToken cancellationToken)
    {
        TdApi.File? file = null;
        string? destination = null;
        try
        {
            file = await resolve(request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(file.Local?.Path))
                destination = ResolveDestination(outputDirectory, request, file);

            var receiptPath = Path.Combine(receiptsDirectory, RequestPrefix(request) + ".json");
            var receipt = await VerifyReceiptAsync(receiptPath, outputDirectory, request, file, cancellationToken);
            if (receipt is not null)
            {
                return new(request.Index, request.ChatId, request.MessageId, file.Id,
                    Path.Combine(outputDirectory, receipt.Name), null, "skipped", receipt.Size);
            }

            file = await complete(request, file, cancellationToken);
            destination = ResolveDestination(outputDirectory, request, file);
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
            return new(request.Index, request.ChatId, request.MessageId, file?.Id, destination, "Download canceled.", "canceled");
        }
        catch (Exception ex)
        {
            return new(request.Index, request.ChatId, request.MessageId, file?.Id, destination, ex.Message, "error");
        }
    }

    private static async Task<Receipt?> VerifyReceiptAsync(
        string path, string outputDirectory, BatchDownloadRequest request, TdApi.File file, CancellationToken cancellationToken)
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
                || !receipt.Name.StartsWith(RequestPrefix(request) + "_", StringComparison.Ordinal)
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

    private static string RequestPrefix(BatchDownloadRequest request) =>
        FormattableString.Invariant($"{request.ChatId}_{request.MessageId}");

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

    public static string ResolveDestination(string outputDirectory, BatchDownloadRequest request, TdApi.File file)
    {
        var directory = Path.GetFullPath(outputDirectory);
        var sourceName = Path.GetFileName(file.Local?.Path);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = $"telegram-file-{file.Id}";
        }

        return Path.Combine(directory, $"{RequestPrefix(request)}_{sourceName}");
    }

    public static JObject ToJson(BatchDownloadResult result)
    {
        return new JObject
        {
            ["index"] = result.Index,
            ["chat_id"] = result.ChatId,
            ["message_id"] = result.MessageId,
            ["ok"] = result.Ok,
            ["status"] = result.Status,
            ["size_bytes"] = result.SizeBytes,
            ["file_id"] = result.FileId is null ? JValue.CreateNull() : result.FileId.Value,
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

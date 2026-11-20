using System.Buffers;
using System.Security.Cryptography;
using TdLib;

namespace TgCli;

internal sealed class AtomicDownload(string temporaryPath, string destination, long size, string sha256) : IDisposable
{
    public long Size { get; } = size;
    public string Sha256 { get; } = sha256;

    public static string? TryGetCompletedPath(TdApi.File file)
    {
        if (file.Local is not { IsDownloadingCompleted: true, Path: { Length: > 0 } path })
            return null;
        var info = new FileInfo(path);
        return info.Exists && (file.Size <= 0 || info.Length == file.Size) ? info.FullName : null;
    }

    public static string GetCompletedPath(TdApi.File file) => TryGetCompletedPath(file)
        ?? throw new IOException($"Telegram did not provide a complete local file for file_id={file.Id}.");

    public static async Task<AtomicDownload> StageAsync(TdApi.File file, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = GetCompletedPath(file);
        destination = Path.GetFullPath(destination);
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(destination)!).FullName;
        var temporaryPath = Path.Combine(directory, $".tgcli-download-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            await using var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            var length = source.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            long copied = 0;
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    copied += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (copied != length || (file.Size > 0 && copied != file.Size))
                throw new IOException("The downloaded source file changed during copying.");
            await target.FlushAsync(cancellationToken);
            target.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            return new(temporaryPath, destination, copied, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch
        {
            DeleteTemporary(temporaryPath);
            throw;
        }
    }

    public void Promote(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporaryPath, destination, overwrite: true);
    }

    public void Dispose() => DeleteTemporary(temporaryPath);

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

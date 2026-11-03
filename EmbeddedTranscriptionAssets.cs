using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TgCli;

internal sealed class EmbeddedTranscriptionAssets
{
    private const string Prefix = "TgCli.Transcription.Assets.";
    private readonly Assembly assembly = typeof(EmbeddedTranscriptionAssets).Assembly;
    private static readonly Lazy<EmbeddedTranscriptionAssets> Instance = new(() => new());
    public static EmbeddedTranscriptionAssets Default => Instance.Value;
    public string DirectoryPath { get; }
    public string DecoderPath => Path.Combine(DirectoryPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    private EmbeddedTranscriptionAssets()
    {
        using var manifest = assembly.GetManifestResourceStream(Prefix + "manifest.json")
            ?? throw new InvalidOperationException("This build has no embedded transcription assets. Run python tools/prepare_transcription.py --rid <runtime>, then rebuild tgcli. Release binaries include the model and decoder.");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)
            ?? throw new InvalidOperationException("Invalid embedded transcription manifest.");
        DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "tgcli", "transcription", assembly.ManifestModule.ModuleVersionId.ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var resourceNames = assembly.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), StringComparer.Ordinal);
        foreach (var (relativePath, sha256) in files)
        {
            var relative = relativePath.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or ""))
                throw new InvalidOperationException("Invalid path in embedded transcription manifest.");
            var destination = Path.Combine(DirectoryPath, relative);
            var source = resourceNames.TryGetValue(Prefix + relative, out var resourceName) ? assembly.GetManifestResourceStream(resourceName) : null;
            using var verifiedSource = source
                ?? throw new InvalidOperationException($"Embedded transcription asset '{relative}' is missing.");
            ExtractVerified(verifiedSource, destination, sha256);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DecoderPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    internal static void ExtractVerified(Stream source, string destination, string expectedHash)
    {
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (Convert.ToHexString(SHA256.HashData(existing)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(file);
            using (var file = File.OpenRead(temporary))
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Embedded transcription asset failed checksum verification.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public byte[] ReadModel()
    {
        using var model = assembly.GetManifestResourceStream("TgCli.Transcription.Model")
            ?? throw new InvalidOperationException("The Whisper model is missing from this build. Prepare transcription assets and rebuild.");
        var bytes = new byte[checked((int)model.Length)];
        model.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals("60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The embedded Whisper model failed checksum verification.");
        return bytes;
    }
}

using System.Reflection;
using TdLib;

namespace TgCli;

internal sealed record MessageFile(
    string Kind,
    int FileId,
    string? RemoteId,
    string? UniqueId,
    string? FileName,
    string? MimeType,
    int? Width,
    int? Height,
    int? Duration,
    long? Size,
    string PropertyPath,
    TdApi.File File);

internal static class MessageFiles
{
    public static MessageFile? GetPrimaryAttachment(TdApi.MessageContent? content, string? requestedType = null)
    {
        var requestedKind = string.IsNullOrWhiteSpace(requestedType) ? AttachmentKind.File : AttachmentKinds.Parse(requestedType);

        return content switch
        {
            TdApi.MessageContent.MessageVoiceNote voice when Matches(requestedKind, AttachmentKind.Voice, AttachmentKind.File) =>
                FromFile("voice", voice.VoiceNote?.Voice, null, null, null, null, voice.VoiceNote?.Duration, null, "content.voice_note.voice"),
            TdApi.MessageContent.MessageAudio audio when Matches(requestedKind, AttachmentKind.Audio, AttachmentKind.File) =>
                FromFile("audio", audio.Audio?.Audio_, audio.Audio?.FileName, audio.Audio?.MimeType, null, null, audio.Audio?.Duration, null, "content.audio.audio"),
            TdApi.MessageContent.MessageDocument document when Matches(requestedKind, AttachmentKind.Document, AttachmentKind.File) =>
                FromFile("document", document.Document?.Document_, document.Document?.FileName, document.Document?.MimeType, null, null, null, null, "content.document.document"),
            TdApi.MessageContent.MessagePhoto photo when Matches(requestedKind, AttachmentKind.Photo, AttachmentKind.File) =>
                FromPhoto(photo),
            TdApi.MessageContent.MessageVideo video when Matches(requestedKind, AttachmentKind.Video, AttachmentKind.File) =>
                FromFile("video", video.Video?.Video_, video.Video?.FileName, video.Video?.MimeType, video.Video?.Width, video.Video?.Height, video.Video?.Duration, null, "content.video.video"),
            TdApi.MessageContent.MessageVideoNote videoNote when Matches(requestedKind, AttachmentKind.VideoNote, AttachmentKind.File) =>
                FromFile("video-note", videoNote.VideoNote?.Video, null, "video/mp4", videoNote.VideoNote?.Length, videoNote.VideoNote?.Length, videoNote.VideoNote?.Duration, null, "content.video_note.video"),
            TdApi.MessageContent.MessageAnimation animation when Matches(requestedKind, AttachmentKind.Animation, AttachmentKind.File) =>
                FromFile("animation", animation.Animation?.Animation_, animation.Animation?.FileName, animation.Animation?.MimeType, animation.Animation?.Width, animation.Animation?.Height, animation.Animation?.Duration, null, "content.animation.animation"),
            _ => null
        };
    }

    public static IReadOnlyList<MessageFile> GetAllFiles(TdApi.Message message)
    {
        var files = new List<MessageFile>();
        var seenObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var seenFiles = new HashSet<int>();
        CollectFiles(message.Content, "content", files, seenObjects, seenFiles);
        return files;
    }

    public static string GetMessageText(TdApi.MessageContent? content)
    {
        return content switch
        {
            TdApi.MessageContent.MessageText text => text.Text?.Text ?? string.Empty,
            TdApi.MessageContent.MessageVoiceNote voice => voice.Caption?.Text ?? string.Empty,
            TdApi.MessageContent.MessageAudio audio => FirstNonEmpty(audio.Caption?.Text, audio.Audio?.Title, audio.Audio?.FileName),
            TdApi.MessageContent.MessageDocument document => FirstNonEmpty(document.Caption?.Text, document.Document?.FileName),
            TdApi.MessageContent.MessagePhoto photo => photo.Caption?.Text ?? string.Empty,
            TdApi.MessageContent.MessageVideo video => FirstNonEmpty(video.Caption?.Text, video.Video?.FileName),
            TdApi.MessageContent.MessageVideoNote => string.Empty,
            TdApi.MessageContent.MessageAnimation animation => animation.Caption?.Text ?? string.Empty,
            _ => string.Empty
        };
    }

    public static string GetKind(TdApi.MessageContent? content)
    {
        return GetPrimaryAttachment(content)?.Kind
            ?? content switch
            {
                TdApi.MessageContent.MessageText => "text",
                null => "unknown",
                _ => content.GetType().Name.Replace("Message", string.Empty).ToLowerInvariant()
            };
    }

    private static MessageFile? FromPhoto(TdApi.MessageContent.MessagePhoto photo)
    {
        var size = photo.Photo?.Sizes?
            .OrderByDescending(x => (long)x.Width * x.Height)
            .FirstOrDefault();

        return FromFile("photo", size?.Photo, null, "image/jpeg", size?.Width, size?.Height, null, null, "content.photo.sizes[].photo");
    }

    private static MessageFile? FromFile(
        string kind,
        TdApi.File? file,
        string? fileName,
        string? mimeType,
        int? width,
        int? height,
        int? duration,
        long? size,
        string propertyPath)
    {
        if (file is null)
        {
            return null;
        }

        return new MessageFile(
            kind,
            file.Id,
            file.Remote?.Id,
            file.Remote?.UniqueId,
            fileName,
            mimeType,
            width,
            height,
            duration,
            size ?? file.Size,
            propertyPath,
            file);
    }

    private static bool Matches(AttachmentKind requested, AttachmentKind actual, AttachmentKind fallback)
    {
        return requested == actual || requested == fallback || requested == AttachmentKind.All;
    }

    private static void CollectFiles(
        object? value,
        string path,
        List<MessageFile> files,
        HashSet<object> seenObjects,
        HashSet<int> seenFiles)
    {
        if (value is null || value is string)
        {
            return;
        }

        if (value is TdApi.File file)
        {
            if (seenFiles.Add(file.Id))
            {
                files.Add(new MessageFile(
                    GuessKind(path),
                    file.Id,
                    file.Remote?.Id,
                    file.Remote?.UniqueId,
                    TryGetStringProperty(file, "FileName"),
                    TryGetStringProperty(file, "MimeType"),
                    TryGetIntProperty(file, "Width"),
                    TryGetIntProperty(file, "Height"),
                    TryGetIntProperty(file, "Duration"),
                    file.Size,
                    path,
                    file));
            }

            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal)
        {
            return;
        }

        if (!seenObjects.Add(value))
        {
            return;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                CollectFiles(item, $"{path}[{index++}]", files, seenObjects, seenFiles);
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            CollectFiles(child, $"{path}.{ToSnakeCase(property.Name)}", files, seenObjects, seenFiles);
        }
    }

    private static string GuessKind(string path)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.Contains("voice"))
        {
            return "voice";
        }

        if (normalized.Contains("audio"))
        {
            return "audio";
        }

        if (normalized.Contains("document"))
        {
            return "document";
        }

        if (normalized.Contains("photo"))
        {
            return "photo";
        }

        if (normalized.Contains("video_note"))
        {
            return "video-note";
        }

        if (normalized.Contains("video"))
        {
            return "video";
        }

        if (normalized.Contains("animation"))
        {
            return "animation";
        }

        if (normalized.Contains("thumbnail"))
        {
            return "thumbnail";
        }

        return "file";
    }

    private static string? TryGetStringProperty(object value, string propertyName)
    {
        return value.GetType().GetProperty(propertyName)?.GetValue(value) as string;
    }

    private static int? TryGetIntProperty(object value, string propertyName)
    {
        return value.GetType().GetProperty(propertyName)?.GetValue(value) as int?;
    }

    private static string ToSnakeCase(string value)
    {
        return string.Concat(value.Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }
}

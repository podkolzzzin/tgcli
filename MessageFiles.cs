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
    TdApi.File File,
    string Role = "primary");

internal static class MessageFiles
{
    public static long? GetEffectiveSize(MessageFile file)
    {
        var size = file.Size.GetValueOrDefault();
        if (size <= 0)
        {
            size = file.File.ExpectedSize;
        }

        return size > 0 ? size : null;
    }

    public static MessageFile? GetPrimaryAttachment(TdApi.MessageContent? content, string? requestedType = null)
    {
        var requestedKind = string.IsNullOrWhiteSpace(requestedType) ? AttachmentKind.File : AttachmentKinds.Parse(requestedType);

        return content switch
        {
            TdApi.MessageContent.MessageVoiceNote voice when Matches(requestedKind, AttachmentKind.Voice, AttachmentKind.File) =>
                FromFile("voice", voice.VoiceNote?.Voice, null, voice.VoiceNote?.MimeType, null, null, voice.VoiceNote?.Duration, null, "content.voice_note.voice"),
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
        if (GetPrimaryAttachment(message.Content) is { } primary)
        {
            files.Add(primary);
            seenFiles.Add(primary.FileId);
        }

        CollectFiles(message.Content, null, "content", files, seenObjects, seenFiles);
        return files;
    }

    public static string GetCaption(TdApi.MessageContent? content) => content switch
    {
        TdApi.MessageContent.MessageVoiceNote voice => voice.Caption?.Text ?? string.Empty,
        TdApi.MessageContent.MessageAudio audio => audio.Caption?.Text ?? string.Empty,
        TdApi.MessageContent.MessageDocument document => document.Caption?.Text ?? string.Empty,
        TdApi.MessageContent.MessagePhoto photo => photo.Caption?.Text ?? string.Empty,
        TdApi.MessageContent.MessageVideo video => video.Caption?.Text ?? string.Empty,
        TdApi.MessageContent.MessageAnimation animation => animation.Caption?.Text ?? string.Empty,
        _ => string.Empty
    };

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
            .Where(x => x.Photo is not null)
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
            size ?? (file.Size > 0 ? file.Size : file.ExpectedSize > 0 ? file.ExpectedSize : null),
            propertyPath,
            file);
    }

    private static bool Matches(AttachmentKind requested, AttachmentKind actual, AttachmentKind fallback)
    {
        return requested == actual || requested == fallback || requested == AttachmentKind.All;
    }

    private static void CollectFiles(
        object? value,
        object? parent,
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
                files.Add(DescribeNestedFile(file, parent, path));
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
                CollectFiles(item, value, $"{path}[{index++}]", files, seenObjects, seenFiles);
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

            CollectFiles(child, value, $"{path}.{ToSnakeCase(property.Name)}", files, seenObjects, seenFiles);
        }
    }

    private static MessageFile DescribeNestedFile(TdApi.File file, object? parent, string path) =>
        parent switch
        {
            TdApi.Thumbnail thumbnail => FromFile("thumbnail", file, null, ThumbnailMimeType(thumbnail.Format),
                thumbnail.Width, thumbnail.Height, null, null, path)! with { Role = "thumbnail" },
            TdApi.PhotoSize photo => FromFile("photo", file, null, "image/jpeg",
                photo.Width, photo.Height, null, null, path)! with { Role = "variant" },
            TdApi.Video video => FromFile("video", file, video.FileName, video.MimeType, video.Width,
                video.Height, video.Duration, null, path)! with { Role = "auxiliary" },
            TdApi.Animation animation => FromFile("animation", file, animation.FileName, animation.MimeType,
                animation.Width, animation.Height, animation.Duration, null, path)! with { Role = "auxiliary" },
            TdApi.Audio audio => FromFile("audio", file, audio.FileName, audio.MimeType, null,
                null, audio.Duration, null, path)! with { Role = "auxiliary" },
            TdApi.Document document => FromFile("document", file, document.FileName, document.MimeType,
                null, null, null, null, path)! with { Role = "auxiliary" },
            TdApi.VideoNote videoNote => FromFile("video-note", file, null, "video/mp4",
                videoNote.Length, videoNote.Length, videoNote.Duration, null, path)! with { Role = "auxiliary" },
            TdApi.VoiceNote voiceNote => FromFile("voice", file, null, voiceNote.MimeType, null,
                null, voiceNote.Duration, null, path)! with { Role = "auxiliary" },
            _ => FromFile("file", file, null, null, null, null, null, null, path)! with { Role = "auxiliary" }
        };

    private static string? ThumbnailMimeType(TdApi.ThumbnailFormat? format) => format switch
    {
        TdApi.ThumbnailFormat.ThumbnailFormatJpeg => "image/jpeg",
        TdApi.ThumbnailFormat.ThumbnailFormatPng => "image/png",
        TdApi.ThumbnailFormat.ThumbnailFormatWebp => "image/webp",
        TdApi.ThumbnailFormat.ThumbnailFormatGif => "image/gif",
        TdApi.ThumbnailFormat.ThumbnailFormatMpeg4 => "video/mp4",
        TdApi.ThumbnailFormat.ThumbnailFormatWebm => "video/webm",
        TdApi.ThumbnailFormat.ThumbnailFormatTgs => "application/x-tgsticker",
        _ => null
    };

    private static string ToSnakeCase(string value)
    {
        return string.Concat(value.TrimEnd('_').Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }
}

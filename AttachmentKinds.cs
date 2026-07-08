using TdLib;
using System.ComponentModel.DataAnnotations;

namespace TgCli;

internal enum AttachmentKind
{
    All,
    Voice,
    Document,
    Audio,
    Video,
    Photo,
    Animation,
    VideoNote,
    File
}

internal static class AttachmentKinds
{
    public static AttachmentKind Parse(string value)
    {
        return Normalize(value) switch
        {
            "" or "all" => AttachmentKind.All,
            "voice" or "voice-note" or "voicenote" => AttachmentKind.Voice,
            "attach" or "attachment" or "document" or "doc" => AttachmentKind.Document,
            "audio" or "music" => AttachmentKind.Audio,
            "video" => AttachmentKind.Video,
            "photo" or "image" or "pic" or "picture" => AttachmentKind.Photo,
            "animation" or "gif" => AttachmentKind.Animation,
            "video-note" or "videonote" or "round" => AttachmentKind.VideoNote,
            "file" => AttachmentKind.File,
            _ => throw new ValidationException($"Unsupported attachment type '{value}'.")
        };
    }

    public static TdApi.SearchMessagesFilter ToSearchFilter(string value)
    {
        return Parse(value) switch
        {
            AttachmentKind.Voice => new TdApi.SearchMessagesFilter.SearchMessagesFilterVoiceNote(),
            AttachmentKind.Document => new TdApi.SearchMessagesFilter.SearchMessagesFilterDocument(),
            AttachmentKind.Audio => new TdApi.SearchMessagesFilter.SearchMessagesFilterAudio(),
            AttachmentKind.Video => new TdApi.SearchMessagesFilter.SearchMessagesFilterVideo(),
            AttachmentKind.Photo => new TdApi.SearchMessagesFilter.SearchMessagesFilterPhoto(),
            AttachmentKind.Animation => new TdApi.SearchMessagesFilter.SearchMessagesFilterAnimation(),
            AttachmentKind.VideoNote => new TdApi.SearchMessagesFilter.SearchMessagesFilterVideoNote(),
            AttachmentKind.All or AttachmentKind.File => new TdApi.SearchMessagesFilter.SearchMessagesFilterEmpty(),
            _ => new TdApi.SearchMessagesFilter.SearchMessagesFilterEmpty()
        };
    }

    public static TdApi.FileType ToFileType(AttachmentKind kind)
    {
        return kind switch
        {
            AttachmentKind.Voice => new TdApi.FileType.FileTypeVoiceNote(),
            AttachmentKind.Document => new TdApi.FileType.FileTypeDocument(),
            AttachmentKind.Audio => new TdApi.FileType.FileTypeAudio(),
            AttachmentKind.Video => new TdApi.FileType.FileTypeVideo(),
            AttachmentKind.Photo => new TdApi.FileType.FileTypePhoto(),
            AttachmentKind.Animation => new TdApi.FileType.FileTypeAnimation(),
            AttachmentKind.VideoNote => new TdApi.FileType.FileTypeVideoNote(),
            AttachmentKind.All or AttachmentKind.File => new TdApi.FileType.FileTypeUnknown(),
            _ => new TdApi.FileType.FileTypeUnknown()
        };
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant().Replace("_", "-");
    }
}

namespace Orbweaver
{
    // CB-275: what the panel needs to draw and open an audio tile or a file
    // chip. Pure, so every rule is one call from a test. The three seams with
    // the outside world (the gateway, the bytes, the system player) are the
    // ones OpenClawVideo already owns: one transport, one cache, one 32 MiB
    // ceiling for every kind, so there is exactly one place to fake them.
    internal static class OpenClawAttachment
    {
        // "1.2 MB", "840 KB", "12 B" — the way a file manager says it. Null
        // for an unknown size, which draws the name alone rather than a made-up
        // number. Binary units (1024), matching the MB the size notes below
        // already quote against the 32 MiB ceiling.
        internal static string? FormatSize(long? sizeBytes)
        {
            if (sizeBytes is null or < 0) return null;

            var n = sizeBytes.Value;
            if (n < 1024) return $"{n} B";

            // Each unit is promoted when its *rounded* figure would reach the
            // next one, so 1,048,575 bytes is "1.0 MB" and never "1024 KB".
            var kb = Math.Round(n / 1024.0, MidpointRounding.AwayFromZero);
            if (kb < 1024) return $"{kb:0} KB";

            var mb = Math.Round(n / 1_048_576.0, 1, MidpointRounding.AwayFromZero);
            if (mb < 1024.0) return $"{mb:0.0} MB";

            return $"{n / 1_073_741_824.0:0.0} GB";
        }

        // The extension each mime type is written under. A document's open
        // name takes its extension from HERE and never from the label: on
        // Windows OpenClawMedia.Open does Process.Start with UseShellExecute,
        // so a label ending .exe/.bat/.cmd/.ps1/.js/.vbs/.scr/.lnk/.url would
        // run rather than open, and the label is untrusted text. An unmapped
        // type is .bin, which opens the OS "choose an app" dialog — the honest
        // answer for a file the app cannot classify.
        private static readonly Dictionary<string, string> MimeExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["application/pdf"] = ".pdf",
                ["application/json"] = ".json",
                ["application/yaml"] = ".yaml",
                ["application/x-yaml"] = ".yaml",
                ["text/yaml"] = ".yaml",
                ["text/csv"] = ".csv",
                ["text/plain"] = ".txt",
                ["text/markdown"] = ".md",
                ["application/zip"] = ".zip",
                ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
                ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
                ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
                ["application/msword"] = ".doc",
                ["application/vnd.ms-excel"] = ".xls",
                ["application/vnd.ms-powerpoint"] = ".ppt",
                ["video/mp4"] = ".mp4",
                ["video/quicktime"] = ".mov",
                ["video/webm"] = ".webm",
                ["video/x-m4v"] = ".m4v",
                ["audio/mp4"] = ".m4a",
                ["audio/x-m4a"] = ".m4a",
                ["audio/mpeg"] = ".mp3",
                ["audio/ogg"] = ".ogg",
                ["audio/wav"] = ".wav",
                ["audio/x-wav"] = ".wav",
            };

        internal static string ExtensionFor(string? mimeType) =>
            mimeType is not null && MimeExtensions.TryGetValue(mimeType.Split(';')[0].Trim(), out var ext)
                ? ext
                : ".bin";

        // The name a document is written under: the label's stem (path
        // separators stripped, its own extension dropped) plus the mime-derived
        // extension. A label with no usable stem becomes "file".
        internal static string OpenName(ChatAttachment attachment) => StemOf(attachment.Label, "file")
            + ExtensionFor(attachment.MimeType);

        // A delivered clip's name: the label's stem and the mime's extension,
        // never the label's own (see the table above). An unmapped video type
        // is .mp4, the overwhelmingly common one, rather than .bin: every
        // video mime the gateway can name plays in the system player.
        internal static string VideoOpenName(ChatAttachment attachment)
        {
            var ext = ExtensionFor(attachment.MimeType);
            return StemOf(attachment.Label, "video") + (OpenClawVideo.HasVideoExtension(ext) ? ext : ".mp4");
        }

        // Audio keeps the label's own name when it already ends in an audio
        // extension (that is the player's cue and it is on the safe list);
        // otherwise the mime decides, as for a document, and a nameless note is
        // "audio" + extension.
        internal static string AudioOpenName(ChatAttachment attachment)
        {
            var label = attachment.Label;
            if (!string.IsNullOrWhiteSpace(label) && OpenClawAudio.HasAudioExtension(label))
                return StemOf(label, "audio") + Path.GetExtension(label).ToLowerInvariant();

            return StemOf(label, "audio") + ExtensionFor(attachment.MimeType);
        }

        private static string StemOf(string? label, string fallback)
        {
            var name = label ?? "";
            name = name[(name.LastIndexOfAny(new[] { '/', '\\' }) + 1)..].Trim();

            var dot = name.LastIndexOf('.');
            var stem = dot > 0 ? name[..dot] : name;

            // A stem that is only dots or empty after reduction is no name.
            stem = stem.Trim('.', ' ');
            return stem.Length == 0 ? fallback : stem;
        }

        // ---- notes, by kind ----

        private static string Noun(AttachmentKind kind) => kind switch
        {
            AttachmentKind.Audio => "Audio",
            AttachmentKind.Video => "Video",
            AttachmentKind.Image => "Picture",
            _ => "File"
        };

        internal static string TooLargeNote(AttachmentKind kind, long sizeBytes)
        {
            var megabytes = (sizeBytes + 1_048_575) / 1_048_576;
            return $"{Noun(kind)} not opened — it is {megabytes} MB and this app fetches at most "
                   + $"{OpenClawVideo.MaxOpenBytes / 1_048_576} MB. Open it on the gateway machine instead.";
        }

        internal static string NotSentNote(AttachmentKind kind) =>
            $"{Noun(kind)} not opened — the gateway didn't send it.";

        internal static string TruncatedNote(AttachmentKind kind) =>
            $"{Noun(kind)} not opened — it came back incomplete (over "
            + $"{OpenClawVideo.MaxOpenBytes / 1_048_576} MB).";

        internal static bool TooLargeToOpen(ChatAttachment? attachment) =>
            OpenClawVideo.TooLargeToOpen(attachment);
    }
}

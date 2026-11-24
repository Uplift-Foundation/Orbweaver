namespace Orbweaver
{
    // A Paperclip agent's picture as the drawing code wants it (CB-445): decoded
    // through the same cache the persona pictures use, keyed by the cached file's
    // own path. That path is a hash of Paperclip's url for the picture, and
    // Paperclip gives every upload a new url, so a changed picture is a new key
    // and the old frames are simply never asked for again.
    internal static class PaperclipAvatars
    {
        internal static string Key(string file) => "paperclip:" + file;

        internal static OpenClawAvatars.Avatar? For(string? file) =>
            string.IsNullOrEmpty(file) ? null : OpenClawAvatars.ForFile(Key(file), file);
    }
}

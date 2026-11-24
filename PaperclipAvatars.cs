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

        // A null decode is forgotten, not kept: OpenClawAvatars remembers a failed read for its key for
        // good, and a kept picture that was locked or half-written for one tick would otherwise draw
        // initials until restart (the CB-148 shape). The retry is one small file read per scan.
        internal static void Forget(string file) => OpenClawAvatars.Forget(Key(file));

        internal static OpenClawAvatars.Avatar? For(string? file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var avatar = OpenClawAvatars.ForFile(Key(file), file);
            if (avatar is null) OpenClawAvatars.Forget(Key(file));
            return avatar;
        }
    }
}

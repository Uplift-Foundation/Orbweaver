namespace Orbweaver
{
    // A picture sent with a native session's message (CB-270 Stage 4), in the shape
    // the gateway's chat.send takes. Measured on 2026.10.5: an image with real
    // content is accepted (the user turn then carries it under __openclaw.media,
    // not in its content), and one with an empty payload is refused with
    // "empty payload", which is how the first result is known to mean the
    // attachment was parsed and not ignored.
    //
    // Only images, and only the types the panel's paste path produces. Anything
    // else has no way in: a file the gateway might refuse is not one to offer.
    internal sealed record OpenClawOutgoingImage(string MimeType, string FileName, string Base64)
    {
        // The extensions a pasted or dropped picture arrives with.
        private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp"
        };

        // Null for an empty file, a name with no usable extension or one that is not
        // an image type: there is nothing honest to send.
        internal static OpenClawOutgoingImage? From(string fileName, byte[] bytes)
        {
            if (bytes.Length == 0) return null;

            var name = Path.GetFileName(fileName);
            if (!Types.TryGetValue(Path.GetExtension(name), out var type)) return null;

            return new OpenClawOutgoingImage(type, name, Convert.ToBase64String(bytes));
        }

        // The wire form, in the order the probe sent it.
        internal Dictionary<string, object> ToWire() => new()
        {
            ["type"] = "image",
            ["mimeType"] = MimeType,
            ["fileName"] = FileName,
            ["content"] = Base64
        };

        // Whether a message carrying these can be put on the connection at all. The
        // gateway states the largest frame it accepts at connect; one over it would
        // be dropped without an answer, which a person would meet as a message that
        // never arrived. The headroom covers the rest of the request.
        internal static bool Fits(IEnumerable<OpenClawOutgoingImage> attachments, long maxPayload)
        {
            const int Headroom = 4096;
            long total = Headroom;
            foreach (var a in attachments) total += a.Base64.Length;

            return total <= maxPayload;
        }
    }
}

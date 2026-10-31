namespace Orbweaver
{
    // CB-274 / CB-275: what kind of file an OpenClaw attachment block carried.
    // The gateway's own vocabulary (managed-image-attachments, 2026.10.5) is
    // exactly these four: image, audio, video and document.
    public enum AttachmentKind { Image, Video, Audio, Document }

    // One block off a display-projected chat.history message, already
    // projected by the gateway. In its own dependency-free file for the same
    // reason OpenClawVideoKind.cs is: ChatTurn (RemoteChat.cs) carries one,
    // and tests/TranscriptTests compiles RemoteChat.cs in by hand, so anything
    // RemoteChat.cs names has to be compilable without the rest of the app.
    //
    // **No local path ever lives here.** The gateway strips every private
    // field from a media block before serving history, so a block carries a
    // gateway-relative url or nothing. A turn's ImageSourcePath is set beside
    // this only for a *failed* block, from a path recovered somewhere else.
    public readonly record struct ChatAttachment(
        AttachmentKind Kind,

        // The gateway's outgoing route for the file, fetched verbatim. Null
        // when the gateway failed to prepare it or omitted it.
        string? Url,

        // The bare filename the user sees: fileName / alt / attachment.label.
        string Label,

        string? MimeType,
        long? SizeBytes,
        long? DurationMs,
        int? Width,
        int? Height,

        // attachment_error only: file-not-found, unsupported-format,
        // delivery-failed or invalid-reference; "omitted" is this app's own
        // word for a url the projection removed.
        string? FailureCode)
    {
        public bool Failed => FailureCode is not null;

        public bool Fetchable => Url is not null;

        // m:ss, rounded to the nearest second. Null for an unknown or zero
        // duration, which draws no badge rather than "0:00".
        public string? Duration => FormatDuration(DurationMs);

        public static string? FormatDuration(long? durationMs)
        {
            if (durationMs is not > 0) return null;

            var seconds = (long)Math.Round(durationMs.Value / 1000.0, MidpointRounding.AwayFromZero);
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}

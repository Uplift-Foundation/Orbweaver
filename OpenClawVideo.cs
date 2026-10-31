using System.Text.Json;

namespace Orbweaver
{
    // CB-272: a video an OpenClaw agent posts, shown in the chat panel as a
    // tile rather than played inline.
    //
    // The decision was made before this file existed and is not reopened here:
    // the panel draws a clickable tile sized to the clip, with a play glyph and
    // its duration, and a click fetches the bytes and hands them to the OS's
    // default player through OpenClawMedia.Open. Inline playback would mean
    // shipping a video decoder into a menu-bar app, and the system player does
    // everything a viewer needs already — the same argument OpenClawMedia makes
    // for pictures.
    //
    // **A separate kind, never a widened image list.** The picture path decodes
    // a bitmap out of whatever bytes it is handed, and Avalonia's decoder does
    // not throw on rubbish (see TurnView.DecodeAtDrawWidth), so video bytes
    // reaching it would not fail loudly — they would produce a plausible garbage
    // bitmap. So a video turn never reaches LoadImageBytes: ChatTurn.IsVideo is
    // what routes it, and OpenClawSessions.ImageExtensions is untouched.
    //
    // Pure where it can be — the extension rule, the meta parse, the duration
    // text and the tile's proportions are all one function call away from a
    // test. The three delegates at the bottom are the only seams with the
    // outside world, and exist so a headless test can drive a tile and a click
    // without a gateway or a real process launch.
    internal static partial class OpenClawVideo
    {
        // The gateway's `&meta=1` answer for a servable clip, e.g.
        //
        //   {"available":true,"mimeType":"video/mp4","sizeBytes":1042813,
        //    "playback":"native","durationMs":12750,"width":544,"height":800,
        //    "mediaTicket":"..."}
        //
        // Every field but `available` is optional: a gateway that cannot probe
        // a clip says so by leaving the numbers out, and the tile then falls
        // back to a default shape and no duration rather than a made-up one.
        internal readonly record struct Meta(
            bool Available, long? DurationMs, int? Width, int? Height, long? SizeBytes);

        // Null when there is no answer at all (the gateway did not reply, or
        // replied with something that is not a JSON object) — distinct from an
        // answer of available:false, so a caller can tell "couldn't ask" from
        // "asked and refused". Both then explain themselves through
        // OpenClawMediaRefusal, which does its own parse of the same text.
        internal static Meta? ParseMeta(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var available = root.TryGetProperty("available", out var a)
                                && a.ValueKind == JsonValueKind.True;

                return new Meta(
                    available,
                    LongOrNull(root, "durationMs"),
                    IntOrNull(root, "width"),
                    IntOrNull(root, "height"),
                    LongOrNull(root, "sizeBytes"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A dimension past int.MaxValue is a malformed answer, not a clip that
        // wide; an unchecked cast would wrap it to a small positive number and
        // draw a wrong-shaped tile instead of the 16:9 default.
        internal static int? IntOrNull(JsonElement obj, string name) =>
            LongOrNull(obj, name) is { } n && n <= int.MaxValue ? (int)n : null;

        internal static long? LongOrNull(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n)
            && n >= 0
                ? n
                : null;

        // m:ss, the way a player shows it — "0:13" for 12.75 seconds, rounded
        // to the nearest second rather than truncated so a 12.75s clip does not
        // read as shorter than the one the player will report. Minutes are not
        // wrapped into hours: a chat clip over an hour is not a case worth a
        // second format, and "75:00" is still true.
        //
        // Null for an unknown or zero duration, which draws no label at all
        // rather than "0:00" claiming a clip with nothing in it.
        internal static string? FormatDuration(long? durationMs) =>
            ChatAttachment.FormatDuration(durationMs);

        // The tile's width, matching the 228 a picture is drawn at so a clip
        // and a picture in the same conversation line up.
        internal const double TileWidth = 228;

        // The tile's height for a clip of this shape, at TileWidth. A portrait
        // 544x800 clip is a tall tile and a 16:9 one a wide, short tile —
        // which is the point of asking the gateway for the dimensions at all —
        // clamped so an extreme clip (a banner, a phone-held sliver) cannot
        // make a tile too thin to carry its glyph or tall enough to push the
        // whole conversation off screen. Unknown dimensions get 16:9, the
        // overwhelmingly common shape, rather than a square that would be
        // wrong for nearly every clip.
        internal static double TileHeight(int? width, int? height)
        {
            const double MinHeight = 96;
            const double MaxHeight = 320;

            if (width is not > 0 || height is not > 0) return TileWidth * 9 / 16;

            var h = TileWidth * height.Value / width.Value;
            return Math.Clamp(h, MinHeight, MaxHeight);
        }

        // ---- the socket's body ceiling ----
        //
        // OpenClawSocket.ReadResponseAsync stops once a body has exceeded
        // MaxBodyBytes and returns what it has rather than null, so for a clip
        // — ordinarily bigger than a picture — a fetch can succeed with a
        // truncated file that QuickTime would then be handed. Two checks stand
        // between that and the player: one before the fetch from the meta's
        // `sizeBytes`, and one after it for a gateway whose meta left it out.
        // The second costs one wasted download before the note appears, which
        // is acceptable for a gateway that did not say how big the clip was.
        internal static long MaxOpenBytes => OpenClawSocket.MaxBodyBytes;

        internal static bool TooLargeToOpen(Meta? meta) => meta?.SizeBytes is > OpenClawSocket.MaxBodyBytes;

        // CB-274: the same guard for whatever the tile was drawn from, a
        // gateway block or a converted meta answer.
        internal static bool TooLargeToOpen(ChatAttachment? attachment) =>
            attachment?.SizeBytes is > OpenClawSocket.MaxBodyBytes;

        // The assistant-media `&meta=1` answer as the one shape the panel
        // draws a clip from. No url: the clip's route is the turn's ImageUrl.
        internal static ChatAttachment FromMeta(Meta meta, string label) =>
            new(AttachmentKind.Video, null, label, null, meta.SizeBytes, meta.DurationMs,
                meta.Width, meta.Height, null);

        internal static bool IsTruncated(int length) => length > MaxOpenBytes;

        internal static string TooLargeNote(long sizeBytes)
        {
            var megabytes = (sizeBytes + 1_048_575) / 1_048_576;
            return $"Video not opened — the clip is {megabytes} MB and this app fetches at most "
                   + $"{MaxOpenBytes / 1_048_576} MB. Open it on the gateway machine instead.";
        }

        internal const string NotSentNote = "Video not opened — the gateway didn't send the clip.";

        internal static readonly string TruncatedNote =
            $"Video not opened — the clip came back incomplete (over {OpenClawSocket.MaxBodyBytes / 1_048_576} MB).";

        // The name the clip is written under for the player. macOS `open` and
        // Windows ShellExecute choose the player by extension, so a nameless
        // clip keeps its own (.mov stays .mov) rather than being called .mp4.
        // OpenClawMedia.SafeName strips anything path-shaped afterwards, as it
        // does for pictures.
        internal static string OpenName(string? alt, string? sourcePath)
        {
            var ext = string.IsNullOrEmpty(sourcePath) ? "" : Path.GetExtension(sourcePath);

            // A name that already ends in a clip extension is what a recovered
            // path's basename is, and is kept. Anything else (a label that
            // reached here some other way) is not trusted to choose how the
            // OS opens the file: ShellExecute runs an .exe.
            if (!string.IsNullOrEmpty(alt) && HasVideoExtension(alt)) return alt;
            if (!string.IsNullOrEmpty(alt)) return Path.GetFileNameWithoutExtension(alt) + ".mp4";

            return "video" + (HasVideoExtension(ext) ? ext : ".mp4");
        }

        // ---- seams with the outside world ----
        //
        // Static and replaceable for the same reason OpenClawSessions' own
        // fetchers are static: TurnView is built by the panel, not handed its
        // collaborators. Tests swap these and put them back; every test that
        // does is in the "Settings" collection, which serialises it, so one
        // test's fake can never be another's gateway.

        internal static Func<string, CancellationToken, Task<string?>> FetchMeta =
            OpenClawSessions.FetchLocalMediaMetaAsync;

        // A clip shares the picture cache (24 entries, oldest out) on purpose:
        // one transport, one cache, one excluded fetcher, and the 32 MiB body
        // ceiling bounds every entry. A clip evicted by later pictures is
        // simply fetched again on the next click.
        internal static Func<string, CancellationToken, Task<byte[]?>> FetchBytes =
            OpenClawSessions.FetchMediaAsync;

        internal static Action<byte[], string> Open = OpenClawMedia.Open;
    }
}

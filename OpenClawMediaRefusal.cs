using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Orbweaver
{
    // CB-93: when the gateway refuses to serve a picture named by
    // `MEDIA:<path>`, it will say why if asked — `&meta=1` on the same
    // assistant-media route answers a capability question instead of bytes:
    //
    //   {"available":true,"mediaTicket":"v1.…","mediaTicketExpiresAt":"…"}
    //   {"available":false,"code":"outside-allowed-folders","reason":"…"}
    //
    // This turns that answer into what the panel shows: a one-line reason in
    // the slot the picture would have occupied, and a tooltip carrying the
    // path and the raw code. Pure, no window, no settings — the same habit
    // as OrbGlyph and OrbArrangement — so the messages are one function call
    // away from a test rather than something only visible on a real gateway.
    //
    // Kept out of OpenClawMedia.cs deliberately: that file is the OS-viewer
    // call (Preview.app), excluded from coverage whole. Nothing here opens
    // anything.
    internal static class OpenClawMediaRefusal
    {
        // The happy-path guard. A meta round trip doubles the request count
        // for a fetch, so it is only worth asking once the plain fetch has
        // already come back empty — never before or instead of it — and only
        // against a route that can actually answer `&meta=1` at all. An
        // ordinary `[media attached: ...]` marker resolves through the same
        // TurnView.LoadImage path but has no meta variant; asking it would be
        // a second wasted request against a route that was never going to
        // explain itself.
        //
        // Gated on AssistantMediaPathPrefix rather than AssistantMediaRoute
        // since CB-109, and the difference is not cosmetic. AssistantMediaRoute
        // ends in `?source=`, so a StartsWith against it was quietly asserting
        // that `source` is the *first* query parameter — which it is, on
        // purpose (see OpenClawMediaSource.Route), but which made this guard
        // depend on parameter order for no reason of its own. Had CB-109 put
        // the identity in front of `source`, this test would have stopped
        // matching and every refusal note in the app would have disappeared
        // with no test failing and no picture visibly breaking. The prefix
        // without the parameter is what this actually wants to know.
        internal static bool ShouldAskWhy(byte[]? bytes, string? url) =>
            (bytes is null || bytes.Length == 0)
            && url is not null
            && url.StartsWith(OpenClawSessions.AssistantMediaPathPrefix, StringComparison.Ordinal);

        private const string Prefix = "Picture not shown — ";

        // CB-272: the same note for a video tile. The sentences below are
        // the gateway's codes put into words and are the same for both kinds
        // except where the word "picture" is itself the claim — see
        // unsupported-media-type — so a clip's note is built from this
        // prefix and a code table with that one row swapped, not from a second
        // parallel Explain.
        private const string VideoPrefix = "Video not shown — ";

        // Cap on a gateway-supplied reason, so a verbose message from the
        // other end doesn't blow out the bubble's width. 200 is generous for
        // a sentence and short of anything that would wrap more than a line
        // or two at the note's own small font size.
        private const int MaxReasonLength = 200;

        // CB-108: the gateway's codes for a media fetch that didn't come
        // back with bytes, most of which are not permission decisions at
        // all — a missing file and a refused folder are different problems
        // and read wrong when both come out as "refused". Kept as data
        // rather than a stack of `if`s off `code` because the shape of
        // "one code, one sentence" is exactly what OpenClawMediaRefusalTests
        // walks row for row: adding a code here is adding a row there, with
        // nothing else in this method to touch.
        //
        // Every code below (like outside-allowed-folders, handled separately
        // below because it alone carries a remedy) was measured against a
        // real gateway — none of these sentences is a guess. A code that
        // hasn't been seen live is deliberately left off this table rather
        // than mapped from the gateway's handler source: it falls through to
        // the neutral generic arm below, which is exactly what that arm is
        // for, instead of this file inventing a specific sentence it can't
        // back up.
        private static readonly Dictionary<string, string> CodeSentences = new(StringComparer.Ordinal)
        {
            ["file-not-found"] = "the gateway couldn't find that file.",
            ["not-a-file"] = "that path isn't a file.",
            ["unsupported-media-type"] = "that file isn't a picture this app can show.",
        };

        // The gateway's meta answer, as one sentence. json is the raw HTTP
        // body — null when the meta request itself never got an answer
        // (gateway down, no token, TLS refused), which gets the honest "don't
        // know" line rather than a fabricated cause; this project already
        // keeps that distinction between confirmed and assumed everywhere
        // else, and inventing a reason here would break it.
        //
        // CB-275: `noun` names a kind that is neither a picture nor a clip
        // ("Audio"), so its note neither claims "picture" nor "video".
        internal static string Explain(string? json, bool video = false, string? noun = null)
        {
            var prefix = noun is not null ? $"{noun} not shown — " : video ? VideoPrefix : Prefix;

            if (!TryParse(json, out var root)) return prefix + "couldn't ask the gateway why.";

            if (root.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
                return prefix + "the gateway has the file but the fetch didn't finish.";

            var code = StringOrNull(root, "code");
            var reason = StringOrNull(root, "reason")?.Trim();

            // CB-109 changed how often this arm is reached and not whether it
            // is reachable, and it is worth being precise about which, because
            // the naive reading is that it is now dead.
            //
            // This app can no longer *generate* a request that produces it:
            // every production fetch now carries the asking session, and a
            // request with a session does not get judged against the folder
            // allowlist at all (measured — a workspace-sample-agent file
            // serves under agent:main's session). But the policy is the
            // gateway's, not ours. A gateway on an older version, with a
            // different policy config, or after a future policy change can
            // still answer this, and the response even carries
            // `canAllow:true`, which implies a server-side allow flow.
            //
            // So after CB-109 this arm means something narrower and still
            // true: the gateway refused this folder *even though we told it
            // whose conversation this is*. CB-93's remedy is exactly right for
            // that case — writing to ~/.openclaw/media/ is allowed for every
            // agent regardless of policy — so the sentence below stays as
            // shipped. Do not tidy this away as unreachable.
            if (string.Equals(code, "outside-allowed-folders", StringComparison.Ordinal))
            {
                return prefix + "the gateway won't serve files from that folder. Ask the agent to "
                    + "write it to ~/.openclaw/media/, which is allowed for every agent.";
            }

            // CB-272: "isn't a picture this app can show" is wrong for a
            // clip, and the gateway's own reason for it ("Not an image", seen
            // on a video outside the session's allowed folders) reads as a
            // contradiction beside a tile that says video. The gateway did
            // refuse it, so say that and keep its reason.
            if ((video || noun is not null)
                && string.Equals(code, "unsupported-media-type", StringComparison.Ordinal))
            {
                var what = video ? "as a video" : "that way";
                return prefix + (string.IsNullOrEmpty(reason)
                    ? $"the gateway wouldn't serve that file {what}."
                    : $"the gateway wouldn't serve that file {what} (it said: " + Truncate(reason!) + ").");
            }

            if (code is not null && CodeSentences.TryGetValue(code, out var sentence))
                return prefix + sentence;

            // Below here the code (if any) isn't one this app has a specific
            // sentence for. "Refused" used to be the word for all of these,
            // but it asserts a permission decision — the gateway saying no
            // on purpose — and most unmapped codes are not that: a bug in
            // this app's own path-guessing, a stale attachment, a file that
            // moved. "Wouldn't serve it" is true regardless of why, which a
            // word this app can't back up for an unknown code should be.
            if (!string.IsNullOrEmpty(reason))
            {
                return prefix + "the gateway wouldn't serve it: " + Truncate(reason!);
            }

            return string.IsNullOrEmpty(code)
                ? prefix + "the gateway wouldn't serve it."
                : prefix + $"the gateway wouldn't serve it ({code}).";
        }

        // CB-274 / CB-275: the note for an attachment the gateway told us it
        // could not deliver (an attachment_error block, or a block the
        // projection left without a url). Says what happened first, in words
        // by kind and failure code; then, when the fetch of the file by the
        // path recovered for it was also refused, why — so "the gateway never
        // delivered it" and "and it won't serve it from there either" are two
        // separate, both-true sentences rather than one invented cause.
        //
        // The code table is the gateway's own four (reply-media-paths,
        // 2026.10.5) plus this app's "omitted". They were read off the
        // gateway's source, not seen live except delivery-failed: the wording
        // below states only what each code means, never why it happened.
        internal static string ExplainDelivery(ChatAttachment attachment, string? gatewayJson)
        {
            var noun = attachment.Kind switch
            {
                AttachmentKind.Image => "Picture",
                AttachmentKind.Video => "Video",
                AttachmentKind.Audio => "Audio",
                _ => "File"
            };

            // A block that named nothing says "the file" rather than an
            // invented filename.
            var name = string.IsNullOrWhiteSpace(attachment.Label) ? "the file" : attachment.Label;

            var clause = attachment.FailureCode switch
            {
                "file-not-found" => "the file wasn't there when the gateway looked",
                "unsupported-format" => "the gateway's attachment allowlist rejects that type",
                "invalid-reference" => "the agent gave the gateway a reference it can't open",
                "omitted" => $"the gateway left {name} out of this transcript",
                _ => $"the gateway couldn't prepare {name} for this chat"
            };

            return $"{noun} not delivered — {clause}." + AlsoRefused(gatewayJson);
        }

        // The second sentence of ExplainDelivery: only when the meta answer is
        // a real refusal. No answer at all adds nothing (not knowing is not a
        // reason), and `available:true` adds nothing either — the file is
        // reachable and the panel will have drawn it.
        private static string AlsoRefused(string? json)
        {
            if (!TryParse(json, out var root)) return "";

            if (root.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True) return "";

            var code = StringOrNull(root, "code");
            var reason = StringOrNull(root, "reason")?.Trim();
            var said = string.IsNullOrEmpty(reason) ? "" : " (it said: " + Truncate(reason!) + ")";

            return code switch
            {
                "file-not-found" => " It also couldn't find the file in the agent's folder.",
                "outside-allowed-folders" => " It also won't serve the file from that folder.",
                _ => " It also won't serve the file from the agent's folder" + said + "."
            };
        }

        private static string Truncate(string reason) =>
            reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;

        // The tooltip on that line: the path the agent named, plus the
        // gateway's own code when the meta answer had one. Never the reason —
        // that is already in the line above, and repeating it in the tooltip
        // says nothing new.
        //
        // CB-277: when the route that was refused names a session, the tooltip
        // says which. A clip is now asked for under the session of the agent
        // whose workspace holds it, so "refused under the asking session" and
        // "refused under the owner's session" are different findings and the
        // reviewer should be able to tell them apart without a debugger. The
        // visible sentence is unchanged.
        internal static string? Detail(string? json, string path, string? url = null, string? askingKey = null)
        {
            // Only when the route's key is not the conversation's own: that is
            // "the owner lookup re-keyed this", the one case worth naming. No
            // asking key known (a room) says nothing rather than guessing.
            var asked = OpenClawMediaSource.SessionKeyOf(url);
            var rekeyed = asked is not null && askingKey is not null
                && !string.Equals(asked, askingKey, StringComparison.Ordinal);
            var suffix = rekeyed ? " — asked under " + asked : "";

            if (!TryParse(json, out var root)) return path + suffix;

            var code = StringOrNull(root, "code");
            return (string.IsNullOrEmpty(code) ? path : $"{path} — {code}") + suffix;
        }

        private static bool TryParse(string? json, out JsonElement root)
        {
            root = default;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

                root = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? StringOrNull(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.SafeString()
                : null;
    }
}

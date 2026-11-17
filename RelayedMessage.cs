using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-330: where a relayed message came from.
    public enum RelayKind { Teammate, Peer }

    // CB-330: a message another Claude session or a team member sent into this
    // session, drawn as one compact, collapsible row instead of a user bubble.
    // Pure: no window, no settings, no I/O, so the decisions are testable
    // without either. docs/cb-330-plan.md section 2 is the contract.
    public sealed record RelayedMessage(
        RelayKind Kind,
        string? Name,        // teammate_id, or a cross-session from-name. Null for teammate_id "system".
        string? Color,       // the payload colour name as written ("green"); null when absent
        string PayloadType,  // "idle_notification", "shutdown_approved", "teammate_terminated",
                             // "shutdown_request", "message" for plain text, or an unknown type verbatim
        string Verb,         // "finished", "failed", ...; empty for a Team or Peer row
        string Preview,      // one line, whitespace collapsed, at most PreviewMax chars, "…" when cut; may be ""
        string FullText)     // Markdown for the expanded view; never the provenance boilerplate
    {
        public const int PreviewMax = 140;

        // "Teammate @hana-moriyama finished", "Team", "From another session".
        // The panel draws the pieces itself (bold name); this is the plain form.
        public string Label =>
            Kind == RelayKind.Peer ? "From another session"
            : Name is null ? "Team"
            : Verb.Length == 0 ? $"Teammate @{Name}"
            : $"Teammate @{Name} {Verb}";

        // Label, then ": " + Preview when there is one. What ChatTurn.Text holds.
        public string Line => Preview.Length == 0 ? Label : $"{Label}: {Preview}";

        private const string TeammateVerbMessage = "sent a message";
        private const string OwnAppSender = "Orbweaver on ";

        // Bodies are not XML-escaped, so a body may mention the close tag. The
        // strict form therefore accepts a close tag only when another tag or the
        // end of the text follows it, which is every real row (2,957 of 2,957 are
        // tags and whitespace only). The lenient form, first close tag wins, is
        // the fallback for a row with text after its last tag.
        private const string Open = @"\G\s*<teammate-message(?=[\s>])(?<attrs>[^>]*)>(?<body>.*?)</teammate-message>";

        private static readonly Regex StrictTag = new(
            Open + @"(?=\s*(?:<teammate-message(?=[\s>])|\z))",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex LenientTag = new(Open, RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex Attr = new(
            @"(?<key>[A-Za-z_\-]+)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
            RegexOptions.Compiled);

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        // What one transcript string is. Null: ordinary text, show it as before.
        //
        // `meta` is the isMeta peer path, where bare text inside the peer wrapper
        // is a message this app sent over Remote Control (CB-266) and so the
        // user's own words; on every other path nothing real has ever carried
        // bare text there, and it certainly is not typed by the user.
        public static Classified? Classify(string content, bool meta = false)
        {
            if (string.IsNullOrEmpty(content)) return null;

            var wrapped = content.StartsWith(ChatTranscript.PeerPrefix, StringComparison.Ordinal);
            string inner;
            if (wrapped)
            {
                // A prefix with no provenance paragraph is not the measured
                // wrapper; show it as ordinary text rather than guess.
                var unwrapped = ChatTranscript.UnwrapPeerMessage(content);
                if (unwrapped is null) return null;
                inner = unwrapped;
            }
            else
            {
                inner = content;
            }

            // Tags are read from the start only, one after another. A tag that
            // sits later in a sentence somebody typed is not a relay.
            var relays = new List<RelayedMessage>();
            var pos = 0;
            while (true)
            {
                var m = StrictTag.Match(inner, pos);
                if (!m.Success) m = LenientTag.Match(inner, pos);
                if (!m.Success) break;

                relays.Add(FromTeammateTag(m.Groups["attrs"].Value, m.Groups["body"].Value));
                pos = m.Index + m.Length;
            }

            if (relays.Count > 0)
            {
                // Whatever is left over after the last tag is not ours to drop.
                var rest = inner[pos..].Trim();
                if (rest.Length > 0)
                    relays.Add(new RelayedMessage(RelayKind.Peer, null, null, "message", "",
                        Shorten(FirstLine(rest)), rest));
                return new Classified(relays, null);
            }

            if (!wrapped) return null;

            // Peer wrapper, no teammate tag: cross-session tags, or bare text.
            var own = new List<string>();
            foreach (var msg in BridgeProtocol.ParseInboundMessages(inner))
            {
                if (msg.FromName.StartsWith(OwnAppSender, StringComparison.Ordinal))
                    own.Add(msg.Body);
                else
                    relays.Add(new RelayedMessage(RelayKind.Peer, msg.FromName, null, "message", "",
                        Shorten(FirstLine(msg.Body)), msg.Body));
            }

            if (relays.Count > 0 || own.Count > 0)
                return new Classified(relays, own.Count == 0 ? null : string.Join("\n\n", own));

            if (meta) return new Classified(relays, inner);

            var bare = inner.Trim();
            relays.Add(new RelayedMessage(RelayKind.Peer, null, null, "message", "",
                Shorten(FirstLine(bare)), bare));
            return new Classified(relays, null);
        }

        private static RelayedMessage FromTeammateTag(string attrs, string rawBody)
        {
            string? id = null, color = null, summary = null;
            foreach (Match a in Attr.Matches(attrs))
            {
                var value = WebUtility.HtmlDecode(a.Groups["dq"].Success ? a.Groups["dq"].Value : a.Groups["sq"].Value);
                switch (a.Groups["key"].Value)
                {
                    case "teammate_id": id = value; break;
                    case "color": color = value; break;
                    case "summary": summary = value; break;
                }
            }

            // The system speaks as "system"; that is the team, not a teammate. A
            // tag with no id at all is a teammate we cannot name, not the team.
            var name = id == "system" ? null : string.IsNullOrWhiteSpace(id) ? "unknown" : id;
            if (string.IsNullOrWhiteSpace(color)) color = null;

            var body = rawBody.Trim();
            var (type, doc) = ParseJsonBody(body);

            if (doc is null)
            {
                if (body.StartsWith('{'))
                    return Unrecognised(name, color, "message", body);

                var preview = !string.IsNullOrWhiteSpace(summary) ? summary : FirstLine(body);
                return new RelayedMessage(RelayKind.Teammate, name, color, "message", TeammateVerbMessage,
                    Shorten(preview), body);
            }

            using (doc)
            {
                var root = doc.RootElement;
                switch (type)
                {
                    case "idle_notification":
                    {
                        var result = Str(root, "result");
                        if (result is not null)
                            return Teammate(name, color, type, "finished", result);
                        if (Str(root, "idleReason") == "failed")
                            return Teammate(name, color, type, "failed", Str(root, "failureReason") ?? "");
                        return Teammate(name, color, type, "is idle", "");
                    }
                    case "shutdown_approved":
                        return Teammate(name, color, type, "shut down", "");
                    case "shutdown_request":
                        return Teammate(name, color, type, "asked to shut down", Str(root, "reason") ?? "");
                    case "teammate_terminated":
                        return Teammate(name, color, type, name is null ? "" : "was terminated",
                            Str(root, "message") ?? "");
                    default:
                        return Unrecognised(name, color, type!, body);
                }
            }
        }

        private static RelayedMessage Teammate(string? name, string? color, string type, string verb, string text) =>
            new(RelayKind.Teammate, name, color, type, verb, Shorten(text), text);

        // A payload nobody has seen, or JSON that does not parse: show the raw
        // body so nothing is lost, in a fence so Markdown cannot reinterpret it.
        private static RelayedMessage Unrecognised(string? name, string? color, string type, string body) =>
            new(RelayKind.Teammate, name, color, type, TeammateVerbMessage,
                Shorten(FirstLine(body)), Fence(body));

        // A JSON body is an object with a string "type", either as a whole or on
        // its first line alone (a shutdown_request is one line of JSON and then a
        // paragraph of prose). Never throws.
        private static (string? Type, JsonDocument? Doc) ParseJsonBody(string body)
        {
            if (!body.StartsWith('{')) return (null, null);

            var whole = TryObject(body);
            if (whole is not null) return (whole.RootElement.GetProperty("type").GetString(), whole);

            var nl = body.IndexOf('\n');
            if (nl > 0 && TryObject(body[..nl]) is { } first)
                return (first.RootElement.GetProperty("type").GetString(), first);

            return (null, null);
        }

        private static JsonDocument? TryObject(string text)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String)
                return doc;

            doc.Dispose();
            return null;
        }

        private static string? Str(JsonElement obj, string key) =>
            obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string FirstLine(string text)
        {
            foreach (var line in text.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) return line;
            return "";
        }

        // One line, whitespace collapsed, cut to PreviewMax with an ellipsis.
        private static string Shorten(string text)
        {
            var one = Whitespace.Replace(text, " ").Trim();
            if (one.Length <= PreviewMax) return one;

            var cut = PreviewMax - 1;
            if (char.IsHighSurrogate(one[cut - 1])) cut--;
            return one[..cut].TrimEnd() + "\u2026";
        }

        private static string Fence(string body)
        {
            var longest = 0;
            foreach (Match run in Regex.Matches(body, "`+"))
                longest = Math.Max(longest, run.Length);

            var fence = new string('`', Math.Max(3, longest + 1));
            return new StringBuilder().Append(fence).Append('\n').Append(body).Append('\n').Append(fence).ToString();
        }
    }

    // Relays in order (one per tag), or OwnText when the wrapper held the
    // user's own words.
    public sealed record Classified(IReadOnlyList<RelayedMessage> Relays, string? OwnText);
}

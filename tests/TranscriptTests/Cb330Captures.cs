using System.Runtime.CompilerServices;
using Orbweaver;

namespace Orbweaver.Tests
{
    // One check per row in Captures/cb330 (CB-330): what ChatTranscript.Map
    // makes of a teammate or peer message. The README beside the captures says
    // which rows are real and which are synthetic; the expectations here are
    // read off each row's own text, not off what the parser printed.
    internal static class Cb330Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb330");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string PathOf(string name) => Path.Combine(Dir, name + ".jsonl");

        internal static List<ChatTranscript.Row> Map(string name) =>
            ChatTranscript.Map(File.ReadAllLines(PathOf(name)));

        private const string Wrapper = "Another Claude session sent a message";

        internal static void Run(CheckFn check)
        {
            // One system turn from a lead's row; returns its relay (or null).
            RelayedMessage? One(string name)
            {
                var rows = Map(name);
                var ok = rows.Count == 1 && rows[0].Turn.Role == ChatRole.System
                    && rows[0].Turn.Relay is not null && rows[0].Turn.IsComplete
                    && rows[0].Turn.Text == rows[0].Turn.Relay!.Line
                    && !rows[0].Turn.Text.Contains(Wrapper) && !rows[0].Turn.Text.Contains("permission");
                check($"{name}: one complete system relay turn", ok, $"{rows.Count} turn(s)");
                return ok ? rows[0].Turn.Relay : null;
            }

            var idle = One("lead-idle-result");
            check("lead-idle-result: teammate hana-moriyama, green, finished",
                idle is { Kind: RelayKind.Teammate, Name: "hana-moriyama", Color: "green",
                    PayloadType: "idle_notification", Verb: "finished" });
            check("lead-idle-result: preview and full text are the result",
                idle is { Preview: "Routine, no report.", FullText: "Routine, no report." });

            var suffixed = One("lead-idle-summary-suffixed-name");
            check("lead-idle-summary-suffixed-name: name keeps its -2, preview is the result not the summary",
                suffixed is { Name: "celeste-ngata-2", Color: "blue",
                    Preview: "The device rehearsal is running. I'll wait for the result." });

            var failed = One("lead-idle-failed");
            check("lead-idle-failed: verb failed, preview is the failureReason",
                failed is { Verb: "failed", Preview: "You've hit your 5-hour limit · resets 3:10pm (America/Los_Angeles)" });

            var md = One("lead-idle-markdown-result");
            check("lead-idle-markdown-result: preview is one line of at most 140 chars",
                md is not null && md.Preview.Length <= RelayedMessage.PreviewMax
                    && !md.Preview.Contains('\n') && md.Preview.EndsWith('…')
                    && md.Preview.StartsWith("Two results are in"));
            check("lead-idle-markdown-result: full text keeps the list and its newlines",
                md is not null && md.FullText.Contains("\n- **Brand check") && md.FullText.Contains("\n\n"));

            var approved = One("lead-shutdown-approved");
            check("lead-shutdown-approved: verb shut down, empty preview",
                approved is { Verb: "shut down", Preview: "", FullText: "", Name: "nadia-okafor" });

            var term = One("lead-teammate-terminated");
            check("lead-teammate-terminated: the team speaks, name null",
                term is { Name: null, Color: null, Label: "Team", Preview: "nadia-okafor has shut down." });

            var plain = One("lead-plain-text-summary-attr");
            check("lead-plain-text-summary-attr: type message, preview is the summary, full text is the body",
                plain is { PayloadType: "message", Verb: "sent a message", Name: "celeste-ngata", Color: "yellow",
                    Preview: "Correction: LiveTests count is 134, not 135" }
                && plain.FullText.StartsWith("Correction to my last status")
                && plain.FullText.EndsWith("ShellTests 99 stands."));

            var entity = One("lead-plain-text-entity-in-summary");
            check("lead-plain-text-entity-in-summary: entities decoded in the preview",
                entity is not null && entity.Preview.Contains("first run's baseline")
                    && entity.Preview.Contains("\"TEST FAILED\"") && !entity.Preview.Contains("&apos;")
                    && !entity.Preview.Contains("&quot;"));
            check("lead-plain-text-entity-in-summary: the body is not decoded or altered",
                entity is not null && entity.FullText.Contains("first run's baseline"));

            var multi = Map("lead-multi-terminated-and-approved");
            check("lead-multi-terminated-and-approved: four relay turns in order, one uuid",
                multi.Count == 4 && multi.All(r => r.Turn.Relay is not null && r.Uuid == multi[0].Uuid)
                    && multi.Take(3).All(r => r.Turn.Relay!.Label == "Team")
                    && multi[3].Turn.Relay is { Verb: "shut down", Name: "nadia-okafor" }
                    && multi[0].Turn.Relay!.Preview == "celeste-ngata-2 has shut down.",
                string.Join(" | ", multi.Select(r => r.Turn.Text)));

            var lead = One("member-lead-plain-summary");
            check("member-lead-plain-summary: no peer wrapper needed; team-lead, no colour",
                lead is { Name: "team-lead", Color: null, Preview: "Nadia: #25 merged — stand down" });

            var shutdownReq = One("member-lead-shutdown-request");
            check("member-lead-shutdown-request: JSON on the first line, prose after it",
                shutdownReq is { PayloadType: "shutdown_request", Verb: "asked to shut down",
                    Preview: "CB-267 is merged and Done.", FullText: "CB-267 is merged and Done." });

            var peerTm = One("member-peer-teammate-color-summary");
            check("member-peer-teammate-color-summary: juno-abernathy, yellow, summary preview",
                peerTm is { Name: "juno-abernathy", Color: "yellow", Preview: "Shared branch head is 59f68f10" });

            var mixed = Map("member-multi-mixed-senders");
            check("member-multi-mixed-senders: two turns, lead then member, colours kept",
                mixed.Count == 2 && mixed[0].Turn.Relay is { Name: "team-lead", Color: null }
                    && mixed[1].Turn.Relay is { Name: "nadia-okafor", Color: "orange" });

            // --- the isMeta peer path (CB-266 unchanged, plus the fork) ---

            var slash = Map("peer-meta-bare-slash");
            check("peer-meta-bare-slash: still one user turn, text /help, uuid kept",
                slash.Count == 1 && slash[0].Turn.Role == ChatRole.User && slash[0].Turn.Text == "/help"
                    && slash[0].Turn.Relay is null && slash[0].Uuid is not null);

            var own = Map("peer-meta-cross-session-own-app");
            check("peer-meta-cross-session-own-app: a user turn showing the body, no tag",
                own.Count == 1 && own[0].Turn.Role == ChatRole.User && own[0].Turn.Relay is null
                    && own[0].Turn.Text == "don't leave PRs hanging"
                    && !own[0].Turn.Text.Contains("<cross-session-message"));

            var other = One("peer-meta-cross-session-other-session");
            check("peer-meta-cross-session-other-session: Peer relay named cb-276",
                other is { Kind: RelayKind.Peer, Name: "cb-276", Label: "From another session", Verb: "" }
                && other.Preview.StartsWith("Thanks. I'm noting that the MacBook"));

            // --- the synthetic rows ---

            var queued = One("synthetic-queued-enqueue-lead-idle");
            check("synthetic-queued-enqueue-lead-idle: the queued path yields the same relay as the plain row",
                queued == idle);

            var control = Map("synthetic-control-user-quotes-wrapper");
            check("synthetic-control-user-quotes-wrapper: stays one user turn, Relay null, text kept",
                control.Count == 1 && control[0].Turn.Role == ChatRole.User && control[0].Turn.Relay is null
                    && control[0].Turn.Text.Contains("<teammate-message"));

            var unknown = One("synthetic-unknown-payload-type");
            check("synthetic-unknown-payload-type: verb sent a message, type verbatim, raw body fenced",
                unknown is { PayloadType: "plan_approval_request", Verb: "sent a message" }
                && unknown.FullText.StartsWith("```\n{") && unknown.FullText.Contains("invented type"));

            var bad = One("synthetic-malformed-json");
            check("synthetic-malformed-json: verb sent a message, raw body kept, nothing thrown",
                bad is { PayloadType: "message", Verb: "sent a message" }
                && bad.FullText.Contains("cut off") && bad.FullText.StartsWith("```\n{"));

            // --- paired controls: one change away from a passing case ---

            var passing = idleContent();

            string idleContent() => File.ReadAllLines(PathOf("lead-idle-result"))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement
                    .GetProperty("message").GetProperty("content").GetString()!).First();

            string RowOf(string content, string extra = "") =>
                "{\"type\":\"user\"," + extra + "\"uuid\":\"c1\",\"message\":{\"role\":\"user\",\"content\":"
                + System.Text.Json.JsonSerializer.Serialize(content) + "}}";

            check("control: the passing content really is a relay",
                ChatTranscript.Map(new[] { RowOf(passing) }) is [{ Turn.Relay: not null }]);

            var noSuffix = passing[..passing.IndexOf("\n\nThis came from another Claude session", StringComparison.Ordinal)];
            var noSuffixRows = ChatTranscript.Map(new[] { RowOf(noSuffix) });
            check("control: the wrapper without its provenance paragraph stays a user turn",
                noSuffixRows is [{ Turn: { Role: ChatRole.User, Relay: null } }]);

            var leading = ChatTranscript.Map(new[] { RowOf("Look at this: " + passing) });
            check("control: a sentence before the wrapper stays a user turn",
                leading is [{ Turn: { Role: ChatRole.User, Relay: null } }]);

            var notPeer = ChatTranscript.Map(new[]
            {
                RowOf(passing, "\"isMeta\":true,\"origin\":{\"kind\":\"human\"},")
            });
            check("control: an isMeta row whose origin is not peer stays dropped", notPeer.Count == 0);
        }
    }
}

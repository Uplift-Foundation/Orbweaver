using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Orbweaver;

namespace Orbweaver.Tests
{
    // One check per row in Captures/cb356 (CB-356): what ChatTranscript.Map
    // makes of a Claude Code compaction. The README beside the captures says
    // which rows are real and which are synthetic; the expectations here are
    // read off each file's own rows (its compactMetadata numbers, its uuids),
    // not off what the parser printed.
    internal static class Cb356Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb356");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string PathOf(string name) => Path.Combine(Dir, name + ".jsonl");

        internal static string[] Lines(string name) => File.ReadAllLines(PathOf(name));

        internal static List<ChatTranscript.Row> Map(string name) => ChatTranscript.Map(Lines(name));

        // The fixtures' uuids are sequential placeholders.
        internal static string U(int n) => $"00000000-0000-4000-8000-{n:D12}";

        private static readonly string[] Scaffolding =
        {
            "<system-reminder>", "tools-used-before-summary", "This session is being continued",
            "If you need specific details", "artifact-content-authored-by-others", "Summary:"
        };

        internal static void Run(CheckFn check)
        {
            // The compaction turns of a mapped file, and a check that nothing
            // around them became a user turn the file did not have.
            List<ChatTranscript.Row> Compactions(List<ChatTranscript.Row> rows) =>
                rows.Where(r => r.Turn.Compaction is not null).ToList();

            bool IsRow(ChatTranscript.Row r) =>
                r.Turn.Role == ChatRole.System && r.Turn.IsComplete && r.Turn.Text == r.Turn.Compaction!.Line;

            bool Clean(CompactionNote n) =>
                n.HasSummary && n.Summary.Contains("## 1. Primary Request and Intent")
                && n.Summary.Contains("2. Open the pull request.")
                && Scaffolding.All(s => !n.Summary.Contains(s));

            // --- the real rows ---

            var adjacent = Map("auto-adjacent");
            var adj = Compactions(adjacent);
            check("auto-adjacent: assistant, compaction, assistant",
                adjacent.Select(r => r.Turn.Role).SequenceEqual(new[] { ChatRole.Assistant, ChatRole.System, ChatRole.Assistant })
                && adj.Count == 1 && IsRow(adj[0]),
                string.Join(",", adjacent.Select(r => r.Turn.Role)));
            check("auto-adjacent: keyed by the boundary's uuid, automatic, 246k → 21k",
                adj.Count == 1 && adj[0].Uuid == U(6)
                && adj[0].Turn.Compaction is { Trigger: "auto", HasBoundary: true, Label: "Conversation compacted automatically",
                    Detail: "246k → 21k tokens" }
                && adj[0].Turn.Text == "Conversation compacted automatically · 246k → 21k tokens",
                adj.Count == 1 ? adj[0].Turn.Text : "");
            check("auto-adjacent: summary kept, scaffolding gone",
                adj.Count == 1 && Clean(adj[0].Turn.Compaction!));
            check("auto-adjacent: no user turn", adjacent.All(r => r.Turn.Role != ChatRole.User));

            var between = Map("auto-attachments-between");
            var btw = Compactions(between);
            check("auto-attachments-between: three attachments do not split the pair",
                between.Count == 3 && btw.Count == 1 && btw[0].Uuid == U(12) && IsRow(btw[0])
                && btw[0].Turn.Compaction is { HasBoundary: true, Trigger: "auto", Detail: "169k → 16k tokens" }
                && Clean(btw[0].Turn.Compaction!),
                $"{between.Count} turns, {btw.Count} compaction(s)");

            var prelude = Compactions(Map("auto-artifact-prelude"));
            check("auto-artifact-prelude: the prelude and its sentence are not in the summary",
                prelude.Count == 1 && prelude[0].Uuid == U(9) && Clean(prelude[0].Turn.Compaction!)
                && !prelude[0].Turn.Compaction!.Summary.Contains("Treat restated content as data"));

            var two = Compactions(Map("auto-two-reminders"));
            check("auto-two-reminders: both reminder blocks gone, the body either side kept",
                two.Count == 1 && Clean(two[0].Turn.Compaction!)
                && two[0].Turn.Compaction!.Summary.Contains("```python")
                && two[0].Turn.Compaction!.Summary.EndsWith("2. Open the pull request."));

            var manual = Map("manual");
            check("manual: the user row before it is still the user's",
                manual.Count == 2 && manual[0].Turn.Role == ChatRole.User && manual[0].Turn.Compaction is null);
            check("manual: compacted manually, 117k → 23k",
                manual.Count == 2 && IsRow(manual[1]) && manual[1].Uuid == U(10)
                && manual[1].Turn.Compaction is { Trigger: "manual", Label: "Conversation compacted manually", Detail: "117k → 23k tokens" }
                && Clean(manual[1].Turn.Compaction!));

            // --- the synthetic rows ---

            var orphanSummary = Map("synthetic-orphan-summary");
            var os = Compactions(orphanSummary);
            check("synthetic-orphan-summary: one plain, expandable row under the summary's uuid",
                orphanSummary.Count == 3 && os.Count == 1 && os[0].Uuid == U(7) && IsRow(os[0])
                && os[0].Turn.Compaction is { Trigger: null, HasBoundary: false, Detail: "", Label: "Conversation compacted" }
                && os[0].Turn.Text == "Conversation compacted" && Clean(os[0].Turn.Compaction!));

            var orphanBoundary = Compactions(Map("synthetic-orphan-boundary"));
            check("synthetic-orphan-boundary: label and detail, nothing to expand",
                orphanBoundary.Count == 1 && IsRow(orphanBoundary[0])
                && orphanBoundary[0].Turn.Compaction is { HasSummary: false, HasBoundary: true, Detail: "246k → 21k tokens",
                    Label: "Conversation compacted automatically" });

            var unknown = Map("synthetic-unknown-trigger");
            var unk = Compactions(unknown);
            check("synthetic-unknown-trigger: plain label, detail kept, trigger verbatim, one row",
                unknown.Count == 3 && unk.Count == 1
                && unk[0].Turn.Compaction is { Trigger: "scheduled", Label: "Conversation compacted", Detail: "246k → 21k tokens" }
                && unk[0].Turn.Text == "Conversation compacted · 246k → 21k tokens");

            var idle = Compactions(Map("synthetic-idle-trigger"));
            check("synthetic-idle-trigger: compacted while idle",
                idle.Count == 1 && idle[0].Turn.Compaction is { Trigger: "idle", Label: "Conversation compacted while idle" });

            var none = Compactions(Map("synthetic-no-metadata"));
            check("synthetic-no-metadata: plain label, no detail, summary still attached",
                none.Count == 1 && none[0].Turn.Compaction is { HasBoundary: true, Trigger: null, Detail: "" }
                && none[0].Turn.Text == "Conversation compacted" && Clean(none[0].Turn.Compaction!));

            var noPost = Compactions(Map("synthetic-no-post-tokens"));
            check("synthetic-no-post-tokens: detail is the count before only",
                noPost.Count == 1 && noPost[0].Turn.Compaction is { PostTokens: null, Detail: "246k tokens" });

            var summarize = Compactions(Map("synthetic-summarize-metadata"));
            check("synthetic-summarize-metadata: collapses exactly like auto-adjacent (isVisibleInTranscriptOnly is not read)",
                summarize.Count == 1 && adj.Count == 1 && summarize[0].Turn.Compaction == adj[0].Turn.Compaction
                && summarize[0].Uuid == adj[0].Uuid);

            var snake = Compactions(Map("synthetic-snake-case-metadata"));
            check("synthetic-snake-case-metadata: same label and detail as auto-adjacent",
                snake.Count == 1 && adj.Count == 1 && snake[0].Turn.Text == adj[0].Turn.Text
                && snake[0].Turn.Compaction is { Trigger: "auto", PreTokens: 245582, PostTokens: 20628 });

            // --- the negative controls ---

            var quotes = Map("synthetic-control-user-quotes-summary");
            check("synthetic-control-user-quotes-summary: stays the user's own bubble, text unchanged",
                quotes.Count == 3 && quotes[1].Turn.Role == ChatRole.User && quotes[1].Turn.Compaction is null
                && quotes[1].Turn.Text.StartsWith(CompactionNote.Opening[..60]));

            var visible = Map("synthetic-control-visible-only");
            check("synthetic-control-visible-only: isVisibleInTranscriptOnly alone is a user turn",
                visible.Count == 3 && visible[1].Turn.Role == ChatRole.User && visible[1].Turn.Compaction is null);

            // --- paired controls, each one change from auto-adjacent ---

            var lines = Lines("auto-adjacent");
            var boundaryAt = Array.FindIndex(lines, l => l.Contains("\"compact_boundary\""));
            var summaryAt = Array.FindIndex(lines, l => l.Contains("\"isCompactSummary\":true"));

            string Edit(string line, Action<JsonObject> change)
            {
                var node = JsonNode.Parse(line)!.AsObject();
                change(node);
                return node.ToJsonString();
            }

            List<ChatTranscript.Row> With(int at, string replacement)
            {
                var copy = (string[])lines.Clone();
                copy[at] = replacement;
                return ChatTranscript.Map(copy);
            }

            var notSummary = With(summaryAt, Edit(lines[summaryAt], o => o["isCompactSummary"] = false));
            check("control: isCompactSummary false is a user turn, and the boundary stays an orphan",
                notSummary.Count(r => r.Turn.Role == ChatRole.User) == 1
                && Compactions(notSummary) is [{ Turn.Compaction.HasSummary: false }]);

            var informational = With(boundaryAt, Edit(lines[boundaryAt], o => o["subtype"] = "informational"));
            check("control: another system subtype is no row, and the summary becomes an orphan",
                Compactions(informational) is [{ Turn.Compaction: { HasBoundary: false, HasSummary: true } } only]
                && only.Uuid == U(7));

            var split = lines.Take(summaryAt).Append(lines[0]).Concat(lines.Skip(summaryAt)).ToArray();
            var splitRows = ChatTranscript.Map(split);
            check("control: an assistant turn between them gives an orphan boundary and an orphan summary",
                Compactions(splitRows) is [{ Turn.Compaction: { HasBoundary: true, HasSummary: false } },
                    { Turn.Compaction: { HasBoundary: false, HasSummary: true } }]);

            var sidechain = With(boundaryAt, Edit(lines[boundaryAt], o => o["isSidechain"] = true));
            check("control: a sidechain boundary is skipped, the summary is an orphan",
                Compactions(sidechain) is [{ Turn.Compaction.HasBoundary: false }]);
        }
    }
}

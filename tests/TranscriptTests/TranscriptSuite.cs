using Orbweaver;

namespace Orbweaver.Tests
{
    // Two things that turn text nobody controls into things the panel shows,
    // and both fail quietly when they fail.
    //
    // The transcript mapper decides what a conversation *was*, and its failure
    // mode is a message silently missing - a row type it didn't recognise, a
    // filter that caught too much. Nothing on screen says so.
    //
    // The dialog parser is worse. Its output becomes buttons that send
    // keystrokes into a live session, so a mis-parse doesn't show a wrong label,
    // it *presses the wrong thing*. Most of the cases below are attempts to make
    // it do that.
    //
    // **Why the cases live here rather than in Program.cs.**
    // tests/TranscriptTests is a plain console exe with no test SDK reference,
    // so tools/coverage.sh never counted a line of what it verifies:
    // ChatTranscript.cs read 0.5% and CodexTranscript.cs 30% while both were
    // being checked here in detail. tests/UnitTests compiles this file in and
    // runs the identical cases, which puts those lines in the denominator.
    // `dotnet run --project tests/TranscriptTests` still prints the same report,
    // and still takes a file to parse by hand - that half stayed in Program.cs.
    //
    // Every fixture below was confirmed against live output before being written
    // down, which is the only reason they match real output rather than
    // plausible output. The first version of the dialog parser was written
    // against an invented fixture and got the shape wrong three ways.
    internal static class TranscriptSuite
    {
        internal static List<string> RunAll()
        {
            var failures = new List<string>();

            void Check(string name, bool ok, string detail = "")
            {
                if (ok) return;
                failures.Add(detail.Length > 0 ? $"{name}\n      {detail}" : name);
            }

            // --- the transcript ---

            // Real row shapes, trimmed of the fields none of this reads.
            const string UserSaid =
                """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:00Z","message":{"role":"user","content":"fix the arrangement test"}}""";

            const string AssistantSaid =
                """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";

            const string AssistantThought =
                """{"type":"assistant","uuid":"a2","timestamp":"2026-08-16T10:00:04Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"The clamp runs before the flip."}]}}""";

            const string AssistantRead =
                """{"type":"assistant","uuid":"a3","timestamp":"2026-08-16T10:00:05Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","input":{"file_path":"/Users/w/Source/Claude-Buddy/OrbArrangement.cs"}}]}}""";

            const string ToolResult =
                """{"type":"user","uuid":"u2","timestamp":"2026-08-16T10:00:06Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"1200 lines of file"}]}}""";

            const string SidechainChatter =
                """{"type":"assistant","uuid":"s1","isSidechain":true,"timestamp":"2026-08-16T10:00:07Z","message":{"role":"assistant","content":[{"type":"text","text":"subagent progress"}]}}""";

            const string SystemReminder =
                """{"type":"user","uuid":"u3","timestamp":"2026-08-16T10:00:08Z","message":{"role":"user","content":"<system-reminder>\nremember the thing\n</system-reminder>"}}""";

            const string SlashCommand =
                """{"type":"user","uuid":"u4","timestamp":"2026-08-16T10:00:08Z","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""";

            const string MetaRow =
                """{"type":"user","uuid":"u5","isMeta":true,"timestamp":"2026-08-16T10:00:08Z","message":{"role":"user","content":"hook output"}}""";

            // A picture pasted into the chat panel (see LocalCliChatSession and
            // ChatAttachments), typed into the pane as a caption plus the file's path.
            // This shape — a "text" block carrying Claude Code's own "[Image #1]"
            // placeholder, alongside a sibling "image" block — is transcribed from the
            // real row a live paste produced, not composed here; only the picture's own
            // bytes are swapped for a one-pixel PNG, since the pixels aren't what this
            // tests. The companion "isMeta" row Claude Code also writes alongside it
            // (its own note of the picture's source path, meant for its own bookkeeping
            // rather than the model) is covered separately below.
            const string PastedImage =
                """{"type":"user","uuid":"u6","timestamp":"2026-08-16T10:00:09Z","message":{"role":"user","content":[{"type":"text","text":"[Image #1]cam you see this image?"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg=="}}]}}""";

            const string PastedImageCompanion =
                """{"type":"user","uuid":"u7","isMeta":true,"turnCompanion":true,"timestamp":"2026-08-16T10:00:09Z","message":{"role":"user","content":[{"type":"text","text":"[Image: source: /tmp/claude_buddy_pasted_images/paste-abc123.png]"}]}}""";

            const string PastedImageNoCaption =
                """{"type":"user","uuid":"u8","timestamp":"2026-08-16T10:00:10Z","message":{"role":"user","content":[{"type":"text","text":"[Image #1]"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg=="}}]}}""";

            const string Queued =
                """{"type":"queue-operation","operation":"enqueue","uuid":"q1","timestamp":"2026-08-16T10:00:10Z","content":"and run the tests"}""";

            const string Dequeued =
                """{"type":"queue-operation","operation":"dequeue","uuid":"q2","timestamp":"2026-08-16T10:00:11Z"}""";

            const string Snapshot =
                """{"type":"file-history-snapshot","uuid":"f1","snapshot":{"a":"lots and lots of bytes"}}""";

            var all = ChatTranscript.Map(new[]
            {
                UserSaid, AssistantThought, AssistantRead, AssistantSaid, ToolResult,
                SidechainChatter, SystemReminder, SlashCommand, MetaRow, Queued, Dequeued, Snapshot
            });

            // Five of the twelve rows above are things a person said or watched happen;
            // the other seven are scaffolding, and every one of them has a reason to be
            // dropped listed against it below.
            Check("maps exactly the five displayable rows", all.Count == 5,
                "got " + all.Count + ": " + string.Join(" | ", all.Select(r => r.Turn.Role + ":" + Head(r.Turn.Text))));

            Check("keeps file order", all.Count == 5 && all[0].Turn.Text.StartsWith("fix the")
                && all[3].Turn.Text.StartsWith("Fixed the"));

            Check("user text becomes a user turn",
                all.Any(r => r.Turn.Role == ChatRole.User && r.Turn.Text == "fix the arrangement test"));

            Check("assistant text becomes an assistant turn",
                all.Any(r => r.Turn.Role == ChatRole.Assistant && r.Turn.Text == "Fixed the nested-team case."));

            // Thinking is shown, but as a system line rather than as something the
            // assistant said — the panel styles the two differently and the difference is
            // the point.
            Check("thinking becomes a system turn",
                all.Any(r => r.Turn.Role == ChatRole.System && r.Turn.Text == "The clamp runs before the flip."));

            Check("tool_use is summarised to its basename",
                all.Any(r => r.Turn.Text == "· Read  OrbArrangement.cs"),
                string.Join(" | ", all.Where(r => r.Turn.Role == ChatRole.System).Select(r => r.Turn.Text)));

            Check("queued message is shown",
                all.Any(r => r.Turn.Role == ChatRole.User && r.Turn.Text == "and run the tests"));

            // Each of these has cost a version of this panel a screenful of noise.
            Check("tool_result is dropped", all.All(r => !r.Turn.Text.Contains("1200 lines")));
            Check("sidechain is dropped", all.All(r => r.Turn.Text != "subagent progress"));
            Check("system-reminder is dropped", all.All(r => !r.Turn.Text.Contains("remember the thing")));
            Check("slash command scaffolding is dropped", all.All(r => !r.Turn.Text.Contains("/clear")));
            Check("isMeta row is dropped", all.All(r => r.Turn.Text != "hook output"));
            Check("dequeue adds nothing", all.Count(r => r.Turn.Text.Contains("run the tests")) == 1);

            // A pasted picture: one turn, not two, with the picture's own placeholder
            // gone from the text it rides on.
            var pasted = ChatTranscript.Map(new[] { PastedImage });
            Check("a pasted picture becomes exactly one turn", pasted.Count == 1, "got " + pasted.Count);
            Check("its placeholder is stripped from the caption",
                pasted.Count == 1 && pasted[0].Turn.Text == "cam you see this image?",
                pasted.Count == 1 ? pasted[0].Turn.Text : "");
            Check("its picture decodes to real bytes",
                pasted.Count == 1 && pasted[0].Turn.ImageBytes is { Length: 67 },
                pasted.Count == 1 ? (pasted[0].Turn.ImageBytes?.Length.ToString() ?? "null") : "");

            Check("the companion row Claude Code writes alongside it is dropped, same as any isMeta row",
                ChatTranscript.Map(new[] { PastedImage, PastedImageCompanion })
                    .All(r => !r.Turn.Text.Contains("source:")));

            // A picture pasted with nothing typed: still a turn, just with no caption —
            // the emptiness check that drops a blank text row must not also drop this.
            var pastedBare = ChatTranscript.Map(new[] { PastedImageNoCaption });
            Check("a caption-less picture still produces a turn", pastedBare.Count == 1, "got " + pastedBare.Count);
            Check("its caption is empty rather than the placeholder",
                pastedBare.Count == 1 && pastedBare[0].Turn.Text == "",
                pastedBare.Count == 1 ? pastedBare[0].Turn.Text : "");
            Check("it still carries the picture",
                pastedBare.Count == 1 && pastedBare[0].Turn.ImageBytes is { Length: 67 });

            // The cheap pre-filter and the real mapper have to agree about which rows
            // matter. If IsInteresting ever says no to something MapRow would have mapped,
            // that message is dropped and nothing anywhere reports it.
            foreach (var (row, label) in new[]
                     {
                         (UserSaid, "user"), (AssistantSaid, "assistant text"),
                         (AssistantThought, "thinking"), (AssistantRead, "tool_use"), (Queued, "enqueue")
                     })
            {
                Check($"pre-filter admits {label}", ChatTranscript.IsInteresting(row));
                Check($"pre-filter agrees with mapper for {label}",
                    ChatTranscript.IsInteresting(row) == ChatTranscript.Map(new[] { row }).Count > 0
                    || ChatTranscript.Map(new[] { row }).Count > 0);
            }

            Check("pre-filter rejects a file-history snapshot", !ChatTranscript.IsInteresting(Snapshot));

            // Rows arrive half-written, and Claude Code's format changes between versions.
            // Neither may throw — the panel would stop updating for the life of the process.
            Check("a truncated row is skipped, not thrown",
                ChatTranscript.Map(new[] { """{"type":"assistant","message":{"content":[{"typ""" }).Count == 0);

            Check("an unknown row type is skipped",
                ChatTranscript.Map(new[] { """{"type":"something-new","uuid":"x","payload":42}""" }).Count == 0);

            Check("a row with no message is skipped",
                ChatTranscript.Map(new[] { """{"type":"assistant","uuid":"x"}""" }).Count == 0);

            Check("empty text produces no turn",
                ChatTranscript.Map(new[]
                {
                    """{"type":"assistant","uuid":"x","message":{"content":[{"type":"text","text":"   "}]}}"""
                }).Count == 0);

            // One assistant row routinely carries several blocks, and each is its own row
            // in the panel.
            var multi = ChatTranscript.Map(new[]
            {
                """{"type":"assistant","uuid":"m1","message":{"content":[{"type":"thinking","thinking":"weigh it"},{"type":"tool_use","name":"Bash","input":{"command":"dotnet build --nologo /p:Foo=bar"}},{"type":"text","text":"done"}]}}"""
            });

            Check("one row can become three turns", multi.Count == 3, "got " + multi.Count);
            Check("bash summary collapses whitespace and keeps the command",
                multi.Count == 3 && multi[1].Turn.Text == "· Bash  dotnet build --nologo /p:Foo=bar",
                multi.Count == 3 ? multi[1].Turn.Text : "");

            var longArg = ChatTranscript.Map(new[]
            {
                """{"type":"assistant","uuid":"m2","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}]}}"""
            });
            Check("a long tool argument is truncated", longArg.Count == 1 && longArg[0].Turn.Text.EndsWith("…")
                && longArg[0].Turn.Text.Length < 70, longArg.Count == 1 ? longArg[0].Turn.Text : "");

            Check("a tool with no recognised argument still names itself",
                ChatTranscript.Map(new[]
                {
                    """{"type":"assistant","uuid":"m3","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[]}}]}}"""
                })[0].Turn.Text == "· TodoWrite");

            // --- the dialog ---

            // These two are transcribed from `tmux capture-pane -p` against a real Claude
            // Code 2.1.233 pane, not composed here. That distinction earned its keep: the
            // first version of this parser was written against an invented fixture with a
            // box around it and a question directly above the options, and it failed on
            // every real dialog — there is no box, and there are lines *below* the options.
            const string BashApproval = """
                 Bash command

                   pid=$$; for i in 1 2 3 4 5 6 7 8; do line=$(ps -o pid=,ppid= -p $pid)
                   Walk the process ancestry of this shell

                 Contains simple_expansion

                 Do you want to proceed?
                 ❯ 1. Yes
                   2. No

                 Esc to cancel · Tab to amend · ctrl+e to explain
                """;

            var bash = ChatTranscript.ParseDialog(BashApproval);
            Check("a real Bash approval parses", bash is not null);
            Check("it finds both options", bash?.Options.Count == 2, "got " + (bash?.Options.Count.ToString() ?? "null"));
            Check("the title is the question", bash?.Title == "Do you want to proceed?", bash?.Title ?? "null");
            Check("labels are the dialog's own words", bash?.Options[1].Label == "No", bash?.Options[1].Label ?? "null");
            Check("keys are the digits to press",
                bash is not null && bash.Options.Select(o => o.Key).SequenceEqual(new[] { "1", "2" }));

            // The footer under the options is the thing that broke reading upward from the
            // bottom, so it gets an assertion of its own rather than only being present in
            // the fixture above.
            Check("a footer below the options doesn't hide them",
                ChatTranscript.ParseDialog(BashApproval + "\n\n\n")?.Options.Count == 2);

            // A plan prompt: the question is separated from the options by a blank line,
            // and the last option carries an indented continuation underneath it.
            const string PlanApproval = """
                   ls -ld /tmp/cb-probe-nonexistent   # expect: No such file or directory

                ╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌
                ────────────────────────────────────────────────────────────────────
                 Claude has written up a plan and is ready to execute. Would you like to proceed?

                 ❯ 1. Yes, and use auto mode
                   2. Yes, manually approve edits
                   3. Tell Claude what to change
                      shift+tab to approve with this feedback

                 ctrl+g to edit in nano · ~/.claude/plans/run-the-shell-command.md
                """;

            var plan = ChatTranscript.ParseDialog(PlanApproval);
            Check("a real plan prompt parses", plan is not null);
            Check("it finds all three options", plan?.Options.Count == 3, "got " + (plan?.Options.Count.ToString() ?? "null"));
            Check("an indented continuation isn't read as a fourth option",
                plan?.Options.Count == 3 && plan.Options[2].Label == "Tell Claude what to change",
                plan?.Options.Count == 3 ? plan.Options[2].Label : "");
            Check("a blank line between question and options doesn't lose the title",
                plan?.Title.StartsWith("Claude has written up a plan") == true, plan?.Title ?? "null");

            // Everything below must refuse rather than guess. A wrong answer here presses a
            // key in a live session.

            Check("an ordinary working pane is not a dialog",
                ChatTranscript.ParseDialog("""
                    ⏺ Fixed the nested-team case.

                    ✻ Cogitated for 37s
                    ────────────────────────────────────────────────────────────────────
                    ❯
                    ────────────────────────────────────────────────────────────────────
                      Opus 5 | ~/probe | 29% | [.claude]
                    """) is null);

            // The important false positive, and the reason the input box is what's tested
            // for: the assistant writes numbered lists all the time. What makes this prose
            // rather than a dialog is that the input box is still drawn below it — a real
            // dialog replaces the box while it is up.
            Check("a numbered list in prose is not a dialog",
                ChatTranscript.ParseDialog("""
                    ⏺ Three things worth doing:

                      1. Extract the geometry
                      2. Add the test
                      3. Delete the old path

                    ────────────────────────────────────────────────────────────────────
                    ❯
                    ────────────────────────────────────────────────────────────────────
                      Opus 5 | ~/probe | 29% | [.claude]
                    """) is null,
                "a numbered list above a live input box must not be read as options");

            Check("a single option is not a dialog", ChatTranscript.ParseDialog("""
                 Do you want to proceed?
                 ❯ 1. Yes
                """) is null);

            Check("options that don't start at 1 are refused", ChatTranscript.ParseDialog("""
                 Do you want to proceed?
                 ❯ 2. Yes
                   3. No
                """) is null);

            Check("a gap in the numbering is refused", ChatTranscript.ParseDialog("""
                 Do you want to proceed?
                 ❯ 1. Yes
                   2. Maybe
                   4. No
                """) is null);

            Check("an empty screen is refused", ChatTranscript.ParseDialog("") is null);
            Check("whitespace is refused", ChatTranscript.ParseDialog("\n\n   \n") is null);

            // A framed dialog with nothing above the options: the frame is not a question.
            var noQuestion = ChatTranscript.ParseDialog("""
                ╭─────────────╮
                │ ❯ 1. Yes    │
                │   2. No     │
                ╰─────────────╯
                """);
            Check("a frame edge doesn't become the title", noQuestion?.Title == "Waiting for input",
                noQuestion?.Title ?? "null");

            // --- markdown ---

            // The failure mode here is visible rather than silent — you see the asterisks —
            // but the mis-fires are not: a rule that eats `snake_case`, or an unclosed
            // delimiter in a half-streamed reply swallowing the rest of the paragraph.

            static ChatMarkdown.MdBlock[] Blocks(string s) => ChatMarkdown.Parse(s).ToArray();
            static ChatMarkdown.MdSpan[] Spans(string s) => ChatMarkdown.Inline(s).ToArray();

            var spans = Spans("Fixed **the clamp** in `OrbArrangement.cs` — see *why* below.");
            Check("bold is found", spans.Any(s => s.Style == ChatMarkdown.MdStyle.Bold && s.Text == "the clamp"));
            Check("inline code is found",
                spans.Any(s => s.Style == ChatMarkdown.MdStyle.Code && s.Text == "OrbArrangement.cs"));
            Check("italic is found", spans.Any(s => s.Style == ChatMarkdown.MdStyle.Italic && s.Text == "why"));
            Check("delimiters are not in the output", spans.All(s => !s.Text.Contains('*') && !s.Text.Contains('`')));
            Check("nothing is lost", string.Concat(spans.Select(s => s.Text))
                == "Fixed the clamp in OrbArrangement.cs — see why below.");

            // The reason underscores are not emphasis. This is the single most common
            // string in this app's own conversations.
            var snake = Spans("the file_path and session_pid fields");
            Check("underscores are left alone", snake.Length == 1 && snake[0].Style == ChatMarkdown.MdStyle.Normal,
                string.Join(" | ", snake.Select(s => s.Style + ":" + s.Text)));

            // A reply being streamed is half-written by definition.
            var unclosed = Spans("this is **not finished");
            Check("an unclosed delimiter stays literal",
                unclosed.Length == 1 && unclosed[0].Text == "this is **not finished",
                string.Join(" | ", unclosed.Select(s => s.Style + ":" + s.Text)));

            Check("an unclosed backtick stays literal",
                Spans("run `dotnet buil").Single().Text == "run `dotnet buil");

            // Markup inside a code span is text, which is why code is matched first.
            var literal = Spans("write `**bold**` like that");
            Check("markup inside code is literal",
                literal.Any(s => s.Style == ChatMarkdown.MdStyle.Code && s.Text == "**bold**"),
                string.Join(" | ", literal.Select(s => s.Style + ":" + s.Text)));

            Check("bold-italic is three stars",
                Spans("***very***").Single().Style == ChatMarkdown.MdStyle.BoldItalic);

            var link = Spans("see [the findings](docs/openclaw-findings.md) for more");
            Check("a link keeps its label and drops its url",
                link.Any(s => s.Style == ChatMarkdown.MdStyle.Link && s.Text == "the findings")
                && link.All(s => !s.Text.Contains(".md")),
                string.Join(" | ", link.Select(s => s.Style + ":" + s.Text)));

            // Blocks.
            var fenced = Blocks("""
                Here is the fix:

                ```csharp
                var x = 1;
                if (x > 0) return;
                ```

                That's it.
                """);

            Check("a fence becomes one code block", fenced.Count(b => b.Kind == ChatMarkdown.MdKind.Code) == 1);
            Check("the code keeps its line breaks",
                fenced.Single(b => b.Kind == ChatMarkdown.MdKind.Code).Text == "var x = 1;\nif (x > 0) return;",
                fenced.Single(b => b.Kind == ChatMarkdown.MdKind.Code).Text.Replace("\n", "\\n"));
            Check("the language is kept",
                fenced.Single(b => b.Kind == ChatMarkdown.MdKind.Code).Marker == "csharp");
            Check("prose around the fence survives",
                fenced.Count(b => b.Kind == ChatMarkdown.MdKind.Paragraph) == 2);

            // Indented code inside an already-indented bubble reads as doubly indented.
            Check("a code block is dedented",
                Blocks("```\n    one\n      two\n```").Single().Text == "one\n  two");

            // A fence that hasn't closed yet is the normal case mid-reply.
            Check("an unclosed fence is still a code block",
                Blocks("```\nvar x = 1;").Single().Kind == ChatMarkdown.MdKind.Code);

            var list = Blocks("""
                Three things:

                - extract the geometry
                - add the test
                - delete the old path
                """);
            Check("bullets become bullet blocks", list.Count(b => b.Kind == ChatMarkdown.MdKind.Bullet) == 3);
            Check("the bullet glyph is not the source dash",
                list.First(b => b.Kind == ChatMarkdown.MdKind.Bullet).Marker == "•");
            Check("bullet text loses its marker",
                list.First(b => b.Kind == ChatMarkdown.MdKind.Bullet).Text == "extract the geometry");

            var ordered = Blocks("4. fourth\n5. fifth");
            Check("an ordered list keeps its own numbering",
                ordered.Length == 2 && ordered[0].Marker == "4." && ordered[1].Marker == "5.",
                string.Join(" | ", ordered.Select(b => b.Marker)));

            Check("a heading is a heading",
                Blocks("## Findings").Single() is { Kind: ChatMarkdown.MdKind.Heading, Text: "Findings", Depth: 2 });

            Check("a hash with no space is not a heading",
                Blocks("#hashtag not a heading").Single().Kind == ChatMarkdown.MdKind.Paragraph);

            // Reflowing a table destroys it; monospace at least keeps the columns.
            var table = Blocks("| a | b |\n| - | - |\n| 1 | 2 |");
            Check("a table is kept verbatim as code",
                table.Single().Kind == ChatMarkdown.MdKind.Code && table.Single().Text.Split('\n').Length == 3);

            Check("a rule draws nothing", Blocks("one\n\n---\n\ntwo").All(b => b.Text != "---"));

            Check("wrapped prose joins into one paragraph",
                Blocks("a line\nand its continuation").Single().Text == "a line and its continuation");

            Check("a blank line separates paragraphs", Blocks("first\n\nsecond").Length == 2);

            Check("empty text is no blocks", Blocks("").Length == 0 && Blocks("   \n  ").Length == 0);

            // --- agent colours ---

            // The property that matters is stability: an agent that changed colour between
            // launches would be worse than every agent sharing one, because the ring would
            // be actively misleading rather than merely uninformative. That rules out
            // string.GetHashCode(), which is randomised per process — a test can't catch
            // that within one process, so the hash is hand-written and pinned below.
            var agents = new[] { "main", "aurora", "nova", "asher", "main-2", "ops", "scribe", "warden" };

            Check("a colour is stable within a run",
                agents.All(a => AgentPalette.HexFor(a) == AgentPalette.HexFor(a)));

            // Pinned. If these change, every agent silently changes colour on upgrade —
            // which is the thing the whole derived-not-stored design exists to prevent. A
            // deliberate change to the palette means updating these on purpose.
            Check("the hash is pinned to known values",
                AgentPalette.HexFor("main") == "#5FBFD7"
                && AgentPalette.HexFor("aurora") == "#D7915F"
                && AgentPalette.HexFor("nova") == "#5F91D7",
                $"main={AgentPalette.HexFor("main")} aurora={AgentPalette.HexFor("aurora")} "
                + $"nova={AgentPalette.HexFor("nova")}");

            // The reason Assign exists: agents whose hashes land on the same anchor must
            // not ship as identical orbs.
            var pair = AgentPalette.Assign(new[] { "warden", "main-3" });
            Check("colliding agents are separated", pair["warden"] != pair["main-3"],
                $"warden={pair["warden"]} main-3={pair["main-3"]}");

            var assigned = AgentPalette.Assign(agents);
            Check("every agent in a set gets a colour", assigned.Count == agents.Length);
            Check("a set's colours are all distinct", assigned.Values.Distinct().Count() == agents.Length,
                string.Join(" ", assigned.Select(kv => kv.Key + "=" + kv.Value)));

            Check("assignment doesn't depend on listing order",
                AgentPalette.Assign(agents.Reverse()).OrderBy(kv => kv.Key).SequenceEqual(
                    assigned.OrderBy(kv => kv.Key)));

            Check("duplicates in the input are harmless",
                AgentPalette.Assign(new[] { "main", "main", "nova" }).Count == 2);

            // Separation has to hold across the wrap: hue 350 and hue 10 are 20 apart.
            static int HueOf(string hex)
            {
                int r = Convert.ToInt32(hex[1..3], 16), g = Convert.ToInt32(hex[3..5], 16), b = Convert.ToInt32(hex[5..7], 16);
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max == min) return 0;
                double h = max == r ? (g - b) / (double)(max - min)
                    : max == g ? 2 + (b - r) / (double)(max - min)
                    : 4 + (r - g) / (double)(max - min);
                return ((int)Math.Round(h * 60) + 360) % 360;
            }

            var hues = assigned.Values.Select(HueOf).ToList();
            var closest = 360;
            for (var a = 0; a < hues.Count; a++)
                for (var b = a + 1; b < hues.Count; b++)
                {
                    var d = Math.Abs(hues[a] - hues[b]) % 360;
                    closest = Math.Min(closest, Math.Min(d, 360 - d));
                }

            // Thirty degrees, not the 45 that dividing the circle by eight suggests:
            // placement starts from wherever each id hashes to rather than from an even
            // grid, so the achievable separation is well below the theoretical one. What
            // matters is that it is a real step — this is the assertion that would have
            // caught the 13° pair a person called "the same colour".
            Check("eight agents are at least 30° apart", closest >= 30,
                $"closest pair is {closest}° apart in {string.Join(",", hues.OrderBy(h => h))}");

            // Separation scales with the count rather than being a fixed number: fewer
            // agents must be spread *further*, not the same distance with gaps left over.
            var three = AgentPalette.Assign(new[] { "one", "two", "three" }).Values.Select(HueOf).ToList();
            var threeClosest = 360;
            for (var a = 0; a < three.Count; a++)
                for (var b = a + 1; b < three.Count; b++)
                {
                    var d = Math.Abs(three[a] - three[b]) % 360;
                    threeClosest = Math.Min(threeClosest, Math.Min(d, 360 - d));
                }

            Check("three agents are spread further than eight", threeClosest >= 55,
                $"closest of three is {threeClosest}°");

            // Crowded, but every one still distinct and nobody dropped.
            var crowd = AgentPalette.Assign(Enumerable.Range(0, 40).Select(n => "agent-" + n));
            Check("forty agents are all handled", crowd.Count == 40);
            Check("forty agents are all distinct", crowd.Values.Distinct().Count() == 40,
                crowd.Values.Distinct().Count().ToString());

            // The whole wheel is in use — not a fixed set of named colours.
            var manyHues = crowd.Values.Select(HueOf).ToList();
            Check("colours are spread across the spectrum",
                manyHues.Any(h => h < 60) && manyHues.Any(h => h is >= 60 and < 180)
                && manyHues.Any(h => h is >= 180 and < 300) && manyHues.Any(h => h >= 300),
                string.Join(",", manyHues.OrderBy(h => h)));

            Check("every colour is a valid #RRGGBB",
                agents.All(a =>
                {
                    var hex = AgentPalette.HexFor(a);
                    return hex.Length == 7 && hex[0] == '#'
                        && hex[1..].All(Uri.IsHexDigit) && hex[1..] == hex[1..].ToUpperInvariant();
                }));

            Check("different agents get different colours",
                agents.Select(AgentPalette.HexFor).Distinct().Count() == agents.Length,
                string.Join(" ", agents.Select(a => a + "=" + AgentPalette.HexFor(a))));

            // Ids that differ only in their last character are the realistic collision:
            // "main", "main-1", "main-2" is exactly the case that made four orbs read "M".
            Check("near-identical ids are well separated",
                new[] { "main", "main-1", "main-2", "main-3" }.Select(AgentPalette.HexFor).Distinct().Count() == 4);

            // Every generated colour sits on Claude Code's own saturation/value surface, so
            // it reads as the same kind of colour as a /color one — see AgentPalette.
            foreach (var agent in agents)
            {
                var hex = AgentPalette.HexFor(agent);
                var r = Convert.ToInt32(hex[1..3], 16);
                var g = Convert.ToInt32(hex[3..5], 16);
                var b = Convert.ToInt32(hex[5..7], 16);

                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));

                // #D75F5F, #5F87D7 and the rest of Claude's palette are all max 215,
                // min 95 — the same numbers these must land on, within rounding.
                Check($"{agent} sits on Claude's palette surface",
                    Math.Abs(max - 215) <= 1 && Math.Abs(min - 95) <= 1,
                    $"{hex} -> max {max}, min {min}");
            }

            Check("an empty id doesn't throw", AgentPalette.HexFor("").StartsWith('#'));

            // --- session kinds ---

            // The mistake this can make is silent and directional: a channel shown as a
            // direct message says a room other people can read is private, and nothing on
            // screen contradicts it. So the unrecognised cases assert Unknown rather than a
            // guess at the commoner of the two.

            static SessionKind Kind(string key, string? chatType = null) =>
                OpenClawSessionKind.From(key, chatType);

            // Real keys, from docs/openclaw-findings.md.
            Check("a cron job is a cron job",
                Kind("agent:main:cron:2f54203e-1c2f-4a1e-9c0e-2b1d8e5a7c31") == SessionKind.Cron);

            Check("cron wins over anything attached to it",
                Kind("agent:main:cron:2f54203e", "direct") == SessionKind.Cron,
                "the key is structural; chatType must not override it");

            Check("an agent's own session is Main",
                Kind("agent:alexis:main") == SessionKind.Main);

            Check("a dashboard session is Main",
                Kind("agent:main:dashboard:9f1c2a3b-4d5e-4f60-8a7b-1c2d3e4f5a6b") == SessionKind.Main
                && Kind("agent:main:dashboard:9f1c2a3b", "direct") == SessionKind.Main);

            Check("a DM is Direct",
                Kind("agent:main:discord:direct:200000000000000001") == SessionKind.Direct);

            Check("a channel is a Channel",
                Kind("agent:main:discord:channel:1900000000000000001") == SessionKind.Channel);

            // origin.chatType is what separates these two when the key says only the
            // surface, which is the usual case.
            Check("chatType decides when the key only names a surface",
                Kind("agent:main:discord", "channel") == SessionKind.Channel
                && Kind("agent:main:discord", "direct") == SessionKind.Direct);

            Check("chatType is preferred over the key's fourth segment",
                Kind("agent:main:discord:direct:2467", "channel") == SessionKind.Channel,
                "origin is the gateway's own word for the conversation");

            Check("the surfaces' other words for a group are all Channel",
                new[] { "channel", "group", "guild" }.All(t => Kind("agent:m:slack", t) == SessionKind.Channel));

            Check("the surfaces' other words for a DM are all Direct",
                new[] { "direct", "dm", "im" }.All(t => Kind("agent:m:slack", t) == SessionKind.Direct));

            Check("case doesn't matter", Kind("agent:m:CRON:x") == SessionKind.Cron
                && Kind("agent:m:slack", "Direct") == SessionKind.Direct);

            // Everything below must decline to guess.
            Check("an unrecognised chatType is Unknown",
                Kind("agent:main:discord", "thread") == SessionKind.Unknown);
            Check("a surface with no chatType is Unknown",
                Kind("agent:main:discord") == SessionKind.Unknown);
            Check("an empty key is Unknown", Kind("") == SessionKind.Unknown);
            Check("a non-agent key is Unknown", Kind("something:else:entirely") == SessionKind.Unknown);
            Check("a null key is Unknown", OpenClawSessionKind.From(null, null) == SessionKind.Unknown);

            // --- heartbeat sessions ---

            // Which sessions the gateway's heartbeat drives. See OpenClawHeartbeat for why
            // this is answered from the key's shape rather than from anything the gateway
            // says about a turn: it says nothing about them at all.
            //
            // The keys below are real, read off a live gateway rather than invented — the
            // same rule the dialog fixtures follow, and for the same reason.

            Check("an agent's main session is where its heartbeat lands",
                OpenClawHeartbeat.Is("agent:main:main"));

            Check("every agent's main session, not just the one called main",
                new[] { "agent:alexis:main", "agent:comfyui:main", "agent:ea-hope:main" }
                    .All(k => OpenClawHeartbeat.Is(k)));

            // The distinction the badge exists to draw: these are the orbs that light up
            // because somebody typed something.
            Check("a discord channel is not a heartbeat",
                !OpenClawHeartbeat.Is("agent:main:discord:channel:1900000000000000001"));

            Check("a discord DM is not a heartbeat",
                !OpenClawHeartbeat.Is("agent:main:discord:direct:200000000000000001"));

            // A cron job is scheduled too, and is deliberately *not* a heartbeat: it has its
            // own session, its own label and its own clock badge, and the two would be
            // telling you the same thing twice while hiding which one you had.
            Check("a cron session is not a heartbeat",
                !OpenClawHeartbeat.Is(
                    "agent:main:cron:2f54203e-6099-4c31-b9f4-d70b04e82ae6",
                    "Cron: stalled-session-watchdog"));

            Check("nor are the other surfaces a real gateway had",
                new[] { "agent:main:avatar", "agent:main:clitest", "agent:comfyui:avatar-build",
                        "agent:main:subagent:b0bcedf4-08ca-468d-9800-cfeed922400e" }
                    .All(k => !OpenClawHeartbeat.Is(k)));

            // Untested against a real payload — the gateway's system-owned job is behind a
            // scope this app doesn't hold, so this is the shape its documented name would
            // arrive in rather than one that was observed. Asserted anyway so the intent
            // survives somebody rewriting the label handling.
            Check("a session labelled with the gateway's own job name is a heartbeat",
                OpenClawHeartbeat.Is("agent:main:cron:1d82", "Heartbeat (main)")
                && OpenClawHeartbeat.Is("agent:main:cron:1d82", "Cron: heartbeat"));

            Check("a heartbeat keyed on the surface is one too",
                OpenClawHeartbeat.Is("agent:main:heartbeat"));

            Check("case doesn't matter to either half",
                OpenClawHeartbeat.Is("agent:main:MAIN") && OpenClawHeartbeat.Is("x", "HEARTBEAT (main)"));

            // Prefix, not substring: a job somebody named after the heartbeat is not it.
            Check("a job that merely mentions the word is not the heartbeat",
                !OpenClawHeartbeat.Is("agent:main:cron:1d82", "Cron: check-heartbeat-health"));

            Check("an empty or null key is not a heartbeat",
                !OpenClawHeartbeat.Is("") && !OpenClawHeartbeat.Is(null) && !OpenClawHeartbeat.Is(null, null));

            // A local Claude Code session never reaches this, but a key that isn't the
            // gateway's shape at all must not be read as one.
            Check("a non-agent key is not a heartbeat",
                !OpenClawHeartbeat.Is("something:else:main"));

            // --- the beat itself ---

            // The curve both the orb badge and the panel chip beat on. Asserted because it
            // is the whole of the signal — a heart that came out flat, or that never rested,
            // would look like a rendering bug rather than a wrong number.

            Check("the heart rests for most of the cycle",
                OpenClawHeartbeat.Beat(0.75) == 0 && OpenClawHeartbeat.Beat(0.95) == 0);

            Check("it peaks near the start of the cycle",
                Math.Abs(OpenClawHeartbeat.Beat(0.11) - 1.0) < 0.001);

            // Lub-dub: the second contraction is real, and smaller than the first.
            Check("there is a second, smaller beat",
                OpenClawHeartbeat.Beat(0.37) > 0.5 && OpenClawHeartbeat.Beat(0.37) < OpenClawHeartbeat.Beat(0.11));

            Check("it never leaves 0..1",
                Enumerable.Range(0, 400).Select(i => OpenClawHeartbeat.Beat(i / 100.0))
                    .All(v => v is >= 0 and <= 1));

            // The callers hand it elapsed-time-over-period rather than a wrapped phase, so
            // wrapping is part of the contract and not the caller's job.
            Check("the phase wraps, so successive cycles are identical",
                Math.Abs(OpenClawHeartbeat.Beat(3.11) - OpenClawHeartbeat.Beat(0.11)) < 1e-9);

            // --- rooms ---

            // Every agent in a channel has to agree on the room key, or a room fragments
            // into one orb per agent and the grouping is worse than none.
            static string? Room(string key) => OpenClawSessionKind.RoomOf(key);

            Check("agents in one channel agree on the room",
                Room("agent:aurora:discord:channel:1900000000000000001")
                == Room("agent:nova:discord:channel:1900000000000000001")
                && Room("agent:nova:discord:channel:1900000000000000001") == "discord:1900000000000000001");

            Check("different channels are different rooms",
                Room("agent:nova:discord:channel:111") != Room("agent:nova:discord:channel:222"));

            // The same channel id on two surfaces is not the same room.
            Check("the surface is part of the room",
                Room("agent:z:discord:channel:111") != Room("agent:z:slack:channel:111"));

            // A DM is not a room: two people talking privately is not somewhere others
            // can be standing.
            Check("a direct message is not a room", Room("agent:main:discord:direct:2467") is null);
            Check("a cron job is not a room", Room("agent:main:cron:2f54203e") is null);
            Check("an agent's own session is not a room", Room("agent:alexis:main") is null);
            Check("a malformed key is not a room",
                Room("") is null && Room("agent:z:discord") is null && Room("nonsense") is null);

            // A session whose "channel" is another session's key, seen on a real gateway.
            // It reports itself as a group and carries no channel name, and treating it as
            // a room split #arch into two — the real one and a nameless twin.
            Check("a key nested inside a key is not a room",
                Room("agent:main:discord:channel:agent:ea-hope:discord:channel:1900000000000000002") is null);

            Check("the real session for that channel still is a room",
                Room("agent:ea-hope:discord:channel:1900000000000000002") == "discord:1900000000000000002");

            // A channel id containing a colon must not be truncated into a different room.
            Check("a colon in the channel id survives",
                Room("agent:z:matrix:channel:!abc:server.org") == "matrix:!abc:server.org");

            // --- the codex transcript ---

            // Real rows from ~/.codex/sessions/2026/08/19/rollout-*.jsonl, trimmed of the
            // fields none of this reads. Ordinals and timestamps are the originals: the
            // ordinal is what the reader dedupes on, so a fixture that invented one would
            // be testing the wrong thing.
            //
            // Codex writes the same conversation twice — see CodexTranscript's header —
            // so the response_item row below is here to be ignored, and it is the largest
            // single reason this parser exists as its own file rather than a mode of
            // ChatTranscript.
            const string CxUser =
                """{"timestamp":"2026-08-19T16:57:08.663Z","ordinal":9,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"01a01af4-8937","content":[{"type":"text","text":"convert this from a claude project to codex","text_elements":[]}]}}}""";

            const string CxAgent =
                """{"timestamp":"2026-08-19T16:57:11.063Z","ordinal":12,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"msg_000c72","content":[{"type":"Text","text":"I'll inventory the repository's Claude-specific files."}],"phase":"commentary"}}}""";

            const string CxAgentFinal =
                """{"timestamp":"2026-08-19T17:03:05.443Z","ordinal":310,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"msg_000c73","content":[{"type":"Text","text":"Converted the workspace from Claude to Codex."}],"phase":"final_answer"}}}""";

            const string CxReasoning =
                """{"timestamp":"2026-08-19T16:57:10.334Z","ordinal":10,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"Reasoning","id":"rs_000c72","summary_text":[],"raw_content":[]}}}""";

            const string CxExec =
                """{"timestamp":"2026-08-19T16:57:14.073Z","ordinal":15,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"exec-a5a4bb49","process_id":"65218","command":["/bin/zsh","-lc","sed -n '1,240p' SKILL.md"],"cwd":"file:///Users/w/Source/AIEA","parsed_cmd":[{"type":"read","cmd":"sed -n '1,240p' SKILL.md","name":"SKILL.md"}],"aggregated_output":"...240 lines..."}}}""";

            const string CxEdit =
                """{"timestamp":"2026-08-19T16:58:45.136Z","ordinal":113,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"FileChange","id":"exec-4c46b3cb","changes":{"/Users/w/Source/AIEA/backend/utils/config.py":{"type":"update","unified_diff":"@@ -1,3 +1,3 @@\n-import anthropic\n+from openai import OpenAI\n"}}}}}""";

            const string CxSearch =
                """{"timestamp":"2026-08-19T16:57:28.434Z","ordinal":37,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"Extension","kind":"web.search","id":"exec-9b827849","query":"site:developers.openai.com/codex AGENTS.md","action":{"type":"search"},"results":[{"type":"text_result","snippet":"lots of bytes"}]}}}""";

            // The wire log. Same content, and on a real rollout it is half the rows and
            // nearly all the bytes.
            const string CxResponseItem =
                """{"timestamp":"2026-08-19T16:57:11.064Z","ordinal":13,"type":"response_item","payload":{"type":"message","id":"msg_000c72","role":"assistant","content":[{"type":"output_text","text":"I'll inventory the repository's Claude-specific files."}]}}""";

            // Not a conversation: the session header, which carries the whole system
            // prompt and is the first row of every rollout.
            const string CxSessionMeta =
                """{"timestamp":"2026-08-19T16:57:08.290Z","ordinal":0,"type":"session_meta","payload":{"session_id":"01a01af4","cwd":"/Users/w/Source/AIEA","originator":"codex-tui","cli_version":"0.148.0"}}""";

            var cx = CodexTranscript.Map(new[]
            {
                CxSessionMeta, CxUser, CxReasoning, CxAgent, CxExec, CxSearch, CxEdit,
                CxResponseItem, CxAgentFinal
            });

            // Six of the nine rows are things a person said or watched happen. The empty
            // Reasoning, the response_item duplicate and the session header are not.
            Check("codex maps exactly the six displayable rows", cx.Count == 6,
                "got " + cx.Count + ": " + string.Join(" | ", cx.Select(r => r.Turn.Role + ":" + Head(r.Turn.Text))));

            Check("codex keeps file order",
                cx.Count == 6 && cx[0].Turn.Role == ChatRole.User && cx[5].Turn.Role == ChatRole.Assistant,
                string.Join(" | ", cx.Select(r => r.Turn.Role.ToString())));

            // The one that would cost every reply. UserMessage content blocks are typed
            // "text" and AgentMessage content blocks are typed "Text", which is not a
            // transcription slip in the fixtures above — it is what Codex writes. A parser
            // that assumes either casing applies to both drops half the conversation and
            // says nothing about it.
            Check("a user message uses lowercase \"text\"",
                cx.Any(r => r.Turn.Role == ChatRole.User && r.Turn.Text.StartsWith("convert this")));

            Check("an agent message uses capital \"Text\"",
                cx.Any(r => r.Turn.Role == ChatRole.Assistant && r.Turn.Text.StartsWith("I'll inventory")));

            Check("the casing is not accepted the other way round",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"Text","text":"wrong case"}]}}}""",
                    """{"ordinal":2,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"text","text":"wrong case"}]}}}"""
                }).Count == 0);

            Check("both agent phases are shown",
                cx.Count(r => r.Turn.Role == ChatRole.Assistant) == 2,
                "commentary and final_answer are both things the TUI drew");

            // --- what a tool call reads as ---

            Check("a command reads as its parsed form",
                cx.Any(r => r.Turn.Text == "· exec  sed -n '1,240p' SKILL.md"),
                string.Join(" | ", cx.Select(r => r.Turn.Text)));

            Check("a command with no parsed form loses the shell wrapper",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","command":["/bin/zsh","-lc","git status --short"]}}}"""
                })[0].Turn.Text == "· exec  git status --short");

            Check("an edit names the file, not its path",
                cx.Any(r => r.Turn.Text == "· edit  config.py"),
                string.Join(" | ", cx.Select(r => r.Turn.Text)));

            Check("an edit touching several files says how many",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"FileChange","changes":{"/a/one.cs":{"type":"update"},"/b/two.cs":{"type":"add"},"/c/three.cs":{"type":"delete"}}}}}"""
                })[0].Turn.Text == "· edit  one.cs +2");

            // Not every web action has an argument: `action.type` "other" comes with an
            // empty query, and three of the fifteen Extension items on a real rollout were
            // that. A bare "· web.search" is the honest reading — the session did reach for
            // the web — and dropping the row would lose that it happened at all.
            Check("a web action with no query is still a row",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"Extension","kind":"web.search","query":"","action":{"type":"other"}}}}"""
                }) is { Count: 1 } bare && bare[0].Turn.Text == "· web.search");

            Check("a web search reads as its kind and query",
                cx.Any(r => r.Turn.Text == "· web.search  site:developers.openai.com/codex AGENTS.md"),
                string.Join(" | ", cx.Select(r => r.Turn.Text)));

            Check("a long command is truncated and its whitespace collapsed",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","parsed_cmd":[{"cmd":"rg    --files   --hidden --glob '!**/node_modules/**' --glob '!**/target/**' ."}]}}}"""
                })[0].Turn.Text is { Length: < 70 } trimmed
                && trimmed.EndsWith('…')
                && !trimmed.Contains("  --files"));

            // --- reasoning ---

            Check("an empty reasoning summary adds nothing",
                !cx.Any(r => r.Turn.Role == ChatRole.System && r.Turn.Text.Length == 0));

            // Never observed populated on a real rollout — 109 reasoning items, all empty
            // — so this pins the behaviour for when it starts happening rather than
            // describing something seen.
            Check("a populated reasoning summary is a system turn",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"Reasoning","summary_text":["Checking the clamp order."]}}}"""
                }) is { Count: 1 } reasoned
                && reasoned[0].Turn.Role == ChatRole.System
                && reasoned[0].Turn.Text == "Checking the clamp order.");

            // --- what identifies a row ---

            // The ordinal, not the item id. Ids are absent on some items and shared
            // between a call and its output on others; the ordinal is dense and unique.
            Check("a row is keyed by its ordinal",
                cx.Any(r => r.Uuid == "9") && cx.Any(r => r.Uuid == "15"),
                string.Join(",", cx.Select(r => r.Uuid ?? "null")));

            // --- the pre-filter has to agree with the mapper ---

            // Same invariant the Claude suite pins, and for the same reason: if
            // IsInteresting says no to something MapRow would have mapped, that message is
            // dropped and nothing anywhere reports it.
            foreach (var row in new[]
                     {
                         CxSessionMeta, CxUser, CxReasoning, CxAgent, CxExec, CxSearch,
                         CxEdit, CxResponseItem, CxAgentFinal
                     })
            {
                if (CodexTranscript.IsInteresting(row)) continue;

                Check("the codex pre-filter drops a row the mapper would have shown",
                    CodexTranscript.Map(new[] { row }).Count == 0, Head(row));
            }

            Check("the wire log is filtered out before it is parsed",
                !CodexTranscript.IsInteresting(CxResponseItem));

            // --- rows too large to parse ---

            // A CommandExecution that ran something noisy. The largest measured on a real
            // rollout was 1,046,104 bytes, with the output starting at offset 503,008 and
            // the command itself at 311 — so the head is read and the rest is never
            // touched. Above two megabytes the parse is skipped entirely; this row is
            // deliberately over that line.
            var huge =
                """{"timestamp":"2026-08-19T16:57:14.073Z","ordinal":15,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","id":"exec-1","command":["/bin/zsh","-lc","cat big.log"],"parsed_cmd":[{"type":"unknown","cmd":"cat big.log"}],"aggregated_output":"""
                + "\"" + new string('x', 3 * 1024 * 1024) + "\"}}}";

            var oversized = CodexTranscript.Map(new[] { huge });

            Check("a row too large to parse still names its command",
                oversized.Count == 1 && oversized[0].Turn.Text == "· exec  cat big.log",
                oversized.Count == 0 ? "dropped" : oversized[0].Turn.Text);

            Check("a row too large to parse keeps its ordinal",
                oversized.Count == 1 && oversized[0].Uuid == "15",
                oversized.Count == 0 ? "dropped" : oversized[0].Uuid ?? "null");

            Check("a row too large to parse keeps its timestamp",
                oversized.Count == 1 && oversized[0].Turn.At.Year == 2026 && oversized[0].Turn.At.Month == 8);

            // --- nothing here may throw ---

            Check("an unknown item type is skipped",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"SomethingNewInCodex","content":[{"type":"Text","text":"x"}]}}}"""
                }).Count == 0);

            Check("injected context arriving as a user message is dropped",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"text","text":"<environment_context>\n  <cwd>/Users/w</cwd>\n</environment_context>"}]}}}"""
                }).Count == 0);

            Check("a truncated row is skipped rather than thrown on",
                CodexTranscript.Map(new[] { """{"ordinal":1,"type":"event_msg","payload":{"type":"item_comple""" }).Count == 0);

            Check("a row that isn't json is skipped",
                CodexTranscript.Map(new[] { "item_completed but not json at all" }).Count == 0);

            Check("an item with no content is skipped",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage"}}}"""
                }).Count == 0);

            Check("whitespace-only text is not a turn",
                CodexTranscript.Map(new[]
                {
                    """{"ordinal":1,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"Text","text":"   "}]}}}"""
                }).Count == 0);

            Check("an empty list maps to nothing", CodexTranscript.Map(Array.Empty<string>()).Count == 0);

            // --- grok ---
            //
            // Shapes from a live grok 1.0.13 updates.jsonl. Consecutive chunks of
            // the same kind stitch; tool_call_update and the housekeeping kinds
            // do not become turns. See docs/grok-findings.md.

            static string GrokEnvelope(string update) =>
                "{\"timestamp\":1788198313,\"method\":\"session/update\","
                + "\"params\":{\"sessionId\":\"s1\",\"update\":" + update + "}}";

            var grok = GrokTranscript.Map(new[]
            {
                GrokEnvelope("{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Hello \"}}"),
                GrokEnvelope("{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"world\"}}"),
                GrokEnvelope("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"t1\",\"title\":\"grep\",\"kind\":\"search\"}"),
                GrokEnvelope("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t1\",\"status\":\"completed\"}"),
                GrokEnvelope("{\"sessionUpdate\":\"hook_execution\",\"event_name\":\"session_start\"}"),
                GrokEnvelope("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"done\"}}"),
            });

            Check("grok stitches chunks and drops housekeeping", grok.Count == 3,
                "got " + grok.Count + ": " + string.Join(" | ", grok.Select(r => r.Turn.Role + ":" + Head(r.Turn.Text))));
            Check("grok user chunks become one turn",
                grok.Count == 3 && grok[0].Turn.Role == ChatRole.User && grok[0].Turn.Text == "Hello world");
            Check("grok tool_call is a system line",
                grok.Count == 3 && grok[1].Turn.Role == ChatRole.System && grok[1].Turn.Text == "· grep");

            // --- codex's approval dialog ---

            // Transcribed from `tmux capture-pane -p` against a real Codex 0.148 pane,
            // escalating a write outside its sandbox. Not composed here — the same rule
            // that produced the two Claude Code fixtures above, and it earned its keep
            // again: this parser was expected to need a Codex-specific rewrite, because the
            // strings in Codex's own binary advertise "Allow" / "Allow for this session"
            // wording with a description line under each, which would have broken the
            // contiguous-1..n rule outright. The TUI does not use that shape at all.
            const string CodexApproval = """
              Would you like to run the following command?

              Environment: local

              Reason: Allow creating the requested empty file cb-approval-probe in your home directory?

              $ touch $HOME/cb-approval-probe

            › 1. Yes, proceed (y)
              2. Yes, and don't ask again for commands that start with `touch $HOME/cb-approval-probe` (p)
              3. No, and tell Codex what to do differently (esc)

              Press enter to confirm or esc to cancel
            """;

            var codexDialog = ChatTranscript.ParseDialog(CodexApproval);

            Check("codex's approval prompt parses with the shared parser",
                codexDialog is not null,
                "a Codex-specific parser would be dead code if this holds, and a dead click if it doesn't");

            Check("codex's three options come through in order",
                codexDialog is { Options.Count: 3 }
                && codexDialog.Options[0].Key == "1"
                && codexDialog.Options[1].Key == "2"
                && codexDialog.Options[2].Key == "3",
                codexDialog is null ? "no dialog" : string.Join(" | ", codexDialog.Options.Select(o => o.Key + ":" + o.Label)));

            // The labels carry Codex's own key hints — (y), (p), (esc). Kept verbatim
            // rather than stripped: they are what the terminal shows, and a button whose
            // label differs from the terminal's is the start of pressing the wrong thing.
            Check("the option labels are the dialog's own wording",
                codexDialog is not null
                && codexDialog.Options[0].Label == "Yes, proceed (y)"
                && codexDialog.Options[2].Label.StartsWith("No, and tell Codex", StringComparison.Ordinal),
                codexDialog is null ? "no dialog" : codexDialog.Options[0].Label);

            // The hint line below the options must not be read as a fourth one, and must
            // not be mistaken for the input box's rule — which is what tells a real dialog
            // from a numbered list the model happened to write in prose.
            Check("the confirm hint below the options is not an option",
                codexDialog is { Options.Count: 3 });

            // Walking up past the blank line lands on the command rather than the
            // question. That is the more useful of the two — "Would you like to run the
            // following command?" says nothing a button doesn't — so it is pinned rather
            // than worked around.
            Check("the title is the command being approved",
                codexDialog?.Title == "$ touch $HOME/cb-approval-probe",
                codexDialog?.Title ?? "null");


            return failures;

            static string Head(string s)
            {
                var line = s.Split('\n')[0];
                return line.Length > 40 ? line[..40] + "…" : line;
            }
        }
    }
}

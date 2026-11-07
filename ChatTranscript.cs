using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // Turning what Claude Code writes down into what the chat panel shows.
    //
    // Pure on purpose, and kept out of ClaudeCodeChatSession for the same reason
    // the geometry is kept out of SessionManager: no files, no windows, no
    // settings, no dispatcher — just text in and turns out, so it can be tested
    // by `dotnet run --project tests/TranscriptTests` rather than by opening a
    // panel and reading it.
    //
    // Two jobs, and they are less similar than they look:
    //
    //  * Map() reads the transcript, which is a documented-by-example append-only
    //    JSONL file. Unfamiliar rows are skipped, so a Claude Code upgrade that
    //    adds a row type degrades rather than breaks.
    //
    //  * ParseDialog() reads the *screen*, because a permission prompt is drawn
    //    by the TUI and never written to the transcript. That one is strict to
    //    the point of rudeness — anything unexpected returns null — because its
    //    output becomes buttons that send keystrokes, and a button whose label
    //    disagrees with what it sends is the one failure this must not have.
    public static class ChatTranscript
    {
        public readonly record struct Row(string? Uuid, ChatTurn Turn);

        // --- the transcript ---

        public static List<Row> Map(IEnumerable<string> lines)
        {
            var mapped = new List<Row>();

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '{') continue;

                // Parsing every row would mean parsing the ones that carry the
                // bytes: a file-history snapshot or an attachment is routinely
                // larger than the whole conversation around it, and none of the
                // types skipped here can produce a turn. A substring test rather
                // than a parse for the same reason TranscriptReader uses one —
                // the point is to not build a JsonDocument for a megabyte that
                // is about to be discarded.
                if (!IsInteresting(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    MapRow(doc.RootElement, mapped);
                }
                catch
                {
                    // A row we can't parse is a row we don't show.
                }
            }

            return mapped;
        }

        // A false positive costs nothing: a tool result whose text happens to
        // contain one of these parses to no turns anyway, because it has no text
        // blocks of its own. A false negative would silently drop a message, so
        // the test that matters is that these three strings are exactly the row
        // types MapRow handles.
        public static bool IsInteresting(string line) =>
            line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"queue-operation\"", StringComparison.Ordinal);

        private static void MapRow(JsonElement root, List<Row> into)
        {
            var type = Str(root, "type");
            if (type is null) return;

            // Subagent chatter. One team run produces thousands of these and
            // they would bury the conversation the panel was opened to read.
            if (Bool(root, "isSidechain")) return;

            var uuid = Str(root, "uuid");
            var at = Time(root);

            switch (type)
            {
                case "user":
                    MapUser(root, uuid, at, into);
                    return;

                case "assistant":
                    MapAssistant(root, uuid, at, into);
                    return;

                // Sent while the session was busy. Claude Code holds it and
                // picks it up at the end of the turn, and showing that is the
                // difference between "did that send?" and knowing it did.
                case "queue-operation" when Str(root, "operation") == "enqueue":
                {
                    var queued = Str(root, "content");
                    if (string.IsNullOrWhiteSpace(queued) || IsNoise(queued)) return;

                    into.Add(new Row(uuid, new ChatTurn
                    {
                        Role = ChatRole.User,
                        Text = queued.Trim(),
                        IsComplete = true,
                        At = at
                    }));
                    return;
                }
            }
        }

        internal const string PeerPrefix = "Another Claude session sent a message:\n";
        internal const string PeerSuffixMarker = "\n\nThis came from another Claude session";

        // Measured 2026-10-14 (CB-266) on a real Remote Control session: the
        // content is the prefix, our text, a blank line, then a paragraph of
        // provenance and a warning against permission laundering. Only the text
        // between is ours. Null for anything that is not that wrapper, so a
        // caller never shows boilerplate it failed to recognise.
        internal static string? UnwrapPeerMessage(string content)
        {
            if (!content.StartsWith(PeerPrefix, StringComparison.Ordinal)) return null;

            var body = content[PeerPrefix.Length..];
            var cut = body.IndexOf(PeerSuffixMarker, StringComparison.Ordinal);
            return cut < 0 ? null : body[..cut];
        }

        private static bool IsPeerOrigin(JsonElement root) =>
            root.TryGetProperty("origin", out var origin)
            && origin.ValueKind == JsonValueKind.Object
            && Str(origin, "kind") == "peer";

        private static void MapUser(JsonElement root, string? uuid, DateTimeOffset at, List<Row> into)
        {
            // Rows Claude Code writes to itself — hook output, command
            // scaffolding, the transcript's own bookkeeping.
            //
            // One isMeta row is not bookkeeping: a message another session (or
            // this app, over Remote Control, CB-266) sends in is written as
            // isMeta with origin.kind "peer" and its text wrapped in a sentence
            // of provenance. It is the user's own sentence as far as the panel
            // is concerned, so it is unwrapped and kept, uuid and all.
            var meta = Bool(root, "isMeta");
            if (meta && !IsPeerOrigin(root)) return;
            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("content", out var content)) return;

            if (meta)
            {
                // A peer row that is not the shape measured stays dropped, as
                // every isMeta row was before: better a missing bubble than the
                // provenance boilerplate shown as if the user had typed it.
                if (content.ValueKind == JsonValueKind.String
                    && UnwrapPeerMessage(content.SafeString() ?? "") is { } ours)
                    AddText(ours, ChatRole.User, uuid, at, into);

                return;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                AddText(content.SafeString(), ChatRole.User, uuid, at, into);
                return;
            }

            if (content.ValueKind != JsonValueKind.Array) return;

            // A pasted picture arrives as two sibling blocks in one message —
            // its own "image" block, and a "text" block carrying Claude
            // Code's own "[Image #1]" placeholder for wherever its own UI
            // would draw the picture inline. Confirmed against a real
            // transcript row, not assumed: pasting a picture through this
            // panel and typing its path (see LocalCliChatSession) produced
            // exactly this shape. Whether Codex's rollout format matches is
            // unverified — CodexTranscript's own Map is untouched, so a
            // picture in a Codex turn still shows as whatever text Codex
            // wrote, with no thumbnail.
            //
            // Only the first image is kept: a turn carries one, the same
            // limit a received picture already has.
            byte[]? image = null;
            var textBlocks = new List<string?>();

            foreach (var block in content.EnumerateArray())
            {
                var kind = Str(block, "type");

                if (kind == "image")
                {
                    image ??= DecodeImage(block);
                    continue;
                }

                // tool_result blocks are the other half of a tool_use that is
                // already one line in the panel. Rendering them would put the
                // contents of every file read into the conversation.
                if (kind != "text") continue;

                textBlocks.Add(Str(block, "text"));
            }

            if (textBlocks.Count == 0)
            {
                // A picture with nothing typed alongside it is still a turn
                // worth showing — just with no caption.
                if (image is not null)
                {
                    into.Add(new Row(uuid, new ChatTurn
                    {
                        Role = ChatRole.User,
                        Text = "",
                        IsComplete = true,
                        At = at,
                        ImageBytes = image
                    }));
                }

                return;
            }

            for (var i = 0; i < textBlocks.Count; i++)
            {
                // The placeholder for the picture sits in the last text
                // block found in the real row this was measured against, so
                // that is where the decoded picture rides too.
                var carries = image is not null && i == textBlocks.Count - 1;
                AddText(textBlocks[i], ChatRole.User, uuid, at, into, carries ? image : null);
            }
        }

        // Claude Code writes "[Image #1]", "[Image #2]" and so on directly
        // into the text block beside a pasted picture's own block — a
        // placeholder for wherever its own UI draws the picture inline. This
        // app draws the picture itself right there in the bubble, so once one
        // is actually attached the placeholder is only noise. Leading only:
        // the real row this was measured against had it at the front, and
        // there is nothing to say it is always there.
        private static readonly Regex ImagePlaceholder = new(@"^(\[Image #\d+\]\s*)+", RegexOptions.Compiled);

        // A picture's own bytes, already inline as base64 — unlike
        // OpenClawSessions.DecodeDataUri, which decodes a single "data:…"
        // URI string, a transcript's image block already carries "type" and
        // "data" as separate fields.
        private static byte[]? DecodeImage(JsonElement block)
        {
            if (!block.TryGetProperty("source", out var source)) return null;
            if (Str(source, "type") != "base64") return null;

            var data = Str(source, "data");
            if (string.IsNullOrEmpty(data)) return null;

            try { return Convert.FromBase64String(data); }
            catch { return null; }
        }

        private static void MapAssistant(JsonElement root, string? uuid, DateTimeOffset at, List<Row> into)
        {
            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("content", out var content)) return;
            if (content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        AddText(Str(block, "text"), ChatRole.Assistant, uuid, at, into);
                        break;

                    // Shown, and as its own turn rather than folded into the
                    // reply — the same call OpenClawChatSession makes, for the
                    // same reason: watching a session think is most of the value
                    // of an orb that pulses.
                    case "thinking":
                        AddText(Str(block, "thinking"), ChatRole.System, uuid, at, into);
                        break;

                    case "tool_use":
                        into.Add(new Row(uuid, new ChatTurn
                        {
                            Role = ChatRole.System,
                            Text = ToolSummary(block),
                            IsComplete = true,
                            At = at
                        }));
                        break;
                }
            }
        }

        private static void AddText(
            string? text, ChatRole role, string? uuid, DateTimeOffset at, List<Row> into, byte[]? imageBytes = null)
        {
            var shown = imageBytes is null ? text : ImagePlaceholder.Replace(text ?? "", "");

            // A caption-less picture still deserves a turn, so the emptiness
            // check has to run on the text after the placeholder is
            // stripped — otherwise a picture whose only text was that
            // placeholder would vanish along with it.
            if (imageBytes is null)
            {
                if (string.IsNullOrWhiteSpace(shown)) return;
                if (role == ChatRole.User && IsNoise(shown!)) return;
            }
            else if (role == ChatRole.User && !string.IsNullOrWhiteSpace(shown) && IsNoise(shown!))
            {
                return;
            }

            into.Add(new Row(uuid, new ChatTurn
            {
                Role = role,
                Text = (shown ?? "").Trim(),
                IsComplete = true,
                At = at,
                ImageBytes = imageBytes
            }));
        }

        // Things that arrive as user turns but were never said by the user:
        // injected context, slash-command scaffolding, notifications from
        // background work. All of it is addressed to the model, none of it to a
        // person reading the conversation back.
        private static readonly string[] NoisePrefixes =
        {
            "<system-reminder>",
            "<command-name>",
            "<command-message>",
            "<command-args>",
            "<local-command-stdout>",
            "<local-command-caveat>",
            "<task-notification>",
            "<user-prompt-submit-hook>",
            "[Request interrupted",
            "Caveat: The messages below"
        };

        public static bool IsNoise(string text)
        {
            var t = text.TrimStart();
            foreach (var prefix in NoisePrefixes)
                if (t.StartsWith(prefix, StringComparison.Ordinal)) return true;

            return false;
        }

        // "· Read OrbArrangement.cs" — the tool, and the one argument saying
        // which thing it touched. A bare tool name is nearly useless in a column
        // of nine of them, and the whole input is a paragraph.
        private static readonly string[] ArgKeys =
        {
            "file_path", "notebook_path", "path", "command", "pattern", "url", "query", "description", "prompt"
        };

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        public static string ToolSummary(JsonElement block)
        {
            var name = Str(block, "name");

            return block.TryGetProperty("input", out var input)
                ? ToolSummary(name, input)
                : ToolSummary(name, default);
        }

        // The same line from a name and an arguments object, for producers whose
        // shape is not a Claude Code tool_use block: OpenClaw's `toolcall`
        // history block and its live `session.tool` event both carry
        // `{name, args}` and must draw the identical line (CB-270).
        public static string ToolSummary(string? name, JsonElement input)
        {
            name ??= "tool";

            if (input.ValueKind != JsonValueKind.Object)
                return "· " + name;

            foreach (var key in ArgKeys)
            {
                if (!input.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;

                var arg = value.SafeString();
                if (string.IsNullOrWhiteSpace(arg)) continue;

                if (key is "file_path" or "notebook_path" or "path") arg = Path.GetFileName(arg);

                arg = Whitespace.Replace(arg.Trim(), " ");
                if (arg.Length > 48) arg = arg[..48] + "…";

                return "· " + name + "  " + arg;
            }

            return "· " + name;
        }

        // --- the screen ---

        internal static readonly Regex OptionLine =
            new(@"^(?:[❯>›»→]\s*)?(\d{1,2})[.)]\s+(\S.*)$", RegexOptions.Compiled);

        // How far up the pane to look. The dialog is always at the bottom; this
        // is only enough room for the longest option list plus its trailer.
        internal const int DialogWindow = 30;

        // The most non-option lines crossed between two options: a three-line
        // description, or one separating rule, in the real captures.
        private const int MaxOptionGap = 3;

        // The numbered dialog at the bottom of the pane, or null.
        //
        // Every rule below was written against a real capture rather than
        // guessed, and the guesses were wrong in three ways worth recording,
        // because the shape is nothing like what the documentation of a "dialog"
        // suggests:
        //
        //  1. **There is no box.** A Bash approval is a horizontal rule, a
        //     heading, the command, the question, then the options — no frame at
        //     all. An earlier version stripped box-drawing edges and stopped
        //     dead on the "╰────╯" it expected to find.
        //  2. **Things come after the options.** A footer ("Esc to cancel · Tab
        //     to amend · ctrl+e to explain"), and in a plan prompt an indented
        //     continuation under the last option. So the options cannot be found
        //     by reading up from the last non-blank line; they have to be
        //     searched for.
        //  3. **The dialog replaces the input box.** That is the one reliable
        //     difference between a real dialog and a numbered list the assistant
        //     happened to write in prose — the prose has the input box and its
        //     two horizontal rules below it, and the dialog has nothing below it
        //     but a hint. That is what the rule check at the end is for, and it
        //     is the whole defence against pressing a key to answer a question
        //     nobody asked.
        //  4. **A list that is navigated is not a dialog that is answered**
        //     (CB-307's addendum). `/permissions` draws numbered rules with no
        //     input box below them, which is exactly shape (3), so its Allow
        //     tab read as a nine-option dialog whose buttons would press keys
        //     meaning nothing their labels say. The key is the screen's tab
        //     bar, `Permissions  Recently blocked …`, the one line every tab
        //     draws, populated or empty — measured on all eight captures. It
        //     is matched as a prefix because the bar wraps on a narrow pane.
        //     The footer `←/→ to switch` and the `⌕` search box are second
        //     signals only: the populated Recently-blocked tab has neither (its
        //     footer is "Enter to approve · r to retry · Esc to approve 1
        //     blocked"), and it is the case that matters, since with two
        //     blocked items it would have parsed as a dialog whose keys toggle
        //     approvals. Searched over the whole screen, not the dialog window:
        //     a tall pane puts the bar more than thirty lines up, and a false
        //     match here only ever sends someone to the terminal.
        //
        // Null on anything unexpected, and every caller treats null as "send the
        // person to the terminal".
        public static ChatPrompt? ParseDialog(string screen) => ParseDialogCore(screen, out _, out _);

        // ParseDialog, also handing back the cleaned lines and the index of
        // option 1, so ParseAskForm reads the same option run rather than a
        // second one of its own.
        private static ChatPrompt? ParseDialogCore(string screen, out string[] cleanedLines, out int firstOption)
        {
            cleanedLines = Array.Empty<string>();
            firstOption = -1;

            var lines = ScreenLines(screen);

            // Box drawing is stripped anyway: it costs nothing, and an
            // elicitation dialog may still be framed even though a permission
            // prompt isn't.
            var cleaned = lines.Select(l => l.Trim().Trim('│', '┃', '|', '║').Trim()).ToArray();
            cleanedLines = cleaned;

            if (IsNavigatedList(cleaned)) return null;

            var from = WindowStart(cleaned);

            // The *last* run of numbered lines in the window. Last, because
            // anything the assistant wrote earlier in the conversation is above
            // whatever it is now blocked on.
            //
            // A run may have short gaps (CB-307's addendum, from a real
            // AskUserQuestion dialog on the mini): each option there carries
            // an indented description of up to three lines, and a rule
            // separates "Type something." from "Chat about this". Walking up,
            // a gap of at most MaxOptionGap non-option lines is crossed only
            // when the option above it is numbered exactly one less, so a list
            // that skips a number still stops where it did. Gaps never weaken
            // (3): the input-box check below still runs after the last option.
            var runEnd = -1;
            for (var i = cleaned.Length - 1; i >= from && runEnd < 0; i--)
                if (OptionLine.IsMatch(cleaned[i])) runEnd = i;

            if (runEnd < 0) return null;

            var picked = new List<int> { runEnd };
            var at = runEnd;
            while (true)
            {
                var want = int.Parse(OptionLine.Match(cleaned[at]).Groups[1].Value) - 1;
                if (want < 1) break;

                var above = -1;
                for (var j = at - 1; j >= Math.Max(from, at - 1 - MaxOptionGap); j--)
                {
                    if (!OptionLine.IsMatch(cleaned[j])) continue;
                    if (OptionLine.Match(cleaned[j]).Groups[1].Value == want.ToString()) above = j;
                    break;
                }

                if (above < 0) break;
                picked.Add(above);
                at = above;
            }

            picked.Reverse();
            var runStart = picked[0];

            var options = new List<ChatPromptOption>();
            foreach (var i in picked)
            {
                var match = OptionLine.Match(cleaned[i]);
                options.Add(new ChatPromptOption(match.Groups[1].Value, match.Groups[2].Value.Trim()));
            }

            if (options.Count < 2) return null;

            // Numbered 1..n in order, or this is not the list we think it is.
            // A list that reads 1, 2, 4 is one this did not understand, and
            // pressing "3" on it would answer something nobody asked.
            for (var n = 0; n < options.Count; n++)
                if (options[n].Key != (n + 1).ToString()) return null;

            // The input box, still drawn below — so the session is not blocked
            // on this and these numbers are prose. See (3) above.
            for (var i = runEnd + 1; i < cleaned.Length; i++)
                if (IsHorizontalRule(cleaned[i])) return null;

            var t = runStart - 1;
            while (t >= 0 && IsBlankOrFrame(cleaned[t])) t--;

            // Usually "Do you want to proceed?". A rule or a frame edge is not a
            // question, so those fall through to the generic title.
            var title = t >= 0 ? cleaned[t] : "";
            if (title.Length == 0 || !title.Any(char.IsLetterOrDigit)) title = "Waiting for input";

            firstOption = runStart;
            return new ChatPrompt(title, options);
        }

        // --- AskUserQuestion forms (CB-307 addendum item 3) ---

        // A form Claude Code draws for AskUserQuestion: ParseDialog's options,
        // plus what makes it a form rather than a yes/no dialog. Read off a real
        // capture of the mini's form (PaneFixtures.MiniAskForm), bottom up: the
        // footer, "6. Chat about this" under a rule, "5. Type something.",
        // numbered options with indented descriptions, a │-framed question, and
        // a tab bar "←  ☒ …  ☐ …  ✔ Submit  →" in which ☒ is a question already
        // answered, ☐ one still open and ✔ Submit the last tab. A form with one
        // question draws a "☐ <header>" line instead of the bar.
        //
        // Null for anything that is not a form, including every ordinary dialog
        // — those keep reading through ParseDialog exactly as before.
        internal static AskForm? ParseAskForm(string screen)
        {
            var prompt = ParseDialogCore(screen, out var cleaned, out var first);
            if (prompt is null || first < 0) return null;

            var from = WindowStart(cleaned);
            var raw = ScreenLines(screen).Select(l => l.Trim()).ToArray();

            // The bar or the single-question header: the nearest one above the
            // options, inside the window.
            var head = -1;
            for (var i = first - 1; i >= from && head < 0; i--)
                if (IsTabBar(cleaned[i]) || cleaned[i].StartsWith("☐ ", StringComparison.Ordinal)) head = i;

            // Multi-select options are drawn "[ ] Cheese" / "[✔] Cheese", and a
            // single-select option chosen earlier "Blue ✔". The box and the
            // mark are state, not label: they are stripped, and the keys ticked
            // are kept.
            var checkedKeys = new List<string>();
            var multi = false;
            var options = prompt.Options.Select(o =>
            {
                var label = o.Label;
                if (label.StartsWith("[ ] ", StringComparison.Ordinal)) { multi = true; label = label[4..]; }
                else if (label.StartsWith("[✔] ", StringComparison.Ordinal))
                {
                    multi = true; label = label[4..]; checkedKeys.Add(o.Key);
                }
                else if (label.EndsWith(" ✔", StringComparison.Ordinal))
                {
                    label = label[..^2].TrimEnd(); checkedKeys.Add(o.Key);
                }
                return o with { Label = label };
            }).ToList();

            var freeText = options.FirstOrDefault(o => o.Label.StartsWith(FreeTextLabel, StringComparison.Ordinal))?.Key;
            var chatAbout = options.FirstOrDefault(o => o.Label.StartsWith(ChatAboutLabel, StringComparison.Ordinal))?.Key;

            // The Submit tab's own screen: "Review your answers", the answers as
            // "● question" / "→ answer" pairs, then Submit answers / Cancel.
            var isReview = false;
            var review = new List<AskReviewItem>();
            for (var i = from; i < first; i++)
            {
                if (cleaned[i] == ReviewHeading) isReview = true;
                if (cleaned[i].StartsWith("● ", StringComparison.Ordinal) && i + 1 < first
                    && cleaned[i + 1].StartsWith("→ ", StringComparison.Ordinal))
                    review.Add(new AskReviewItem(cleaned[i][2..].Trim(), cleaned[i + 1][2..].Trim()));
            }

            if (head < 0 && freeText is null && !isReview) return null;

            var tabs = head < 0 ? new List<AskTab>()
                : IsTabBar(cleaned[head]) ? TabsOf(cleaned[head])
                : new List<AskTab> { new(cleaned[head][2..].Trim(), AskTabState.Open) };

            // The question is the │-framed block between the head and option 1,
            // which wraps; ParseDialog's title is only its last line.
            var question = new List<string>();
            for (var i = Math.Max(head + 1, from); i < first; i++)
                if (raw[i].StartsWith('│')) question.Add(cleaned[i]);

            var title = question.Count > 0 ? string.Join(" ", question) : prompt.Title;

            // Which tab is showing cannot be read off the bar: the terminal
            // marks it in reverse video, which capture-pane -p does not carry,
            // and the ☐/☒ marks are no guide — QA's captures show "☒ Toppings"
            // while still on Toppings (one box ticked) and "☐ Toppings" after
            // going back to Color. So only what the body proves is claimed: the
            // review screen is the Submit tab, and a form with one question is
            // on it. Anything else is -1, and the panel says nothing.
            var questions = tabs.Count(t => t.State != AskTabState.Submit);
            var current = isReview ? tabs.FindIndex(t => t.State == AskTabState.Submit)
                : questions == 1 ? tabs.FindIndex(t => t.State != AskTabState.Submit)
                : -1;

            // The unnumbered row a multi-select question draws under its
            // options, and the option the terminal's cursor is on.
            var submitRow = false;
            var lastOption = prompt.Options[^1].Key + ".";
            for (var i = first; i < cleaned.Length; i++)
            {
                if (cleaned[i].TrimStart('❯', ' ').StartsWith(lastOption, StringComparison.Ordinal)) break;
                if (cleaned[i] == SubmitRowLabel) submitRow = true;
            }

            string? cursor = null;
            for (var i = first; i < cleaned.Length && cursor is null; i++)
                if (cleaned[i].StartsWith('❯') && OptionLine.Match(cleaned[i]) is { Success: true } m) cursor = m.Groups[1].Value;

            string? KeyOf(string label) => isReview ? options.FirstOrDefault(o => o.Label == label)?.Key : null;

            return new AskForm(prompt with { Title = title, Options = options }, tabs, current, freeText, chatAbout,
                multi, checkedKeys, submitRow, cursor, isReview, review,
                KeyOf(SubmitAnswersLabel), KeyOf(CancelLabel));
        }

        internal const string ReviewHeading = "Review your answers";
        internal const string SubmitAnswersLabel = "Submit answers";
        internal const string CancelLabel = "Cancel";
        internal const string SubmitRowLabel = "Submit";

        internal const string FreeTextLabel = "Type something";
        internal const string ChatAboutLabel = "Chat about this";

        private static bool IsTabBar(string line) =>
            line.StartsWith('←') && line.EndsWith('→') && line.IndexOfAny(new[] { '☐', '☒', '✔' }) >= 0;

        private static List<AskTab> TabsOf(string bar)
        {
            var inner = bar.Trim('←', '→').Trim();
            var tabs = new List<AskTab>();

            foreach (Match m in TabCell.Matches(inner))
            {
                var state = m.Groups[1].Value switch
                {
                    "☒" => AskTabState.Answered,
                    "✔" => AskTabState.Submit,
                    _ => AskTabState.Open,
                };
                tabs.Add(new AskTab(m.Groups[2].Value.Trim(), state));
            }

            return tabs;
        }

        private static readonly Regex TabCell = new(@"([☐☒✔])\s+(.*?)(?=\s+[☐☒✔]\s|$)", RegexOptions.Compiled);

        // (4) above: whether these trimmed, frame-stripped lines are a
        // navigated list such as `/permissions` rather than a question.
        internal const string PermissionsTabBar = "Permissions  Recently blocked";

        internal static bool IsNavigatedList(IEnumerable<string> cleanedLines) =>
            cleanedLines.Any(l =>
                l.StartsWith(PermissionsTabBar, StringComparison.Ordinal)
                || l.Contains("←/→ to switch", StringComparison.Ordinal)
                || l.StartsWith('⌕'));

        private const string BoxDrawing = "─━═│┃║╭╮╰╯┌┐└┘├┤┬┴┼╔╗╚╝▐▌╌╍┄┅";

        private static bool IsBlankOrFrame(string line) =>
            line.Length == 0 || line.All(c => char.IsWhiteSpace(c) || BoxDrawing.Contains(c));

        // A captured screen as lines, with the blank rows below the last
        // drawn line dropped. Every reader of a pane goes through this.
        //
        // capture-pane -p returns every row of the pane, and a screen that is
        // not full — a fresh or /clear'ed session, a short conversation, a tall
        // pane — has its dialog or input box near the top and empty rows
        // beneath. Every rule here looks at the bottom DialogWindow lines, so
        // those empty rows pushed the whole dialog out of view: QA's real
        // capture of a live form in a 45-row pane (content ending on row 23)
        // parsed as nothing, and the peer panel showed no form. The fixtures
        // had hidden it, because generating them trimmed exactly those rows.
        internal static string[] ScreenLines(string screen)
        {
            var lines = screen.Replace("\r", "").Split('\n');
            var end = lines.Length;
            while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
            return end == lines.Length ? lines : lines[..end];
        }

        // Where the bottom DialogWindow *drawn* lines start: counted up from the
        // bottom over lines that hold something, so blank rows anywhere on the
        // screen do not use the window up.
        //
        // A real form on the mini (regress/mini-d-2f8a228f.txt) is drawn near
        // the top of a 45-row pane, then ~20 blank rows, then the session's
        // titled rule on the very last row. Counting raw rows, the window held
        // only blanks and that rule and the form parsed as nothing — on every
        // commit back to 6310763e, so it was never a regression, only a screen
        // shape no fixture had. Trimming trailing blanks (ScreenLines) cannot
        // help it, because the last row is not blank.
        internal static int WindowStart(IReadOnlyList<string> lines)
        {
            var drawn = 0;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]) && ++drawn == DialogWindow) return i;
            }
            return 0;
        }

        // The full-width line the TUI draws above and below its input box.
        // Length matters: a short run of dashes is punctuation in a sentence.
        internal static bool IsHorizontalRule(string line) =>
            line.Length >= 10 && line.All(c => c is '─' or '━' or '═' || char.IsWhiteSpace(c));

        // --- json helpers ---

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.SafeString()
                : null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.True;

        private static DateTimeOffset Time(JsonElement e) =>
            Str(e, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var at)
                ? at.ToLocalTime()
                : DateTimeOffset.Now;
    }
}

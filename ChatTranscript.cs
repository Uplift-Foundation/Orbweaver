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
        // the test that matters is that these four strings are exactly the row
        // types MapRow handles. CB-356: the system row MapRow handles is only the
        // compaction boundary, so it is matched by its subtype, which holds
        // whatever order the row's keys are written in, rather than by
        // "type":"system", which would pass every turn_duration row too.
        public static bool IsInteresting(string line) =>
            line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"queue-operation\"", StringComparison.Ordinal)
            || line.Contains("\"subtype\":\"compact_boundary\"", StringComparison.Ordinal);

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

                // CB-356: the first half of a compaction. Always a row, even
                // with no metadata; the summary that follows folds into it.
                case "system" when Str(root, "subtype") == "compact_boundary":
                    into.Add(new Row(uuid, CompactionTurn(CompactionNote.FromBoundary(root), at)));
                    return;

                // Sent while the session was busy. Claude Code holds it and
                // picks it up at the end of the turn, and showing that is the
                // difference between "did that send?" and knowing it did.
                case "queue-operation" when Str(root, "operation") == "enqueue":
                {
                    var queued = Str(root, "content");
                    if (string.IsNullOrWhiteSpace(queued) || IsNoise(queued)) return;

                    // CB-330: no real queued row has carried a relay wrapper (0 of
                    // 493 measured), but the same text arriving here would leak
                    // exactly as it did on the plain user path.
                    if (RelayedMessage.Classify(queued) is { } relayed)
                    {
                        AddClassified(relayed, uuid, at, into);
                        return;
                    }

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
            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("content", out var content)) return;

            // CB-356: the summary a compaction resumes from. Keyed on
            // isCompactSummary alone (isVisibleInTranscriptOnly is missing from
            // the partial-summary shape) and handled before the isMeta test, so
            // it never reaches the user-text path. It folds into the boundary
            // when that boundary's turn is still the last one: attachment rows
            // in between produce no turn and so do not break the pair, while
            // anything that did produce a turn makes this an orphan.
            if (Bool(root, "isCompactSummary"))
            {
                var summary = CompactionTurn(CompactionNote.FromSummary(SummaryText(content)), at);
                if (into.Count == 0 || !CompactionNote.TryAbsorb(into[^1].Turn, summary))
                    into.Add(new Row(uuid, summary));
                return;
            }

            var meta = Bool(root, "isMeta");
            if (meta && !IsPeerOrigin(root)) return;

            if (meta)
            {
                // A peer row that is not the shape measured stays dropped, as
                // every isMeta row was before: better a missing bubble than the
                // provenance boilerplate shown as if the user had typed it.
                if (content.ValueKind == JsonValueKind.String
                    && RelayedMessage.Classify(content.SafeString() ?? "", meta: true) is { } peer)
                    AddClassified(peer, uuid, at, into);

                return;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                AddUserText(content.SafeString(), uuid, at, into);
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
                if (carries) AddText(textBlocks[i], ChatRole.User, uuid, at, into, image);
                else AddUserText(textBlocks[i], uuid, at, into);
            }
        }

        // CB-356: Role System and Text = Line, so anything that does not know
        // about Compaction draws a one-line note.
        private static ChatTurn CompactionTurn(CompactionNote note, DateTimeOffset at) => new()
        {
            Role = ChatRole.System,
            Text = note.Line,
            IsComplete = true,
            At = at,
            Compaction = note
        };

        // A string on every real summary (77 of 77); the text blocks of an array
        // joined with blank lines in case one is ever written that way.
        private static string SummaryText(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.SafeString() ?? "";
            if (content.ValueKind != JsonValueKind.Array) return "";

            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
                if (Str(block, "type") == "text" && Str(block, "text") is { } text)
                    parts.Add(text);
            return string.Join("\n\n", parts);
        }

        // CB-330: a user row's text may be a message a teammate or another session
        // relayed rather than anything the user typed. Those become compact system
        // rows; the user's own words, and anything that is not the measured
        // wrapper, go through AddText exactly as before.
        private static void AddUserText(string? text, string? uuid, DateTimeOffset at, List<Row> into)
        {
            if (text is not null && RelayedMessage.Classify(text) is { } relayed)
                AddClassified(relayed, uuid, at, into);
            else
                AddText(text, ChatRole.User, uuid, at, into);
        }

        // One system turn per relay, in tag order, all sharing the row's uuid, the
        // way a row with several text blocks already yields several turns.
        private static void AddClassified(Classified classified, string? uuid, DateTimeOffset at, List<Row> into)
        {
            foreach (var relay in classified.Relays)
            {
                into.Add(new Row(uuid, new ChatTurn
                {
                    Role = ChatRole.System,
                    Text = relay.Line,
                    IsComplete = true,
                    At = at,
                    Relay = relay
                }));
            }

            if (classified.OwnText is not null)
                AddText(classified.OwnText, ChatRole.User, uuid, at, into);
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

        // The most non-option lines crossed between two options (CB-327). It was
        // 3 — "a three-line description or one separating rule" — until a form on
        // a narrow pane wrapped every label and every description across several
        // rows: measured at 40 columns a gap of 10. The gap is now structural
        // (see the walk in ParseDialogCore) and this is only its ceiling, so a
        // run of indented prose cannot be crossed without end.
        private const int MaxOptionGap = 16;

        // The gap develop crossed, of lines of any kind, kept as a fallback.
        private const int LegacyOptionGap = 3;

        // How far above the options a form's tab bar may be looked for. The
        // option walk is no longer held to the DialogWindow, since a narrow
        // pane wraps one form over far more than 30 rows; the head search has
        // to follow it, but not into scrollback, where an old form's bar would
        // turn an ordinary dialog into a form.
        private const int HeadWithin = 40;

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
        //
        // CB-400: a screen that is not a dialog at all but is drawn over the
        // conversation and closed only by Esc (`/mobile`'s QR screen) reads as a
        // one-option prompt with a Body, from OverlayScreen. It is the fallback
        // after ParseDialogCore, never before it, so a real dialog always wins,
        // and ParseDialogCore itself is untouched: everything that reads options
        // (the ask-form parser, the fingerprint's numbered branch) sees what it
        // always did. One line here carries it to PaneScreen, TypingGate, the
        // mirror's PromptFromScreen and the local chat session.
        public static ChatPrompt? ParseDialog(string screen) =>
            ParseDialogCore(screen, out _, out _)
            ?? (OverlayScreen.Read(screen) is { } overlay ? OverlayScreen.ToPrompt(overlay) : null);

        // ParseDialog, also handing back the cleaned lines and the index of
        // option 1, so ParseAskForm reads the same option run rather than a
        // second one of its own.
        internal static ChatPrompt? ParseDialogCore(string screen, out string[] cleanedLines, out int firstOption)
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

            // Grok's dialog is "1 (○) label", not "1. label", so nothing below
            // would find it. It has its own shape, tried first and only when a
            // radio line is on screen at all (CB-315).
            if (GrokDialog.LooksLike(cleaned, from))
            {
                var grok = GrokDialog.Parse(cleaned, from, out firstOption);
                if (grok is not null) return grok;
                firstOption = -1;
            }

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

            // Between option n-1 and option n the walk crosses a line that is
            // blank, a rule, or indented past the number (CB-327). That is the
            // structure of a real form: a description sits under its option at
            // the label column, a rule separates "Type something." from "Chat
            // about this", and on a narrow pane both wrap over as many rows as
            // they need. A line at the margin — the question, the assistant's
            // prose — ends the run, a skipped number still stops it, and the
            // walk is capped at MaxOptionGap lines. It reads above `from`: the
            // window finds the last option, it does not bound the form.
            var picked = new List<int> { runEnd };
            var at = runEnd;
            while (true)
            {
                var want = int.Parse(OptionLine.Match(cleaned[at]).Groups[1].Value) - 1;
                if (want < 1) break;

                // Past the "N." of this option's own line: a description is at
                // the label column, the question and the prose are not.
                var numberColumn = lines[at].IndexOf(OptionLine.Match(cleaned[at]).Groups[1].Value, StringComparison.Ordinal);
                var indentFloor = numberColumn + 2;

                // `crossed` counts the lines walked past; the line after the 16th
                // is still looked at, because it may be the option itself.
                var above = -1;
                var crossed = 0;
                for (var j = at - 1; j >= 0 && crossed <= MaxOptionGap; j--)
                {
                    if (OptionLine.IsMatch(cleaned[j]))
                    {
                        if (OptionLine.Match(cleaned[j]).Groups[1].Value == want.ToString()) above = j;
                        break;
                    }

                    if (cleaned[j].Length > 0 && !IsHorizontalRule(cleaned[j]) && CrossColumn(lines[j]) < indentFloor) break;
                    crossed++;
                }

                // What develop did, kept for what the structural walk refuses: up to
                // LegacyOptionGap lines of anything, the nearest option being the
                // right number. A dialog can carry a note at the margin between its
                // options ("Remaining output is hidden"), which is no description
                // and no rule, and develop read it as a dialog.
                if (above < 0)
                    for (var j = at - 1; j >= Math.Max(from, at - 1 - LegacyOptionGap); j--)
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
            if (IsClippedList(cleaned, runStart)) return null;

            var width = ScreenWidth(lines);
            var options = new List<ChatPromptOption>();
            for (var p = 0; p < picked.Count; p++)
            {
                var i = picked[p];
                var match = OptionLine.Match(cleaned[i]);
                var label = match.Groups[2].Value.Trim();

                // The next option, or the end of the run, bounds how far down
                // this label may have wrapped.
                var limit = p + 1 < picked.Count ? picked[p + 1] : Math.Min(cleaned.Length, runEnd + 1 + MaxWrappedLines);
                var unjoined = label;
                label = JoinWrapped(lines, cleaned, i, label, limit, width);

                options.Add(new ChatPromptOption(match.Groups[1].Value, label, Mode: OptionModes.IsMode(label),
                    ScopeTruncated: OptionModes.IsTruncatedAtEdge(label, label != unjoined, lines[i].TrimEnd().Length, RuleWidth(lines))));
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
                if (IsHorizontalRule(cleaned[i]) && !IsBottomBorder(cleaned, runEnd, i)) return null;

            var t = runStart - 1;
            while (t >= 0 && IsBlankOrFrame(cleaned[t])) t--;

            // Usually "Do you want to proceed?". A rule or a frame edge is not a
            // question, so those fall through to the generic title.
            var title = t >= 0 ? cleaned[t] : "";
            if (title.Length == 0 || !title.Any(char.IsLetterOrDigit)) title = "Waiting for input";

            firstOption = runStart;
            return new ChatPrompt(title, options);
        }

        // The most lines one option's label may run over.
        private const int MaxWrappedLines = 3;

        // A terminal narrower than an option's label wraps it onto the next line,
        // and that line is the rest of the label, not another line of the dialog.
        // Left alone the parser kept the first half ("Yes, and always allow
        // access to /private/tmp/… from this") and lost the rest ("project"),
        // which is what iTerm2 and WezTerm showed at 80 columns (CB-313).
        //
        // A line continues an option's label only when ALL of these hold:
        //  - it directly follows the option's line (or its other continuations);
        //  - it is not itself an option, a rule, or a hint line;
        //  - it is indented at least to the column the label started at, which
        //    is where a terminal's wrap puts it (a footer is at the margin);
        //  - the line above it was FULL: it ends within one word of the screen's
        //    width. That last test is what keeps a description drawn UNDER an
        //    option (an AskUserQuestion's indented text) from being glued into
        //    the label: a description sits under a short label, a wrap under a
        //    label that ran out of room. The width is the longest line on the
        //    screen, which the wrapped line itself is a candidate for.
        // Joined with a single space, as the terminal broke it at one.
        private static string JoinWrapped(string[] lines, string[] cleaned, int optionLine, string label,
                                          int limit, int width)
        {
            var labelColumn = lines[optionLine].IndexOf(label, StringComparison.Ordinal);
            if (labelColumn < 0) return label;

            var joined = label;
            var previous = optionLine;

            for (var k = optionLine + 1; k < limit && k < lines.Length; k++)
            {
                var text = cleaned[k];
                if (text.Length == 0 || OptionLine.IsMatch(text) || IsHorizontalRule(text) || IsHint(text)) break;
                if (ContentColumn(lines[k]) < labelColumn) break;

                var firstWord = text.Split(' ', 2)[0];
                var above = lines[previous].TrimEnd().Length;
                if (above + 1 + firstWord.Length <= width) break;

                // A terminal also breaks a word at a hyphen ("cb313-" / "probe"); that one is
                // rejoined without the space it never had. A label that truly ends in " -"
                // has a space before the hyphen, which is not a break inside a word.
                var brokeAtHyphen = joined.Length >= 2 && joined[^1] == '-' && joined[^2] != ' ';
                joined += (brokeAtHyphen ? "" : " ") + text;
                previous = k;
                if (k - optionLine >= MaxWrappedLines) break;
            }

            return joined;
        }

        // How wide the terminal is, read off the screen. The full-width rules Claude Code
        // and Grok draw (a ─ ╌ or ━ run) are exactly one line wide, so the longest of
        // those is the answer when there are any. Without one, the longest line on screen,
        // which a wrapped label's first line is within one word of. (The longest line is
        // not used first: a capture can hold one wider line than the terminal, as iTerm2's
        // did, and that made a wrap look like it still had room.)
        private static int ScreenWidth(string[] lines) => RuleWidth(lines) ?? lines.Max(l => l.TrimEnd().Length);

        // The width the full-width rules give, or null when the screen has none (then the longest
        // line is only a guess, and a line that long proves nothing about being cut).
        private static int? RuleWidth(string[] lines)
        {
            var rules = lines.Select(l => l.Trim()).Where(l => l.Length >= 40 && l.All(c => "─━═╌╍┄┈-=".Contains(c))).ToList();
            return rules.Count > 0 ? rules.Max(l => l.Length) : null;
        }

        // ContentColumn, but a leading cursor mark is padding too: the terminal
        // draws ❯ at the margin on the row the cursor is on, and a description
        // or the unnumbered Submit row does not leave the form by being selected.
        private static int CrossColumn(string line)
        {
            var at = ContentColumn(line);
            // Only called for a line with something on it, so `at` is on a character.
            if (line[at] != '❯') return at;
            return at + 1 + ContentColumn(line[(at + 1)..]);
        }

        // The column of a line's first character that is not padding or a frame.
        private static int ContentColumn(string line)
        {
            var i = 0;
            while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] is '│' or '┃' or '|' or '║')) i++;
            return i;
        }

        // "Esc to cancel · Tab to amend", "Press enter to confirm or esc to cancel",
        // "Enter to confirm" — the footer of a dialog, never part of an option.
        private static bool IsHint(string text) =>
            text.StartsWith("Esc to ", StringComparison.Ordinal)
            || text.StartsWith("Enter to ", StringComparison.Ordinal)
            || text.StartsWith("Press enter", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tab:", StringComparison.Ordinal);

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
            for (var i = first - 1; i >= Math.Max(0, first - HeadWithin) && head < 0; i--)
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

            // A pane about twelve columns wide wraps even these two fixed rows ("Type
            // somethi ng.", "Chat" / "about this"), and a form whose free-text row was
            // missed here was declined as no form at all (CB-407). So each is also found
            // by its letters with spaces ignored, "Chat about this" by being the last
            // row right under the free-text row and a start of that label; and a row
            // found that way is given its whole label.
            var freeText = options.FirstOrDefault(o => o.Label.StartsWith(FreeTextLabel, StringComparison.Ordinal))?.Key
                ?? options.FirstOrDefault(o => LettersStart(o.Label, FreeTextLabel))?.Key;
            var chatAbout = options.FirstOrDefault(o => o.Label.StartsWith(ChatAboutLabel, StringComparison.Ordinal))?.Key
                // Keys are the digits ParseDialog read off the numbered rows, so they always parse.
                ?? (freeText is not null
                    ? options.FirstOrDefault(o => o.Key == (int.Parse(freeText) + 1).ToString() && o == options[^1] && LettersStart(ChatAboutLabel, o.Label))?.Key
                    : null);
            options = options.Select(o =>
                o.Key == freeText && !o.Label.StartsWith(FreeTextLabel, StringComparison.Ordinal) ? o with { Label = FreeTextRowLabel }
                : o.Key == chatAbout && !o.Label.StartsWith(ChatAboutLabel, StringComparison.Ordinal) ? o with { Label = ChatAboutLabel }
                : o).ToList();

            // The Submit tab's own screen: "Review your answers", the answers as
            // "● question" / "→ answer" pairs, then Submit answers / Cancel.
            var isReview = false;
            var review = new List<AskReviewItem>();
            for (var i = Math.Max(0, first - HeadWithin); i < first; i++)
            {
                if (cleaned[i] == ReviewHeading) isReview = true;
                if (cleaned[i].StartsWith("● ", StringComparison.Ordinal) && i + 1 < first
                    && cleaned[i + 1].StartsWith("→ ", StringComparison.Ordinal))
                    review.Add(new AskReviewItem(cleaned[i][2..].Trim(), cleaned[i + 1][2..].Trim()));
            }

            if (head < 0 && freeText is null && !isReview) return null;

            // A bar too wide for the pane wraps onto a second row that carries the
            // names the first row had no room for (CB-327, measured at 32 columns).
            var nameRow = head >= 0 && IsTabBar(cleaned[head]) && BarNeedsNames(cleaned[head]) && head + 1 < first
                && cleaned[head + 1].Length > 0 && !raw[head + 1].StartsWith('│')
                && cleaned[head + 1].IndexOfAny(TabMarks) < 0
                ? head + 1 : -1;

            var tabs = head < 0 ? new List<AskTab>()
                : IsTabBar(cleaned[head]) ? TabsOf(cleaned[head], nameRow < 0 ? null : cleaned[nameRow])
                : new List<AskTab> { new(cleaned[head][2..].Trim(), AskTabState.Open) };

            // The question is the │-framed block between the head and option 1,
            // which wraps; ParseDialog's title is only its last line. Claude Code
            // 2.1.325 no longer frames it, so with no │ line at all the question
            // is every line there is between the head (and its name row) and
            // option 1, joined as the terminal broke them (CB-327).
            var question = new List<string>();
            var questionFrom = head < 0 ? from : Math.Max(head + 1, nameRow + 1);
            for (var i = questionFrom; i < first; i++)
                if (raw[i].StartsWith('│')) question.Add(cleaned[i]);

            // Not on the review screen, whose lines between the bar and the options
            // are the answers being reviewed: its title stays ParseDialog's. And not
            // without a head: a form known only by its "Type something" row has no
            // top to join from, and everything above would become the question.
            if (question.Count == 0 && !isReview && head >= 0)
                for (var i = questionFrom; i < first; i++)
                    if (cleaned[i].Length > 0 && !IsHorizontalRule(cleaned[i])) question.Add(cleaned[i]);

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
                if (cleaned[i] is SubmitRowLabel or NextRowLabel) submitRow = true;
            }

            string? cursor = null;
            for (var i = first; i < cleaned.Length && cursor is null; i++)
            {
                if (!cleaned[i].StartsWith('❯')) continue;
                var mark = OptionLine.Match(cleaned[i]);
                if (mark.Success) cursor = mark.Groups[1].Value;
            }

            string? KeyOf(string label) => isReview ? options.FirstOrDefault(o => o.Label == label)?.Key : null;

            return new AskForm(prompt with { Title = title, Options = options }, tabs, current, freeText, chatAbout,
                multi, checkedKeys, submitRow, cursor, isReview, review,
                KeyOf(SubmitAnswersLabel), KeyOf(CancelLabel));
        }

        internal const string ReviewHeading = "Review your answers";
        internal const string SubmitAnswersLabel = "Submit answers";
        internal const string CancelLabel = "Cancel";
        internal const string SubmitRowLabel = "Submit";
        // CB-323: the same row on a multi-select question that is not the form's
        // last reads "Next" (live capture PaneFixtures.MacMultiSelectNext). It
        // commits the same way, Right moving on to the next tab with the ticks
        // kept, so HasSubmitRow means "has its commit row" whichever word it wears.
        internal const string NextRowLabel = "Next";

        internal const string FreeTextLabel = "Type something";
        internal const string FreeTextRowLabel = "Type something.";

        // Whether `text` begins with `wanted`, comparing letters only: every space gone,
        // because a narrow pane breaks the rows inside words.
        internal static bool LettersStart(string text, string wanted) =>
            Regex.Replace(text, @"\s+", "").StartsWith(Regex.Replace(wanted, @"\s+", ""), StringComparison.Ordinal)
            && Regex.Replace(text, @"\s+", "").Length >= 3;
        internal const string ChatAboutLabel = "Chat about this";

        private static bool IsTabBar(string line) =>
            line.StartsWith('←') && line.EndsWith('→') && line.IndexOfAny(new[] { '☐', '☒', '✔' }) >= 0;

        private static readonly char[] TabMarks = { '☐', '☒', '✔' };

        // A mark with nothing after it before the next mark (or the end) has had
        // its name moved to the bar's second row. A bar whose every mark carries
        // its name has no second row, and the line under it is the question.
        private static bool BarNeedsNames(string bar) =>
            Regex.IsMatch(bar.Trim('←', '→'), @"[☐☒✔]\s*(?=[☐☒✔]|$)");

        // The tabs of a bar, counted by their marks rather than parsed as
        // "mark, space, name": a bar that ran out of room draws marks with no
        // name beside them (`☐      ☐   ☐ S… ☐ T… ✔`). A mark's name is the text
        // after it up to the next mark. Where that is empty, the names come, in
        // order, from the bar's second row (`Venue  C…   Submit`), whose cells
        // are separated by two or more spaces; a cell it has no name for is left
        // empty, and the Submit tab is only ever called Submit.
        private static List<AskTab> TabsOf(string bar, string? nameRow = null)
        {
            var inner = bar.Trim('←', '→').Trim();
            var names = nameRow is null ? new Queue<string>()
                : new Queue<string>(Regex.Split(nameRow.Trim(), @"\s{2,}").Where(n => n.Length > 0));
            var tabs = new List<AskTab>();

            var marks = new List<int>();
            for (var i = 0; i < inner.Length; i++)
                if (Array.IndexOf(TabMarks, inner[i]) >= 0) marks.Add(i);

            for (var m = 0; m < marks.Count; m++)
            {
                var end = m + 1 < marks.Count ? marks[m + 1] : inner.Length;
                var name = inner[(marks[m] + 1)..end].Trim();
                var state = inner[marks[m]] switch
                {
                    '☒' => AskTabState.Answered,
                    '✔' => AskTabState.Submit,
                    _ => AskTabState.Open,
                };
                if (name.Length == 0 && names.Count > 0) name = names.Dequeue();
                if (name.Length == 0 && state == AskTabState.Submit) name = SubmitRowLabel;
                tabs.Add(new AskTab(name, state));
            }

            return tabs;
        }

        // (4) above: whether these trimmed, frame-stripped lines are a
        // navigated list such as `/permissions` rather than a question.
        internal const string PermissionsTabBar = "Permissions  Recently blocked";

        // A list that does not fit the pane (CB-327): when it is taller than the
        // screen the terminal marks the cut with a line that is only an arrow, or
        // ends in spaces and one (`↓`, `       ↓`), as the /model picker does at
        // 23 and 32 columns. Its footer and its last options are not on the screen,
        // so whatever is read of it is a part of a list, and a button pressed on a
        // part of a list can mean something its label does not say. Not minted.
        //
        // Only the two places a list's own scroll marks are drawn count: the last
        // drawn line of the screen (measured at model-w23 and model-w32, where the
        // mark is the final line) and the nearest line above option 1. An arrow in
        // scrollback, higher up, is the assistant's prose and says nothing about
        // the dialog beneath it.
        internal static bool IsClippedList(string[] cleaned, int firstOption)
        {
            var last = cleaned.Length - 1;
            while (last >= 0 && cleaned[last].Length == 0) last--;
            // An option line is on screen, so there is a last drawn line.
            if (IsClipped(cleaned[last])) return true;

            var above = firstOption - 1;
            while (above >= 0 && cleaned[above].Length == 0) above--;
            return above >= 0 && IsClipped(cleaned[above]);
        }

        internal static bool IsClipped(string cleanedLine) =>
            cleanedLine.Length > 0 && cleanedLine[^1] is '↓' or '↑'
            && (cleanedLine.Length == 1 || char.IsWhiteSpace(cleanedLine[^2]));

        internal static bool IsNavigatedList(IEnumerable<string> cleanedLines) =>
            cleanedLines.Any(l =>
                l.StartsWith(PermissionsTabBar, StringComparison.Ordinal)
                || l.Contains("←/→ to switch", StringComparison.Ordinal)
                || l.StartsWith('⌕'));

        private const string BoxDrawing = "─━═│┃║╭╮╰╯┌┐└┘├┤┬┴┼╔╗╚╝▐▌╌╍┄┅";

        private static bool IsBlankOrFrame(string line) =>
            line.Length == 0 || line.All(c => char.IsWhiteSpace(c) || BoxDrawing.Contains(c));

        // The session's own bottom border under a form, not an input box. A
        // session started with a name (`claude -n <name>`) draws that name into
        // a rule on the last row, under the form's footer — QA's real captures
        // of named sessions (item3/named/). The name carries letters, so the
        // rule is not "pure" and never tripped the input-box check; but on a
        // narrow pane tmux cuts the line from the right, and cut before the
        // name it is a pure rule, which read as a live input box and refused
        // the form. So a pure rule that is the last drawn line, after the
        // form's own footer hint, is the border. An input box is followed by
        // its ❯ line and its status line, never by nothing, and never comes
        // after "Enter to select"/"Esc to cancel".
        private static bool IsBottomBorder(string[] cleaned, int runEnd, int rule)
        {
            // Last drawn line: ScreenLines has already dropped the blank rows
            // below it, so "last" is simply the end of the array.
            if (rule != cleaned.Length - 1) return false;

            for (var j = runEnd + 1; j < rule; j++)
                if (cleaned[j].Contains("Enter to select", StringComparison.Ordinal)
                    || cleaned[j].Contains("Enter to confirm", StringComparison.Ordinal)
                    || cleaned[j].Contains("Esc to cancel", StringComparison.Ordinal))
                    return true;

            return false;
        }

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
            // CB-353: a docked side panel is cut off first, because the rows
            // under a form are blank only once the panel's text is gone.
            var lines = PaneColumns.StripSidePanel(screen.Replace("\r", "").Split('\n'));
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

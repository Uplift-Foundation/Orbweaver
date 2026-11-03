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

        private static readonly Regex OptionLine =
            new(@"^(?:[❯>›»→]\s*)?(\d{1,2})[.)]\s+(\S.*)$", RegexOptions.Compiled);

        // How far up the pane to look. The dialog is always at the bottom; this
        // is only enough room for the longest option list plus its trailer.
        private const int DialogWindow = 30;

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
        //
        // Null on anything unexpected, and every caller treats null as "send the
        // person to the terminal".
        public static ChatPrompt? ParseDialog(string screen)
        {
            var lines = screen.Replace("\r", "").Split('\n');

            // Box drawing is stripped anyway: it costs nothing, and an
            // elicitation dialog may still be framed even though a permission
            // prompt isn't.
            var cleaned = lines.Select(l => l.Trim().Trim('│', '┃', '|', '║').Trim()).ToArray();

            var from = Math.Max(0, cleaned.Length - DialogWindow);

            // The *last* contiguous run of numbered lines in the window. Last,
            // because anything the assistant wrote earlier in the conversation
            // is above whatever it is now blocked on.
            var runEnd = -1;
            var runStart = -1;

            for (var i = cleaned.Length - 1; i >= from; i--)
            {
                if (!OptionLine.IsMatch(cleaned[i])) continue;

                runEnd = i;
                runStart = i;
                while (runStart - 1 >= from && OptionLine.IsMatch(cleaned[runStart - 1])) runStart--;
                break;
            }

            if (runStart < 0) return null;

            var options = new List<ChatPromptOption>();
            for (var i = runStart; i <= runEnd; i++)
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

            return new ChatPrompt(title, options);
        }

        private const string BoxDrawing = "─━═│┃║╭╮╰╯┌┐└┘├┤┬┴┼╔╗╚╝▐▌╌╍┄┅";

        private static bool IsBlankOrFrame(string line) =>
            line.Length == 0 || line.All(c => char.IsWhiteSpace(c) || BoxDrawing.Contains(c));

        // The full-width line the TUI draws above and below its input box.
        // Length matters: a short run of dashes is punctuation in a sentence.
        private static bool IsHorizontalRule(string line) =>
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

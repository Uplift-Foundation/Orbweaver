namespace Orbweaver
{
    // Every sentence the approval prompt and its outcomes use (CB-270 Stage 6), in
    // one pure place so the wording is a one-line change and a test can pin it.
    //
    // Everything the gateway supplies is untrusted text that ends up in front of
    // someone deciding whether to let a command run, so it is bounded and stripped
    // of control characters here, and drawn as plain text by the panel, never as
    // markup. The record's own parser bounds it first; this does not trust that.
    internal static class OpenClawApprovalText
    {
        // The two answers, fixed by the client. Never taken from a payload: a
        // gateway that could name its own options could offer one this app never
        // meant to send. Deny first, so it is the first stop for the keyboard and
        // the one a stray Enter lands on.
        internal const string DenyKey = "deny";
        internal const string AllowOnceKey = "allow-once";

        // Each option is bound to the approval it is drawn for, so a click that
        // lands after that request has gone, and another is showing, answers
        // nothing. Deny only for a request that could not be shown safely: a person
        // cannot be asked to allow what they could not read.
        internal static IReadOnlyList<ChatPromptOption> Options(string approvalId, bool denyOnly = false) =>
            denyOnly
                ? new[] { new ChatPromptOption(DenyKey, "Deny", approvalId) }
                : new[]
                {
                    new ChatPromptOption(DenyKey, "Deny", approvalId),
                    new ChatPromptOption(AllowOnceKey, "Allow once", approvalId),
                };

        internal const string TooLongToAllow =
            "This command is too long to read here, so it can only be denied here; answer it on the gateway.";

        internal const string ElsewhereLabel = "How to answer on the gateway";

        internal const string RequestChanged = "That request changed before you clicked; look again";

        internal const string UnsafeRequest =
            "A request couldn't be shown safely; answer it on the gateway";

        // The command is what a person is deciding on, so it is shown whole up to here:
        // heredocs and long pipelines run past a few hundred characters, and a
        // limit that low would send most real approvals to the gateway. Past this
        // the end of the command could not be read in the prompt, so it can only
        // be denied here.
        internal const int MaxCommand = 4000;
        internal const int MaxLocation = 240;
        internal const int MaxName = 240;
        internal const int MaxReason = 60;

        internal const string ApprovingOff =
            "Approving is off. Turn on \"Allow approving agent actions\" in Settings — "
            + "it asks the gateway for permission to approve, which you approve there.";

        internal const string NoLongerPending = "This request is no longer pending";

        // The title and the line under it: what is being asked, then where. Plain
        // text; the panel draws both in a TextBlock.
        //
        // Nothing is repaired. A displayed field with a control, format or line- or
        // paragraph-separator character in it is not shown at all (flattening it
        // would show a person something other than what the agent asked for, and a
        // command is the one thing that must not be reworded for display), and the
        // request becomes the unsafe prompt: what it is, where to answer it, and
        // Deny. The caller's flag says the parser already judged it so; this also
        // judges, since neither should be trusted to be the only check.
        internal static ChatPrompt Prompt(
            string approvalId, string agent, string command, string? cwd, string? host,
            bool unsafeToShow = false, bool commandTruncated = false, int? commandLength = null)
        {
            if (unsafeToShow || HasUnsafe(agent) || HasUnsafe(command) || HasUnsafe(cwd) || HasUnsafe(host))
            {
                return new ChatPrompt(
                    UnsafeRequest + "\nRequest id: " + QuoteId(approvalId),
                    Options(approvalId, denyOnly: true), ArmedActivation: true, ElsewhereLabel: ElsewhereLabel);
            }

            var who = Cap(agent, MaxName);

            // A command too long to show whole, by our bound or the parser's, is
            // shown up to the bound with what is missing counted, and can only be
            // denied: Allow once is never offered for a command whose end was not
            // shown.
            var tooLong = commandTruncated || command.Length > MaxCommand;
            var what = tooLong ? CapCommand(command, commandLength) : command;
            var title = (who.Length == 0 ? "An agent" : who) + " wants to run: " + (what.Length == 0 ? "(no command shown)" : what);

            var where = new List<string>();
            if (Cap(cwd, MaxLocation) is { Length: > 0 } dir) where.Add("in " + dir);
            if (Cap(host, MaxName) is { Length: > 0 } machine) where.Add("on " + machine);
            if (where.Count > 0) title += "\n" + string.Join(" ", where);

            if (tooLong) title += "\n" + TooLongToAllow;

            return new ChatPrompt(title, Options(approvalId, denyOnly: tooLong), ArmedActivation: true, ElsewhereLabel: ElsewhereLabel);
        }

        // What our own answer reads as once the gateway has applied it.
        internal static string Answered(string decision) =>
            decision == AllowOnceKey ? "Allowed once" : "Denied";

        // Another reviewer got there first; the snapshot says what they decided.
        internal static string AlreadyDecided(string? status) =>
            "Already decided elsewhere: " + (Safe(status, MaxReason) is { Length: > 0 } s ? s : "unknown");

        internal static string CouldntAnswer(string message) =>
            "Couldn't answer: " + (Safe(message, MaxCommand) is { Length: > 0 } m ? m : "no reason given");

        // A request we did not answer ended: from a resolved event, a poll that no
        // longer lists it, or the local clock. Terminal reasons are the gateway's
        // seven; each reads as what it means, and an unknown one by name.
        internal static string Ended(string? status, string? reason)
        {
            switch (reason)
            {
                case "timeout":
                    return "Expired unanswered";
                case "run-aborted":
                    return "The run was stopped";
                case "gateway-restart":
                case "no-route":
                case "storage-corrupt":
                case "malformed-verdict":
                    return "Dropped by the gateway (" + reason + ")";
            }

            return status switch
            {
                "allowed" => "Allowed by another reviewer",
                "denied" => "Denied by another reviewer",
                _ => "Dropped by the gateway" + (Safe(reason, MaxReason) is { Length: > 0 } r ? " (" + r + ")" : "")
            };
        }

        // How to answer it on the gateway instead, since there is no terminal to
        // focus. The id is shown as data, quoted and escaped, never composed into a
        // line to paste: ids are the gateway's, may be up to 512 characters, and may
        // carry printable punctuation a shell reads as syntax. allow-always is named
        // here and nowhere else: it mints a standing grant this app has no surface to
        // list or revoke.
        internal static string Elsewhere(string id) =>
            "To answer this yourself, use the openclaw CLI on the gateway host: approvals resolve, "
            + "with --deny or --allow-once, for this request id: " + QuoteId(id)
            + ". (A standing grant is --allow-always --expires-in-days N, and is managed there.)";

        // Single-quoted, with an embedded quote escaped the way a shell would need
        // it, and anything unprintable written out as \u{...}, so what is shown is
        // exactly the id and nothing in it can pass for text of ours.
        internal static string QuoteId(string? id)
        {
            var sb = new System.Text.StringBuilder("'");

            foreach (var rune in (id ?? "").EnumerateRunes())
            {
                if (rune.Value == '\'') sb.Append("'\\''");
                else if (IsUnsafe(rune)) sb.Append("\\u{").Append(rune.Value.ToString("X")).Append('}');
                else sb.Append(rune.ToString());
            }

            return sb.Append('\'').ToString();
        }

        // One line in a room's transcript: an approval belongs to one member's
        // session and is answered in that member's own panel.
        internal static string RoomLine(string member) =>
            Safe(member, MaxName) + " is waiting for an approval";

        // The command cut at the bound, with the count of what is missing. The
        // length the parser saw is used when it is known, since the text handed over
        // may already have been cut.
        private static string CapCommand(string command, int? fullLength)
        {
            var shown = command.Length <= MaxCommand ? command : Cap(command, MaxCommand);
            if (command.Length > MaxCommand) return shown;

            var missing = (fullLength ?? command.Length) - command.Length;
            return missing > 0 ? command + $"… ({missing} more characters)" : command + "…";
        }

        // Capped, and what is cut is said as a count, so a long command cannot look
        // shorter than it is. Never altered otherwise.
        internal static string Cap(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= max) return text;

            // Not through the middle of a surrogate pair.
            var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
            return text[..cut] + $"… ({text.Length - cut} more characters)";
        }

        // Text the gateway supplied for a sentence of ours: shown if it is clean,
        // and absent (so the sentence says there is none) if it is not.
        private static string Safe(string? text, int max) => HasUnsafe(text) ? "" : Cap(text, max);

        // A control (Cc), format (Cf), line-separator (Zl) or paragraph-separator
        // (Zp) character anywhere in it. Such text is not shown.
        internal static bool HasUnsafe(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            foreach (var rune in text.EnumerateRunes())
            {
                if (IsUnsafe(rune)) return true;
            }

            return false;
        }

        private static bool IsUnsafe(System.Text.Rune rune) =>
            System.Text.Rune.GetUnicodeCategory(rune) is
                System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator;
    }
}

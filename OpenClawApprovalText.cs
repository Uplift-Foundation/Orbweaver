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

        internal const string UnsafeRequest =
            "A request couldn't be shown safely; answer it on the gateway";

        internal const int MaxCommand = 400;
        internal const int MaxLocation = 160;
        internal const int MaxName = 60;
        internal const int MaxReason = 60;

        internal const string ApprovingOff =
            "Approving is off. Turn on \"Allow approving agent actions\" in Settings — "
            + "it asks the gateway for permission to approve, which you approve there.";

        internal const string NoLongerPending = "This request is no longer pending";

        // The title and the line under it: what is being asked, then where. Plain
        // text; the panel draws both in a TextBlock.
        internal static ChatPrompt Prompt(
            string approvalId, string agent, string command, string? cwd, string? host,
            bool unsafeToShow = false)
        {
            // Nothing of the request is drawn when the parser could not make it safe
            // to: only that there is one, and a way to refuse it.
            if (unsafeToShow) return new ChatPrompt(UnsafeRequest, Options(approvalId, denyOnly: true), Deliberate: true);

            var who = Clean(agent, MaxName);
            var what = Clean(command, MaxCommand);
            var title = (who.Length == 0 ? "An agent" : who) + " wants to run: " + (what.Length == 0 ? "(no command shown)" : what);

            var where = new List<string>();
            if (Clean(cwd, MaxLocation) is { Length: > 0 } dir) where.Add("in " + dir);
            if (Clean(host, MaxName) is { Length: > 0 } machine) where.Add("on " + machine);
            if (where.Count > 0) title += "\n" + string.Join(" ", where);

            return new ChatPrompt(title, Options(approvalId), Deliberate: true);
        }

        // What our own answer reads as once the gateway has applied it.
        internal static string Answered(string decision) =>
            decision == AllowOnceKey ? "Allowed once" : "Denied";

        // Another reviewer got there first; the snapshot says what they decided.
        internal static string AlreadyDecided(string? status) =>
            "Already decided elsewhere: " + (Clean(status, MaxReason) is { Length: > 0 } s ? s : "unknown");

        internal static string CouldntAnswer(string message) =>
            "Couldn't answer: " + (Clean(message, MaxCommand) is { Length: > 0 } m ? m : "no reason given");

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
                _ => "Dropped by the gateway" + (Clean(reason, MaxReason) is { Length: > 0 } r ? " (" + r + ")" : "")
            };
        }

        // The command a person can run on the gateway instead, since there is no
        // terminal to focus. allow-always is named here and nowhere else: it mints
        // a standing grant this app has no surface to list or revoke.
        internal static string Elsewhere(string id)
        {
            var safe = Clean(id, MaxName);
            return $"To answer this yourself, on the gateway host run: openclaw approvals resolve {safe} --deny "
                + $"or --allow-once. (A standing grant is --allow-always --expires-in-days N, and is managed there.)";
        }

        // One line in a room's transcript: an approval belongs to one member's
        // session and is answered in that member's own panel.
        internal static string RoomLine(string member) =>
            Clean(member, MaxName) + " is waiting for an approval";

        // Single-spaced, no control characters, and capped: what is cut is said, as
        // a count, so a long command cannot look shorter than it is. Newlines and
        // tabs become spaces: a command that sprawls over lines is shown on one so
        // it cannot push the buttons off the bar, and cannot fake a second line.
        internal static string Clean(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new System.Text.StringBuilder(Math.Min(text.Length, max + 1));
            var lastSpace = false;
            var total = 0;

            foreach (var c in text)
            {
                var ch = char.IsControl(c) ? ' ' : c;
                if (ch == ' ')
                {
                    if (lastSpace || total == 0) continue;
                    lastSpace = true;
                }
                else
                {
                    lastSpace = false;
                }

                total++;
                if (sb.Length <= max) sb.Append(ch);
            }

            // A trailing space is not content.
            if (lastSpace) total--;

            if (total <= max) return sb.ToString().TrimEnd();

            return sb.ToString(0, max).TrimEnd() + $"… ({total - max} more characters)";
        }
    }
}

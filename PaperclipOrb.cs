namespace Orbweaver
{
    // What a Paperclip orb draws and offers, decided with no window behind it
    // (CB-421, plan 2.7). OrbWindow only paints these answers, for the reason
    // OrbGlyph and ClickRouting are pure: a rule that lives next to the controls
    // it drives can only be checked by looking at the screen.
    internal static class PaperclipOrb
    {
        // The drawn mark in the presence corner for a Paperclip agent that broke,
        // is paused, or is over budget. Over budget is the same pause mark in the
        // warning colour: the plan wants it to read as "paused, and here is why",
        // and the why is the hover's MarkReason. Null for None.
        internal readonly record struct MarkLook(string Path, string Name, string FillHex);

        internal const string WhiteHex = "#F2FFFFFF";

        internal static MarkLook? MarkFor(PaperclipMark mark) => mark switch
        {
            PaperclipMark.Broke => new(SymbolMarks.Alert, "broke", AccountOrbWindow.DangerHex),
            PaperclipMark.Paused => new(SymbolMarks.Pause, "paused", WhiteHex),
            PaperclipMark.OverBudget => new(SymbolMarks.Pause, "over budget", AccountOrbWindow.WarnHex),
            _ => null
        };

        // The "+N" on a lead that has sleeping reports folded into it. Null when
        // nothing is folded, and for anything that is not a Paperclip orb (the
        // members are only ever set by the Paperclip merge, but a stale value on
        // a reused status object must not draw a pill on another kind of orb).
        internal static string? FoldedLabel(SessionStatus status) =>
            status.Source == SessionSource.Paperclip && status.FoldedCount > 0
                ? "+" + status.FoldedCount
                : null;

        // The tooltip's body under the agent's name: the hover lines Paperclip's
        // own data produced (need titles, the mark's reason, task, activity,
        // running time, last result, budget; PaperclipApi owns their order and
        // which are omitted), then the folded agents last, since they are about
        // the company rather than this agent. Null when there is nothing to say,
        // which draws the name alone: a line saying "unknown" would be on screen
        // far more often than a real detail.
        internal static string? TipBody(IReadOnlyList<string>? lines, int foldedCount, string? foldedNames)
        {
            var parts = new List<string>();
            if (lines is not null)
                foreach (var line in lines)
                    if (!string.IsNullOrWhiteSpace(line)) parts.Add(line.Trim());
            if (foldedCount > 0)
            {
                parts.Add(string.IsNullOrWhiteSpace(foldedNames)
                    ? $"+{foldedCount} asleep"
                    : $"+{foldedCount} asleep: {foldedNames.Trim()}");
            }
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        // Which of the three Paperclip rows a menu offers. Open goes wherever the
        // orb's Url points (the page that decides what is waiting when something
        // is, else the agent's page), so it needs an address a browser may be
        // handed; Review is the amber orb's own name for the same destination;
        // Reconnect is offered only while the key is gone. Wake, Pause and Resume
        // are release 2 and are not rows yet.
        internal readonly record struct MenuOffer(bool Open, bool Review, bool Reconnect);

        internal static MenuOffer Offer(SessionStatus? status, PaperclipLink link)
        {
            if (status?.Source != SessionSource.Paperclip) return default;
            var openable = CloudSessionLink.IsOpenable(status.Url);
            return new MenuOffer(
                Open: openable,
                Review: openable && status.Presence == OrbPresence.NeedsInput,
                Reconnect: link == PaperclipLink.NeedsReconnect);
        }
    }
}

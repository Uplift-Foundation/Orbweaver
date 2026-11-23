using System.Linq;

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

        // Whether a Paperclip orb is drawn dimmed. Only an agent that is asleep or
        // manually paused is: an orb that asks for you (amber), has failed, or is
        // stopped at its budget cap must never be among the faintest on the
        // screen (product principle 3, "amber means you can act"). That is
        // narrower than the rule for a held background job, which dims on every
        // presence but Present, and is why this is asked per row rather than of
        // the presence alone. Null for a row that is not a Paperclip agent, which
        // keeps the existing rule exactly as it was.
        internal static bool? Dims(SessionStatus status)
        {
            if (status.Source != SessionSource.Paperclip) return null;
            if (status.Presence == OrbPresence.NeedsInput) return false;
            if (status.Mark is PaperclipMark.Broke or PaperclipMark.OverBudget) return false;
            if (status.Mark == PaperclipMark.Paused) return true;
            return status.Presence != OrbPresence.Present;
        }

        // The budget a ring draws. A Paperclip row draws its budget and never a
        // context percent; every other row draws its context percent and never a
        // budget, so a stale BudgetPercent on a reused status object cannot put a
        // ring on a session that has no budget. Null draws no ring.
        internal static int? RingPercent(SessionStatus status) =>
            status.Source == SessionSource.Paperclip ? status.BudgetPercent : status.ContextPercent;

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

        // Which Paperclip rows a menu offers. Open goes wherever the orb's Url
        // points (the page that decides what is waiting when something is, else
        // the agent's page), so it needs an address a browser may be handed;
        // Review is the amber orb's own name for the same destination; Reconnect
        // is offered only while the key is gone. Wake, Pause and Resume (CB-427)
        // are offered only where they apply, and only while the link is live: a
        // parked company is a picture, and an answer to it would go nowhere.
        //   Wake    an agent that is neither paused, over budget, running nor awaiting its own approval
        //   Pause   an agent that is not paused or over budget
        //   Resume  an agent paused by hand (never one paused for budget)
        internal readonly record struct MenuOffer(
            bool Open, bool Review, bool Reconnect,
            bool Wake = false, bool Pause = false, bool Resume = false);

        internal static MenuOffer Offer(SessionStatus? status, PaperclipLink link)
        {
            if (status?.Source != SessionSource.Paperclip) return default;
            var openable = CloudSessionLink.IsOpenable(status.Url);
            var live = link == PaperclipLink.Connected && status.PaperclipAgentId.Length > 0
                && status.PaperclipAgentStatus.Length > 0 && status.PaperclipAgentStatus != "terminated";
            // Plan 13.2. A budget pause is Paperclip's to lift (and that resume is
            // unmeasured), so an over-budget agent is offered neither Pause (it is
            // already paused) nor Resume nor Wake.
            var paused = status.Mark == PaperclipMark.Paused || status.PaperclipAgentStatus == "paused";
            var overBudget = status.Mark == PaperclipMark.OverBudget;
            var running = status.State == "generating" || status.PaperclipAgentStatus == "running";
            return new MenuOffer(
                Open: openable,
                Review: openable && status.Presence == OrbPresence.NeedsInput,
                Reconnect: link == PaperclipLink.NeedsReconnect,
                Wake: live && !paused && !overBudget && !running && status.PaperclipAgentStatus != "pending_approval",
                Pause: live && !paused && !overBudget,
                Resume: live && paused && !overBudget);
        }

        // Whether a click on this orb opens the answer panel rather than the
        // browser: an amber Paperclip orb holding at least one need this app can
        // answer. An amber orb holding only a blocked task keeps its old click
        // (the link), and so does any orb while the link is not live.
        internal static bool ShowsNeeds(SessionStatus? status, PaperclipLink link) =>
            status?.Source == SessionSource.Paperclip
            && link == PaperclipLink.Connected
            && status.Presence == OrbPresence.NeedsInput
            && status.Needs.Any(PaperclipActions.Answerable);
    }
}

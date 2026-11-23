using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // Binding types for the Paperclip arm (CB-421). Names and shapes are fixed
    // by docs/plans/paperclip-orbs-plan.md section 2.1 so the packages that
    // build on them (relay, orb drawing, wiring) compile in parallel; the
    // bodies are package A's.
    internal enum PaperclipMark { None, Broke, Paused, OverBudget }

    internal enum PaperclipLink { Off, Connecting, Connected, NeedsReconnect, Unreachable }

    // One thing that needs a person. Kind is "approval", "action" or "blocked".
    internal sealed record PaperclipNeed(
        string Kind,
        string Id,
        string Title,
        string Url);

    internal sealed record PaperclipAgent(
        string Id, string Name, string Role, string Title,
        string Status, string? ReportsTo, string AdapterType,
        string State,                       // "idle" | "generating" | "waiting"
        PaperclipMark Mark, string? MarkReason,
        IReadOnlyList<PaperclipNeed> Needs, // empty unless waiting
        string? Task, string? Activity,
        DateTime? RunStarted, string? LastResult,
        int? BudgetPercent,                 // null = draw no ring
        DateTime LastActivity, string Url,
        bool Sleeping,
        int? RunPid = null, string? RunSessionId = null);

    internal static class PaperclipSessions
    {
        private static volatile IReadOnlyList<PaperclipAgent> _snapshot = Array.Empty<PaperclipAgent>();
        private static volatile PaperclipLink _link = PaperclipLink.Off;
        private static volatile string _statusText = "Paperclip is switched off.";

        // Every agent, unfolded; empty unless enabled.
        public static IReadOnlyList<PaperclipAgent> Snapshot() => _snapshot;

        internal static PaperclipLink Link => _link;

        internal static string StatusText => _statusText;

        // The seam every downstream test uses, as ClaudeCloudSessions has.
        internal static void SetSnapshotForTests(IReadOnlyList<PaperclipAgent> agents) => _snapshot = agents;

        internal static void SetLinkForTests(PaperclipLink link, string statusText)
        {
            _link = link;
            _statusText = statusText;
        }

        public static void Restart() { }
    }
}

using System.Collections.Generic;

namespace Orbweaver
{
    // CB-330: where a relayed message came from.
    public enum RelayKind { Teammate, Peer }

    // CB-330: a message another Claude session or a team member sent into this
    // session, drawn as one compact, collapsible row instead of a user bubble.
    // Pure: no window, no settings, no I/O, so the decisions are testable
    // without either. docs/cb-330-plan.md section 2 is the contract.
    public sealed record RelayedMessage(
        RelayKind Kind,
        string? Name,        // teammate_id, or a cross-session from-name. Null for teammate_id "system".
        string? Color,       // the payload colour name as written ("green"); null when absent
        string PayloadType,  // "idle_notification", "shutdown_approved", "teammate_terminated",
                             // "shutdown_request", "message" for plain text, or an unknown type verbatim
        string Verb,         // "finished", "failed", ...; empty for a Team or Peer row
        string Preview,      // one line, whitespace collapsed, at most PreviewMax chars, "…" when cut; may be ""
        string FullText)     // Markdown for the expanded view; never the provenance boilerplate
    {
        public const int PreviewMax = 140;

        // "Teammate @hana-moriyama finished", "Team", "From another session".
        // The panel draws the pieces itself (bold name); this is the plain form.
        public string Label =>
            Kind == RelayKind.Peer ? "From another session"
            : Name is null ? "Team"
            : Verb.Length == 0 ? $"Teammate @{Name}"
            : $"Teammate @{Name} {Verb}";

        // Label, then ": " + Preview when there is one. What ChatTurn.Text holds.
        public string Line => Preview.Length == 0 ? Label : $"{Label}: {Preview}";

        // What one transcript string is. Null: ordinary text, show it as before.
        public static Classified? Classify(string content) => null;
    }

    // Relays in order (one per tag), or OwnText when the wrapper held the
    // user's own words.
    public sealed record Classified(IReadOnlyList<RelayedMessage> Relays, string? OwnText);
}

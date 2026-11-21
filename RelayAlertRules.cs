using System.Text.Json;

namespace Orbweaver;

// CB-358: which relay alerts a paired phone gets for agent-team members. The
// phone stores the choice and sends it as a `prefs` frame under the pair key;
// the desktop keeps it per phone and decides, when a session finishes, whether
// that delta carries the done push. Pure, so the decision has a test per arm
// and no window, relay or clock behind it.
internal enum DoneAlerts { All, Leads, Off }

internal readonly record struct AlertPrefs(DoneAlerts DoneAlerts, bool AmberMembers)
{
    // What a phone gets before it has ever sent prefs: today's behaviour.
    internal static readonly AlertPrefs Default = new(DoneAlerts.All, AmberMembers: true);

    // A prefs frame with an unknown done_alerts value is ignored rather than
    // guessed at, so a newer phone's setting never silently becomes "all".
    internal static AlertPrefs? Parse(JsonElement body)
    {
        if (!body.TryGetProperty("done_alerts", out var done) || done.ValueKind != JsonValueKind.String) return null;
        if (Mode(done.GetString()) is not { } known) return null;
        var amber = !body.TryGetProperty("amber_members", out var a) || a.ValueKind != JsonValueKind.False;
        return new AlertPrefs(known, amber);
    }

    private static DoneAlerts? Mode(string? name) => name switch
    {
        "all" => DoneAlerts.All,
        "leads" => DoneAlerts.Leads,
        "off" => DoneAlerts.Off,
        _ => null,
    };

    // The choice as relay-identity.json keeps it, in the frame's own words so
    // the file reads like the wire. A phone that never sent prefs has neither
    // key, and a done_alerts this build does not know reads as no choice at
    // all, for the reason Parse refuses one: it must not become a guess.
    internal string DoneAlertsName => DoneAlerts switch
    {
        DoneAlerts.All => "all",
        DoneAlerts.Leads => "leads",
        _ => "off",
    };

    internal static AlertPrefs? FromStored(string? doneAlerts, bool? amberMembers) =>
        Mode(doneAlerts) is { } known ? new AlertPrefs(known, amberMembers ?? true) : null;
}

internal static class RelayAlertRules
{
    // A team member is a session whose parent is still in the roster. A member
    // whose lead has ended (an orphan, CB-13) has nobody to report through, so
    // it counts as a lead.
    internal static bool IsMember(string? parentSessionId, Func<string, bool> inRoster) =>
        !string.IsNullOrEmpty(parentSessionId) && inRoster(parentSessionId);

    internal static bool PushDone(AlertPrefs prefs, bool member) => prefs.DoneAlerts switch
    {
        DoneAlerts.All => true,
        DoneAlerts.Leads => !member,
        _ => false,
    };

    internal static bool PushAmber(AlertPrefs prefs, bool member) => !member || prefs.AmberMembers;

    // The lead's done alert names how many members finished since its last
    // one, so the summary is the one place a team's noise is collapsed.
    internal static string WithMembersSince(string label, int membersSince) =>
        membersSince <= 0 ? label
        : $"{label} · {membersSince} {(membersSince == 1 ? "member" : "members")} finished since";
}

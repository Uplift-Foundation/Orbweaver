namespace Orbweaver
{
    // The `usage` frame and the `limit` push (CB-316, frames-v4
    // sealed_plaintext.usage). Pure: the account readings AccountOrbs already
    // polls go in, canonical plaintexts come out. It projects; it never polls
    // on its own, so the phone's numbers are exactly the rings on the desk.
    internal static class RelayUsage
    {
        internal const int LimitPercent = 80;

        internal sealed record Account(
            string Name, string Cli, string Plan,
            int? WindowPct, long? ResetsAt, int? WeeklyPct, long? WeeklyResetsAt,
            long AsOf, bool Available,
            string? AccountId = null, string? DisplayName = null, string? Initials = null,
            string? IconHash = null, long? OverrideUpdatedAt = null)
        {
            // What a change means for "send again" (frames-v4: any integer
            // percent, resets_at or availability): as_of is deliberately not
            // part of it, or every poll would be a frame. frames-v12 adds the
            // five CB-340 keys, so a rename, a new icon or a Reset reaches the
            // phone without waiting for the next poll.
            //
            // Plan is not in it either (frames-v4 names percents, resets_at and
            // availability, and frames-v12's shape vectors pin `plan_only` as
            // sends:false): a plan change alone reaches the phone with the next
            // frame that is sent for another reason.
            internal object Shape => (Name, Cli, WindowPct, ResetsAt, WeeklyPct, WeeklyResetsAt, Available,
                AccountId, DisplayName, Initials, IconHash, OverrideUpdatedAt);
        }

        // frames-v12 limits.
        internal const int MaxDisplayName = 64;
        internal const int MaxInitials = 16;

        // Half away from zero, clamped: 61.5 -> 62, 61.49 -> 61, 104 -> 100, -1 -> 0.
        internal static int Pct(double percent) =>
            (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);

        internal static string CliOf(AccountUsageSource source) => source switch
        {
            AccountUsageSource.Codex => "codex",
            AccountUsageSource.Grok => "grok",
            _ => "claude",
        };

        // The CB-287 look this PC gives the account, as the phone is told it
        // (frames-v12): the override's name (null without one), the glyph the
        // orb draws, the offered icon's hash (null until its bundle is built)
        // and the override's stamp (null without an override, 0 for one that
        // predates the stamp).
        internal sealed record Look(string? DisplayName, string? Initials, string? IconHash, long? OverrideUpdatedAt);

        internal static Account From(AccountUsage r, string? email = null, Look? look = null)
        {
            // A window the CLI did not report — or one with no reset time to
            // pair with it — is omitted, both keys together.
            int? pct = null, weekly = null;
            long? resets = null, weeklyResets = null;
            if (r.Session is { ResetsAt: { } sr } s) { pct = Pct(s.Percent); resets = sr.ToUnixTimeSeconds(); }
            if (r.Weekly is { ResetsAt: { } wr } w) { weekly = Pct(w.Percent); weeklyResets = wr.ToUnixTimeSeconds(); }
            return new Account(
                string.IsNullOrWhiteSpace(email) ? r.Label : email,
                CliOf(r.Source),
                r.SubscriptionType?.ToLowerInvariant() ?? "",
                pct, resets, weekly, weeklyResets,
                (r.ObservedAt ?? r.ReadAt).ToUnixTimeSeconds(),
                r.Available,
                r.AccountId, look?.DisplayName, look?.Initials, look?.IconHash, look?.OverrideUpdatedAt);
        }

        // Claude, Codex, Grok, and by name within each, so two readings of the
        // same accounts always produce the same frame.
        internal static List<Account> Order(IEnumerable<Account> accounts) =>
            accounts.OrderBy(a => a.Cli switch { "claude" => 0, "codex" => 1, _ => 2 })
                .ThenBy(a => a.Name, StringComparer.Ordinal).ToList();

        internal static string AccountJson(Account a)
        {
            var j = new CanonicalJson().Str("name", a.Name).Str("cli", a.Cli).Str("plan", a.Plan);
            if (a.WindowPct is { } p && a.ResetsAt is { } r) j.Num("window_5h_pct", p).Num("resets_at", r);
            if (a.WeeklyPct is { } wp && a.WeeklyResetsAt is { } wr) j.Num("weekly_pct", wp).Num("weekly_resets_at", wr);
            j.Num("as_of", a.AsOf);

            // frames-v12, in the contract's order, each present only when it
            // meets the grammar a phone checks, so one bad value costs itself
            // and not the account.
            if (IsAccountId(a.AccountId)) j.Str("account_id", a.AccountId!);
            if (DisplayNameOf(a) is { } name) j.Str("display_name", name);
            // Omitted, not cut, when over the cap: a cut grapheme is a different
            // glyph, and the phone draws its own letters from the title anyway.
            if (!string.IsNullOrWhiteSpace(a.Initials) && a.Initials.Length <= MaxInitials) j.Str("initials", a.Initials);
            if (PersonaFrames.IsHash(a.IconHash)) j.Str("icon_hash", a.IconHash!);
            if (a.OverrideUpdatedAt is >= 0 and var at) j.Num("override_updated_at", at);
            return j.ToString();
        }

        // 32 lowercase hex: the shape AccountIdentity produces and the phone
        // accepts. Anything else is left off rather than sent to be ignored.
        internal static bool IsAccountId(string? id) =>
            id is { Length: AccountIdentity.HexLength } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

        private static string? DisplayNameOf(Account a)
        {
            // Cut of a non-blank trimmed string is never blank (it keeps at
            // least one unit besides the mark), so there is nothing to re-check.
            return string.IsNullOrWhiteSpace(a.DisplayName) ? null : PromptFrames.Cut(a.DisplayName.Trim(), MaxDisplayName);
        }

        // What the desktop's log may say about the accounts it just projected
        // (frames-v12 desktop_rules.log): each as its CLI and the first six hex
        // of its account id, "-" for one with none. Never a name, a display
        // name, an email or a provider id — which is also what lets the same
        // account be compared across PCs by reading three logs.
        internal static string LogLine(IEnumerable<Account> accounts) =>
            "usage accounts " + string.Join(' ', accounts.Select(a =>
                a.Cli + ":" + (IsAccountId(a.AccountId) ? AccountIdentity.Short(a.AccountId!) : "-")));

        internal static string Frame(IEnumerable<Account> accounts) =>
            new CanonicalJson().Str("t", "usage").Array("accounts", accounts.Select(AccountJson)).ToString();

        // The push ciphertext's plaintext for a crossing (frames-v4 limit_e),
        // with the PC's default persona as the last key when there is one
        // (frames-v6 limit_e: a crossing belongs to an account, not a session).
        //
        // frames-v12 appends account_id and display_name after the persona, each
        // under the condition it has on the usage account; `accountId` and
        // `displayName` are the fitting rule's way to leave them off.
        internal static string LimitE(Account a, PushPersona? persona = null, bool accountId = true, bool displayName = true)
        {
            var j = new CanonicalJson().Num("v", 1).Str("t", "limit").Str("name", a.Name).Str("cli", a.Cli)
                .Num("window_5h_pct", a.WindowPct!.Value).Num("resets_at", a.ResetsAt!.Value);
            if (persona is not null) j.Raw("persona", persona.Canonical());
            if (accountId && IsAccountId(a.AccountId)) j.Str("account_id", a.AccountId!);
            if (displayName && DisplayNameOf(a) is { } name) j.Str("display_name", name);
            return j.ToString();
        }

        // The limit edge (frames-v4 usage.limit_rule): a reading at or over 80
        // fires when the previous reading for that account was absent, under
        // 80, or for a different 5-hour window.
        internal static bool Fires((int Pct, long ResetsAt)? previous, int pct, long resetsAt) =>
            pct >= LimitPercent && (previous is not { } p || p.Pct < LimitPercent || p.ResetsAt != resetsAt);
    }

    // The one piece of usage state: what was last sent, and each account's last
    // 5-hour reading for the edge rule. One per desktop, shared by every phone.
    internal sealed class RelayUsageTracker
    {
        internal sealed record Send(string Plaintext, RelayUsage.Account? Crossing);

        private readonly Dictionary<(string Name, string Cli), (int Pct, long ResetsAt)> _last = new();
        private List<object>? _shape;

        // The frame a phone coming online is sent (after its roster), or null
        // before the first reading.
        internal string? Latest { get; private set; }

        // The frames this reading produces: nothing when nothing a phone would
        // draw changed; one plain frame for a change; one frame per crossing
        // account, each carrying that account's limit, when any crossed.
        internal IReadOnlyList<Send> Next(IReadOnlyList<RelayUsage.Account> readings)
        {
            var accounts = RelayUsage.Order(readings);
            var crossings = new List<RelayUsage.Account>();
            var seen = new HashSet<(string, string)>();
            foreach (var a in accounts)
            {
                var key = (a.Name, a.Cli);
                seen.Add(key);
                if (a.WindowPct is not { } pct || a.ResetsAt is not { } resets)
                {
                    _last.Remove(key);   // no reading is "absent": the next one re-arms
                    continue;
                }
                if (RelayUsage.Fires(_last.TryGetValue(key, out var prev) ? prev : null, pct, resets)) crossings.Add(a);
                _last[key] = (pct, resets);
            }
            foreach (var key in _last.Keys.ToList())
                if (!seen.Contains(key)) _last.Remove(key);

            var plaintext = RelayUsage.Frame(accounts);
            var shape = accounts.Select(a => a.Shape).ToList();
            var changed = _shape is null || !_shape.SequenceEqual(shape);
            _shape = shape;
            Latest = plaintext;

            if (crossings.Count > 0) return crossings.Select(c => new Send(plaintext, c)).ToList();
            return changed ? [new Send(plaintext, null)] : [];
        }
    }
}

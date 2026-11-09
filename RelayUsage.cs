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
            long AsOf, bool Available)
        {
            // What a change means for "send again" (frames-v4: any integer
            // percent, resets_at or availability): as_of is deliberately not
            // part of it, or every poll would be a frame.
            internal object Shape => (Name, Cli, Plan, WindowPct, ResetsAt, WeeklyPct, WeeklyResetsAt, Available);
        }

        // Half away from zero, clamped: 61.5 -> 62, 61.49 -> 61, 104 -> 100, -1 -> 0.
        internal static int Pct(double percent) =>
            (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);

        internal static string CliOf(AccountUsageSource source) => source switch
        {
            AccountUsageSource.Codex => "codex",
            AccountUsageSource.Grok => "grok",
            _ => "claude",
        };

        internal static Account From(AccountUsage r, string? email = null)
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
                r.Available);
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
            return j.Num("as_of", a.AsOf).ToString();
        }

        internal static string Frame(IEnumerable<Account> accounts) =>
            new CanonicalJson().Str("t", "usage").Array("accounts", accounts.Select(AccountJson)).ToString();

        // The push ciphertext's plaintext for a crossing (frames-v4 limit_e).
        internal static string LimitE(Account a) =>
            new CanonicalJson().Num("v", 1).Str("t", "limit").Str("name", a.Name).Str("cli", a.Cli)
                .Num("window_5h_pct", a.WindowPct ?? 0).Num("resets_at", a.ResetsAt ?? 0).ToString();

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
            foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);

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

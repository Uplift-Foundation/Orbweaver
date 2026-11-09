using System.Globalization;
using System.Text;

namespace Orbweaver
{
    // The usage frame and the pushes that ride on it (CB-316, frames-v4.json
    // usage, limit_e, done_e). Pure: readings in, canonical plaintext out.
    internal static class PhoneUsage
    {
        // The edge the 5-hour window must cross for a near-limit alert.
        internal const int LimitPercent = 80;

        // One account as the phone sees it. Windows are present or absent
        // together with their reset time: the CLI either reported a window or
        // it did not.
        internal sealed record Account(
            string Name, string Cli, string Plan,
            int? Pct5h, long? ResetsAt, int? WeeklyPct, long? WeeklyResetsAt, long AsOf)
        {
            internal string Key => Cli + "\n" + Name;
        }

        // The CLI's percentage rounded half away from zero and clamped to 0-100.
        internal static int Round(double percent) =>
            (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);

        internal static string CliOf(AccountUsageSource source) => source switch
        {
            AccountUsageSource.Grok => "grok",
            AccountUsageSource.Codex => "codex",
            _ => "claude",
        };

        // Every account the poller read, in a stable order (cli, then name), the
        // windows that have not expired and the reading's own time.
        internal static IReadOnlyList<Account> From(IEnumerable<AccountUsage> readings, DateTimeOffset now) =>
            readings
                .Select(r =>
                {
                    var five = r.Available ? r.LiveSession(now) : null;
                    var week = r.Available ? r.LiveWeekly(now) : null;
                    return new Account(
                        r.Label, CliOf(r.Source), (r.SubscriptionType ?? "").ToLowerInvariant(),
                        five is null ? null : Round(five.Percent),
                        five?.ResetsAt?.ToUnixTimeSeconds(),
                        week is null ? null : Round(week.Percent),
                        week?.ResetsAt?.ToUnixTimeSeconds(),
                        r.AsOf.ToUnixTimeSeconds());
                })
                .OrderBy(a => a.Cli, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal)
                .ToList();

        private static string Int(long n) => n.ToString(CultureInfo.InvariantCulture);
        private static string Str(string s) => "\"" + AnswerSigning.Escape(s) + "\"";

        internal static string AccountJson(Account a)
        {
            var sb = new StringBuilder("{\"name\":").Append(Str(a.Name)).Append(",\"cli\":").Append(Str(a.Cli)).Append(",\"plan\":").Append(Str(a.Plan));
            if (a.Pct5h is { } p && a.ResetsAt is { } r) sb.Append(",\"window_5h_pct\":").Append(Int(p)).Append(",\"resets_at\":").Append(Int(r));
            if (a.WeeklyPct is { } w && a.WeeklyResetsAt is { } wr) sb.Append(",\"weekly_pct\":").Append(Int(w)).Append(",\"weekly_resets_at\":").Append(Int(wr));
            return sb.Append(",\"as_of\":").Append(Int(a.AsOf)).Append('}').ToString();
        }

        internal static string Frame(IReadOnlyList<Account> accounts) =>
            "{\"t\":\"usage\",\"accounts\":[" + string.Join(",", accounts.Select(AccountJson)) + "]}";

        // What a frame says apart from its times: a new frame is worth sending
        // when an integer percent, a reset time or an account's availability
        // moved. as_of alone ticking over is not news.
        internal static string Signature(IReadOnlyList<Account> accounts) =>
            string.Join("|", accounts.Select(a => $"{a.Key}\n{a.Plan}\n{a.Pct5h}\n{a.ResetsAt}\n{a.WeeklyPct}\n{a.WeeklyResetsAt}"));

        // The e of a limit push: the crossing account, the integer percent that
        // crossed and when the window resets.
        internal static string LimitE(Account a) =>
            "{\"v\":1,\"t\":\"limit\",\"name\":" + Str(a.Name) + ",\"cli\":" + Str(a.Cli)
            + ",\"window_5h_pct\":" + Int(a.Pct5h ?? 0) + ",\"resets_at\":" + Int(a.ResetsAt ?? 0) + "}";

        // The e of a done push: '<title or folder> on <machine>' as the amber's
        // label, and the title.
        internal static string DoneE(string sessionId, string label, string title) =>
            "{\"v\":1,\"t\":\"done\",\"session_id\":" + Str(sessionId) + ",\"label\":" + Str(label) + ",\"title\":" + Str(title) + "}";

        internal static string DoneLabel(string title, string machine) =>
            string.IsNullOrWhiteSpace(title) ? machine : $"{title} on {machine}";
    }

    // Edge-triggered: an account fires when its 5-hour window is at or above
    // 80% and the previous reading for it was absent, below 80 or for a
    // different window (its resets_at differs). Held per desktop, so a restart
    // may alert once more for an account already past the edge: accepted.
    internal sealed class UsageLimitWatch
    {
        private readonly Dictionary<string, (int Pct, long? ResetsAt)> _last = new(StringComparer.Ordinal);

        // The accounts that crossed on this reading, in account order.
        internal IReadOnlyList<PhoneUsage.Account> Crossings(IReadOnlyList<PhoneUsage.Account> accounts)
        {
            var fired = new List<PhoneUsage.Account>();
            foreach (var a in accounts)
            {
                if (a.Pct5h is not { } pct)
                {
                    _last.Remove(a.Key);
                    continue;
                }
                var crossed = pct >= PhoneUsage.LimitPercent
                    && (!_last.TryGetValue(a.Key, out var before) || before.Pct < PhoneUsage.LimitPercent || before.ResetsAt != a.ResetsAt);
                _last[a.Key] = (pct, a.ResetsAt);
                if (crossed) fired.Add(a);
            }
            return fired;
        }
    }
}

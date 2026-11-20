using System;
using System.Globalization;
using System.Text.Json;

namespace Orbweaver
{
    // How much of a rate-limit window an account has spent, as Claude Code
    // reports it.
    //
    // **Percent, not a fraction, and that is the whole reason this is a named
    // type rather than a bare double.** Claude Code publishes the same numbers
    // on two scales from two places: the `get_usage` control response this app
    // reads gives 0-100 (`"utilization": 84` is 84% used, and the CLI's own
    // /usage prints exactly that), while the statusline payload derives its copy
    // from the `anthropic-ratelimit-unified-*` response headers and gives 0-1.
    // Reading one on the other's scale is a hundred-fold error that still draws
    // a perfectly plausible ring, so the scale lives in the type name and the
    // parser is the only place that has to know it.
    //
    // Above 100 is legal rather than a bug — usage can run past a window's cap —
    // and is deliberately not clamped here. Clamping is a drawing decision and
    // belongs to UsageRingGeometry; a card that wants to print "104%" should be
    // able to.
    internal sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt)
    {
        // Whether this number still means anything.
        //
        // A percentage describes the period ending at ResetsAt. Once that moment
        // passes the figure is not stale-but-indicative, it is simply about a
        // period that has ended — Claude Code's own guidance is to expire any UI
        // derived from this field. Drawing it anyway is worse than drawing
        // nothing, because a ring at 89% for a window that reset an hour ago is
        // a confident wrong answer.
        //
        // A window with no ResetsAt never expires: the server sent a number
        // without a deadline, and inventing one here would be this file making
        // up policy.
        public bool HasExpired(DateTimeOffset now) => ResetsAt is { } at && now >= at;
    }

    // The extra-usage (overage) position on an account, in minor units.
    //
    // Minor units — cents — because that is what the API sends
    // (`spend.used.amount_minor` alongside `exponent: 2`), and converting on the
    // way in would mean rounding money twice. Formatting is the card's job.
    //
    // Enabled is not "has spent nothing yet". An account can have extra usage
    // switched off at the org level, which is what DisabledReason carries, and
    // the difference matters to the drawing: a disabled account has no cap to be
    // a fraction of, so its ring is an absence rather than a gauge sitting at
    // zero. Every account on the machine this was written against is in exactly
    // that state ("org_level_disabled_until"), which is why it is modelled
    // rather than treated as the empty case.
    // SpendLimitReached and UserDisabled are the two causes this app is
    // entitled to state out loud. They are booleans the API sends about
    // specific, named facts, and they mean what they say.
    //
    // `DisabledReason` is not. It is an opaque string, and this app
    // deliberately does **not** translate it — the first version did, mapping
    // "org_level_disabled_until" to "extra usage is off for your organisation",
    // which was shown to a user whose organisation had done no such thing. What
    // had actually happened was the month's extra-usage budget running out,
    // which `spend_limit_reached` states plainly one field away. A reason code
    // seen once, with no documentation, is not a sentence.
    internal sealed record ExtraUsage(
        bool Enabled,
        long? UsedMinor,
        long? LimitMinor,
        string Currency,
        int DecimalPlaces,
        string? DisabledReason,
        bool UserDisabled = false,
        bool SpendLimitReached = false)
    {
        // The share of the cap spent, or null when there is no cap to be a share
        // of. Null is the common case and is not a failure.
        public double? Percent =>
            Enabled && UsedMinor is { } used && LimitMinor is { } limit && limit > 0
                ? used * 100.0 / limit
                : null;

        // What the inner ring should draw.
        //
        // A spend limit that has been reached is a full ring, not an absent one,
        // even when the API sends no numbers to go with it. "You have spent all
        // of it" and "there is none here" are opposite states, and the first
        // version drew them identically — as a dotted absence — which is how a
        // spent budget came to look like an account that had never had one.
        public double? RingPercent =>
            Percent ?? (SpendLimitReached ? 100 : null);
    }

    // One account's usage, as of one reading.
    //
    // ConfigDir is null for the account this app itself runs under — the same
    // convention BackgroundJobs.ReadOne uses for "leave the environment alone" —
    // and a full path for every extra account. It doubles as the identity: it is
    // what CLAUDE_CONFIG_DIR was set to when the reading was taken, so two
    // readings cannot be confused for one another.
    //
    // Available is not the same as "has windows". It mirrors the CLI's own
    // `rate_limits_available`, which is false for API-key, Bedrock and Vertex
    // accounts — perfectly healthy accounts that simply have no subscription
    // windows to report. Those get an orb that says so, rather than one that
    // looks like an error or no orb at all.

    // Which CLI this reading came from. The pollers share one orb collection,
    // and turning one source off must not close the others' orbs — or leave
    // them up after their switch went off. The keep-stale rule in
    // AccountOrbs.Apply cannot tell "this source was not asked" from "this
    // source failed", so the reading itself has to say who produced it.
    internal enum AccountUsageSource { ClaudeCode, Grok, Codex }

    internal sealed record AccountUsage(
        string? ConfigDir,
        string Label,
        bool Available,
        string? SubscriptionType,
        UsageWindow? Session,
        UsageWindow? Weekly,
        ExtraUsage? Extra,
        DateTimeOffset ReadAt,
        AccountUsageSource Source = AccountUsageSource.ClaudeCode,
        DateTimeOffset? ObservedAt = null,
        string? AccountId = null)
    {
        // CB-340: AccountIdentity's id for this account, or null when the CLI's
        // identity file offered none. Carried so the phone can merge one
        // account's readings across PCs; never drawn, and deliberately not part
        // of DrawnValuesDiffer — no ring depends on it.
        // How long a reading is trusted before its orb is dimmed.
        //
        // Three missed polls, not one. The poller runs every five minutes, and a
        // single failure is the ordinary case — a laptop that slept, a network
        // that blinked, a CLI upgraded mid-poll. Dimming on the first miss would
        // have the orbs flickering between confident and doubtful all day for no
        // information gain, while a quarter of an hour of silence genuinely does
        // mean nobody has managed to ask.
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

        // When the number was *true*, as opposed to when this app looked.
        //
        // For the Claude Code poller the two are the same moment: it asks the
        // CLI and the CLI answers about now, so ObservedAt is left null and this
        // falls back to ReadAt. The Codex and Grok readings are different in
        // kind — neither CLI can be asked, so both are read out of a file the
        // CLI last wrote whenever it last ran, which may have been days ago.
        //
        // Keeping the two apart is the whole point. Before CB-83 both file
        // pollers stamped ReadAt with UtcNow, which is honest about the read and
        // a lie about the number: a Grok reading taken from a log line written
        // 38 hours earlier could never go stale, never dimmed its orb, and had
        // its card announce "Last read 0m ago". Everything downstream asks
        // AsOf now, so an old number reads as old wherever it is drawn.
        public DateTimeOffset AsOf => ObservedAt ?? ReadAt;

        public bool IsStale(DateTimeOffset now) => now - AsOf >= StaleAfter;

        // What each ring should actually draw: the window, unless it has
        // expired, in which case nothing. Asked here rather than at each call
        // site so every caller answers the question the same way.
        public UsageWindow? LiveSession(DateTimeOffset now) => Live(Session, now);

        public UsageWindow? LiveWeekly(DateTimeOffset now) => Live(Weekly, now);

        private static UsageWindow? Live(UsageWindow? window, DateTimeOffset now) =>
            window is null || window.HasExpired(now) ? null : window;
    }

    // Turning the CLI's answer into the record above, and nothing else.
    //
    // Pure and static so all of it is testable without launching anything:
    // UsagePoller owns the subprocess, this owns the meaning of what comes back.
    // That split is BackgroundJobs' — its ReadOne is excluded from coverage and
    // its Parse is covered against hand-written listings — and it is the only
    // reason a feature whose data arrives from another program can be tested at
    // all.
    //
    // **Everything here is lenient by design.** The `get_usage` control request
    // is documented as experimental and the schema it arrives through is
    // `.passthrough()` at every level, so new keys will appear and existing ones
    // may go null without warning. Every field is optional, every unexpected
    // shape degrades to null, and nothing throws: an answer that cannot be
    // understood has to read as "no reading" — which callers already treat as
    // "change nothing" — and must never read as zero usage.
    internal static class UsageParse
    {
        // The one line worth reading out of the stream, and the payload in it.
        //
        // The CLI answers a control request on stdout as JSONL, and in print
        // mode it emits other rows too. So this scans for the control_response
        // rather than assuming a position, and ignores any row it cannot parse
        // rather than failing the whole read because something unrelated changed
        // shape.
        internal static AccountUsage? FromStream(
            string? stdout, string? configDir, string label, DateTimeOffset readAt)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{') continue;

                // Cheap reject before parsing, the way TranscriptIdentity
                // anchors on a prefix: most rows in this stream are not the one
                // wanted, and parsing every one to find that out is work for
                // nothing on a poll that runs for as long as the app does.
                if (!trimmed.Contains("\"control_response\"", StringComparison.Ordinal)) continue;

                var usage = FromResponseLine(trimmed, configDir, label, readAt);
                if (usage is not null) return usage;
            }

            return null;
        }

        internal static AccountUsage? FromResponseLine(
            string line, string? configDir, string label, DateTimeOffset readAt)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (Str(root, "type") != "control_response") return null;
                if (!Obj(root, "response", out var response)) return null;

                // An error response carries subtype "error" and no payload.
                // Treated as no reading rather than an empty one, for the same
                // reason a failed launch is: the account's usage is unknown, not
                // zero.
                if (Str(response, "subtype") != "success") return null;
                if (!Obj(response, "response", out var payload)) return null;

                return FromPayload(payload, configDir, label, readAt);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The `get_usage` payload: { session, subscription_type,
        // rate_limits_available, rate_limits, behaviors }.
        //
        // `behaviors` and `session` are deliberately ignored. The first is a
        // local transcript scan the CLI does for its own /usage display and has
        // nothing to do with subscription windows; the second is the counters of
        // the throwaway process that answered, which are always zero because it
        // makes no model call.
        internal static AccountUsage? FromPayload(
            JsonElement payload, string? configDir, string label, DateTimeOffset readAt)
        {
            var available = Bool(payload, "rate_limits_available") ?? false;
            var subscription = Str(payload, "subscription_type");

            if (!Obj(payload, "rate_limits", out var limits))
            {
                // Reached by an API-key, Bedrock or Vertex account, which
                // answers successfully and simply has no windows. A real
                // reading, and one worth drawing.
                return new AccountUsage(
                    configDir, label, available, subscription, null, null, null, readAt);
            }

            return new AccountUsage(
                configDir,
                label,
                available,
                subscription,
                Window(limits, "five_hour"),
                Window(limits, "seven_day"),
                Extra(limits),
                readAt);
        }

        private static UsageWindow? Window(JsonElement limits, string name)
        {
            if (!Obj(limits, name, out var window)) return null;

            var percent = Num(window, "utilization");
            if (percent is null) return null;

            return new UsageWindow(percent.Value, Instant(window, "resets_at"));
        }

        // Extra usage, read from `spend` with `extra_usage` filling in what
        // `spend` does not carry.
        //
        // Two objects describe one thing and neither is sufficient alone:
        // `spend` has the money (`used.amount_minor`, `limit`) while
        // `extra_usage` has the currency's decimal places and, on the accounts
        // seen so far, the same `disabled_reason`. Preferring `spend` keeps the
        // money coming from the object that states its own exponent, which is
        // the one that cannot be misread by a factor of a hundred.
        private static ExtraUsage? Extra(JsonElement limits)
        {
            var hasSpend = Obj(limits, "spend", out var spend);
            var hasExtra = Obj(limits, "extra_usage", out var extra);

            if (!hasSpend && !hasExtra) return null;

            var enabled = (hasSpend ? Bool(spend, "enabled") : null)
                          ?? (hasExtra ? Bool(extra, "is_enabled") : null)
                          ?? false;

            long? used = null;
            var currency = "USD";

            if (hasSpend && Obj(spend, "used", out var usedObj))
            {
                used = Int(usedObj, "amount_minor");
                currency = Str(usedObj, "currency") ?? currency;
            }

            var limit = hasSpend ? Int(spend, "limit") : null;
            if (limit is null && hasExtra) limit = Int(extra, "monthly_limit");

            if (hasExtra) currency = Str(extra, "currency") ?? currency;

            var reason = (hasSpend ? Str(spend, "disabled_reason") : null)
                         ?? (hasExtra ? Str(extra, "disabled_reason") : null);

            var places = (hasExtra ? Int(extra, "decimal_places") : null) ?? 2;

            var userDisabled = (hasExtra ? Bool(extra, "user_disabled") : null) ?? false;
            var limitReached = (hasExtra ? Bool(extra, "spend_limit_reached") : null) ?? false;

            return new ExtraUsage(
                enabled, used, limit, currency, (int)places,
                reason, userDisabled, limitReached);
        }

        // ---- JSON helpers ---------------------------------------------------
        //
        // Every one answers null rather than throwing on the wrong kind, because
        // "the server sends something else this week" has to read as an absent
        // field and not as a crashed poll.

        private static bool Obj(JsonElement parent, string name, out JsonElement value)
        {
            if (parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            value = default;
            return false;
        }

        private static string? Str(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool? Bool(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        private static double? Num(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
                ? number
                : null;

        private static long? Int(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (value.TryGetInt64(out var whole)) return whole;

            // A money field arriving as 1234.0 rather than 1234 is still a
            // number of cents; rounding beats discarding it.
            return value.TryGetDouble(out var real) ? (long)Math.Round(real) : null;
        }

        // resets_at is ISO-8601 with an offset in this payload
        // ("2026-09-03T04:59:59.643579+00:00"), unlike the statusline's epoch
        // seconds. Both spellings are accepted anyway: the two sources
        // disagreeing about this once already is reason enough not to assume
        // which is on the wire.
        private static DateTimeOffset? Instant(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var epochSeconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(epochSeconds); }
                catch (ArgumentOutOfRangeException) { return null; }
            }

            if (value.ValueKind != JsonValueKind.String) return null;

            return DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : null;
        }
    }
}

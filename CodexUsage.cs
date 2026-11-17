using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;

namespace Orbweaver
{
    // Turning Codex's rate_limits snapshot into AccountUsage, and nothing else.
    //
    // Pure so a fixture copied from a real token_count event covers every field
    // without launching Codex or walking a live sessions tree. Measured on
    // codex-cli 0.151.0, 31 Aug 2026 — see docs/codex-findings.md.
    internal static class CodexUsageParse
    {
        // A five-hour window is 300 minutes; a week is 10080. Anything at or
        // under twelve hours is the inner "this session" ring, everything
        // longer is the week. Twelve rather than five so a slightly different
        // primary window still lands on the ring a person would look at.
        private const int SessionWindowMinutes = 12 * 60;

        internal static AccountUsage? FromRateLimits(
            string? json, string? codexHome, string label, DateTimeOffset readAt,
            DateTimeOffset? observedAt = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var limits = Unwrap(document.RootElement);
                if (limits is null) return null;

                UsageWindow? session = null;
                UsageWindow? weekly = null;
                Assign(limits.Value, "primary", ref session, ref weekly);
                Assign(limits.Value, "secondary", ref session, ref weekly);

                var plan = Str(limits.Value, "plan_type") ?? Str(limits.Value, "planType");
                var extra = Extra(limits.Value);

                return new AccountUsage(
                    codexHome,
                    label,
                    session is not null || weekly is not null,
                    plan,
                    session,
                    weekly,
                    extra,
                    readAt,
                    AccountUsageSource.Codex,
                    observedAt);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Whether a line is worth keeping at all: does it carry a window with a
        // percentage in it?
        //
        // The same lenience as everything else here — an unparseable line, or
        // one whose windows are both null, is not a reading. See the comment on
        // CodexUsagePoller.LatestSnapshotFrom for why the null/null case is the
        // one that matters and is not rare.
        internal static bool HasWindow(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                using var document = JsonDocument.Parse(json);
                var limits = Unwrap(document.RootElement);
                if (limits is null) return false;

                return HasPercent(limits.Value, "primary")
                       || HasPercent(limits.Value, "secondary");
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool HasPercent(JsonElement limits, string name) =>
            limits.TryGetProperty(name, out var window)
            && window.ValueKind == JsonValueKind.Object
            && Field(window, "used_percent", "usedPercent", out var percent)
            && percent.ValueKind == JsonValueKind.Number
            && percent.TryGetDouble(out _);

        // The moment Codex wrote the line, off the JSONL envelope.
        //
        // Null for anything without one, which is deliberate: guessing an age
        // for a snapshot that did not state one would put a fabricated number
        // in front of the very rule this exists to serve.
        internal static DateTimeOffset? TimestampOf(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("timestamp", out var stamp)
                    || stamp.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                return DateTimeOffset.TryParse(
                    stamp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The snapshot is nested five different ways in the wild, and the two
        // added by CB-85 come from a different transport rather than a
        // different nesting of the same one.
        //
        // From the rollout: a bare rate_limits object (what the tests hand
        // this), payload.rate_limits on a token_count event, and a token_count
        // event inside the JSONL envelope. From `codex app-server`: the JSON-RPC
        // `result` carrying `rateLimits`, and that `result` unwrapped. Unwrap
        // rather than requiring the caller to know which — the caller knows
        // which transport it used, but nothing else here needs to.
        private static JsonElement? Unwrap(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("rate_limits", out var nested)
                && nested.ValueKind == JsonValueKind.Object)
            {
                return nested;
            }

            if (root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && Obj(result, "rateLimits") is { } fromResult)
            {
                return fromResult;
            }

            if (root.TryGetProperty("rate_limits", out var mid)
                && mid.ValueKind == JsonValueKind.Object)
            {
                return mid;
            }

            if (Obj(root, "rateLimits") is { } live) return live;

            if (root.TryGetProperty("primary", out _)) return root;

            return null;
        }

        private static JsonElement? Obj(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object
                ? value
                : null;

        // One field under either of its two spellings.
        //
        // The rollout writes snake_case (`used_percent`) and the app-server
        // answers camelCase (`usedPercent`) — the same numbers over two
        // transports, and the difference is not something either end will
        // reconcile for us. Asking for both here rather than translating a whole
        // document keeps one parser for one meaning, which matters because the
        // window-selection rule and the credits rules below are the subtle part
        // and nobody wants two copies of them.
        private static bool Field(
            JsonElement parent, string snake, string camel, out JsonElement value) =>
            parent.TryGetProperty(snake, out value)
            || parent.TryGetProperty(camel, out value);

        private static void Assign(
            JsonElement limits, string name,
            ref UsageWindow? session, ref UsageWindow? weekly)
        {
            if (!limits.TryGetProperty(name, out var window)
                || window.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // ValueKind before TryGetDouble, which throws rather than
            // returning false when the element is not a number at all. Every
            // other field here degrades to null on a surprise, and this one
            // would have taken the whole reading down with an
            // InvalidOperationException that FromRateLimits does not catch —
            // the one shape in a deliberately lenient parser that was not.
            if (!Field(window, "used_percent", "usedPercent", out var percentEl)
                || percentEl.ValueKind != JsonValueKind.Number
                || !percentEl.TryGetDouble(out var percent))
            {
                return;
            }

            DateTimeOffset? resets = null;
            if (Field(window, "resets_at", "resetsAt", out var at)
                && at.ValueKind == JsonValueKind.Number
                && at.TryGetInt64(out var unix))
            {
                resets = DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            var parsed = new UsageWindow(percent, resets);

            var minutes = Field(window, "window_minutes", "windowDurationMins", out var minEl)
                          && minEl.ValueKind == JsonValueKind.Number
                          && minEl.TryGetInt32(out var m)
                ? m
                : (int?)null;

            if (minutes is { } n && n <= SessionWindowMinutes) session = parsed;
            else weekly = parsed;
        }

        private static ExtraUsage Extra(JsonElement limits)
        {
            if (!limits.TryGetProperty("credits", out var credits)
                || credits.ValueKind != JsonValueKind.Object)
            {
                return new ExtraUsage(false, null, null, "", 0, "no_credits");
            }

            var unlimited = credits.TryGetProperty("unlimited", out var unl)
                            && unl.ValueKind == JsonValueKind.True;
            if (unlimited)
                return new ExtraUsage(false, null, null, "", 0, "unlimited");

            var has = Field(credits, "has_credits", "hasCredits", out var hasEl)
                      && hasEl.ValueKind == JsonValueKind.True;
            if (!has)
                return new ExtraUsage(false, null, null, "", 0, "no_credits");

            // A remaining balance without a cap is not a percentage. Drawing it
            // as a ring would invent a share of a budget Codex did not send.
            return new ExtraUsage(false, null, null, "", 0, "credits_no_cap");
        }

        private static string? Str(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
    }

    internal static class CodexUsageAccounts
    {
        internal static string AuthFilePath(string codexHome) =>
            Path.Combine(codexHome, "auth.json");

        internal static string FallbackLabel(string? codexHome)
        {
            if (string.IsNullOrWhiteSpace(codexHome)) return "codex";
            var name = Path.GetFileName(codexHome.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return "codex";
            name = name.TrimStart('.');
            if (name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase)
                && name.Length > "codex-".Length)
            {
                name = name["codex-".Length..];
            }

            return name.Length == 0 ? "codex" : name;
        }

        // Email local-part if auth.json has one. Never the tokens object, never
        // OPENAI_API_KEY — those rotate, and a second reader that loses a
        // compare-and-swap logs the user out. Same argument GrokUsageAccounts
        // makes for not reading Grok's refresh token.
        //
        // CB-340 narrowed that, deliberately and in one place: AccountIdentity.Codex
        // now reads exactly two identifier strings out of `tokens` —
        // tokens.account_id and the id_token's chatgpt_user_id claim — so one
        // Codex account can be recognised across PCs. The objection above was
        // always to *refreshing* or *writing* a rotating token, which a read of
        // an identifier does not touch; nothing from `tokens` is logged, stored
        // or sent, and access_token, refresh_token and OPENAI_API_KEY stay
        // unread. This method still skips `tokens`: the label never needed it.
        internal static string LabelFrom(string? json, string? codexHome)
        {
            var fallback = FallbackLabel(codexHome);
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return fallback;

                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("tokens")
                        || property.NameEquals("OPENAI_API_KEY"))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.NameEquals("email"))
                    {
                        var local = LocalPart(property.Value.GetString());
                        if (local is not null) return local;
                    }

                    if (property.Value.ValueKind != JsonValueKind.Object) continue;
                    if (property.Value.TryGetProperty("email", out var email)
                        && email.ValueKind == JsonValueKind.String)
                    {
                        var local = LocalPart(email.GetString());
                        if (local is not null) return local;
                    }
                }
            }
            catch (JsonException)
            {
                return fallback;
            }

            return fallback;
        }

        private static string? LocalPart(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            var at = address.IndexOf('@');
            var local = at > 0 ? address[..at] : address;
            return local.Length > 0 ? local : null;
        }

        internal static List<string> Homes(string home, IReadOnlyList<string> extras)
        {
            var codex = Path.Combine(home, ".codex");
            var dirs = new List<string> { codex };
            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;
                var path = extra.StartsWith(Path.DirectorySeparatorChar)
                           || extra.StartsWith(Path.AltDirectorySeparatorChar)
                    ? extra
                    : Path.Combine(home, extra);
                if (!string.Equals(path, codex, StringComparison.Ordinal))
                    dirs.Add(path);
            }

            return dirs;
        }
    }

    // One token_count line, and the moment Codex wrote it.
    //
    // The timestamp travels with the JSON because it is the only thing that can
    // order snapshots across files, and because the reading it becomes has to
    // say how old it is. Null means the envelope carried no timestamp — a bare
    // rate_limits object handed in by a test, or a shape Codex has changed —
    // in which case the caller falls back to the file's own mtime for ordering
    // and the reading falls back to ReadAt for its age.
    internal readonly record struct CodexSnapshot(string Json, DateTimeOffset? WrittenAt);

    // Last-resort read: the newest token_count rate_limits snapshot in
    // $CODEX_HOME/sessions/**/rollout-*.jsonl. No token, no process. Freshness
    // is "as of the last Codex session", which the reading now states outright
    // by carrying the snapshot's own timestamp as ObservedAt.
    internal sealed class CodexUsagePoller : IUsageSource
    {
        internal const int MaxLineBytes = 32_768;

        public IReadOnlyList<AccountUsage> Read()
        {
            if (!OrbweaverSettings.CodexAccountUsageEnabled)
                return Array.Empty<AccountUsage>();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return ReadFrom(
                CodexUsageAccounts.Homes(home, OrbweaverSettings.CodexHomes),
                DateTimeOffset.UtcNow);
        }

        // One reading per $CODEX_HOME it is handed, live if Codex will say and
        // out of the rollout if it will not.
        //
        // Split out of Read so the per-account path can be driven against a
        // temp directory: Read itself can only ever be pointed at this
        // machine's real home, and a test that scanned it would be reading
        // whatever rollouts the developer happens to have — slow, different on
        // every machine, and unrunnable on a CI leg with no Codex installed.
        // What is left in Read is the two lines that are genuinely about this
        // machine, the switch and where home is.
        //
        // `ask` is the app-server call, a parameter so both branches are
        // reachable without a subprocess: a test hands it a captured payload,
        // or null to force the fallback. Null here means "use the real one",
        // which is the same defaulting ClaudeBinary.Locate uses for the same
        // reason.
        //
        // **The order matters and is not a preference.** The live answer is
        // current; the rollout is whatever the last session wrote, which CB-83
        // established can be hours old. So the live read is tried first and the
        // rollout answers only when it returns nothing usable — Codex not
        // installed where CodexBinary can find it, an app-server too old to know
        // the method, a machine where spawning it fails. "Usable" is
        // Available, not merely parseable, for CB-83's reason exactly: the live
        // call can answer with both windows null when the workspace has run out
        // of credits, and falling through to a real number from an hour ago is
        // better than drawing an account with no limits. A stale number honestly
        // dated beats no orb, which is why the fallback stays rather than being
        // deleted the moment something better exists.
        internal static IReadOnlyList<AccountUsage> ReadFrom(
            IEnumerable<string> homes, DateTimeOffset readAt,
            Func<string, string?>? ask = null)
        {
            ask ??= LiveAsk;

            var readings = new List<AccountUsage>();
            foreach (var codexHome in homes)
            {
                var auth = ReadAuth(codexHome);
                var label = CodexUsageAccounts.LabelFrom(auth, codexHome);
                var accountId = AccountIdentity.Codex(auth);

                // ObservedAt is left null on the live answer, so AsOf falls back
                // to the read: the number *is* current, and stamping it with a
                // snapshot time it does not have would be the CB-83 mistake
                // pointing the other way.
                var live = CodexUsageParse.FromRateLimits(
                    ask(codexHome), codexHome, label, readAt);
                if (live is { Available: true })
                {
                    readings.Add(live with { AccountId = accountId });
                    continue;
                }

                var snapshot = LatestSnapshotFrom(Path.Combine(codexHome, "sessions"));
                var usage = CodexUsageParse.FromRateLimits(
                    snapshot?.Json, codexHome, label, readAt, snapshot?.WrittenAt);
                if (usage is not null) readings.Add(usage with { AccountId = accountId });
            }

            return readings;
        }

        // The real app-server call, behind the same null-object shape the rest
        // of this file uses: no binary means no live answer, not an exception.
        [ExcludeFromCodeCoverage]
        private static string? LiveAsk(string codexHome)
        {
            var codex = CodexBinary.Path;
            return codex is null ? null : CodexAppServerUsage.Ask(codex, codexHome);
        }

        // The newest snapshot that actually carries a window, across every
        // rollout in the tree.
        //
        // Two things here were wrong before CB-83 and are worth stating, since
        // both looked reasonable and both produced a confidently wrong orb.
        //
        // **A file's mtime is not its newest snapshot's timestamp.** The old
        // scan took the newest-modified rollout and stopped there, which is only
        // the same thing when one session is running. Several are, routinely,
        // and the file touched last is whichever one wrote *anything* last — a
        // tool result, an agent message — not the one holding the latest usage.
        //
        // **A snapshot with no windows is not a reading.** Codex emits
        // `primary: null, secondary: null` alongside a rate_limit_reached_type,
        // and emits it to every live session at once, so on a busy machine the
        // newest line in the newest file is reliably the empty one. Taking it
        // handed the card an Available:false reading, which it renders as "No
        // subscription limits on this account." — said about a Team account
        // measured at 99% of its five-hour window, from a line 0.3 seconds
        // earlier in the very same file.
        //
        // So: keep only lines with a window, and order by what the snapshot says
        // about itself. mtime survives as a *bound* rather than an answer — no
        // line in a file can post-date the file's last write, so once the best
        // snapshot so far is newer than the next file's mtime, nothing further
        // down the list can beat it and the scan stops. That keeps the common
        // case at roughly the cost of the old one-file read.
        internal static CodexSnapshot? LatestSnapshotFrom(string sessionsDir)
        {
            if (!Directory.Exists(sessionsDir)) return null;

            List<(string Path, DateTimeOffset Modified)> files;
            try
            {
                files = Directory
                    .EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Select(p => (Path: p, Modified: new DateTimeOffset(File.GetLastWriteTimeUtc(p))))
                    .OrderByDescending(f => f.Modified)
                    .ToList();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            CodexSnapshot? best = null;
            DateTimeOffset bestAt = DateTimeOffset.MinValue;

            foreach (var (path, modified) in files)
            {
                if (best is not null && bestAt >= modified) break;

                var line = LatestWindowedLine(path);
                if (line is null) continue;

                var writtenAt = CodexUsageParse.TimestampOf(line);
                var at = writtenAt ?? modified;
                if (best is not null && at <= bestAt) continue;

                best = new CodexSnapshot(line, writtenAt);
                bestAt = at;
            }

            return best;
        }

        // The last line in one rollout that carries a usable snapshot.
        //
        // Last rather than best-by-timestamp because a rollout is append-only
        // and written by one process, so its lines are already in order; the
        // cross-file pass is where ordering has to be reasoned about. The
        // substring tests stay as a cheap reject before parsing — most lines in
        // a multi-megabyte rollout are not this one, and JSON-parsing every line
        // to find that out is work for nothing on a poll that runs all day.
        internal static string? LatestWindowedLine(string path)
        {
            try
            {
                string? last = null;
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length > MaxLineBytes) continue;
                    if (!line.Contains("\"token_count\"", StringComparison.Ordinal)
                        || !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (CodexUsageParse.HasWindow(line)) last = line;
                }

                return last;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string? ReadAuth(string codexHome)
        {
            try
            {
                var path = CodexUsageAccounts.AuthFilePath(codexHome);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}

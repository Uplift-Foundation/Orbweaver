using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // CB-404: the windowless half of the model dropdown and effort dial.
    //
    // Everything here is pure: what a session is running, what it could run,
    // which route a change would take and what the panel should therefore draw.
    // RunControlsHost reads files and sends keys or control requests; the
    // session classes and the panel only draw what Resolve hands back. Kept apart
    // so each outcome — enabled, disabled with a reason, not known yet, pending —
    // is one unit case rather than a window to construct (docs/plans/
    // model-effort-controls-plan.md, 4.1).

    public enum RunCli { Claude, ClaudeCloud, Codex, Grok, OpenClaw }

    public enum RunField { Model, Effort }

    // How a change reaches the session. Control is a control request over a
    // Remote Control bridge or a cloud worker; Typed is a slash command typed
    // into the pane; GatewayPatch is OpenClaw's sessions.patch; None is no way
    // at all, and always comes with a reason.
    public enum RunRoute { None, Control, Typed, GatewayPatch }


    // What the session is actually running, as far as anything on this machine
    // says. Either half may be missing; At is when the source wrote it.
    //
    // Each part also carries its own time (ModelAt, EffortAt), because a merged
    // reading can hold a model from one row and an effort from an older one: a
    // row naming only the model must not make an old effort look fresh to a
    // pending effort change (nadia's review). A part's time defaults to At.
    public sealed record RunReading(string? Model, string? Effort, DateTimeOffset? At, RunReadingSource Source)
    {
        public static readonly RunReading None = new(null, null, null, RunReadingSource.None);

        private readonly DateTimeOffset? _modelAt;
        private readonly DateTimeOffset? _effortAt;
        private readonly bool _modelAtSet;
        private readonly bool _effortAtSet;

        public DateTimeOffset? ModelAt
        {
            get => Model is null ? null : _modelAtSet ? _modelAt : At;
            init { _modelAt = value; _modelAtSet = true; }
        }

        public DateTimeOffset? EffortAt
        {
            get => Effort is null ? null : _effortAtSet ? _effortAt : At;
            init { _effortAt = value; _effortAtSet = true; }
        }

        // Equal by what is read, not by whether a part's time was given
        // explicitly or taken from At.
        public bool Equals(RunReading? other) =>
            other is not null && Model == other.Model && Effort == other.Effort && At == other.At
            && Source == other.Source && ModelAt == other.ModelAt && EffortAt == other.EffortAt;

        public override int GetHashCode() => HashCode.Combine(Model, Effort, At, Source, ModelAt, EffortAt);

        // The time of the part a pending change is about.
        public DateTimeOffset? TimeOf(RunField field) => field == RunField.Model ? ModelAt : EffortAt;

        // The newer of two readings, part by part, each part judged by its own
        // time: a reading that knows only the model must not wipe an effort an
        // older one knew. Undated parts lose to dated ones, and between two
        // undated parts the second wins.
        public static RunReading Merge(RunReading older, RunReading newer)
        {
            var (model, modelAt) = Pick(older.Model, older.ModelAt, newer.Model, newer.ModelAt);
            var (effort, effortAt) = Pick(older.Effort, older.EffortAt, newer.Effort, newer.EffortAt);

            var newerWins = !(older.At is { } a && newer.At is { } b && b < a) && !(newer.At is null && older.At is not null);
            var (first, second) = newerWins ? (older, newer) : (newer, older);

            return new RunReading(model, effort, Max(older.At, newer.At),
                second.Model is null && second.Effort is null ? first.Source : second.Source)
            {
                ModelAt = modelAt,
                EffortAt = effortAt,
            };
        }

        private static (string?, DateTimeOffset?) Pick(string? a, DateTimeOffset? aAt, string? b, DateTimeOffset? bAt)
        {
            if (b is null) return (a, aAt);
            if (a is null) return (b, bAt);
            if (aAt is { } x && bAt is { } y) return y < x ? (a, aAt) : (b, bAt);
            return bAt is null && aAt is not null ? (a, aAt) : (b, bAt);
        }

        private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
            a is null ? b : b is null ? a : a > b ? a : b;
    }

    // One model a session may be moved to. EffortLevels is the model's own
    // ordered list, lowest first; null means the model has no effort setting at
    // all (Haiku 4.5, a Grok model with supports_reasoning_effort false), which
    // disables the dial rather than offering levels it would ignore.
    public sealed record RunModel(string Id, string Label, string? ResolvedId, IReadOnlyList<string>? EffortLevels);

    public sealed record RunCatalogue(IReadOnlyList<RunModel> Models)
    {
        public static readonly RunCatalogue Empty = new(Array.Empty<RunModel>());

        // A reading names the model however its source spells it: an alias the
        // person typed ("opus"), the id the API resolved ("claude-opus-5-5"), or
        // Codex's lower-case id. Any of the three finds the entry.
        public RunModel? Find(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            // By resolved id, Claude Code's "default" entry is passed over for the
            // named one it resolves to: list_models lists both "default" and
            // "opus" as claude-opus-5-5 (measured), and the transcript should
            // read as Opus, not as "Default (recommended)".
            var resolved = Models.Where(m => string.Equals(m.ResolvedId, model, StringComparison.OrdinalIgnoreCase)).ToList();
            return Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
                ?? resolved.FirstOrDefault(m => !string.Equals(m.Id, "default", StringComparison.OrdinalIgnoreCase))
                ?? resolved.FirstOrDefault()
                ?? Models.FirstOrDefault(m => string.Equals(m.Label, model, StringComparison.OrdinalIgnoreCase));
        }
    }

    // A change this app made that no reading has confirmed yet. Success from
    // the far side is not proof (apply_flag_settings answers success to a level
    // it then ignores, measured), so the value is drawn as "applying…" until a
    // newer reading agrees.
    public sealed record RunPending(RunField Field, string Value, DateTimeOffset At);

    // A pending change that a reading contradicted or that timed out, with the
    // control it belongs under.
    public sealed record RunFailure(RunField Field, string Why);

    public sealed record RunRouteDecision(RunRoute Route, string? DisabledReason, bool SetsNewSessionDefault)
    {
        public static RunRouteDecision Unavailable(string reason) => new(RunRoute.None, reason, false);
    }

    public static class RunText
    {
        public const string EffortLabel = "Effort";
        public const string ChipTip = "Model and effort for this session. Click to change them.";
        public const string ModelUnknown = "model not known yet";
        public const string EffortUnknown = "effort not known yet";
        public const string Applying = "applying…";
        public const string NoEffortSetting = "this model has no effort setting";
        public const string NoRoute = "this session has no way to change it from here";
        public const string CloudEffortUnreported = "not reported by a cloud session";
        public const string CodexVersionUnknown = "checking Codex version…";
        public const string ClaudeTypedDefault =
            "Typing this into the terminal also saves it as Claude Code's default for new sessions.";
        public const string CodexPickerDefault =
            "This Codex is older than 0.164, so its picker also saves the choice as the default for new sessions.";

        public static string Contradicted(RunField field, string wanted, string? actual) =>
            (field == RunField.Model ? "The model" : "The effort level")
            + $" didn't change to {wanted}; the session is on {actual}.";

        public static string TimedOut(string wanted) =>
            $"No reading has confirmed {wanted} yet, so it may not have applied.";

        public static string UnknownModel(string value) => $"\"{value}\" is not one of this session's models.";

        public static string UnknownEffort(string value, IReadOnlyList<string> levels) =>
            $"\"{value}\" is not an effort level for this model; use one of: {string.Join(", ", levels)}.";
    }

    public static class RunRoutes
    {
        // Codex 0.164.0 is the first with `/model <id> --reasoning=<level>`
        // (bisected on the npm releases, plan 2.4). Before it the only way in is
        // the picker, which also rewrites ~/.codex/config.toml.
        public static readonly Version CodexTypedArguments = new(0, 164, 0);

        // Prefer control, fall back to typed: the one place that rule lives.
        //
        //   hasPane          a terminal pane exists to type into
        //   bridgeId         the Remote Control bridge session id, if any
        //   bridgeSlashOn    "Run slash commands in background jobs and Desktop
        //                    sessions" is on, which is what lets this app post
        //                    control requests to a bridge at all
        //   codexVersion     the Codex CLI version, null when not known yet
        //   noBridgeReason   the existing sentence for a paneless session with no
        //                    bridge (SlashRouting.PeerSocketReason or
        //                    DesktopNoBridgeReason)
        public static RunRouteDecision For(
            RunCli cli, bool hasPane, string? bridgeId, bool bridgeSlashOn,
            Version? codexVersion = null, string? noBridgeReason = null)
        {
            switch (cli)
            {
                case RunCli.Claude:
                    if (!string.IsNullOrEmpty(bridgeId) && bridgeSlashOn) return new(RunRoute.Control, null, false);
                    // F1: typed works and saves a new-session default; say so.
                    if (hasPane) return new(RunRoute.Typed, null, true);
                    return RunRouteDecision.Unavailable(noBridgeReason ?? SlashRouting.PeerSocketReason);

                case RunCli.ClaudeCloud:
                    return new(RunRoute.Control, null, false);

                case RunCli.Codex:
                    if (!hasPane) return RunRouteDecision.Unavailable(RunText.NoRoute);
                    // Never typed blind: on a Codex before 0.164 the argument form
                    // opens the picker and the rest of the line lands in it. So
                    // until the version is read the controls wait (lead's ruling).
                    if (codexVersion is not { } v) return RunRouteDecision.Unavailable(RunText.CodexVersionUnknown);
                    return new(RunRoute.Typed, null, v < CodexTypedArguments);

                case RunCli.Grok:
                    return hasPane
                        ? new(RunRoute.Typed, null, false)
                        : RunRouteDecision.Unavailable(RunText.NoRoute);

                default:
                    return new(RunRoute.GatewayPatch, null, false);
            }
        }

        public static string? DefaultWarning(RunCli cli, RunRouteDecision route) =>
            !route.SetsNewSessionDefault ? null
            : cli == RunCli.Codex ? RunText.CodexPickerDefault
            : RunText.ClaudeTypedDefault;
    }

    public static class RunPendingRules
    {
        // How long a change may go unconfirmed before it is reported, sized to
        // the CLI's own cadence rather than a round number. Codex writes
        // thread_settings_applied at once (measured within 3 s); OpenClaw reads
        // back on the next sessions.list; Claude and Grok write the effort only
        // on the next turn, so their pending is cleared by a turn, and the
        // timeout is the outer bound for a person who never sends one.
        public static TimeSpan TimeoutFor(RunCli cli) => cli switch
        {
            RunCli.Codex => TimeSpan.FromSeconds(15),
            RunCli.OpenClaw => TimeSpan.FromSeconds(15),
            _ => TimeSpan.FromMinutes(10),
        };

        // Reconciles outstanding sets against a reading. A pending set is
        // cleared when a reading at or after it agrees, reported when a reading
        // after it disagrees, and reported when it outlives the timeout. A
        // reading older than the set says nothing about it either way.
        public static (IReadOnlyList<RunPending> Still, IReadOnlyList<RunFailure> Failures) Reconcile(
            IReadOnlyList<RunPending> pending, RunReading reading, DateTimeOffset now, TimeSpan timeout,
            RunCatalogue? catalogue = null)
        {
            var still = new List<RunPending>();
            var failures = new List<RunFailure>();

            foreach (var p in pending)
            {
                var actual = p.Field == RunField.Model ? reading.Model : reading.Effort;
                // Judged against the time of the part it is about, not the
                // reading's newest row.
                var fresh = reading.TimeOf(p.Field) is { } at && at >= p.At && actual is not null;

                if (fresh && Same(p.Field, p.Value, actual!, catalogue)) continue;
                if (fresh) { failures.Add(new(p.Field, RunText.Contradicted(p.Field, p.Value, actual))); continue; }
                if (now - p.At > timeout) { failures.Add(new(p.Field, RunText.TimedOut(p.Value))); continue; }

                still.Add(p);
            }

            return (still, failures);
        }

        // Adds a set, replacing any earlier one for the same field.
        public static IReadOnlyList<RunPending> With(IReadOnlyList<RunPending> pending, RunPending set) =>
            pending.Where(p => p.Field != set.Field).Append(set).ToList();

        internal static bool Same(RunField field, string wanted, string actual, RunCatalogue? catalogue)
        {
            if (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase)) return true;
            if (field != RunField.Model || catalogue is null) return false;
            // "opus" was asked for and "claude-opus-5-5" is what the transcript
            // says: the same model under its two names.
            var a = catalogue.Find(wanted);
            var b = catalogue.Find(actual);
            return a is not null && b is not null && string.Equals(a.ResolvedId ?? a.Id, b.ResolvedId ?? b.Id, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class RunControlsResolver
    {
        // What the panel draws. One arm per outcome:
        //   - the route is None: both controls disabled with its reason;
        //   - modelLockedReason: the model disabled with it (OpenClaw lock, a
        //     surface chat), effort still follows the model;
        //   - the current model has no effort setting: the dial disabled;
        //   - effortUnavailableReason: the dial disabled with that (a cloud
        //     worker that refuses effort);
        //   - a part not in the reading: "not known yet";
        //   - a pending set: drawn as the chosen value, marked pending.
        public static RemoteRunOptions Resolve(
            RunCli cli, RunRouteDecision route, RunReading reading, RunCatalogue catalogue,
            IReadOnlyList<RunPending> pending,
            string? modelLockedReason = null, string? effortUnavailableReason = null,
            IReadOnlyList<RunFailure>? failures = null)
        {
            var pendingModel = pending.LastOrDefault(p => p.Field == RunField.Model)?.Value;
            var pendingEffort = pending.LastOrDefault(p => p.Field == RunField.Effort)?.Value;

            var modelId = pendingModel ?? reading.Model;
            var current = catalogue.Find(modelId);

            // The model the session runs is always listed, even when the
            // catalogue does not have it (the OpenClaw rule) — and so is the
            // confirmed one while another is pending, so both ids the wire
            // carries are among the choices.
            var models = catalogue.Models.Select(m => new RemoteRunChoice(m.Id, m.Label)).ToList();
            var confirmedModel = reading.Model is null ? null : catalogue.Find(reading.Model)?.Id ?? reading.Model;
            foreach (var extra in new[] { confirmedModel, modelId }.Distinct())
                if (extra is not null && catalogue.Find(extra) is null && models.All(m => m.Id != extra))
                    models.Insert(0, new RemoteRunChoice(extra, extra));
            var shownModelId = current?.Id ?? modelId;

            // Effort ids in the level's own spelling: the wire and the phone
            // match ids exactly, so a reading of "High" must come out as "high".
            // A value outside the levels goes at the end, since the choice order
            // is the dial order on every platform.
            var levels = Levels(cli, current, catalogue, modelId);
            var effortChoices = (levels ?? Array.Empty<string>()).Select(l => new RemoteRunChoice(l, l)).ToList();
            string? Spell(string? e) => e is null ? null
                : effortChoices.FirstOrDefault(c => string.Equals(c.Id, e, StringComparison.OrdinalIgnoreCase))?.Id ?? e;
            var effortId = Spell(pendingEffort ?? reading.Effort);
            var confirmedEffort = Spell(reading.Effort);
            if (levels is not null)
                foreach (var extra in new[] { confirmedEffort, effortId }.Distinct())
                    if (extra is not null && effortChoices.All(c => c.Id != extra))
                        effortChoices.Add(new RemoteRunChoice(extra, extra));

            var routeReason = route.Route == RunRoute.None ? route.DisabledReason ?? RunText.NoRoute : null;
            var modelOff = routeReason ?? modelLockedReason;
            var effortOff = routeReason
                ?? effortUnavailableReason
                ?? (current is not null && current.EffortLevels is null ? RunText.NoEffortSetting : null);

            var modelLabel = models.FirstOrDefault(m => m.Id == shownModelId)?.Label ?? RunText.ModelUnknown;
            var effortLabel = effortId
                ?? (cli == RunCli.ClaudeCloud ? RunText.CloudEffortUnreported : RunText.EffortUnknown);
            var chip = effortOff == RunText.NoEffortSetting ? modelLabel : $"{modelLabel} · {effortLabel}";

            return new RemoteRunOptions(
                shownModelId,
                models,
                effortId,
                null,
                effortChoices,
                Fast: false,
                FastIsSet: false,
                Locked: modelLockedReason is not null,
                CanResetToDefault: false,
                ChipText: chip)
            {
                ModelDisabledReason = modelOff,
                EffortDisabledReason = effortOff,
                ModelPending = pendingModel is not null,
                EffortPending = pendingEffort is not null,
                Source = reading.Source,
                DefaultWarning = RunRoutes.DefaultWarning(cli, route),
                SavesDefault = route.SetsNewSessionDefault,
                HasFast = false,
                HasTimeLimit = false,
                ConfirmedModelId = confirmedModel,
                ConfirmedEffortId = confirmedEffort,
                ModelFailure = failures?.LastOrDefault(f => f.Field == RunField.Model)?.Why,
                EffortFailure = failures?.LastOrDefault(f => f.Field == RunField.Effort)?.Why,
            };
        }

        // The levels the dial offers. The model's own list; with no model known
        // yet, the union of the catalogue's lists in their own order, so the dial
        // still has something honest to show. A cloud session drops "max": the
        // CLI says it "can't be set on a remote session" (read).
        internal static IReadOnlyList<string>? Levels(RunCli cli, RunModel? current, RunCatalogue catalogue, string? modelId)
        {
            IReadOnlyList<string>? levels;
            if (current is not null) levels = current.EffortLevels;
            else
            {
                var union = new List<string>();
                foreach (var m in catalogue.Models)
                foreach (var l in m.EffortLevels ?? Array.Empty<string>())
                    if (!union.Contains(l, StringComparer.OrdinalIgnoreCase)) union.Add(l);
                levels = union;
            }

            if (levels is not null && cli == RunCli.ClaudeCloud)
                levels = levels.Where(l => !string.Equals(l, "max", StringComparison.OrdinalIgnoreCase)).ToList();

            return levels;
        }

        // Checked before anything is sent: a bad effort level is accepted
        // silently by apply_flag_settings (measured), so the app refuses it here
        // rather than trusting the far side. Null when the value may go.
        public static string? Validate(RunCli cli, RunField field, string value, RunReading reading, RunCatalogue catalogue)
        {
            if (string.IsNullOrWhiteSpace(value)) return field == RunField.Model ? "name a model" : "name an effort level";

            if (field == RunField.Model)
            {
                // An empty catalogue (the probe failed) leaves the CLI to refuse
                // an unknown name, which both Claude and Codex do in words.
                if (catalogue.Models.Count == 0 || catalogue.Find(value) is not null) return null;
                return RunText.UnknownModel(value);
            }

            var current = catalogue.Find(reading.Model);
            if (current is not null && current.EffortLevels is null) return RunText.NoEffortSetting;
            var levels = Levels(cli, current, catalogue, reading.Model);
            if (levels is null || levels.Count == 0) return null;
            return levels.Contains(value, StringComparer.OrdinalIgnoreCase) ? null : RunText.UnknownEffort(value, levels);
        }
    }
}

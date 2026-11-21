using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    // CB-404: the model dropdown and effort dial for a session this machine
    // hosts, with no window behind it.
    //
    // Three callers and one answer: the chat panel on this desk (through
    // LocalCliChatSession), a phone over the relay, and a linked desktop over
    // the mirror. The last two never reach a chat-session object — the relay
    // works from the scan snapshot and the mirror server from its own roster —
    // which is why this exists apart from any panel, in MirrorSlashHost's
    // pattern: every decision from RunControls' pure core, everything that
    // touches the machine (the pane, the bridge, the files) through Seams.
    //
    // Pending sets are held here, per session id, so a change made from the
    // phone shows "applying…" on the desk's own panel too, and the other way
    // round: there is one set in flight per session, not one per caller.
    internal sealed class RunControlsHost : IRunControls
    {
        internal sealed record Seams(
            // A pane to type into (TerminalFocuser.CanSendQuietly).
            Func<SessionStatus, bool> CanType,

            // The session's Remote Control bridge id, if it has one.
            Func<SessionStatus, string?> BridgeIdFor,

            // "Run slash commands in background jobs and Desktop sessions":
            // whether this app may post control requests to a bridge at all.
            Func<bool> BridgeCommandsEnabled,

            // What the session is running, from the files this app already
            // tails (RunReadings over transcript, rollout, summary or pane).
            Func<SessionStatus, RunReading?> ReadingFor,

            // What it could run, from the per-CLI catalogue caches.
            Func<SessionStatus, RunCatalogue?> CatalogueFor,

            // Codex's own version, for the typed form's cut-off; null when not known.
            Func<SessionStatus, Version?> CodexVersionFor,

            Func<SessionStatus, string, Task<bool>> TypeInto,
            Func<SessionStatus, string, JsonObject, Task<MirrorSlashHost.ControlVerdict>> PostControl,
            Func<DateTimeOffset> Now,

            // The typing gate's reads, as on MirrorSlashHost; null on both is ungated.
            Func<SessionStatus, string?>? RecordStatus = null,
            Func<SessionStatus, Task<string?>>? CapturePane = null,
            Func<SessionStatus, bool>? CanReadPane = null,
            Func<bool>? Shadow = null,
            Action<string, string>? Log = null,

            // A Codex older than 0.164 has no argument form: its full-screen
            // picker is driven by digits (plan 2.4). The route belongs to the
            // set routes; null answers unavailable.
            Func<SessionStatus, RunField, string, Task<RunSetResult>>? CodexPicker = null);

        private readonly Seams _seams;
        private readonly object _gate = new();
        private readonly Dictionary<string, IReadOnlyList<RunPending>> _pending = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Session, RunField Field), string> _failures = new();
        private readonly HashSet<string> _running = new(StringComparer.Ordinal);

        internal RunControlsHost(Seams seams) => _seams = seams;

        // Raised (on whatever thread made the change) when a session's resolved
        // options may have moved because of something this host did: a set
        // went out, or a pending one was confirmed or reported. A reading that
        // changes on disk is the caller's own poll to notice.
        internal event Action<string>? Changed;

        internal static RunCli? CliFor(SessionStatus status) => status.Source switch
        {
            SessionSource.ClaudeCode => RunCli.Claude,
            SessionSource.Codex => RunCli.Codex,
            SessionSource.Grok => RunCli.Grok,
            _ => null,
        };

        private static string Key(SessionStatus status) => status.SessionId;

        internal const string CheckingCodexVersion = "checking Codex version…";

        // Until a Codex session's version is known, neither form can be chosen
        // safely: "/model x" opens the old picker and a digit means nothing to
        // the new one. So the controls are off and nothing is typed (lead's
        // ruling on F4).
        internal RunRouteDecision RouteFor(SessionStatus status, RunCli cli) =>
            cli == RunCli.Codex && _seams.CodexVersionFor(status) is null
                ? RunRouteDecision.Unavailable(CheckingCodexVersion)
                : RunRoutes.For(cli,
                hasPane: !status.IsDesktopHosted && _seams.CanType(status),
                bridgeId: _seams.BridgeIdFor(status),
                bridgeSlashOn: _seams.BridgeCommandsEnabled(),
                codexVersion: cli == RunCli.Codex ? _seams.CodexVersionFor(status) : null,
                noBridgeReason: status.IsDesktopHosted ? SlashRouting.DesktopNoBridgeReason : SlashRouting.PeerSocketReason);

        // What the panel draws for this session, or null for a session this
        // host does not serve (anything but a local Claude, Codex or Grok).
        internal RemoteRunOptions? Options(SessionStatus status)
        {
            if (CliFor(status) is not { } cli) return null;
            var reading = _seams.ReadingFor(status) ?? RunReading.None;
            var catalogue = _seams.CatalogueFor(status) ?? RunCatalogue.Empty;
            var pending = Reconcile(status, cli, reading, catalogue);
            return RunControlsResolver.Resolve(cli, RouteFor(status, cli), reading, catalogue, pending);
        }

        // The sentence for a field's last set that a reading contradicted or that
        // timed out. It stays until the next set of that field goes out, which is
        // how frames-v13's `failed` reads: present while the desk is reporting it.
        internal string? FailureFor(SessionStatus status, RunField field)
        {
            lock (_gate) return _failures.GetValueOrDefault((Key(status), field));
        }

        private IReadOnlyList<RunPending> Reconcile(SessionStatus status, RunCli cli, RunReading reading, RunCatalogue catalogue)
        {
            IReadOnlyList<RunPending> still;
            bool moved;
            lock (_gate)
            {
                if (!_pending.TryGetValue(Key(status), out var pending) || pending.Count == 0) return [];
                // One field at a time, so each failure sentence is filed under
                // the field whose set it reports.
                var kept = new List<RunPending>();
                foreach (var p in pending)
                {
                    var (still1, failures) = RunPendingRules.Reconcile([p], reading, _seams.Now(),
                        RunPendingRules.TimeoutFor(cli), catalogue);
                    kept.AddRange(still1);
                    if (failures.Count > 0) _failures[(Key(status), p.Field)] = failures[0];
                }
                still = kept;
                moved = kept.Count != pending.Count;
                if (kept.Count == 0) _pending.Remove(Key(status));
                else _pending[Key(status)] = kept;
            }
            if (moved) Changed?.Invoke(Key(status));
            return still;
        }

        public RunState? Read(SessionStatus status) =>
            Options(status) is { } o
                ? Wire(o, FailureFor(status, RunField.Model), FailureFor(status, RunField.Effort))
                : null;

        // The wire's view of the panel's record (frames-v13 run, the mirror's
        // `run` roster field): the confirmed value as id, the chosen one as
        // pending while it is unconfirmed.
        internal static RunState Wire(RemoteRunOptions o, string? modelFailed = null, string? effortFailed = null) => new(
            new RunFieldState(o.ConfirmedModelId, o.ModelPending ? o.ModelId : null, o.Models, o.ModelDisabledReason, modelFailed),
            new RunFieldState(o.ConfirmedEffortId, o.EffortPending ? o.ThinkingId : null, o.ThinkingChoices,
                o.EffortDisabledReason, effortFailed),
            o.DefaultWarning is not null);

        public Task<RunSetResult> SetAsync(SessionStatus status, string field, string value) =>
            SetAsync(status, field == RunFrames.FieldEffort ? RunField.Effort : RunField.Model, value);

        public bool IsRunning(SessionStatus status)
        {
            lock (_gate) return _running.Contains(Key(status));
        }

        // Runs one change by the session's route. Sent means it went and is now
        // pending until a reading confirms it — never that it took. One change
        // per session at a time: two phones, or a phone and the panel, would
        // otherwise interleave two typed commands in one pane.
        internal async Task<RunSetResult> SetAsync(SessionStatus status, RunField field, string value)
        {
            if (CliFor(status) is not { } cli) return new(RunSetKind.Unavailable, RunText.NoRoute);
            lock (_gate)
                if (!_running.Add(Key(status))) return new(RunSetKind.Unavailable, RunFrames.BusyWhy);
            try { return await RunAsync(status, cli, field, value).ConfigureAwait(false); }
            finally
            {
                lock (_gate) _running.Remove(Key(status));
            }
        }

        private async Task<RunSetResult> RunAsync(SessionStatus status, RunCli cli, RunField field, string value)
        {

            var route = RouteFor(status, cli);
            if (route.Route == RunRoute.None) return new(RunSetKind.Unavailable, route.DisabledReason ?? RunText.NoRoute);

            var reading = _seams.ReadingFor(status) ?? RunReading.None;
            var catalogue = _seams.CatalogueFor(status) ?? RunCatalogue.Empty;
            if (RunControlsResolver.Validate(cli, field, value, reading, catalogue) is { } problem)
                return new(RunSetKind.BadValue, problem);

            var result = route.Route == RunRoute.Control
                ? await ControlAsync(status, field, value).ConfigureAwait(false)
                : await TypedAsync(status, cli, route, field, value).ConfigureAwait(false);

            if (result.Kind == RunSetKind.Sent)
            {
                lock (_gate)
                {
                    _pending[Key(status)] = RunPendingRules.With(
                        _pending.GetValueOrDefault(Key(status)) ?? [], new RunPending(field, value, _seams.Now()));
                    _failures.Remove((Key(status), field));
                }
                Changed?.Invoke(Key(status));
            }
            return result;
        }

        internal static JsonObject? ControlFor(RunField field, string value, out string? problem) =>
            field == RunField.Model
                ? SlashRouting.ControlRequest("set_model", value, out problem)
                : SlashRouting.ControlRequest("apply_flag_settings", value, out problem);

        private async Task<RunSetResult> ControlAsync(SessionStatus status, RunField field, string value)
        {
            if (ControlFor(field, value, out var problem) is not { } request) return new(RunSetKind.BadValue, problem);
            var subtype = request["subtype"]!.GetValue<string>();
            var verdict = await _seams.PostControl(status, subtype, request).ConfigureAwait(false);
            return verdict.Refusal is { } refusal ? new(RunSetKind.Failed, refusal) : RunSetResult.Sent;
        }

        // The line each CLI takes (plan 2.1, 2.4, 2.5). Codex sets effort as an
        // argument of /model, keeping the model it is on.
        internal static string TypedLine(RunCli cli, RunField field, string value) => (cli, field) switch
        {
            (RunCli.Codex, RunField.Model) => "/model " + value,
            (RunCli.Codex, RunField.Effort) => "/model --reasoning=" + value,
            (_, RunField.Model) => "/model " + value,
            _ => "/effort " + value,
        };

        internal const string NoPickerReason =
            "this Codex is older than 0.164, and its /model picker can't be driven from here";

        private async Task<RunSetResult> TypedAsync(SessionStatus status, RunCli cli, RunRouteDecision route, RunField field, string value)
        {
            // An old Codex has no argument form: a typed "/model x" would open
            // its full-screen picker, so the picker is driven instead.
            if (cli == RunCli.Codex && route.SetsNewSessionDefault)
                return _seams.CodexPicker is { } picker
                    ? await picker(status, field, value).ConfigureAwait(false)
                    : new(RunSetKind.Unavailable, NoPickerReason);

            var (typed, notAtPrompt, _) = await TypingGate.GuardedTypeAsync(status, TypedLine(cli, field, value),
                _seams.TypeInto, _seams.RecordStatus, _seams.CapturePane, _seams.CanReadPane, _seams.Shadow,
                _seams.Log ?? ((what, detail) => MirrorLog.Say(what, detail)), "run-host").ConfigureAwait(false);
            if (notAtPrompt is not null) return new(RunSetKind.Failed, TypingGate.NotAtPromptReason(notAtPrompt));
            return typed ? RunSetResult.Sent : new(RunSetKind.Failed, "the terminal refused the text");
        }
    }

    // The one host this app runs, shared by the chat panel, the phone relay and
    // the mirror server, so a set from any of them is pending for all three.
    //
    // Excluded from coverage: every seam is one line into something that types
    // into a real pane, posts to a real bridge or scans a real registry —
    // RunControlsHost itself is driven with fakes.
    [ExcludeFromCodeCoverage]
    internal static class RunControlsLive
    {
        // What the session is running and could run. Pointed at the core's
        // readers (RunControls' parsers over the tailed files and the catalogue
        // caches); until then nothing is read, and the controls say "not known
        // yet" rather than guessing. Both must answer from what is already
        // cached: the mirror asks on its peer pump.
        internal static Func<SessionStatus, RunReading?> ReadingFor = _ => null;
        internal static Func<SessionStatus, RunCatalogue?> CatalogueFor = _ => null;
        internal static Func<SessionStatus, Version?> CodexVersionFor = _ => null;

        private static readonly Lazy<RunControlsHost> _shared = new(Build);

        internal static RunControlsHost Shared => _shared.Value;

        private static string? BridgeIdFor(SessionStatus status) => LocalSlashChannel.BridgeIdFor(
            status.IsDesktopHosted ? status.BridgeSessionId : null,
            status.IsDesktopHosted ? null : RemoteMirrorServer.RegistryEntryFor(status)?.BridgeSessionId);

        private static RunControlsHost Build()
        {
            var cloudApi = new HttpCloudApi();
            return new RunControlsHost(new RunControlsHost.Seams(
                TerminalFocuser.CanSendQuietly,
                BridgeIdFor,
                () => OrbweaverSettings.BridgeCommandsEnabled,
                status => ReadingFor(status),
                status => CatalogueFor(status),
                status => CodexVersionFor(status),
                (status, text) => TerminalFocuser.SendTextAndSubmit(status, text),
                MirrorSlashHost.GatedPost(
                    () => OrbweaverSettings.BridgeCommandsEnabled,
                    BridgeIdFor,
                    status => RemoteMirrorServer.RegistryEntryFor(status)?.Version,
                    (status, bridgeId, request) => CloudTurnSender.PostControlAndReadAnswerAsync(
                        cloudApi,
                        CloudAccounts.SourceFor(ClaudeConfigRoots.RootOfTranscript(status.TranscriptPath)),
                        CredentialBudget.Unmeasured, bridgeId, request, Task.Delay,
                        CancellationToken.None)),
                () => DateTimeOffset.UtcNow,
                RecordStatus: status => status.SessionPid > 0 && status.SessionId.Length > 0
                    ? SessionPark.RecordStatus(status.SessionPid, status.SessionId) : null,
                CapturePane: TerminalFocuser.CapturePane,
                CanReadPane: TerminalFocuser.CanReadPane,
                Log: (what, detail) => MirrorLog.Say(what, detail)));
        }
    }
}

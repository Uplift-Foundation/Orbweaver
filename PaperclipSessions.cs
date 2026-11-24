using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Binding types for the Paperclip arm (CB-421). Names and shapes are fixed
    // by docs/plans/paperclip-orbs-plan.md section 2.1 so the packages that
    // build on them (relay, orb drawing, wiring) compile in parallel; the
    // bodies are package A's.
    public enum PaperclipMark { None, Broke, Paused, OverBudget }

    internal enum PaperclipLink { Off, Connecting, Connected, NeedsReconnect, Unreachable }

    // One thing that needs a person. Kind is "approval", "action", "runtime" or "blocked".
    internal sealed record PaperclipNeed(
        string Kind,
        string Id,
        string Title,
        string Url,
        // What a person needs to tell two of one kind apart, and when it was raised
        // (and, for a tool action, when Paperclip lets it lapse). All optional.
        string? Summary = null,
        DateTime? CreatedAt = null,
        DateTime? ExpiresAt = null,
        // CB-428: what a phone's prompt calls it ("Hire", "Budget override", "Strategy", "Approval",
        // "Tool action"). Null on a need no prompt is minted for.
        string? Tool = null,
        // CB-442: a runtime permission request is answered on its run, with the turn it was raised in.
        string? RunId = null,
        string? TurnId = null);

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
        int? RunPid = null, string? RunSessionId = null,
        long BudgetCapCents = 0, long SpentCents = 0,
        DateTime? LastRunFinished = null);   // the newest finished run's own finishedAt, for "last run … ago"   // the hover's "$0.26 of $50.00"

    // The poll loop (plan 2.5), shaped after ClaudeCloudSessions: a volatile
    // snapshot replaced whole, a pure StepAsync that takes its transport and
    // clock as arguments so every branch is a unit test, and a loop around it
    // that is only a `while` and a delay.
    internal static class PaperclipSessions
    {
        private static readonly object Gate = new();
        private static volatile IReadOnlyList<PaperclipAgent> _snapshot = Array.Empty<PaperclipAgent>();
        private static volatile PaperclipLink _link = PaperclipLink.Off;
        private static volatile string _statusText = "Paperclip is switched off.";
        private static CancellationTokenSource? _cts;

        // Every agent, unfolded; empty unless enabled.
        public static IReadOnlyList<PaperclipAgent> Snapshot() => _snapshot;

        internal static PaperclipLink Link => _link;

        internal static string StatusText => _statusText;

        // Where the loop reads its configuration. Defaults to "off"; the
        // settings wiring replaces it, so this file never reads settings.
        internal static Func<PaperclipConfig> ConfigSource { get; set; } = () => PaperclipConfig.Off;

        // Raised (from the poll loop's thread) when a publish changes anything
        // the orbs draw, so the scan can run now instead of at its next tick.
        // Never raised for an unchanged snapshot, so a healthy idle company adds
        // no scans.
        internal static event Action? Changed;

        // True while what is published is the remembered picture of the company, not
        // a live read: the scan draws every such row parked, with the link's reason.
        private static volatile bool _remembered;
        internal static bool Remembered => _remembered;

        // Where the roster is kept between launches. The app wires a file beside the
        // settings; nothing here reads settings (the probe compiles this file).
        internal static IRosterStore RosterStore { get; set; } = new NullRosterStore();

        private static string _lastRosterSignature = "";

        private static string _lastDraw = "";

        // Everything the orbs draw, and nothing that merely moves with the clock
        // (the hover's "running for 3 m" is recomputed on every scan anyway).
        internal static string DrawSignature(IReadOnlyList<PaperclipAgent> agents, PaperclipLink link, string status)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(link).Append('\u001f').Append(status);
            foreach (var a in agents)
            {
                sb.Append('\n').Append(a.Id).Append('|').Append(a.Name).Append('|').Append(a.ReportsTo)
                  .Append('|').Append(a.State).Append('|').Append(a.Mark).Append('|').Append(a.MarkReason)
                  .Append('|').Append(a.BudgetPercent).Append('|').Append(a.SpentCents).Append('|').Append(a.Sleeping)
                  .Append('|').Append(a.Task).Append('|').Append(a.Activity).Append('|').Append(a.LastResult)
                  .Append('|').Append(a.Url);
                foreach (var n in a.Needs) sb.Append('|').Append(n.Kind).Append(':').Append(n.Id).Append(':').Append(n.Title).Append(':').Append(n.Url);
            }
            return sb.ToString();
        }

        // The one way the loop publishes. Sets what Snapshot, Link and StatusText
        // read, and raises Changed only when what is drawn is different.
        internal static void Publish(IReadOnlyList<PaperclipAgent> agents, PaperclipLink link, string status,
            bool remembered = false)
        {
            var signature = DrawSignature(agents, link, status) + (remembered ? "\nremembered" : "");
            _remembered = remembered;
            bool changed;
            // The snapshot and the mark that says when it was published go in together,
            // so a scan that reads one reads the other (CB-425).
            lock (PubGate)
            {
                _snapshot = agents;
                changed = signature != _lastDraw;
                if (changed)
                {
                    _lastDraw = signature;
                    MarkChanged(DateTime.UtcNow);
                }
            }
            _link = link;
            _statusText = status;
            if (changed) Changed?.Invoke();
        }

        // What the loop publishes after a step. A live snapshot is published (and the
        // roster remembered if it changed). Before the first successful read, the
        // remembered rows stand in, parked with the link's own words, so a refused
        // key or an unreachable host at the first poll after a launch dims the company
        // instead of drawing nothing. Once a live snapshot exists the remembered ones
        // are never used again.
        internal static void PublishStep(PollState st, PaperclipConfig cfg,
            IReadOnlyList<PaperclipAgent> remembered, IRosterStore store, DateTime now)
        {
            if (st.Published is { } live)
            {
                Publish(live, st.Link, st.Status);
                RememberRoster(live, st, cfg, store, now);
            }
            else if (remembered.Count > 0)
                Publish(remembered, st.Link, st.Status, remembered: true);
            else
                Publish(Array.Empty<PaperclipAgent>(), st.Link, st.Status);
        }

        // Written only when the roster changed: a healthy company costs no writes.
        private static void RememberRoster(IReadOnlyList<PaperclipAgent> live, PollState st, PaperclipConfig cfg,
            IRosterStore store, DateTime now)
        {
            if (string.IsNullOrEmpty(cfg.CompanyId)) return;
            var file = PaperclipRoster.FromLive(cfg.BaseUrl, cfg.CompanyId, st.IssuePrefix, live, now);
            var signature = PaperclipRoster.Signature(file);
            if (signature == _lastRosterSignature) return;
            _lastRosterSignature = signature;
            store.Save(file);
        }

        // The picture to draw before the loop's first read, and the signature of what
        // is on disk so an identical live roster is not rewritten.
        internal static IReadOnlyList<PaperclipAgent> LoadRemembered(PaperclipConfig cfg, IRosterStore store)
        {
            var rows = PaperclipRoster.Remembered(store, cfg.BaseUrl, cfg.CompanyId, out var file);
            _lastRosterSignature = file is null ? "" : PaperclipRoster.Signature(file);
            return rows;
        }

        // Removed on Disconnect and when the company or address changes.
        internal static void ForgetRoster()
        {
            _lastRosterSignature = "";
            try { RosterStore.Delete(); } catch { }
        }

        // The seam every downstream test uses, as ClaudeCloudSessions has.
        internal static void SetSnapshotForTests(IReadOnlyList<PaperclipAgent> agents)
        {
            lock (PubGate) _snapshot = agents;
        }

        internal static void SetLinkForTests(PaperclipLink link, string statusText)
        {
            _link = link;
            _statusText = statusText;
        }

        // ---- how long a change takes to reach the orbs (CB-425) --------------------

        // When the poll published the earliest change no orb update has drawn yet.
        // Published together with the snapshot it belongs to (under PubGate), and
        // taken together with it: a scan that took the mark at the start of its pass
        // and read the snapshot later lost the line whenever a poll published in
        // between (the new state was drawn with no mark to report).
        private static readonly object PubGate = new();
        private static long _changedTicks;

        internal static void MarkChanged(DateTime nowUtc) =>
            Interlocked.CompareExchange(ref _changedTicks, nowUtc.Ticks, 0);

        internal static DateTime? TakeChangeSeen()
        {
            lock (PubGate)
            {
                var t = Interlocked.Exchange(ref _changedTicks, 0);
                return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
            }
        }

        // The snapshot to draw and the earliest change published since the last
        // draw, read as one: the mark is never older or newer than its snapshot.
        internal static (IReadOnlyList<PaperclipAgent> Agents, DateTime? Seen) TakeSnapshotAndSeen()
        {
            lock (PubGate)
            {
                var t = Interlocked.Exchange(ref _changedTicks, 0);
                return (_snapshot, t == 0 ? null : new DateTime(t, DateTimeKind.Utc));
            }
        }

        // ---- answering in place (CB-427) -------------------------------------

        // Wakes the loop now and makes its next step re-read everything, so an
        // answer given in the flyout is seen at once rather than at the next
        // tick. Safe from any thread; a nudge with no loop running is dropped.
        private static readonly SemaphoreSlim NudgeSignal = new(0, 1);
        private static int _nudgePending;

        internal static void PollNow()
        {
            Interlocked.Exchange(ref _nudgePending, 1);
            try { NudgeSignal.Release(); } catch (SemaphoreFullException) { }
        }

        // True once per PollNow, however many were asked for.
        internal static bool TakeNudge() => Interlocked.Exchange(ref _nudgePending, 0) == 1;

        // What a nudge does to the loop's memory: both cadences come due, and a
        // backoff is not waited out. The last answers are kept so a failed read
        // still has something to publish.
        internal static PollState Nudged(PollState st) =>
            st with { LastFast = null, LastSlow = null, NextAttempt = null };

        // The delay between ticks, cut short by PollNow. Returns true when it was
        // cut short. A wake with no nudge behind it (the signal can outlive the
        // nudge that raised it) is ignored and the rest of the wait is served.
        internal static async Task<bool> WaitAsync(TimeSpan wait, CancellationToken ct)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (TakeNudge()) { DrainSignal(); return true; }
                var left = wait - clock.Elapsed;
                if (left <= TimeSpan.Zero) return false;
                if (!await NudgeSignal.WaitAsync(left, ct).ConfigureAwait(false)) return false;
            }
        }

        private static void DrainSignal() { NudgeSignal.Wait(0); }

        // The actions the flyout and the menu make, bound to the live key and
        // company. Rebuilt only when either moves, as the poll's own client is;
        // null while Paperclip is switched off or has no company.
        // An approve runs a tool inside the call (3.4 s measured), so an answer gets
        // far more than the poll's 5 s: a timeout there would say "can't reach"
        // after Paperclip had in fact approved.
        internal static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(20);

        private static readonly object ActionsGate = new();
        private static PaperclipActions? _actions;
        private static PaperclipHttp? _actionsHttp;
        private static string _actionsKey = "", _actionsUrl = "", _actionsCompany = "";

        private static volatile IPaperclipActions? _actionsOverride;

        // The seam the flyout, the menu and the phone's answers call. Settable, like
        // RosterStore, so a suite can hand it a fake; otherwise the live one.
        internal static IPaperclipActions? Actions
        {
            get => _actionsOverride ?? LiveActions();
            set => _actionsOverride = value;
        }

        internal static IPaperclipActions? LiveActions()
        {
            var cfg = ConfigSource();
            if (!cfg.Enabled || string.IsNullOrEmpty(cfg.CompanyId)) return null;
            lock (ActionsGate)
            {
                if (_actions == null || cfg.Key != _actionsKey || cfg.BaseUrl != _actionsUrl || cfg.CompanyId != _actionsCompany)
                {
                    _actionsHttp?.Dispose();
                    _actionsHttp = new PaperclipHttp(cfg.BaseUrl, cfg.Key, timeout: ActionTimeout, log: PaperclipHttp.DefaultLog, logSuccess: true);
                    _actions = new PaperclipActions(_actionsHttp, cfg.CompanyId);
                    _actionsKey = cfg.Key; _actionsUrl = cfg.BaseUrl; _actionsCompany = cfg.CompanyId;
                }
                return _actions;
            }
        }

        // The last Wake pressed on this desktop, per agent. A Wake that fails at
        // once is an event the person caused, so it must not also raise a "broke"
        // alarm on a phone (plan 13.2); BrokeTransitions reads these times.
        private static readonly object WakeGate = new();
        private static readonly Dictionary<string, DateTime> _wakes = new();

        internal static void NoteWake(string agentId, DateTime now)
        {
            lock (WakeGate) _wakes[agentId] = now;
        }

        // Whether a Wake was pressed on this desktop for this agent within the last
        // 30 s: a Broke that follows it is the person's own doing, not news.
        internal static readonly TimeSpan OwnWakeWindow = TimeSpan.FromSeconds(30);

        internal static bool WokeRecently(string agentId, DateTimeOffset now)
        {
            lock (WakeGate)
                return _wakes.TryGetValue(agentId, out var at) && now.UtcDateTime - at >= TimeSpan.Zero
                    && now.UtcDateTime - at <= OwnWakeWindow;
        }

        internal static IReadOnlyDictionary<string, DateTime> Wakes()
        {
            lock (WakeGate) return new Dictionary<string, DateTime>(_wakes);
        }

        // ---- cadences and wording -------------------------------------------

        internal static readonly TimeSpan FastEvery = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan SlowEvery = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(3);
        internal static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
        };

        internal sealed record PaperclipConfig(
            bool Enabled, string BaseUrl, string CompanyId, string Key, DateTime? KeyExpires, string Sleeping)
        {
            internal static readonly PaperclipConfig Off = new(false, "", "", "", null, "fold");
        }

        // Everything the loop remembers between ticks.
        internal sealed record PollState(
            string CompanyId = "",
            string IssuePrefix = "",
            DateTime? LastFast = null,
            DateTime? LastSlow = null,
            IReadOnlyList<PaperclipApi.RawAgent>? Agents = null,
            IReadOnlyList<PaperclipApi.RawLiveRun>? LiveRuns = null,
            IReadOnlyList<PaperclipApi.RawRun>? Runs = null,
            IReadOnlyList<PaperclipApi.RawApproval>? Approvals = null,
            IReadOnlyList<PaperclipApi.RawActionRequest>? Actions = null,
            IReadOnlyList<PaperclipApi.RawIssue>? Blocked = null,
            IReadOnlyDictionary<string, DateTime?>? Heartbeats = null,
            IReadOnlyDictionary<string, string>? TaskTitles = null,
            IReadOnlyDictionary<string, DateTime>? AwakeStamps = null,
            IReadOnlyList<PaperclipAgent>? Published = null,
            int Failures = 0,
            DateTime? FailingSince = null,
            DateTime? NextAttempt = null,
            PaperclipLink Link = PaperclipLink.Connecting,
            string Status = "Connecting to Paperclip…",
            bool NeedRuns = true,
            IReadOnlyList<string>? Degraded = null,
            // CB-442: per running run, what its events said last time and whether that reading is already a tick old.
            IReadOnlyDictionary<string, (IReadOnlyList<PaperclipApi.RawRuntimeRequest> Requests, bool Stale)>? RuntimeByRun = null)
        {
            internal static readonly PollState Initial = new();
        }

        internal sealed record StepResult(PollState Next, TimeSpan Wait);

        private static string CompanyPath(PollState st, string tail) => $"/api/companies/{st.CompanyId}/{tail}";

        // One tick. Calls only what this tick is due for (2 s: agents and
        // live-runs; 4 s: approvals, tool action requests, blocked issues; runs
        // when something moved), composes, and reports. A 401/403 on anything
        // means reconnect and keeps the snapshot; anything else is a failure
        // that backs off and, past the grace, says unreachable. Never throws
        // for a transport failure.
        internal static async Task<StepResult> StepAsync(
            IPaperclipApi api, PaperclipConfig cfg, PollState st, DateTime now, CancellationToken ct)
        {
            if (!cfg.Enabled)
                return new StepResult(PollState.Initial with { Link = PaperclipLink.Off, Status = "Paperclip is switched off." }, FastEvery);

            if (st.NextAttempt is { } next && now < next)
                return new StepResult(st, next - now);

            if (st.CompanyId != cfg.CompanyId)
                st = PollState.Initial with { CompanyId = cfg.CompanyId, Link = PaperclipLink.Connecting };

            if (string.IsNullOrEmpty(cfg.CompanyId))
                return new StepResult(st with { Link = PaperclipLink.Connecting, Status = "Choose a company in Settings." }, SlowEvery);

            var fast = st.LastFast == null || now - st.LastFast >= FastEvery;
            var slow = st.LastSlow == null || now - st.LastSlow >= SlowEvery;

            var agents = st.Agents; var live = st.LiveRuns; var runs = st.Runs;
            var approvals = st.Approvals; var actions = st.Actions; var blocked = st.Blocked;
            var heartbeats = st.Heartbeats; var tasks = st.TaskTitles ?? new Dictionary<string, string>();
            var needRuns = st.NeedRuns;
            PaperclipHttpKind? firstFailure = null; string? failureDetail = null;

            void Note(PaperclipResponse r)
            {
                if (firstFailure == null || r.Kind == PaperclipHttpKind.Unauthorized)
                {
                    firstFailure = r.Kind; failureDetail = r.Detail;
                }
            }

            // The issue prefix is what every page URL is made of, and it is on the
            // company row, so it is read once per company, not per tick.
            var prefix = st.IssuePrefix;
            if (prefix.Length == 0)
            {
                var co = await api.GetAsync("/api/companies", ct).ConfigureAwait(false);
                if (co.IsOk)
                    prefix = PaperclipApi.ParseCompanies(co.Body).FirstOrDefault(c => c.Id == cfg.CompanyId)?.IssuePrefix ?? "";
                else Note(co);
            }
            st = st with { IssuePrefix = prefix };

            if (fast)
            {
                var a = await api.GetAsync(CompanyPath(st, "agents"), ct).ConfigureAwait(false);
                if (a.IsOk)
                {
                    agents = PaperclipApi.ParseAgents(a.Body);
                    var hb = agents.ToDictionary(x => x.Id, x => x.LastHeartbeatAt);
                    if (heartbeats == null || hb.Any(k => !heartbeats.TryGetValue(k.Key, out var v) || v != k.Value))
                        needRuns = true;
                    heartbeats = hb;
                }
                else Note(a);

                var l = await api.GetAsync(CompanyPath(st, "live-runs"), ct).ConfigureAwait(false);
                if (l.IsOk)
                {
                    live = PaperclipApi.ParseLiveRuns(l.Body);
                    if (live.Count > 0) needRuns = true;
                }
                else Note(l);
            }

            // Approvals, tool action requests and blocked issues are optional: the
            // link is "connected" when the company and agents reads succeed. A 404
            // means this Paperclip has no such route, so the list is empty; any
            // other failure keeps the last answer and is named in the status line,
            // never a link failure. Only a refused key (401/403) means reconnect.
            var degraded = new List<string>();
            if (slow)
            {
                var ap = await api.GetAsync(CompanyPath(st, "approvals?status=pending"), ct).ConfigureAwait(false);
                if (ap.IsOk) approvals = PaperclipApi.ParseApprovals(ap.Body).Where(x => x.Status is "pending" or "").ToList();
                else if (ap.Kind == PaperclipHttpKind.NotFound) approvals = Array.Empty<PaperclipApi.RawApproval>();
                else if (ap.Kind == PaperclipHttpKind.Unauthorized) Note(ap);
                else degraded.Add("approvals");

                var ac = await api.GetAsync(CompanyPath(st, "tools/action-requests"), ct).ConfigureAwait(false);
                if (ac.IsOk) actions = PaperclipApi.ParseActionRequests(ac.Body);
                else if (ac.Kind == PaperclipHttpKind.NotFound) actions = Array.Empty<PaperclipApi.RawActionRequest>();
                else if (ac.Kind == PaperclipHttpKind.Unauthorized) Note(ac);
                else degraded.Add("tool actions");

                var bl = await api.GetAsync(CompanyPath(st, "issues?status=blocked"), ct).ConfigureAwait(false);
                if (bl.IsOk) blocked = PaperclipApi.ParseIssues(bl.Body);
                else if (bl.Kind == PaperclipHttpKind.NotFound) blocked = Array.Empty<PaperclipApi.RawIssue>();
                else if (bl.Kind == PaperclipHttpKind.Unauthorized) Note(bl);
                else degraded.Add("blocked tasks");
            }
            else if (st.Degraded is { Count: > 0 }) degraded.AddRange(st.Degraded);

            if (needRuns && (fast || slow) && firstFailure == null)
            {
                var r = await api.GetAsync(CompanyPath(st, "heartbeat-runs?limit=20"), ct).ConfigureAwait(false);
                if (r.IsOk) { runs = PaperclipApi.ParseRuns(r.Body); needRuns = false; }
                else if (r.Kind == PaperclipHttpKind.Unauthorized) Note(r);
                else degraded.Add("recent runs");
            }

            // The hover's task line, only for an issue a live run names that is
            // not yet known. The filter value is assumed (plan 2.5); on failure
            // there is simply no task line, and it is not a link failure.
            var unknown = (live ?? Array.Empty<PaperclipApi.RawLiveRun>())
                .Select(x => x.IssueId).Where(i => i != null && !tasks.ContainsKey(i!)).Distinct().ToList();
            if (unknown.Count > 0 && firstFailure == null)
            {
                var t = await api.GetAsync(CompanyPath(st, "issues?status=in_progress"), ct).ConfigureAwait(false);
                if (t.IsOk)
                {
                    var map = tasks.ToDictionary(k => k.Key, k => k.Value);
                    foreach (var i in PaperclipApi.ParseIssues(t.Body)) map[i.Id] = i.Title;
                    tasks = map;
                }
            }

            // CB-442: a run waiting on a permission prompt has no flag anywhere (measured), so each running run's events
            // are read on the slow tick. A failed read keeps that run's last answer for one tick; a second miss drops it.
            var runtime = st.RuntimeByRun;
            if (slow && firstFailure == null && live != null)
            {
                var seen = new Dictionary<string, (IReadOnlyList<PaperclipApi.RawRuntimeRequest>, bool)>();
                foreach (var run in live.Where(x => x.Status == "running").DistinctBy(x => x.Id))
                {
                    var ev = await api.GetAsync($"/api/heartbeat-runs/{Uri.EscapeDataString(run.Id)}/events?limit=500", ct).ConfigureAwait(false);
                    if (ev.IsOk)
                        seen[run.Id] = (PaperclipApi.ParseRuntimeRequests(run.Id, ev.Body)
                            .Select(r => r with { AgentId = run.AgentId, IssueId = run.IssueId }).ToList(), false);
                    else if (runtime != null && runtime.TryGetValue(run.Id, out var prior) && !prior.Stale)
                        seen[run.Id] = (prior.Requests, true);
                }
                runtime = seen;
            }

            var stamped = st with
            {
                RuntimeByRun = runtime,
                LastFast = fast ? now : st.LastFast,
                LastSlow = slow ? now : st.LastSlow,
                Agents = agents, LiveRuns = live, Runs = runs, Approvals = approvals,
                Actions = actions, Blocked = blocked, Heartbeats = heartbeats,
                TaskTitles = tasks, NeedRuns = needRuns, Degraded = degraded,
            };

            if (firstFailure is { } kind)
                return Failed(stamped, kind, failureDetail, cfg, now);

            var composed = agents == null ? Array.Empty<PaperclipAgent>() as IReadOnlyList<PaperclipAgent>
                : PaperclipApi.Compose(new PaperclipApi.ComposeInput(
                    agents, live ?? Array.Empty<PaperclipApi.RawLiveRun>(), runs ?? Array.Empty<PaperclipApi.RawRun>(),
                    approvals ?? Array.Empty<PaperclipApi.RawApproval>(), actions ?? Array.Empty<PaperclipApi.RawActionRequest>(),
                    blocked ?? Array.Empty<PaperclipApi.RawIssue>(), tasks, cfg.BaseUrl, st.IssuePrefix, now,
                    runtime?.Values.SelectMany(v => v.Requests).ToList()));
            var (stamps, _) = PaperclipApi.TrackAwake(st.AwakeStamps ?? new Dictionary<string, DateTime>(), composed, now);

            return new StepResult(stamped with
            {
                Published = composed, AwakeStamps = stamps, Failures = 0, FailingSince = null, NextAttempt = null,
                Link = PaperclipLink.Connected,
                Status = ConnectedText(cfg, composed.Count, now) + (degraded.Count > 0 ? " Couldn't read " + string.Join(", ", degraded) + "." : ""),
            }, FastEvery);
        }

        private static StepResult Failed(PollState st, PaperclipHttpKind kind, string? detail, PaperclipConfig cfg, DateTime now)
        {
            if (kind == PaperclipHttpKind.Unauthorized)
                return new StepResult(st with
                {
                    Link = PaperclipLink.NeedsReconnect, Status = "Reconnect to Paperclip",
                    Failures = 0, FailingSince = null, NextAttempt = null,
                }, SlowEvery);

            var since = st.FailingSince ?? now;
            var failures = st.Failures + 1;
            var wait = Backoff[Math.Min(failures - 1, Backoff.Length - 1)];
            var past = now - since >= DisconnectGrace;
            var host = HostOf(cfg.BaseUrl);
            var text = $"Can't reach {host}." + (detail != null && detail.Contains("Local Network") ? " " + detail : "");
            return new StepResult(st with
            {
                Failures = failures, FailingSince = since, NextAttempt = now + wait,
                // Kept as they are until the grace has passed, then dimmed.
                Link = past ? PaperclipLink.Unreachable : (st.Link == PaperclipLink.Connecting ? PaperclipLink.Connecting : PaperclipLink.Connected),
                Status = past || st.Link == PaperclipLink.Connecting ? text : st.Status,
            }, wait);
        }

        internal static string ConnectedText(PaperclipConfig cfg, int agents, DateTime now)
        {
            var text = $"Connected to {HostOf(cfg.BaseUrl)}. {agents} agent{(agents == 1 ? "" : "s")}.";
            if (cfg.KeyExpires is { } exp && exp - now <= ExpiryWarning)
            {
                var days = (int)Math.Max(0, Math.Ceiling((exp - now).TotalDays));
                text = days <= 0 ? "Connected. The key expires today."
                    : $"Connected. The key expires in {days} day{(days == 1 ? "" : "s")}.";
            }
            return text;
        }

        internal static string HostOf(string baseUrl) =>
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Authority : baseUrl;

        // The loop's one decision about its transport, pure so it is a test: a
        // client is built when there is none, or the key or the address moved.
        internal static bool NeedsNewClient(bool haveClient, string lastKey, string lastUrl, PaperclipConfig cfg) =>
            !haveClient || cfg.Key != lastKey || cfg.BaseUrl != lastUrl;

        // CB-442: a step that throws backs the loop off for 30 s, and used to say nothing, which hid a parser
        // fault on the mini for the length of a proof. One line per distinct message: the exception's type and
        // message, cut, never a body, key or address from this code (a message is the framework's own words).
        internal static string? StepFailureLine(Exception ex, ref string? lastMessage)
        {
            var message = ex.Message.Length <= 200 ? ex.Message : ex.Message[..200];
            if (message == lastMessage) return null;
            lastMessage = message;
            return $"poll step failed {ex.GetType().Name}: {message}";
        }

        // ---- the loop -------------------------------------------------------

        // Excluded from coverage: a `while` around StepAsync and a delay.
        [ExcludeFromCodeCoverage]
        private static async Task RunAsync(IReadOnlyList<PaperclipAgent> remembered, CancellationToken ct)
        {
            var state = PollState.Initial;
            string lastKey = "", lastUrl = "";
            string? lastFailure = null;
            IPaperclipApi? api = null;
            while (!ct.IsCancellationRequested)
            {
                var cfg = ConfigSource();
                if (NeedsNewClient(api != null, lastKey, lastUrl, cfg))
                {
                    (api as IDisposable)?.Dispose();
                    api = new PaperclipHttp(cfg.BaseUrl, cfg.Key, log: PaperclipHttp.DefaultLog, logSuccess: false);
                    lastKey = cfg.Key; lastUrl = cfg.BaseUrl;
                }
                TimeSpan wait;
                try
                {
                    var step = await StepAsync(api, cfg, state, DateTime.UtcNow, ct).ConfigureAwait(false);
                    state = step.Next; wait = step.Wait;
                    lastFailure = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    state = state with { Status = ex.Message };
                    wait = Backoff[^1];
                    if (StepFailureLine(ex, ref lastFailure) is { } line) PaperclipHttp.DefaultLog(line);
                }
                PublishStep(state, cfg, remembered, RosterStore, DateTime.UtcNow);
                if (state.Published != null) remembered = Array.Empty<PaperclipAgent>();   // never used once live
                try
                {
                    if (await WaitAsync(wait, ct).ConfigureAwait(false)) state = Nudged(state);
                }
                catch { break; }
            }
        }

        [ExcludeFromCodeCoverage]
        public static void Restart()
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _cts = null;
                if (!ConfigSource().Enabled)
                {
                    Publish(Array.Empty<PaperclipAgent>(), PaperclipLink.Off, "Paperclip is switched off.");
                    return;
                }
                var cfg = ConfigSource();
                var remembered = LoadRemembered(cfg, RosterStore);
                Publish(remembered, PaperclipLink.Connecting, "Connecting to Paperclip…", remembered: remembered.Count > 0);
                var cts = new CancellationTokenSource();
                _cts = cts;
                _ = Task.Run(() => RunAsync(remembered, cts.Token), cts.Token);
            }
        }
    }
}

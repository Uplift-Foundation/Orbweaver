using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // What the feed is told about the open panel's agent, read off the poll's state by the session.
    internal sealed record ChatTarget(string AgentId, PaperclipChat.TaskRef? Task, string? RunId, bool RunLive);

    internal sealed record ChatSnapshot(PaperclipChat.TaskRef? Task, IReadOnlyList<PaperclipChat.Comment> Comments,
        PaperclipChat.Activity? Activity, bool NeedsReconnect);

    // CB-443 (plan 2.3): the reads behind one open chat panel. Nothing is requested until Opened() and
    // nothing after Closed(): a closed panel costs nothing. While open: the task's comments every 4 s
    // (and at once on Nudge), and the newest run's events every 2 s while it is live, once more when it
    // ends, then kept. Events are read with afterSeq so a tick in a quiet moment returns []; a full page
    // of 500 is followed at once by the next, at most four pages a tick (limit returns the OLDEST rows,
    // so a plain limit=500 would freeze on a long run's first 500). A failed read keeps what is shown and
    // tries again; a 401 or 403 stops the feed with NeedsReconnect set. Separate from the poll's own
    // events read (CB-442's permission prompts) on purpose: two readers of one body would couple them.
    //
    // Not measured: the cost of re-reading a long comment thread whole every 4 s (plan 10, assumption 7).
    internal sealed class PaperclipChatFeed : IDisposable
    {
        internal const int PageSize = 500;
        internal const int MaxPagesPerTick = 4;

        private readonly IPaperclipApi _api;
        private readonly string _companyId;
        private readonly Func<ChatTarget> _target;
        private readonly TimeSpan _eventsEvery;
        private readonly TimeSpan _commentsEvery;
        private readonly SemaphoreSlim _wake = new(0);
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private volatile bool _nudge;

        private string? _taskId;
        private IReadOnlyList<PaperclipChat.Comment> _comments = Array.Empty<PaperclipChat.Comment>();
        private PaperclipChat.Activity? _activity;
        private bool _lastLive, _readOnce, _needsReconnect;
        private DateTime _commentsAt = DateTime.MinValue;

        internal event Action<ChatSnapshot>? Updated;

        internal PaperclipChatFeed(IPaperclipApi api, string companyId, Func<ChatTarget> target,
            TimeSpan? eventsEvery = null, TimeSpan? commentsEvery = null)
        {
            _api = api; _companyId = companyId; _target = target;
            _eventsEvery = eventsEvery ?? TimeSpan.FromSeconds(2);
            _commentsEvery = commentsEvery ?? TimeSpan.FromSeconds(4);
        }

        internal bool IsOpen { get { lock (_gate) return _cts != null; } }

        internal void Opened()
        {
            lock (_gate)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
                _taskId = null; _comments = Array.Empty<PaperclipChat.Comment>(); _activity = null;
                _lastLive = _readOnce = _needsReconnect = false; _commentsAt = DateTime.MinValue;
                var ct = _cts.Token;
                _ = Task.Run(() => LoopAsync(ct));
            }
        }

        internal void Closed()
        {
            lock (_gate) { _cts?.Cancel(); _cts = null; }
            _wake.Release();
        }

        // Read the comments on the next tick instead of waiting for the 4 s one (after a send, when a run ends).
        internal void Nudge() { _nudge = true; _wake.Release(); }

        public void Dispose() => Closed();

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var comments = _nudge || DateTime.UtcNow - _commentsAt >= _commentsEvery;
                    _nudge = false;
                    await TickAsync(comments, ct).ConfigureAwait(false);
                    if (_needsReconnect) return;
                    await _wake.WaitAsync(_eventsEvery, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        // One tick. `comments` says whether the comments list is read this time.
        internal async Task TickAsync(bool comments, CancellationToken ct)
        {
            var t = _target();
            var changed = false;
            var taskId = t.Task?.Id;
            if (taskId != _taskId)
            {
                _taskId = taskId; _comments = Array.Empty<PaperclipChat.Comment>(); _activity = null;
                _lastLive = _readOnce = false; changed = true;
                comments = true;
            }

            if (taskId != null && comments)
            {
                _commentsAt = DateTime.UtcNow;
                var res = await _api.GetAsync($"/api/issues/{Uri.EscapeDataString(taskId)}/comments", ct).ConfigureAwait(false);
                if (Refused(res)) changed |= SetReconnect();
                else if (res.IsOk)
                {
                    var parsed = PaperclipChat.ParseComments(res.Body);
                    if (!parsed.SequenceEqual(_comments)) { _comments = parsed; changed = true; }
                }
            }

            if (taskId != null && !_needsReconnect && !string.IsNullOrEmpty(t.RunId))
            {
                if (_activity == null || _activity.RunId != t.RunId) { _activity = PaperclipChat.Activity.Empty(t.RunId); _readOnce = false; }
                // Live: every tick. Just ended: once more for the tail. Finished: once, then kept.
                if (t.RunLive || _lastLive || !_readOnce)
                    changed |= await ReadEventsAsync(t.RunId, ct).ConfigureAwait(false);
                _lastLive = t.RunLive;
            }
            else if (string.IsNullOrEmpty(t.RunId) && _activity != null) { _activity = null; changed = true; }

            if (changed) Updated?.Invoke(new ChatSnapshot(t.Task, _comments, _activity, _needsReconnect));
        }

        private async Task<bool> ReadEventsAsync(string runId, CancellationToken ct)
        {
            var changed = false;
            for (var page = 0; page < MaxPagesPerTick; page++)
            {
                var after = _activity!.LastSeq;
                var res = await _api.GetAsync(
                    $"/api/heartbeat-runs/{Uri.EscapeDataString(runId)}/events?afterSeq={after}&limit={PageSize}", ct).ConfigureAwait(false);
                if (Refused(res)) return SetReconnect() | changed;
                if (!res.IsOk) return changed;
                _readOnce = true;
                var next = PaperclipChat.Fold(_activity, res.Body);
                if (next.LastSeq != _activity.LastSeq || next.Rows.Count != _activity.Rows.Count) changed = true;
                _activity = next;
                if (PaperclipChat.CountEvents(res.Body) < PageSize || next.LastSeq == after) break;
            }
            return changed;
        }

        // The rows of issues?assigneeAgentId=<agent>, for the task rung that is read once on open (plan 2.2).
        // Null on a failed read, so the caller can tell "none" from "could not ask".
        internal static async Task<IReadOnlyList<PaperclipChat.AssignedIssue>?> ReadAssignedAsync(
            IPaperclipApi api, string companyId, string agentId, CancellationToken ct)
        {
            var res = await api.GetAsync(
                $"/api/companies/{Uri.EscapeDataString(companyId)}/issues?assigneeAgentId={Uri.EscapeDataString(agentId)}", ct).ConfigureAwait(false);
            return res.IsOk ? PaperclipChat.ParseAssignedIssues(res.Body) : null;
        }

        private static bool Refused(PaperclipResponse r) => r.StatusCode is 401 or 403;

        private bool SetReconnect()
        {
            if (_needsReconnect) return false;
            _needsReconnect = true;
            return true;
        }
    }
}

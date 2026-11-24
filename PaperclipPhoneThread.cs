using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // What RelayRemote needs from the desk to serve a phone's watch and input on a Paperclip row (CB-447,
    // frames-v18). A seam so the relay is tested without Paperclip: the live one is PaperclipPhoneThreads.
    internal interface IPaperclipPhoneHost
    {
        // The caps name's condition: this desk can show and message a Paperclip agent right now.
        bool Available { get; }

        // One thread per agent, shared by every phone watching it, counted: the feed behind it reads only
        // while someone holds it. The holder calls Release() when its watch ends.
        PaperclipPhoneThread? Acquire(string sessionId, string agentName);
    }

    internal enum PaperclipSendResult { Sent, Failed }

    // One agent's thread as phones see it: the same rows the desktop panel shows (the same Build and Merge, the
    // same task choice), held headless and thread-safe because the relay runs on its own threads and
    // PaperclipChatSession is the UI thread's. Its own feed, opened by the first holder and closed by the last
    // Release, so a phone watching counts as an open panel and costs nothing once it stops. A desk with the
    // panel and a phone open on one agent therefore reads twice (15 to 45 reads a minute extra), which is the
    // price of not reaching across threads into the panel's session.
    internal sealed class PaperclipPhoneThread
    {
        internal sealed record Snapshot(IReadOnlyList<PaperclipChat.Row> Rows, string? TaskId, long Version);

        private readonly string _agentId;
        private readonly string _name;
        private readonly IPaperclipChatFeed _feed;
        private readonly PaperclipTaskPicker _picker;
        private readonly Func<IPaperclipActions?> _actions;
        private readonly Func<IReadOnlyList<PaperclipAgent>> _roster;
        private readonly Func<PaperclipLink> _link;
        private readonly Func<CancellationToken, Task<IReadOnlyList<PaperclipChat.AssignedIssue>?>> _readAssigned;
        private readonly Func<DateTime> _now;
        private readonly Action<PaperclipPhoneThread> _onEmpty;
        private readonly TimeSpan _assignedWait;
        private readonly object _gate = new();
        // Serialises the first Hold and the last Release, so a Hold arriving as the last holder leaves cannot have
        // its freshly opened feed closed underneath it.
        private readonly object _life = new();

        private IReadOnlyList<PaperclipChat.Row> _rows = Array.Empty<PaperclipChat.Row>();
        private PaperclipChat.TaskRef? _task;
        private string? _taskId;
        private long _version;
        private int _holders;
        private bool _refused;
        private CancellationTokenSource? _assignedCts;
        // The assigned-issues read Hold() started, which a send with no known task waits on (below).
        private Task? _assignedRead;

        // How long a send that finds no task waits for that read before it opens one. The read measured 79 ms
        // for one agent; this is generous, and it is a bound, not a retry: past it the message opens a task.
        internal static readonly TimeSpan DefaultAssignedWait = TimeSpan.FromSeconds(3);

        internal PaperclipPhoneThread(
            string agentId, string agentName, IPaperclipChatFeed feed, PaperclipTaskPicker picker,
            Func<IPaperclipActions?> actions, Func<IReadOnlyList<PaperclipAgent>> roster, Func<PaperclipLink> link,
            Func<CancellationToken, Task<IReadOnlyList<PaperclipChat.AssignedIssue>?>> readAssigned,
            Func<DateTime> now, Action<PaperclipPhoneThread> onEmpty, TimeSpan? assignedWait = null)
        {
            _assignedWait = assignedWait ?? DefaultAssignedWait;
            _agentId = agentId;
            _name = agentName;
            _feed = feed;
            _picker = picker;
            _actions = actions;
            _roster = roster;
            _link = link;
            _readAssigned = readAssigned;
            _now = now;
            _onEmpty = onEmpty;
        }

        internal string AgentId => _agentId;
        internal int Holders { get { lock (_gate) return _holders; } }

        internal Snapshot Current()
        {
            lock (_gate) return new Snapshot(_rows, _taskId, _version);
        }

        // ---- lifetime ----------------------------------------------------------

        internal void Hold()
        {
            lock (_life)
            {
                bool first;
                lock (_gate) first = ++_holders == 1;
                if (!first) return;
                _feed.Updated += OnFeed;
                PaperclipSessions.Changed += OnRoster;
                _feed.Opened();
                _assignedRead = ReadAssignedOnceAsync();
            }
        }

        internal void Release()
        {
            lock (_life)
            {
                bool last;
                lock (_gate)
                {
                    if (_holders == 0) return;
                    last = --_holders == 0;
                }
                if (!last) return;
                _feed.Updated -= OnFeed;
                PaperclipSessions.Changed -= OnRoster;
                _assignedCts?.Cancel();
                _assignedCts = null;
                _feed.Closed();
            }
            _onEmpty(this);
        }

        private async Task ReadAssignedOnceAsync()
        {
            if (_picker.Pick() != null) return;
            var cts = _assignedCts = new CancellationTokenSource();
            IReadOnlyList<PaperclipChat.AssignedIssue>? read;
            try { read = await _readAssigned(cts.Token).ConfigureAwait(false); }
            catch (Exception) { return; }   // cancelled, or failed: no task, and never an exception text
            if (cts.IsCancellationRequested || read == null) return;
            _picker.SetAssigned(read);
            _feed.Nudge();
        }

        // The key was refused and the link is Connected again: the feed stopped itself on the 401, so start it
        // over rather than stay frozen until every phone has left and come back.
        private void OnRoster()
        {
            bool restart;
            lock (_gate)
            {
                restart = _refused && _holders > 0 && _link() == PaperclipLink.Connected;
                if (restart) _refused = false;
            }
            if (!restart) return;
            _feed.Closed();
            _feed.Opened();
        }

        // ---- reads -> rows -----------------------------------------------------

        internal ChatTarget Target() => _picker.Target();

        private void OnFeed(ChatSnapshot snap)
        {
            var names = _roster().Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToDictionary(a => a.Id, a => a.Name);
            var built = PaperclipChat.Build(snap.Task, _agentId, names, snap.Comments, snap.Activity);
            lock (_gate)
            {
                _refused = snap.NeedsReconnect;
                _task = snap.Task;
                // Only a change away from a task replaces the rows; from none to the task a message opened keeps
                // the line the phone just sent (it is the task's description, not a comment).
                var replace = _taskId != null && snap.Task?.Id != _taskId;
                _rows = replace || _version == 0 ? built : PaperclipChat.Merge(_rows, built);
                if (snap.Task?.Id is { } id) _taskId = id; else if (replace) _taskId = null;
                _version++;
            }
        }

        // ---- sending -----------------------------------------------------------

        // A phone's message: the same decision as the panel's. A comment on the agent's current task, or a task
        // opened by the message when it has none. The sent line is added at once, keyed by the comment id when
        // the answer carried one, so the next read adopts it instead of doubling it.
        internal async Task<(PaperclipSendResult Result, string? Why)> SendAsync(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return (PaperclipSendResult.Failed, PaperclipActions.NothingToSendWhy);
            var actions = _actions();
            if (actions is null) return (PaperclipSendResult.Failed, PaperclipChatSession.NotConnectedText);

            PaperclipChat.TaskRef? task;
            lock (_gate) task = _task;
            task ??= _picker.Pick();

            // No task known yet is not "no task": rung 4 (the agent's assigned issues) is read once when the first
            // phone holds the thread, and a message sent before that read lands would open a second task for an
            // agent that already has one. Wait for it, bounded, and decide again.
            if (task is null && _assignedRead is { IsCompleted: false } read)
            {
                using var stop = new CancellationTokenSource();
                await Task.WhenAny(read, Task.Delay(_assignedWait, stop.Token)).ConfigureAwait(false);
                stop.Cancel();
                task = _picker.Pick();
            }

            PaperclipSent sent;
            try
            {
                sent = task is { } t
                    ? await actions.CommentAsync(t.Id, text, CancellationToken.None).ConfigureAwait(false)
                    : await actions.OpenTaskAsync(_agentId, text, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return (PaperclipSendResult.Failed, PaperclipChatSession.UnreachableText);   // never the exception text
            }

            if (!sent.Outcome.IsDone)
            {
                if (sent.Outcome.Why == PaperclipActions.TaskGoneWhy) _feed.Nudge();
                return (PaperclipSendResult.Failed, PaperclipChatSession.RefusalFor(sent.Outcome, _name));
            }

            var key = string.IsNullOrEmpty(sent.CommentId)
                ? PaperclipChat.LocalPrefix + Guid.NewGuid().ToString("N")
                : PaperclipChat.CommentKey(sent.CommentId);
            lock (_gate)
            {
                _rows = _rows.Append(new PaperclipChat.Row(key, ChatRole.User, text, _now())).ToList();
                _version++;
            }
            if (task is null && !string.IsNullOrEmpty(sent.IssueId))
                _picker.Pin(new PaperclipChat.TaskRef(sent.IssueId, "", FirstLine(text), "todo"));
            _feed.Nudge();
            return (PaperclipSendResult.Sent, null);
        }

        private static string FirstLine(string text)
        {
            var line = text.Split('\n')[0].Trim();
            return line.Length <= 80 ? line : line[..80];
        }
    }

    // The live host: one PaperclipPhoneThread per agent, created on the first Acquire and dropped at the last
    // Release. Excluded nowhere: every seam it uses is a constructor argument, so a test builds it with fakes.
    internal sealed class PaperclipPhoneThreads : IPaperclipPhoneHost
    {
        private readonly Func<bool> _available;
        private readonly Func<Func<ChatTarget>, IPaperclipChatFeed> _feeds;
        private readonly Func<IPaperclipActions?> _actions;
        private readonly Func<IReadOnlyList<PaperclipAgent>> _roster;
        private readonly Func<PaperclipSessions.ChatView> _view;
        private readonly Func<PaperclipLink> _link;
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<PaperclipChat.AssignedIssue>?>> _readAssigned;
        private readonly Func<DateTime> _now;
        private readonly TimeSpan? _assignedWait;
        private readonly object _gate = new();
        private readonly Dictionary<string, PaperclipPhoneThread> _threads = new(StringComparer.Ordinal);

        internal PaperclipPhoneThreads(
            Func<bool> available, Func<Func<ChatTarget>, IPaperclipChatFeed> feeds, Func<IPaperclipActions?> actions,
            Func<IReadOnlyList<PaperclipAgent>> roster, Func<PaperclipSessions.ChatView> view, Func<PaperclipLink> link,
            Func<string, CancellationToken, Task<IReadOnlyList<PaperclipChat.AssignedIssue>?>> readAssigned,
            Func<DateTime> now, TimeSpan? assignedWait = null)
        {
            _assignedWait = assignedWait;
            _available = available;
            _feeds = feeds;
            _actions = actions;
            _roster = roster;
            _view = view;
            _link = link;
            _readAssigned = readAssigned;
            _now = now;
        }

        // The desk's own: the live feed, actions and roster the desktop panel reads, for as long as a phone watches.
        // Excluded from coverage: one line into each of the statics the constructor's seams stand for.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static PaperclipPhoneThreads Live { get; } = new(
            () => RelayService.PaperclipChatAvailable,
            target => SessionManager.PaperclipFeedFactory(target),
            () => PaperclipSessions.Actions,
            PaperclipSessions.Snapshot,
            () => PaperclipSessions.CurrentChatView,
            () => PaperclipSessions.Link,
            PaperclipTaskPicker.ReadAssignedLiveAsync,
            () => DateTime.UtcNow);

        public bool Available => _available();

        internal int Count { get { lock (_gate) return _threads.Count; } }

        public PaperclipPhoneThread? Acquire(string sessionId, string agentName)
        {
            if (!_available()) return null;
            var agentId = sessionId.StartsWith(PaperclipScan.KeyPrefix, StringComparison.Ordinal)
                ? sessionId[PaperclipScan.KeyPrefix.Length..] : sessionId;
            PaperclipPhoneThread thread;
            lock (_gate)
            {
                if (!_threads.TryGetValue(agentId, out var existing))
                {
                    PaperclipPhoneThread? made = null;
                    var picker = new PaperclipTaskPicker(agentId, _view, _roster);
                    made = new PaperclipPhoneThread(agentId, agentName, _feeds(() => made!.Target()), picker, _actions, _roster, _link,
                        ct => _readAssigned(agentId, ct), _now, Forget, _assignedWait);
                    _threads[agentId] = existing = made;
                }
                thread = existing;
                // Held under the table's lock, so a Release that empties the thread cannot drop it between this
                // lookup and the hold.
                thread.Hold();
            }
            return thread;
        }

        private void Forget(PaperclipPhoneThread thread)
        {
            lock (_gate)
                if (_threads.TryGetValue(thread.AgentId, out var held) && ReferenceEquals(held, thread) && thread.Holders == 0)
                    _threads.Remove(thread.AgentId);
        }
    }
}

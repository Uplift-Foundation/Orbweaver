using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Orbweaver
{
    // The timed reads behind one open panel, as the session sees them. A seam so the session is
    // built and tested without a network or a clock: the live one wraps PaperclipChatFeed, and the
    // UI suite hands the session snapshots by hand.
    internal interface IPaperclipChatFeed
    {
        // Raised from any thread; the session hops to the UI thread itself.
        event Action<ChatSnapshot>? Updated;
        void Opened();
        void Closed();
        void Nudge();
    }

    // PaperclipChatFeed is internal-membered and engineer A's; this is the thin adapter.
    // Excluded from coverage: every line is a one-line forward to a class that is tested.
    [ExcludeFromCodeCoverage]
    internal sealed class LiveChatFeed : IPaperclipChatFeed
    {
        private readonly PaperclipChatFeed _feed;
        internal LiveChatFeed(PaperclipChatFeed feed) => _feed = feed;
        public event Action<ChatSnapshot>? Updated
        {
            add => _feed.Updated += value;
            remove => _feed.Updated -= value;
        }
        public void Opened() => _feed.Opened();
        public void Closed() => _feed.Closed();
        public void Nudge() => _feed.Nudge();
    }

    // The chat panel's view of one Paperclip agent (CB-443): the thread of the task it is on, live
    // tool lines and speech as Paperclip records them, and a box whose message is a comment on that
    // task (or, with no task, the one that opens a task).
    //
    // The rules are PaperclipChat's (pure) and the reads are PaperclipChatFeed's; this class owns
    // what is on screen: it picks the task the feed reads, turns each snapshot into rows, rows into
    // TurnAdded/TurnUpdated, and a send into a comment. Modelled on ClaudeCloudChatSession: cached
    // per orb in SessionManager, with a `post` seam so it runs without an Avalonia app. Nothing
    // optional beyond the box's words, the read-only state, the link and the lifetime is
    // implemented, which is what leaves out stop, images and run options.
    //
    // Never logs, and never puts a comment body or a command in a hover or roster row: they reach
    // the panel and nowhere else (plan 2.4).
    internal sealed class PaperclipChatSession : IRemoteChatSession, IRemoteChatComposer,
        IRemoteChatComposerChanges, IRemoteChatReadOnly, IRemoteChatBacklog,
        IRemoteChatPanelLifetime, IRemoteChatReplyLabel
    {
        internal const string ReplyLinkLabel = "Open in Paperclip";
        internal const string UnreachableText = "Can't reach Paperclip.";
        internal const string RefusedText = "Paperclip refused that.";
        internal const string NotConnectedText = "Paperclip is not connected.";

        private readonly string _agentId;
        private readonly IPaperclipChatFeed _feed;
        private readonly Func<IPaperclipActions?> _actions;
        private readonly Func<IReadOnlyList<PaperclipAgent>> _roster;
        private readonly Func<PaperclipSessions.ChatView> _view;
        private readonly Func<CancellationToken, Task<IReadOnlyList<PaperclipChat.AssignedIssue>?>> _readAssigned;
        private readonly Func<PaperclipLink> _link;
        private readonly Func<DateTime> _now;
        private readonly Action<Action> _post;
        private readonly Action<TimeSpan, Action> _schedule;
        // Guards what the feed's thread (Target) and the UI thread (Apply, SendAsync) both touch.
        private readonly object _gate = new();

        // The rows on screen and the turn for each, index for index; a replaced row keeps its
        // place and its turn. The red send-failure line is not one of them.
        private IReadOnlyList<PaperclipChat.Row> _shown = Array.Empty<PaperclipChat.Row>();
        private readonly List<ChatTurn> _rowTurns = new();
        private readonly List<ChatTurn> _history = new();
        private ChatTurn? _errorTurn;

        private IReadOnlyList<PaperclipChat.AssignedIssue>? _assigned;
        private PaperclipChat.TaskRef? _task;
        private PaperclipChat.TaskRef? _focus;
        private bool _loaded, _live, _refused, _open;
        private DateTime? _wakingSince;
        private CancellationTokenSource? _assignedCts;
        private RemoteChatState _state = RemoteChatState.Connecting;
        private string _hint;

        internal PaperclipChatSession(
            string agentId, string name, string? url, Func<IPaperclipChatFeed> feed,
            Func<IPaperclipActions?> actions,
            Func<IReadOnlyList<PaperclipAgent>>? roster = null,
            Func<PaperclipSessions.ChatView>? view = null,
            Func<CancellationToken, Task<IReadOnlyList<PaperclipChat.AssignedIssue>?>>? readAssigned = null,
            Func<PaperclipLink>? link = null,
            Func<DateTime>? now = null, Action<Action>? post = null, Action<TimeSpan, Action>? schedule = null)
        {
            _agentId = agentId;
            SessionId = PaperclipScan.KeyFor(agentId);
            DisplayName = string.IsNullOrWhiteSpace(name) ? agentId : name;
            ReplyUrl = string.IsNullOrWhiteSpace(url) ? null : url;
            _feed = feed();
            _actions = actions;
            _roster = roster ?? PaperclipSessions.Snapshot;
            _view = view ?? (() => PaperclipSessions.CurrentChatView);
            _readAssigned = readAssigned ?? (_ => Task.FromResult<IReadOnlyList<PaperclipChat.AssignedIssue>?>(null));
            _link = link ?? (() => PaperclipSessions.Link);
            _now = now ?? (() => DateTime.UtcNow);
            _post = post ?? (a => Dispatcher.UIThread.Post(a));
            _schedule = schedule ?? ((after, a) => _ = Task.Delay(after).ContinueWith(_ => a(), TaskScheduler.Default));
            _hint = HintFor();
        }

        public string SessionId { get; }
        public string DisplayName { get; }
        public RemoteChatState State => _state;
        public IReadOnlyList<ChatTurn> History => _history;
        public string? ReplyUrl { get; }
        public string ReplyLabel => ReplyLinkLabel;
        public bool IsReadOnly => _refused || _link() == PaperclipLink.NeedsReconnect;
        public string ComposerHint => _hint;
        public bool HasMore => false;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? ComposerHintChanged;
        public event Action? ReadOnlyChanged;
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended { add { } remove { } }

        public Task<bool> LoadOlderAsync(CancellationToken ct) => Task.FromResult(false);

        // Nothing to stop from here: Stop is not in this slice (plan 1.5).
        public void Cancel() { }

        // ---- lifetime ----------------------------------------------------------

        public void PanelOpened()
        {
            if (_open) return;
            _open = true;
            _feed.Updated += OnFeed;
            PaperclipSessions.Changed += OnRoster;
            _feed.Opened();
            _ = ReadAssignedOnceAsync();
        }

        public void PanelClosed()
        {
            if (!_open) return;
            _open = false;
            _feed.Updated -= OnFeed;
            PaperclipSessions.Changed -= OnRoster;
            _assignedCts?.Cancel();
            _assignedCts = null;
            _feed.Closed();
        }

        // Rung 4 of the task choice (plan 2.2): read once on open, and only when the poll's own
        // state gave no task. A 49-row, 169 KB read, so it is never polled.
        private async Task ReadAssignedOnceAsync()
        {
            if (PickTask() != null) return;
            var cts = _assignedCts = new CancellationTokenSource();
            IReadOnlyList<PaperclipChat.AssignedIssue>? read = null;
            try { read = await _readAssigned(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { return; }   // a failed read leaves "no task", never an exception text
            if (cts.IsCancellationRequested || read == null) return;
            lock (_gate) _assigned = read;
            _feed.Nudge();
        }

        private void OnFeed(ChatSnapshot snap) => _post(() => Apply(snap));
        private void OnRoster() => _post(() =>
        {
            // The key was refused and the link is Connected again (the person reconnected): the feed
            // stopped itself on the 401, so start it over rather than stay frozen until reopened.
            if (_open && _refused && _link() == PaperclipLink.Connected)
            {
                _refused = false;
                _feed.Closed();
                _feed.Opened();
                SetState(RemoteChatState.Connecting);
            }
            RefreshAffordances();
        });

        // ---- which task, and which run: what the feed is told to read ----------

        internal PaperclipChat.TaskRef? PickTask()
        {
            var view = _view();
            PaperclipChat.TaskRef? task;
            lock (_gate)
            {
                var liveHere = view.Live.Any(r => r.AgentId == _agentId && !string.IsNullOrEmpty(r.IssueId));
                // A run on any task is the agent's own word on which task it is on: the task a message
                // just opened stops being pinned.
                if (liveHere) _focus = null;
                var picked = PaperclipChat.CurrentTask(_agentId, view.Live, view.Blocked, view.Runs, _assigned,
                    Array.Empty<PaperclipApi.RawIssue>());
                task = _focus ?? picked;
            }
            if (task != null && string.IsNullOrWhiteSpace(task.Title) && AgentRow()?.Task is { Length: > 0 } title)
                task = task with { Title = title };
            return task;
        }

        // What the feed reads this tick: the task, and the newest run on it (the live one, or the
        // newest finished).
        internal ChatTarget Target()
        {
            var task = PickTask();   // runs on the feed's thread: it writes no field the UI thread reads
            var view = _view();
            var live = task == null ? null
                : view.Live.Where(r => r.AgentId == _agentId && r.IssueId == task.Id)
                    .OrderByDescending(r => r.StartedAt ?? DateTime.MinValue).FirstOrDefault();
            if (live != null) return new ChatTarget(_agentId, task, live.Id, true);
            var finished = task == null ? null
                : view.Runs.Where(r => r.AgentId == _agentId && r.IssueId == task.Id)
                    .OrderByDescending(r => r.FinishedAt ?? r.StartedAt ?? DateTime.MinValue).FirstOrDefault();
            return new ChatTarget(_agentId, task, finished?.Id, false);
        }


        private PaperclipAgent? AgentRow() => _roster().FirstOrDefault(a => a.Id == _agentId);

        // ---- reads -> turns ----------------------------------------------------

        internal void Apply(ChatSnapshot snap)
        {
            if (!_open) return;

            _task = snap.Task;
            _live = snap.Activity != null && PickLive(snap.Task);
            // A run showing is the wake that "Waking…" was waiting for. So is the agent's own comment, or a
            // row of a run, newer than the send: a run can start and end between two ticks.
            if (_live || (_wakingSince is { } since && AnswerSince(snap, since))) _wakingSince = null;

            var names = _roster().Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToDictionary(a => a.Id, a => a.Name);
            var built = PaperclipChat.Build(snap.Task, _agentId, names, snap.Comments, snap.Activity);

            // From no task to the one a message just opened is not a change of thread: the sent bubble
            // (the task's description, not a comment) must survive it.
            var taskChanged = _loaded && _taskId != null && snap.Task?.Id != _taskId;
            _taskId = snap.Task?.Id;
            if (!_loaded || taskChanged) ReplaceRows(built);
            else
            {
                var merged = PaperclipChat.Merge(_shown, built);
                // At the row cap Merge drops the oldest row after the task row, which moves every
                // later row one place up: the index-for-index diff below no longer lines up.
                var shifted = merged.Count > 2 && merged.Count == _shown.Count && _shown.Count >= PaperclipChat.RowCap
                    && merged[1].Key != _shown[1].Key && merged[1].Key == _shown[2].Key;
                if (shifted) ReplaceRows(merged); else MergeRows(merged);
            }

            if (!_loaded) { _loaded = true; SetState(RemoteChatState.Connected); }
            _refused = snap.NeedsReconnect;
            SetState(IsReadOnly ? RemoteChatState.Error : RemoteChatState.Connected);
            RefreshAffordances();
        }

        private string? _taskId;

        private bool AnswerSince(ChatSnapshot snap, DateTime since) =>
            snap.Comments.Any(c => c.AuthorAgentId == _agentId && c.At is { } at && at > since)
            || (snap.Activity?.Rows.Any(r => r.At > since) ?? false);

        private bool PickLive(PaperclipChat.TaskRef? task) =>
            task != null && _view().Live.Any(r => r.AgentId == _agentId && r.IssueId == task.Id);

        private ChatTurn TurnFor(PaperclipChat.Row r) => new()
        {
            Role = r.Role,
            Text = r.Text,
            IsError = r.Error,
            IsComplete = true,
            At = r.At == DateTime.MinValue ? DateTimeOffset.Now
                : new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc)).ToLocalTime(),
        };

        private void ReplaceRows(IReadOnlyList<PaperclipChat.Row> rows)
        {
            _shown = rows;
            _history.Clear();
            _rowTurns.Clear();
            _errorTurn = null;
            foreach (var r in rows)
            {
                var t = TurnFor(r);
                _rowTurns.Add(t);
                _history.Add(t);
            }
            HistoryReplaced?.Invoke();
        }

        // Merge keeps every shown row in its place and appends the rest, so the two lists line up by
        // index: update in place what changed, add what is new.
        private void MergeRows(IReadOnlyList<PaperclipChat.Row> merged)
        {
            for (var i = 0; i < merged.Count; i++)
            {
                var r = merged[i];
                if (i < _rowTurns.Count)
                {
                    var t = _rowTurns[i];
                    if (t.Text == r.Text && t.IsError == r.Error) continue;
                    t.Text = r.Text;
                    t.IsError = r.Error;
                    TurnUpdated?.Invoke(t);
                }
                else
                {
                    var t = TurnFor(r);
                    _rowTurns.Add(t);
                    _history.Add(t);
                    TurnAdded?.Invoke(t);
                }
            }
            _shown = merged;
        }

        // ---- sending -----------------------------------------------------------

        public async Task<ChatSendOutcome> SendAsync(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return ChatSendOutcome.Failed;

            var actions = _actions();
            if (actions is null) return Fail(NotConnectedText);

            var task = _task ?? PickTask();
            PaperclipSent sent;
            try
            {
                sent = task is { } t
                    ? await actions.CommentAsync(t.Id, text, CancellationToken.None).ConfigureAwait(true)
                    : await actions.OpenTaskAsync(_agentId, text, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // Never the exception text: a wire error can carry the body.
                return Fail(UnreachableText);
            }

            if (!sent.Outcome.IsDone)
            {
                // A task that is gone: look again for the one the agent is on now.
                if (sent.Outcome.Why == PaperclipActions.TaskGoneWhy) _feed.Nudge();
                return Fail(RefusalText(sent.Outcome));
            }

            // Their bubble is raised on Paperclip's acceptance and not before (plan 1.3). Keyed by
            // the comment id when the answer carried one, else locally for Merge to adopt.
            ClearError();
            var key = string.IsNullOrEmpty(sent.CommentId)
                ? PaperclipChat.LocalPrefix + Guid.NewGuid().ToString("N")
                : PaperclipChat.CommentKey(sent.CommentId);
            var row = new PaperclipChat.Row(key, ChatRole.User, text, _now());
            _shown = _shown.Append(row).ToList();
            var turn = TurnFor(row);
            _rowTurns.Add(turn);
            _history.Add(turn);
            TurnAdded?.Invoke(turn);

            _wakingSince = _now();
            // The 30 s edge has no tick of its own (an unchanged read raises nothing): wake the box then.
            _schedule(PaperclipChat.WakeGrace + TimeSpan.FromMilliseconds(250), () => _post(RefreshAffordances));
            // A task this message opened is the panel's task now (until the agent runs on another).
            if (task is null && !string.IsNullOrEmpty(sent.IssueId))
                lock (_gate) _focus = new PaperclipChat.TaskRef(sent.IssueId, "", FirstLine(text), "todo");
            _feed.Nudge();
            RefreshAffordances();
            return ChatSendOutcome.Sent;
        }

        private static string FirstLine(string text)
        {
            var line = text.Split('\n')[0].Trim();
            return line.Length <= 80 ? line : line[..80];
        }

        internal string RefusalText(PaperclipOutcome o) => o.Result switch
        {
            PaperclipActionResult.NeedsReconnect => $"Reconnect to Paperclip to message {DisplayName}.",
            PaperclipActionResult.Unreachable => UnreachableText,
            _ => string.IsNullOrWhiteSpace(o.Why) ? RefusedText : o.Why!,
        };

        // A failed send keeps the text in the box (the panel does that on Failed) and says why in
        // one red line, which is reused rather than stacked.
        private ChatSendOutcome Fail(string why)
        {
            if (_errorTurn is null)
            {
                _errorTurn = new ChatTurn { Role = ChatRole.System, Text = why, IsError = true, IsComplete = true };
                _history.Add(_errorTurn);
                TurnAdded?.Invoke(_errorTurn);
            }
            else if (_errorTurn.Text != why)
            {
                _errorTurn.Text = why;
                TurnUpdated?.Invoke(_errorTurn);
            }
            return ChatSendOutcome.Failed;
        }

        // A later success removes the stale red line; the panel rebuilds on HistoryReplaced.
        private void ClearError()
        {
            if (_errorTurn is null) return;
            _history.Remove(_errorTurn);
            _errorTurn = null;
            HistoryReplaced?.Invoke();
        }

        // ---- the box's words ---------------------------------------------------

        private bool _wasReadOnly;

        private void RefreshAffordances()
        {
            var ro = IsReadOnly;
            if (ro != _wasReadOnly)
            {
                _wasReadOnly = ro;
                ReadOnlyChanged?.Invoke();
            }
            var hint = HintFor();
            if (hint != _hint)
            {
                _hint = hint;
                ComposerHintChanged?.Invoke();
            }
        }

        internal string HintFor() =>
            PaperclipChat.Hint(DisplayName, AgentRow()?.Status, _task is not null,
                _live || AgentRow()?.State == "generating",
                _refused ? PaperclipLink.NeedsReconnect : _link(), _wakingSince, _now());

        private void SetState(RemoteChatState s)
        {
            if (_state == s) return;
            _state = s;
            StateChanged?.Invoke(s);
        }
    }
}

namespace Orbweaver
{
    // The feed of a panel opened while Paperclip is switched off or has no company: reads
    // nothing, says nothing. The panel still opens, with its link and the orb's own state.
    internal sealed class IdleChatFeed : IPaperclipChatFeed
    {
        public event Action<ChatSnapshot>? Updated { add { } remove { } }
        public void Opened() { }
        public void Closed() { }
        public void Nudge() { }
    }
}

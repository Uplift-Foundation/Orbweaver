using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Which task an agent's thread follows, and which run on it the feed reads (plan 2.2). Pulled out of
    // PaperclipChatSession so the rule exists once: the desktop panel and a phone watching the same agent
    // (CB-447) both ask it. Thread-safe: the feed's thread calls Target() while the UI thread (or the
    // relay's) pins a task and stores the assigned-issues answer.
    internal sealed class PaperclipTaskPicker
    {
        private readonly string _agentId;
        private readonly Func<PaperclipSessions.ChatView> _view;
        private readonly Func<IReadOnlyList<PaperclipAgent>> _roster;
        private readonly object _gate = new();
        private IReadOnlyList<PaperclipChat.AssignedIssue>? _assigned;
        private PaperclipChat.TaskRef? _focus;

        internal PaperclipTaskPicker(string agentId, Func<PaperclipSessions.ChatView> view,
            Func<IReadOnlyList<PaperclipAgent>> roster)
        {
            _agentId = agentId;
            _view = view;
            _roster = roster;
        }

        // The live read behind rung 4: this agent's assigned issues, through the feed's own client. Null while
        // Paperclip is off, and null when the read failed, so "none" and "could not ask" stay apart.
        internal static async Task<IReadOnlyList<PaperclipChat.AssignedIssue>?> ReadAssignedLiveAsync(string agentId, CancellationToken ct)
        {
            var live = PaperclipSessions.LiveChatApi();
            return live is { } l
                ? await PaperclipChatFeed.ReadAssignedAsync(l.Api, l.CompanyId, agentId, ct).ConfigureAwait(false)
                : null;
        }

        // Rung 4's input: read once on open, never polled.
        internal void SetAssigned(IReadOnlyList<PaperclipChat.AssignedIssue> read)
        {
            lock (_gate) _assigned = read;
        }

        // How long a send that finds no task waits for the first assigned-issues read before it opens one. The read
        // measured 79 ms for one agent; this is generous, and it is a bound, not a retry.
        internal static readonly TimeSpan DefaultAssignedWait = TimeSpan.FromSeconds(3);

        // The choice for a send. No task known is not "no task" while rung 4 is still being read: an agent whose
        // only task has no live, blocked or finished run is found only by that read, and a message sent first would
        // open a second task for it. So wait for the read to finish or fail, bounded, and decide again.
        internal async Task<PaperclipChat.TaskRef?> PickWaitingAsync(Task? assignedRead, TimeSpan wait)
        {
            var task = Pick();
            if (task != null || assignedRead is not { IsCompleted: false }) return task;
            using var stop = new CancellationTokenSource();
            await Task.WhenAny(assignedRead, Task.Delay(wait, stop.Token)).ConfigureAwait(false);
            stop.Cancel();
            return Pick();
        }

        // The task a message just opened: it is the thread's task until the agent runs on another.
        internal void Pin(PaperclipChat.TaskRef task)
        {
            lock (_gate) _focus = task;
        }

        internal PaperclipChat.TaskRef? Pick()
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
            if (task != null && string.IsNullOrWhiteSpace(task.Title)
                && _roster().FirstOrDefault(a => a.Id == _agentId)?.Task is { Length: > 0 } title)
                task = task with { Title = title };
            return task;
        }

        // What the feed reads this tick: the task, and the newest run on it (the live one, or the newest
        // finished).
        internal ChatTarget Target()
        {
            var task = Pick();
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
    }
}

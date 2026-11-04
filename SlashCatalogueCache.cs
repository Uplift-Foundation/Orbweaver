using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // The slash catalogue for a (CLI, cwd), scanned off the caller's thread and
    // cached, so no caller ever waits on the filesystem longer than it chose to.
    //
    // CB-306's second reopen is why this exists. The roster a peer asks for
    // carried each session's catalogue, and SlashCommandCatalog read it
    // synchronously — `.claude/skills` in the cwd and every parent up to `/` —
    // on the PeerLink pump thread, with no timeout. On the Mac mini a session
    // whose cwd was under ~/Documents made that `opendir` never return (a TCC
    // check a locked screen cannot answer, or an iCloud placeholder; which one
    // is unconfirmed), and dotnet-stack showed three pump threads parked in
    // FileSystemEnumerator.Init under HelloAsync. Every HELLO after that hung,
    // and the MacBook stopped receiving the mini's roster at all. One folder
    // nobody could read took the whole link down.
    //
    // So the scan runs on its own worker and the answer is whatever has
    // already been read: the cached catalogue when there is one (a stale one
    // too, while a refresh runs behind it), and the CLI's built-ins when there
    // is not yet — built-ins, not nothing, because the near side treats a name
    // as a command only if the roster lists it, and an empty list would send
    // "/compact" to the far model as prose.
    //
    // A scan that hangs is left hanging rather than abandoned and retried:
    // there is no cancelling a blocked opendir, so starting another would only
    // pin another thread on the same folder every time someone asked. One scan
    // per key while it is in flight, and a ceiling on how many are in flight
    // at once, bound what a pathological machine can cost to MaxInFlight
    // parked threads. The ceiling is shared, so enough distinct hung folders
    // stop new scans for everyone; those sessions are offered built-ins, which
    // is the same answer a fresh key gets, and it recovers on its own the
    // moment any stuck scan returns.
    internal sealed class SlashCatalogueCache
    {
        internal sealed record Seams(
            // The real scan. May block for ever; may throw.
            Func<SessionSource, string, IReadOnlyList<SlashCommand>> Scan,
            // What to answer before a scan has: no IO, ever.
            Func<SessionSource, IReadOnlyList<SlashCommand>> Builtins,
            Func<DateTime> Now,
            // Runs the scan somewhere that is not the caller.
            Action<Action> StartWorker);

        // How long a scanned catalogue is trusted before a refresh is started
        // behind it. A user who adds a skill sees it within this much of the
        // next ask; the old code rescanned on every hello (ten seconds), which
        // was disk IO for an answer that almost never changes.
        internal static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(1);

        internal const int MaxInFlight = 8;

        // What a lookup came to: the commands, and whether they are a scan's
        // answer (true) or the built-ins standing in for one (false), so a
        // caller that memoizes can decline to memoize a stand-in.
        internal readonly record struct Lookup(IReadOnlyList<SlashCommand> Commands, bool Scanned);

        private sealed class Entry
        {
            public IReadOnlyList<SlashCommand>? Value;
            public DateTime At;
            public TaskCompletionSource? Scan;
            public DateTime ScanStarted;
        }

        private readonly Seams _seams;
        private readonly object _gate = new();
        private readonly Dictionary<(SessionSource, string), Entry> _entries = new();
        private int _inFlight;

        public SlashCatalogueCache(Seams seams) => _seams = seams;

        // The process's own: SlashCommandCatalog on a dedicated background
        // thread, never a pool thread — a scan parked in the kernel would
        // otherwise hold a pool worker the rest of the app is counting on.
        internal static readonly SlashCatalogueCache Shared = new(new Seams(
            SlashCommandCatalog.For,
            SlashCommandCatalog.BuiltinsFor,
            () => DateTime.UtcNow,
            work => new Thread(() => work()) { IsBackground = true, Name = "slash-catalogue" }.Start()));

        internal int InFlight { get { lock (_gate) return _inFlight; } }

        // Never waits. What the roster path uses.
        public IReadOnlyList<SlashCommand> Peek(SessionSource source, string? cwd) =>
            For(source, cwd, TimeSpan.Zero).Commands;

        // Waits at most `wait` for a first scan, measured from when that scan
        // started rather than from this call, so a folder that hangs costs a
        // caller the wait once and every later caller nothing at all.
        public Lookup For(SessionSource source, string? cwd, TimeSpan wait)
        {
            // Codex and Grok read only the home directory, so their catalogue
            // does not depend on the cwd and one key serves every session.
            var key = (source, source is SessionSource.Codex or SessionSource.Grok ? "" : cwd ?? "");
            var now = _seams.Now();
            TaskCompletionSource? pending;
            TimeSpan remaining;
            bool start;

            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry();

                start = entry.Scan is null
                        && (entry.Value is null || now - entry.At >= RefreshAfter)
                        && _inFlight < MaxInFlight;
                if (start)
                {
                    entry.Scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    entry.ScanStarted = now;
                    _inFlight++;
                }

                pending = entry.Value is null ? entry.Scan : null;
                remaining = pending is null ? TimeSpan.Zero : entry.ScanStarted + wait - now;
            }

            if (start) _seams.StartWorker(() => Run(key));

            if (pending is not null && remaining > TimeSpan.Zero) pending.Task.Wait(remaining);

            lock (_gate)
            {
                var value = _entries[key].Value;
                return value is null ? new Lookup(_seams.Builtins(source), false) : new Lookup(value, true);
            }
        }

        // For a caller that keeps its own copy (a local panel, which used to
        // scan once per session): `kept` is filled only with a real scan's
        // answer, so a stand-in is never remembered in place of one.
        internal IReadOnlyList<SlashCommand> Remembered(
            ref IReadOnlyList<SlashCommand>? kept, SessionSource source, string? cwd, TimeSpan wait)
        {
            if (kept is not null) return kept;
            var found = For(source, cwd, wait);
            if (found.Scanned) kept = found.Commands;
            return found.Commands;
        }

        private void Run((SessionSource Source, string Cwd) key)
        {
            IReadOnlyList<SlashCommand> value;
            // A scan that throws is answered with the built-ins and cached like
            // any other answer, so a folder that faults is retried at the
            // refresh interval rather than on every ask.
            try { value = _seams.Scan(key.Source, key.Cwd); }
            catch { value = _seams.Builtins(key.Source); }

            TaskCompletionSource done;
            lock (_gate)
            {
                var entry = _entries[key];
                entry.Value = value;
                entry.At = _seams.Now();
                done = entry.Scan!;
                entry.Scan = null;
                _inFlight--;
            }
            done.SetResult();
        }
    }
}

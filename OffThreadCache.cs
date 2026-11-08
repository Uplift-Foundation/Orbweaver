using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // An answer read off the filesystem, computed on a worker and cached, so no
    // caller ever waits on the disk longer than it chose to — zero, for the
    // peer pump (CB-306's second reopen).
    //
    // The pump answered a HELLO by reading each session's cwd and every parent
    // up to `/`, synchronously and with no timeout. On the Mac mini one cwd
    // under ~/Documents made that opendir never return (a TCC check a locked
    // screen cannot answer, or an iCloud placeholder; which one is
    // unconfirmed), and dotnet-stack showed three pump threads parked in
    // FileSystemEnumerator.Init under HelloAsync. Every HELLO after that hung
    // and the MacBook stopped receiving the mini's roster at all. One folder
    // nobody could read took the whole link down.
    //
    // So the read runs on its own worker and a caller is answered with what is
    // already known: the cached value when there is one (a stale one too,
    // while a refresh runs behind it), and the caller's stand-in when there is
    // not yet.
    //
    // A read that hangs is left hanging rather than abandoned and retried:
    // there is no cancelling a blocked opendir, so starting another would only
    // pin another thread on the same folder every time someone asked. One read
    // per key while it is in flight, and a ceiling on how many are in flight
    // at once, bound what a pathological machine can cost to MaxInFlight
    // parked threads. The ceiling is per cache, so enough distinct hung folders
    // stop new reads for that cache; those keys get the stand-in, which is
    // what a fresh key gets anyway, and it recovers on its own the moment any
    // stuck read returns.
    internal static class OffThread
    {
        // A dedicated background thread, never a pool thread: a read parked in
        // the kernel would otherwise hold a pool worker the rest of the app is
        // counting on.
        internal static void OnOwnThread(Action work) =>
            new Thread(() => work()) { IsBackground = true, Name = "off-thread-cache" }.Start();
    }

    internal sealed class OffThreadCache<TKey, TValue> where TKey : notnull
    {
        internal sealed record Seams(
            // The real read. May block for ever; may throw.
            Func<TKey, TValue> Read,
            // What a key is answered with before its read has finished, and
            // what a read that throws is cached as. No IO, ever.
            Func<TKey, TValue> StandIn,
            Func<DateTime> Now,
            // Runs the read somewhere that is not the caller.
            Action<Action> StartWorker);

        internal const int MaxInFlight = 8;

        // What a lookup came to: the value, and whether it is a read's answer
        // (true) or the stand-in (false), so a caller that memoizes can decline
        // to memoize a stand-in.
        internal readonly record struct Lookup(TValue Value, bool Read);

        private sealed class Entry
        {
            public bool Known;
            public TValue Value = default!;
            public DateTime At;
            public TaskCompletionSource? Reading;
            public DateTime ReadStarted;
        }

        private readonly Seams _seams;
        private readonly TimeSpan _refreshAfter;
        private readonly object _gate = new();
        private readonly Dictionary<TKey, Entry> _entries;
        private int _inFlight;

        public OffThreadCache(Seams seams, TimeSpan refreshAfter, IEqualityComparer<TKey>? comparer = null)
        {
            _seams = seams;
            _refreshAfter = refreshAfter;
            _entries = new Dictionary<TKey, Entry>(comparer);
        }

        internal int InFlight { get { lock (_gate) return _inFlight; } }

        // Never waits.
        public TValue Peek(TKey key) => For(key, TimeSpan.Zero).Value;

        // Waits at most `wait` for a first read, measured from when that read
        // started rather than from this call, so a folder that hangs costs a
        // caller the wait once and every later caller with the same wait
        // nothing at all.
        public Lookup For(TKey key, TimeSpan wait)
        {
            var now = _seams.Now();
            TaskCompletionSource? pending;
            TimeSpan remaining;
            bool start;

            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry();

                start = entry.Reading is null
                        && (!entry.Known || now - entry.At >= _refreshAfter)
                        && _inFlight < MaxInFlight;
                if (start)
                {
                    entry.Reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    entry.ReadStarted = now;
                    _inFlight++;
                }

                pending = entry.Known ? null : entry.Reading;
                remaining = pending is null ? TimeSpan.Zero : entry.ReadStarted + wait - now;
            }

            if (start) _seams.StartWorker(() => Run(key));

            if (pending is not null && remaining > TimeSpan.Zero) pending.Task.Wait(remaining);

            lock (_gate)
            {
                var entry = _entries[key];
                return entry.Known ? new Lookup(entry.Value, true) : new Lookup(_seams.StandIn(key), false);
            }
        }

        private void Run(TKey key)
        {
            TValue value;
            // A read that throws is answered with the stand-in and cached like
            // any other answer, so a folder that faults is retried at the
            // refresh interval rather than on every ask.
            try { value = _seams.Read(key); }
            catch { value = _seams.StandIn(key); }

            TaskCompletionSource done;
            lock (_gate)
            {
                var entry = _entries[key];
                entry.Value = value;
                entry.Known = true;
                entry.At = _seams.Now();
                done = entry.Reading!;
                entry.Reading = null;
                _inFlight--;
            }
            done.SetResult();
        }
    }
}

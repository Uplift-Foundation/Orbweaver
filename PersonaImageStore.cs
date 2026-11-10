namespace Orbweaver
{
    // The persona pictures this desktop can offer a phone right now, by the
    // path the persona names and by the hash the wire names (CB-321).
    //
    // Offer never reads or decodes anything: it stats the file (the scan already
    // stats every persona's picture each tick, see Persona.Watched) and either
    // answers with the hash of a finished bundle or starts one building on a
    // worker and answers null. So the roster offers an image_hash only once the
    // bundle behind it exists, and the phone's request is answered from that
    // bundle — a request never waits on a decode, and an image never reaches the
    // wire as anything but bounded windows (CB-322).
    //
    // Bounded (frames-v5 desktop_rules.offer: eight bundles or 32 MB, oldest
    // out). Forgetting a bundle costs one rebuild the next time its path is
    // offered, and a phone that asks for a forgotten hash is told unknown_image,
    // which is what the contract says the phone should expect.
    internal sealed class PersonaImageStore
    {
        internal const int Capacity = 8;
        internal const long MaxBytes = 32L * 1024 * 1024;

        private enum State { Building, Ready, Failed }

        private sealed record Entry(long Length, DateTime Written, State State, string? Hash);

        private readonly Func<string, (long Length, DateTime Written)?> _stat;
        private readonly Func<string, byte[]?> _read;
        private readonly Func<byte[], PersonaImageBundle?> _build;
        private readonly Action<Action> _startWorker;
        private readonly Action<string> _log;
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PersonaImageBundle> _byHash = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _age = new();

        internal PersonaImageStore(
            Func<string, (long Length, DateTime Written)?> stat, Func<string, byte[]?> read,
            Func<byte[], PersonaImageBundle?> build, Action<Action> startWorker, Action<string> log)
        {
            _stat = stat;
            _read = read;
            _build = build;
            _startWorker = startWorker;
            _log = log;
        }

        // The app's: the real file system, Skia and a thread of its own.
        internal static PersonaImageStore Shared { get; } = new(
            PeerAvatarStore.StatFile, PersonaFiles.ReadAvatarFile, b => PersonaImageBundle.Build(b),
            OffThread.OnOwnThread, line => MirrorLog.Say("relay", line));

        internal int Count { get { lock (_gate) return _byHash.Count; } }

        internal long TotalBytes { get { lock (_gate) return _byHash.Values.Sum(b => b.Bytes); } }

        // The hash a roster may offer for the picture at path, or null while it
        // is building, when it cannot be offered at all, or when there is no
        // picture. A changed file (length or mtime) is rebuilt.
        internal string? Offer(string? path)
        {
            if (string.IsNullOrEmpty(path) || _stat(path) is not { } stat) return null;
            lock (_gate)
            {
                if (_byPath.TryGetValue(path, out var e) && e.Length == stat.Length && e.Written == stat.Written)
                {
                    switch (e.State)
                    {
                        case State.Building:
                        case State.Failed:
                            return null;
                        case State.Ready when _byHash.ContainsKey(e.Hash!):
                            return e.Hash;
                    }
                }
                _byPath[path] = new Entry(stat.Length, stat.Written, State.Building, null);
            }
            _startWorker(() => BuildFor(path, stat));
            return null;
        }

        // The bundle offered under a hash, or null for one this desktop never
        // offered or has since forgotten.
        internal PersonaImageBundle? ById(string hash)
        {
            lock (_gate) return _byHash.GetValueOrDefault(hash);
        }

        private void BuildFor(string path, (long Length, DateTime Written) stat)
        {
            PersonaImageBundle? bundle = null;
            try
            {
                if (_read(path) is { } bytes) bundle = _build(bytes);
            }
            // A Skia failure on a picture it accepted the header of: the same
            // answer as an unreadable one — the disc — rather than a dead worker
            // and a path stuck building for ever.
            catch (Exception ex)
            {
                _log($"persona_image build threw {ex.GetType().Name}");
            }

            lock (_gate)
            {
                // A newer stat has superseded this build; its own build answers.
                // Entries are never removed, only replaced, so the path is here.
                var e = _byPath[path];
                if (e.Length != stat.Length || e.Written != stat.Written) return;
                if (bundle is null)
                {
                    _byPath[path] = e with { State = State.Failed };
                    _log("persona_image not offered: unreadable, over the cap, or no frames fit");
                    return;
                }
                _byPath[path] = e with { State = State.Ready, Hash = bundle.Hash };
                if (_byHash.ContainsKey(bundle.Hash)) _age.Remove(bundle.Hash);
                _byHash[bundle.Hash] = bundle;
                _age.AddLast(bundle.Hash);
                while (_byHash.Count > 1 && (_byHash.Count > Capacity || _byHash.Values.Sum(b => b.Bytes) > MaxBytes))
                {
                    _byHash.Remove(_age.First!.Value);
                    _age.RemoveFirst();
                }
            }
            _log($"persona_image {PersonaFrames.Short(bundle.Hash)} ready frames {bundle.Frames.Count} {bundle.W}x{bundle.H} {bundle.Bytes}B");
        }
    }
}

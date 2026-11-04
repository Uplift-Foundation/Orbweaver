using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // The persona a roster entry carries, read through OffThreadCache so the
    // peer pump never opens a persona file itself (CB-306's second reopen).
    //
    // The slash catalogue was what froze the mini's link, but LocalPersona
    // walks the very same folders — CLAUDE.md and the persona files in the cwd
    // and every parent — and ran right after it for the same entry, so it only
    // escaped the stack because the catalogue blocked first. A session whose
    // cwd hangs is offered no persona until the read answers, which is what
    // an asker already handles for a session that has none; the roster hash
    // then changes once and the persona arrives on the next HELLO.
    internal sealed class PeerPersonaCache
    {
        // Short enough that a persona someone has just edited reaches the far
        // orb within a minute or so of the next ask.
        internal static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);

        // Everything the read depends on. The config dirs are part of it
        // because one cwd resolves differently under a different account.
        internal readonly record struct Key(SessionSource Source, string Cwd, string Agent, string Dirs);

        private readonly OffThreadCache<Key, RemoteMirrorServer.PersonaRead?> _cache;
        private readonly Func<string, byte[]?> _knownById;

        public PeerPersonaCache(
            Func<SessionSource, string, string, IReadOnlyList<string>, RemoteMirrorServer.PersonaRead?> read,
            Func<DateTime> now, Action<Action> startWorker, Func<string, byte[]?> knownById)
        {
            _cache = new(new(
                key => read(key.Source, key.Cwd, key.Agent, key.Dirs.Length == 0 ? Array.Empty<string>() : key.Dirs.Split('\0')),
                _ => null,
                now,
                startWorker), RefreshAfter);
            _knownById = knownById;
        }

        internal static readonly PeerPersonaCache Shared = new(
            (source, cwd, agent, dirs) => RemoteMirrorServer.ReadPeerPersona(source, cwd, agent, dirs),
            () => DateTime.UtcNow,
            OffThread.OnOwnThread,
            PeerAvatarStore.Shared.ById);

        internal int InFlight => _cache.InFlight;

        // Never waits: the cached persona, or none while it is being read.
        public MirrorProtocol.PeerPersona? Peek(SessionStatus status, IReadOnlyList<string> userConfigDirs, bool picturesById)
        {
            if (!status.IsLocalCli || string.IsNullOrWhiteSpace(status.Cwd)) return null;
            var key = new Key(status.Source, status.Cwd, status.Agent, string.Join('\0', userConfigDirs));
            return RemoteMirrorServer.ToPeerPersona(_cache.Peek(key), picturesById, _knownById);
        }
    }
}

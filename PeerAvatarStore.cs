using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Orbweaver
{
    // Persona pictures the mirror has offered, by path and by id (CB-216).
    //
    // Two jobs, both about the same measured cost. A roster is built for every
    // peer's ask every ten seconds, and the pictures in it were animated GIFs of
    // 11-15 MB: reading and hashing each of them on every build was most of the
    // work in answering. So a picture is kept against the file's length and
    // modification time, and read again only when either changes, which is also
    // what makes a changed picture reach the peer promptly: the next roster names
    // a new id.
    //
    // The second job is answering AVATAR, which names a picture by id: only an id
    // this machine has offered is answered, from the bytes it offered under it.
    //
    // Bounded, because pictures are large and sessions come and go all day. The
    // oldest entry goes first; forgetting one costs a single re-read the next
    // time it is offered, never a wrong answer.
    internal sealed class PeerAvatarStore
    {
        internal readonly record struct Picture(byte[] Bytes, string Id);

        private readonly record struct Kept(long Length, DateTime Written, Picture Picture);

        internal const int Capacity = 16;

        private readonly Func<string, (long Length, DateTime Written)?> _stat;
        private readonly Func<string, byte[]?> _read;
        private readonly object _gate = new();
        private readonly Dictionary<string, Kept> _byPath = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _age = new();

        internal PeerAvatarStore(
            Func<string, (long Length, DateTime Written)?> stat, Func<string, byte[]?> read)
        {
            _stat = stat;
            _read = read;
        }

        // The one every server shares. Process-wide, since the pictures are this
        // machine's files whichever account's server offers them.
        internal static PeerAvatarStore Shared { get; } = new(StatFile, PersonaFiles.ReadAvatarFile);

        // The picture at a path, read only if the file changed since it was last
        // kept. Null when it cannot be read, which PersonaFiles has already
        // reported the reason for.
        internal Picture? At(string path)
        {
            var stat = _stat(path);
            if (stat is null) return null;

            lock (_gate)
            {
                if (_byPath.TryGetValue(path, out var kept)
                    && kept.Length == stat.Value.Length && kept.Written == stat.Value.Written)
                {
                    return kept.Picture;
                }
            }

            var bytes = _read(path);
            if (bytes is null) return null;

            var picture = new Picture(bytes, MirrorProtocol.AvatarIdOf(bytes));

            lock (_gate)
            {
                if (_byPath.ContainsKey(path)) _age.Remove(path);
                _byPath[path] = new Kept(stat.Value.Length, stat.Value.Written, picture);
                _age.AddLast(path);

                while (_byPath.Count > Capacity)
                {
                    _byPath.Remove(_age.First!.Value);
                    _age.RemoveFirst();
                }
            }

            return picture;
        }

        // The bytes offered under an id, or null for an id this machine has not
        // offered or has since forgotten.
        internal byte[]? ById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            lock (_gate)
            {
                return _byPath.Values
                    .Select(kept => kept.Picture)
                    .Where(picture => string.Equals(picture.Id, id, StringComparison.Ordinal))
                    .Select(picture => picture.Bytes)
                    .FirstOrDefault();
            }
        }

        // Excluded from coverage: a FileInfo, and nothing decided.
        [ExcludeFromCodeCoverage]
        internal static (long, DateTime)? StatFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }
}

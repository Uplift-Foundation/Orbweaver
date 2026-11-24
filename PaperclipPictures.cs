using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // The pictures of a company's Paperclip agents (CB-445), kept as files.
    //
    // Why files: the phone's picture path (PersonaImageStore.Offer) and the
    // desktop's (OpenClawAvatars.ForFile) both take a path, so a picture that
    // lives in a file reaches the orb, the panel header and the phone's roster
    // through code that already exists and needs no new contract.
    //
    // Why the url is the key: Paperclip gives every upload a new asset id, so a
    // changed picture is a changed url and is fetched again, and an unchanged one
    // is never fetched twice. The generated avatars are rendered on demand
    // (540 ms measured) so they are worth keeping too.
    //
    // A picture that cannot be had is no picture: FileFor answers null and the
    // orb keeps its initials. A failure never reaches the link state, because a
    // refused asset must not read as a refused key for the whole company.
    internal sealed class PaperclipPictureCache
    {
        internal const int Capacity = 64;
        internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

        private readonly string _dir;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _age = new();
        private readonly Dictionary<string, DateTime> _failedAt = new(StringComparer.Ordinal);
        private bool _pruned;

        internal PaperclipPictureCache(string dir, Func<DateTime>? now = null)
        {
            _dir = dir;
            _now = now ?? (() => DateTime.UtcNow);
        }

        // The app's: under the settings directory, which is per user.
        internal static PaperclipPictureCache Shared { get; } =
            new(Path.Combine(OrbweaverSettings.Directory, "paperclip-avatars"));

        // Only a path under /api/ is ever fetched. The url comes out of a
        // response, so it is untrusted: an absolute url, a scheme-relative one, a
        // traversal or a control character would send the board key somewhere
        // Paperclip did not mean, and gets initials instead.
        internal static bool IsFetchable(string? url) =>
            url is { Length: > 5 and <= 512 }
            && url.StartsWith("/api/", StringComparison.Ordinal)
            && !url.Contains("..", StringComparison.Ordinal)
            && !url.Contains('\\') && !url.Contains("//", StringComparison.Ordinal)
            && !url.Any(char.IsControl) && !url.Any(char.IsWhiteSpace);

        internal static bool LooksLikePng(byte[] b) =>
            b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

        internal int Count { get { lock (_gate) return _files.Count; } }

        internal string? FileFor(string? url)
        {
            if (url is null) return null;
            lock (_gate) return _files.GetValueOrDefault(url);
        }

        internal string PathFor(string url) =>
            Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant() + ".png");

        // Fetches whatever the agents name that is not already held, all at once
        // so a company of eight agents is one wait and not eight. Never throws
        // for a fetch or a file: those are logged and retried after RetryAfter.
        internal async Task SyncAsync(IEnumerable<string?> urls, IPaperclipApi api, CancellationToken ct, Action<string>? log = null)
        {
            var wanted = urls.Where(IsFetchable).Select(u => u!).Distinct(StringComparer.Ordinal).ToList();
            PruneOnce();
            List<string> todo;
            var now = _now();
            lock (_gate)
                todo = wanted.Where(u => !_files.ContainsKey(u)
                    && !(_failedAt.TryGetValue(u, out var at) && now - at < RetryAfter)).ToList();
            if (todo.Count == 0) return;

            var fetched = await Task.WhenAll(todo.Select(u => FetchAsync(u, api, ct, log))).ConfigureAwait(false);
            lock (_gate)
            {
                for (var i = 0; i < todo.Count; i++)
                {
                    if (fetched[i] is { } file)
                    {
                        _failedAt.Remove(todo[i]);
                        _files[todo[i]] = file;
                        _age.AddLast(todo[i]);
                    }
                    else _failedAt[todo[i]] = now;
                }
                // The oldest goes first, and never one still named by an agent.
                for (var node = _age.First; node != null && _files.Count > Capacity;)
                {
                    var next = node.Next;
                    if (!wanted.Contains(node.Value))
                    {
                        TryDelete(_files[node.Value]);
                        _files.Remove(node.Value);
                        _age.Remove(node);
                    }
                    node = next;
                }
            }
        }

        private async Task<string?> FetchAsync(string url, IPaperclipApi api, CancellationToken ct, Action<string>? log)
        {
            try
            {
                var r = await api.GetBytesAsync(url, ct).ConfigureAwait(false);
                if (!r.IsOk) return null;
                if (!string.Equals(r.ContentType, "image/png", StringComparison.OrdinalIgnoreCase) || !LooksLikePng(r.Body))
                {
                    log?.Invoke("a picture was not a PNG; keeping initials");
                    return null;
                }
                Directory.CreateDirectory(_dir);
                var path = PathFor(url);
                var tmp = path + ".part";
                var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var fs = new FileStream(tmp, opts))
                    await fs.WriteAsync(r.Body, ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke("a picture could not be kept (" + ex.GetType().Name + "); keeping initials");
                return null;
            }
        }

        // Files left by an earlier run are dropped once: the cache starts empty
        // each launch, so they would never be named again.
        private void PruneOnce()
        {
            lock (_gate) { if (_pruned) return; _pruned = true; }
            try
            {
                if (!Directory.Exists(_dir)) return;
                foreach (var f in Directory.EnumerateFiles(_dir)) TryDelete(f);
            }
            catch (Exception) { /* a directory we cannot list is just not pruned */ }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { /* held open by a reader: the next prune gets it */ }
        }
    }
}

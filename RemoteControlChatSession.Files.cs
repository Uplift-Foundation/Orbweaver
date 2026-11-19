using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // CB-363: opening a file a remote session's chat linked to.
    //
    // The file is on the far machine, so it is fetched over the peer link
    // (FILE), kept in PeerFileCache, and the cache path is what gets opened.
    // This side decides nothing about whether that is safe — the far side
    // enforced its roots, and ChatFileLinks runs FileLinkPolicy on the cached
    // file before anything starts. What lives here is only the mapping from
    // "what the transport answered" to a ChatFileResult, behind seams so each
    // arm is a test rather than a far machine misbehaving on cue.
    internal sealed partial class RemoteControlChatSession : IRemoteChatFiles
    {
        public ChatFileHost FileHost => ChatFileHost.Peer;

        public string? FileHostName => MachineName;

        // Whether the machine serving this session said it serves FILE.
        // False for a Buddy that predates the frame, which is how an old peer
        // is told to update instead of being sent a request nobody answers.
        internal Func<string, bool>? PeerSpeaksFiles { get; set; }

        // The request itself: (session route, far path, `have` stamp of the
        // newest cached copy or null, progress, ct).
        internal Func<string, string, string?, IProgress<(long Got, long Total)>?, CancellationToken, Task<PeerFileResult>>? FetchPeerFile { get; set; }

        internal PeerFileCache FileCache { get; set; } = PeerFileCache.Shared;

        public async Task<ChatFileResult> FetchFileAsync(
            string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        {
            var speaks = PeerSpeaksFiles ?? (name => RemoteControlSessions.MirrorClientFor(_account)?.SpeaksFiles(name) ?? false);
            if (!speaks(_remoteName)) return ChatFileResult.Refused("update");

            var fetch = FetchPeerFile ?? ((name, p, have, prog, token) =>
                RemoteControlSessions.MirrorClientFor(_account) is { } client
                    ? client.FetchFileAsync(name, p, have, prog, token)
                    : Task.FromResult<PeerFileResult>(new PeerFileResult.Error(MirrorProtocol.ErrNoSession, null)));

            // The newest copy already here is offered as `have`: when the far
            // file still has that stamp the answer is "same" and no bytes
            // cross. The far side echoes its executable bit on that answer,
            // because the cached copy is written without one and could not
            // otherwise tell a script from a document on a repeat click.
            var machine = MachineName ?? _account;
            var cached = FileCache.Newest(machine, path);
            var reply = await fetch(_remoteName, path, cached?.Have, progress, ct).ConfigureAwait(true);

            return Map(reply, path, machine, cached);
        }

        private ChatFileResult Map(PeerFileResult reply, string path, string machine, PeerFileCache.CachedFile? cached)
        {
            if (reply is PeerFileResult.Bytes bytes)
            {
                var stored = FileCache.Store(machine, path, bytes.MtimeMs, bytes.Data, machine);
                return stored is null
                    ? ChatFileResult.Refused("failed")
                    : ChatFileResult.Cached(stored.LocalPath, bytes.Exec);
            }

            // "Same" is only believable against a copy that was offered; one
            // answered when nothing was sent is refused rather than guessed
            // at. Touched, so the 30-day sweep counts from last use and a copy
            // someone opens daily is not swept.
            if (reply is PeerFileResult.Same same)
            {
                if (cached is null) return ChatFileResult.Refused("failed");
                FileCache.Touch(cached);
                return ChatFileResult.Cached(cached.LocalPath, same.Exec);
            }

            return reply is PeerFileResult.Error error
                ? ChatFileResult.Refused(RefusalCodeOf(error.Code))
                : ChatFileResult.Refused("failed");
        }

        // The wire's codes that mean something a reader can act on pass
        // through under the same name; the transport's own failures
        // (no-session, unreachable, bad-hash, unsupported, bad_frame) are all
        // "couldn't fetch it".
        internal static string RefusalCodeOf(string code) => code switch
        {
            MirrorProtocol.ErrForbiddenPath => "forbidden_path",
            MirrorProtocol.ErrNotFound => "not_found",
            MirrorProtocol.ErrTooLarge => "too_large",
            MirrorProtocol.ErrUnreadable => "unreadable",
            MirrorProtocol.ErrRateLimited => "rate_limited",
            MirrorProtocol.ErrBusy => "busy",
            _ => "failed"
        };
    }
}

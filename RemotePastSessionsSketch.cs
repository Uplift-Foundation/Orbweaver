using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // CB-434 desktop UI seam (celeste's amendment 1 keeps IRemotePastSessions as
    // its own interface). The protocol engineer replaces this file with the real
    // implementation's types; the window depends only on this shape.
    //
    // The whole list for one CLI, account and window comes back (the far desk
    // is asked for up to 2,000 rows) and the window filters, searches and groups
    // it with PastSessions.List. IsLive and FolderMissing are the far desk's.
    internal enum RemoteListOutcome { Ok, NeedsUpdate, Unreachable }

    internal enum RemoteResumeOutcome { Launched, AlreadyRunning, Unsupported, NeedsUpdate, Unreachable, Failed }

    internal sealed record RemoteListResult(
        RemoteListOutcome Outcome, IReadOnlyList<PastSessionRow> Rows, string? Why, int? Total = null);

    internal sealed record RemoteResumeResult(
        RemoteResumeOutcome Outcome, string? Message, bool IsCodex, string? SessionId = null);

    internal interface IRemotePastSessions
    {
        Task<RemoteListResult> ListAsync(
            string machine, NewChatCli cli, string profile, bool showOlder, CancellationToken ct);

        // `cwd` is the row's folder as the far desk gave it: it is in the signed
        // body and the far desk checks it against its own index.
        Task<RemoteResumeResult> ResumeAsync(
            string machine, NewChatCli cli, string sessionId, string cwd, string profile, CancellationToken ct);
    }
}

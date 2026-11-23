using System.Text.Json.Serialization;

namespace Orbweaver
{
    // CB-434: resuming a past session on another desktop (and, over the relay,
    // from the phone).
    //
    // Types only, pushed first so New Chat's remote Resume list (which asks),
    // and the peer client and server (which carry and answer) can be built
    // against one shape in parallel — the same move PeerLaunchContract made
    // for CB-357. Nothing here does work.

    // ---- what the window sees ----------------------------------------------------

    // The seam New Chat's Resume mode uses when another machine is selected.
    // The real one is the peer launch client (PeerSessions.Launcher), which
    // also implements IPeerLauncher; a separate interface so a launcher that
    // cannot list is not forced to pretend.
    //
    // The whole list for one CLI, account and window comes back and the window
    // filters, searches and groups it with PastSessions.List exactly as it does
    // a local one. The far desk marks IsLive and FolderMissing itself; the rows'
    // TranscriptPath is "" (a far desk's path means nothing here) and
    // MessageCount is null — this side never reads a transcript it does not have.
    //
    // Neither method throws: every failure is an outcome.
    internal interface IRemotePastSessions
    {
        // profile is the wire account string: RelayAccounts' default id or a
        // profile id, always explicit, as PeerLaunchSpec.Account is.
        Task<RemoteListResult> ListAsync(string machine, NewChatCli cli, string profile, bool showOlder, CancellationToken ct);

        Task<RemoteResumeResult> ResumeAsync(string machine, NewChatCli cli, string sessionId, string profile, CancellationToken ct);
    }

    internal enum RemoteListOutcome
    {
        Ok,
        // The far desk predates CB-434: say "update Orbweaver on <machine>".
        NeedsUpdate,
        // No answer, link lost, or the far desk has not opted in to being
        // launched on (Why says which).
        Unreachable,
    }

    internal sealed record RemoteListResult(RemoteListOutcome Outcome, IReadOnlyList<PastSessionRow> Rows, string? Why);

    internal enum RemoteResumeOutcome
    {
        Launched,
        // The conversation is already running there: the window focuses that
        // machine's orb for SessionId (mirrored orbs keep the far session id).
        AlreadyRunning,
        // The far CLI is too old to resume by id, the folder is gone there, or
        // the session is unknown there; Message is the far desk's wording.
        Unsupported,
        NeedsUpdate,
        Unreachable,
        Failed,
    }

    // IsCodex: the orb appears only after the first prompt, so the window says
    // so, as it does for a local Codex resume.
    internal sealed record RemoteResumeResult(RemoteResumeOutcome Outcome, string? Message, bool IsCodex, string? SessionId = null);

    // ---- what crosses the wire ---------------------------------------------------

    // One past session as it crosses the peer link (and the relay). Not the
    // local PastSession: no transcript path, which discloses the far desk's
    // layout, and the CLI is the wire's string ("claude", "codex", "grok").
    // Title and Cwd are the sensitive fields; they cross only when the far
    // desk has opted in to being launched on (the switch launch-info honours).
    internal sealed record PastSessionWire(
        [property: JsonPropertyName("cli")] string Cli,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("cwd")] string? Cwd,
        [property: JsonPropertyName("title")] string Title,
        // Unix seconds.
        [property: JsonPropertyName("last_activity")] long LastActivity,
        [property: JsonPropertyName("folder_missing")] bool FolderMissing,
        [property: JsonPropertyName("live")] bool Live);

    internal static class PeerResumeCodes
    {
        // The conversation is already running on the far desk.
        public const string AlreadyRunning = "already_running";

        // The far desk has no such session in its index for that CLI and
        // account (or it is one it will not resume: headless, OpenClaw-written).
        public const string UnknownSession = "unknown_session";

        // The far desk's CLI is too old to resume by id (ResumeSupport).
        public const string CliTooOld = "cli_too_old";

        // The session's folder is gone on the far desk; never resumed
        // elsewhere instead.
        public const string FolderMissing = "folder_missing";

        // Client-side: the far desk predates CB-434.
        public const string Unsupported = "resume_unsupported";
    }
}

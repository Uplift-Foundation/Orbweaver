using System.Text.Json.Serialization;

namespace Orbweaver
{
    // CB-434: resuming a past session on another desktop (and, over the relay,
    // from the phone).
    //
    // Types only, pushed first so New Chat's remote Resume list (which asks),
    // and the peer client and server (which carry and answer) can be built
    // against one shape in parallel — the same move PeerLaunchContract made
    // for CB-357. Nothing here does work. The shape is provisional until the
    // architect's contract lands; field names follow the phone's snake_case.

    // One past session as it crosses the wire. Deliberately not the local
    // PastSession: no TranscriptPath (a far desk's file path means nothing
    // here and discloses its layout), and the CLI is the wire's string
    // ("claude", "codex", "grok") rather than the enum. Title and Cwd are the
    // sensitive fields; they cross only when the far desk has opted in to
    // being launched on (the same switch launch-info honours).
    internal sealed record PastSessionWire(
        [property: JsonPropertyName("cli")] string Cli,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("cwd")] string? Cwd,
        [property: JsonPropertyName("title")] string Title,
        // Unix seconds.
        [property: JsonPropertyName("last_activity")] long LastActivity,
        [property: JsonPropertyName("folder_missing")] bool FolderMissing,
        // An orb is already running this conversation on that desk: the
        // caller focuses it (or opens its chat) instead of resuming.
        [property: JsonPropertyName("live")] bool Live);

    // The asker's query: the same knobs PastSessions.List takes, plus a page.
    // Account is explicit ("default" or a profile id), as PeerLaunchSpec's is.
    internal sealed record PastSessionQuery(
        string Cli,
        string Account,
        string? Filter = null,
        string? Folder = null,
        bool ShowOlder = false,
        int Offset = 0,
        int Limit = 50);

    // A page of rows, newest first. Folders is the folder filter's choices,
    // sent with the first page only. Total is the count before paging.
    internal sealed record PastSessionPage(
        IReadOnlyList<PastSessionWire> Rows,
        int Total,
        IReadOnlyList<string> Folders);

    // A page, or the one code that refused it: PeerLaunchCodes'
    // (peer_launch_off, no_answer, link_lost), unknown_cli, or
    // PeerResumeCodes.Unsupported for a far desk too old to list.
    internal sealed record PastSessionResult(bool Ok, string Code, PastSessionPage? Page)
    {
        internal static PastSessionResult Fail(string code) => new(false, code, null);
    }

    // What the window hands the client to resume. The far desk looks the id
    // up in its own index, so the folder it resumes in is its own reading of
    // the transcript, never the asker's.
    internal sealed record PeerResumeSpec(string Cli, string SessionId, string Account);

    // The seam the window uses for a remote Resume list. The real one will be
    // PeerSessions.Launcher, which also implements IPeerLauncher; kept apart
    // so a launcher that cannot list is not forced to pretend.
    internal interface IPeerPastSessions
    {
        // Never throws: every failure is a PastSessionResult with a code.
        Task<PastSessionResult> ListPastAsync(string machine, PastSessionQuery query, CancellationToken ct);

        // Never throws. Ok with PeerLaunchCodes.Launched on a start;
        // PeerResumeCodes.AlreadyRunning (SessionId is the live orb's) when
        // the conversation is already open there; otherwise a refusal code.
        Task<PeerLaunchResult> ResumeAsync(string machine, PeerResumeSpec spec, CancellationToken ct);
    }

    internal static class PeerResumeCodes
    {
        // The conversation is already running on the far desk.
        public const string AlreadyRunning = "already_running";

        // The far desk has no such session in its index (or it is outside
        // what it will resume: headless, OpenClaw-written).
        public const string UnknownSession = "unknown_session";

        // The far desk's CLI is too old to resume by id (ResumeSupport).
        public const string CliTooOld = "cli_too_old";

        // The session's folder is gone on the far desk; never resumed
        // elsewhere instead.
        public const string FolderMissing = "folder_missing";

        // Client-side: the far desk predates CB-434 and never answered the
        // listing, so it cannot resume either. Distinct from no_answer so the
        // window can say "update Orbweaver on <machine>" rather than "try again".
        public const string Unsupported = "resume_unsupported";
    }
}

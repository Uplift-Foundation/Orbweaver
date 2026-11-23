using System.Text.Json.Serialization;

namespace Orbweaver
{
    // CB-357: starting a session on another desktop over the peer link.
    //
    // Types only, and pushed first so the window (which asks) and the sender
    // and receiver (which carry and answer) can be built against one shape in
    // parallel. Nothing here does any work; the codec is PeerLaunchWire, the
    // asking side is PeerLaunchClient, and this desk's own answer is built by
    // PeerLaunchInfoSource.

    // What `launch-info` carries. `Allowed` false means the receiving desk has
    // not opted in, and then nothing but `Os` is disclosed: no folders, no
    // accounts, no home directory.
    internal sealed record PeerLaunchInfo(
        [property: JsonPropertyName("allowed")] bool Allowed,
        [property: JsonPropertyName("os")] string Os,
        [property: JsonPropertyName("home")] string? Home = null,
        [property: JsonPropertyName("terminal")] bool? Terminal = null,
        [property: JsonPropertyName("clis")] IReadOnlyList<PeerLaunchCli>? Clis = null,
        [property: JsonPropertyName("accounts")] IReadOnlyList<PeerLaunchAccount>? Accounts = null,
        [property: JsonPropertyName("recents")] IReadOnlyList<string>? Recents = null,
        // CB-434: this desk can list and resume past sessions. An int so a
        // future incompatible change can bump it; 0 (absent) is a desk that
        // predates it, and the window says to update that machine rather than
        // timing a request out.
        [property: JsonPropertyName("resume"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Resume = 0);

    internal sealed record PeerLaunchCli(
        [property: JsonPropertyName("cli")] string Cli,
        [property: JsonPropertyName("installed")] bool Installed);

    internal sealed record PeerLaunchAccount(
        [property: JsonPropertyName("cli")] string Cli,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label);

    // What the window hands the client. `Account` is always explicit
    // ("default" or a profile id) so the phone's designated-account fallback
    // never applies to a peer.
    //
    // CB-434: ResumeSessionId non-empty resumes that past session instead of
    // starting a new chat. Cwd is then the session's folder as its row gave it;
    // the far desk checks it against its own index.
    internal sealed record PeerLaunchSpec(string Cli, string Cwd, string Account)
    {
        internal string ResumeSessionId { get; init; } = "";
    }

    // What came back. `Code` is "launched" on success; otherwise one of the
    // phone's refusal codes, or one of PeerLaunchCodes below.
    //
    // CB-397: `Trust` is set only with PeerLaunchCodes.TrustNeeded.
    internal sealed record PeerLaunchResult(bool Ok, string Code, string? SessionId, string? Why, PeerLaunchTrust? Trust = null);

    // CB-397: the far desk's question, "trust this folder?". The token names
    // the pending launch on that desk; Cli and Cwd are what that desk says it
    // started, which the asking window checks against what it sent. It
    // authorises nothing: only the signed `launch-trust` answer does.
    internal sealed record PeerLaunchTrust(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("cli")] string Cli,
        [property: JsonPropertyName("cwd")] string Cwd,
        [property: JsonPropertyName("expires_at")] long ExpiresAt,
        // Display only and unsigned: the far screen carries the CLI's own
        // warning that the folder configures hooks that run commands. It
        // changes what the dialog says, never what an answer authorises.
        [property: JsonPropertyName("hooks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hooks = false);

    // One folder in a far machine's listing. Path and Drive are set only at
    // the top level (the roots); below it a folder is reached by Name.
    internal sealed record PeerDirItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("path")] string? Path = null,
        [property: JsonPropertyName("drive")] string? Drive = null,
        [property: JsonPropertyName("home")] bool Home = false,
        [property: JsonPropertyName("git")] bool Git = false,
        [property: JsonPropertyName("claude")] bool Claude = false,
        [property: JsonPropertyName("persona")] bool Persona = false);

    // A folder listing as the far desk's own browse rules produced it. Path is
    // empty at the top level; Parent is empty at a root.
    internal sealed record PeerDirListing(
        string Path, string Parent, bool Truncated, IReadOnlyList<PeerDirItem> Items);

    // What `list-dir` carried: a listing, or the one code that refused it. The
    // codes are BrowseRules' (forbidden_path, unknown_path, unreadable, bad_frame)
    // plus PeerLaunchCodes' (peer_launch_off, no_answer, link_lost).
    internal sealed record PeerDirResult(bool Ok, string Code, PeerDirListing? Listing)
    {
        internal static PeerDirResult Fail(string code) => new(false, code, null);
    }

    // CB-434: one page of a far machine's past sessions. The far desk does all
    // the work — its index, its clock for the 30-day cut, its live set, its
    // search and folder filter — so the rows equal what that machine's own
    // New Chat would show. Text and Folder are "" for none; From is a row offset.
    internal sealed record PastSessionQuery(string Cli, string Account, bool Older, string Text, string Folder, int From);

    // One row. Never the transcript path. Count is null when the far desk had
    // no count ready within its budget; At is last activity, unix seconds.
    internal sealed record PeerPastSession(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cwd,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("at")] long At,
        [property: JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
        [property: JsonPropertyName("missing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Missing = false,
        [property: JsonPropertyName("live"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Live = false);

    // A page, or the code that refused it: peer_launch_off, bad_frame,
    // unknown_cli, unknown_account, unreadable from the far desk; no_answer,
    // link_lost from this one. Total is the rows matching the query; Folders
    // is set on a From = 0 page only.
    internal sealed record PeerPastSessionsResult(bool Ok, string Code, int Total, int From,
        IReadOnlyList<PeerPastSession> Rows, IReadOnlyList<string> Folders)
    {
        internal static PeerPastSessionsResult Fail(string code) => new(false, code, 0, 0, [], []);
    }

    internal sealed record PeerMachine(string Name, bool Online);

    // The seam the window uses. The real one is PeerSessions.Launcher.
    internal interface IPeerLauncher
    {
        // Paired peers. Online means the link is connected right now.
        IReadOnlyList<PeerMachine> Machines();

        // Null means no answer: an older peer that ignores the query, a link
        // that dropped, or a timeout.
        Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct);

        Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct);

        // The far desk's folders at `path` ("" is its top level). Never throws:
        // every failure is a PeerDirResult with a code.
        Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct);

        // CB-397: answers a PeerLaunchCodes.TrustNeeded question, Trust or
        // Cancel. Never throws. A default so a launcher that cannot ask
        // answers "no answer" instead of every implementer changing at once.
        Task<PeerLaunchResult> TrustAsync(string machine, PeerLaunchTrust trust, bool trusted, CancellationToken ct) =>
            Task.FromResult(new PeerLaunchResult(false, PeerLaunchCodes.NoAnswer, null, null));

        // CB-434: one page of the far desk's past sessions. Never throws. A
        // default for the same reason as TrustAsync.
        Task<PeerPastSessionsResult> PastSessionsAsync(string machine, PastSessionQuery query, CancellationToken ct) =>
            Task.FromResult(PeerPastSessionsResult.Fail(PeerLaunchCodes.NoAnswer));
    }

    internal static class PeerLaunchCodes
    {
        public const string Launched = "launched";

        // The receiving desk's switch is off. Kept out of RelayReplies.ErrCodes
        // on purpose: that array is the phone's contract.
        public const string PeerLaunchOff = "peer_launch_off";

        // Client-side only; a peer never sends these.
        public const string NoAnswer = "no_answer";
        public const string LinkLost = "link_lost";

        // This desk could not sign the request, so nothing was sent. Not
        // bad_signature, which would blame the far machine for something that
        // never left this one.
        public const string CannotSign = "cannot_sign";

        // CB-397. The far desk started the CLI and it is asking whether to
        // trust the folder (the result carries PeerLaunchResult.Trust); the
        // asker was cancelled or answered no; the far desk no longer holds
        // that question (expired, answered at the desk, or never its own).
        // None of these is in RelayReplies.ErrCodes: that array is the phone's contract.
        public const string TrustNeeded = "trust_needed";
        public const string TrustCancelled = "trust_cancelled";
        public const string TrustGone = "trust_gone";
    }
}

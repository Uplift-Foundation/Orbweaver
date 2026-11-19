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
        [property: JsonPropertyName("recents")] IReadOnlyList<string>? Recents = null);

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
    internal sealed record PeerLaunchSpec(string Cli, string Cwd, string Account);

    // What came back. `Code` is "launched" on success; otherwise one of the
    // phone's refusal codes, or one of PeerLaunchCodes below.
    internal sealed record PeerLaunchResult(bool Ok, string Code, string? SessionId, string? Why);

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
    }
}

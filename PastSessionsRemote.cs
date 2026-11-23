using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // CB-434: this desk answering "which past sessions could I resume here?"
    // for somebody else — another desktop over the peer link, or the phone
    // over the relay. One function for both, so a remote New Chat and the
    // phone never disagree about what this machine would resume.
    //
    // The far desk does all the work (contract section 1): its own index, its
    // own clock for the 30-day cut, its live set, PastSessions.List unchanged.
    // So the rows equal what this machine's own New Chat shows. Pure apart
    // from its seams.
    internal sealed class PastSessionsRemote
    {
        internal sealed record Seams(
            RelayRemote.AccountSeams Accounts,
            // PastSessionIndex.Load: cli, the account's profile dir (null for
            // the default), showOlder, now. Blocking.
            Func<NewChatCli, string?, bool, DateTimeOffset, IReadOnlyList<PastSession>> Load,
            // Session ids an orb is running right now on this desk.
            Func<IReadOnlySet<string>> Live,
            // PastSessionIndex.Count: the whole-file message count, cached by stamp.
            Func<NewChatCli, string, int?> Count,
            Func<DateTimeOffset> Now,
            // Time since the answer began, for the count budget.
            Func<Func<TimeSpan>> StartClock);

        // What launch-info's `resume` says this build speaks. Bumped only for a
        // change an asker of the previous number would misread.
        internal const int ResumeVersion = 1;

        internal const int PageSize = 50;
        internal const int MaxFolders = 64;
        internal const int MaxText = 256;
        internal const int MaxPath = 4096;

        // Counting reads a whole transcript. A page's counts that are not ready
        // within this are left out, and the asker draws nothing for them.
        internal static readonly TimeSpan CountBudget = TimeSpan.FromMilliseconds(1500);

        private readonly Seams _seams;

        internal PastSessionsRemote(Seams seams) => _seams = seams;

        // In order: the CLI, the account, then the read. The peer link's
        // switch is checked by the caller before this is reached, so a desk
        // that has not opted in reads nothing at all.
        internal PeerPastSessionsResult Page(PastSessionQuery q)
        {
            if (Array.IndexOf(LaunchGate.Clis, q.Cli) < 0) return PeerPastSessionsResult.Fail(RelayReplies.UnknownCli);

            var cli = LaunchGate.CliFor(q.Cli);
            var accounts = _seams.Accounts;
            if (!RelayAccounts.TryResolve(cli, q.Account, accounts.Choices(cli), accounts.Designated(cli), null, out var profileDir))
                return PeerPastSessionsResult.Fail(RelayReplies.UnknownAccount);

            var now = _seams.Now();
            IReadOnlyList<PastSession> sessions;
            try { sessions = _seams.Load(cli, profileDir, q.Older, now); }
            catch (Exception) { return PeerPastSessionsResult.Fail(RelayReplies.Unreadable); }

            // A row whose id could never be resumed from here is not offered:
            // it would only be refused when chosen.
            var rows = PastSessions.List(sessions, _seams.Live(), q.Text, q.Folder.Length == 0 ? null : q.Folder, now, q.Older)
                .Where(r => LaunchGate.ResumeIdOk(r.Session.Id))
                .ToList();

            var elapsed = _seams.StartClock();
            var page = rows.Skip(q.From).Take(PageSize)
                .Select(r => ToWire(r, elapsed() < CountBudget ? _seams.Count(cli, r.Session.TranscriptPath) : null))
                .ToList();

            var folders = q.From == 0
                ? PastSessions.Folders(sessions).Where(f => f.Length <= MaxPath && !f.Any(char.IsControl)).Take(MaxFolders).ToList()
                : [];

            return new PeerPastSessionsResult(true, Listed, rows.Count, q.From, page, folders);
        }

        internal const string Listed = "ok";

        // Never the transcript path. A title is cleaned of control characters
        // (a pasted prompt can carry a stray escape) rather than refusing the
        // whole page on the asker's side; a folder is never rewritten, because
        // a resume has to name exactly it — one that cannot cross is sent
        // absent and marked missing, so the row reads as not resumable.
        internal static PeerPastSession ToWire(PastSessionRow row, int? count)
        {
            var s = row.Session;
            var cwdOk = s.Cwd is { } c && c.Length <= MaxPath && !c.Any(char.IsControl);
            return new PeerPastSession(
                s.Id,
                cwdOk ? s.Cwd : null,
                new string(s.Title.Where(ch => !char.IsControl(ch)).ToArray()),
                Math.Max(0, s.LastActivity.ToUnixTimeSeconds()),
                count,
                s.FolderMissing || !cwdOk,
                row.IsLive);
        }

        // The real desk. Excluded: each seam is one line into the account
        // list, the shared index, the session snapshot or the clock.
        [ExcludeFromCodeCoverage]
        internal static PastSessionsRemote ForThisDesk() => new(new Seams(
            new RelayRemote.AccountSeams(RelayAccounts.Choices, _ => null),
            PastSessionIndex.Load,
            () => RemoteControlSessions.LocalSessions()
                .Where(s => s.Status.IsLocalCli)
                .Select(s => s.SessionId)
                .ToHashSet(StringComparer.Ordinal),
            (cli, path) => PastSessionIndex.Shared.Count(cli, path),
            () => DateTimeOffset.UtcNow,
            () => { var sw = Stopwatch.StartNew(); return () => sw.Elapsed; }));
    }

    // The peer link's answer to `past-sessions-get`, in the order list-dir's
    // is checked: the switch first, before anything is parsed, so a desk that
    // has not opted in spends nothing and discloses nothing; then the shape;
    // then the page.
    internal sealed class PeerPastSessionsServer(Func<bool> enabled, Func<PastSessionQuery, PeerPastSessionsResult> page)
    {
        internal PeerPastSessionsResult Handle(JsonElement body)
        {
            if (!enabled()) return PeerPastSessionsResult.Fail(PeerLaunchCodes.PeerLaunchOff);
            return PastSessionsWire.ParseQuery(body) is { } q ? page(q) : PeerPastSessionsResult.Fail(RelayReplies.BadFrame);
        }

        [ExcludeFromCodeCoverage]
        internal static PeerPastSessionsServer ForThisDesk(Func<bool> enabled)
        {
            var remote = PastSessionsRemote.ForThisDesk();
            return new(enabled, remote.Page);
        }
    }

    // The peer link's codec for `past-sessions-get` / `past-sessions`. As strict
    // as PeerLaunchWire: everything here came from another process and is drawn
    // in a window, so a parser answers null for anything not exactly the
    // documented shape and bounds what it keeps.
    internal static class PastSessionsWire
    {
        private sealed record QueryBody(
            [property: JsonPropertyName("cli")] string? Cli,
            [property: JsonPropertyName("account")] string? Account,
            [property: JsonPropertyName("older")] bool Older,
            [property: JsonPropertyName("q")] string? Text,
            [property: JsonPropertyName("folder")] string? Folder,
            [property: JsonPropertyName("from")] int From);

        private sealed record PageBody(
            [property: JsonPropertyName("ok")] bool Ok,
            [property: JsonPropertyName("code")] string? Code,
            [property: JsonPropertyName("total")] int Total,
            [property: JsonPropertyName("from")] int From,
            [property: JsonPropertyName("rows")] IReadOnlyList<PeerPastSession>? Rows,
            [property: JsonPropertyName("folders")] IReadOnlyList<string>? Folders);

        internal static JsonElement EncodeQuery(PastSessionQuery q) =>
            PeerProtocol.BodyOf(new QueryBody(q.Cli, q.Account, q.Older, q.Text, q.Folder, q.From));

        // Null for anything but a whole query. cli and account are required
        // (account is always explicit between desks, as PeerLaunchSpec's is);
        // q and folder may be empty or absent.
        internal static PastSessionQuery? ParseQuery(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            QueryBody? q;
            try { q = body.Deserialize<QueryBody>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            return Sane(q!.Cli, 32, true) && Sane(q.Account, 256, true)
                && Sane(q.Text, PastSessionsRemote.MaxText, false) && Sane(q.Folder, PastSessionsRemote.MaxPath, false)
                && q.From >= 0
                ? new PastSessionQuery(q.Cli!, q.Account!, q.Older, q.Text ?? "", q.Folder ?? "", q.From)
                : null;
        }

        internal static JsonElement EncodePage(PeerPastSessionsResult r) =>
            PeerProtocol.BodyOf(r.Ok
                ? new PageBody(true, r.Code, r.Total, r.From, r.Rows, r.Folders.Count > 0 ? r.Folders : null)
                // A refusal carries its code and nothing else.
                : new PageBody(false, r.Code, 0, 0, null, null));

        internal static PeerPastSessionsResult? ParsePage(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            PageBody? p;
            try { p = body.Deserialize<PageBody>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            if (!Sane(p!.Code, 64, true)) return null;
            if (!p.Ok) return PeerPastSessionsResult.Fail(p.Code!);

            var rows = p.Rows ?? [];
            var folders = p.Folders ?? [];
            if (p.Total < 0 || p.From < 0 || rows.Count > PastSessionsRemote.PageSize || folders.Count > PastSessionsRemote.MaxFolders
                || rows.Any(r => !RowOk(r)) || folders.Any(f => !Sane(f, PastSessionsRemote.MaxPath, true)))
                return null;

            return new PeerPastSessionsResult(true, p.Code!, p.Total, p.From, rows, folders);
        }

        // The id is what a resume puts on a command line over there, so it is
        // held to the gate's own shape here as well.
        private static bool RowOk(PeerPastSession? r) =>
            r is not null
            && LaunchGate.ResumeIdOk(r.Id)
            && Sane(r.Cwd, PastSessionsRemote.MaxPath, false)
            && r.Title is not null && Sane(r.Title, PastSessions.TitleLength * 2, false)
            && r.At >= 0 && r.Count is null or >= 0;

        private static bool Sane(string? value, int max, bool required) =>
            value is null
                ? !required
                : (!required || value.Length > 0) && value.Length <= max && value.All(c => !char.IsControl(c));
    }
}

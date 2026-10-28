using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // Which team a session belongs to, answered from the files Claude Code
    // writes instead of from the process — for the sessions whose process this
    // app cannot ask about.
    //
    // AgentTeam reads `--parent-session-id`, `--agent-name` and `--agent-color`
    // off a pid's command line, and everything that follows from a team (the
    // arrow to the lead, the member's own name and colour) hangs off that.
    // It cannot work for a Claude Code running in WSL, and the reason is
    // measured rather than assumed: every status file the hook wrote for a WSL
    // session carries `"session_pid":0` and an empty colour, because the hook
    // is a Windows PowerShell process that WSL's interop starts, so its
    // parent chain dead-ends in the interop bridge and never reaches the Linux
    // `claude` (OrbweaverHook.ps1 says so where it leaves the pid at 0). With
    // no pid there is nothing to hand AgentTeam, and AgentTeam's Windows read
    // is a WMI query over the Windows process table, where a Linux process is
    // not listed in any case. A team spawned from WSL therefore drew as
    // unrelated orbs, each with the main-session persona and no arrows, even
    // though every member's command line carried exactly the right flags
    // (checked with `ps` inside the distro).
    //
    // What a WSL session does leave on disk, and what is enough:
    //
    //   * its transcript, whose entries carry `"teamName"` and `"agentName"`
    //     from the first user entry on — a member's name and its team's;
    //   * `<config dir>/teams/<team>/config.json` beside the `projects`
    //     folder the transcript sits in, naming `leadSessionId` and giving
    //     each member's `color`.
    //
    // Those are the two things the command line gave: the lead and the
    // colour, with the name coming from the transcript. A lead's own
    // transcript carries neither field, so a lead reads as "not a member",
    // which is also what the process read says of one.
    //
    // **The process read still wins.** This is consulted only for a session
    // with no pid to ask, so a native Windows or macOS session is exactly as
    // before and pays for no file read. That is also why it is not the first
    // version of team detection brought back (see AgentTeam's header, which
    // read the same two files from the hook and only learned the answer when a
    // member next fired one): that was replaced because the command line is
    // true the moment the orb appears, and for every session that has one it
    // still is.
    //
    // Parsing is split from reading for the reason everything else in this app
    // is: the parsers are pure with fixtures, and the file reads are the part a
    // headless test must be able to substitute.
    internal static class AgentTeamFiles
    {
        // How much of a transcript's start is read to find who the session is.
        // The identifying fields are on the first user entry, which is a few
        // hundred bytes in; the entries after it can be enormous (the one after
        // a member's first message carries its whole CLAUDE.md), so reading a
        // whole transcript to learn two strings would be the expensive part of
        // the scan. A line cut off by this bound fails to parse and is skipped,
        // which is the right answer for a line nobody asked to read.
        internal const int HeadBytes = 64 * 1024;

        // The team config is a few hundred bytes. This bounds a file that is
        // not one, rather than describing how big a real one gets.
        internal const int ConfigBytes = 256 * 1024;

        // A team member as the transcript names it. Both empty for a session
        // that is not one.
        internal readonly record struct Identity(string Team, string Agent);

        // "Not a member", said with empty strings rather than `default`, for
        // the reason AgentTeam.None is: a record struct's default leaves both
        // fields null, and the first caller to ask `.Length` of one throws.
        internal static readonly Identity Nobody = new("", "");

        // The first entry that names both a team and an agent. Entries naming
        // only one are skipped rather than half-believed, since a team without
        // a member name cannot be looked up in the config and a name without a
        // team cannot be placed.
        internal static Identity IdentityIn(string? head)
        {
            if (string.IsNullOrEmpty(head)) return Nobody;

            foreach (var line in head.Split('\n'))
            {
                if (line.Length < 2 || line[0] != '{') continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                    var team = Str(doc.RootElement, "teamName");
                    var agent = Str(doc.RootElement, "agentName");

                    if (!string.IsNullOrEmpty(team) && !string.IsNullOrEmpty(agent))
                        return new Identity(team, agent);
                }
                catch (JsonException)
                {
                    // A cut-off or damaged line: nothing to learn from it.
                }
            }

            return Nobody;
        }

        // Where this team's config lives, given where the member's transcript
        // does: `<config>/projects/<project>/<id>.jsonl` is three segments
        // below `<config>`, and the middle one is checked to be `projects`
        // rather than assumed, so a transcript somewhere else produces no path
        // at all instead of a guess in the wrong directory.
        //
        // Done on the string rather than with Path, because the path may be a
        // `\\wsl.localhost\...` one and this has to give the same answer when
        // the suites run on a runner whose Path does not split on a backslash.
        // The separator it joins with is the one the path already used.
        //
        // The team name is the only part of the result a process chooses, and
        // it is checked like every other value read from one: letters, digits
        // and `-`, nothing that could step out of `teams/`.
        internal static string? ConfigPath(string? transcriptPath, string? team)
        {
            var safeTeam = AgentTeam.Sanitize(team);
            if (safeTeam.Length == 0 || string.IsNullOrEmpty(transcriptPath)) return null;

            var cut = transcriptPath;
            var separator = '/';
            for (var segment = 0; segment < 3; segment++)
            {
                var at = cut.LastIndexOfAny(Separators);
                if (at <= 0) return null;

                separator = cut[at];
                var removed = cut[(at + 1)..];
                cut = cut[..at];

                if (segment == 2 && !removed.Equals("projects", StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            return string.Concat(cut, separator, "teams", separator, safeTeam, separator, "config.json");
        }

        private static readonly char[] Separators = { '/', '\\' };

        // The lead and the member's colour, out of one team's config. The lead
        // is what makes the arrow, so a config that names none is "not known to
        // be in a team" and not a member with nobody to point at.
        //
        // The member is found by name and by nothing else, in a config whose
        // other members, and the lead itself, are irrelevant to this session.
        internal static AgentTeam.Membership MembershipIn(string? configJson, string? agent)
        {
            if (string.IsNullOrWhiteSpace(configJson) || string.IsNullOrEmpty(agent)) return AgentTeam.None;

            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return AgentTeam.None;

                var lead = AgentTeam.Sanitize(Str(doc.RootElement, "leadSessionId"));
                if (lead.Length == 0) return AgentTeam.None;

                var color = "";
                if (doc.RootElement.TryGetProperty("members", out var members)
                    && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        if (member.ValueKind != JsonValueKind.Object) continue;
                        if (!string.Equals(Str(member, "name"), agent, StringComparison.Ordinal)) continue;

                        color = AgentTeam.Sanitize(Str(member, "color"));
                        break;
                    }
                }

                return new AgentTeam.Membership(lead, color, AgentTeam.SanitizeName(agent));
            }
            catch (JsonException)
            {
                return AgentTeam.None;
            }
        }

        // The path this machine can open for a transcript path a status file
        // recorded, or null when it cannot be opened from here.
        //
        // A WSL session's status file names its transcript the way Linux does
        // (`/home/user/.claude/projects/...`), which on Windows is not a path
        // at all. The distro is not recorded anywhere, so each is tried: the
        // transcript is a file one of them has, and a path that exists in two
        // distros at the same absolute location (two installs with the same
        // user) is read from the first, which is a transcript of the same shape
        // either way and is only used to learn a team name.
        //
        // Anything not shaped like a Linux path on a Windows host is already
        // readable and is returned as it is — which is every path on macOS and
        // every native Windows one.
        internal static string? Locate(
            string transcriptPath, bool onWindows, IReadOnlyList<string> distros, Func<string, bool> exists)
        {
            if (!onWindows || transcriptPath.Length == 0 || transcriptPath[0] != '/') return transcriptPath;

            var relative = transcriptPath.TrimStart('/').Replace('/', '\\');
            foreach (var distro in distros)
            {
                foreach (var root in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
                {
                    var candidate = string.Concat(root, distro, "\\", relative);
                    if (exists(candidate)) return candidate;
                }
            }

            return null;
        }

        // The start of a file as text, or null when it cannot be read. Opened
        // for sharing because Claude Code is appending to the transcript while
        // this runs, and a read that insists on exclusive access would fail
        // exactly when the session is live.
        internal static string? ReadHead(string path, int maxBytes)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[maxBytes];
                var read = stream.Read(buffer, 0, buffer.Length);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static string Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        // The reader the scan uses: a session's team, by session id, for the
        // sessions that have no pid.
        //
        // Cached because the scan runs every couple of seconds and a file over
        // `\\wsl.localhost` is a round trip into a VM. A member's team does not
        // change while it lives, so a found membership is kept; "not in a team"
        // is re-asked after a short while instead, because the answer for a
        // session whose transcript or whose team config has not been written yet
        // is "not yet", and a teammate would otherwise draw without its arrow
        // for as long as the answer was cached.
        internal sealed class Reader
        {
            // Long enough that an ordinary session is not re-read every pass,
            // short enough that a member's arrow appears within a few seconds
            // of its config landing.
            internal const long NotAMemberRecheckMs = 15_000;

            private readonly Func<string, string?> _head;
            private readonly Func<string, string?> _config;
            private readonly Func<string, string?> _locate;
            private readonly Func<long> _clock;
            private readonly object _gate = new();
            private readonly Dictionary<string, (AgentTeam.Membership Value, long Stamp)> _cache = new(StringComparer.Ordinal);

            internal Reader(
                Func<string, string?> head,
                Func<string, string?> config,
                Func<string, string?> locate,
                Func<long>? clock = null)
            {
                _head = head;
                _config = config;
                _locate = locate;
                _clock = clock ?? (() => Environment.TickCount64);
            }

            // Every session's answer at once, by session id. A session with no
            // team is simply absent from the result, the same as "no entry" is
            // for the pid read.
            internal IReadOnlyDictionary<string, AgentTeam.Membership> ReadMany(
                IEnumerable<(string SessionId, string TranscriptPath)> sessions)
            {
                var answers = new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal);
                var now = _clock();

                foreach (var (sessionId, transcriptPath) in sessions)
                {
                    if (answers.ContainsKey(sessionId) || string.IsNullOrEmpty(transcriptPath)) continue;

                    AgentTeam.Membership membership;
                    bool fresh;
                    lock (_gate)
                    {
                        fresh = _cache.TryGetValue(sessionId, out var cached)
                            && (!string.IsNullOrEmpty(cached.Value.Lead) || now - cached.Stamp < NotAMemberRecheckMs);
                        membership = fresh ? _cache[sessionId].Value : AgentTeam.None;
                    }

                    if (!fresh)
                    {
                        membership = Read(transcriptPath);
                        lock (_gate)
                        {
                            _cache[sessionId] = (membership, now);
                            if (_cache.Count > 256) Prune(now);
                        }
                    }

                    if (!string.IsNullOrEmpty(membership.Lead)) answers[sessionId] = membership;
                }

                return answers;
            }

            private AgentTeam.Membership Read(string transcriptPath)
            {
                var located = _locate(transcriptPath);
                if (located is null) return AgentTeam.None;

                var who = IdentityIn(_head(located));
                if (who.Team.Length == 0) return AgentTeam.None;

                var configPath = ConfigPath(located, who.Team);
                if (configPath is null) return AgentTeam.None;

                return MembershipIn(_config(configPath), who.Agent);
            }

            // Sessions come and go all day; without this the map grows for as
            // long as the app runs.
            private void Prune(long now)
            {
                foreach (var (id, entry) in _cache.ToList())
                {
                    if (now - entry.Stamp >= NotAMemberRecheckMs * 4) _cache.Remove(id);
                }
            }
        }

        // The reader the app uses: real files, the Lxss registry's distro list
        // when the host is Windows, and the clock. Wired here rather than in
        // SessionManager so the constructor line stays a one-liner and the
        // choice of "what is a real read" lives with the rest of this file.
        //
        // Excluded from coverage: reads this machine's distro list from the
        // registry and opens files in a live distro. What each read is *for* is
        // Locate, IdentityIn, ConfigPath and MembershipIn, and the cache is
        // Reader, all covered; this is only the wiring of real files into them.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static Reader Default()
        {
            IReadOnlyList<string>? distros = null;
            long distrosAt = 0;

            IReadOnlyList<string> Distros()
            {
                var now = Environment.TickCount64;
                if (distros is null || now - distrosAt > 60_000)
                {
                    distros = OperatingSystem.IsWindows() ? WslIntegration.ListDistros() : Array.Empty<string>();
                    distrosAt = now;
                }

                return distros;
            }

            return new Reader(
                head: path => ReadHead(path, HeadBytes),
                config: path => ReadHead(path, ConfigBytes),
                locate: path => Locate(path, OperatingSystem.IsWindows(), Distros(), File.Exists));
        }
    }
}

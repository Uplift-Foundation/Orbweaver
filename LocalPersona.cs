using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Orbweaver
{
    // The persona a local CLI session wears, read from the CLAUDE.md files that
    // are already beside its work.
    //
    // Pure given its inputs: every entry point takes the working directory, the
    // session's source and the user-level config directories rather than asking
    // the machine for them, so the whole of the resolution — which files, in
    // which order, which field wins — is a plain unit test over a temp tree.
    // UserConfigDirs is the one thing here that asks the machine, and it does
    // nothing else — which is what lets SessionManager's scan ask it once per
    // pass and hand the answer down, rather than have each step here ask again
    // and risk two steps of one pass disagreeing (CB-143).
    //
    // Nearest-first, and that is the interesting half. A CLAUDE.md in the
    // directory you are working in is about *this* project; one three levels up
    // is about the family of projects; the one in ~/.claude is about you. So
    // the first file to name a field wins it, and a name set at the top of a
    // monorepo is a default rather than an override — the same way Claude Code
    // itself layers them.
    internal static class LocalPersona
    {
        // AvatarSource and AvatarPath are two different files and both are
        // needed. AvatarSource is the markdown that *named* the picture, which
        // is what says whose picture it is and which directory a relative path
        // was resolved against; AvatarPath is the picture itself. The scan
        // watches both, because they move independently: a portrait replaced
        // in place — same filename, new bytes, CLAUDE.md untouched — changes
        // nothing about any markdown file, and watching the markdown alone
        // left the old face on the orb until something else in the tree
        // happened to move or the app was restarted.
        // Deliberately no `byte[] Avatar` field, and that is CB-135's change
        // rather than an omission. This registry holds one Persona per session
        // for the life of the session, and two sessions in one repository are
        // two entries by design — so a retained picture is one copy of the
        // source bytes *per agent*, on a machine that routinely runs twenty or
        // thirty of them, all of the same file. What an orb actually draws is
        // a 144 px frame out of OpenClawAvatars' cache; the source bytes were
        // only ever the thing those were decoded from, and AvatarPath is
        // enough to decode them again. See OpenClawAvatars.ForFile, which does.
        //
        // Nulling the field after the first decode was considered and
        // rejected: a byte[] that is populated sometimes is a worse record to
        // reason about than one that does not exist, and every caller would
        // have had to know which half of the session's life it was in.
        internal sealed record Persona(
            string? Name,
            string? Voice,
            double? Rate,
            string? AvatarSource,
            string? AvatarPath,
            IReadOnlyList<string> Files)
        {
            internal bool IsEmpty => Name is null && Voice is null && AvatarPath is null;

            // The files this persona's answer depends on: what was read, plus
            // the picture if there is one. Here rather than at the call site
            // because forgetting the picture is exactly the bug this exists to
            // have fixed, and a caller that assembles the set by hand is a
            // caller that can forget it again.
            internal IEnumerable<string> Watched =>
                AvatarPath is null ? Files : Files.Append(AvatarPath);
        }

        internal static readonly Persona Empty =
            new(null, null, null, null, null, Array.Empty<string>());

        // How far up the tree to look. Not a security bound — the walk
        // terminates at the root on its own — but a bound on what the scan
        // costs: these paths are stat'd every two seconds, four per level, and
        // a working directory forty levels deep is already somebody's build
        // artifact rather than somebody's project.
        private const int MaxDepth = 40;

        // What an import may reach, and how much of it. A CLAUDE.md that
        // imports a file that imports a file is ordinary; one five deep is a
        // structure, and twenty files in is more markdown than any persona
        // needs to state a name.
        private const int MaxImportHops = 5;
        private const int MaxFiles = 20;

        // The files a session's persona could be written in, nearest first, as
        // paths — no IO at all, so the caller can stat them for a signature
        // before deciding whether anything is worth reading.
        //
        // The four names per directory are Claude Code's own set: CLAUDE.md and
        // its untracked CLAUDE.local.md sibling, the .claude/CLAUDE.md a
        // project keeps out of its root, and AGENTS.md — which this repository
        // itself keeps beside CLAUDE.md for the CLI that does not read the
        // other one.
        //
        // The user-level file is asked only for Claude Code. Codex and Grok
        // have their own config directories with their own layouts, and
        // guessing that ~/.claude describes a Codex session would be a persona
        // taken from the wrong CLI — the ticket calls that a non-goal, and a
        // wrong name is worse than no name. OpenClaw and RemoteControl have no
        // local directory to walk in the first place: the first is a
        // conversation on a gateway, and the second is a session whose cwd is
        // on somebody else's machine, where a path that happens to exist here
        // too would be a different directory wearing the same string.
        // agentName is what CB-154 added: an agent-team member's own name
        // (AgentTeam.Membership.Name, already sanitised to letters, digits and
        // a handful of separators — never a path separator, so it cannot walk
        // out of profiles/<name>). Empty for anything that isn't a team
        // member, which is every caller from before this ticket, so the
        // default reproduces their exact candidate list unchanged.
        //
        // profiles/<name>/<name>.md and .profiles-assets/<name>/<name>.md —
        // not .claude/agents/<name>.md, which is where an earlier version of
        // this change looked. That file is Claude Code's own subagent
        // definition (frontmatter of tools/model/hooks) and was found, on a
        // real running team, to carry no picture at all and a lowercase
        // `name:` (the slug) that would have *shadowed* the real display
        // name rather than supplying one — checking it first would have made
        // this feature actively worse than not checking anything. The two
        // paths here are profile-gen's own `write_profile.py --output file`
        // layout (`profilegen/storage.py`'s tracked and gitignored cases),
        // the tool real teams already use to give each hire a name, a voice
        // and a portrait — so this reads what such a team already produces
        // rather than asking for a third convention beside it.
        //
        // Checked at every directory level the walk visits, same as the four
        // shared names below — not root-only. A track lead's hires are
        // written with --root set to the lead's own directory, which is also
        // where each hire's session normally sits, but a session's cwd can
        // still drift one level below that during a turn, and repeating this
        // check costs nothing a shared name's repetition does not already
        // cost. It is checked first at each level for the reason a nearer
        // file already wins over a farther one: it says more specifically
        // who this is. A team that gives a member neither file falls
        // straight through to the shared project persona, unchanged from
        // before this ticket.
        //
        // Each candidate carries the directory level of the walk that offered
        // it (CB-187): <dir> for <dir>/CLAUDE.md, for <dir>/.claude/CLAUDE.md
        // and for both profile layouts alike, and the config directory itself
        // for a user-level file. profile-gen writes a persona's picture
        // relative to that project directory, and the path alone cannot say
        // which it was — ~/.claude/CLAUDE.md is level ~ when the walk reaches
        // it from below and level ~/.claude when only the config list offers
        // it — so the walk, which knows, says so.
        // IsMemberProfile marks the two agent-owned shapes above (profiles/…
        // and .profiles-assets/…) and nothing else — see ResolveFrom's own
        // comment for what that flag buys (CB-191). Defaulted so every
        // existing two-argument `new Candidate(path, level)` call, including
        // OwnLevels' own, keeps meaning "not a member file" unchanged.
        // SlugFilter is for the one file whose path does not say whose it is:
        // profile-gen's private-persona layout writes `persona/persona.md` for
        // whoever the persona is, so the name it answers to is inside it. It is
        // `|`-separated alternatives (an agent and the persona it is an
        // instance of), and a candidate carrying one is only read when its
        // front matter's `slug:` is one of them.
        //
        // Place is which persona a member candidate belongs to, for ResolveFrom's
        // run of consecutive member candidates. profile-gen writes one persona in
        // two layouts (profiles/ and .profiles-assets/), and those two are one
        // persona whose fields are folded together; but the project's own
        // .orbweaver folder, an account's pool, the shared pool, and an instance's
        // own file against its persona's are different places holding different
        // personas, and folding those together gave one persona's name a
        // different file's voice. Candidates in the same place share a number; a
        // hand-built list leaves them all at 0, which is the old behaviour.
        internal readonly record struct Candidate(
            string Path, string Level, bool IsMemberProfile = false, string? SlugFilter = null, int Place = 0);

        internal static IReadOnlyList<string> CandidateFiles(
            string? cwd, IEnumerable<string> userConfigDirs, SessionSource source, string agentName = "") =>
            Candidates(cwd, userConfigDirs, source, agentName).Select(candidate => candidate.Path).ToArray();

        // The walk as the app runs it: the ceiling, if any, comes from the
        // environment (see WalkCeilingVariable).
        internal static IReadOnlyList<Candidate> Candidates(
            string? cwd, IEnumerable<string> userConfigDirs, SessionSource source, string agentName = "") =>
            CandidatesBelow(cwd, userConfigDirs, source, agentName, WalkCeiling(), PersonaPool.SharedRoot());

        // The same walk with the ceiling given outright, so the rule itself
        // can be tested without the process-wide variable: null climbs to the
        // filesystem root.
        internal static IReadOnlyList<Candidate> CandidatesBelow(
            string? cwd, IEnumerable<string> userConfigDirs, SessionSource source, string agentName,
            string? ceiling, string? poolRoot = null)
        {
            var files = new List<Candidate>();
            // ClaudeCloud joins these two for the same reason, and the reason is
            // worth restating because this guard names sources one by one rather
            // than asking IsLocalCli: a cloud session's cwd is a path inside a
            // container that does not exist on this disk. Falling through would
            // walk *this* machine for a persona and hand whatever it found to
            // somebody else's conversation, which is CB-140 again.
            if (source is SessionSource.OpenClaw
                or SessionSource.RemoteControl
                or SessionSource.ClaudeCloud) return files;

            // OrdinalIgnoreCase for the reason BackgroundJobs.ExtraAccountDirs
            // is: Windows paths are, and one file reached under two
            // capitalizations is one file whose second reading cannot say
            // anything the first did not.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var place = 0;

            void Add(string path, string level, bool isMemberProfile = false, string? slugFilter = null)
            {
                if (seen.Add(path)) files.Add(new Candidate(path, level, isMemberProfile, slugFilter, place));
            }

            var hasAgentName = agentName is not ("" or "." or "..");

            // The names a team member answers to: its own, then the persona it
            // is an instance of (`vector_2` is Vector's second instance).
            var names = hasAgentName ? PersonaPool.NamesFor(agentName) : Array.Empty<string>();

            // One place a persona can be kept, in the three layouts profile-gen
            // and the pool use: the tracked and the gitignored per-persona
            // folders, and the fixed private file that is recognised by the
            // slug it carries. Every one is a member profile, so a persona found
            // here is exclusively the answer (see ResolveFrom) rather than being
            // blended with the main session's.
            //
            // The picture a persona names is relative to `root`, which is also
            // its level, exactly as profile-gen writes it relative to the
            // directory it was run against.
            //
            // Each name in each place is its own persona (`place++`): an
            // instance's own file replaces its persona's outright instead of
            // lending it a name and borrowing a voice, and so does one place
            // against the next. Only the two layouts of one name share a place.
            void AddMemberProfiles(string root)
            {
                foreach (var name in names)
                {
                    place++;
                    Add(Path.Combine(root, "profiles", name, name + ".md"), root, isMemberProfile: true);
                    Add(Path.Combine(root, ".profiles-assets", name, name + ".md"), root, isMemberProfile: true);
                }

                place++;
                Add(Path.Combine(root, "persona", "persona.md"), root, isMemberProfile: true,
                    slugFilter: string.Join('|', names));
            }

            var directory = FullPathOrNull(cwd);
            for (var depth = 0; directory is not null && depth < MaxDepth; depth++)
            {
                if (hasAgentName)
                {
                    // The layouts profile-gen has always written straight into a
                    // project come first, so a list built before the pool existed
                    // keeps its order; the project's `.orbweaver` folder follows
                    // at the same level.
                    foreach (var name in names)
                    {
                        place++;
                        Add(Path.Combine(directory, "profiles", name, name + ".md"), directory, isMemberProfile: true);
                        Add(Path.Combine(directory, ".profiles-assets", name, name + ".md"), directory, isMemberProfile: true);
                    }

                    AddMemberProfiles(Path.Combine(directory, PersonaPool.FolderName));
                }

                Add(Path.Combine(directory, "CLAUDE.md"), directory);
                Add(Path.Combine(directory, "CLAUDE.local.md"), directory);
                Add(Path.Combine(directory, ".claude", "CLAUDE.md"), directory);
                Add(Path.Combine(directory, "AGENTS.md"), directory);
                if (ceiling is not null && PathsEqual(directory, ceiling)) break;
                directory = Path.GetDirectoryName(directory);
            }

            if (source != SessionSource.ClaudeCode) return files;

            foreach (var configDir in userConfigDirs)
            {
                if (string.IsNullOrWhiteSpace(configDir)) continue;

                // An account's own pool comes before its CLAUDE.md, which is the
                // main session's persona. The CLAUDE.md is also what ends the run
                // of member candidates, so two accounts' pools are two personas
                // and the nearer one replaces the farther rather than being
                // merged field by field with it.
                if (hasAgentName) AddMemberProfiles(configDir.Trim());
                Add(Path.Combine(configDir.Trim(), "CLAUDE.md"), configDir.Trim());
            }

            // The shared pool last: the user's own personas, for every project,
            // consulted only when nothing nearer names this one.
            if (hasAgentName && !string.IsNullOrWhiteSpace(poolRoot)) AddMemberProfiles(poolRoot);

            return files;
        }

        // Where the walk stops climbing, when something says so: the last
        // directory it visits, not the first one it skips. Unset in the app,
        // which wants the whole way up — a persona in ~ is meant to reach a
        // project below it. Set by the test suites to the temp directory their
        // scratch projects live in, because on Windows that directory is under
        // the developer's home, and an unbounded walk from a scratch project
        // reaches the home CLAUDE.md and every persona it imports: one Windows
        // machine measured 7 + 33 + 9 failures from it, each a test that
        // expected "no persona" or its own fixture and was handed the
        // developer's. macOS never showed it because $TMPDIR is /var/folders,
        // which no home directory is an ancestor of. A ceiling that the cwd is
        // not below changes nothing — the walk simply never meets it.
        internal const string WalkCeilingVariable = BrandEnv.PersonaWalkCeiling;

        private static string? WalkCeiling() =>
            FullPathOrNull(BrandEnv.Get(WalkCeilingVariable));

        private static bool PathsEqual(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string? FullPathOrNull(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (ArgumentException) { return null; }
            // Windows refuses a path past its own length limit, and an import
            // token is only as short as the line it was written on. Not
            // reachable from a test that has to pass on both runners — macOS
            // has no such limit and returns the path — so it is named in the
            // PR body rather than covered.
            catch (IOException) { return null; }
        }

        // The candidates that exist, in order, each with its lines — plus
        // whatever they import.
        //
        // Claude Code's `@path.md` import is part of how people write these:
        // the persona is often in a file the CLAUDE.md pulls in rather than in
        // the CLAUDE.md itself, and refusing to follow one would mean reading
        // the half of the document that says the least. A file's own lines are
        // read before the files it imports, so an import is a default the
        // importing file may override — the same rule the directory walk
        // already uses, one level down.
        //
        // The visited set is keyed on the canonical path, so a cycle (two files
        // importing each other, or two paths reaching one file through a link)
        // is read once and not twice.
        //
        // Level is the walk-level directory of the candidate that brought each
        // file in (see Candidate), carried to everything that candidate
        // imports, because a persona file profile-gen imports writes its
        // picture relative to that project directory and nothing else here
        // remembers which one it was. A file two candidates both reach keeps
        // the first, nearer one's, since the visited set reads it once.
        //
        // An import of an import inherits the candidate's level too, not the
        // intermediate file's directory. The level is the one directory here
        // that is what it is *because* of where the project is — the walk
        // found it — while an intermediate file's directory is only wherever
        // the author chose to split their markdown, and the file naming the
        // picture already gets its own directory as the first root. Offering
        // every directory along the chain would make the places a relative
        // picture can land grow with the import depth, for no convention
        // anyone writes: profile-gen never nests an import at all.
        //
        // Canonicalised like the workspace root, so it compares with the
        // canonical paths the roots are measured against. A level that will
        // not canonicalise — gone between the walk and the read — falls back
        // to the file's own directory, which is simply no extra root.
        internal static IReadOnlyList<(string Path, string[] Lines, string Level)> Load(
            IReadOnlyList<Candidate> candidates)
        {
            var read = new List<(string Path, string[] Lines, string Level)>();
            ReadCandidates(candidates, read, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return read;
        }

        // The body Load's loop used to be, pulled out so ResolveFrom can run
        // it one candidate at a time — the shared `read` list and `visited`
        // set are what keep the twenty-file cap and the cycle guard the same
        // cumulative cap and guard across separate calls that they already
        // were across one candidate to the next inside a single call.
        private static void ReadCandidates(
            IReadOnlyList<Candidate> candidates,
            List<(string Path, string[] Lines, string Level)> read,
            HashSet<string> visited)
        {
            foreach (var candidate in candidates)
            {
                if (read.Count >= MaxFiles) break;
                if (candidate.SlugFilter is not null && !NamesOneOf(candidate.Path, candidate.SlugFilter)) continue;
                ReadInto(candidate.Path, 0, PersonaFiles.CanonicalDirectory(candidate.Level), read, visited);
            }
        }

        // Whether the file at `path` says, in its front matter, that it is one
        // of the personas in `alternatives` (`|`-separated). Used for the fixed
        // `persona/persona.md` path, which is the same file name for whoever the
        // persona is: reading it for every agent would hand one persona's face to
        // every teammate, which is the bug this lookup exists to end.
        private static bool NamesOneOf(string path, string alternatives)
        {
            var canonical = PersonaFiles.CanonicalFile(path);
            if (canonical is null) return false;

            var lines = PersonaFiles.ReadMarkdown(canonical);
            if (lines is null) return false;

            var slug = SlugIn(lines);
            return slug is not null
                && alternatives.Split('|').Any(alternative => slug.Equals(alternative, StringComparison.OrdinalIgnoreCase));
        }

        // The `slug:` of a file's front matter, or null. Front matter only — a
        // `slug:` further down is prose about something else.
        internal static string? SlugIn(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0 || lines[0].Trim() != "---") return null;

            for (var i = 1; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line == "---") return null;
                if (!line.StartsWith("slug:", StringComparison.Ordinal)) continue;

                var value = line["slug:".Length..].Trim().Trim('"', '\'');
                return value.Length == 0 ? null : value;
            }

            return null;
        }

        // A bare list of paths, each its own level: what every caller before
        // CB-187 meant, since the only root it offered was the file's own
        // directory. Kept for the tests that build a list by hand.
        //
        // A blank level rather than a second computation of the file's
        // directory: CanonicalDirectory answers null for it, and ReadInto
        // already falls back to the directory of the canonical file it read
        // — the one place that knows that directory, and knows it after links
        // are resolved rather than before.
        internal static IReadOnlyList<(string Path, string[] Lines, string Level)> Load(
            IReadOnlyList<string> candidates) =>
            Load(OwnLevels(candidates));

        private static IReadOnlyList<Candidate> OwnLevels(IReadOnlyList<string> candidates) =>
            candidates.Select(path => new Candidate(path, "")).ToArray();

        private static void ReadInto(
            string path, int hop, string? level,
            List<(string Path, string[] Lines, string Level)> read, HashSet<string> visited)
        {
            if (read.Count >= MaxFiles) return;

            var canonical = PersonaFiles.CanonicalFile(path);
            if (canonical is null || !visited.Add(canonical)) return;

            var lines = PersonaFiles.ReadMarkdown(canonical);
            if (lines is null) return;

            // Never null: canonical is a FileInfo.FullName for a file that
            // exists, so it is absolute and has a parent.
            var directory = Path.GetDirectoryName(canonical)!;
            level ??= directory;

            read.Add((canonical, lines, level));
            if (hop >= MaxImportHops) return;

            foreach (var import in ImportsIn(lines, directory))
                ReadInto(import, hop + 1, level, read, visited);
        }

        // An import is a bare `@something.md` token: either the whole line or a
        // word in it. Bare deliberately — `@notes.md.` with a full stop after
        // it is a sentence mentioning the file, and a sentence mentioning a
        // file is not an instruction to read it.
        //
        // Resolved against the importing file's own directory, which is what
        // makes a `docs/persona.md` beside a CLAUDE.md mean the obvious thing.
        // Path.Combine already lets an absolute import be itself, so there is
        // no separate arm for one.
        private static IEnumerable<string> ImportsIn(string[] lines, string directory)
        {
            foreach (var line in lines)
            {
                foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length <= 1 || token[0] != '@') continue;

                    var relative = token[1..];
                    if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;

                    var combined = FullPathOrNull(Path.Combine(directory, relative));
                    if (combined is not null) yield return combined;
                }
            }
        }

        // The fold. First file to state a field owns it, and the picture is
        // resolved against the directory of the file that named it first —
        // not against the session's cwd, so a `portrait.png` written in
        // ~/.claude/CLAUDE.md means the one in ~/.claude and cannot be
        // shadowed by a file of that name in whatever directory the session
        // happens to be sitting in. The walk-level directory the candidate
        // was found at is tried next (CB-187), and the workspace root
        // (CB-147) last, each only when the narrower lookup before it finds
        // nothing — so a picture written relative to the project directory,
        // as profile-gen writes it, or to the workspace root still resolves,
        // without weakening the guarantee the file-directory lookup already
        // made.
        internal static Persona Resolve(
            string? cwd, SessionSource source, IEnumerable<string> userConfigDirs, string agentName = "") =>
            ResolveFrom(Candidates(cwd, userConfigDirs, source, agentName), cwd);

        // The same fold, over a candidate list somebody else has already built.
        //
        // Split out for CB-143. The scan needs the candidates twice in one pass
        // — once to stat them for the signature, once to read them — and
        // building them twice meant building them from UserConfigDirs twice,
        // which is two readings of two process-wide globals with the whole of
        // the signature comparison in between. A list that moved between those
        // two readings gave a pass that watched one set of files and read
        // another, with nothing anywhere saying so. Handing the list down makes
        // the two the same list by construction rather than by both asking the
        // same question and hoping for the same answer, and it saves the second
        // walk besides.
        //
        // workspaceCwd defaults to null so every caller from before CB-147 —
        // the direct-candidate-list tests among them — keeps its original,
        // single-root behaviour without editing a call site: CandidateRoots
        // treats a null workspace root as "one candidate root", which is
        // this ticket's required degenerate case (D8) rather than a special
        // case bolted on beside it.
        //
        // A bare list of paths is each its own level — see Load's overload —
        // so a hand-built list behaves exactly as it did before CB-187.
        internal static Persona ResolveFrom(IReadOnlyList<string> candidates, string? workspaceCwd = null) =>
            ResolveFrom(OwnLevels(candidates), workspaceCwd);

        // CB-191: name, voice, rate and avatar all come from one persona, not
        // from whichever candidate happened to state each one first. Before
        // this the fold below ran name/voice/rate/avatar as four independent
        // first-wins races over the whole flattened candidate list, so a team
        // member's own `.profiles-assets/<name>/<name>.md` — naming itself
        // but no picture — supplied the name and then lost the avatar race to
        // the project's CLAUDE.md a few candidates later. The result wore the
        // member's name and the project's face: two different personas'
        // fields, stitched together, which is not what either file said.
        //
        // The fix is scoped to exactly the case the ticket is about, not
        // generalised to every candidate. Every candidate here is read and
        // folded into the running first-wins totals exactly as before *except*
        // a member-profile candidate (Candidate.IsMemberProfile — the two
        // agent-owned shapes Candidates adds ahead of the project's own
        // files): the moment one of those, together with whatever it itself
        // imports, states any field at all, that group's own fields are the
        // whole answer, returned immediately with nothing folded in from a
        // shallower project or home file the walk had already read, and
        // nothing from a farther one it has not reached yet.
        //
        // That leaves TheNearestFileToNameAFieldOwnsIt's monorepo case —
        // several plain CLAUDE.md files at different directory levels, no
        // agent name anywhere — resolving exactly as it always has: a nearer
        // file's name and a farther file's voice are still one project
        // persona layered over its own defaults, which was never the bug.
        // Only a member's own file is exclusive once it says anything, because
        // only a member's own file is a *different* persona from the
        // project's, in the sense CB-154 introduced the shape to mean.
        internal static Persona ResolveFrom(IReadOnlyList<Candidate> candidates, string? workspaceCwd = null)
        {
            if (candidates.Count == 0) return Empty;

            // Canonicalised once per resolve rather than once per candidate
            // file, since it is the same directory for every one of them. A
            // cwd that is blank or does not exist canonicalises to null,
            // which is exactly the "no second root" case D8 requires.
            var workspaceRoot = PersonaFiles.CanonicalDirectory(workspaceCwd);

            var read = new List<(string Path, string[] Lines, string Level)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? name = null;
            string? voice = null;
            double? rate = null;
            string? avatarSource = null;
            string? avatarPath = null;
            var files = new List<string>();

            var index = 0;
            while (index < candidates.Count && read.Count < MaxFiles)
            {
                var candidate = candidates[index];

                if (!candidate.IsMemberProfile)
                {
                    var start = read.Count;
                    ReadCandidates(new[] { candidate }, read, visited);
                    index++;
                    if (read.Count == start) continue;

                    var group = Fold(read, start, read.Count, workspaceRoot);
                    files.AddRange(group.Files);
                    name ??= group.Name;
                    voice ??= group.Voice;
                    rate ??= group.Rate;
                    if (avatarPath is null)
                    {
                        avatarSource = group.AvatarSource;
                        avatarPath = group.AvatarPath;
                    }

                    continue;
                }

                // A run of one or more consecutive member-profile candidates
                // — Candidates() offers both profiles/<name>/<name>.md and
                // .profiles-assets/<name>/<name>.md back to back at every
                // level, profile-gen's two --output layouts for one agent —
                // is read and folded together as *one* persona before the
                // exclusivity check below runs, not candidate by candidate.
                //
                // Tightened here after QA measured the regression a
                // candidate-at-a-time version of this fix produced: a name
                // in profiles/... and a picture in .profiles-assets/... for
                // the same agent used to resolve together, as two
                // independent first-wins races landing on develop today.
                // Deciding exclusivity on the first of the two candidates
                // alone would return with the name and never even read the
                // second file, silently losing a picture that worked before
                // this ticket. The two shapes name the same agent, so they
                // are the same persona, not two competing ones — unlike a
                // member file against the project's, which is the mixing
                // this ticket exists to stop.
                //
                // One run is one *place* (Candidate.Place): the two layouts of
                // one persona, not two different personas that happen to be
                // listed next to each other.
                var runStart = read.Count;
                var runPlace = candidate.Place;
                while (index < candidates.Count && candidates[index].IsMemberProfile
                       && candidates[index].Place == runPlace && read.Count < MaxFiles)
                {
                    ReadCandidates(new[] { candidates[index] }, read, visited);
                    index++;
                }

                if (read.Count == runStart) continue;

                var member = Fold(read, runStart, read.Count, workspaceRoot);
                if (!member.IsEmpty)
                {
                    // The member's own persona, once the run says anything at
                    // all, is the whole answer — see the comment above.
                    // Whatever was already folded in from an earlier,
                    // shallower candidate above this loop is dropped rather
                    // than used to fill in what this run leaves blank.
                    return new Persona(
                        member.Name, member.Voice, member.Rate, member.AvatarSource, member.AvatarPath, member.Files);
                }

                files.AddRange(member.Files);
            }

            return new Persona(name, voice, rate, avatarSource, avatarPath, files);
        }

        // One candidate's own fields — its file plus whatever it imports,
        // read[start..end) — first-wins within itself exactly as the old
        // single flattened fold was first-wins across everything. Split out
        // of ResolveFrom so that fold can be asked of one candidate's slice
        // for the member short-circuit above and of the running totals for
        // every other candidate, without two copies of the field-reading body.
        private readonly record struct Group(
            string? Name, string? Voice, double? Rate, string? AvatarSource, string? AvatarPath,
            bool AvatarNamed, List<string> Files)
        {
            // AvatarNamed is not just AvatarPath is not null — a member file
            // that names a picture PersonaFiles could not read (missing,
            // too large, escapes its root) is still a member file that
            // *found itself*, and must exclude the project's face exactly as
            // one whose picture did resolve would. Without this, a member
            // whose only mistake was a typo in `image:` fell through to the
            // project's CLAUDE.md and wore its face — the same symptom this
            // ticket exists to fix, reached through a broken reference
            // instead of an absent field, and no more a non-goal for that.
            // PersonaMarkdown.Fields.RawAvatar already exists for exactly
            // this — see its own header comment — so this reads that rather
            // than inventing a second way to ask "did a file try to name
            // one".
            internal bool IsEmpty =>
                Name is null && Voice is null && Rate is null && AvatarPath is null && !AvatarNamed;
        }

        private static Group Fold(
            List<(string Path, string[] Lines, string Level)> read, int start, int end, string? workspaceRoot)
        {
            string? name = null;
            string? voice = null;
            double? rate = null;
            string? avatarSource = null;
            string? avatarPath = null;
            var avatarNamed = false;
            var files = new List<string>();

            for (var i = start; i < end; i++)
            {
                var (path, lines, level) = read[i];
                files.Add(path);

                var fields = PersonaMarkdown.Parse(lines);
                name ??= fields.Name;
                voice ??= fields.Voice;
                rate ??= fields.Rate;
                avatarNamed |= fields.RawAvatar is not null;

                if (avatarPath is not null) continue;

                // The directory of the file that named the picture, taken from
                // the canonical path Load already resolved — never null, for
                // the reason ReadInto's own call says. Not re-canonicalised:
                // if that directory is itself a link, the picture beside the
                // markdown is on the other end of the same link the markdown
                // was, so resolving it could only refuse a file we have
                // already read from, never admit one from anywhere new.
                var root = Path.GetDirectoryName(path)!;

                // The path only. The bytes are read to prove the file opens
                // and then dropped — see PersonaFiles.AvatarPathAt for why
                // that read is not skipped.
                // The whole Fields rather than fields.Avatar. The early-out
                // above used to skip a file whose Avatar was null, which is
                // also every file where somebody wrote a picture this grammar
                // could not read — so the one case worth reporting was the one
                // case that never reached the resolver at all.
                //
                // root — the file's own directory — first, then level — the
                // walk-level directory of the candidate that brought it in —
                // then workspaceRoot, each only if the one before finds
                // nothing: see PersonaFiles.AvatarAt's own comments (D1/D2)
                // for why that order is not arbitrary. For a CLAUDE.md sitting
                // at its own level, level is root, and CandidateRoots drops
                // the repeat.
                var picture = PersonaFiles.AvatarPathAt(root, level, workspaceRoot, fields);
                if (picture is null) continue;

                avatarSource = path;
                avatarPath = picture;
            }

            return new Group(name, voice, rate, avatarSource, avatarPath, avatarNamed, files);
        }

        // What the scan compares to decide whether anything is worth re-reading:
        // for each path, whether it exists, how long it is and when it was last
        // written. Stats only — no file is opened — because this runs on the UI
        // thread every two seconds for every local session, and reading a
        // handful of markdown files at that cadence is a cost with nothing to
        // show for it.
        //
        // Takes paths rather than a session so the caller decides which set. A
        // candidate list alone will not notice an edit to an *imported* file,
        // which is not a candidate; a caller that wants it to should pass the
        // previous Persona.Files alongside, which is what that list is for.
        internal static string Signature(IEnumerable<string> paths)
        {
            // ASCII's own separators rather than a printable delimiter: a path
            // on macOS or Linux may legally contain a newline or a pipe, and a
            // signature two different file sets can both produce is not a
            // signature.
            const char Unit = '\u001f';
            const char Record = '\u001e';

            var signature = new StringBuilder();

            foreach (var path in paths)
            {
                signature.Append(path).Append(Unit);

                // One catch, and only around the constructor: FileInfo caches
                // its stat the first time anything asks, so Length and
                // LastWriteTimeUtc read that cache rather than the disk once
                // Exists has been answered — neither can throw for a file that
                // has gone away in between. What is left is a path the
                // constructor itself refuses.
                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists) signature.Append(file.Length).Append('@').Append(file.LastWriteTimeUtc.Ticks);
                    else signature.Append('-');
                }
                catch (ArgumentException) { signature.Append('?'); }

                signature.Append(Record);
            }

            return signature.ToString();
        }

        // The Claude Code config directories this app knows about, in the order
        // they should be asked.
        //
        // CLAUDE_CONFIG_DIR first when this process has one, because a Claude
        // Code launched under that variable *is* running out of that directory
        // and its own CLAUDE.md is the user-level file for it. Then
        // ClaudeConfigRoots, which is already the answer to "every config
        // directory on this machine" — ~/.claude plus the configured extras —
        // and exists precisely so nothing new builds that list a fourth time.
        internal static IReadOnlyList<string> UserConfigDirs(string? configDirEnv = null, string? home = null)
        {
            configDirEnv ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

            var dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(configDirEnv)) dirs.Add(configDirEnv.Trim());

            foreach (var root in ClaudeConfigRoots.All(home))
            {
                if (!dirs.Contains(root, StringComparer.OrdinalIgnoreCase)) dirs.Add(root);
            }

            return dirs;
        }

        // Agent > persona > title > folder.
        //
        // Pure, and here rather than in OrbWindow because "what is this orb
        // called" is a rule with a right answer and OrbWindow is a place you
        // have to look at to check one — the same argument that moved the orb's
        // initials into OrbGlyph after a year of them being wrong.
        //
        // The persona sits below the agent name and above the title on
        // purpose. An agent name is what a team member is called *right now*
        // and changes per session; a persona is what the project says its
        // assistant is called, which is more specific than the chat's
        // generated title but less specific than a role someone assigned. The
        // title is not lost either way — the orb's tooltip still says it.
        internal static string OrbLabel(string? agent, string? personaName, string? title, string? folder)
        {
            if (!string.IsNullOrEmpty(agent)) return agent;
            if (!string.IsNullOrEmpty(personaName)) return personaName;
            if (!string.IsNullOrEmpty(title)) return title;
            return folder ?? "";
        }
    }

    // Which persona belongs to which live session, for the drawing code to ask.
    //
    // Mirrors OpenClawSessions' identity registry deliberately, down to the
    // lock and the test seam: the scan writes, everything that draws reads, and
    // an orb that wants to know who it is asks one question of one place
    // whether the session is a gateway conversation or a terminal.
    internal static class LocalPersonas
    {
        private static readonly object Gate = new();

        private static readonly Dictionary<string, LocalPersona.Persona> Registry =
            new(StringComparer.OrdinalIgnoreCase);

        // The key an avatar is cached under. Prefixed rather than bare, because
        // OpenClawAvatars' cache is shared with the gateway's agents and a
        // session id and an agent id are two namespaces that must not collide.
        internal static string AvatarKey(string sessionId) => "local:" + sessionId;

        // Setting a *different* persona object drops any bitmap decoded from
        // the old one. Identity rather than content: the scan keeps one persona
        // object per session and hands the same one back until something it
        // watches moves, so this costs a dictionary removal per change and
        // nothing per tick.
        //
        // That makes this half of a two-part invariant, and the other half is
        // the scan's. A picture is only re-decoded here if the scan hands over
        // a new object, and it only does that if it noticed the picture change
        // — which for a portrait overwritten in place, with the markdown left
        // alone, means it has to be watching the picture file itself. It is:
        // see Persona.Watched and SessionManager.ApplyPersona. Neither half is
        // sufficient alone, and the version of this comment that claimed an
        // edited picture was re-decoded said so while the scan was still
        // watching only the markdown.
        internal static void Set(string sessionId, LocalPersona.Persona persona)
        {
            lock (Gate)
            {
                if (Registry.TryGetValue(sessionId, out var existing) && ReferenceEquals(existing, persona)) return;
                Registry[sessionId] = persona;
            }

            OpenClawAvatars.Forget(AvatarKey(sessionId));
        }

        internal static void Forget(string sessionId)
        {
            lock (Gate) Registry.Remove(sessionId);
            OpenClawAvatars.Forget(AvatarKey(sessionId));
        }

        internal static LocalPersona.Persona? For(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (Gate) return Registry.GetValueOrDefault(sessionId);
        }

        // Every voice a local persona names, for SessionIdentity.PersonaVoiceRequests.
        internal static List<string> Voices()
        {
            lock (Gate) return Registry.Values.Select(persona => persona.Voice).OfType<string>().ToList();
        }

        // A test seam, matching OpenClawSessions.SetIdentitiesForTests: the only
        // thing that fills this table in production is SessionManager's scan,
        // which needs a status file on disk and a two-second timer. Without
        // this, everything that draws a local session's name or picture is
        // unreachable for a reason that has nothing to do with the code.
        internal static void SetForTests(IReadOnlyDictionary<string, LocalPersona.Persona> personas)
        {
            lock (Gate)
            {
                foreach (var id in Registry.Keys.ToList()) OpenClawAvatars.Forget(AvatarKey(id));
                Registry.Clear();
                foreach (var (id, persona) in personas) Registry[id] = persona;
            }

            foreach (var id in personas.Keys) OpenClawAvatars.Forget(AvatarKey(id));
        }

        // The persona's voice resolved over the options actually available.
        // Null when the session has no persona, when its persona names no
        // voice, or when the name it does give matches nothing here — all three
        // meaning "use the user's own voice", which is what the caller does
        // with a null.
        //
        // VoiceForPersona rather than MatchVoiceOption since CB-136, so a
        // persona whose voice is written as a mixture — `Voice is 50% sky and
        // 50% nicole`, which is what this repository's own `.claude/PERSONA.MD`
        // says — resolves to the blended voice rather than to nothing. A
        // mixture the machine cannot build still answers null, and null is
        // still the user's global voice rather than silence.
        internal static TextToSpeech.VoiceOption? VoiceForSession(
            string? sessionId, IEnumerable<TextToSpeech.VoiceOption> options) =>
            TextToSpeech.VoiceForPersona(For(sessionId)?.Voice, options);

        // Excluded from coverage: AllVoiceOptions asks the neural engine to
        // enumerate itself and runs the user's listing command, which is two
        // process launches on the machine running the tests. The decision this
        // wraps — which option a persona's voice resolves to, and what happens
        // when it resolves to none — is the overload above, and it is tested.
        [ExcludeFromCodeCoverage]
        internal static TextToSpeech.VoiceOption? VoiceForSession(string? sessionId) =>
            VoiceForSession(sessionId, TextToSpeech.AllVoiceOptions());

        // Only the neural (Kokoro) engine has a speaking rate to set, so a rate
        // resolved for a system voice or a custom command is simply never read
        // — same as OpenClawSessions.RateForSession, and kept separate from the
        // voice for the same reason: VoiceOption is also the shape of every row
        // in the settings picker, where a per-utterance rate means nothing.
        internal static double? RateForSession(string? sessionId) => For(sessionId)?.Rate;
    }
}

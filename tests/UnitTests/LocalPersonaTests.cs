using Xunit;

namespace Orbweaver.Tests;

// LocalPersona — which files a session's persona could be written in, which of
// them actually says what, and what the orb ends up called.
//
// In the Settings collection because UserConfigDirs reaches
// ClaudeConfigRoots, which reads ClaudeCodeProfileDirs — one process-wide static
// that a dozen classes in this assembly touch. SettingsCollection.cs has the
// story of the once-in-five failure that bought that rule.
[Collection("Settings")]
public class LocalPersonaTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-local-persona-" + Guid.NewGuid());

    public LocalPersonaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string directory, string name, params string[] lines)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    // --- CandidateFiles: which paths, in which order, with no IO ----------

    [Fact]
    public void EachDirectoryOffersTheFourNamesClaudeCodeItselfReads()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);

        var files = LocalPersona.CandidateFiles(cwd, Array.Empty<string>(), SessionSource.ClaudeCode);

        Assert.Equal(new[]
        {
            Path.Combine(full, "CLAUDE.md"),
            Path.Combine(full, "CLAUDE.local.md"),
            Path.Combine(full, ".claude", "CLAUDE.md"),
            Path.Combine(full, "AGENTS.md"),
        }, files.Take(4));
    }

    // Nearest first: the working directory's own files come before its
    // parent's, all the way up. A name set at the top of a monorepo is a
    // default, not an override.
    [Fact]
    public void TheWalkGoesUpwardsFromTheWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);
        var parent = Path.GetDirectoryName(full)!;

        // No ceiling: the rule itself, unaffected by the one the suite's
        // bootstrap sets in the environment.
        var files = LocalPersona.CandidatesBelow(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "", ceiling: null)
            .Select(candidate => candidate.Path).ToArray();

        Assert.Equal(Path.Combine(parent, "CLAUDE.md"), files[4]);
        Assert.Contains(Path.Combine(Path.GetPathRoot(full)!, "CLAUDE.md"), files);
    }

    // The ceiling is the last directory visited: its own files are offered,
    // nothing above it is. This is what keeps a scratch project under a
    // Windows temp directory from reading the developer's home CLAUDE.md.
    [Fact]
    public void ACeilingIsTheLastDirectoryTheWalkVisits()
    {
        var ceiling = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-ceiling"));
        var cwd = Path.Combine(ceiling, "a", "b");

        var files = LocalPersona.CandidatesBelow(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "", ceiling)
            .Select(candidate => candidate.Path).ToArray();

        Assert.Contains(Path.Combine(ceiling, "CLAUDE.md"), files);
        Assert.Contains(Path.Combine(ceiling, "a", "CLAUDE.md"), files);
        Assert.DoesNotContain(Path.Combine(Path.GetDirectoryName(ceiling)!, "CLAUDE.md"), files);
        Assert.DoesNotContain(Path.Combine(Path.GetPathRoot(ceiling)!, "CLAUDE.md"), files);
    }

    // A ceiling the working directory is not below is never met, so the walk
    // climbs to the root exactly as it would without one.
    [Fact]
    public void ACeilingTheWorkingDirectoryIsNotBelowChangesNothing()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a");
        var elsewhere = Path.Combine(Path.GetTempPath(), "cb-elsewhere");

        var bounded = LocalPersona.CandidatesBelow(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "", elsewhere);
        var unbounded = LocalPersona.CandidatesBelow(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "", null);

        Assert.Equal(unbounded.Select(c => c.Path), bounded.Select(c => c.Path));
    }

    [Fact]
    public void TheCeilingComparisonIgnoresCase()
    {
        var ceiling = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-ceiling"));
        var cwd = Path.Combine(ceiling, "a");

        var files = LocalPersona.CandidatesBelow(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "", ceiling.ToUpperInvariant())
            .Select(candidate => candidate.Path).ToArray();

        // On every OS, by the rule this file already keeps for paths (see the
        // dedupe in Candidates): Windows paths ignore case, and so does the
        // default macOS volume, so an upper-cased ceiling is the same directory.
        // The salvaged draft of this test expected the opposite off Windows and
        // failed on macOS against its own implementation.
        Assert.Contains(Path.Combine(ceiling, "CLAUDE.md"), files);
        Assert.DoesNotContain(Path.Combine(Path.GetDirectoryName(ceiling)!, "CLAUDE.md"), files);
    }

    // The wiring, read off the environment the suite's bootstrap set: a walk
    // from a scratch project under the temp directory offers the temp
    // directory's own files and stops there, on every OS.
    [Fact]
    public void TheEnvironmentCeilingBoundsTheWalkTheAppRuns()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var cwd = Path.Combine(temp, "cb-env-ceiling", "a");

        var files = LocalPersona.Candidates(cwd, Array.Empty<string>(), SessionSource.ClaudeCode)
            .Select(candidate => candidate.Path).ToArray();

        Assert.Contains(Path.Combine(temp, "CLAUDE.md"), files);
        Assert.DoesNotContain(Path.Combine(Path.GetDirectoryName(temp)!, "CLAUDE.md"), files);
    }

    // CB-154: an agent-team member's own files — the two layouts
    // write_profile.py's --output file can produce — win over the project's
    // shared files at the same directory level, same rule as any nearer file,
    // one convention narrower.
    [Fact]
    public void AnAgentsOwnProfileFilesComeBeforeTheProjectsSharedFiles()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);

        var files = LocalPersona.CandidateFiles(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "helena-marsh");

        Assert.Equal(Path.Combine(full, "profiles", "helena-marsh", "helena-marsh.md"), files[0]);
        Assert.Equal(Path.Combine(full, ".profiles-assets", "helena-marsh", "helena-marsh.md"), files[1]);

        // The project's own `.orbweaver` folder is one more place the same agent
        // can be kept at this level (the pool's project layer), so it is offered
        // here too. What this pins is unchanged: every one of an agent's own
        // files comes before the project's shared ones.
        Assert.Equal(Path.Combine(full, ".orbweaver", "profiles", "helena-marsh", "helena-marsh.md"), files[2]);

        var shared = files.ToList().IndexOf(Path.Combine(full, "CLAUDE.md"));
        Assert.True(shared > 2, "the project's CLAUDE.md should come after the agent's own files");
        Assert.All(files.Take(shared), file => Assert.StartsWith(full + Path.DirectorySeparatorChar, file));
        Assert.DoesNotContain(files.Take(shared), file => file.EndsWith("CLAUDE.md", StringComparison.Ordinal));
    }

    // Repeated at every ancestor directory, same as the four shared names —
    // not root-only. A hire's session normally sits exactly at the directory
    // write_profile.py was told to use as --root, but a turn can leave its
    // cwd one level below that, and this costs nothing extra to check.
    [Fact]
    public void AnAgentsOwnProfileFilesAreOfferedAtEveryAncestorDirectoryToo()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);
        var parent = Path.GetDirectoryName(full)!;

        var files = LocalPersona.CandidateFiles(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "helena-marsh");

        Assert.Contains(Path.Combine(parent, "profiles", "helena-marsh", "helena-marsh.md"), files);
    }

    // Every existing caller passes no agent name at all, and must see exactly
    // today's list — an empty string is "not a team member", not a name.
    [Fact]
    public void NoAgentNameLeavesTheListUnchanged()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);

        var files = LocalPersona.CandidateFiles(cwd, Array.Empty<string>(), SessionSource.ClaudeCode, "");

        Assert.Equal(Path.Combine(full, "CLAUDE.md"), files[0]);
    }

    // "." and ".." survive AgentTeam.SanitizeName (both are legal under its
    // letter/digit/dash/underscore/dot/space rule) but neither names a real
    // agent, and ".." one level under profiles is the project root itself —
    // not a directory this should ever be told is a persona.
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void ADotOnlyAgentNameAddsNoCandidate(string agentName)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");

        var files = LocalPersona.CandidateFiles(
            cwd, Array.Empty<string>(), SessionSource.ClaudeCode, agentName);

        Assert.DoesNotContain(files, f => f.Contains("profiles"));
    }

    [Fact]
    public void TheUserLevelFileIsAskedLastAndOnlyForClaudeCode()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a");
        var configDir = Path.Combine(Path.GetTempPath(), "cb-config");
        var expected = Path.Combine(configDir, "CLAUDE.md");

        var claude = LocalPersona.CandidateFiles(cwd, new[] { configDir }, SessionSource.ClaudeCode);
        Assert.Equal(expected, claude[^1]);

        // Codex and Grok keep their own config directories with their own
        // layouts. Reading ~/.claude for one of them would be a persona taken
        // from the wrong CLI, and a wrong name is worse than no name.
        Assert.DoesNotContain(
            expected, LocalPersona.CandidateFiles(cwd, new[] { configDir }, SessionSource.Codex));
        Assert.DoesNotContain(
            expected, LocalPersona.CandidateFiles(cwd, new[] { configDir }, SessionSource.Grok));
    }

    // A gateway conversation has no directory here at all, and a Remote Control
    // session's cwd is a path on somebody else's machine — one that may well
    // exist here too, wearing the same string over a different directory.
    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void ASessionWithNoLocalDirectoryHasNoCandidates(SessionSource source)
    {
        var files = LocalPersona.CandidateFiles(
            Path.GetTempPath(), new[] { Path.GetTempPath() }, source);

        Assert.Empty(files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfigDirIsSkipped(string blank)
    {
        var files = LocalPersona.CandidateFiles(null, new[] { blank }, SessionSource.ClaudeCode);

        Assert.Empty(files);
    }

    [Fact]
    public void SurroundingSpaceOnAConfigDirIsTrimmed()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "cb-config");

        var files = LocalPersona.CandidateFiles(null, new[] { "  " + configDir + "  " }, SessionSource.ClaudeCode);

        Assert.Equal(Path.Combine(configDir, "CLAUDE.md"), Assert.Single(files));
    }

    // A config directory that the upward walk already passed through is one
    // file, not two: stating it twice cannot say anything the first reading did
    // not, and every candidate costs a stat on every scan.
    [Fact]
    public void AFileReachedTwiceIsOnlyAskedOnce()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "cb-candidates", "a");

        var files = LocalPersona.CandidateFiles(configDir, new[] { configDir }, SessionSource.ClaudeCode);

        Assert.Single(files, path => path == Path.Combine(Path.GetFullPath(configDir), "CLAUDE.md"));
    }

    // Not a security bound — the walk terminates at the root on its own — but a
    // bound on what the scan costs, four stats per level every two seconds.
    [Fact]
    public void TheWalkStopsAtAFixedDepth()
    {
        var deep = Path.Combine(
            new[] { Path.GetTempPath() }.Concat(Enumerable.Repeat("d", 45)).ToArray());

        var files = LocalPersona.CandidateFiles(deep, Array.Empty<string>(), SessionSource.ClaudeCode);

        Assert.Equal(40 * 4, files.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoWorkingDirectoryLeavesNothingToWalk(string? cwd)
    {
        Assert.Empty(LocalPersona.CandidateFiles(cwd, Array.Empty<string>(), SessionSource.ClaudeCode));
    }

    // A cwd the platform refuses to resolve is not a directory to walk. It
    // costs the walk rather than the whole persona — the user-level file is
    // still asked.
    [Fact]
    public void AWorkingDirectoryThePlatformRefusesIsSkipped()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "cb-config");

        var files = LocalPersona.CandidateFiles("a\0b", new[] { configDir }, SessionSource.ClaudeCode);

        Assert.Equal(Path.Combine(configDir, "CLAUDE.md"), Assert.Single(files));
    }

    // CB-187: the walk level each candidate carries. Pure path arithmetic,
    // like the rest of CandidateFiles — one row per shape the walk offers.
    [Fact]
    public void EveryShapeTheWalkOffersCarriesTheDirectoryLevelThatOfferedIt()
    {
        var full = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-levels", "a"));
        var configDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-levels-config"));

        var candidates = LocalPersona.Candidates(full, new[] { configDir }, SessionSource.ClaudeCode, "ines");
        string LevelOf(string path) => Assert.Single(candidates, candidate => candidate.Path == path).Level;

        Assert.Equal(full, LevelOf(Path.Combine(full, "CLAUDE.md")));
        Assert.Equal(full, LevelOf(Path.Combine(full, "CLAUDE.local.md")));
        Assert.Equal(full, LevelOf(Path.Combine(full, ".claude", "CLAUDE.md")));
        Assert.Equal(full, LevelOf(Path.Combine(full, "AGENTS.md")));
        Assert.Equal(full, LevelOf(Path.Combine(full, "profiles", "ines", "ines.md")));
        Assert.Equal(full, LevelOf(Path.Combine(full, ".profiles-assets", "ines", "ines.md")));

        var parent = Path.GetDirectoryName(full)!;
        Assert.Equal(parent, LevelOf(Path.Combine(parent, ".claude", "CLAUDE.md")));

        Assert.Equal(configDir, LevelOf(Path.Combine(configDir, "CLAUDE.md")));

        // And CandidateFiles is the same list, paths only.
        Assert.Equal(
            candidates.Select(candidate => candidate.Path),
            LocalPersona.CandidateFiles(full, new[] { configDir }, SessionSource.ClaudeCode, "ines"));
    }

    // ~/.claude/CLAUDE.md is two shapes at once: level ~ when the walk climbs
    // to it from a project under the home directory, and level ~/.claude when
    // only the config list offers it. The walk comes first, so it is level ~
    // — and a picture written relative to ~/.claude still resolves, because
    // the file's own directory is tried before any level.
    [Fact]
    public void AConfigFileTheWalkReachesFirstKeepsTheWalksLevel()
    {
        var home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-levels-home"));
        var configDir = Path.Combine(home, ".claude");

        var candidates = LocalPersona.Candidates(
            Path.Combine(home, "project"), new[] { configDir }, SessionSource.ClaudeCode);

        Assert.Equal(home, Assert.Single(
            candidates, candidate => candidate.Path == Path.Combine(configDir, "CLAUDE.md")).Level);
    }

    // --- Load: what is actually read, and what it imports ------------------

    [Fact]
    public void OnlyTheCandidatesThatExistAreRead()
    {
        var project = Dir("project");
        var present = Write(project, "CLAUDE.md", "Her name is Leota");

        var read = LocalPersona.Load(new[]
        {
            present,
            Path.Combine(project, "CLAUDE.local.md"),
        });

        Assert.Equal(present, Assert.Single(read).Path);
    }

    // The import is part of how people actually write these: the persona is
    // often in the file the CLAUDE.md pulls in rather than in the CLAUDE.md.
    [Fact]
    public void AnImportedFileIsReadAfterTheFileThatImportedIt()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md", "See @persona.md for who you are");
        var imported = Write(project, "persona.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(new[] { main, imported }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void AnImportIsResolvedAgainstTheImportingFilesOwnDirectory()
    {
        var project = Dir("project");
        var docs = Dir("project", "docs");
        var main = Write(project, "CLAUDE.md", "@docs/persona.md");
        var first = Write(docs, "persona.md", "@second.md");
        var second = Write(docs, "second.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(new[] { main, first, second }, read.Select(entry => entry.Path));
    }

    // CB-187: every file carries the walk level of the candidate that brought
    // it in, however deep the import — never the intermediate file's
    // directory, and for a .claude/CLAUDE.md never .claude.
    [Fact]
    public void EveryFileCarriesTheWalkLevelOfTheCandidateThatBroughtItIn()
    {
        var project = Dir("project");
        var dotClaude = Dir("project", ".claude");
        var docs = Dir("project", ".claude", "docs");
        Write(dotClaude, "CLAUDE.md", "@docs/persona.md");
        Write(docs, "persona.md", "@second.md");
        Write(docs, "second.md", "Her name is Leota");

        var read = LocalPersona.Load(
            LocalPersona.Candidates(project, Array.Empty<string>(), SessionSource.ClaudeCode));

        var level = PersonaFiles.CanonicalDirectory(project);
        Assert.Equal(3, read.Count);
        Assert.All(read, entry => Assert.Equal(level, entry.Level));
    }

    // A bare list of paths is each its own level: what a hand-built list
    // meant before CB-187, when the file's own directory was the only root.
    [Fact]
    public void ABareListOfPathsIsEachItsOwnLevel()
    {
        var dotClaude = Dir("project", ".claude");
        var main = Write(dotClaude, "CLAUDE.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(Path.GetDirectoryName(PersonaFiles.CanonicalFile(main)), Assert.Single(read).Level);
    }

    [Fact]
    public void TwoFilesImportingEachOtherAreEachReadOnce()
    {
        var project = Dir("project");
        var a = Write(project, "CLAUDE.md", "@b.md");
        var b = Write(project, "b.md", "@CLAUDE.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { a });

        Assert.Equal(new[] { a, b }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void ImportsStopAfterFiveHops()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md", "@1.md");
        for (var i = 1; i <= 6; i++) Write(project, i + ".md", "@" + (i + 1) + ".md");

        var read = LocalPersona.Load(new[] { main });

        // The importing file plus five hops; the sixth is written and never
        // opened.
        Assert.Equal(6, read.Count);
        Assert.DoesNotContain(Path.Combine(project, "6.md"), read.Select(entry => entry.Path));
    }

    [Fact]
    public void NoMoreThanTwentyFilesAreReadHoweverManyAreOffered()
    {
        var project = Dir("project");
        var imports = Enumerable.Range(1, 30).Select(i => "@" + i + ".md").ToArray();
        var main = Write(project, "CLAUDE.md", imports);
        foreach (var i in Enumerable.Range(1, 30)) Write(project, i + ".md", "nothing here");
        var second = Write(project, "AGENTS.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main, second });

        Assert.Equal(20, read.Count);
        // The cap stops the second candidate too, not just the imports.
        Assert.DoesNotContain(second, read.Select(entry => entry.Path));
    }

    // An import is a bare token. A sentence mentioning a file is not an
    // instruction to read it, and neither is a link to something that is not
    // markdown at all.
    [Fact]
    public void OnlyABareMarkdownTokenIsAnImport()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md",
            "@notes.txt",
            "@notes.md.",
            "notes.md",
            "@",
            "see @real.md");
        Write(project, "notes.txt", "x");
        Write(project, "notes.md", "x");
        var real = Write(project, "real.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(new[] { main, real }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void AnImportThePlatformRefusesToResolveIsSkipped()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md", "@a\0b.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(main, Assert.Single(read).Path);
    }

    // --- Resolve: the fold ------------------------------------------------

    [Fact]
    public void TheNearestFileToNameAFieldOwnsIt()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        Write(parent, "CLAUDE.md", "Her name is Parent", "Her voice is Samantha");
        Write(child, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Leota", persona.Name);
        // Not stated nearer, so the parent still supplies it.
        Assert.Equal("Samantha", persona.Voice);
        Assert.False(persona.IsEmpty);
    }

    [Fact]
    public void TheUserLevelFileIsTheLastResort()
    {
        var project = Dir("tree", "project");
        var configDir = Dir("config");
        Write(configDir, "CLAUDE.md", "Her name is UserLevel");

        var withoutProjectFile =
            LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });
        Assert.Equal("UserLevel", withoutProjectFile.Name);

        Write(project, "CLAUDE.md", "Her name is Leota");
        var withProjectFile =
            LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });
        Assert.Equal("Leota", withProjectFile.Name);
    }

    [Fact]
    public void APictureIsReadFromBesideTheFileThatNamedIt()
    {
        var project = Dir("tree", "project");
        var configDir = Dir("config");
        File.WriteAllBytes(Path.Combine(configDir, "leota.png"), Png());
        var named = Write(configDir, "CLAUDE.md", "Her profile picture is leota.png");
        // A file of the same name beside the session, which must not be the one
        // that wins: the picture belongs to the markdown that named it.
        File.WriteAllBytes(Path.Combine(project, "leota.png"), new byte[] { 1, 2, 3 });
        Write(project, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });

        Assert.Equal(Path.Combine(configDir, "leota.png"), persona.AvatarPath);
        Assert.Equal(Png(), File.ReadAllBytes(persona.AvatarPath!));
        Assert.Equal(named, persona.AvatarSource);
    }

    // profile-gen's layout, exactly: a CLAUDE.md imports the persona file from
    // inside a marker block, and the persona file writes its picture relative
    // to the project directory that CLAUDE.md sits at. From the repository
    // root that happened to work, because the cwd is that directory; from
    // anywhere below it the picture was lost and the name kept, which is the
    // bug CB-187 pins. The walk level is its own root now.
    [Fact]
    public void APictureNamedRelativeToTheImportingFileResolvesFromASubdirectory()
    {
        var root = Dir("repo");
        var personaDir = Dir("repo", ".claude", "persona");
        var cwd = Dir("repo", "a", "b");
        Write(root, "CLAUDE.md",
            "<!-- profile-gen:start slug=persona -->",
            "@.claude/persona/persona.md",
            "<!-- profile-gen:end slug=persona -->");
        var named = Write(personaDir, "persona.md",
            "---",
            "schema_version: 1",
            "name: \"Jennifer Voss\"",
            "image: \".claude/persona/persona.gif\"",
            "---");
        File.WriteAllBytes(Path.Combine(personaDir, "persona.gif"), Png());

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Jennifer Voss", persona.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "persona.gif")), persona.AvatarPath);
        Assert.Equal(PersonaFiles.CanonicalFile(named), persona.AvatarSource);
    }

    // The nearest root still wins, in both directions: a picture beside the
    // file that named it beats one at the walk level, and one at the walk
    // level beats one in the session's cwd — the last a deliberate change
    // from CB-147, where the cwd's copy was the only second root.
    [Fact]
    public void TheNamingFilesDirectoryBeatsTheLevelAndTheLevelBeatsTheWorkspace()
    {
        var root = Dir("repo");
        var personaDir = Dir("repo", "persona");
        var cwd = Dir("repo", "a");
        Write(root, "CLAUDE.md", "@persona/persona.md");
        Write(personaDir, "persona.md", "Her profile picture is face.png");

        File.WriteAllBytes(Path.Combine(cwd, "face.png"), new byte[] { 3 });
        File.WriteAllBytes(Path.Combine(root, "face.png"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(personaDir, "face.png"), new byte[] { 1 });

        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(
            LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>()).AvatarPath!));

        File.Delete(Path.Combine(personaDir, "face.png"));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(
            LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>()).AvatarPath!));

        File.Delete(Path.Combine(root, "face.png"));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(
            LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>()).AvatarPath!));
    }

    [Fact]
    public void APictureThatIsNotThereLeavesTheOrbItsLetters()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "Her name is Leota", "Her profile picture is missing.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Leota", persona.Name);
        Assert.Null(persona.AvatarPath);
        Assert.Null(persona.AvatarSource);
    }

    [Fact]
    public void TheFirstPictureThatCanBeReadWins()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        File.WriteAllBytes(Path.Combine(child, "near.png"), Png());
        File.WriteAllBytes(Path.Combine(parent, "far.png"), Png());
        var nearer = Write(child, "CLAUDE.md", "Her picture is near.png");
        Write(parent, "CLAUDE.md", "Her picture is far.png");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(nearer, persona.AvatarSource);
    }

    // A portrait is not a markdown file and is not a candidate, so nothing in
    // Files names it — and the scan compares Signature over what a persona
    // watches to decide whether to read anything again. Leaving the picture out
    // of that set is the whole of the bug this pins: the same filename with new
    // bytes changes no markdown file at all, so every signature over Files
    // alone is identical and the orb keeps the old face until the app restarts.
    [Fact]
    public void APortraitIsPartOfWhatAPersonaWatches()
    {
        var project = Dir("tree", "project");
        var picture = Path.Combine(project, "leota.png");
        File.WriteAllBytes(picture, Png());
        Write(project, "CLAUDE.md", "Her name is Leota", "Her picture is leota.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(picture, persona.AvatarPath);
        Assert.Equal(persona.Files.Append(picture), persona.Watched);

        var watchedBefore = LocalPersona.Signature(persona.Watched);
        var filesBefore = LocalPersona.Signature(persona.Files);

        // Overwritten in place: same name, new bytes, and not one markdown
        // file touched.
        File.WriteAllBytes(picture, Png().Concat(new byte[] { 0 }).ToArray());

        Assert.NotEqual(watchedBefore, LocalPersona.Signature(persona.Watched));
        Assert.Equal(filesBefore, LocalPersona.Signature(persona.Files));
    }

    // Only a length or an mtime, whichever the filesystem gives: both are in
    // the signature precisely so a picture edited either way is noticed, and a
    // same-length rewrite is the case a length alone would miss.
    [Fact]
    public void APortraitTouchedWithoutChangingItsLengthStillMoves()
    {
        var project = Dir("tree", "project");
        var picture = Path.Combine(project, "leota.png");
        File.WriteAllBytes(picture, Png());
        Write(project, "CLAUDE.md", "Her picture is leota.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());
        var before = LocalPersona.Signature(persona.Watched);

        // Set rather than slept for: the assertion is about the signature
        // reading the timestamp, not about how fine this filesystem's clock is.
        File.SetLastWriteTimeUtc(picture, File.GetLastWriteTimeUtc(picture).AddSeconds(5));

        Assert.NotEqual(before, LocalPersona.Signature(persona.Watched));
    }

    [Fact]
    public void APersonaWithNoPortraitWatchesOnlyWhatItRead()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Null(persona.AvatarPath);
        Assert.Equal(persona.Files, persona.Watched);
        Assert.Null(LocalPersona.Empty.AvatarPath);
        Assert.Empty(LocalPersona.Empty.Watched);
    }

    [Fact]
    public void EveryFileActuallyReadIsReported()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        var near = Write(child, "CLAUDE.md", "@extra.md");
        var extra = Write(child, "extra.md", "Her name is Leota");
        var far = Write(parent, "AGENTS.md", "nothing");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(new[] { near, extra, far }, persona.Files);
    }

    [Fact]
    public void AGatewaySessionResolvesToNothingWithoutTouchingTheDisk()
    {
        var persona = LocalPersona.Resolve(
            Dir("tree"), SessionSource.OpenClaw, Array.Empty<string>());

        Assert.Same(LocalPersona.Empty, persona);
        Assert.True(persona.IsEmpty);
        Assert.Empty(persona.Files);
    }

    [Fact]
    public void AFileWithNothingInItLeavesAnEmptyPersona()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "# Just a heading", "and some prose about the project.");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.True(persona.IsEmpty);
        Assert.Single(persona.Files);
    }

    // --- Resolve: a member's persona resolves as a unit (CB-191) -----------
    //
    // Before this, name/voice/rate/avatar were four independent first-wins
    // races over the whole candidate list. A team member's own profile —
    // naming itself but no picture — won the name race and then lost the
    // avatar race a few candidates later to the project's own CLAUDE.md. The
    // result wore the member's name and the project's face: two personas'
    // fields, stitched together.

    private void WriteProjectPersona(string project, string claudeDir)
    {
        File.WriteAllBytes(Path.Combine(claudeDir, "cto.png"), Png());
        Write(claudeDir, "PERSONA.MD", "## Attributes", "Name Jennifer", "Profile Photo cto.png");
        Write(project, "CLAUDE.md", "@.claude/PERSONA.MD");
    }

    // The ticket's own repro steps, and the negative control this ticket
    // asks for: on develop this fails, because AvatarPath resolves to
    // Jennifer's cto.png instead of staying null.
    [Fact]
    public void AMembersNameDoesNotBorrowTheProjectsPicture()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        Write(memberDir, "ines-harrow.md", "## Attributes", "Name Ines Harrow");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Ines Harrow", persona.Name);
        Assert.Null(persona.AvatarPath);
        Assert.Null(persona.AvatarSource);
    }

    // The other mixing direction: a member file naming only a picture must
    // not pick up the project's name either.
    [Fact]
    public void AMembersPictureDoesNotBorrowTheProjectsName()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        File.WriteAllBytes(Path.Combine(memberDir, "face.png"), Png());
        Write(memberDir, "ines-harrow.md", "## Attributes", "Profile Photo face.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        // No name at all — never the project's. OrbLabel's own fallback to
        // the agent's name is what puts a label on the orb; that is a
        // separate mechanism (see the OrbLabel tests below) and not this
        // Persona's own Name field.
        Assert.Null(persona.Name);
        Assert.Equal(Path.Combine(memberDir, "face.png"), persona.AvatarPath);
    }

    // Control: a session that is not a team member at all still gets the
    // project persona, name and picture together, exactly as before CB-154.
    [Fact]
    public void ASessionThatIsNotATeamMemberStillGetsTheProjectPersona()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Jennifer", persona.Name);
        Assert.Equal(Path.Combine(claudeDir, "cto.png"), persona.AvatarPath);
    }

    // Control, the other shape of "not a team member": an agent name is
    // given, but neither profiles/ nor .profiles-assets/ exists for it —
    // there is no member candidate to be exclusive, so this falls straight
    // through to the project persona exactly as the no-agent-name case does.
    [Fact]
    public void AnAgentNameWithNoMemberFileStillGetsTheProjectPersona()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Jennifer", persona.Name);
        Assert.Equal(Path.Combine(claudeDir, "cto.png"), persona.AvatarPath);
    }

    // Control: a member profile that states both fields keeps both — the fix
    // only has to stop mixing, not stop a member from having a picture.
    [Fact]
    public void AMemberWithBothANameAndAPictureGetsBoth()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        File.WriteAllBytes(Path.Combine(memberDir, "face.png"), Png());
        Write(memberDir, "ines-harrow.md", "## Attributes", "Name Ines Harrow", "Profile Photo face.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Ines Harrow", persona.Name);
        Assert.Equal(Path.Combine(memberDir, "face.png"), persona.AvatarPath);
    }

    // The two shapes profile-gen's --output can write for one agent —
    // profiles/<name>/<name>.md (tracked) and .profiles-assets/<name>/<name>.md
    // (gitignored) — split across both, same as CB-6's own storage.py cases.
    // On develop this already worked, as two independent first-wins races
    // over the whole candidate list; a version of this fix that decided
    // exclusivity candidate by candidate regressed it, returning as soon as
    // profiles/ alone said anything and never even reading the second file.
    // Both member candidates are read and folded together before that
    // decision runs, so this is the control that pins the regression QA
    // found rather than the ticket's own AC.
    [Fact]
    public void TheTwoMemberProfileShapesCombineAsOnePersonaRatherThanRacing()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var trackedDir = Dir("tree", "project", "profiles", "ines-harrow");
        Write(trackedDir, "ines-harrow.md", "## Attributes", "Name Ines Harrow");

        var gitignoredDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        File.WriteAllBytes(Path.Combine(gitignoredDir, "face.png"), Png());
        Write(gitignoredDir, "ines-harrow.md", "## Attributes", "Profile Photo face.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Ines Harrow", persona.Name);
        Assert.Equal(Path.Combine(gitignoredDir, "face.png"), persona.AvatarPath);
    }

    // The other half of the split-shape case: both member files exist and
    // are read together, and *neither* names a picture. The merge above
    // must not turn "both files read" into "go looking somewhere else" —
    // the run is still a member's own persona once it says anything, so it
    // stays exclusive, and the orb wears initials rather than the project's
    // face even though two files, not one, were tried and came up short on
    // exactly that field.
    [Fact]
    public void TheTwoMemberProfileShapesStillExcludeTheProjectsFaceWhenNeitherNamesAPicture()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var trackedDir = Dir("tree", "project", "profiles", "ines-harrow");
        Write(trackedDir, "ines-harrow.md", "## Attributes", "Name Ines Harrow");

        var gitignoredDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        Write(gitignoredDir, "ines-harrow.md", "## Attributes", "Name Ines Harrow Again");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        // profiles/ is read first, so it wins the name; .profiles-assets/
        // is still read (both files are in the run), but has nothing left
        // to contribute since the name is already taken.
        Assert.Equal("Ines Harrow", persona.Name);
        Assert.Null(persona.AvatarPath);
        Assert.Null(persona.AvatarSource);
    }

    // AC4: voice and rate follow the same rule as name and avatar — a
    // member's own file, once it says anything, is the whole answer, so a
    // member does not inherit the project's voice either.
    [Fact]
    public void AMembersNameDoesNotBorrowTheProjectsVoice()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        Write(claudeDir, "PERSONA.MD", "## Attributes", "Name Jennifer", "Her voice is Samantha");
        Write(project, "CLAUDE.md", "@.claude/PERSONA.MD");

        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        Write(memberDir, "ines-harrow.md", "## Attributes", "Name Ines Harrow");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Ines Harrow", persona.Name);
        Assert.Null(persona.Voice);
    }

    // A member candidate that exists but states nothing at all is not
    // "found" in the ticket's sense — it falls through exactly as a missing
    // file would, and the walk keeps folding the project's files behind it.
    [Fact]
    public void AMemberFileWithNothingInItFallsThroughToTheProject()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        Write(memberDir, "ines-harrow.md", "# Just a heading", "and some prose about the member.");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Jennifer", persona.Name);
        Assert.Equal(Path.Combine(claudeDir, "cto.png"), persona.AvatarPath);
    }

    // The other way a member file can be "found": naming a picture that
    // PersonaFiles cannot read (a typo, here — no file at that path). A
    // group that leaves AvatarPath null is not the same as a group that
    // said nothing at all, and treating it that way would fall through to
    // the project's CLAUDE.md and put the project lead's face back on this
    // member's orb — CB-191's exact symptom, reached through a broken
    // reference rather than an absent field.
    [Fact]
    public void AMemberNamingAnUnreadablePictureStillExcludesTheProjectsFaceEvenThoughItGetsNoPicture()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        // Front matter — RawAvatar, the field this fix reads, is set by
        // every labelled arm (front matter, a bullet, a bold field, a table
        // row, and the colon-less "## Attributes" form too, since
        // PersonaMarkdown.Parse's Assign now sets it for that arm alongside
        // the prose one — see PersonaMarkdown.cs). The sibling case right
        // below writes the colon-less shape instead, so both are pinned
        // rather than just the one this file already reached for elsewhere.
        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        Write(memberDir, "ines-harrow.md", "---", "image: \"typo.png\"", "---");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.NotEqual("Jennifer", persona.Name);
        Assert.Null(persona.Name);
        Assert.Null(persona.AvatarPath);
        Assert.Null(persona.AvatarSource);
    }

    // The colon-less sibling of the test above, and the one that actually
    // matters most: "## Attributes" / "Profile Photo <file>" is this
    // repository's own PERSONA.MD convention and, per PersonaMarkdown.cs's
    // own header comment, "the form people actually reach for when they are
    // *listing* attributes rather than addressing Claude" — so a member
    // profile is more likely to be written this way than in front matter.
    // Before PersonaMarkdown.Parse's Assign started setting RawAvatar for
    // this arm too, this exact fixture fell through to the project's
    // CLAUDE.md and put Jennifer's face on the member's orb, identical to
    // the bug this whole ticket exists to fix — just reached through a
    // grammar arm the fix's first pass never touched.
    [Fact]
    public void AMemberNamingAnUnreadablePictureInTheColonLessFormStillExcludesTheProjectsFace()
    {
        var project = Dir("tree", "project");
        var claudeDir = Dir("tree", "project", ".claude");
        WriteProjectPersona(project, claudeDir);

        var memberDir = Dir("tree", "project", ".profiles-assets", "ines-harrow");
        Write(memberDir, "ines-harrow.md", "## Attributes", "Profile Photo typo.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.NotEqual("Jennifer", persona.Name);
        Assert.Null(persona.Name);
        Assert.Null(persona.AvatarPath);
        Assert.Null(persona.AvatarSource);
    }

    // The monorepo layering TheNearestFileToNameAFieldOwnsIt already pins
    // must survive this ticket untouched: with no agent name anywhere, no
    // candidate is ever IsMemberProfile, so a nearer file's name is still
    // just a default the farther file's voice can fill in behind it — that
    // was never the bug CB-191 is about.
    [Fact]
    public void ANonMemberMonorepoStillLayersFieldsAcrossLevels()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        Write(parent, "CLAUDE.md", "Her name is Parent", "Her voice is Samantha");
        Write(child, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Leota", persona.Name);
        Assert.Equal("Samantha", persona.Voice);
    }

    // --- Signature: what the scan compares ---------------------------------

    [Fact]
    public void AnEditChangesTheSignature()
    {
        var project = Dir("tree", "project");
        var file = Write(project, "CLAUDE.md", "Her name is Leota");
        var before = LocalPersona.Signature(new[] { file });

        // The stat has to differ in something the signature reads, and a same-
        // length rewrite within one filesystem timestamp tick would not. Both
        // fields are in there precisely so either one changing is enough.
        File.WriteAllLines(file, new[] { "Her name is Leota", "Her voice is Bella" });

        Assert.NotEqual(before, LocalPersona.Signature(new[] { file }));
    }

    [Fact]
    public void ASignatureSaysWhetherEachFileIsThereAtAll()
    {
        var project = Dir("tree", "project");
        var file = Path.Combine(project, "CLAUDE.md");
        var missing = LocalPersona.Signature(new[] { file });

        Write(project, "CLAUDE.md", "Her name is Leota");

        Assert.NotEqual(missing, LocalPersona.Signature(new[] { file }));
    }

    [Fact]
    public void APathThePlatformRefusesIsAStateLikeAnyOther()
    {
        var signature = LocalPersona.Signature(new[] { "a\0b" });

        Assert.Contains("?", signature);
    }

    [Fact]
    public void TwoFilesWithTheSameStateDoNotCollideIntoOneSignature()
    {
        var project = Dir("tree", "project");
        var a = Write(project, "CLAUDE.md", "Her name is Leota");
        var b = Write(project, "AGENTS.md", "Her name is Leota");

        Assert.NotEqual(LocalPersona.Signature(new[] { a }), LocalPersona.Signature(new[] { b }));
        Assert.NotEqual(
            LocalPersona.Signature(new[] { a, b }), LocalPersona.Signature(new[] { b, a }));
    }

    // --- ResolveFrom: the fold over a list somebody else built --------------
    //
    // What used to be here was ResolveForSession, a wrapper that asked
    // UserConfigDirs for itself and applied the two guards
    // SessionManager.ApplyPersona had already applied a moment earlier. CB-143
    // removed it: the scan needs the candidate list twice in one pass and now
    // builds it once, so the fold takes the list rather than the question that
    // produces it, and the guards live in the one place that was always
    // checking them first. Both of them are covered where they now are —
    // LocalPersonaScanTests' AGatewaySessionIsNotGivenAPersona and
    // ASessionWithNoWorkingDirectoryIsNotGivenAPersona.

    [Fact]
    public void TheFoldTakesTheCandidateListItIsGiven()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "Her name is Leota", "Her voice is Bella");

        var persona = LocalPersona.ResolveFrom(
            LocalPersona.CandidateFiles(project, Array.Empty<string>(), SessionSource.ClaudeCode));

        Assert.Equal("Leota", persona.Name);
        Assert.Equal("Bella", persona.Voice);
    }

    // An empty list is the answer for a session there is nowhere to look for —
    // a gateway conversation, whose CandidateFiles is empty by the rule at the
    // top of that method. Empty by reference, because the registry's own change
    // check is a reference check and a fresh record every pass would drop a
    // decoded portrait every pass.
    [Fact]
    public void AnEmptyCandidateListIsTheEmptyPersonaItself()
    {
        Assert.Same(LocalPersona.Empty, LocalPersona.ResolveFrom(Array.Empty<string>()));
    }

    // --- UserConfigDirs -----------------------------------------------------

    [Fact]
    public void TheProcessesOwnConfigDirectoryIsAskedFirst()
    {
        var dirs = LocalPersona.UserConfigDirs("/custom/claude", "/home/w");

        Assert.Equal("/custom/claude", dirs[0]);
        Assert.Equal(Path.Combine("/home/w", ".claude"), dirs[1]);
    }

    [Fact]
    public void AProcessConfigDirectoryThatIsTheDefaultIsNotAskedTwice()
    {
        var home = "/home/w";

        var dirs = LocalPersona.UserConfigDirs(Path.Combine(home, ".claude"), home);

        Assert.Equal(Path.Combine(home, ".claude"), Assert.Single(dirs));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NoProcessConfigDirectoryLeavesTheAccountsAsTheWholeAnswer(string unset)
    {
        var dirs = LocalPersona.UserConfigDirs(unset, "/home/w");

        Assert.Equal(Path.Combine("/home/w", ".claude"), Assert.Single(dirs));
    }

    // The default argument is the process environment, which is what production
    // gets. Asserted only on the account list, because another test in another
    // class may legitimately be holding CLAUDE_CONFIG_DIR at a sentinel while
    // this runs — see ConfigDirEnvCollection.cs — and what that variable
    // happens to say is not this test's claim.
    [Fact]
    public void TheEnvironmentIsWhereTheProcessConfigDirectoryComesFrom()
    {
        var dirs = LocalPersona.UserConfigDirs(home: "/home/w");

        Assert.Contains(Path.Combine("/home/w", ".claude"), dirs);
    }

    // --- OrbLabel -----------------------------------------------------------

    [Theory]
    // An agent's own name beats everything: every member of a team inherits the
    // team session's title, so without it a team of four draws one letter four
    // times.
    [InlineData("MenuUX", "Leota", "Fixing the flyout", "claude-buddy", "MenuUX")]
    // The persona is what the project says its assistant is called — more
    // specific than a generated chat title, less specific than an assigned role.
    [InlineData("", "Leota", "Fixing the flyout", "claude-buddy", "Leota")]
    [InlineData("", "", "Fixing the flyout", "claude-buddy", "Fixing the flyout")]
    [InlineData("", "", "", "claude-buddy", "claude-buddy")]
    [InlineData(null, null, null, null, "")]
    [InlineData(null, "Leota", null, "claude-buddy", "Leota")]
    public void TheOrbWearsTheMostSpecificNameItHas(
        string? agent, string? persona, string? title, string? folder, string expected)
    {
        Assert.Equal(expected, LocalPersona.OrbLabel(agent, persona, title, folder));
    }

    // --- LocalPersonas: the live registry -----------------------------------

    private static LocalPersona.Persona Named(string name, string? voice = null, double? rate = null) =>
        new(name, voice, rate, null, null, Array.Empty<string>());

    [Fact]
    public void APersonaIsRememberedAgainstItsSession()
    {
        var id = "sess-" + Guid.NewGuid();
        var persona = Named("Leota");

        LocalPersonas.Set(id, persona);

        Assert.Same(persona, LocalPersonas.For(id));
        Assert.Equal("local:" + id, LocalPersonas.AvatarKey(id));
    }

    [Fact]
    public void SettingTheSamePersonaObjectAgainLeavesEverythingAlone()
    {
        var id = "sess-" + Guid.NewGuid();
        var persona = Named("Leota");

        LocalPersonas.Set(id, persona);
        // The early return this exercises is what stops the scan dropping the
        // decoded avatar twice a second: the scan hands back one persona object
        // per session until the files under it change.
        LocalPersonas.Set(id, persona);

        Assert.Same(persona, LocalPersonas.For(id));
    }

    [Fact]
    public void ADifferentPersonaReplacesTheOneThatWasThere()
    {
        var id = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(id, Named("Leota"));

        var replacement = Named("Aurora");
        LocalPersonas.Set(id, replacement);

        Assert.Same(replacement, LocalPersonas.For(id));
    }

    [Fact]
    public void AForgottenSessionHasNoPersona()
    {
        var id = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(id, Named("Leota"));

        LocalPersonas.Forget(id);

        Assert.Null(LocalPersonas.For(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-set")]
    public void ASessionNobodyHasSetHasNoPersona(string? id)
    {
        Assert.Null(LocalPersonas.For(id));
    }

    [Fact]
    public void TheTestSeamReplacesTheWholeTable()
    {
        var stale = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(stale, Named("Stale"));

        var fresh = "sess-" + Guid.NewGuid();
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
        {
            [fresh] = Named("Leota"),
        });

        Assert.Null(LocalPersonas.For(stale));
        Assert.Equal("Leota", LocalPersonas.For(fresh)!.Name);

        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
        Assert.Null(LocalPersonas.For(fresh));
    }

    // --- LocalPersonas: speech ---------------------------------------------

    private static readonly TextToSpeech.VoiceOption Bella =
        new(TextToSpeech.SpeakEngine.Neural, "af_bella", "af_bella (Kokoro)");
    private static readonly TextToSpeech.VoiceOption Samantha =
        new(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha (system)");

    [Fact]
    public void APersonaVoiceResolvesOverWhateverThisMachineCanSpeakWith()
    {
        var id = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(id, Named("Leota", "Bella", 1.2));

        Assert.Equal(Bella, LocalPersonas.VoiceForSession(id, new[] { Bella, Samantha }));
        Assert.Equal(1.2, LocalPersonas.RateForSession(id));
    }

    // Three different ways of having no voice, all of which mean "use the
    // user's own": no persona at all, a persona that names none, and a name
    // that matches nothing installed here.
    [Fact]
    public void EveryWayOfHavingNoVoiceResolvesToNothing()
    {
        var options = new[] { Bella, Samantha };

        Assert.Null(LocalPersonas.VoiceForSession("never-set", options));
        Assert.Null(LocalPersonas.RateForSession("never-set"));

        var silent = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(silent, Named("Leota"));
        Assert.Null(LocalPersonas.VoiceForSession(silent, options));
        Assert.Null(LocalPersonas.RateForSession(silent));

        var unmatched = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(unmatched, Named("Leota", "Not Installed Here"));
        Assert.Null(LocalPersonas.VoiceForSession(unmatched, options));

        // The engine being switched off is the same answer arriving a different
        // way: with no neural options in the list there is nothing for a Kokoro
        // name to resolve to.
        var neuralOff = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(neuralOff, Named("Leota", "Bella"));
        Assert.Null(LocalPersonas.VoiceForSession(neuralOff, new[] { Samantha }));
    }
}

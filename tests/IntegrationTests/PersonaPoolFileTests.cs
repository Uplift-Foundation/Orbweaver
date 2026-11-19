using Xunit;

namespace Orbweaver.Tests;

// The persona pool against real trees: where a team member's persona is found
// when it is not in the project it is working in.
//
// A team member is its own `claude` process named by --agent-name, and until
// the pool the only places its persona was looked up were
// profiles/<name>/<name>.md in the working directory and above. A pool kept in
// an account's config directory, or in one folder shared by every project, was
// invisible, so every teammate drew in the main session's face (four orbs, one
// portrait, on a real WSL team). Each test below is one thing the lookup
// promises, resolved through the same fold the app uses.
[Collection("LogDir")]
public class PersonaPoolFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-pool-" + Guid.NewGuid());

    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-pool-log-" + Guid.NewGuid());

    private readonly IDisposable _logScope;

    public PersonaPoolFileTests()
    {
        Directory.CreateDirectory(_root);
        _logScope = CrashLog.ScopeForTests(_logDir);
        PersonaLog.ResetForTests();
    }

    public void Dispose()
    {
        _logScope.Dispose();
        PersonaLog.ResetForTests();

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    // A persona the way profile-gen writes a tracked one: YAML front matter, the
    // picture relative to the root the profiles/ folder sits in.
    private void WriteProfile(string root, string name, string display, string? voice = null)
    {
        var dir = Dir(Path.GetRelativePath(_root, root), "profiles", name);
        File.WriteAllText(Path.Combine(dir, name + ".md"),
            $"---\nschema_version: 1\nname: \"{display}\"\nslug: \"{name}\"\n"
            + $"image: \"profiles/{name}/{name}.png\"\n"
            + (voice is null ? "" : $"voice: \"{voice}\"\n")
            + "---\n\n# " + display + "\n");
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), Png());
    }

    // Bounded at this test's own scratch root, never null. On Windows the temp
    // directory is under the developer's home, so an unbounded walk from a
    // scratch project climbs into the real ~/CLAUDE.md and the persona it
    // imports, and every test here expecting "no persona" or its own fixture
    // is handed the developer's instead. LocalPersona.WalkCeilingVariable
    // guards the suites that go through Candidates(); this class calls
    // CandidatesBelow directly, so it has to pass the ceiling itself. Nine of
    // these failed on a real Windows box and never on CI, whose runner's home
    // holds no persona.
    private LocalPersona.Persona Resolve(string cwd, string[] configDirs, string? pool, string agent) =>
        LocalPersona.ResolveFrom(
            LocalPersona.CandidatesBelow(cwd, configDirs, SessionSource.ClaudeCode, agent, ceiling: _root, poolRoot: pool),
            cwd);

    [Fact]
    public void ASuffixedMembersOwnFileDoesNotBorrowTheBasePersonasVoice()
    {
        var pool = Dir("pool");
        WriteProfile(pool, "juno", "Juno", voice: "base-voice");
        WriteProfile(pool, "juno-2", "Juno Two");
        var p = Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-2");
        Assert.Equal("Juno Two", p.Name);
        Assert.Null(p.Voice);
        Assert.Equal("base-voice", Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-3").Voice);
    }

    [Theory]
    [InlineData("juno-2x")]
    [InlineData("juno-v2")]
    [InlineData("juno-")]
    public void ANonDigitSuffixIsNotARespawnAndGetsNoPersona(string agent)
    {
        var pool = Dir("pool");
        WriteProfile(pool, "juno", "Juno");
        Assert.True(Resolve(Dir("work"), Array.Empty<string>(), pool, agent).IsEmpty);
    }

    [Fact]
    public void TheBasePersonasSlugInPersonaMdIsOfferedToARespawnedMemberButNotAVersion()
    {
        var pool = Dir("pool");
        var dir = Dir("pool", "persona");
        File.WriteAllText(Path.Combine(dir, "persona.md"), "---\nname: \"Juno\"\nslug: \"juno\"\n---\n");
        Assert.Equal("Juno", Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-2").Name);
        Assert.True(Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-v2").IsEmpty);
    }

    [Fact]
    public void ATeamMemberIsFoundInTheSharedPoolFromAnyProject()
    {
        var pool = Dir("pool");
        WriteProfile(pool, "atlas", "Atlas");
        var project = Dir("work", "unrelated");

        var persona = Resolve(project, Array.Empty<string>(), pool, "atlas");

        Assert.Equal("Atlas", persona.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(pool, "profiles", "atlas", "atlas.png")), persona.AvatarPath);

        // Watched, so editing a pool persona's picture is noticed.
        Assert.Contains(persona.AvatarPath!, persona.Watched);
    }

    [Fact]
    public void ATeamMemberIsNotFoundInAPoolThatNamesSomeoneElse()
    {
        var pool = Dir("pool");
        WriteProfile(pool, "atlas", "Atlas");

        Assert.True(Resolve(Dir("work"), Array.Empty<string>(), pool, "vector").IsEmpty);
    }

    [Fact]
    public void AnInstanceOfAPersonaWearsThatPersona()
    {
        // `atlas_2` is the second live instance of Atlas: the portrait and name
        // are Atlas's, though no file is called atlas_2.
        var pool = Dir("pool");
        WriteProfile(pool, "atlas", "Atlas");

        var persona = Resolve(Dir("work"), Array.Empty<string>(), pool, "atlas_2");

        Assert.Equal("Atlas", persona.Name);
        Assert.NotNull(persona.AvatarPath);
    }

    [Fact]
    public void ARespawnedMemberWithAHyphenSuffixWearsItsOriginalPersona()
    {
        // CB-288: `juno-abernathy-2` has no directory under .profiles-assets,
        // but `juno-abernathy` does, and it must not fall back to the lead.
        var pool = Dir("pool");
        WriteProfile(pool, "juno-abernathy", "Juno Abernathy");

        var persona = Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-abernathy-2");

        Assert.Equal("Juno Abernathy", persona.Name);
        Assert.NotNull(persona.AvatarPath);
    }

    // profile-gen's gitignored layout: .profiles-assets/<name>/<name>.md, the
    // picture relative to the root the folder sits in.
    private void WriteAssetsProfile(string root, string name, string display)
    {
        var dir = Dir(Path.GetRelativePath(_root, root), ".profiles-assets", name);
        File.WriteAllText(Path.Combine(dir, name + ".md"),
            $"---\nschema_version: 1\nname: \"{display}\"\nslug: \"{name}\"\n"
            + $"image: \".profiles-assets/{name}/{name}.png\"\n---\n\n# {display}\n");
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), Png());
    }

    // The lead: a project CLAUDE.md that imports a main-session persona.
    private string WriteLeadProject()
    {
        var project = Dir("work", "project");
        var lead = Dir("work", "project", "persona");
        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "@persona/persona.md\n");
        File.WriteAllText(Path.Combine(lead, "persona.md"),
            "---\nname: \"Jennifer Voss\"\nslug: \"jennifer-voss\"\nimage: \"persona/lead.png\"\n---\n");
        File.WriteAllBytes(Path.Combine(lead, "lead.png"), Png());
        return project;
    }

    [Fact]
    public void ARespawnedTeammateInAProjectWearsItsOwnPersonaNotTheLeads()
    {
        // CB-288 as it happened: the project's .profiles-assets holds
        // juno-abernathy, the member came up as juno-abernathy-2, and it drew
        // the lead's face because the lead's CLAUDE.md was all that matched.
        var project = WriteLeadProject();
        WriteAssetsProfile(project, "juno-abernathy", "Juno Abernathy");

        var persona = Resolve(project, Array.Empty<string>(), null, "juno-abernathy-2");

        Assert.Equal("Juno Abernathy", persona.Name);
        Assert.Contains("juno-abernathy", persona.AvatarPath);
        Assert.DoesNotContain("lead.png", persona.AvatarPath);
    }

    [Fact]
    public void ARespawnedTeammatesOwnAssetsFileStillWinsAndAVersionFallsToTheLead()
    {
        var project = WriteLeadProject();
        WriteAssetsProfile(project, "juno", "Juno");
        WriteAssetsProfile(project, "juno-2", "Juno Two");

        Assert.Equal("Juno Two", Resolve(project, Array.Empty<string>(), null, "juno-2").Name);
        Assert.Equal("Jennifer Voss", Resolve(project, Array.Empty<string>(), null, "juno-v2").Name);
    }

    [Fact]
    public void AHyphenSuffixedMembersOwnFileWinsAndAVersionIsNotStripped()
    {
        var pool = Dir("pool");
        WriteProfile(pool, "juno", "Juno");
        WriteProfile(pool, "juno-2", "Juno Two");

        Assert.Equal("Juno Two", Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-2").Name);
        Assert.Equal("Juno", Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-12").Name);
        Assert.True(Resolve(Dir("work"), Array.Empty<string>(), pool, "juno-v2").IsEmpty);
    }

    [Fact]
    public void AnInstanceThatHasItsOwnFileWearsItsOwnPersona()
    {
        // A persona whose real name ends in _2 finds its own file first.
        var pool = Dir("pool");
        WriteProfile(pool, "atlas", "Atlas");
        WriteProfile(pool, "atlas_2", "Atlas the Younger");

        Assert.Equal("Atlas the Younger", Resolve(Dir("work"), Array.Empty<string>(), pool, "atlas_2").Name);
        Assert.Equal("Atlas", Resolve(Dir("work"), Array.Empty<string>(), pool, "atlas_3").Name);
    }

    [Fact]
    public void AnAccountsOwnPoolBeatsTheSharedPoolAndIsNotMergedWithIt()
    {
        // The nearer file replaces the farther one entirely. The account's Atlas
        // names no voice; the shared pool's does. The result must not be an
        // account name wearing the shared pool's voice, which is neither file.
        var config = Dir("config");
        var pool = Dir("pool");
        WriteProfile(config, "atlas", "Atlas (account)");
        WriteProfile(pool, "atlas", "Atlas (shared)", voice: "am_michael");

        var persona = Resolve(Dir("work"), new[] { config }, pool, "atlas");

        Assert.Equal("Atlas (account)", persona.Name);
        Assert.Null(persona.Voice);
    }

    [Fact]
    public void TwoAccountsPoolsAreTwoPersonasNotOne()
    {
        var first = Dir("config-a");
        var second = Dir("config-b");
        WriteProfile(first, "atlas", "Atlas A");
        WriteProfile(second, "atlas", "Atlas B", voice: "am_michael");

        var persona = Resolve(Dir("work"), new[] { first, second }, null, "atlas");

        Assert.Equal("Atlas A", persona.Name);
        Assert.Null(persona.Voice);
    }

    [Fact]
    public void ADirectorysTwoPlacesAreTwoPersonasAndTheOlderLayoutWinsEntirely()
    {
        // The project has the persona in profile-gen's own layout and in its
        // .orbweaver folder. They are two files saying two different things, and
        // the nearer-listed one must be the whole answer: a name from one with a
        // voice from the other is a persona neither file describes.
        var project = Dir("work", "project");
        var orbweaver = Dir("work", "project", ".orbweaver");
        WriteProfile(project, "atlas", "Atlas (profiles)");
        WriteProfile(orbweaver, "atlas", "Atlas (orbweaver)", voice: "Samantha");

        var persona = Resolve(project, Array.Empty<string>(), null, "atlas");

        Assert.Equal("Atlas (profiles)", persona.Name);
        Assert.Null(persona.Voice);
    }

    [Fact]
    public void AnInstancesOwnFileReplacesItsPersonasOutrightRatherThanLendingItAName()
    {
        // vector_2 has its own file, with a name and no voice; vector has a voice.
        // The instance is the whole answer, not V2's name speaking in Vector's voice.
        var config = Dir("config");
        WriteProfile(config, "vector_2", "Vector the Second");
        WriteProfile(config, "vector", "Vector", voice: "bm_george");

        var persona = Resolve(Dir("work"), new[] { config }, null, "vector_2");

        Assert.Equal("Vector the Second", persona.Name);
        Assert.Null(persona.Voice);
    }

    [Fact]
    public void ProfileGensTwoLayoutsOfOnePersonaStillFoldTogether()
    {
        // The other direction, so the fix above is not "never merge anything":
        // a name in profiles/ and a voice in .profiles-assets/ for the SAME
        // persona are one persona written in two layouts, and resolve together.
        var project = Dir("work", "project");
        var named = Dir("work", "project", "profiles", "atlas");
        File.WriteAllText(Path.Combine(named, "atlas.md"), "---\nname: \"Atlas\"\nslug: \"atlas\"\n---\n");
        var assets = Dir("work", "project", ".profiles-assets", "atlas");
        File.WriteAllText(Path.Combine(assets, "atlas.md"), "---\nvoice: \"am_michael\"\n---\n");

        var persona = Resolve(project, Array.Empty<string>(), null, "atlas");

        Assert.Equal("Atlas", persona.Name);
        Assert.Equal("am_michael", persona.Voice);
    }

    [Fact]
    public void TheProjectsOwnOrbweaverFolderBeatsBothPools()
    {
        var project = Dir("work", "project");
        var config = Dir("config");
        var pool = Dir("pool");
        WriteProfile(Dir("work", "project", ".orbweaver"), "atlas", "Atlas (project)");
        WriteProfile(config, "atlas", "Atlas (account)");
        WriteProfile(pool, "atlas", "Atlas (shared)");

        Assert.Equal("Atlas (project)", Resolve(project, new[] { config }, pool, "atlas").Name);
    }

    [Fact]
    public void ThePoolPersonaBeatsTheMainSessionsPersonaInTheAccountsClaudeMd()
    {
        // The reason for all of this: the account's CLAUDE.md describes the main
        // session (Echo), and a teammate named Nova must not inherit it.
        var config = Dir("config");
        var echoDir = Dir("config", "persona");
        File.WriteAllText(Path.Combine(config, "CLAUDE.md"), "@persona/persona.md\n");
        File.WriteAllText(Path.Combine(echoDir, "persona.md"),
            "---\nname: \"Echo\"\nslug: \"echo\"\nimage: \"persona/echo.png\"\n---\n");
        File.WriteAllBytes(Path.Combine(echoDir, "echo.png"), Png());
        var pool = Dir("pool");
        WriteProfile(pool, "nova", "Nova");

        Assert.Equal("Nova", Resolve(Dir("work"), new[] { config }, pool, "nova").Name);

        // Without a teammate name nothing changes: the main session is Echo.
        Assert.Equal("Echo", Resolve(Dir("work"), new[] { config }, pool, "").Name);
    }

    [Fact]
    public void ProfileGensFixedPersonaFileIsRecognisedBySlugAndOnlyBySlug()
    {
        // `persona/persona.md` is the same path for whoever the persona is, so
        // it is only offered to the agent its own slug names — and never handed
        // to a teammate called something else, even with nothing else to find.
        var config = Dir("config");
        var echoDir = Dir("config", "persona");
        File.WriteAllText(Path.Combine(echoDir, "persona.md"),
            "---\nname: \"Echo\"\nslug: \"echo\"\nimage: \"persona/echo.png\"\n---\n");
        File.WriteAllBytes(Path.Combine(echoDir, "echo.png"), Png());

        var echo = Resolve(Dir("work"), new[] { config }, null, "echo");
        Assert.Equal("Echo", echo.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(echoDir, "echo.png")), echo.AvatarPath);

        Assert.Equal("Echo", Resolve(Dir("work"), new[] { config }, null, "echo_2").Name);
        Assert.True(Resolve(Dir("work"), new[] { config }, null, "nova").IsEmpty);
    }

    [Fact]
    public void ASessionThatIsNotATeamMemberNeverReadsThePool()
    {
        var pool = Dir("pool");
        WriteProfile(pool, "atlas", "Atlas");

        Assert.True(Resolve(Dir("work"), Array.Empty<string>(), pool, "").IsEmpty);
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    public void OnlyClaudeCodeTeamMembersReadThePool(SessionSource source)
    {
        // Agent teams are a Claude Code concept, and a gateway session's cwd is
        // somebody else's disk (CB-140), so the pool is not offered to either.
        var pool = Dir("pool");
        WriteProfile(pool, "atlas", "Atlas");

        var candidates = LocalPersona.CandidatesBelow(Dir("work"), Array.Empty<string>(), source, "atlas", null, pool);

        Assert.DoesNotContain(candidates, candidate => candidate.Path.StartsWith(pool, StringComparison.Ordinal));
    }
}

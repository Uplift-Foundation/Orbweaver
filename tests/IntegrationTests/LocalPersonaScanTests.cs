using Xunit;

namespace Orbweaver.Tests;

// The scan's half of the persona feature, driven against a real filesystem.
//
// LocalPersonaFilesTests covers what the reader refuses; the unit suites cover
// the grammar and the fold. What none of them can answer is the question this
// one is about: does the thing that runs twice a second actually put a persona
// in the registry, does it stop reading the disk once it has, and does it
// notice when somebody edits the file — including a file that is only reached
// through an `@` import, which is not a candidate and so is invisible to the
// candidate list alone.
//
// SessionManager.ApplyPersona rather than ScanAndUpdate, deliberately.
// ScanAndUpdate builds OrbWindows and needs an Avalonia application, which is
// why SessionScanTests lives in tests/UiTests — and there is an end-to-end
// scan-to-registry case there for exactly that reason. Everything ApplyPersona
// does is filesystem, so it belongs here, where the filesystem is real and no
// window has to exist for it to run.
//
// The user-level config directories are PINNED, and that is CB-143's change.
//
// ApplyPersona used to ask LocalPersona.UserConfigDirs on every pass, which
// reads two pieces of process-wide mutable state: the CLAUDE_CONFIG_DIR
// environment variable and ClaudeCodeProfileDirs by way of ClaudeConfigRoots.
// Either one changing between two passes lengthens the candidate list, which
// changes the signature, which is the scan's definition of "something moved" —
// so the persona resolved again and the registry got a brand-new object with
// identical content. Every case below whose claim is "the same object came
// back" then fails with nothing wrong with it and nothing in its own fixture to
// point at.
//
// That is not hypothetical and it is not only a settings problem.
// AnImportedFileSettlesRatherThanResolvingEveryPass failed once on the Windows
// CI leg and passed twice on the identical sha, and the mechanism was the
// environment variable: UsagePollerEnvironmentTests and
// AgentRosterEnvironmentTests are [Collection("ConfigDirEnv")], a *different*
// collection, which xUnit runs in parallel with this one, and the first of
// those holds CLAUDE_CONFIG_DIR at a sentinel while a stand-in child process
// runs. One extra record in the signature — a config directory that does not
// even exist — is the whole of it.
//
// The two writers were reachable by two different collection attributes, and a
// third attribute would only have closed one of them. Pinning the provider
// closes both at once and closes them structurally: this scan no longer reads
// either global, so there is nothing left for a future writer of either to
// disturb. Array.Empty rather than the machine's real answer for a second
// reason — without it these cases read the developer's own ~/.claude/CLAUDE.md,
// so a persona written there would break AProjectWithNoPersonaIsRecordedAsHavingNone
// on that machine and nowhere else. The user-level arm is covered on purpose
// below, against a fixture directory, instead of by accident against a real one.
//
// [Collection("Settings")] is kept as a second line rather than as the
// mechanism: the seam is what makes the claims independent, and the attribute
// only still costs nothing.
//
// LocalPersonas is process-wide too, so every case uses its own session id and
// empties the registry afterwards.
[Collection("Settings")]
public class LocalPersonaScanTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "cb-persona-scan-" + Guid.NewGuid());

    private readonly string _statusDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-status-" + Guid.NewGuid());

    // A stand-in for ~/.claude: a user-level config directory this fixture owns
    // outright, so the cases that need one can have one without the answer
    // depending on whose machine the suite is running on.
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-config-" + Guid.NewGuid());

    private readonly string _sessionId = "persona-scan-" + Guid.NewGuid();

    public LocalPersonaScanTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_statusDir);
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        LocalPersonas.Forget(_sessionId);
        try { Directory.Delete(_project, recursive: true); } catch { }
        try { Directory.Delete(_statusDir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private void WriteMarkdown(string name, string body) =>
        File.WriteAllText(Path.Combine(_project, name), body);

    private SessionStatus Status(SessionSource source = SessionSource.ClaudeCode, string agent = "") => new()
    {
        Source = source,
        State = "idle",
        Cwd = _project,
        Title = "cb-persona-scan",
        Agent = agent,
    };

    // One dictionary per pass, the same way ScanAndUpdate makes one per tick.
    private Dictionary<(string Cwd, SessionSource Source, string Agent), IReadOnlyList<LocalPersona.Candidate>> Pass() => new();

    // Nothing above the project tree, unless a case says otherwise. See the
    // header: this is the pin that makes every settling claim below independent
    // of what else in the process is running.
    private SessionManager Manager(IReadOnlyList<string>? userConfigDirs = null) =>
        new(_statusDir, null, userConfigDirs: () => userConfigDirs ?? Array.Empty<string>());

    // The whole point, end to end through the method the timer calls: a
    // sentence in a file on disk becomes a name in the registry the orb reads.
    [Fact]
    public void AProjectsClaudeMdPutsItsPersonaInTheRegistry()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\nHer name is Leota.\n");

        Manager().ApplyPersona(_sessionId, Status(), Pass());

        var persona = LocalPersonas.For(_sessionId);
        Assert.NotNull(persona);
        Assert.Equal("Leota", persona!.Name);
        Assert.Contains(Path.Combine(_project, "CLAUDE.md"), persona.Files.Select(Path.GetFullPath));
    }

    // CB-154, the ticket's own reproduction: two team members sharing one
    // project cwd, one with its own profiles/<name>/<name>.md — the file
    // real teams already have, written by profile-gen's write_profile.py
    // --output file — resolve to two different personas rather than the
    // second collapsing onto whichever the shared cache entry happened to
    // hold first. Front matter, not a sentence, because that is the literal
    // shape write_profile.py writes.
    [Fact]
    public void TwoTeamMembersInOneCwdCanEachHaveTheirOwnPersona()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");
        Directory.CreateDirectory(Path.Combine(_project, "profiles", "helena-marsh"));
        File.WriteAllText(
            Path.Combine(_project, "profiles", "helena-marsh", "helena-marsh.md"),
            "---\nname: \"Constance\"\n---\n\n# Constance\n");

        var manager = Manager();
        var pass = Pass();
        var leadId = _sessionId + "-lead";
        var memberId = _sessionId + "-member";

        try
        {
            manager.ApplyPersona(leadId, Status(), pass);
            manager.ApplyPersona(memberId, Status(agent: "helena-marsh"), pass);

            Assert.Equal("Leota", LocalPersonas.For(leadId)!.Name);
            Assert.Equal("Constance", LocalPersonas.For(memberId)!.Name);
        }
        finally
        {
            LocalPersonas.Forget(leadId);
            LocalPersonas.Forget(memberId);
        }
    }

    // The other half of the same claim: a member with no file of its own still
    // falls through to the project's shared persona, unchanged from before
    // this ticket — this is a fallback, not a requirement that every member
    // name one.
    [Fact]
    public void ATeamMemberWithNoOwnFileFallsBackToTheProjectsPersona()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var manager = Manager();
        var pass = Pass();
        var memberId = _sessionId + "-member";

        try
        {
            manager.ApplyPersona(memberId, Status(agent: "SomeoneElse"), pass);
            Assert.Equal("Leota", LocalPersonas.For(memberId)!.Name);
        }
        finally
        {
            LocalPersonas.Forget(memberId);
        }
    }

    // CB-291, the scan's half of the persona pool: a team member named for a
    // persona kept in the user's shared pool — outside the project, named by
    // ORBWEAVER_PERSONAS the way a user would name it — gets that persona
    // instead of the project's, and the lead beside it is untouched. The
    // environment variable is set rather than a seam handed over because the
    // variable is what the user types, and the whole chain from it to the
    // registry is the claim.
    [Fact]
    public void ATeamMemberIsGivenItsPersonaFromTheSharedPoolTheEnvironmentNames()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");
        var pool = Path.Combine(Path.GetTempPath(), "cb-persona-pool-scan-" + Guid.NewGuid());
        var profile = Path.Combine(pool, "profiles", "atlas");
        Directory.CreateDirectory(profile);
        var file = Path.Combine(profile, "atlas.md");
        File.WriteAllText(file, "---\nname: \"Atlas\"\nslug: \"atlas\"\n---\n");

        var was = Environment.GetEnvironmentVariable(PersonaPool.EnvironmentVariable);
        var manager = Manager();
        var leadId = _sessionId + "-lead";
        var memberId = _sessionId + "-pool-member";

        try
        {
            Environment.SetEnvironmentVariable(PersonaPool.EnvironmentVariable, pool);

            manager.ApplyPersona(leadId, Status(), Pass());
            manager.ApplyPersona(memberId, Status(agent: "atlas"), Pass());

            Assert.Equal("Leota", LocalPersonas.For(leadId)!.Name);
            var member = LocalPersonas.For(memberId)!;
            Assert.Equal("Atlas", member.Name);
            Assert.Contains(file, member.Files.Select(Path.GetFullPath));

            // Edited in place — a longer name, so the size moves even on a
            // filesystem with coarse timestamps — and noticed on the next pass,
            // because the pool's file is one the scan's signature watches.
            File.WriteAllText(file, "---\nname: \"Atlas Prime\"\nslug: \"atlas\"\n---\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            manager.ApplyPersona(memberId, Status(agent: "atlas"), Pass());

            Assert.Equal("Atlas Prime", LocalPersonas.For(memberId)!.Name);

            // A second instance of the same persona wears the same persona.
            var secondId = _sessionId + "-pool-second";
            try
            {
                manager.ApplyPersona(secondId, Status(agent: "atlas_2"), Pass());
                Assert.Equal("Atlas Prime", LocalPersonas.For(secondId)!.Name);
            }
            finally
            {
                LocalPersonas.Forget(secondId);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersonaPool.EnvironmentVariable, was);
            LocalPersonas.Forget(leadId);
            LocalPersonas.Forget(memberId);
            try { Directory.Delete(pool, recursive: true); } catch { }
        }
    }

    // The same session with nothing in the pool is exactly what it was: the
    // fallback to the project's persona that a member with no file of its own
    // has always had.
    [Fact]
    public void ATeamMemberThePoolDoesNotNameStillFallsBackToTheProjectsPersona()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");
        var pool = Path.Combine(Path.GetTempPath(), "cb-persona-pool-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(pool);

        var was = Environment.GetEnvironmentVariable(PersonaPool.EnvironmentVariable);
        var memberId = _sessionId + "-pool-nobody";

        try
        {
            Environment.SetEnvironmentVariable(PersonaPool.EnvironmentVariable, pool);

            Manager().ApplyPersona(memberId, Status(agent: "nobody-in-the-pool"), Pass());

            Assert.Equal("Leota", LocalPersonas.For(memberId)!.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersonaPool.EnvironmentVariable, was);
            LocalPersonas.Forget(memberId);
            try { Directory.Delete(pool, recursive: true); } catch { }
        }
    }

    // The cache, stated as the thing it actually promises. Not "it is fast" —
    // that is unassertable — but "the second pass did not resolve again", which
    // is visible as the very same Persona object still being in the registry.
    // A rebuild would put an equal-but-different one there, and would also make
    // LocalPersonas.Set evict a decoded portrait on every tick forever.
    [Fact]
    public void ASecondPassOverUnchangedFilesDoesNotResolveAgain()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var manager = Manager();
        var pass = Pass();

        manager.ApplyPersona(_sessionId, Status(), pass);
        var first = LocalPersonas.For(_sessionId);

        manager.ApplyPersona(_sessionId, Status(), pass);
        var second = LocalPersonas.For(_sessionId);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    // An edit is picked up on the next pass. The replacement name is a
    // different length as well as a different word, so this does not depend on
    // the filesystem's timestamp resolution being finer than the gap between
    // two statements — a same-length rewrite inside one mtime tick is a real
    // hole, and one this test would silently stop covering if it relied on the
    // timestamp alone.
    [Fact]
    public void EditingTheMarkdownIsPickedUpOnTheNextPass()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);

        WriteMarkdown("CLAUDE.md", "Her name is Constance.\n");
        manager.ApplyPersona(_sessionId, Status(), Pass());

        Assert.Equal("Constance", LocalPersonas.For(_sessionId)!.Name);
    }

    // The case the candidate list cannot see on its own. `persona.md` is not a
    // candidate — no directory walk would ever name it — and it is only read
    // because the CLAUDE.md imports it. Watching the candidates alone would
    // leave an edit here invisible until something else in the tree moved,
    // which is why the signature is taken over the files the last read actually
    // followed as well.
    [Fact]
    public void EditingAnImportedFileIsPickedUpToo()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\n@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);

        WriteMarkdown("persona.md", "Her name is Constance.\n");
        manager.ApplyPersona(_sessionId, Status(), Pass());

        Assert.Equal("Constance", LocalPersonas.For(_sessionId)!.Name);
    }

    // ...and having noticed the import once, it settles: an unchanged pass
    // after an imported file has been read must not resolve again either. This
    // is the arm that catches the off-by-one in *what* is being compared — the
    // first pass has no previous read to take imports from, so its signature is
    // over the candidates alone, and storing that would make every second pass
    // differ from every first one, forever.
    [Fact]
    public void AnImportedFileSettlesRatherThanResolvingEveryPass()
    {
        WriteMarkdown("CLAUDE.md", "@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = Manager();

        manager.ApplyPersona(_sessionId, Status(), Pass());
        var first = LocalPersonas.For(_sessionId);

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    // A picture beside the markdown reaches the registry as a path — not as
    // bytes, which is CB-135's change and is asserted on its own further down.
    // The reader's refusals are LocalPersonaFilesTests' subject; what this adds
    // is that the scan asks for one at all.
    [Fact]
    public void APictureBesideTheMarkdownReachesTheRegistry()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(Path.Combine(_project, "leota.png"), new byte[] { 1, 2, 3, 4 });

        Manager().ApplyPersona(_sessionId, Status(), Pass());

        var persona = LocalPersonas.For(_sessionId);
        Assert.Equal(Path.Combine(_project, "leota.png"), persona!.AvatarPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(persona.AvatarPath!));
    }

    // The case markdown alone cannot see, and the one that shipped broken. A
    // portrait replaced in place changes no markdown file: same filename, new
    // bytes, CLAUDE.md untouched. A signature over the markdown is therefore
    // identical, the cached persona is handed back, the registry's reference
    // check keeps the decoded bitmap, and the orb wears the old face until the
    // app restarts. Watching the picture file is what closes it.
    //
    // The replacement is a different length as well as different bytes, for
    // the reason EditingTheMarkdownIsPickedUpOnTheNextPass gives about
    // timestamp resolution.
    [Fact]
    public void APortraitReplacedInPlaceIsPickedUpOnTheNextPass()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\nHer profile picture is leota.png.\n");
        var picture = Path.Combine(_project, "leota.png");
        File.WriteAllBytes(picture, new byte[] { 1, 2, 3, 4 });

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var first = LocalPersonas.For(_sessionId);
        Assert.Equal(picture, first!.AvatarPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(first.AvatarPath!));

        File.WriteAllBytes(picture, new byte[] { 9, 8, 7, 6, 5 });
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var second = LocalPersonas.For(_sessionId);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, File.ReadAllBytes(second!.AvatarPath!));

        // A *different* object is the half of the fix the registry owns: that
        // is what makes Set drop the bitmap decoded from the old bytes. Same
        // object, and the picture would be re-read and the face still stale.
        Assert.NotSame(first, second);
    }

    // ...and having watched it once, it settles. The picture goes into the
    // stored signature on the pass that read it, so an untouched picture is
    // not a reason to resolve again — otherwise every session with a portrait
    // would re-read a markdown tree and a picture twice a second forever.
    [Fact]
    public void APortraitSettlesRatherThanResolvingEveryPass()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(Path.Combine(_project, "leota.png"), new byte[] { 1, 2, 3, 4 });

        var manager = Manager();

        manager.ApplyPersona(_sessionId, Status(), Pass());
        var first = LocalPersonas.For(_sessionId);

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    // A gateway session is never asked. Its identity is the gateway's, and its
    // Cwd — when it has one at all — is wherever this app was started from, so
    // reading a persona out of it would put the developer's own repository name
    // on somebody else's orb.
    [Fact]
    public void AGatewaySessionIsNotGivenAPersona()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        Manager().ApplyPersona(_sessionId, Status(SessionSource.OpenClaw), Pass());

        Assert.Null(LocalPersonas.For(_sessionId));
    }

    // Neither is a session with nowhere to look.
    //
    // Whitespace as well as empty, and that is CB-143's guard rather than a
    // flourish. The stricter half of this check used to sit in
    // LocalPersona.ResolveForSession, one call further down; with that wrapper
    // gone it lives here, and a cwd of " " that got past it would walk nowhere
    // and be left with the user-level candidates alone — a persona read out of
    // ~/.claude and hung on an orb whose session has no working directory.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ASessionWithNoWorkingDirectoryIsNotGivenAPersona(string cwd)
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var status = Status();
        status.Cwd = cwd;

        // A user-level directory that does name a persona, so a walk that got
        // this far would have something to find and this would fail rather
        // than pass for want of a file.
        File.WriteAllText(Path.Combine(_configDir, "CLAUDE.md"), "Her name is Constance.\n");
        Manager(new[] { _configDir }).ApplyPersona(_sessionId, status, Pass());

        Assert.Null(LocalPersonas.For(_sessionId));
    }

    // A project that says nothing about a persona still gets an entry, and the
    // entry is empty rather than absent. That is what makes the second pass
    // cheap for the overwhelming majority of sessions, which have no persona at
    // all: without a stored signature there is nothing to compare against and
    // every tick would walk the tree again.
    [Fact]
    public void AProjectWithNoPersonaIsRecordedAsHavingNone()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\nThe name is derived from the folder.\n");

        var manager = Manager();
        var pass = Pass();

        manager.ApplyPersona(_sessionId, Status(), pass);
        var first = LocalPersonas.For(_sessionId);

        Assert.NotNull(first);
        Assert.True(first!.IsEmpty);

        manager.ApplyPersona(_sessionId, Status(), pass);
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    // Two sessions in one repository resolve the same files and each gets its
    // own registry entry — the candidate list is shared for the pass, the
    // registry entry is not. A cache keyed by directory rather than by session
    // would leave the second orb nameless.
    [Fact]
    public void TwoSessionsInOneRepositoryEachGetTheirOwnEntry()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var other = _sessionId + "-second";
        try
        {
            var manager = Manager();
            var pass = Pass();

            manager.ApplyPersona(_sessionId, Status(), pass);
            manager.ApplyPersona(other, Status(), pass);

            Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);
            Assert.Equal("Leota", LocalPersonas.For(other)!.Name);

            // One candidate list built, not two: the second session's cwd and
            // source are the first's.
            Assert.Single(pass);
        }
        finally
        {
            LocalPersonas.Forget(other);
        }
    }

    // Codex reads the directory walk and not the user-level config directories,
    // so its candidate list is a different one and must not be served out of
    // the entry Claude Code's pass made. Asserted on the pass dictionary rather
    // than on the outcome, because in a tree with no user-level file the two
    // lists would happen to produce the same persona and the bug would not
    // show.
    // The user-level arm, against a directory this fixture owns. Before CB-143
    // the scan tests reached the machine's real ~/.claude for this, which
    // covered the arm by accident and made every case above depend on what the
    // person running them had written in their own config directory.
    [Fact]
    public void AUserLevelConfigDirectoryIsConsultedForClaudeCode()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\nNothing about a persona here.\n");
        File.WriteAllText(Path.Combine(_configDir, "CLAUDE.md"), "Her name is Leota.\n");

        Manager(new[] { _configDir }).ApplyPersona(_sessionId, Status(), Pass());

        Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);
    }

    // ...and is not consulted for the other CLIs. Codex and Grok have their own
    // config directories with their own layouts, so ~/.claude describing a Codex
    // session would be a persona taken from the wrong CLI. CandidateFiles owns
    // that rule and LocalPersonaTests covers it directly; this is the scan
    // actually honouring it with a user-level file present to be wrongly read.
    [Fact]
    public void AUserLevelConfigDirectoryIsNotConsultedForCodex()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\nNothing about a persona here.\n");
        File.WriteAllText(Path.Combine(_configDir, "CLAUDE.md"), "Her name is Leota.\n");

        Manager(new[] { _configDir })
            .ApplyPersona(_sessionId, Status(SessionSource.Codex), Pass());

        Assert.True(LocalPersonas.For(_sessionId)!.IsEmpty);
    }

    // CB-143's regression test for the settings half of the old coupling: an
    // account added between two passes used to lengthen the candidate list and
    // hand the registry a fresh object. It cannot now, because the scan is not
    // the thing reading the setting. A real account change still re-resolves —
    // see SessionManager's own comment for why that is correct rather than the
    // bug — it simply no longer reaches a scan that was told which directories
    // to look in.
    [Fact]
    public void AnAccountAppearingInSettingsBetweenPassesDoesNotDisturbSettling()
    {
        WriteMarkdown("CLAUDE.md", "@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = Manager();

        manager.ApplyPersona(_sessionId, Status(), Pass());
        var first = LocalPersonas.For(_sessionId);

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-cb143");
        try
        {
            manager.ApplyPersona(_sessionId, Status(), Pass());
            Assert.Same(first, LocalPersonas.For(_sessionId));
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-cb143");
        }

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    [Fact]
    public void ClaudeCodeAndCodexInOneRepositoryDoNotShareACandidateList()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var codex = _sessionId + "-codex";
        try
        {
            var manager = Manager();
            var pass = Pass();

            manager.ApplyPersona(_sessionId, Status(), pass);
            manager.ApplyPersona(codex, Status(SessionSource.Codex), pass);

            Assert.Equal(2, pass.Count);
            Assert.Equal("Leota", LocalPersonas.For(codex)!.Name);
        }
        finally
        {
            LocalPersonas.Forget(codex);
        }
    }
}

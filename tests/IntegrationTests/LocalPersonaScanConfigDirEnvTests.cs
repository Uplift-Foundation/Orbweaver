using Xunit;

namespace Orbweaver.Tests;

// CB-143's regression test, driving the mechanism on purpose rather than
// waiting for a CI runner to interleave into it.
//
// The flake: LocalPersonaScanTests.AnImportedFileSettlesRatherThanResolvingEveryPass
// failed once on windows-latest and passed twice on the identical sha. The two
// personas had identical content and differed only in object identity, so
// nothing about resolution went wrong — what broke was the scan's settling
// invariant. ApplyPersona asked LocalPersona.UserConfigDirs on every pass,
// which reads CLAUDE_CONFIG_DIR; UsagePollerEnvironmentTests holds that
// variable at a sentinel while its stand-in child process runs, and it is in a
// *different* xUnit collection, which is to say it runs in parallel. One extra
// candidate path — a config directory that does not even exist — is one extra
// record in the signature, and a signature that differs is the scan's
// definition of "something moved".
//
// Separate from LocalPersonaScanTests, and [Collection("ConfigDirEnv")], for
// the reason ConfigDirEnvCollection.cs gives from the other direction: a class
// that sets CLAUDE_CONFIG_DIR while AgentRosterEnvironmentTests is between its
// read and its launch would cause exactly the flake that collection exists to
// prevent. Writing the regression test into the Settings collection would have
// fixed one flake by introducing another.
//
// Both cases below pin the provider — which is the fix — so neither is really
// about the environment any more. That is the point: the positive case is the
// one that used to fail, and the negative control beside it is what says the
// fixture is capable of failing at all.
[Collection("ConfigDirEnv")]
public class LocalPersonaScanConfigDirEnvTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "cb-persona-env-" + Guid.NewGuid());

    private readonly string _statusDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-env-status-" + Guid.NewGuid());

    private readonly string _sessionId = "persona-env-" + Guid.NewGuid();
    private readonly string? _previousConfigDir;

    public LocalPersonaScanConfigDirEnvTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_statusDir);
        _previousConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _previousConfigDir);
        LocalPersonas.Forget(_sessionId);
        try { Directory.Delete(_project, recursive: true); } catch { }
        try { Directory.Delete(_statusDir, recursive: true); } catch { }
    }

    private void WriteMarkdown(string name, string body) =>
        File.WriteAllText(Path.Combine(_project, name), body);

    private SessionStatus Status() => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Cwd = _project,
        Title = "cb-persona-env",
    };

    private Dictionary<(string Cwd, SessionSource Source, string Agent, string ConfigDir), IReadOnlyList<LocalPersona.Candidate>> Pass() => new();

    // The exact fixture that failed, plus the flip that failed it. The
    // sentinel is the literal value UsagePollerEnvironmentTests sets, so this
    // reproduces the interleaving rather than a stand-in for it.
    [Fact]
    public void AConfigDirEnvironmentFlipBetweenPassesDoesNotDisturbSettling()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);

        WriteMarkdown("CLAUDE.md", "@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = new SessionManager(
            _statusDir, null, userConfigDirs: () => Array.Empty<string>());

        manager.ApplyPersona(_sessionId, Status(), Pass());
        var first = LocalPersonas.For(_sessionId);
        Assert.Equal("Leota", first!.Name);

        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/Users/someone/.claude-sentinel");
        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));

        // And back again, because the harm is symmetric: the variable being
        // *cleared* between two passes shortens the candidate list by the same
        // record and resolves just as hard.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    // The negative control, and it is the half that makes the case above a
    // measurement rather than a hope. An unpinned manager over the identical
    // fixture and the identical flip resolves again and hands the registry a
    // new object with the same name in it — which is the CI failure, spelled
    // out. If the pin were doing nothing, this would be indistinguishable from
    // the case above; if the fixture could not fail, this would fail here.
    [Fact]
    public void AnUnpinnedManagerIsStillDisturbedByTheSameFlip()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);

        WriteMarkdown("CLAUDE.md", "@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = new SessionManager(_statusDir);

        manager.ApplyPersona(_sessionId, Status(), Pass());
        var first = LocalPersonas.For(_sessionId);

        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/Users/someone/.claude-sentinel");
        manager.ApplyPersona(_sessionId, Status(), Pass());
        var second = LocalPersonas.For(_sessionId);

        Assert.NotSame(first, second);

        // Identical content, which is what said the failure was about settling
        // and not about resolution. Field by field rather than record equality:
        // Persona.Files is a List, and a record compares one of those by
        // reference, so two personas read from the same files are never equal
        // to each other however identical their answers are.
        Assert.Equal(first!.Name, second!.Name);
        Assert.Equal(first.Voice, second.Voice);
        Assert.Equal(first.AvatarPath, second.AvatarPath);
        Assert.Equal(first.Files, second.Files);
    }
}

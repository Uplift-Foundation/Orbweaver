using Xunit;

namespace Orbweaver.Tests;

// The persona scan's config-directory seam, both arms of it (CB-143).
//
// ApplyPersona used to call LocalPersona.UserConfigDirs directly, which reads
// the CLAUDE_CONFIG_DIR environment variable and ClaudeCodeProfileDirs — two
// process-wide globals — on every pass. The answer is part of the candidate
// list and the candidate list is part of the signature, so anything that
// changed either between two passes made the scan resolve again and hand the
// registry a persona that was new by identity and identical by content. That
// is a settling failure rather than a resolution failure, and it is what made
// LocalPersonaScanTests.AnImportedFileSettlesRatherThanResolvingEveryPass fail
// once on the Windows CI leg and pass twice on the identical sha.
//
// The decision this covers is small and is exactly the one worth a unit test:
// with no provider given the scan asks the machine, and with one given the
// machine is not asked at all. The filesystem cases are in tests/IntegrationTests
// and the cost the seam protects — a portrait re-decoded per tick — is in
// tests/UiTests, since neither can be seen from here.
//
// No file has to exist for any of this. CandidateFiles is path arithmetic and
// nothing below reads the disk, so the fixture's working directory is a name
// rather than a directory.
//
// [Collection("Settings")] because the default arm reaches ClaudeConfigRoots,
// which reads ClaudeCodeProfileDirs.
[Collection("Settings")]
public class SessionManagerConfigDirSeamTests : IDisposable
{
    private readonly string _sessionId = "cb143-seam-" + Guid.NewGuid();

    // Off the root rather than under the temp directory, and that is not
    // tidiness. Path.GetTempPath() on Windows is under the user's own profile,
    // so the directory walk would reach C:\Users\<user> on its way up and add
    // that profile's .claude\CLAUDE.md as an ordinary *project* candidate —
    // the identical string the user-level arm contributes. The default-arm
    // assertion below would then hold against a provider that answered
    // nothing, which is a test that cannot fail rather than a test that passes.
    private readonly string _cwd = Path.Combine(
        Path.GetPathRoot(Path.GetTempPath())!, "cb143-seam-" + Guid.NewGuid());

    private static string HomeClaudeMd =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "CLAUDE.md");

    public void Dispose() => LocalPersonas.Forget(_sessionId);

    private SessionStatus Status() => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Cwd = _cwd,
        Title = "cb143-seam",
    };

    private IReadOnlyList<string> Candidates(SessionManager manager)
    {
        var pass = new Dictionary<(string Cwd, SessionSource Source, string Agent, string ConfigDir), IReadOnlyList<LocalPersona.Candidate>>();
        manager.ApplyPersona(_sessionId, Status(), pass);

        return Assert.Single(pass).Value.Select(candidate => candidate.Path).ToArray();
    }

    // The default. Nothing is given, so the scan asks the machine — which is
    // what production does and has to keep doing: a persona written in
    // ~/.claude/CLAUDE.md is a real persona for every Claude Code session on
    // the machine, and a seam that defaulted to nothing would have quietly
    // deleted that feature while fixing a flake.
    //
    // Asserted on ~/.claude rather than on the whole list because ~/.claude is
    // the one entry ClaudeConfigRoots always produces. Asserting the full
    // answer would mean reading CLAUDE_CONFIG_DIR here too, and a test that has
    // to read a global to know what to expect of it is the flake this ticket is
    // about, written a second time.
    [Fact]
    public void WithNoProviderTheScanAsksTheMachine()
    {
        var candidates = Candidates(new SessionManager(Path.GetTempPath(), null));

        Assert.Contains(HomeClaudeMd, candidates);
    }

    // The override, and the half that matters: the machine is not asked at all,
    // rather than asked and then added to. A provider that only prepended would
    // leave every settling claim still hostage to ClaudeCodeProfileDirs.
    [Fact]
    public void WithAProviderTheMachineIsNotAskedAtAll()
    {
        var pinned = Path.Combine(Path.GetTempPath(), "cb143-pinned-" + Guid.NewGuid());
        var asked = 0;

        var candidates = Candidates(new SessionManager(
            Path.GetTempPath(),
            null,
            userConfigDirs: () => { asked++; return new[] { pinned }; }));

        Assert.Contains(Path.Combine(pinned, "CLAUDE.md"), candidates);
        Assert.DoesNotContain(HomeClaudeMd, candidates);
        Assert.Equal(1, asked);
    }

    // And it is asked once per candidate list rather than once per session.
    // The pass dictionary is what makes the scan affordable — twenty agents in
    // one repository build one list between them — so a provider consulted per
    // session would put the cost back whatever it answered.
    [Fact]
    public void TheProviderIsAskedOncePerCandidateListRatherThanOncePerSession()
    {
        var asked = 0;
        var manager = new SessionManager(
            Path.GetTempPath(),
            null,
            userConfigDirs: () => { asked++; return Array.Empty<string>(); });

        var second = _sessionId + "-second";
        var pass = new Dictionary<(string Cwd, SessionSource Source, string Agent, string ConfigDir), IReadOnlyList<LocalPersona.Candidate>>();

        try
        {
            manager.ApplyPersona(_sessionId, Status(), pass);
            manager.ApplyPersona(second, Status(), pass);

            Assert.Single(pass);
            Assert.Equal(1, asked);
        }
        finally
        {
            LocalPersonas.Forget(second);
        }
    }
}

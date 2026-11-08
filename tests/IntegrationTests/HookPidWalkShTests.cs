using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Which process OrbweaverHook.sh decides is "this session", judged by staging
// the parent it walks up to.
//
// Separate from HookScriptShTests because it needs a process tree rather than
// just a payload. That file's own pid-walk test says the motivating case "cannot
// be staged from here"; it can, and this is how — so the shapes that used to be
// verified only by hand on a real machine are now verified on every run.
//
// The whole point is the classification, which is easy to get wrong in a way no
// other test would notice: a status file naming the wrong process makes
// SessionManager either drop a live orb or keep a dead one, and both look like
// the app rather than like the hook.
//
// What each shape is:
//
//   claude bg-spare      a *claimed* spare — the process a daemon-hosted
//                        background job actually runs in. An unclaimed one fires
//                        no hooks, so a hook that can see one is inside it.
//   claude bg-pty-host   the spare's pty wrapper. Never the session, and it
//                        cannot be told from the spare by command line — it
//                        passes the spare's own argv after a `--`, so its
//                        command line contains `--bg-spare` too. The title is
//                        what separates them.
//   claude / 2.1.241     an ordinary session, by either of the two names the
//                        binary reports depending on how it was launched.
//   claude … daemon run  the daemon. Never the session.
//   claude … agents      the Agent View viewer. Never the session — recording it
//                        is what once left a ghost orb pointing at a finished
//                        conversation, because the viewer outlives it.
public class HookPidWalkShTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string HookScript = Path.Combine(RepoRoot, "OrbweaverHook.sh");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OrbweaverHook.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find OrbweaverHook.sh by walking up from " + AppContext.BaseDirectory);
    }

    // Stands in for the process that runs a session. Two things are needed here
    // and bash on macOS offers neither:
    //
    //   os.setsid()  drops the controlling terminal so `ps -o tty=` reports "??",
    //                the way it does for a real daemon-hosted session. Without
    //                it the walk finds a tty on its first hop and stops there,
    //                and every case below would pass for the wrong reason — the
    //                tty fallback would name the staged parent whatever the name
    //                rules decided.
    //   os.execv()   sets argv[0] independently of the binary. macOS reports
    //                argv[0] for `ps -o comm=`, which is exactly why the hook
    //                reads a *title* like "claude bg-spare" rather than a plain
    //                executable name — so this is how a title is reproduced.
    //
    // The titled process records its own pid and runs the hook as a child, so
    // the hook's parent is it. Nothing in the embedded snippet may contain a
    // string the hook's skip list matches, because it lands in this process's
    // own command line — which is also what makes the `decoys` argument work.
    private const string FakeParentPy = """
        import os
        import sys

        os.setsid()

        pidfile, title, hook, agent, state = sys.argv[1:6]
        decoys = sys.argv[6:]

        script = 'printf %s "$$" > "$1"; bash "$2" "$3" "$4"'

        os.execv("/bin/bash", [title, "-c", script, "_", pidfile, hook, agent, state] + decoys)
        """;

    private sealed record WalkResult(int ExitCode, string Stdout, string Stderr, int FakePid, int RecordedPid);

    // `title` becomes the staged parent's argv[0]; `decoys` are extra arguments
    // appended to its command line, for the shapes the hook can only recognise
    // there (the daemon, the agents viewer).
    private static WalkResult RunUnderFakeParent(
        string title, string[] decoys, string sessionId, string agent = "claude", string state = "generating")
    {
        var tmp = Directory.CreateTempSubdirectory("cb-pidwalk-");
        var fakeScript = Path.Combine(tmp.FullName, "fake-parent.py");
        var pidFile = Path.Combine(tmp.FullName, "fake.pid");
        File.WriteAllText(fakeScript, FakeParentPy);

        var python = PythonUnixFactAttribute.Python3Path()
            ?? throw new InvalidOperationException("python3 disappeared after the skip check");

        var psi = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(fakeScript);
        psi.ArgumentList.Add(pidFile);
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add(state);
        foreach (var decoy in decoys) psi.ArgumentList.Add(decoy);

        // TMPDIR is where the hook writes, and CODEX_HOME / GROK_HOME keep a
        // run off the developer's real ~/.codex and ~/.grok — same reasoning
        // as HookScriptShTests.
        psi.Environment["TMPDIR"] = tmp.FullName;
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tmp.FullName;
        psi.Environment["CODEX_HOME"] = Path.Combine(tmp.FullName, "codex-home");
        Directory.CreateDirectory(psi.Environment["CODEX_HOME"]!);
        psi.Environment["GROK_HOME"] = Path.Combine(tmp.FullName, "grok-home");
        Directory.CreateDirectory(psi.Environment["GROK_HOME"]!);

        // Inherited from a real terminal these would make the walk stop at a
        // tmux pane instead of climbing, so the staged tree has to be clean of
        // them however the suite was launched.
        psi.Environment.Remove("TMUX");
        psi.Environment.Remove("TMUX_PANE");
        psi.Environment.Remove("ITERM_SESSION_ID");

        // Grok injects these into every child. The hook treats GROK_SESSION_ID
        // as stronger than argv, so a walk launched from inside a Grok session
        // would relabel every Claude and Codex case and skip their pid match.
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.Environment.Remove("CLAUDE_CODE_HOST_SESSION_ID");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start python3");

        try
        {
            process.StandardInput.Write(
                JsonSerializer.Serialize(new { session_id = sessionId, cwd = "/tmp/proj", transcript_path = "" }));
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Exited without reading its input; the assertions below still hold.
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var statusFile = Path.Combine(tmp.FullName.TrimEnd('/'), "orbweaver", sessionId + ".txt");
        var recorded = 0;
        if (File.Exists(statusFile))
        {
            recorded = JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(statusFile))?.SessionPid ?? 0;
        }

        var fakePid = File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var p) ? p : 0;

        return new WalkResult(process.ExitCode, stdout, stderr, fakePid, recorded);
    }

    // The hook's one unconditional contract, asserted here as well as in
    // HookScriptShTests: exit 0, nothing on either stream, whatever it decided.
    // Under Codex any stdout at all is read as invalid permission JSON and exit
    // 2 is a deny, so a walk that prints a `ps` error would start refusing the
    // user's own approvals.
    private static void AssertSilentSuccess(WalkResult r)
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Stdout);
        Assert.Equal("", r.Stderr);
        Assert.NotEqual(0, r.FakePid);
    }

    // The bug this file was added for. Every daemon-hosted background job ran
    // with no pid recorded at all, because a claimed spare was skipped along
    // with the two wrappers above it — and, more quietly, because its title is
    // "claude bg-spare" rather than "claude", so the name test never matched it
    // and the skip list was never even reached.
    [PythonUnixFact]
    public void ClaimedSpareIsTheSession()
    {
        var r = RunUnderFakeParent("claude bg-spare", [], "spare");

        AssertSilentSuccess(r);
        Assert.Equal(r.FakePid, r.RecordedPid);
    }

    // The other half of the same pair, and the reason the fix reads the title
    // instead of the command line: this process's command line contains
    // `--bg-spare` too.
    [PythonUnixFact]
    public void PtyHostIsNotTheSession()
    {
        var r = RunUnderFakeParent("claude bg-pty-host", [], "ptyhost");

        AssertSilentSuccess(r);
        Assert.NotEqual(r.FakePid, r.RecordedPid);
    }

    // Asserted as a pair, because "recognise the spare" must not have been
    // bought by loosening what counts as the claude binary.
    [PythonUnixFact]
    public void OrdinarySessionIsRecorded_UnderEitherNameTheBinaryReports()
    {
        var byName = RunUnderFakeParent("claude", [], "plain");
        AssertSilentSuccess(byName);
        Assert.Equal(byName.FakePid, byName.RecordedPid);

        var byVersion = RunUnderFakeParent("2.1.241", [], "versioned");
        AssertSilentSuccess(byVersion);
        Assert.Equal(byVersion.FakePid, byVersion.RecordedPid);
    }

    [PythonUnixFact]
    public void DaemonIsNotTheSession()
    {
        var r = RunUnderFakeParent("claude", ["daemon", "run"], "daemonrun");

        AssertSilentSuccess(r);
        Assert.NotEqual(r.FakePid, r.RecordedPid);
    }

    // The regression that cost someone an orb: recording the viewer meant the
    // orb outlived the session, because the viewer was still running.
    [PythonUnixFact]
    public void AgentsViewerIsNotTheSession()
    {
        var r = RunUnderFakeParent("claude", ["agents"], "agentsviewer");

        AssertSilentSuccess(r);
        Assert.NotEqual(r.FakePid, r.RecordedPid);
    }

    [PythonUnixFact]
    public void SomeOtherBinaryIsNotTheSession()
    {
        var r = RunUnderFakeParent("someothertool", [], "notclaude");

        AssertSilentSuccess(r);
        Assert.NotEqual(r.FakePid, r.RecordedPid);
    }

    // Codex is matched by name, and that name is now read off the first word of
    // the title rather than the whole of it — so a codex that ever wears a
    // subcommand in its title is still found, and a plain one still is.
    [PythonUnixFact]
    public void CodexSessionIsRecorded_TitledOrNot()
    {
        var plain = RunUnderFakeParent("codex", [], "codex-plain", agent: "codex");
        AssertSilentSuccess(plain);
        Assert.Equal(plain.FakePid, plain.RecordedPid);

        var titled = RunUnderFakeParent("codex exec", [], "codex-titled", agent: "codex");
        AssertSilentSuccess(titled);
        Assert.Equal(titled.FakePid, titled.RecordedPid);
    }

    [PythonUnixFact]
    public void GrokSessionIsRecorded_TitledOrNot()
    {
        var plain = RunUnderFakeParent("grok", [], "grok-plain", agent: "grok");
        AssertSilentSuccess(plain);
        Assert.Equal(plain.FakePid, plain.RecordedPid);

        var titled = RunUnderFakeParent("grok build", [], "grok-titled", agent: "grok");
        AssertSilentSuccess(titled);
        Assert.Equal(titled.FakePid, titled.RecordedPid);
    }

    // A claude ancestor is not a codex session and vice versa: the two CLIs each
    // look for their own, which is what stops a nested `codex exec` landing in
    // the Claude Code session's pid bucket and superseding its orb. Grok is
    // the same third party in that triangle.
    [PythonUnixFact]
    public void EachCliOnlyClaimsItsOwnProcess()
    {
        var claudeAskedForCodex = RunUnderFakeParent("claude bg-spare", [], "x-codex", agent: "codex");
        AssertSilentSuccess(claudeAskedForCodex);
        Assert.NotEqual(claudeAskedForCodex.FakePid, claudeAskedForCodex.RecordedPid);

        var codexAskedForClaude = RunUnderFakeParent("codex", [], "x-claude", agent: "claude");
        AssertSilentSuccess(codexAskedForClaude);
        Assert.NotEqual(codexAskedForClaude.FakePid, codexAskedForClaude.RecordedPid);

        var grokAskedForClaude = RunUnderFakeParent("grok", [], "x-grok-claude", agent: "claude");
        AssertSilentSuccess(grokAskedForClaude);
        Assert.NotEqual(grokAskedForClaude.FakePid, grokAskedForClaude.RecordedPid);

        var claudeAskedForGrok = RunUnderFakeParent("claude", [], "x-claude-grok", agent: "grok");
        AssertSilentSuccess(claudeAskedForGrok);
        Assert.NotEqual(claudeAskedForGrok.FakePid, claudeAskedForGrok.RecordedPid);
    }
}

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-265: the seam between the real OrbweaverHook.sh and the app's own reading
// of what it wrote. HookScriptShTests pins the bytes the hook prints; this pins
// that a Claude Desktop session's status file, produced by the real script with
// the environment Claude Desktop really gives it (CLAUDE_CODE_ENTRYPOINT=
// claude-desktop, no TERM_PROGRAM, no tmux), comes back out of the app's
// deserializer as a Claude Code session the app knows is Desktop-hosted. The two
// fail differently: a hook that writes the key under another name still passes
// every byte check, and the orb is silently dropped for having no terminal.
public class DesktopStatusRoundTripTests
{
    private static readonly string HookScript = Path.Combine(FindRepoRoot(), "OrbweaverHook.sh");

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

    private static SessionStatus RunAndDeserialize(string agent, string? entrypoint, string? hostSessionId = null)
    {
        var tmp = Directory.CreateTempSubdirectory("cb-desktop-rt-");
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add("idle");
        psi.Environment["TMPDIR"] = tmp.FullName;
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tmp.FullName;
        psi.Environment["CODEX_HOME"] = Path.Combine(tmp.FullName, "codex-home");
        psi.Environment["GROK_HOME"] = Path.Combine(tmp.FullName, "grok-home");
        foreach (var name in new[]
        {
            "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_HOST_SESSION_ID", "TERM_PROGRAM", "TMUX", "TMUX_PANE", "ITERM_SESSION_ID",
            "GROK_SESSION_ID", "GROK_HOOK_EVENT", "GROK_WORKSPACE_ROOT",
        })
            psi.Environment.Remove(name);
        if (entrypoint is not null) psi.Environment["CLAUDE_CODE_ENTRYPOINT"] = entrypoint;
        if (hostSessionId is not null) psi.Environment["CLAUDE_CODE_HOST_SESSION_ID"] = hostSessionId;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start bash");
        process.StandardInput.Write("{\"session_id\":\"rt-1\",\"cwd\":\"/tmp\"}");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // The hook invariant, asserted here as in every hook test.
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);

        var file = Path.Combine(tmp.FullName, "orbweaver", "rt-1.txt");
        var status = JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(file));
        Assert.NotNull(status);
        status!.Source = SessionManager.SourceOf(status);
        return status;
    }

    [UnixFact]
    public void ADesktopLaunchedClaudeSessionReadsBackAsDesktopHosted()
    {
        var status = RunAndDeserialize("claude", "claude-desktop");

        Assert.Equal("claude-desktop", status.Entrypoint);
        Assert.Equal(SessionSource.ClaudeCode, status.Source);
        Assert.True(status.IsDesktopHosted);
        // The shape that got these orbs dropped: no terminal handle anywhere.
        Assert.Equal("", status.TermProgram);
        Assert.Equal("", status.TmuxPane);
    }

    // CB-266: the host id survives the real hook and the app's deserializer, and
    // the app-derived BridgeSessionId is not something the file can carry.
    [UnixFact]
    public void ADesktopSessionsHostIdReadsBackAndItsBridgeStaysUnset()
    {
        var status = RunAndDeserialize("claude", "claude-desktop", "local_00000000-aaaa-bbbb-cccc-000000000002");

        Assert.Equal("local_00000000-aaaa-bbbb-cccc-000000000002", status.HostSessionId);
        Assert.Equal("", status.BridgeSessionId);
        Assert.False(status.IsBridgedDesktop);
    }

    [UnixFact]
    public void ACodexFileWrittenUnderADesktopEnvironmentCarriesNoHostId()
    {
        Assert.Equal("", RunAndDeserialize("codex", "claude-desktop", "local_00000000-aaaa-bbbb-cccc-000000000002").HostSessionId);
    }

    [UnixFact]
    public void ATerminalLaunchedClaudeSessionIsNotDesktopHosted()
    {
        Assert.False(RunAndDeserialize("claude", "cli").IsDesktopHosted);
        Assert.False(RunAndDeserialize("claude", null).IsDesktopHosted);
    }

    [UnixFact]
    public void ACodexFileWrittenUnderADesktopEnvironmentIsNotDesktopHosted()
    {
        var status = RunAndDeserialize("codex", "claude-desktop");

        Assert.Equal(SessionSource.Codex, status.Source);
        Assert.False(status.IsDesktopHosted);
    }
}

using System.Diagnostics;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// What the hook writes into `term_id`, driving the real script as a
// subprocess.
//
// **The field is "the handle this terminal understands", not an iTerm2
// field** — which is what it has always meant, and what CB-79's kitty and
// WezTerm routes depend on. Covered here rather than only in a unit test
// because the two halves fail differently: `TerminalTyping.RouteFor` can be
// perfectly right about a `term_id` the hook never wrote, and the result is a
// Send button that is correctly enabled and delivers nothing.
//
// The environment variables are the ones each terminal really exports, taken
// from their documentation rather than from memory: iTerm2's
// `ITERM_SESSION_ID` is `w0t0p0:UUID` and only the part after the colon is the
// session GUID, kitty's `KITTY_WINDOW_ID` is a bare integer, and WezTerm's
// `WEZTERM_PANE` likewise.
public class HookRecordsTerminalHandleTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string HookScript = Path.Combine(RepoRoot, "OrbweaverHook.sh");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OrbweaverHook.sh"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not find the repo root");
    }

    // The terminal variables this test controls. Cleared on every run so the
    // machine the suite happens to be running on cannot supply one — a real
    // risk here, since this suite is routinely run *inside* one of these
    // terminals and would otherwise pass by accident.
    private static readonly string[] TerminalVars =
    {
        "TMUX", "TMUX_PANE", "ITERM_SESSION_ID", "KITTY_WINDOW_ID", "WEZTERM_PANE",
        "TERM_PROGRAM",
    };

    private static string RunAndReadStatus(
        string sessionId, IDictionary<string, string> env)
    {
        var tmp = Directory.CreateTempSubdirectory("cb-termid-");

        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add("claude");
        psi.ArgumentList.Add("idle");
        psi.Environment["TMPDIR"] = tmp.FullName;
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tmp.FullName;

        foreach (var name in TerminalVars) psi.Environment.Remove(name);
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.Environment.Remove("CLAUDE_CODE_HOST_SESSION_ID");
        foreach (var (key, value) in env) psi.Environment[key] = value;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start bash");

        try
        {
            process.StandardInput.Write(
                $"{{\"session_id\":\"{sessionId}\",\"cwd\":\"/tmp\"}}");
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Exited before reading stdin, which some states do by design.
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // The invariant every hook test asserts: Codex reads any stdout as
        // malformed permission JSON, so a chatty hook silently starts refusing
        // the user's own approvals.
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);

        return File.ReadAllText(
            Path.Combine(tmp.FullName, "orbweaver", sessionId + ".txt"));
    }

    [UnixFact]
    public void KittysWindowIdIsRecordedAsTheHandle()
    {
        var status = RunAndReadStatus("kitty-1", new Dictionary<string, string>
        {
            ["KITTY_WINDOW_ID"] = "3",
        });

        Assert.Contains("\"term_id\":\"3\"", status);

        // kitty sets no TERM_PROGRAM at all, so the hook fills it in. Without
        // this the route has nothing to key on and the one terminal that
        // declines to name itself would be unaddressable over a missing
        // variable.
        Assert.Contains($"\"term_program\":\"{TerminalTyping.KittyProgram}\"", status);
    }

    [UnixFact]
    public void WezTermsPaneIdIsRecordedAsTheHandle()
    {
        var status = RunAndReadStatus("wez-1", new Dictionary<string, string>
        {
            ["WEZTERM_PANE"] = "17",
            ["TERM_PROGRAM"] = TerminalTyping.WezTermProgram,
        });

        Assert.Contains("\"term_id\":\"17\"", status);
        Assert.Contains($"\"term_program\":\"{TerminalTyping.WezTermProgram}\"", status);
    }

    [UnixFact]
    public void ITerm2StillWinsAndStillLosesItsWindowPrefix()
    {
        // The behaviour that was there before, unchanged — and the prefix
        // matters: `w0t0p0:` describes where the session was *created*, and
        // matching on the whole string finds nothing.
        var status = RunAndReadStatus("iterm-1", new Dictionary<string, string>
        {
            ["ITERM_SESSION_ID"] = "w0t0p0:DAE2A8B4-78AF-4C2A-B5A6-4803FD95331C",
            ["TERM_PROGRAM"] = TerminalTyping.ITerm2Program,
        });

        Assert.Contains("\"term_id\":\"DAE2A8B4-78AF-4C2A-B5A6-4803FD95331C\"", status);
        Assert.DoesNotContain("w0t0p0", status);
    }

    [UnixFact]
    public void TmuxTakesPrecedenceOverEveryEmulator()
    {
        // A pane's environment carries whatever was set when it was created,
        // so all three of these can be present at once and be stale. tmux owns
        // the input, so it is the only one that means anything — recording an
        // emulator handle here would point at the terminal *around* the
        // session rather than at the session.
        var status = RunAndReadStatus("tmux-1", new Dictionary<string, string>
        {
            ["TMUX"] = "/private/tmp/tmux-501/default,123,0",
            ["TMUX_PANE"] = "%7",
            ["ITERM_SESSION_ID"] = "w0t0p0:STALE-GUID",
            ["KITTY_WINDOW_ID"] = "9",
            ["WEZTERM_PANE"] = "9",
        });

        Assert.Contains("\"tmux_pane\":\"%7\"", status);
        Assert.Contains("\"term_id\":\"\"", status);
        Assert.DoesNotContain("STALE-GUID", status);
    }

    [UnixFact]
    public void ATerminalThatExportsNothingLeavesTheHandleEmpty()
    {
        // Ghostty, Alacritty, a bare ssh session. Empty is the honest answer
        // and it is what makes the route refuse rather than guess.
        var status = RunAndReadStatus("plain-1", new Dictionary<string, string>
        {
            ["TERM_PROGRAM"] = "Ghostty",
        });

        Assert.Contains("\"term_id\":\"\"", status);
        Assert.Contains("\"term_program\":\"Ghostty\"", status);
    }
}

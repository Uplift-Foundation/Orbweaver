using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The header's third line, against bytes this process did not write.
//
// The unit table in tests/UnitTests/ChatHeaderMetaTests.cs asserts every arm of
// the rule; this asserts the *seam*, and the two fail differently — the rule
// gets a judgement wrong, the seam gets the whole exchange wrong. Both facts on
// that line come from a file Claude Code's hook writes, in a format this app
// does not own: a title that is empty until a `custom-title` record lands in a
// transcript, and a cwd the hook takes off the payload. A parser test cannot
// notice that the field was renamed, that the hook stopped writing it, or that
// what arrives is the *session's* directory rather than the one the panel
// wanted.
//
// So the hook runs for real, as a subprocess, exactly the way Claude Code runs
// it — argv, payload on stdin, its own TMPDIR — and the line is composed from
// what it actually left on disk.
public class ChatHeaderMetaStatusFileTests
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

    // The same invocation shape HookScriptShTests uses, down to stripping the
    // Grok variables: a test run from inside a Grok session would otherwise
    // have every Claude case relabelled, and this one cares which CLI it is —
    // only the Claude arm of the hook reads a title out of a transcript.
    private static void RunHook(string state, string payloadJson, string tmpDir)
    {
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
        psi.ArgumentList.Add(state);
        psi.Environment["TMPDIR"] = tmpDir;
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tmpDir;
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        psi.Environment.Remove("GROK_HOME");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.Environment.Remove("CLAUDE_CODE_HOST_SESSION_ID");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bash");

        try
        {
            process.StandardInput.Write(payloadJson);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Exited without reading its input. Not a state this test asks for,
            // but the write is the same race HookScriptShTests documents.
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // The invariant the whole script is built around, asserted here too
        // rather than assumed: a hook that printed something would be denying
        // the user's own tool approvals under Codex, and a test of the header
        // that quietly tolerated it would be the wrong place to find out.
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    private static SessionStatus StatusAfterHook(string tmpDir, string sessionId)
    {
        var file = Path.Combine(tmpDir.TrimEnd('/'), "orbweaver", sessionId + ".txt");
        Assert.True(File.Exists(file), "the hook wrote no status file at " + file);

        return JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(file))
            ?? throw new InvalidOperationException("status file did not deserialize: " + file);
    }

    // A real Claude Code transcript row, in the shape the hook's own comment
    // documents and anchors its grep to. Written as a file rather than passed
    // as a string because the whole point of this test is that the title
    // arrives through the filesystem.
    private static string WriteTranscript(string dir, string title)
    {
        var path = Path.Combine(dir, "transcript.jsonl");
        File.WriteAllText(path,
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}\n" +
            "{\"type\":\"custom-title\",\"customTitle\":\"" + title + "\",\"sessionId\":\"s\"}\n");

        return path;
    }

    [UnixFact]
    public void TheHeaderLineIsBuiltFromWhatTheHookActuallyWrote()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-header-meta-");
        var home = Path.Combine(tmp.FullName, "home");
        var project = Path.Combine(home, "Source", "HauntedMansionTerminalTheme");
        Directory.CreateDirectory(project);

        var transcript = WriteTranscript(tmp.FullName, "haunted-mansion");
        var payload = JsonSerializer.Serialize(new
        {
            session_id = "header-meta",
            cwd = project,
            transcript_path = transcript,
        });

        RunHook("idle", payload, tmp.FullName);

        var status = StatusAfterHook(tmp.FullName, "header-meta");

        // The two facts, as they came off the disk. Asserted separately from
        // the composed line so a failure says which half moved — a renamed
        // field and a mis-composed line look identical in the line alone.
        Assert.Equal("haunted-mansion", status.Title);
        Assert.Equal(project, status.Cwd);

        // A persona took the title line, which is the case where the session
        // name earns its place. `home` is the temp tree rather than the real
        // one deliberately: a test that read Environment's home would assert
        // something different on every machine it ran on.
        var meta = ChatHeaderMeta.Compose(
            "Leota", status.Title, status.Cwd, home, "warrens-macbook-pro", true);

        Assert.True(meta.IsVisible);
        Assert.Equal(
            "haunted-mansion · ~/Source/HauntedMansionTerminalTheme · warrens-macbook-pro",
            meta.Text);

        // The tooltip is the only place the whole path exists once the line is
        // trimmed, so it has to be the path the hook wrote and not the
        // abbreviation.
        Assert.Contains(project, meta.Tooltip);

        Directory.Delete(tmp.FullName, true);
    }

    [UnixFact]
    public void ASessionTooYoungToBeNamedStillShowsWhereItIsWorking()
    {
        // The state every local session starts in, and the reason ApplyTitle
        // re-reads the status instead of caching it at Bind: Claude Code names
        // a chat some way into it, so the first hook writes `"title":""`. A
        // status file read live during this ticket's work said exactly that.
        var tmp = Directory.CreateTempSubdirectory("cb-header-meta-");
        var home = Path.Combine(tmp.FullName, "home");
        var project = Path.Combine(home, "Source", "Claude-Buddy");
        Directory.CreateDirectory(project);

        var payload = JsonSerializer.Serialize(new
        {
            session_id = "header-meta-young",
            cwd = project,
            transcript_path = "",
        });

        RunHook("generating", payload, tmp.FullName);

        var status = StatusAfterHook(tmp.FullName, "header-meta-young");

        Assert.Equal("", status.Title);

        var meta = ChatHeaderMeta.Compose(
            "Claude-Buddy", status.Title, status.Cwd, home, "warrens-macbook-pro", true);

        Assert.True(meta.IsVisible);
        Assert.Equal("~/Source/Claude-Buddy · warrens-macbook-pro", meta.Text);

        // And the same panel a moment later, once the title lands. Nothing else
        // about the line moves — the folder and the machine are where they
        // were, and the name simply appears in front of them.
        var named = ChatHeaderMeta.Compose(
            "Claude-Buddy", "Package the app with a tray", status.Cwd, home,
            "warrens-macbook-pro", true);

        Assert.Equal("Package the app with a tray · " + meta.Text, named.Text);

        Directory.Delete(tmp.FullName, true);
    }

    [UnixFact]
    public void APathOutsideHomeIsShownWholeRatherThanGuessedAt()
    {
        // Every worktree in this project lives in /tmp, so this is not a corner
        // case here — it is where half the work happens. The hook is what
        // decides the cwd is a real path at all, which is why this goes through
        // it rather than composing from a literal.
        var tmp = Directory.CreateTempSubdirectory("cb-header-meta-");
        var project = Path.Combine(tmp.FullName, "checkout");
        Directory.CreateDirectory(project);

        var payload = JsonSerializer.Serialize(new
        {
            session_id = "header-meta-outside",
            cwd = project,
            transcript_path = "",
        });

        RunHook("idle", payload, tmp.FullName);

        var status = StatusAfterHook(tmp.FullName, "header-meta-outside");

        var meta = ChatHeaderMeta.Compose(
            "checkout", status.Title, status.Cwd,
            Path.Combine(tmp.FullName, "home"), "warrens-macbook-pro", true);

        Assert.Equal(project + " · warrens-macbook-pro", meta.Text);
        Assert.DoesNotContain("~", meta.Text);

        Directory.Delete(tmp.FullName, true);
    }

    // The same seam on the other platform, because a feature works on Windows
    // and macOS or it is not a feature — and because the one thing in this
    // change that is genuinely platform-shaped is the path. ChatHeaderMeta
    // keeps the native separator rather than normalising it (a path is a thing
    // somebody pastes into their own shell), so what a Windows header shows is
    // `~\Source\Claude-Buddy`, and asserting that from a Mac would be
    // asserting a string rather than a behaviour.
    //
    // The title is left empty here rather than staged through a transcript:
    // this is about the status file's cwd surviving the round trip on a
    // platform whose separator differs, and the ps1 hook's own title parsing
    // has its own tests next door in HookScriptPs1Tests.
    [WindowsFact]
    public void TheWindowsHookFeedsTheSameHeaderLineWithWindowsSeparators()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-header-meta-");
        var home = Path.Combine(tmp.FullName, "home");
        var project = Path.Combine(home, "Source", "Claude-Buddy");
        Directory.CreateDirectory(project);

        var payload = JsonSerializer.Serialize(new
        {
            session_id = "header-meta-win",
            cwd = project,
            transcript_path = "",
        });

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path.Combine(RepoRoot, "OrbweaverHook.ps1"));
        psi.ArgumentList.Add("-State");
        psi.ArgumentList.Add("idle");
        psi.ArgumentList.Add("-Agent");
        psi.ArgumentList.Add("claude");
        psi.ArgumentList.Add("-TempDir");
        psi.ArgumentList.Add(tmp.FullName);
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        psi.Environment.Remove("GROK_HOME");

        using (var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh"))
        {
            process.StandardInput.Write(payload);
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", stdout);
            Assert.Equal("", stderr);
        }

        var status = StatusAfterHook(tmp.FullName, "header-meta-win");

        var meta = ChatHeaderMeta.Compose(
            "Claude-Buddy", status.Title, status.Cwd, home, "the-windows-box", true);

        Assert.Equal(
            "~" + Path.DirectorySeparatorChar + "Source" + Path.DirectorySeparatorChar
                + "Claude-Buddy · the-windows-box",
            meta.Text);

        // And the tooltip is still the whole thing, drive letter included —
        // the half of the path the abbreviation threw away is the half a
        // Windows user needs to paste anywhere.
        Assert.Contains(project, meta.Tooltip);

        Directory.Delete(tmp.FullName, true);
    }
}

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Exercises OrbweaverHook.ps1 -- the PowerShell twin of OrbweaverHook.sh --
// as a real subprocess, under BOTH interpreters (CB-227). Every Windows
// install registers the hook as `powershell.exe -NoProfile -ExecutionPolicy
// Bypass -File ...` (Windows PowerShell 5.1), so 5.1 is the interpreter that
// matters; pwsh 7 is what developers happen to have, and a construct that
// only 7 understands (`??`, `?.`, ternary) passes there and breaks the hook
// for every real user. Each case body takes the executable and is wrapped
// twice, one wrapper per shell, with the skip attributes from
// SapiSpeakScriptTests: on Windows both must run (a missing one fails), off
// Windows 5.1 is reported Skipped and pwsh skips only if absent.
//
// Argv shape, from the script's param() block: -State <idle|generating|
// waiting|ended> (mandatory), -Agent <claude|codex|grok> (default claude),
// -TempDir <path> (default '' -> Path.GetTempPath()). The payload arrives on
// stdin as JSON, same as the bash twin.
public class HookScriptPs1Tests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string HookScript = Path.Combine(RepoRoot, "OrbweaverHook.ps1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OrbweaverHook.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find OrbweaverHook.ps1 by walking up from " + AppContext.BaseDirectory);
    }

    private sealed record HookResult(int ExitCode, string Stdout, string Stderr);

    private static HookResult RunHook(
        string exe,
        string agent,
        string state,
        string payloadJson,
        string tempDir,
        IDictionary<string, string>? extraEnv = null,
        TimeSpan? deadline = null,
        IEnumerable<string>? unsetEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        // Exactly what the installers register (install-windows-hooks.ps1 and
        // friends): -NoProfile -ExecutionPolicy Bypass -File.
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add("-State");
        psi.ArgumentList.Add(state);
        psi.ArgumentList.Add("-Agent");
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add("-TempDir");
        psi.ArgumentList.Add(tempDir);

        // Same isolation as the bash twin: Grok injects GROK_SESSION_ID into
        // every child, and the hook treats that as stronger than -Agent. A
        // test run from inside a Grok session would otherwise relabel every
        // Claude and Codex case. Strip them; extraEnv puts them back when
        // the test is about that override.
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        psi.Environment.Remove("GROK_HOME");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.Environment.Remove("CLAUDE_CODE_HOST_SESSION_ID");

        // CB-306: the script honours ORBWEAVER_STATUS_ROOT ahead of -TempDir,
        // the way the app's StatusDirectory.Root does, and TestBootstrap
        // exports that variable for this whole process pointed at its own
        // scratch root. Pinned to -TempDir here so each test's file lands
        // where that test looks; the tests about the order itself override
        // or unset it.
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tempDir;

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }
        if (unsetEnv is not null)
        {
            foreach (var name in unsetEnv)
                psi.Environment.Remove(name);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start " + exe);

        process.StandardInput.Write(payloadJson);
        process.StandardInput.Close();

        // Read both streams asynchronously so a deadline can fire while the
        // hook is still running: a synchronous ReadToEnd would block until it
        // exits, and the regression a deadline exists for is one that never does.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (deadline is { } limit && !process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"hook did not exit within {limit.TotalSeconds}s");
        }

        process.WaitForExit();
        return new HookResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    // Same Codex-safety invariant as the bash twin's own header explains:
    // any stdout is invalid permission-request JSON to Codex, and exit code
    // 2 specifically means "deny". Repeated in full in every test method.
    private static void AssertSilentSuccess(HookResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private static string StatusDir(string tempDir) => Path.Combine(tempDir, "orbweaver");

    private static string StatusFile(string tempDir, string sessionId) =>
        Path.Combine(StatusDir(tempDir), sessionId + ".txt");

    private static string Payload(object fields) => JsonSerializer.Serialize(fields);

    // The Windows script computes its own CRC-32 (no `cksum` binary on
    // Windows) with the explicit goal — per its own comment — of agreeing
    // with the bash twin's `cksum` byte-for-byte, so the same project
    // directory gets the same colour on either platform. This is a direct
    // C# port of OrbweaverHook.ps1's Get-CksumCrc, used to build the
    // golden expectation for the auto-color test below without guessing a
    // magic number.
    //
    // Cross-checked (see WindowsCrc32Port_AgreesWithThePosixCksumBinary,
    // a plain, always-runs [Fact] below) against the real `cksum` binary's
    // output for "/tmp/proj" — 591481296 — captured by hand on this machine.
    // That check runs on every OS and does not touch pwsh, so it validates
    // this port even though the pwsh-invoking tests around it cannot run
    // here.
    internal static uint WindowsCksumCrc(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        uint crc = 0;

        void Roll(byte value)
        {
            crc ^= (uint)value << 24;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        foreach (var b in bytes) Roll(b);

        var len = bytes.Length;
        while (len > 0)
        {
            Roll((byte)(len & 0xFF));
            len /= 256;
        }

        return ~crc;
    }

    [Fact]
    public void WindowsCrc32Port_AgreesWithThePosixCksumBinary()
    {
        // Golden value from running the real `cksum` on this machine:
        //   printf '%s' '/tmp/proj' | cksum   =>   591481296 9
        // Verified by hand while writing HookScriptShTests's auto-color
        // test. If this ever fails, the ps1 script's derived colour has
        // silently stopped agreeing with the sh script's for the same
        // directory — exactly the cross-platform guarantee its own comment
        // claims.
        Assert.Equal(591481296u, WindowsCksumCrc("/tmp/proj"));
    }

    private static string ExpectedAutoColor(string cwd)
    {
        string[] palette =
        {
            "red", "orange", "yellow", "green", "teal", "cyan",
            "blue", "purple", "violet", "magenta", "pink"
        };
        var index = (int)(WindowsCksumCrc(cwd) % (uint)palette.Length);
        return palette[index];
    }

    private static void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgentCase(string exe)
    {
        foreach (var agent in new[] { "claude", "codex", "grok" })
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
            var payload = Payload(new { session_id = "s-" + agent + "-" + state, cwd = "C:\\proj", transcript_path = "" });
            var codexHome = Path.Combine(tempDir, "codex-home");
            Directory.CreateDirectory(codexHome);
            var grokHome = Path.Combine(tempDir, "grok-home");
            Directory.CreateDirectory(grokHome);
            var env = new Dictionary<string, string>
            {
                ["CODEX_HOME"] = codexHome,
                ["GROK_HOME"] = grokHome,
            };

            var result = RunHook(exe, agent, state, payload, tempDir, env);

            AssertSilentSuccess(result);
        }
    }

    // CB-306: ORBWEAVER_STATUS_ROOT outranks the baked -TempDir, in the same
    // order the app's StatusDirectory.Root uses, so a sandboxed app and a
    // sandboxed hook land in one place. The negative control is the shape
    // every Windows install actually has: no variable, -TempDir baked by the
    // installer, and the file where it always was. RunHook pins the variable
    // for every other test, so that control is the one place the pin is
    // removed and the baked argument is shown to still carry the file.
    private static void StatusRootOutranksTheBakedTempDirCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-tempdir-").FullName;
        var root = Directory.CreateTempSubdirectory("cb-hook-ps1-root-").FullName;
        var payload = Payload(new { session_id = "root-wins", cwd = "C:\\proj", transcript_path = "" });

        var result = RunHook(exe, "claude", "idle", payload, tempDir,
            extraEnv: new Dictionary<string, string> { ["ORBWEAVER_STATUS_ROOT"] = root });

        AssertSilentSuccess(result);
        Assert.True(File.Exists(StatusFile(root, "root-wins")), "the file belongs under ORBWEAVER_STATUS_ROOT");
        Assert.False(File.Exists(StatusFile(tempDir, "root-wins")), "and not under -TempDir");
    }

    private static void WithoutAStatusRootTheBakedTempDirIsUsedCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-tempdir-").FullName;
        var payload = Payload(new { session_id = "tempdir-wins", cwd = "C:\\proj", transcript_path = "" });

        var result = RunHook(exe, "claude", "idle", payload, tempDir,
            unsetEnv: new[] { "ORBWEAVER_STATUS_ROOT" });

        AssertSilentSuccess(result);
        Assert.True(File.Exists(StatusFile(tempDir, "tempdir-wins")), "with no override the baked -TempDir carries the file");
    }

    private static void EndedStateDeletesTheStatusFileAndExitsSilentlyCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        Directory.CreateDirectory(StatusDir(tempDir));
        var file = StatusFile(tempDir, "s1");
        File.WriteAllText(file, "stale status");

        var payload = Payload(new { session_id = "s1", cwd = "C:\\proj", transcript_path = "" });
        var result = RunHook(exe, "claude", "ended", payload, tempDir);

        AssertSilentSuccess(result);
        Assert.False(File.Exists(file), "ended must delete the session's status file");
    }

    private static void MissingSessionId_WritesTheStatusFileNamedUnknownCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var payloadJson = "{\"cwd\":\"C:\\\\proj\",\"transcript_path\":\"\"}";

        var result = RunHook(exe, "claude", "idle", payloadJson, tempDir);
        AssertSilentSuccess(result);

        Assert.True(File.Exists(StatusFile(tempDir, "unknown")));
    }

    private static void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLastCase(string exe)
    {
        AssertCustomTitleWins(exe, writeCustomFirst: true);
        AssertCustomTitleWins(exe, writeCustomFirst: false);
    }

    private static void AssertCustomTitleWins(string exe, bool writeCustomFirst)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-ps1-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");

        var customLine = "{\"type\":\"custom-title\",\"customTitle\":\"my name\",\"sessionId\":\"s1\"}";
        var aiLine = "{\"type\":\"ai-title\",\"aiTitle\":\"auto name\",\"sessionId\":\"s1\"}";
        var lines = writeCustomFirst
            ? new[] { customLine, aiLine }
            : new[] { aiLine, customLine };
        File.WriteAllLines(transcript, lines);

        var payload = Payload(new { session_id = "s1", cwd = "C:\\proj", transcript_path = transcript });
        var result = RunHook(exe, "claude", "idle", payload, tempDir);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tempDir, "s1"));
        Assert.True(
            status.Contains("\"title\":\"my name\"", StringComparison.Ordinal),
            "custom-title should win over ai-title when the custom record was written " +
            (writeCustomFirst ? "first" : "last") + ". Status file was: " + status);
    }

    // A transcript line can be megabytes long -- an inline image is one JSON
    // row. Get-Content -Tail walks backwards for line breaks and, on both
    // 5.1 and 7.6, was still running after 45s on a single 1.5M-character
    // line, so every hook for such a session hit Claude Code's 30s timeout
    // (CB-247). The deadline sits well inside that budget; the fixed hook
    // takes about a second.
    private static readonly TimeSpan LongLineDeadline = TimeSpan.FromSeconds(20);

    private static string LongLine(int chars) =>
        "{\"type\":\"user\",\"message\":\"" + new string('a', chars) + "\"}";

    private static void TitleAfterAMegabyteLine_IsReadFromTheTailWithoutHangingCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-ps1-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            LongLine(1_500_000),
            "{\"type\":\"ai-title\",\"aiTitle\":\"after the long line\",\"sessionId\":\"s1\"}",
        });

        var payload = Payload(new { session_id = "s1", cwd = "C:\\proj", transcript_path = transcript });
        var result = RunHook(exe, "claude", "idle", payload, tempDir, deadline: LongLineDeadline);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tempDir, "s1"));
        Assert.Contains("\"title\":\"after the long line\"", status, StringComparison.Ordinal);
    }

    // The records sit before 3MB of one line, so the 256KB tail window holds
    // none of them and the whole-file fallback has to find them. The file is
    // held open for writing throughout, as Claude Code holds it mid-append --
    // File.ReadLines' sharing mode would be refused here. The title carries
    // non-ASCII so a codepage read on 5.1 would show up as mojibake.
    private static void RecordsPushedOutOfTheTailWindow_AreFoundByTheFallback_WhileTheFileIsOpenForWritingCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-ps1-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            "{\"type\":\"ai-title\",\"aiTitle\":\"café — fallback\",\"sessionId\":\"s1\"}",
            "{\"type\":\"agent-color\",\"agentColor\":\"teal\",\"sessionId\":\"s1\"}",
            LongLine(3_000_000),
        });

        using var writer = new FileStream(transcript, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var payload = Payload(new { session_id = "s1", cwd = "C:\\proj", transcript_path = transcript });
        var result = RunHook(exe, "claude", "idle", payload, tempDir, deadline: LongLineDeadline);
        AssertSilentSuccess(result);

        using var status = JsonDocument.Parse(File.ReadAllText(StatusFile(tempDir, "s1")));
        Assert.Equal("café — fallback", status.RootElement.GetProperty("title").GetString());
        Assert.Equal("teal", status.RootElement.GetProperty("color").GetString());
    }

    private static void AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHashCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        Directory.CreateDirectory(StatusDir(tempDir));
        File.WriteAllText(Path.Combine(StatusDir(tempDir), ".auto-color"), "");

        var projectDir = Directory.CreateTempSubdirectory("cb-hook-ps1-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        const string cwd = "C:\\some\\fixed\\project\\path";
        var payload = Payload(new { session_id = "s1", cwd, transcript_path = transcript });

        var result = RunHook(exe, "claude", "idle", payload, tempDir);
        AssertSilentSuccess(result);

        var expectedColor = ExpectedAutoColor(cwd);

        var status = File.ReadAllText(StatusFile(tempDir, "s1"));
        Assert.Contains($"\"color\":\"{expectedColor}\"", status);

        var transcriptContents = File.ReadAllText(transcript);
        Assert.Contains(
            $"{{\"type\":\"agent-color\",\"agentColor\":\"{expectedColor}\",\"sessionId\":\"s1\"}}",
            transcriptContents);
    }

    private static void GrokEnvOverridesAClaudeArgvAndWritesCliGrokCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        Directory.CreateDirectory(Path.Combine(tempDir, "grok-home"));
        var payload = Payload(new { session_id = "g1", cwd = "C:\\proj", transcript_path = "" });
        var env = new Dictionary<string, string>
        {
            ["GROK_SESSION_ID"] = "g1",
            ["GROK_HOOK_EVENT"] = "session_start",
            ["GROK_HOME"] = Path.Combine(tempDir, "grok-home")
        };

        var result = RunHook(exe, "claude", "idle", payload, tempDir, env);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tempDir, "g1"));
        Assert.Contains("\"cli\":\"grok\"", status);
    }

    private static void GrokCamelCasePayloadFieldsAreReadCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var grokHome = Path.Combine(tempDir, "grok-home");
        Directory.CreateDirectory(grokHome);
        var payload = Payload(new { sessionId = "camel-1", cwd = "C:\\proj", transcriptPath = "" });
        var env = new Dictionary<string, string> { ["GROK_HOME"] = grokHome };

        var result = RunHook(exe, "grok", "idle", payload, tempDir, env);
        AssertSilentSuccess(result);

        Assert.True(File.Exists(StatusFile(tempDir, "camel-1")));
        var status = File.ReadAllText(StatusFile(tempDir, "camel-1"));
        Assert.Contains("\"cli\":\"grok\"", status);
    }

    private static void GrokAutoColorDoesNotAppendToTheTranscriptCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        Directory.CreateDirectory(StatusDir(tempDir));
        File.WriteAllText(Path.Combine(StatusDir(tempDir), ".auto-color"), "");

        var sessionDir = Path.Combine(tempDir, "sess");
        Directory.CreateDirectory(sessionDir);
        var transcript = Path.Combine(sessionDir, "updates.jsonl");
        File.WriteAllText(transcript, "{\"method\":\"session/update\"}\n");
        File.WriteAllText(Path.Combine(sessionDir, "summary.json"),
            """{"generated_title":"Grok title","title_is_manual":false}""");

        const string cwd = "C:\\some\\fixed\\project\\path";
        var payload = Payload(new { session_id = "g-color", cwd, transcript_path = transcript });
        var env = new Dictionary<string, string>
        {
            ["GROK_HOME"] = Path.Combine(tempDir, "grok-home")
        };

        var result = RunHook(exe, "grok", "idle", payload, tempDir, env);
        AssertSilentSuccess(result);

        Assert.Equal("{\"method\":\"session/update\"}\n", File.ReadAllText(transcript));
        var status = File.ReadAllText(StatusFile(tempDir, "g-color"));
        Assert.Contains("\"cli\":\"grok\"", status);
        Assert.Contains("\"title\":\"Grok title\"", status);
        Assert.Contains($"\"color\":\"{ExpectedAutoColor(cwd)}\"", status);
    }

    private static void CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrintingCase(string exe)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var codexHome = Directory.CreateTempSubdirectory("cb-hook-ps1-codexhome-").FullName;
        var sessionId = "abc123-session";
        var rolloutDir = Path.Combine(codexHome, "sessions", "2026", "08", "21");
        Directory.CreateDirectory(rolloutDir);
        var rolloutFile = Path.Combine(rolloutDir, $"rollout-2026-08-21T00-00-00-{sessionId}.jsonl");
        File.WriteAllText(rolloutFile, "not a real codex rollout row\n");

        var payload = Payload(new { session_id = sessionId, cwd = "C:\\proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome };

        var result = RunHook(exe, "codex", "idle", payload, tempDir, env);

        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tempDir, sessionId));
        Assert.Contains(rolloutFile.Replace("\\", "\\\\"), status);
    }

    [WindowsPowerShellFact]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent_Powershell51() => HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgentCase("powershell");

    [PwshFact]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent_Pwsh() => HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgentCase("pwsh");

    [WindowsPowerShellFact]
    public void StatusRootOutranksTheBakedTempDir_Powershell51() => StatusRootOutranksTheBakedTempDirCase("powershell");

    [PwshFact]
    public void StatusRootOutranksTheBakedTempDir_Pwsh() => StatusRootOutranksTheBakedTempDirCase("pwsh");

    [WindowsPowerShellFact]
    public void WithoutAStatusRootTheBakedTempDirIsUsed_Powershell51() => WithoutAStatusRootTheBakedTempDirIsUsedCase("powershell");

    [PwshFact]
    public void WithoutAStatusRootTheBakedTempDirIsUsed_Pwsh() => WithoutAStatusRootTheBakedTempDirIsUsedCase("pwsh");

    [WindowsPowerShellFact]
    public void EndedStateDeletesTheStatusFileAndExitsSilently_Powershell51() => EndedStateDeletesTheStatusFileAndExitsSilentlyCase("powershell");

    [PwshFact]
    public void EndedStateDeletesTheStatusFileAndExitsSilently_Pwsh() => EndedStateDeletesTheStatusFileAndExitsSilentlyCase("pwsh");

    [WindowsPowerShellFact]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown_Powershell51() => MissingSessionId_WritesTheStatusFileNamedUnknownCase("powershell");

    [PwshFact]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown_Pwsh() => MissingSessionId_WritesTheStatusFileNamedUnknownCase("pwsh");

    [WindowsPowerShellFact]
    public void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLast_Powershell51() => CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLastCase("powershell");

    [PwshFact]
    public void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLast_Pwsh() => CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLastCase("pwsh");

    [WindowsPowerShellFact]
    public void TitleAfterAMegabyteLine_IsReadFromTheTailWithoutHanging_Powershell51() => TitleAfterAMegabyteLine_IsReadFromTheTailWithoutHangingCase("powershell");

    [PwshFact]
    public void TitleAfterAMegabyteLine_IsReadFromTheTailWithoutHanging_Pwsh() => TitleAfterAMegabyteLine_IsReadFromTheTailWithoutHangingCase("pwsh");

    [WindowsPowerShellFact]
    public void RecordsPushedOutOfTheTailWindow_AreFoundByTheFallback_WhileTheFileIsOpenForWriting_Powershell51() => RecordsPushedOutOfTheTailWindow_AreFoundByTheFallback_WhileTheFileIsOpenForWritingCase("powershell");

    [PwshFact]
    public void RecordsPushedOutOfTheTailWindow_AreFoundByTheFallback_WhileTheFileIsOpenForWriting_Pwsh() => RecordsPushedOutOfTheTailWindow_AreFoundByTheFallback_WhileTheFileIsOpenForWritingCase("pwsh");

    [WindowsPowerShellFact]
    public void AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHash_Powershell51() => AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHashCase("powershell");

    [PwshFact]
    public void AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHash_Pwsh() => AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHashCase("pwsh");

    [WindowsPowerShellFact]
    public void GrokEnvOverridesAClaudeArgvAndWritesCliGrok_Powershell51() => GrokEnvOverridesAClaudeArgvAndWritesCliGrokCase("powershell");

    [PwshFact]
    public void GrokEnvOverridesAClaudeArgvAndWritesCliGrok_Pwsh() => GrokEnvOverridesAClaudeArgvAndWritesCliGrokCase("pwsh");

    [WindowsPowerShellFact]
    public void GrokCamelCasePayloadFieldsAreRead_Powershell51() => GrokCamelCasePayloadFieldsAreReadCase("powershell");

    [PwshFact]
    public void GrokCamelCasePayloadFieldsAreRead_Pwsh() => GrokCamelCasePayloadFieldsAreReadCase("pwsh");

    [WindowsPowerShellFact]
    public void GrokAutoColorDoesNotAppendToTheTranscript_Powershell51() => GrokAutoColorDoesNotAppendToTheTranscriptCase("powershell");

    [PwshFact]
    public void GrokAutoColorDoesNotAppendToTheTranscript_Pwsh() => GrokAutoColorDoesNotAppendToTheTranscriptCase("pwsh");

    [WindowsPowerShellFact]
    public void CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrinting_Powershell51() => CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrintingCase("powershell");

    [PwshFact]
    public void CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrinting_Pwsh() => CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrintingCase("pwsh");

    // CB-265: same contract as the bash twin -- CLAUDE_CODE_ENTRYPOINT is
    // recorded as "entrypoint", sanitized to [A-Za-z0-9_-], claude only.
    private static string EntrypointRun(string exe, string agent, string? entrypoint, string sessionId)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var codexHome = Path.Combine(tempDir, "codex-home");
        Directory.CreateDirectory(codexHome);
        var grokHome = Path.Combine(tempDir, "grok-home");
        Directory.CreateDirectory(grokHome);
        var payload = Payload(new { session_id = sessionId, cwd = "C:\\proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome, ["GROK_HOME"] = grokHome };
        if (entrypoint is not null) env["CLAUDE_CODE_ENTRYPOINT"] = entrypoint;

        var result = RunHook(exe, agent, "idle", payload, tempDir, env);
        AssertSilentSuccess(result);
        return File.ReadAllText(StatusFile(tempDir, sessionId));
    }

    private static void DesktopEntrypointIsRecordedForAClaudeSessionCase(string exe)
    {
        var status = EntrypointRun(exe, "claude", "claude-desktop", "ep-desktop");

        Assert.Contains("\"entrypoint\":\"claude-desktop\"", status);
        Assert.True(JsonSerializer.Deserialize<SessionStatus>(status)!.IsDesktopHosted);
    }

    private static void ACliEntrypointIsRecordedAsIsAndIsNotDesktopCase(string exe)
    {
        var status = EntrypointRun(exe, "claude", "cli", "ep-cli");

        Assert.Contains("\"entrypoint\":\"cli\"", status);
        Assert.False(JsonSerializer.Deserialize<SessionStatus>(status)!.IsDesktopHosted);
    }

    private static void AnUnsetEntrypointIsWrittenEmptyCase(string exe)
    {
        var status = EntrypointRun(exe, "claude", null, "ep-unset");

        Assert.Contains("\"entrypoint\":\"\"", status);
    }

    private static void QuotesAndBackslashesAreStrippedFromTheEntrypointCase(string exe)
    {
        var status = EntrypointRun(exe, "claude", "claude-\"desk\\top x", "ep-hostile");

        Assert.Equal("claude-desktopx", JsonSerializer.Deserialize<SessionStatus>(status)!.Entrypoint);
    }

    private static void CodexAndGrokFilesNeverCarryAnEntrypointCase(string exe)
    {
        Assert.Contains("\"entrypoint\":\"\"", EntrypointRun(exe, "codex", "claude-desktop", "ep-codex"));
        Assert.Contains("\"entrypoint\":\"\"", EntrypointRun(exe, "grok", "claude-desktop", "ep-grok"));
    }

    [WindowsPowerShellFact]
    public void DesktopEntrypointIsRecordedForAClaudeSession_Powershell51() => DesktopEntrypointIsRecordedForAClaudeSessionCase("powershell");

    [PwshFact]
    public void DesktopEntrypointIsRecordedForAClaudeSession_Pwsh() => DesktopEntrypointIsRecordedForAClaudeSessionCase("pwsh");

    [WindowsPowerShellFact]
    public void ACliEntrypointIsRecordedAsIsAndIsNotDesktop_Powershell51() => ACliEntrypointIsRecordedAsIsAndIsNotDesktopCase("powershell");

    [PwshFact]
    public void ACliEntrypointIsRecordedAsIsAndIsNotDesktop_Pwsh() => ACliEntrypointIsRecordedAsIsAndIsNotDesktopCase("pwsh");

    [WindowsPowerShellFact]
    public void AnUnsetEntrypointIsWrittenEmpty_Powershell51() => AnUnsetEntrypointIsWrittenEmptyCase("powershell");

    [PwshFact]
    public void AnUnsetEntrypointIsWrittenEmpty_Pwsh() => AnUnsetEntrypointIsWrittenEmptyCase("pwsh");

    [WindowsPowerShellFact]
    public void QuotesAndBackslashesAreStrippedFromTheEntrypoint_Powershell51() => QuotesAndBackslashesAreStrippedFromTheEntrypointCase("powershell");

    [PwshFact]
    public void QuotesAndBackslashesAreStrippedFromTheEntrypoint_Pwsh() => QuotesAndBackslashesAreStrippedFromTheEntrypointCase("pwsh");

    [WindowsPowerShellFact]
    public void CodexAndGrokFilesNeverCarryAnEntrypoint_Powershell51() => CodexAndGrokFilesNeverCarryAnEntrypointCase("powershell");

    [PwshFact]
    public void CodexAndGrokFilesNeverCarryAnEntrypoint_Pwsh() => CodexAndGrokFilesNeverCarryAnEntrypointCase("pwsh");

    // CB-266: the host session id, same contract as the bash twin. Invented id:
    // a real one names a real chat on a real machine. PowerShell serializes a
    // hashtable in no promised key order, so these parse the file and read the
    // property rather than pinning bytes the way the bash cases do.
    private const string InventedHostId = "local_00000000-aaaa-bbbb-cccc-000000000001";

    private static string HostRun(string exe, string agent, string? hostId, string sessionId)
    {
        var tempDir = Directory.CreateTempSubdirectory("cb-hook-ps1-").FullName;
        var codexHome = Path.Combine(tempDir, "codex-home");
        Directory.CreateDirectory(codexHome);
        var grokHome = Path.Combine(tempDir, "grok-home");
        Directory.CreateDirectory(grokHome);
        var payload = Payload(new { session_id = sessionId, cwd = "C:\\proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome, ["GROK_HOME"] = grokHome };
        if (hostId is not null) env["CLAUDE_CODE_HOST_SESSION_ID"] = hostId;

        var result = RunHook(exe, agent, "idle", payload, tempDir, env);
        AssertSilentSuccess(result);
        return JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(StatusFile(tempDir, sessionId)))!.HostSessionId;
    }

    private static void HostSessionIdIsRecordedForAClaudeSessionCase(string exe) =>
        Assert.Equal(InventedHostId, HostRun(exe, "claude", InventedHostId, "hs-claude"));

    private static void AnUnsetHostSessionIdIsWrittenEmptyCase(string exe) =>
        Assert.Equal("", HostRun(exe, "claude", null, "hs-unset"));

    private static void HostSessionIdIsStrippedAndClaudeOnlyCase(string exe)
    {
        Assert.Equal("local_abcde", HostRun(exe, "claude", "local_ab\"c\\d e/..", "hs-hostile"));
        Assert.Equal("", HostRun(exe, "codex", InventedHostId, "hs-codex"));
        Assert.Equal("", HostRun(exe, "grok", InventedHostId, "hs-grok"));
    }

    [WindowsPowerShellFact]
    public void HostSessionIdIsRecordedForAClaudeSession_Powershell51() => HostSessionIdIsRecordedForAClaudeSessionCase("powershell");

    [PwshFact]
    public void HostSessionIdIsRecordedForAClaudeSession_Pwsh() => HostSessionIdIsRecordedForAClaudeSessionCase("pwsh");

    [WindowsPowerShellFact]
    public void AnUnsetHostSessionIdIsWrittenEmpty_Powershell51() => AnUnsetHostSessionIdIsWrittenEmptyCase("powershell");

    [PwshFact]
    public void AnUnsetHostSessionIdIsWrittenEmpty_Pwsh() => AnUnsetHostSessionIdIsWrittenEmptyCase("pwsh");

    [WindowsPowerShellFact]
    public void HostSessionIdIsStrippedAndClaudeOnly_Powershell51() => HostSessionIdIsStrippedAndClaudeOnlyCase("powershell");

    [PwshFact]
    public void HostSessionIdIsStrippedAndClaudeOnly_Pwsh() => HostSessionIdIsStrippedAndClaudeOnlyCase("pwsh");
}

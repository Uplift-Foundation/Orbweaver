using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Exercises OrbweaverHook.sh — the bash half of the hook that Claude Code,
// Codex and Grok Build invoke on every tool call — as a real subprocess, the
// same way the CLIs themselves do: argv is `[claude|codex|grok]
// <idle|generating|waiting|ended>`, the JSON payload arrives on stdin, and the
// only observable contract is the exit code, stdout, and stderr (see
// OrbweaverHook.sh's own header comment) plus whatever it writes under
// $TMPDIR/orbweaver.
//
// Every test point at its own fresh TMPDIR (Directory.CreateTempSubdirectory)
// so nothing here ever touches a real /tmp/orbweaver or a developer's
// actual $HOME/.codex — except the path-traversal test below, which proves
// that isolation itself is not airtight.
public class HookScriptShTests
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

    private sealed record HookResult(int ExitCode, string Stdout, string Stderr);

    // Runs the real script as a subprocess. extraEnv is layered on top of a
    // full copy of this process's environment (ProcessStartInfo.Environment
    // starts pre-populated with it) so PATH, HOME etc. still resolve — only
    // TMPDIR, ORBWEAVER_STATUS_ROOT and whatever extraEnv names are
    // overridden, and unsetEnv names removed last, for the tests about the
    // resolution order itself.
    //
    // ORBWEAVER_STATUS_ROOT is set to the scratch directory, not merely
    // TMPDIR, since CB-306: the hook asks the C library for the per-user temp
    // directory ahead of TMPDIR, and that answer is the directory the
    // developer's running Orbweaver is watching. TestBootstrap already exports
    // the variable for this process, pointed at its own scratch root, and the
    // hook would inherit that — pinning it here is what makes each test's
    // file land where that test looks.
    //
    // Grok injects GROK_SESSION_ID / GROK_HOOK_EVENT into every child, and
    // the hook treats those as stronger than argv. A test run from inside a
    // Grok session would otherwise relabel every Claude and Codex case as
    // grok. Strip them here; a test that is actually about that override
    // puts them back through extraEnv.
    private static HookResult RunHook(
        string agent,
        string state,
        string payloadJson,
        string tmpDir,
        IDictionary<string, string>? extraEnv = null,
        IEnumerable<string>? unsetEnv = null)
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
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add(state);
        psi.Environment["TMPDIR"] = tmpDir;
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tmpDir;
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        psi.Environment.Remove("GROK_HOME");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.Environment.Remove("CLAUDE_CODE_HOST_SESSION_ID");
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
            ?? throw new InvalidOperationException("Failed to start bash");

        // A broken pipe here is a pass, not a failure.
        //
        // The hook is allowed to exit before it ever reads stdin, and one test
        // below asserts precisely that: an unrecognised state hits `*) exit 0`
        // before `PAYLOAD=$(cat)`. When it wins that race the pipe is already
        // closed and this write throws IOException("Broken pipe") — so the
        // harness was failing the very behaviour it exists to verify. Locally
        // the process was usually still alive and it passed; under CI load it
        // was not, and it failed on both runners.
        //
        // Swallowed only around the write: the assertions on exit code, stdout
        // and stderr below are untouched, so a hook that genuinely misbehaves
        // still fails.
        try
        {
            process.StandardInput.Write(payloadJson);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Exited without reading its input, which some states do by design.
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new HookResult(process.ExitCode, stdout, stderr);
    }

    // The single most important invariant in the whole script (see its own
    // header comment): under Codex, any stdout at all is read as invalid
    // permission-request JSON, and exit code 2 specifically means "deny". A
    // hook that ever violates this would start silently refusing the user's
    // own tool approvals. Asserted in full in every single test method below
    // rather than factored into a helper that could be skipped by accident.
    private static void AssertSilentSuccess(HookResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private static string StatusDir(string tmpDir) =>
        Path.Combine(tmpDir.TrimEnd('/'), "orbweaver");

    private static string StatusFile(string tmpDir, string sessionId) =>
        Path.Combine(StatusDir(tmpDir), sessionId + ".txt");

    private static string Payload(object fields) => JsonSerializer.Serialize(fields);

    // A codex-agent test that doesn't care about the rollout fallback still
    // must not touch the developer's real ~/.codex (the script falls back to
    // $HOME/.codex whenever CODEX_HOME is unset), so every such test pins
    // CODEX_HOME at an empty scratch directory.
    private static string EmptyCodexHome(string tmpDir)
    {
        var dir = Path.Combine(tmpDir, "codex-home");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [UnixFact]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent()
    {
        foreach (var agent in new[] { "claude", "codex", "grok" })
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var tmp = Directory.CreateTempSubdirectory("cb-hook-");
            var payload = Payload(new { session_id = "s-" + agent + "-" + state, cwd = "/tmp/proj", transcript_path = "" });
            var env = new Dictionary<string, string>
            {
                ["CODEX_HOME"] = EmptyCodexHome(tmp.FullName),
                ["GROK_HOME"] = Path.Combine(tmp.FullName, "grok-home")
            };

            var result = RunHook(agent, state, payload, tmp.FullName, env);

            AssertSilentSuccess(result);
        }
    }

    // The pid walk must never record a process that is not this session.
    //
    // Run from a test harness there is no claude ancestor at all, which
    // exercises the fallback: the first ancestor owning a real tty. What this
    // pins is that the fallback still produces *something* usable rather than
    // regressing to 0 — on macOS a Claude Code file naming no process is
    // dropped outright, so "safe" here means recording the terminal-owning
    // ancestor, not recording nothing.
    //
    // The case that motivated the change cannot be staged from here: it needs a
    // real background agent whose ancestry runs through `claude agents`, and
    // that walk recorded the *viewer's* pid — a process that outlives the
    // session, so the orb never expired. Verified by hand on a real machine
    // instead (the fix records the session's own pid, 28091, where the old walk
    // recorded 0 or the viewer's 10190), and the Windows twin has always worked
    // this way, matching by process name rather than by who owns a terminal.
    [UnixFact]
    public void HookRecordsAUsablePid_EvenWithNoClaudeAncestor()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payload = Payload(new { session_id = "pid-walk", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        var json = File.ReadAllText(StatusFile(tmp.FullName, "pid-walk"));
        var status = JsonSerializer.Deserialize<SessionStatus>(json);
        Assert.NotNull(status);

        // Whatever it found, it must not be this test process — the walk starts
        // at the hook and climbs, so its own pid would mean the loop never ran.
        Assert.NotEqual(Environment.ProcessId, status!.SessionPid);
    }

    [UnixFact]
    public void HookIgnoresAnUnrecognisedState_SilentlyAndWithoutReadingStdin()
    {
        // OrbweaverHook.sh checks $STATE against its four valid values
        // before it ever reads stdin (`case "$STATE" in ... *) exit 0 ;; esac`
        // comes before `PAYLOAD=$(cat)`), so a bogus state is a fast, silent
        // no-op rather than an attempt to parse whatever was piped in.
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "bogus-state", payload, tmp.FullName);

        AssertSilentSuccess(result);
        Assert.False(Directory.Exists(StatusDir(tmp.FullName)));
    }

    [UnixFact]
    public void EndedStateDeletesTheStatusFileAndExitsSilently()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        Directory.CreateDirectory(StatusDir(tmp.FullName));
        var file = StatusFile(tmp.FullName, "s1");
        File.WriteAllText(file, "stale status");

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = "" });
        var result = RunHook("claude", "ended", payload, tmp.FullName);

        AssertSilentSuccess(result);
        Assert.False(File.Exists(file), "ended must delete the session's status file");
    }

    [UnixFact]
    public void EndedStateWithNoExistingStatusFile_IsStillSilentAndSuccessful()
    {
        // `rm -f` on a file that was never written (Ctrl+C'd session, a
        // duplicate SessionEnd, etc.) must not turn into a noisy failure.
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payload = Payload(new { session_id = "never-existed", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "ended", payload, tmp.FullName);

        AssertSilentSuccess(result);
    }

    // *** Security finding ***
    //
    // FILE="$DIR/$SESSION_ID.txt" (OrbweaverHook.sh, ~line 76) builds the
    // status file's path by direct string concatenation of the attacker-
    // controlled session_id straight out of the hook payload — no
    // sanitisation. The SAFE_ID scrubber that exists in the file
    // (`tr -cd '0-9a-fA-F-'`, ~line 223) is applied only to the value handed
    // to sqlite3 later on, never to $SESSION_ID before $FILE is built. A
    // session_id containing ".." therefore walks the resulting path out of
    // $DIR/orbweaver entirely.
    //
    // This test proves the escape is real rather than theoretical: it hands
    // the hook `session_id: "../../evil"` and shows the JSON status blob
    // (cwd, tty, tmux socket, etc.) actually lands two directories above the
    // isolated temp dir, outside both the intended orbweaver status
    // directory *and* the fresh TMPDIR the test set up for isolation.
    [UnixFact]
    public void SessionIdPathTraversal_EscapesTheStatusDirectory()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-traversal-");
        var maliciousSessionId = "../../evil";
        var payload = Payload(new { session_id = maliciousSessionId, cwd = "/tmp/proj", transcript_path = "" });

        // $DIR/$SESSION_ID.txt, normalised the way the shell's own path
        // resolution (and the filesystem) will normalise it.
        var escapedPath = Path.GetFullPath(
            Path.Combine(StatusDir(tmp.FullName), maliciousSessionId + ".txt"));

        try
        {
            var result = RunHook("claude", "idle", payload, tmp.FullName);

            // Even while writing outside its own sandbox, the hook still
            // honours the Codex-safety invariant — exit 0, nothing printed.
            AssertSilentSuccess(result);

            Assert.True(
                File.Exists(escapedPath),
                $"expected the path-traversal escape to actually write a file at {escapedPath} " +
                "— if this now fails, the script has been sanitising SESSION_ID before building " +
                "$FILE and this is no longer a real finding.");

            Assert.False(
                escapedPath.StartsWith(StatusDir(tmp.FullName), StringComparison.Ordinal),
                "the written file should be OUTSIDE the intended orbweaver status directory " +
                "— that is the vulnerability being demonstrated.");

            // Confirms it escaped the test's own isolation, not just the
            // orbweaver subfolder.
            Assert.False(
                escapedPath.StartsWith(tmp.FullName, StringComparison.Ordinal),
                "the written file escaped even the test's own temp sandbox, landing in a " +
                "directory the test does not own.");

            var written = File.ReadAllText(escapedPath);
            Assert.Contains("\"cwd\":\"/tmp/proj\"", written);
        }
        finally
        {
            // The whole point of this test is that the file lands outside
            // anything `using var tmp` will clean up, so it has to be swept
            // up by hand or it leaks a stray file into the shared temp tree
            // on every run.
            if (File.Exists(escapedPath)) File.Delete(escapedPath);
        }
    }

    [UnixFact]
    public void AutoColorMarker_AppendsAgentColorRecordMatchingTheScriptsOwnCksumHash()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        Directory.CreateDirectory(StatusDir(tmp.FullName));
        File.WriteAllText(Path.Combine(StatusDir(tmp.FullName), ".auto-color"), "");

        var projectDir = Directory.CreateTempSubdirectory("cb-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        const string cwd = "/tmp/some-fixed-project-path";
        var payload = Payload(new { session_id = "s1", cwd, transcript_path = transcript });

        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        // Golden value computed the same way the script computes it —
        // `cksum` of the cwd bytes, mod 11, 1-indexed into this exact list —
        // rather than a hardcoded magic number, so this test keeps meaning
        // if the algorithm (or the palette) ever changes.
        var expectedColor = ExpectedAutoColor(cwd);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "s1"));
        Assert.Contains($"\"color\":\"{expectedColor}\"", status);

        var transcriptContents = File.ReadAllText(transcript);
        Assert.Contains(
            $"{{\"type\":\"agent-color\",\"agentColor\":\"{expectedColor}\",\"sessionId\":\"s1\"}}",
            transcriptContents);
    }

    [UnixFact]
    public void WithoutTheAutoColorMarker_NoColorRecordIsEverAppended()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        // No .auto-color marker file created — this is the off-by-default
        // state install-* leaves things in.
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/some-project", transcript_path = transcript });
        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        Assert.Equal("", File.ReadAllText(transcript));
        var status = File.ReadAllText(StatusFile(tmp.FullName, "s1"));
        Assert.Contains("\"color\":\"\"", status);
    }

    [UnixFact]
    public void GrokEnvOverridesAClaudeArgvAndWritesCliGrok()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        Directory.CreateDirectory(Path.Combine(tmp.FullName, "grok-home"));
        var payload = Payload(new { session_id = "g1", cwd = "/tmp/proj", transcript_path = "" });
        var env = new Dictionary<string, string>
        {
            ["GROK_SESSION_ID"] = "g1",
            ["GROK_HOOK_EVENT"] = "session_start",
            ["GROK_HOME"] = Path.Combine(tmp.FullName, "grok-home")
        };

        var result = RunHook("claude", "idle", payload, tmp.FullName, env);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "g1"));
        Assert.Contains("\"cli\":\"grok\"", status);
    }

    [UnixFact]
    public void GrokCamelCasePayloadFieldsAreRead()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var grokHome = Path.Combine(tmp.FullName, "grok-home");
        Directory.CreateDirectory(grokHome);
        var payload = Payload(new { sessionId = "camel-1", cwd = "/tmp/proj", transcriptPath = "" });
        var env = new Dictionary<string, string> { ["GROK_HOME"] = grokHome };

        var result = RunHook("grok", "idle", payload, tmp.FullName, env);
        AssertSilentSuccess(result);

        Assert.True(File.Exists(StatusFile(tmp.FullName, "camel-1")));
        var status = File.ReadAllText(StatusFile(tmp.FullName, "camel-1"));
        Assert.Contains("\"cli\":\"grok\"", status);
    }

    [UnixFact]
    public void GrokAutoColorDoesNotAppendToTheTranscript()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        Directory.CreateDirectory(StatusDir(tmp.FullName));
        File.WriteAllText(Path.Combine(StatusDir(tmp.FullName), ".auto-color"), "");

        var sessionDir = Path.Combine(tmp.FullName, "sess");
        Directory.CreateDirectory(sessionDir);
        var transcript = Path.Combine(sessionDir, "updates.jsonl");
        File.WriteAllText(transcript, "{\"method\":\"session/update\"}\n");
        File.WriteAllText(Path.Combine(sessionDir, "summary.json"),
            """{"generated_title":"Grok title","title_is_manual":false}""");

        const string cwd = "/tmp/some-fixed-project-path";
        var payload = Payload(new { session_id = "g-color", cwd, transcript_path = transcript });
        var env = new Dictionary<string, string>
        {
            ["GROK_HOME"] = Path.Combine(tmp.FullName, "grok-home")
        };

        var result = RunHook("grok", "idle", payload, tmp.FullName, env);
        AssertSilentSuccess(result);

        Assert.Equal("{\"method\":\"session/update\"}\n", File.ReadAllText(transcript));
        var status = File.ReadAllText(StatusFile(tmp.FullName, "g-color"));
        Assert.Contains("\"cli\":\"grok\"", status);
        Assert.Contains("\"title\":\"Grok title\"", status);
        Assert.Contains($"\"color\":\"{ExpectedAutoColor(cwd)}\"", status);
    }

    // Runs the real `cksum` binary the same way the script does, rather than
    // re-implementing the CRC in C#, so a change to the script's algorithm
    // (or to this repo's chosen palette) breaks this test instead of the
    // test quietly agreeing with a stale expectation.
    private static string ExpectedAutoColor(string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cksum",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(cwd);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // "<checksum> <byte-count> [filename]"
        var hash = ulong.Parse(output.Split(' ')[0]);

        string[] palette =
        {
            "red", "orange", "yellow", "green", "teal", "cyan",
            "blue", "purple", "violet", "magenta", "pink"
        };
        var index = (int)(hash % (ulong)palette.Length);
        return palette[index];
    }

    [UnixFact]
    public void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLast()
    {
        AssertCustomTitleWins(writeCustomFirst: true);
        AssertCustomTitleWins(writeCustomFirst: false);
    }

    private static void AssertCustomTitleWins(bool writeCustomFirst)
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var projectDir = Directory.CreateTempSubdirectory("cb-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");

        var customLine = "{\"type\":\"custom-title\",\"customTitle\":\"my name\",\"sessionId\":\"s1\"}";
        var aiLine = "{\"type\":\"ai-title\",\"aiTitle\":\"auto name\",\"sessionId\":\"s1\"}";
        var lines = writeCustomFirst
            ? new[] { customLine, aiLine }
            : new[] { aiLine, customLine };
        File.WriteAllLines(transcript, lines);

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = transcript });
        var result = RunHook("claude", "idle", payload, tmp.FullName);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "s1"));
        Assert.True(
            status.Contains("\"title\":\"my name\"", StringComparison.Ordinal),
            "custom-title should win over ai-title when the custom record was written " +
            (writeCustomFirst ? "first" : "last") + ". Status file was: " + status);
    }

    [UnixFact]
    public void FieldExtractsTheTopLevelCwd_NotANestedToolInputCwd()
    {
        // Regression test for exactly the bug field()'s own comment
        // describes: Codex payloads nest tool_input as arbitrary JSON that
        // can itself carry a "cwd" key (a command's own working directory),
        // which a naive last-match extraction would pick over the real
        // top-level cwd — silently renaming/repositioning the orb for the
        // wrong directory.
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payloadJson =
            "{\"session_id\":\"s2\",\"cwd\":\"/top/level\"," +
            "\"tool_input\":{\"cwd\":\"file:///nested/level\"}," +
            "\"transcript_path\":\"\"}";

        var result = RunHook("claude", "idle", payloadJson, tmp.FullName);
        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, "s2"));
        Assert.Contains("\"cwd\":\"/top/level\"", status);
        Assert.DoesNotContain("/nested/level", status);
    }

    [UnixFact]
    public void CodexRolloutFallback_GlobsForTheSessionsRolloutFile_WithoutCrashingOrPrinting()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var codexHome = Directory.CreateTempSubdirectory("cb-hook-codexhome-").FullName;
        var sessionId = "abc123-session";
        var rolloutDir = Path.Combine(codexHome, "sessions", "2026", "08", "21");
        Directory.CreateDirectory(rolloutDir);
        var rolloutFile = Path.Combine(rolloutDir, $"rollout-2026-08-21T00-00-00-{sessionId}.jsonl");
        // Content the hook "can't read meaningfully" — no session_meta, no
        // UserMessage row — which is exactly the point: this exercises the
        // glob resolution, not rollout parsing.
        File.WriteAllText(rolloutFile, "not a real codex rollout row\n");

        // transcript_path deliberately empty — that's what triggers the glob
        // fallback (OrbweaverHook.sh, ~line 89-93).
        var payload = Payload(new { session_id = sessionId, cwd = "/tmp/proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome };

        var result = RunHook("codex", "idle", payload, tmp.FullName, env);

        AssertSilentSuccess(result);

        var status = File.ReadAllText(StatusFile(tmp.FullName, sessionId));
        Assert.Contains(rolloutFile.Replace("\\", "\\\\"), status);
    }

    [UnixFact]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payloadJson = "{\"cwd\":\"/tmp/proj\",\"transcript_path\":\"\"}";

        var result = RunHook("claude", "idle", payloadJson, tmp.FullName);
        AssertSilentSuccess(result);

        Assert.True(File.Exists(StatusFile(tmp.FullName, "unknown")));
    }

    // CB-265: the hook records CLAUDE_CODE_ENTRYPOINT as "entrypoint" so the app
    // can keep an orb for a Claude Code session Claude Desktop launched, which
    // has no terminal at all. The harness strips the variable from every
    // child (this suite itself usually runs inside Claude Code, where it is
    // "cli"), so each case below sets exactly what it is about.
    private static string EntrypointRun(
        string agent, string? entrypoint, string sessionId, string? hostSessionId = null)
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-");
        var payload = Payload(new { session_id = sessionId, cwd = "/tmp/proj", transcript_path = "" });
        var env = new Dictionary<string, string>
        {
            ["CODEX_HOME"] = EmptyCodexHome(tmp.FullName),
            ["GROK_HOME"] = Path.Combine(tmp.FullName, "grok-home")
        };
        if (entrypoint is not null) env["CLAUDE_CODE_ENTRYPOINT"] = entrypoint;
        if (hostSessionId is not null) env["CLAUDE_CODE_HOST_SESSION_ID"] = hostSessionId;

        var result = RunHook(agent, "idle", payload, tmp.FullName, env);
        AssertSilentSuccess(result);
        return File.ReadAllText(StatusFile(tmp.FullName, sessionId));
    }

    [UnixFact]
    public void DesktopEntrypointIsRecordedForAClaudeSession()
    {
        var status = EntrypointRun("claude", "claude-desktop", "ep-desktop");

        Assert.Contains("\"cli\":\"claude\",\"entrypoint\":\"claude-desktop\",\"host_session_id\":\"\",\"cwd\"", status);
        Assert.True(JsonSerializer.Deserialize<SessionStatus>(status)!.IsDesktopHosted);
    }

    [UnixFact]
    public void ACliEntrypointIsRecordedAsIsAndIsNotDesktop()
    {
        var status = EntrypointRun("claude", "cli", "ep-cli");

        Assert.Contains("\"entrypoint\":\"cli\"", status);
        Assert.False(JsonSerializer.Deserialize<SessionStatus>(status)!.IsDesktopHosted);
    }

    [UnixFact]
    public void AnUnsetEntrypointIsWrittenEmpty()
    {
        var status = EntrypointRun("claude", null, "ep-unset");

        Assert.Contains("\"entrypoint\":\"\"", status);
    }

    [UnixFact]
    public void QuotesAndBackslashesAreStrippedFromTheEntrypointSoTheJsonSurvives()
    {
        var status = EntrypointRun("claude", "claude-\"desk\\top x", "ep-hostile");

        Assert.Contains("\"entrypoint\":\"claude-desktopx\"", status);
        // The whole file must still parse; an unescaped quote would break it.
        Assert.Equal("claude-desktopx", JsonSerializer.Deserialize<SessionStatus>(status)!.Entrypoint);
    }

    [UnixFact]
    public void CodexAndGrokFilesNeverCarryAnEntrypointEvenWhenTheVariableIsInherited()
    {
        Assert.Contains("\"entrypoint\":\"\"", EntrypointRun("codex", "claude-desktop", "ep-codex"));
        Assert.Contains("\"entrypoint\":\"\"", EntrypointRun("grok", "claude-desktop", "ep-grok"));
    }

    // CB-266: Claude Desktop's own id for the chat, recorded right after
    // "entrypoint" so the app can find the chat's Remote Control bridge. Invented
    // id throughout: a real one names a real chat on a real machine.
    private const string InventedHostId = "local_00000000-aaaa-bbbb-cccc-000000000001";

    [UnixFact]
    public void TheHostSessionIdIsRecordedRightAfterTheEntrypointForAClaudeSession()
    {
        var status = EntrypointRun("claude", "claude-desktop", "hs-claude", InventedHostId);

        Assert.Contains(
            "\"entrypoint\":\"claude-desktop\",\"host_session_id\":\"" + InventedHostId + "\",\"cwd\"", status);
        Assert.Equal(InventedHostId, JsonSerializer.Deserialize<SessionStatus>(status)!.HostSessionId);
    }

    [UnixFact]
    public void AnUnsetHostSessionIdIsWrittenEmpty()
    {
        var status = EntrypointRun("claude", "cli", "hs-unset");

        Assert.Contains("\"host_session_id\":\"\"", status);
        Assert.Equal("", JsonSerializer.Deserialize<SessionStatus>(status)!.HostSessionId);
    }

    [UnixFact]
    public void CodexAndGrokFilesNeverCarryAHostSessionIdEvenWhenTheVariableIsInherited()
    {
        Assert.Contains("\"host_session_id\":\"\"", EntrypointRun("codex", "claude-desktop", "hs-codex", InventedHostId));
        Assert.Contains("\"host_session_id\":\"\"", EntrypointRun("grok", "claude-desktop", "hs-grok", InventedHostId));
    }

    [UnixFact]
    public void QuotesBackslashesAndSpacesAreStrippedFromTheHostSessionIdSoTheJsonSurvives()
    {
        var status = EntrypointRun("claude", "claude-desktop", "hs-hostile", "local_ab\"c\\d e/..");

        Assert.Contains("\"host_session_id\":\"local_abcde\"", status);
        Assert.Equal("local_abcde", JsonSerializer.Deserialize<SessionStatus>(status)!.HostSessionId);
    }

    // CB-306: where the hook writes, and why it is no longer TMPDIR.
    //
    // OpenClaw runs its gateway with TMPDIR=~/.openclaw/tmp and every Claude
    // Code session it spawns inherits that — so did a tmux server started from
    // one, and every session inside it. The hook followed TMPDIR; the app read
    // the login session's /var/folders/.../T/; the mini served no sessions to
    // any peer with five alive. The hook now asks the C library for the
    // per-user directory the way the app does, ahead of TMPDIR, and
    // ORBWEAVER_STATUS_ROOT outranks both — the seam RunHook sets for every
    // other test here, and the reason none of them touches the real directory.
    //
    // The per-user arm is driven through a fake `getconf` placed ahead of the
    // real one on PATH, because that is the only way to exercise it without
    // writing into the developer's live status directory: a real getconf
    // answers with the directory the running Orbweaver is watching, and a test
    // that wrote there would draw an orb.

    [UnixFact]
    public void StatusRootOutranksTmpdir()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-tmpdir-");
        var root = Directory.CreateTempSubdirectory("cb-hook-root-");
        var payload = Payload(new { session_id = "root-wins", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "idle", payload, tmp.FullName,
            extraEnv: new Dictionary<string, string> { ["ORBWEAVER_STATUS_ROOT"] = root.FullName });

        AssertSilentSuccess(result);
        Assert.True(File.Exists(StatusFile(root.FullName, "root-wins")), "the file belongs under ORBWEAVER_STATUS_ROOT");
        Assert.False(File.Exists(StatusFile(tmp.FullName, "root-wins")), "and not under TMPDIR");
    }

    [UnixFact]
    public void ThePerUserTempDirectoryOutranksTmpdir_WhenNoStatusRootIsSet()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-tmpdir-");
        var perUser = Directory.CreateTempSubdirectory("cb-hook-peruser-");
        var payload = Payload(new { session_id = "per-user-wins", cwd = "/tmp/proj", transcript_path = "" });

        // getconf prints the directory with a trailing slash, as the real one
        // does; the hook is expected to cope with it.
        var result = RunHook("claude", "idle", payload, tmp.FullName,
            extraEnv: FakeGetconf(perUser.FullName + "/", exitCode: 0),
            unsetEnv: new[] { "ORBWEAVER_STATUS_ROOT" });

        AssertSilentSuccess(result);
        Assert.True(File.Exists(StatusFile(perUser.FullName, "per-user-wins")), "the file belongs under the per-user temp directory");
        Assert.False(File.Exists(StatusFile(tmp.FullName, "per-user-wins")), "and not under TMPDIR");
    }

    // Negative controls for the arm above, and the Linux shape: a getconf
    // that cannot answer, or that names something that is not a directory,
    // leaves TMPDIR in charge exactly as before CB-306 — and says nothing on
    // stderr while doing it, which AssertSilentSuccess checks.
    [UnixFact]
    public void AGetconfWithNoAnswer_LeavesTmpdirInCharge()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-tmpdir-");
        var payload = Payload(new { session_id = "no-answer", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "idle", payload, tmp.FullName,
            extraEnv: FakeGetconf("", exitCode: 1),
            unsetEnv: new[] { "ORBWEAVER_STATUS_ROOT" });

        AssertSilentSuccess(result);
        Assert.True(File.Exists(StatusFile(tmp.FullName, "no-answer")), "with no per-user answer the file belongs under TMPDIR");
    }

    [UnixFact]
    public void AGetconfNamingAMissingDirectory_LeavesTmpdirInCharge()
    {
        var tmp = Directory.CreateTempSubdirectory("cb-hook-tmpdir-");
        var missing = Path.Combine(tmp.FullName, "never-created-" + Guid.NewGuid().ToString("N")) + "/";
        var payload = Payload(new { session_id = "not-a-dir", cwd = "/tmp/proj", transcript_path = "" });

        var result = RunHook("claude", "idle", payload, tmp.FullName,
            extraEnv: FakeGetconf(missing, exitCode: 0),
            unsetEnv: new[] { "ORBWEAVER_STATUS_ROOT" });

        AssertSilentSuccess(result);
        Assert.True(File.Exists(StatusFile(tmp.FullName, "not-a-dir")), "an answer that is not a directory counts as no answer");
        Assert.False(Directory.Exists(missing), "and the hook must not have created it");
    }

    // A `getconf` ahead of the real one on PATH that answers
    // DARWIN_USER_TEMP_DIR with `answer` and `exitCode`, and defers every
    // other question to /usr/bin/getconf. Returns the PATH override to pass as
    // extraEnv.
    private static Dictionary<string, string> FakeGetconf(string answer, int exitCode)
    {
        var bin = Directory.CreateTempSubdirectory("cb-hook-fakebin-").FullName;
        var script = Path.Combine(bin, "getconf");
        File.WriteAllText(script,
            "#!/bin/sh\n"
            + "if [ \"$1\" = DARWIN_USER_TEMP_DIR ]; then printf '%s' '" + answer + "'; exit " + exitCode + "; fi\n"
            + "exec /usr/bin/getconf \"$@\"\n");
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return new Dictionary<string, string>
        {
            ["PATH"] = bin + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
        };
    }
}

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-316: the seams the v4 desk pieces have with what this process does not
/// own — real transcript files in each CLI's own format, read while another
/// handle is writing them, through each CLI's own parser; a real tmux server
/// (on a private socket, never the user's) creating the detached `orbweaver`
/// session and adding a window to it; and the login-shell launch line run by
/// a real shell.
/// </summary>
public class RelayV4FileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb316-" + Guid.NewGuid().ToString("N")[..8]);

    public RelayV4FileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // One row per append, in each CLI's real shape (copied from the parsers'
    // own suites, which are the authority on what a row is).
    public static TheoryData<string> Clis() => new() { "claude", "codex", "grok" };

    private static SessionSource Source(string cli) => cli switch
    {
        "codex" => SessionSource.Codex,
        "grok" => SessionSource.Grok,
        _ => SessionSource.ClaudeCode,
    };

    private static string Row(string cli, int i) => cli switch
    {
        "codex" => i % 2 == 0
            ? $$$$"""{"timestamp":"2026-08-19T16:57:{{{{i:00}}}}.000Z","ordinal":{{{{i}}}},"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"u{{{{i}}}}","content":[{"type":"text","text":"user {{{{i}}}}","text_elements":[]}]}}}"""
            : $$$$"""{"timestamp":"2026-08-19T16:57:{{{{i:00}}}}.000Z","ordinal":{{{{i}}}},"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"a{{{{i}}}}","content":[{"type":"Text","text":"agent {{{{i}}}}"}],"phase":"final_answer"}}}""",
        "grok" => "{\"timestamp\":" + (1788198313 + i) + ",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":"
                  + (i % 2 == 0
                      ? "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"user " + i + "\"}}"
                      : "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"agent " + i + "\"}}")
                  + "}}",
        _ => i % 2 == 0
            ? $$$"""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"user {{{i}}}"}]},"uuid":"u{{{i}}}","timestamp":"2026-08-28T17:53:{{{i:00}}}.000Z"}"""
            : $$$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"agent {{{i}}}"}]},"uuid":"a{{{i}}}","timestamp":"2026-08-28T17:53:{{{i:00}}}.000Z"}""",
    };

    private string Transcript(string cli, int rows)
    {
        var path = Path.Combine(_dir, cli + ".jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, rows).Select(i => Row(cli, i)));
        return path;
    }

    private static void Append(string path, string cli, int from, int count) =>
        File.AppendAllLines(path, Enumerable.Range(from, count).Select(i => Row(cli, i)));

    private static TranscriptWatcher Watcher(string path, string cli) =>
        new("ph", "s1", TranscriptWatcher.For(new SessionStatus { Source = Source(cli), TranscriptPath = path }));

    [Theory]
    [MemberData(nameof(Clis))]
    public void A_real_transcript_backfills_its_parsed_rows(string cli)
    {
        var path = Transcript(cli, 4);
        var (total, windows) = Watcher(path, cli).Start();
        Assert.Equal(4, total);
        var lines = J(windows.Single()).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user", "assistant"], lines.Select(l => l.GetProperty("role").GetString()));
        Assert.Equal(["user 0", "agent 1", "user 2", "agent 3"], lines.Select(l => l.GetProperty("text").GetString()));
    }

    [Theory]
    [MemberData(nameof(Clis))]
    public void Two_appends_inside_the_debounce_are_one_tail_window(string cli)
    {
        var path = Transcript(cli, 2);
        var w = Watcher(path, cli);
        w.Start();

        Append(path, cli, 2, 1);
        Assert.Empty(w.Poll(T0));
        Append(path, cli, 3, 1);                               // within 250 ms of the first
        Assert.Empty(w.Poll(T0.AddMilliseconds(120)));
        Assert.Empty(w.Poll(T0.AddMilliseconds(300)));          // the second append restarted the quiet period
        var tail = J(w.Poll(T0.AddMilliseconds(400)).Single());
        Assert.Equal((true, 2, 4, 2), (tail.GetProperty("live").GetBoolean(), tail.GetProperty("from_line").GetInt32(),
            tail.GetProperty("total").GetInt32(), tail.GetProperty("lines").GetArrayLength()));
        Assert.Empty(w.Poll(T0.AddSeconds(5)));
    }

    [Theory]
    [MemberData(nameof(Clis))]
    public void A_truncated_or_rotated_transcript_is_a_reset(string cli)
    {
        var path = Transcript(cli, 6);
        var w = Watcher(path, cli);
        w.Start();

        // Truncated in place (a rewritten file).
        File.WriteAllLines(path, Enumerable.Range(0, 2).Select(i => Row(cli, i)));
        w.Poll(T0);
        var reset = J(w.Poll(T0.AddMilliseconds(300)).Single());
        Assert.True(reset.GetProperty("reset").GetBoolean());
        Assert.Equal(2, reset.GetProperty("total").GetInt32());

        // Rotated: a different file under a status that now names it, whose
        // first row differs from the one the phone holds.
        var rotated = Path.Combine(_dir, cli + "-rotated.jsonl");
        File.WriteAllLines(rotated, Enumerable.Range(10, 3).Select(i => Row(cli, i)));
        var status = new SessionStatus { Source = Source(cli), TranscriptPath = path };
        var moving = new TranscriptWatcher("ph", "s1", TranscriptWatcher.For(status) with { PathOf = () => status.TranscriptPath });
        moving.Start();
        status.TranscriptPath = rotated;
        moving.Poll(T0);
        Assert.True(J(moving.Poll(T0.AddMilliseconds(300)).Single()).GetProperty("reset").GetBoolean());
    }

    [Fact]
    public void A_transcript_held_open_for_writing_is_still_read_and_a_missing_one_is_null()
    {
        var path = Transcript("claude", 1);
        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            Assert.Single(TranscriptWatcher.ParseFile(path, SessionSource.ClaudeCode)!);
        Assert.Null(TranscriptWatcher.ParseFile(Path.Combine(_dir, "nope.jsonl"), SessionSource.ClaudeCode));
        Assert.Null(TranscriptWatcher.FileStamp(Path.Combine(_dir, "nope.jsonl")));
        Assert.Equal(new FileInfo(path).Length, TranscriptWatcher.FileStamp(path)!.Value.Length);
        Assert.Equal((0, 0), (Watcher("", "claude").Start().Total, Watcher("", "claude").Start().Windows.Count));
    }

    // ---- real processes ---------------------------------------------------------

    private static string? Tmux() =>
        new[] { "/opt/homebrew/bin/tmux", "/usr/local/bin/tmux", "/usr/bin/tmux" }.FirstOrDefault(File.Exists);

    private static (int Code, string Out) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, output);
    }

    // Skipped where tmux is not installed (the Windows leg, and a Mac without
    // it) — named in the PR body. A private socket, so the user's own tmux
    // server and any real `orbweaver` session are never touched.
    [Fact]
    public void The_detached_orbweaver_session_is_created_once_then_given_windows_by_real_tmux()
    {
        if (OperatingSystem.IsWindows() || Tmux() is not { } tmux) return;
        var socket = Path.Combine(Path.GetTempPath(), "cb316-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        try
        {
            var (first, pane1) = Run(tmux, TerminalScripts.TmuxArgs(socket, TerminalScripts.TmuxDetachedArgs(false, _dir, "sleep 30")));
            Assert.Equal(0, first);
            Assert.StartsWith("%", pane1.Trim());

            Assert.Equal(0, Run(tmux, TerminalScripts.TmuxArgs(socket, "has-session", "-t", "=" + TerminalScripts.DetachedSession)).Code);
            var (second, pane2) = Run(tmux, TerminalScripts.TmuxArgs(socket, TerminalScripts.TmuxDetachedArgs(true, _dir, "sleep 30")));
            Assert.Equal(0, second);
            Assert.NotEqual(pane1.Trim(), pane2.Trim());

            var windows = Run(tmux, TerminalScripts.TmuxArgs(socket, "list-windows", "-t", TerminalScripts.DetachedSession, "-F", "#{pane_current_path}")).Out;
            Assert.Equal(2, windows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

            // The negative control: creating it again when it exists fails, which
            // is why the launcher asks has-session first.
            Assert.NotEqual(0, Run(tmux, TerminalScripts.TmuxArgs(socket, TerminalScripts.TmuxDetachedArgs(false, _dir, "sleep 30"))).Code);
        }
        finally
        {
            Run(tmux, "-S", socket, "kill-server");
            try { File.Delete(socket); } catch { }
        }
    }

    [Fact]
    public void The_launch_line_runs_the_binary_with_every_argument_intact_through_a_login_shell()
    {
        if (OperatingSystem.IsWindows()) return;
        var line = NewChatCommand.LaunchLine("/bin/sh", "/bin/echo", ["--model", "it's a model", "--parent-session-id", "$HOME"]);
        var (code, output) = Run("/bin/sh", "-c", line);
        Assert.Equal(0, code);
        Assert.Equal("--model it's a model --parent-session-id $HOME", output.Trim());
    }
}

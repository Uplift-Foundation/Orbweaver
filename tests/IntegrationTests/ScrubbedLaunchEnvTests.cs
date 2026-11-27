using System.Diagnostics;
using Xunit;

namespace Orbweaver.IntegrationTests
{
    // CB-413: a real scratch tmux server whose own global environment is
    // poisoned (as a server started from an agent's shell is), launched into
    // through the real builders by a client that itself carries TMUX. The
    // pane's env must lack the poison; the control launch (no wrap) proves
    // the setup would have shown it.
    public class ScrubbedLaunchEnvTests : IDisposable
    {
        // The first four are on the fixed list; the last two match only a
        // prefix, which is what a live agent's server actually carries
        // (five CLAUDE_CODE_USER_DELEGATED_SESSION_* names on the mini) and
        // what only the in-pane preamble can reach.
        private static readonly string[] Poison =
            ["CLAUDE_JOB_DIR", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDECODE",
             "CLAUDE_CODE_USER_DELEGATED_SESSION_DIRECT_API", "CLAUDE_CODE_PARENT_SESSION_ID"];

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb413-" + Guid.NewGuid().ToString("N")[..8]);
        private string Socket => Path.Combine(_dir, "s.sock");

        public ScrubbedLaunchEnvTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            if (TerminalLauncher.ResolveTmux() is { } t) Run(t, [], "-S", Socket, "kill-server");
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static (int Code, string Out) Run(string exe, Dictionary<string, string> env, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var (k, v) in env) psi.Environment[k] = v;
            foreach (var a in args) psi.ArgumentList.Add(a);
            AgentEnvironment.Scrubbed(psi);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, o);
        }

        private string[] PaneEnvNames(string tmux, string[] launchArgs)
        {
            // The client carries a stray TMUX, as the app did in the incident.
            // Run scrubs it, and the explicit -S picks the server regardless, so
            // this does not exercise server routing — that is the unit-level
            // claim that every tmux start info is scrubbed. What this test
            // proves is the pane side: the server's own global environment.
            var (code, _) = Run(tmux, new() { ["TMUX"] = "/nonexistent/sock,1,0" }, ["-S", Socket, .. launchArgs]);
            Assert.Equal(0, code);
            var file = Path.Combine(_dir, "env.txt");
            for (var i = 0; i < 50 && !File.Exists(file); i++) Thread.Sleep(100);
            Assert.True(File.Exists(file), "pane never wrote its environment");
            Thread.Sleep(200);
            return File.ReadAllLines(file).Select(l => l.Split('=', 2)[0]).ToArray();
        }

        private void StartPoisonedServer(string tmux)
        {
            var env = Poison.ToDictionary(n => n, _ => "poison");
            var psi = new ProcessStartInfo(tmux) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var (k, v) in env) psi.Environment[k] = v;
            foreach (var a in new[] { "-S", Socket, "-f", "/dev/null", "new-session", "-d", "-s", "anchor", "sleep 120" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            Assert.Equal(0, p.ExitCode);
        }

        private string Dump => "env > " + _dir + "/env.txt; sleep 3";

        [Fact]
        public void The_launched_pane_lacks_the_servers_inherited_identity()
        {
            if (OperatingSystem.IsWindows() || TerminalLauncher.ResolveTmux() is not { } tmux) return;
            StartPoisonedServer(tmux);
            var names = PaneEnvNames(tmux, TerminalScripts.TmuxDetachedArgs(false, _dir, Dump));
            foreach (var p in Poison) Assert.DoesNotContain(p, names);
            // tmux still sets its own, which the launched CLI is meant to see.
            Assert.Contains("TMUX_PANE", names);
        }

        [Fact]
        public void Control_an_unwrapped_command_does_show_the_poison()
        {
            if (OperatingSystem.IsWindows() || TerminalLauncher.ResolveTmux() is not { } tmux) return;
            StartPoisonedServer(tmux);
            var names = PaneEnvNames(tmux, ["new-session", "-d", "-s", "ctl", Dump]);
            Assert.Contains("CLAUDE_JOB_DIR", names);
            Assert.Contains("CLAUDE_CODE_USER_DELEGATED_SESSION_DIRECT_API", names);
            Assert.Contains("CLAUDE_CODE_PARENT_SESSION_ID", names);
        }
    }
}

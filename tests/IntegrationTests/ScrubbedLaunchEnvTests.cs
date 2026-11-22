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
        private static readonly string[] Poison =
            ["CLAUDE_JOB_DIR", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDECODE"];

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
            // The client is handed a TMUX naming a socket that does not exist:
            // unscrubbed, tmux would try it and fail rather than use ours.
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
        }
    }
}

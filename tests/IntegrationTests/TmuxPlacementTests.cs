using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// TMUX_TMPDIR and TMUX are process-wide, and every tmux this process runs reads
// them, so nothing else may run while a test here has them pointed at its own
// server.
[CollectionDefinition("TmuxServerEnv", DisableParallelization = true)]
public sealed class TmuxServerEnvCollection { }

// Where New chat and an orb attach actually land, on a real tmux.
//
// The unit tests pin which placement each caller asks for and what arguments
// that placement becomes. Neither proves what tmux does with them: that a
// new-window really lands in the client's session and moves the client there,
// or that the split really lands in the window the client is on. Those are
// tmux's answers, and they are what the user sees.
//
// Isolated rather than careful: TMUX_TMPDIR moves the *default* server's socket,
// so TerminalLauncher — which always talks to the default server — talks to a
// private one here, and whoever's tmux is running on the machine is never
// touched. TMUX is cleared too, because a tmux run from inside tmux follows it
// instead. The directory is short on purpose: a Unix socket path over 104 bytes
// is refused with "File name too long", and a scratch directory under the
// runner's temp path can exceed that once tmux adds its own tmux-<uid>/default.
[Collection("TmuxServerEnv")]
public sealed class TmuxPlacementTests : IDisposable
{
    private const string Session = "cbtest";

    private readonly string _dir = "/tmp/cbt-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _tmux = TerminalLauncher.ResolveTmux() ?? "tmux";
    private readonly string? _savedTmux = Environment.GetEnvironmentVariable("TMUX");
    private readonly string? _savedTmpDir = Environment.GetEnvironmentVariable("TMUX_TMPDIR");

    // Pinned rather than inherited (CB-335). tmux 3.6a rewrites a TAB in -F
    // output to "_" for a client whose locale is not UTF-8, which is exactly
    // the installed app's situation (launchd hands a GUI app no LANG or LC_*)
    // and was a developer pane's too. A CI image that exports a UTF-8 LANG
    // hid that, so this suite cleared nothing and passed on broken code there.
    // Clearing all three makes every machine run these tests in the locale the
    // bug needs, which is what turns them into a real negative control.
    private static readonly string[] LocaleVars = ["LANG", "LC_ALL", "LC_CTYPE"];
    private readonly Dictionary<string, string?> _savedEnv = new[] { "LANG", "LC_ALL", "LC_CTYPE", "TMUX_PANE" }
        .ToDictionary(k => k, Environment.GetEnvironmentVariable);
    private Process? _client;

    public TmuxPlacementTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("TMUX", null);
        foreach (var v in LocaleVars) Environment.SetEnvironmentVariable(v, null);

        // Not the cause of CB-335 (measured: the same four failed with it
        // unset), but a tmux run from inside a pane follows it the way it
        // follows TMUX, so it is cleared with it.
        Environment.SetEnvironmentVariable("TMUX_PANE", null);
        Environment.SetEnvironmentVariable("TMUX_TMPDIR", _dir);

        // -f /dev/null so a developer's ~/.tmux.conf (base-index, a
        // default-command, plugins) cannot move window indices or placement.
        // Hardening only: nobody here has one.
        Tmux("-f", "/dev/null", "new-session", "-d", "-s", Session, "-x", "200", "-y", "50", "-c", "/tmp");
    }

    public void Dispose()
    {
        try { Tmux("kill-server"); } catch (InvalidOperationException) { }

        if (_client is { HasExited: false }) _client.Kill(entireProcessTree: true);
        _client?.Dispose();

        Environment.SetEnvironmentVariable("TMUX", _savedTmux);
        foreach (var (k, v) in _savedEnv) Environment.SetEnvironmentVariable(k, v);
        Environment.SetEnvironmentVariable("TMUX_TMPDIR", _savedTmpDir);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // A client attached through a pty, which is what makes the server "one
    // the user is looking at". stdin stays open, or script(1) would see EOF and
    // detach at once.
    private void AttachClient()
    {
        var psi = new ProcessStartInfo("/usr/bin/script")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-q", "/dev/null", _tmux, "attach", "-t", Session }) psi.ArgumentList.Add(a);

        _client = Process.Start(psi)!;
        _client.OutputDataReceived += (_, _) => { };
        _client.BeginOutputReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Tmux("list-clients", "-F", "#{client_session}").Trim() != Session)
        {
            Assert.True(DateTime.UtcNow < deadline, "the tmux client never attached");
            Thread.Sleep(100);
        }
    }

    private string Tmux(params string[] args)
    {
        var psi = new ProcessStartInfo(_tmux)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // -u for symmetry with the app's own calls: this helper's formats are
        // single-field today, but a later tab format would be eaten the same way.
        foreach (var a in TerminalScripts.TmuxProcessArgs(_tmux, args)) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException("tmux " + string.Join(" ", args) + ": " + p.StandardError.ReadToEnd());
        return stdout;
    }

    private string[] Windows() =>
        Tmux("list-windows", "-t", Session, "-F", "#{window_index}:#{window_panes}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private string ClientWindow() => Tmux("list-clients", "-F", "#{window_index}").Trim();

    private string WindowOf(string pane) => Tmux("display-message", "-p", "-t", pane, "#{window_index}").Trim();

    [MacTmuxFact]
    public void ANewChatOpensItsOwnWindowAndLeavesTheUsersAlone()
    {
        AttachClient();
        var before = ClientWindow();

        var pane = TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", "/tmp");

        Assert.NotNull(pane);
        Assert.StartsWith("%", pane);
        Assert.Equal(new[] { before + ":1", WindowOf(pane!) + ":1" }, Windows());
        Assert.NotEqual(before, WindowOf(pane!));
        Assert.Equal(WindowOf(pane!), ClientWindow());
    }

    // Round 6a's split, unchanged: an orb attach lands in the window the client
    // is on, as a second pane, and no window is created.
    [MacTmuxFact]
    public void AnOrbAttachStillSplitsTheUsersWindow()
    {
        AttachClient();
        var before = ClientWindow();

        var pane = TerminalLauncher.PlaceInTmux("exec /bin/sleep 30", "/tmp");

        Assert.NotNull(pane);
        Assert.Equal(before, WindowOf(pane!));
        Assert.Equal(new[] { before + ":2" }, Windows());
    }

    private string PathOf(string pane) =>
        Tmux("display-message", "-p", "-t", pane, "#{pane_current_path}").Trim();

    private bool PaneAlive(string pane) =>
        Tmux("list-panes", "-a", "-F", "#{pane_id}").Split('\n').Contains(pane);

    // tmux format-expands -c, so this directory — which exists — became
    // `…/fmtcbtestx`, which does not, and tmux started the pane in $HOME with
    // the CLI running there. The cd guard in the command lands it exactly.
    [MacTmuxFact]
    public void BothPlacementsLandInADirectoryNamedLikeATmuxFormat() => LandsInAFormatNamedDirectory();

    // The same, with the pane's shell starting a second late (CB-235).
    //
    // For a directory like this one -c cannot land, so the pane begins in
    // $HOME and only the command's own `cd` moves it — some moments after the
    // placement has returned. The case above used to read tmux's
    // pane_current_path straight away, and under load it now and then
    // sampled the pane before the cd had run: "/Users/<you>" for the
    // directory. A slow shell makes that window a full second wide, which
    // failed the old shape of the test every time and is why this case is
    // kept as well as fixed: the wait below is on the command having actually
    // started where it should, not on how quickly a shell happens to start.
    [MacTmuxFact]
    public void BothPlacementsLandInADirectoryNamedLikeATmuxFormatEvenWhenTheShellStartsLate()
    {
        var slow = Path.Combine(_dir, "slow-sh");
        File.WriteAllText(slow, "#!/bin/sh\nsleep 1\nexec /bin/sh \"$@\"\n");
        File.SetUnixFileMode(slow, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Tmux("set-option", "-g", "default-shell", slow);

        LandsInAFormatNamedDirectory();
    }

    private void LandsInAFormatNamedDirectory()
    {
        AttachClient();
        var dir = Path.Combine(_dir, "fmt#{session_name}x");
        Directory.CreateDirectory(dir);

        var (chatCommand, chatLanded) = ReportingWhereItRuns("chat");
        var (attachCommand, attachLanded) = ReportingWhereItRuns("attach");

        var chat = TerminalLauncher.PlaceInOwnTmuxWindow(chatCommand, dir);
        var attach = TerminalLauncher.PlaceInTmux(attachCommand, dir);

        // Where each command actually ran, said by the command itself after
        // the cd guard — and once it has, tmux's own view of the pane has
        // settled too, so both can be asserted without racing the shell.
        Assert.Equal(RealPath(dir), LandedIn(chatLanded));
        Assert.Equal(RealPath(dir), LandedIn(attachLanded));
        Assert.Equal(RealPath(dir), PathOf(chat!));
        Assert.Equal(RealPath(dir), PathOf(attach!));
    }

    // A placed command that writes its own working directory to a file before
    // it settles into the long-running process a pane needs.
    private (string Command, string Marker) ReportingWhereItRuns(string name)
    {
        var marker = Path.Combine(_dir, name + ".cwd");
        return ($"pwd -P > '{marker}'; exec /bin/sleep 30", marker);
    }

    // Waits on the command's own report, with a backstop only for a pane that
    // never runs its command at all — not a tolerance the passing path uses.
    private static string LandedIn(string marker)
    {
        var backstop = Stopwatch.StartNew();
        while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
        {
            Assert.True(backstop.Elapsed < TimeSpan.FromMinutes(1), "the placed command never reported where it ran");
            Thread.Sleep(50);
        }

        return File.ReadAllText(marker).Trim();
    }

    // A cwd that has gone runs nothing on either path: the guard exits, the
    // pane closes, and no CLI starts in $HOME in its place. For New chat that
    // leaves the user's one window; for an attach, the split closes and the
    // user's window is back to one pane.
    [MacTmuxFact]
    public void AMissingDirectoryRunsNothingForANewChat()
    {
        AttachClient();

        var pane = TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", Path.Combine(_dir, "gone"));

        WaitUntilGone(pane);
        Assert.Single(Windows());
    }

    [MacTmuxFact]
    public void AMissingDirectoryRunsNothingForAnAttach()
    {
        AttachClient();
        var before = ClientWindow();

        var pane = TerminalLauncher.PlaceInTmux("exec /bin/sleep 30", Path.Combine(_dir, "gone"));

        WaitUntilGone(pane);
        Assert.Equal(new[] { before + ":1" }, Windows());
    }

    private void WaitUntilGone(string? pane)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pane is not null && PaneAlive(pane))
        {
            Assert.True(DateTime.UtcNow < deadline, "the pane for a missing directory stayed open");
            Thread.Sleep(100);
        }
    }

    // /tmp is a link to /private/tmp, and tmux reports the resolved path.
    private static string RealPath(string path) =>
        new DirectoryInfo(path).FullName.StartsWith("/tmp/", StringComparison.Ordinal)
            ? "/private" + new DirectoryInfo(path).FullName
            : new DirectoryInfo(path).FullName;

    // The CB-335 regression itself, through TerminalFocuser's helper (the one
    // click-to-focus and phone placement use), with the locale cleared by the
    // constructor: one client, on this session, with its tty. Before the fix
    // the tab came back as "_", the listing was one field, and ParseClients
    // skipped it — "nobody is attached".
    [MacTmuxFact]
    public void TheClientListingSurvivesANonUtf8Locale()
    {
        AttachClient();

        Assert.True(TerminalFocuser.TryRun(_tmux, out var listing, "list-clients", "-F", TerminalScripts.ClientListFormat));
        var clients = TerminalScripts.ParseClients(listing);

        var client = Assert.Single(clients);
        Assert.Equal(Session, client.Session);
        Assert.False(string.IsNullOrEmpty(client.Tty));
    }

    // The other half of the same sanitising: a non-ASCII session name came
    // back as "caf_".
    [MacTmuxFact]
    public void ANonAsciiSessionNameRoundTrips()
    {
        Tmux("new-session", "-d", "-s", "café", "-x", "80", "-y", "24", "-c", "/tmp");

        Assert.True(TerminalFocuser.TryRun(_tmux, out var listing, "list-sessions", "-F", "#{session_name}"));

        Assert.Contains("café", listing.Split('\n'));
        Assert.DoesNotContain("caf_", listing.Split('\n'));
    }

    // A server with no client is nowhere the user is looking, so New chat
    // declines it — the caller then opens a terminal window — and creates
    // nothing in it.
    [MacTmuxFact]
    public void WithNoClientANewChatLeavesTmuxAlone()
    {
        Assert.Null(TerminalLauncher.PlaceInOwnTmuxWindow("exec /bin/sleep 30", "/tmp"));
        Assert.Single(Windows());
    }
}

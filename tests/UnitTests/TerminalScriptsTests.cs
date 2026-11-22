using Xunit;

namespace Orbweaver.Tests
{
    // The AppleScript handed to osascript, and the small pure rules around it.
    //
    // Nothing here runs a process — that is the whole reason this class exists
    // apart from TerminalFocuser. Which makes it the one part of the
    // click-an-orb path that can be checked at all, and it is the part where
    // being wrong is worst: the output is a script that selects a window and
    // sends keystrokes into a live terminal session, so a builder that drops a
    // clause does not show something wrong, it presses something somewhere else.
    //
    // The assertions are deliberately about *structure* rather than exact text.
    // Comparing the whole script against a golden string would fail on every
    // harmless reflow and say nothing about which clause went missing, and it is
    // the clauses that were bought with real bug reports.
    public class TerminalScriptsTests
    {
        // --- TmuxProcessArgs: tmux -u (CB-335) ---

        [Theory]
        [InlineData("/opt/homebrew/bin/tmux")]
        [InlineData("tmux")]
        [InlineData("/usr/local/bin/tmux")]
        public void TmuxIsToldItsTerminalIsUtf8(string exe)
        {
            Assert.Equal(new[] { "-u", "list-clients" }, TerminalScripts.TmuxProcessArgs(exe, ["list-clients"]));
        }

        // -u is a global flag, so it has to come before -S and the command.
        [Fact]
        public void TheUtf8FlagPrecedesThePinnedSocket()
        {
            var args = TerminalScripts.TmuxProcessArgs("/opt/homebrew/bin/tmux",
                TerminalScripts.TmuxArgs("/tmp/tmux-501/default", "list-clients"));

            Assert.Equal(new[] { "-u", "-S", "/tmp/tmux-501/default", "list-clients" }, args);
        }

        [Fact]
        public void NoArgumentsStillGetTheFlag()
        {
            Assert.Equal(new[] { "-u" }, TerminalScripts.TmuxProcessArgs("tmux", []));
        }

        // The same helpers run ps, id, osascript and powershell, which do not
        // take -u and must be handed exactly what they were.
        [Theory]
        [InlineData("/bin/ps")]
        [InlineData("/usr/bin/osascript")]
        [InlineData("powershell.exe")]
        [InlineData("/usr/local/bin/tmux-wrapper")]
        public void OtherToolsAreLeftAlone(string exe)
        {
            var args = new[] { "-eo", "args=" };

            Assert.Equal(args, TerminalScripts.TmuxProcessArgs(exe, args));
        }

        // --- TmuxArgs: pinning the server ---

        // Several tmux servers can coexist (plain tmux, tmuxinator, a -L named
        // socket) and a pane id is only unique within one, so the socket has to
        // be pinned when the status file recorded it. Sending to the wrong server
        // means typing into somebody else's pane.
        [Fact]
        public void ARecordedSocketIsPinnedWithDashS()
        {
            var args = TerminalScripts.TmuxArgs("/tmp/tmux-501/default", "send-keys", "-t", "%30");

            Assert.Equal(
                new[] { "-S", "/tmp/tmux-501/default", "send-keys", "-t", "%30" }, args);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoSocketTheArgumentsAreUnchanged(string? socket)
        {
            var args = TerminalScripts.TmuxArgs(socket, "list-clients");

            Assert.Equal(new[] { "list-clients" }, args);
        }

        [Fact]
        public void PinningPreservesArgumentOrder()
        {
            var args = TerminalScripts.TmuxArgs("/s", "a", "b", "c", "d");

            Assert.Equal(new[] { "-S", "/s", "a", "b", "c", "d" }, args);
        }

        [Fact]
        public void AnEmptyArgumentListStillGetsTheSocket()
        {
            Assert.Equal(new[] { "-S", "/s" }, TerminalScripts.TmuxArgs("/s"));
        }

        // --- ShellCommandLine: the cd guard, as one line -----------------------
        //
        // The same rule TmuxAttachScript states as two lines of a script file,
        // stated once as a single line for the callers that hand a command
        // straight into an AppleScript string literal instead of a file on disk
        // (CB-80).

        [Fact]
        public void WithACwdTheLineStartsWithTheCdGuard()
        {
            var line = TerminalScripts.ShellCommandLine("/Users/user/proj", "exec '/usr/bin/claude' 'agents'");

            Assert.Equal(
                "cd -- '/Users/user/proj' || exit 1; exec '/usr/bin/claude' 'agents'", line);
        }

        // `cd ''` fails, and `|| exit 1` would take the whole command down with
        // it — the one thing this line exists to run. No cwd means no guard.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoCwdTheLineIsJustTheCommand(string? cwd)
        {
            Assert.Equal("exec 'x'", TerminalScripts.ShellCommandLine(cwd, "exec 'x'"));
        }

        // TmuxAttachCommand's own output already begins "unset TMUX; exec …", and
        // wrapping it in a second `exec` would try to exec a program literally
        // named "unset". ShellCommandLine trusts the command as handed in rather
        // than reshaping it.
        [Fact]
        public void ATmuxAttachCommandIsPrefixedNotRewrapped()
        {
            var command = TerminalScripts.TmuxAttachCommand("/usr/bin/tmux", null, "claude-swarm");

            var line = TerminalScripts.ShellCommandLine("/tmp/x", command);

            Assert.Equal("cd -- '/tmp/x' || exit 1; " + command, line);
            Assert.StartsWith("cd -- '/tmp/x' || exit 1; unset TMUX; exec ", line);
        }

        // --- RunScriptFor / ITermRunScript / TerminalRunScript: CB-80 ---------
        //
        // `open -a <app> <script file>` asks a terminal to open an *executable
        // file*, which is exactly what iTerm2's Automation gate exists to ask
        // about — and it asked on every click rather than once, because the
        // file this app writes has a fresh mtime each launch. These build the
        // AppleScript that asks the terminal to *run a command* instead, which
        // CB-79's probes found does not raise that dialog.

        [Fact]
        public void ITermIsAskedToRunACommandNotOpenAFile()
        {
            var script = TerminalScripts.RunScriptFor("/Applications/iTerm.app", "/tmp/x", "exec 'x'");

            Assert.NotNull(script);
            Assert.Contains("tell application \"iTerm\"", script);
            Assert.Contains("create window with default profile command", script);
            Assert.DoesNotContain("open -a", script);
        }

        [Fact]
        public void TerminalAppIsAskedToRunACommandNotOpenAFile()
        {
            var script = TerminalScripts.RunScriptFor("/System/Applications/Utilities/Terminal.app", "/tmp/x", "exec 'x'");

            Assert.NotNull(script);
            Assert.Contains("tell application \"Terminal\"", script);
            Assert.Contains("do script", script);
        }

        // Ghostty and WezTerm have no comparable scripting surface, so this
        // answers null — the signal AgentTeamViewer's launcher reads as "fall
        // back to the script-file mechanism" for those two apps only.
        [Theory]
        [InlineData("/Applications/Ghostty.app")]
        [InlineData("/Applications/WezTerm.app")]
        [InlineData("/Applications/Unknown Terminal.app")]
        public void AnUnscriptableTerminalGetsNoAppleScript(string app)
        {
            Assert.Null(TerminalScripts.RunScriptFor(app, "/tmp/x", "exec 'x'"));
        }

        // The command line is embedded in the AppleScript string literal, so it
        // has to go through the same escaping the rest of this file relies on —
        // otherwise a directory or session name with a double quote in it closes
        // the literal early and everything after it is read as AppleScript.
        [Fact]
        public void TheCommandLineIsEscapedForTheAppleScriptLiteral()
        {
            var script = TerminalScripts.RunScriptFor(
                "/System/Applications/Utilities/Terminal.app", "/tmp/say \"hi\"", "exec 'x'");

            Assert.Contains("say \\\"hi\\\"", script);
        }

        // iTerm2's command carries double quotes of its own — around the eval'd
        // substitution — and those have to be escaped for the literal as well,
        // or the literal closes at `eval ` and the rest is read as AppleScript.
        [Fact]
        public void TheITermCommandIsEscapedForTheAppleScriptLiteral()
        {
            var script = TerminalScripts.RunScriptFor(
                "/Applications/iTerm.app", "/tmp/x", "exec 'x'", "/bin/zsh");

            Assert.Contains("-c 'eval \\\"$(printf %s ", script);
            Assert.Contains("| /usr/bin/base64 -D)\\\"'\")", script);
        }

        // --- ITermCommand: iTerm2's `command` is argv, not a shell ------------
        //
        // The defect: iTerm2 execs `command` as argv with no shell in between,
        // so `cd '<dir>' || exit 1; exec …` was exec'd as a program named "cd",
        // and every iTerm2 launch opened a window with no Claude Code in it.

        const string Hostile = "/Users/user/it's a \"quoted\" dir \\n\\(x) $HOME ~";

        // The first word iTerm2 execs is a shell, asked to run the line — never
        // the line's own first word.
        [Fact]
        public void ITermIsHandedAShellRatherThanTheLine()
        {
            var command = TerminalScripts.ITermCommand("/bin/zsh", "cd '/tmp' || exit 1; exec 'x'");

            Assert.StartsWith("'/bin/zsh' -l -i -c 'eval \"$(printf %s ", command);
            Assert.EndsWith(" | /usr/bin/base64 -D)\"'", command);
            Assert.DoesNotContain("cd ", command);
        }

        // Login and interactive both: measured on a real Mac, `zsh -lc` alone
        // left ~/.local/bin — where `claude` installs — off PATH, because it is
        // set in .zshrc, which only an interactive shell reads.
        [Fact]
        public void TheShellIsALoginAndInteractiveOne()
        {
            var command = TerminalScripts.ITermCommand("/bin/bash", "exec 'x'");

            Assert.StartsWith("'/bin/bash' -l -i -c ", command);
        }

        // Every character iTerm2 was measured to reinterpret stays out of what
        // it is handed: backslashes (interpreted even inside single quotes),
        // tildes (expanded when bare), and any apostrophe beyond the four that
        // delimit the two quoted words — for a line built from the worst
        // directory the probes used.
        [Fact]
        public void NothingITermReinterpretsReachesIt()
        {
            var line = TerminalScripts.ShellCommandLine(Hostile, "exec '/usr/bin/claude' 'it'\\''s'");
            var command = TerminalScripts.ITermCommand("/bin/zsh", line);

            Assert.DoesNotContain("\\", command);
            Assert.DoesNotContain("~", command);
            Assert.DoesNotContain("\n", command);
            Assert.Equal(4, command.Count(c => c == '\''));
        }

        // What the shell decodes is the line, byte for byte — including the
        // non-ASCII, which base64 carries as UTF-8.
        [Theory]
        [InlineData("cd '/tmp' || exit 1; exec 'x'")]
        [InlineData("unset TMUX; exec 'tmux' '-S' '/tmp/tmux-501/default' 'attach'")]
        [InlineData("cd '/Users/user/caf\u00e9 \u2713' || exit 1; exec 'x'")]
        [InlineData("")]
        public void TheShellDecodesExactlyTheLine(string line)
        {
            var command = TerminalScripts.ITermCommand("/bin/zsh", line);

            const string open = "printf %s ";
            var start = command.IndexOf(open, StringComparison.Ordinal) + open.Length;
            var end = command.IndexOf(" | /usr/bin/base64", StringComparison.Ordinal);
            var decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(command[start..end]));

            Assert.Equal(line, decoded);
        }

        // The whole path: RunScriptFor puts the cd guard and the command inside
        // the shell's payload, not in iTerm2's argv.
        [Fact]
        public void RunScriptForHandsITermTheWrappedLine()
        {
            var script = TerminalScripts.RunScriptFor(
                "/Applications/iTerm.app", Hostile, "exec 'x'", "/bin/zsh");
            var want = TerminalScripts.ITermRunScript(
                TerminalScripts.ITermCommand("/bin/zsh", TerminalScripts.ShellCommandLine(Hostile, "exec 'x'")));

            Assert.Equal(want, script);
            Assert.DoesNotContain("it's", script);
        }

        // Terminal.app is unchanged: `do script` types into a real shell, so it
        // gets the bare line, and ignores the login-shell argument entirely.
        [Fact]
        public void TerminalAppStillGetsTheBareLine()
        {
            var script = TerminalScripts.RunScriptFor(
                "/System/Applications/Utilities/Terminal.app", "/tmp/x", "exec 'x'", "/bin/bash");

            Assert.Equal(TerminalScripts.TerminalRunScript("cd -- '/tmp/x' || exit 1; exec 'x'"), script);
        }

        // Each character a directory name can carry that some layer would
        // otherwise take — the shell (space, apostrophe, dollar), iTerm2's
        // splitter (backslash, double quote, tilde) or the AppleScript literal
        // (double quote, backslash) — reaches the shell as exactly the cd
        // guard ShellCommandLine built, and none of it reaches iTerm2 raw.
        [Theory]
        [InlineData("/Users/user/with space")]
        [InlineData("/Users/user/it's")]
        [InlineData("/Users/user/say \"hi\"")]
        [InlineData("/Users/user/back\\slash")]
        [InlineData("/Users/user/back\\n")]
        [InlineData("/Users/user/$HOME")]
        [InlineData("/Users/user/~tilde")]
        [InlineData("/Users/user/-leading-dash")]
        public void EachHostileCharacterReachesTheShellIntact(string cwd)
        {
            var script = TerminalScripts.RunScriptFor("/Applications/iTerm.app", cwd, "exec 'x'", "/bin/zsh");

            Assert.Equal("cd -- " + TerminalScripts.ShellQuote(cwd) + " || exit 1; exec 'x'", DecodedPayload(script!));
            Assert.DoesNotContain(cwd[12..], script);
        }

        // No cwd: no cd guard at all — `cd -- ''` would fail and take the
        // command with it — so the shell is handed the command alone.
        [Fact]
        public void WithNoCwdTheShellRunsTheCommandAlone()
        {
            var script = TerminalScripts.RunScriptFor("/Applications/iTerm.app", null, "exec 'x'", "/bin/zsh");

            Assert.Equal("exec 'x'", DecodedPayload(script!));
        }

        // The cd guard ends option parsing. Under the interactive shell iTerm2
        // now runs, `cd` can be the user's own function — zoxide's
        // `--cmd cd` fuzzy-matches a missing directory with one argument and
        // goes straight to the builtin with `--` — so a bare `cd` let a deleted
        // cwd land in some other project and run the command there.
        [Fact]
        public void TheCdGuardEndsOptionParsing()
        {
            Assert.StartsWith("cd -- '/tmp/x' || exit 1; ", TerminalScripts.ShellCommandLine("/tmp/x", "exec 'x'"));
        }

        // Terminal.app's script, pinned byte for byte: this change is iTerm2's,
        // and `do script` typing a line into a real shell was never broken.
        [Fact]
        public void TheTerminalRunScriptIsUnchanged()
        {
            Assert.Equal(
                "tell application \"Terminal\"\n" +
                "    set t to do script \"cd -- '/tmp/a \\\"b\\\"' || exit 1; exec 'x'\"\n" +
                "    return tty of t\n" +
                "end tell",
                TerminalScripts.RunScriptFor(
                    "/System/Applications/Utilities/Terminal.app", "/tmp/a \"b\"", "exec 'x'", "/bin/zsh")!
                    .ReplaceLineEndings("\n"));
        }

        static string DecodedPayload(string script)
        {
            const string open = "printf %s ";
            var start = script.IndexOf(open, StringComparison.Ordinal) + open.Length;
            var end = script.IndexOf(" | /usr/bin/base64", StringComparison.Ordinal);
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(script[start..end]));
        }

        // --- LoginShellFor: which shell runs the line -------------------------

        [Theory]
        [InlineData("/bin/zsh", "/bin/zsh")]
        [InlineData("/bin/bash", "/bin/bash")]
        [InlineData("/opt/homebrew/bin/bash", "/opt/homebrew/bin/bash")]
        [InlineData("/bin/sh", "/bin/sh")]
        [InlineData("/bin/dash", "/bin/dash")]
        [InlineData("/bin/ksh", "/bin/ksh")]
        [InlineData("/usr/local/bin/mksh", "/usr/local/bin/mksh")]
        [InlineData("/nix/store/abc-zsh-5.9/bin/zsh", "/nix/store/abc-zsh-5.9/bin/zsh")]
        // Not POSIX: the line's `'\''` and `unset` would be misparsed.
        [InlineData("/opt/homebrew/bin/fish", "/bin/zsh")]
        [InlineData("/opt/homebrew/bin/nu", "/bin/zsh")]
        [InlineData("/usr/local/bin/xonsh", "/bin/zsh")]
        // Unset, empty, or not an absolute path.
        [InlineData(null, "/bin/zsh")]
        [InlineData("", "/bin/zsh")]
        [InlineData("zsh", "/bin/zsh")]
        // Characters that cannot go to iTerm2 safely, even inside quotes.
        [InlineData("/Users/user/my bin/zsh", "/bin/zsh")]
        [InlineData("/Users/user/it's/zsh", "/bin/zsh")]
        [InlineData("/Users/user/a\\b/zsh", "/bin/zsh")]
        [InlineData("~/bin/zsh", "/bin/zsh")]
        public void TheLoginShellIsTheUsersWhenItCanRunTheLine(string? shellEnv, string want)
        {
            Assert.Equal(want, TerminalScripts.LoginShellFor(shellEnv, _ => true));
        }

        // A $SHELL that names a file which is not there falls back too: iTerm2
        // handed a command it cannot exec opens a plain shell instead, silently,
        // which is the very failure this fixes.
        [Fact]
        public void AShellThatDoesNotExistFallsBack()
        {
            Assert.Equal("/bin/zsh", TerminalScripts.LoginShellFor("/opt/homebrew/bin/bash", _ => false));
        }

        [Fact]
        public void TheExistenceCheckIsAskedAboutTheShellItself()
        {
            string? asked = null;
            TerminalScripts.LoginShellFor("/opt/homebrew/bin/bash", p => { asked = p; return true; });

            Assert.Equal("/opt/homebrew/bin/bash", asked);
        }

        [Fact]
        public void TheITermRunScriptReturnsTheNewSessionsTty()
        {
            var script = TerminalScripts.ITermRunScript("exec 'x'");

            Assert.Contains("tell current session of w", script);
            Assert.Contains("return tty", script);
        }

        [Fact]
        public void TheTerminalRunScriptReturnsTheNewTabsTty()
        {
            var script = TerminalScripts.TerminalRunScript("exec 'x'");

            Assert.Contains("return tty of t", script);
        }

        // do script with no "in" clause opens a brand-new window rather than
        // reusing the frontmost one — the same fresh-window arrival
        // `open -a Terminal.app <script>` gave.
        [Fact]
        public void TheTerminalRunScriptDoesNotTargetAnExistingWindow()
        {
            var script = TerminalScripts.TerminalRunScript("exec 'x'");

            Assert.DoesNotContain("in window", script);
            Assert.DoesNotContain("in front window", script);
        }

        // --- ShellQuote and TmuxAttachScript: attaching a detached server ---
        //
        // The script that answers a click on an agent-team member whose pane is
        // alive in a `claude-swarm-<pid>` socket nothing is attached to. Being
        // wrong here is the same shape as being wrong anywhere else in this file:
        // the output is executed, not displayed.

        // A directory with a space or an apostrophe has to arrive as one word,
        // and the shell's way to put a quote inside single quotes is to close,
        // escape and reopen. Getting this wrong splits the argument, and `cd`
        // then either fails or — worse — succeeds somewhere else.
        [Theory]
        [InlineData("/Users/user/Source/Claude-Buddy", "'/Users/user/Source/Claude-Buddy'")]
        [InlineData("/Users/user/My Projects", "'/Users/user/My Projects'")]
        [InlineData("/Users/user/user's", "'/Users/user/user'\\''s'")]
        [InlineData("", "''")]
        public void AnArgumentIsQuotedTheWayTheShellUnderstands(string value, string want)
        {
            Assert.Equal(want, TerminalScripts.ShellQuote(value));
        }

        [Fact]
        public void TheAttachScriptPinsTheServerAndExecsTheAttach()
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/opt/homebrew/bin/tmux", "/tmp/tmux-501/claude-swarm-88341",
                "claude-swarm", "/Users/user/Source/Claude-Buddy");

            Assert.StartsWith("#!/bin/sh\n", script);

            // The socket, pinned. A pane id is only unique within one server, and
            // a swarm socket is not the default one — attaching to the wrong
            // server would show somebody else's session.
            Assert.Contains("'-S' '/tmp/tmux-501/claude-swarm-88341'", script);

            // And the session, targeted, with "=" for an exact match. This used
            // to be a plain `attach`, on the reasoning that select-window and
            // select-pane had already aimed the client — which is true about the
            // window and says nothing about *which session*, and tmux picks the
            // most recently used one. The swarm server holds two, because this
            // app's own remote-control relay cohabits it, and the relay is always
            // the busier: every click landed there and the tab closed on its own.
            Assert.EndsWith("'attach' '-t' '=claude-swarm'\n", script);

            // exec, not a call: the window's shell becomes tmux rather than
            // waiting behind it.
            Assert.Contains("exec '/opt/homebrew/bin/tmux'", script);

            // The cd is for after the attach ends — detach or exit drops the
            // window to a shell, and the useful place to land is the directory
            // whose orb was clicked.
            Assert.Contains("cd -- '/Users/user/Source/Claude-Buddy' || exit 1\n", script);
        }

        // No socket recorded means the default server, which `attach` finds on
        // its own. TmuxArgs already answers this and is reused rather than
        // re-decided, so the two can never disagree about when -S is passed.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoSocketTheAttachDoesNotPinAServer(string? socket)
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/usr/bin/tmux", socket, "claude-swarm", "/tmp");

            Assert.DoesNotContain("-S", script);
            Assert.EndsWith("unset TMUX; exec '/usr/bin/tmux' 'attach' '-t' '=claude-swarm'\n", script);
        }

        // `cd ''` fails, and `|| exit 1` would then take the attach with it —
        // the one thing the script exists to do. A session with no cwd recorded
        // still gets its terminal.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoCwdTheScriptIsJustTheAttach(string? cwd)
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/usr/bin/tmux", "/tmp/s", "claude-swarm", cwd);

            Assert.DoesNotContain("cd ", script);
            Assert.Equal(
                "#!/bin/sh\nunset TMUX; exec '/usr/bin/tmux' '-S' '/tmp/s' 'attach' '-t' '=claude-swarm'\n",
                script);
        }

        // A directory with an apostrophe in it, end to end — the case the
        // quoting rule above exists for, asserted where it is actually used.
        [Fact]
        public void AnAwkwardDirectoryStillArrivesAsOneWord()
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/usr/bin/tmux", null, "claude-swarm", "/Users/user/user's stuff");

            Assert.Contains("cd -- '/Users/user/user'\\''s stuff' || exit 1", script);
        }

        // A session name carries the same hazards a path does — the relay's is
        // generated from an account directory — so it goes through the same
        // quoting rather than being trusted because it looks tame.
        [Fact]
        public void AnAwkwardSessionNameStillArrivesAsOneWord()
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/usr/bin/tmux", null, "user's session", "/tmp");

            Assert.Contains("'-t' '=user'\\''s session'", script);
        }

        // The relay's own shape, which is the name that must never be attached to
        // by accident and the one whose prefix hazard "=" exists for.
        [Fact]
        public void TheRelayShapedNameIsTargetedExactly()
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/usr/bin/tmux", "/tmp/tmux-501/claude-swarm-78137",
                "claude-buddy-rc--claude-host-mbp", "/tmp");

            Assert.Contains("'-t' '=claude-buddy-rc--claude-host-mbp'", script);
        }

        // The untargeted form cannot come back. The signature makes it
        // unexpressible, and even an empty session still emits a `-t` — which
        // fails loudly with "can't find session" rather than silently attaching to
        // whatever the server happened to have used last.
        [Fact]
        public void AnEmptySessionStillEmitsATargetRatherThanNone()
        {
            var script = TerminalScripts.TmuxAttachScript("/usr/bin/tmux", null, "", "/tmp");

            Assert.Contains("'-t'", script);
            Assert.DoesNotContain("'attach'\n", script);
        }

        // --- TmuxAttachCommand: the same attach as a pane ---------------------

        // The socket arm arrives beside the user now, which needs the attach as a
        // command rather than as a script. Owner's own words at the round-13
        // fork: "Splits in beside you showing what THAT agent is doing."
        [Fact]
        public void TheAttachCommandCarriesTheSocketAndTheExactTarget()
        {
            var command = TerminalScripts.TmuxAttachCommand(
                "/opt/homebrew/bin/tmux", "/tmp/tmux-501/claude-swarm-78137", "claude-swarm");

            Assert.Contains("'-S' '/tmp/tmux-501/claude-swarm-78137'", command);
            Assert.EndsWith("'attach' '-t' '=claude-swarm'", command);
        }

        // TMUX is cleared, and it earns that twice: the pane this runs in belongs
        // to the user's own server, so it inherits a TMUX pointing at *that*
        // server while the command attaches to a different one — every tmux
        // command typed in the new pane afterwards would talk to the wrong
        // server. It also makes the nested-session guard moot rather than
        // depending on a reading of when tmux applies it.
        [Fact]
        public void TheAttachCommandClearsTmuxBeforeAttaching()
        {
            var command = TerminalScripts.TmuxAttachCommand("/usr/bin/tmux", null, "s");

            Assert.StartsWith("unset TMUX; exec ", command);
        }

        // The script is the same command with a shebang and an optional cd, so
        // the two can never disagree about the target or the quoting.
        [Fact]
        public void TheScriptIsTheCommandWithAShebang()
        {
            var command = TerminalScripts.TmuxAttachCommand("/usr/bin/tmux", "/tmp/s", "claude-swarm");
            var script = TerminalScripts.TmuxAttachScript("/usr/bin/tmux", "/tmp/s", "claude-swarm", null);

            Assert.Equal("#!/bin/sh\n" + command + "\n", script);
        }

        // --- LeafOf: naming a Windows Terminal tab after its directory ---

        [Theory]
        [InlineData("/Users/user/Source/Claude-Buddy", "Claude-Buddy")]
        [InlineData("C:\\Users\\user\\Source\\Claude-Buddy", "Claude-Buddy")]
        [InlineData("/Users/user/Source/Claude-Buddy/", "Claude-Buddy")]
        [InlineData("C:\\Source\\proj\\", "proj")]
        [InlineData("relative", "relative")]
        public void TheLeafIsTheLastPathSegment(string cwd, string want)
        {
            Assert.Equal(want, TerminalScripts.LeafOf(cwd));
        }

        // Both separators, because a status file can be written on either
        // platform and read on the same one — but a path with mixed separators
        // is what a WSL session produces, so the last of either wins.
        [Fact]
        public void EitherSeparatorEndsASegment()
        {
            Assert.Equal("deep", TerminalScripts.LeafOf("C:\\Users/user\\Source/deep"));
        }

        // "C:" is a drive, not a directory anyone named, so it produces nothing
        // rather than a tab called "C:".
        [Theory]
        [InlineData("C:", "")]
        [InlineData("C:\\", "")]
        [InlineData("/", "")]
        [InlineData("", "")]
        public void ADriveOrRootNamesNothing(string cwd, string want)
        {
            Assert.Equal(want, TerminalScripts.LeafOf(cwd));
        }

        // --- EscapeForAppleScript: quoting dictated text ---

        // The order is the point. Escaping quotes first would then have their
        // new backslashes escaped again by the backslash pass, doubling them —
        // so backslashes go first.
        [Theory]
        [InlineData("plain text", "plain text")]
        [InlineData("say \"hello\"", "say \\\"hello\\\"")]
        [InlineData("a\\b", "a\\\\b")]
        [InlineData("a\\\"b", "a\\\\\\\"b")]
        public void QuotesAndBackslashesAreEscapedInThatOrder(string text, string want)
        {
            Assert.Equal(want, TerminalScripts.EscapeForAppleScript(text));
        }

        // The case that matters for a script being *built* rather than merely
        // displayed: an unescaped quote closes the AppleScript string literal
        // early, and everything after it is read as AppleScript. Escaping is
        // what stops dictated text from becoming instructions.
        [Fact]
        public void TextCannotCloseTheStringLiteralItIsPlacedIn()
        {
            var hostile = "\" & (do shell script \"rm -rf ~\") & \"";

            var escaped = TerminalScripts.EscapeForAppleScript(hostile);

            // Every quote in the result is preceded by a backslash, so none of
            // them terminates the literal.
            for (var i = 0; i < escaped.Length; i++)
            {
                if (escaped[i] != '"') continue;
                Assert.True(i > 0 && escaped[i - 1] == '\\', $"unescaped quote at {i}");
            }
        }

        // --- ActivateThenSettle: waiting for activation to land ---

        // This used to be `delay 0.2`, and it lost the race often enough to be
        // reported as "clicking an orb doesn't switch desktops any more":
        // `activate` was measured at 145ms, 167ms and 531ms on three consecutive
        // runs, so the spread was the problem rather than the average. Polling
        // `frontmost` waits exactly as long as it needs to.
        [Fact]
        public void ActivationIsPolledRatherThanWaitedOutWithAFixedDelay()
        {
            var script = TerminalScripts.ActivateThenSettle("iTerm");

            Assert.Contains("tell application \"iTerm\" to activate", script);
            Assert.Contains($"repeat {TerminalScripts.ActivationPollTicks} times", script);
            Assert.Contains("if frontmost of application \"iTerm\" then exit repeat", script);
            Assert.Contains("delay 0.05", script);
        }

        // Both the delay and the repeat sit *outside* the tell block on purpose:
        // inside one they are dispatched to the application, which does not
        // understand them, and the whole script fails with "Can't continue
        // delay". The one-line `tell ... to activate` form is what keeps them
        // out.
        [Fact]
        public void TheLoopIsNotInsideATellBlock()
        {
            var script = TerminalScripts.ActivateThenSettle("Terminal");

            Assert.DoesNotContain("end tell", script);
            Assert.Contains("to activate", script);
        }

        [Fact]
        public void TheAppNameIsTheOneAskedFor()
        {
            Assert.Contains("\"Terminal\"", TerminalScripts.ActivateThenSettle("Terminal"));
            Assert.DoesNotContain("iTerm", TerminalScripts.ActivateThenSettle("Terminal"));
        }

        // The ceiling is a backstop, not a timeout anyone should reach: if the
        // app never comes forward, selecting a window will fail anyway and
        // hanging the click is worse than trying and missing. Pinned so it stays
        // a couple of seconds rather than drifting into something a user waits
        // through.
        [Fact]
        public void TheActivationCeilingIsAboutTwoSeconds()
        {
            Assert.Equal(2000, TerminalScripts.ActivationPollTicks * 50);
        }

        // --- ITermSelectScript ---

        [Fact]
        public void TheITermScriptActivatesBeforeItSelects()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc-123");

            var activate = script.IndexOf("to activate", StringComparison.Ordinal);
            var select = script.IndexOf("select w", StringComparison.Ordinal);

            Assert.True(activate >= 0 && select >= 0);

            // Activate, *wait*, then select. The order is load-bearing and the
            // obvious experiment gives the wrong answer: tested from a desktop
            // with no terminal window on it, select-then-activate appears to
            // work, because activation alone switches Spaces there. From a
            // desktop that does have one, the same script needed two clicks.
            Assert.True(activate < select, "the script must activate before selecting");
        }

        [Fact]
        public void TheITermScriptMatchesOnTheGivenPropertyAndValue()
        {
            var script = TerminalScripts.ITermSelectScript("tty", "/dev/ttys004");

            Assert.Contains("if tty of s is \"/dev/ttys004\" then", script);
        }

        // The retry loop, which is the fix for orbs going to the wrong desktop on
        // the first click and the right one on the second. `frontmost of
        // application` flips true when the app becomes active, which is *before*
        // macOS has finished raising the window activation brings forward — so a
        // select landing in that gap is overruled a moment later.
        [Fact]
        public void TheITermSelectionIsReAssertedUntilItTakes()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc");

            Assert.Contains($"repeat {TerminalScripts.SelectionVerifyTicks} times", script);

            // Checked against the *window's* own answer. `id of current window`
            // was tried first and is useless: it is iTerm's internal notion of
            // current and reads as correct while a different window is on screen,
            // which is exactly the failure being chased.
            Assert.Contains("if frontmost of w then return", script);
        }

        // Deliberately not `set frontmost of w to true` alongside the select:
        // that was in the first version and measured worse than the code it
        // replaced — three failures in six against none.
        [Fact]
        public void TheITermScriptDoesNotSetFrontmostDirectly()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc");

            Assert.DoesNotContain("set frontmost of w to true", script);
        }

        [Fact]
        public void TheITermScriptSelectsWindowTabAndSessionTogether()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc");

            Assert.Contains("select w", script);
            Assert.Contains("select t", script);
            Assert.Contains("select s", script);
        }

        // --- TerminalSelectScript ---

        // Accepts either form the two paths produce: a bare "ttys004" from the
        // hook, or a "/dev/ttys004" client tty from tmux. Both have to end up
        // matching Terminal's own `tty of t`, which is the fully qualified one.
        [Theory]
        [InlineData("ttys004")]
        [InlineData("/dev/ttys004")]
        public void EitherFormOfTheTtyIsNormalisedToTheDevicePath(string tty)
        {
            var script = TerminalScripts.TerminalSelectScript(tty);

            Assert.Contains("if tty of t is \"/dev/ttys004\" then", script);
            Assert.DoesNotContain("\"/dev//dev/", script);
        }

        [Fact]
        public void TheTerminalScriptActivatesBeforeItSelects()
        {
            var script = TerminalScripts.TerminalSelectScript("ttys004");

            var activate = script.IndexOf("to activate", StringComparison.Ordinal);
            var select = script.IndexOf("set selected of t to true", StringComparison.Ordinal);

            Assert.True(activate >= 0 && select >= 0);
            Assert.True(activate < select, "the script must activate before selecting");
        }

        // Same Spaces rule as iTerm: re-assert until it takes, checked against
        // the window's own `frontmost`.
        [Fact]
        public void TheTerminalSelectionIsReAssertedUntilItTakes()
        {
            var script = TerminalScripts.TerminalSelectScript("ttys004");

            Assert.Contains($"repeat {TerminalScripts.SelectionVerifyTicks} times", script);
            Assert.Contains("if frontmost of w then return", script);
            Assert.Contains("set index of w to 1", script);
        }

        [Fact]
        public void TheSelectionCeilingIsUnderASecond()
        {
            Assert.Equal(600, TerminalScripts.SelectionVerifyTicks * 50);
        }

        // Both select scripts wrap their work in a tell block for the app whose
        // windows they are walking, and both close it. An unclosed tell block is
        // a syntax error osascript reports at run time, by which point the click
        // has already silently done nothing.
        [Fact]
        public void BothSelectScriptsOpenAndCloseTheirTellBlock()
        {
            foreach (var script in new[]
                     {
                         TerminalScripts.ITermSelectScript("id", "abc"),
                         TerminalScripts.TerminalSelectScript("ttys004"),
                     })
            {
                var opens = Occurrences(script, "tell application");
                var closes = Occurrences(script, "end tell");

                // One `tell ... to activate` one-liner needs no `end tell`, so
                // the block form is the difference between the two counts.
                Assert.Equal(opens - 1, closes);
            }
        }

        private static int Occurrences(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        // --- ChooseClient -----------------------------------------------------

        // The two-client state that exposed this, as a fixture: iTerm with two
        // tmux clients, one on the session holding the target pane and one on
        // another session. With a single client attached the distinction does not
        // exist, which is why "any client" survived until a machine had two — and
        // two clients are two windows of the same application, so choosing wrong
        // brings the wrong window to the front.
        private static TerminalScripts.TmuxClient Client(
            string tty, string session, string activity = "1000", bool control = false) =>
            new(tty, session, activity, control);

        // Already looking at the right session: that one, and no switch. Switching
        // it would be a no-op at best; switching a *different* client onto the
        // session would drag that one off whatever it was showing.
        [Fact]
        public void AClientAlreadyOnTheTargetSessionIsChosenAndNotSwitched()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys009", "1", activity: "2000"),
                    Client("/dev/ttys002", "0", activity: "1000"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys002", choice!.Value.Client.Tty);
            Assert.False(choice.Value.NeedsSwitch);
        }

        // ...even when the other client is the more recently active one, which is
        // the case that matters: recency only decides between clients that are all
        // equally wrong, and a client already on the session is right.
        [Fact]
        public void BeingOnTheSessionBeatsBeingMoreRecentlyActive()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys002", "0", activity: "1000"),
                    Client("/dev/ttys009", "1", activity: "9999"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys002", choice!.Value.Client.Tty);
            Assert.False(choice.Value.NeedsSwitch);
        }

        // Nobody on the session: the most recently active client, switched. A
        // person with several terminals open is working in the one they touched
        // last, so moving that one is the least surprising way to show them
        // something.
        [Fact]
        public void WithNobodyOnTheSessionTheMostRecentClientIsSwitched()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys009", "1", activity: "1787874871"),
                    Client("/dev/ttys002", "2", activity: "1787875071"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys002", choice!.Value.Client.Tty);
            Assert.True(choice.Value.NeedsSwitch);
        }

        // Two on the session: the more recently active of those, still no switch.
        [Fact]
        public void AmongSeveralOnTheSessionTheMostRecentWins()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys002", "0", activity: "1000"),
                    Client("/dev/ttys009", "0", activity: "2000"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys009", choice!.Value.Client.Tty);
            Assert.False(choice.Value.NeedsSwitch);
        }

        // Nothing attached at all: no client to choose, and the caller must not
        // invent one. Also a client row with no tty, which `list-clients` can
        // produce and which nothing downstream could aim at.
        [Fact]
        public void WithNoUsableClientThereIsNoChoice()
        {
            Assert.Null(TerminalScripts.ChooseClient(
                Array.Empty<TerminalScripts.TmuxClient>(), "0"));

            Assert.Null(TerminalScripts.ChooseClient(new[] { Client("", "0") }, "0"));
        }

        // Control mode travels with the choice, because the caller has to know:
        // an iTerm2 `-CC` client's tty belongs to a hidden control tab rather than
        // to any window worth looking at, so the per-tty selection is skipped for
        // it and the app is activated instead.
        [Fact]
        public void ControlModeTravelsWithTheChosenClient()
        {
            var choice = TerminalScripts.ChooseClient(
                new[] { Client("/dev/ttys002", "0", control: true) }, "0");

            Assert.True(choice!.Value.Client.ControlMode);
        }

        // --- AttachedWithNoUsableTty (CB-158) -----------------------------

        // The exact case that used to open a new iTerm tab on a session someone
        // was already looking at: a client attached to the right session, but
        // list-clients gave it no tty. ChooseClient rightly can't choose it —
        // nothing downstream can aim a window-selection script at an empty tty
        // — but that is not the same fact as nobody being attached at all, and
        // this is the function that tells the two apart.
        [Fact]
        public void AttachedClientWithEmptyTtyIsReported()
        {
            Assert.True(TerminalScripts.AttachedWithNoUsableTty(
                new[] { Client("", "0") }, "0"));
        }

        // Truly nobody attached: no rows at all, and no row names the target
        // session either. Both are the genuine "detached" fact this function
        // must not blur with the case above.
        [Fact]
        public void NoClientsAtAllIsNotReportedAsAttached()
        {
            Assert.False(TerminalScripts.AttachedWithNoUsableTty(
                Array.Empty<TerminalScripts.TmuxClient>(), "0"));
        }

        [Fact]
        public void ClientsOnlyOnOtherSessionsAreNotReportedAsAttached()
        {
            Assert.False(TerminalScripts.AttachedWithNoUsableTty(
                new[] { Client("/dev/ttys009", "1") }, "0"));
        }

        // A usable client on the target session means ChooseClient will have
        // found it already — this function only needs to answer for the case
        // ChooseClient passed over.
        [Fact]
        public void UsableClientOnTargetSessionIsNotReportedAsUnaimable()
        {
            Assert.False(TerminalScripts.AttachedWithNoUsableTty(
                new[] { Client("/dev/ttys002", "0") }, "0"));
        }

        // Mixed rows: one elsewhere with a fine tty, one on the target session
        // with none. The target session's own row is what decides this, not
        // whichever row happens first or has the tty.
        [Fact]
        public void MixedRowsStillFindTheUnaimableOneOnTheTargetSession()
        {
            Assert.True(TerminalScripts.AttachedWithNoUsableTty(
                new[] { Client("/dev/ttys009", "1"), Client("", "0") }, "0"));
        }

        // --- MostRecentClient / ParseClients / PaneTargetForSession -----------

        // The client a person is most likely sitting at. PlaceInTmux used to take
        // whichever line `list-clients` printed first, which is the same "any
        // client" mistake round eleven found one file away — and with two clients
        // attached it decided which window a pane got split into.
        [Fact]
        public void TheMostRecentlyActiveClientIsTheOneTheUserIsAt()
        {
            var chosen = TerminalScripts.MostRecentClient(new[]
            {
                Client("/dev/ttys002", "0", activity: "1787874871"),
                Client("/dev/ttys009", "1", activity: "1787875071"),
            });

            Assert.Equal("/dev/ttys009", chosen!.Value.Tty);
        }

        [Fact]
        public void AClientWithNoTtyIsNeverTheOne()
        {
            // Later activity, and unusable: nothing downstream can aim at a client
            // with no tty.
            var chosen = TerminalScripts.MostRecentClient(new[]
            {
                Client("/dev/ttys002", "0", activity: "1000"),
                Client("", "1", activity: "9999"),
            });

            Assert.Equal("/dev/ttys002", chosen!.Value.Tty);
            Assert.Null(TerminalScripts.MostRecentClient(Array.Empty<TerminalScripts.TmuxClient>()));
        }

        // One format and one parse, shared by both listings — a field added to one
        // used to be read out of position by the other.
        [Fact]
        public void AClientListingIsReadBackFieldForField()
        {
            var clients = TerminalScripts.ParseClients(
                "/dev/ttys002\t0\t1787875071\t0\n/dev/ttys009\t1\t1787874871\t1\n");

            Assert.Equal(2, clients.Count);
            Assert.Equal("/dev/ttys002", clients[0].Tty);
            Assert.Equal("0", clients[0].Session);
            Assert.Equal("1787875071", clients[0].Activity);
            Assert.False(clients[0].ControlMode);
            Assert.True(clients[1].ControlMode);
        }

        [Fact]
        public void AShortOrEmptyClientRowIsSkippedRatherThanGuessed()
        {
            var clients = TerminalScripts.ParseClients("\n/dev/ttys002\n/dev/ttys009\t1\n");

            // One field is not a client row; two is the minimum that names a
            // session, and the optional tail defaults rather than throwing.
            Assert.Single(clients);
            Assert.Equal("", clients[0].Activity);
            Assert.False(clients[0].ControlMode);
        }

        // The measured one. `display-message -t <session>` takes a target *pane*,
        // so a bare name is read as a window index in the current session and
        // answers about the wrong session without erroring — on a server whose
        // sessions are tmux's default "0" and "1", asking about "1" answered
        // "0:1". The trailing colon makes it a pane target and "=" makes the match
        // exact, which is what the relay's tmux naming built and explained for
        // the same two reasons (deleted with it in 937de9ec).
        [Fact]
        public void ASessionIsNamedAsAPaneTargetNotABareName()
        {
            Assert.Equal("=1:", TerminalScripts.PaneTargetForSession("1"));
            Assert.Equal("=claude-swarm:", TerminalScripts.PaneTargetForSession("claude-swarm"));
        }

        // --- PlacementFor -----------------------------------------------------

        // Round 6a in three cases. "It's taking me to a different tmux window -
        // not this one": every previous answer moved the user, and the complaint
        // was never about the destination.
        [Fact]
        public void AResolvedActiveWindowMeansSplitBesideTheUser()
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.BesideTheUser,
                TerminalScripts.PlacementFor("user", "user:3"));
        }

        // Attached somewhere, but the second lookup failed. A new window in their
        // own session is still inside the thing they use to move around, which is
        // what this app did before 6a — a consolation prize, not a wrong answer.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AClientWithNoResolvableWindowGetsAWindowOfItsOwn(string? activeWindow)
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ItsOwnTmuxWindow,
                TerminalScripts.PlacementFor("user", activeWindow));
        }

        // No client attached anywhere: a window created in a detached server is
        // the same nowhere the orb already pointed at, so neither tmux answer
        // applies and a terminal of its own is the only one left. Asserted for a
        // resolved window too, which cannot really happen — the window lookup is
        // asked of a session a client is on — because the rule must not depend on
        // that staying true.
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData(null, "user:3")]
        public void NothingAttachedMeansATerminalOfItsOwn(string? session, string? activeWindow)
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ATerminalWindow,
                TerminalScripts.PlacementFor(session, activeWindow));
        }

        // --- NewChatPlacementFor: a new chat gets a window of its own ---------
        //
        // Reported the first time the fixed iTerm2 launch let New chat reach
        // tmux: it split a pane into the window the user was working in. A new
        // chat is new work, not an existing conversation brought beside them,
        // so it gets a window of its own in their session.

        [Fact]
        public void ANewChatWithAClientGetsItsOwnTmuxWindow()
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ItsOwnTmuxWindow,
                TerminalScripts.NewChatPlacementFor("user"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ANewChatWithNoClientGetsATerminalWindow(string? session)
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ATerminalWindow,
                TerminalScripts.NewChatPlacementFor(session));
        }

        // The case the report was about: the active window *did* resolve, which
        // is exactly when the attach rule splits. New chat must not.
        [Theory]
        [InlineData("user:3")]
        [InlineData(null)]
        [InlineData("")]
        public void ANewChatNeverSplitsWhateverTheActiveWindow(string? activeWindow)
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ItsOwnTmuxWindow,
                TerminalScripts.NewChatPlacement("user", activeWindow));
        }

        // And the other caller keeps its split: every orb attach and
        // AgentTeamViewer launch goes through PlaceInTmux, which applies this
        // rule. Pinned so giving New chat its own placement cannot quietly move
        // the attach off round 6a's answer.
        [Theory]
        [InlineData("user", "user:3", "BesideTheUser")]
        [InlineData("user", null, "ItsOwnTmuxWindow")]
        [InlineData(null, null, "ATerminalWindow")]
        public void AnOrbAttachStillSplitsBesideTheUser(string? session, string? activeWindow, string want)
        {
            Assert.Equal(
                Enum.Parse<TerminalScripts.AttachPlacement>(want),
                TerminalScripts.OrbAttachPlacement(session, activeWindow));
        }

        // --- TmuxPlacementArgs: what each placement asks tmux to do ------------

        [Fact]
        public void BesideTheUserIsASplitOfTheActiveWindow()
        {
            var args = TerminalScripts.TmuxPlacementArgs(
                TerminalScripts.AttachPlacement.BesideTheUser, "user", "user:3", "/tmp/x", "cmd");

            Assert.Equal(TerminalScripts.TmuxSplitArgs(null, "user:3", "/tmp/x", "cmd"), args);
        }

        [Fact]
        public void ItsOwnTmuxWindowIsANewWindowInTheSession()
        {
            var args = TerminalScripts.TmuxPlacementArgs(
                TerminalScripts.AttachPlacement.ItsOwnTmuxWindow, "user", "user:3", "/tmp/x", "cmd");

            Assert.Equal(TerminalScripts.TmuxNewWindowArgs(null, "user", "/tmp/x", "cmd"), args);
        }

        [Fact]
        public void ATerminalWindowIsNotTmuxsToMake()
        {
            Assert.Null(TerminalScripts.TmuxPlacementArgs(
                TerminalScripts.AttachPlacement.ATerminalWindow, null, null, "/tmp/x", "cmd"));
        }

        // New chat end to end through the pure half: a resolved active window,
        // and still `new-window -t user:` — never `split-window`.
        [Fact]
        public void ANewChatAsksTmuxForANewWindowNotASplit()
        {
            var args = TerminalScripts.TmuxPlacementArgs(
                TerminalScripts.NewChatPlacement("user", "user:3"), "user", "user:3", "/tmp/x", "cmd")!;

            Assert.Equal("new-window", args[0]);
            Assert.Equal("user:", args[Array.IndexOf(args, "-t") + 1]);
            Assert.DoesNotContain("split-window", args);
        }

        // --- TmuxSplitArgs / TmuxNewWindowArgs --------------------------------

        // The command is the last element and arrives untouched behind the cd
        // guard. tmux hands that element to `sh -c`, so anything this builder did
        // to it would be a syntax error in a pane that just appeared — see the
        // `sh -n` cases in tests/IntegrationTests/TmuxAttachScriptTests.
        [Fact]
        public void TheSplitPutsTheCommandLastAndUnaltered()
        {
            const string command = "'/usr/bin/claude' attach '0e043819'";

            var args = TerminalScripts.TmuxSplitArgs(null, "user:3", "/tmp/x", command);

            Assert.Equal("cd -- '/tmp/x' || exit 1; " + command, AgentEnvironment.Unwrap(args[^1]));
        }

        // tmux format-expands -c, so a directory named with `#{…}` or `#(…)`
        // became another path, and one that did not exist started the pane in
        // $HOME with the pane id returned as if it had worked. Both builders
        // therefore guard the command with the shell's own cd, which reads the
        // name literally and exits rather than running somewhere else.
        [Theory]
        [InlineData("/Users/user/fmt#{session_name}x")]
        [InlineData("/Users/user/run#(touch PWNED)x")]
        [InlineData("/Users/user/it's #")]
        [InlineData("-leading-dash")]
        public void BothBuildersGuardTheCommandWithALiteralCd(string cwd)
        {
            var guard = "cd -- " + TerminalScripts.ShellQuote(cwd) + " || exit 1; exec 'x'";

            Assert.Equal(guard, AgentEnvironment.Unwrap(TerminalScripts.TmuxSplitArgs(null, "user:3", cwd, "exec 'x'")[^1]));
            Assert.Equal(guard, AgentEnvironment.Unwrap(TerminalScripts.TmuxNewWindowArgs(null, "user", cwd, "exec 'x'")[^1]));
        }

        // No cwd, no guard — `cd -- ''` would fail and take the command with it.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoCwdMeansTheBareCommand(string? cwd)
        {
            Assert.Equal("exec 'x'", AgentEnvironment.Unwrap(TerminalScripts.TmuxSplitArgs(null, "user:3", cwd, "exec 'x'")[^1]));
            Assert.Equal("exec 'x'", AgentEnvironment.Unwrap(TerminalScripts.TmuxNewWindowArgs(null, "user", cwd, "exec 'x'")[^1]));
        }

        // The new window hands back its pane id, which is what the caller treats
        // as "tmux took it" — the same -P -F contract the split has.
        [Fact]
        public void TheNewWindowAsksForItsPaneId()
        {
            var args = TerminalScripts.TmuxNewWindowArgs(null, "user", "/tmp/x", "cmd");

            Assert.Contains("-P", args);
            Assert.Equal("#{pane_id}", args[Array.IndexOf(args, "-F") + 1]);
        }

        [Fact]
        public void ACwdIsPassedThroughDashCToTheNewWindowToo()
        {
            var args = TerminalScripts.TmuxNewWindowArgs(null, "user", "/tmp/x", "cmd");

            Assert.Equal("/tmp/x", args[Array.IndexOf(args, "-c") + 1]);
        }

        // -h so the conversation lands beside their work rather than under it, and
        // -P -F '#{pane_id}' so the new pane's id comes back for the caller to
        // focus — the same contract new-window already had, and the reason neither
        // builder selects or raises anything itself.
        [Fact]
        public void TheSplitAsksForAHorizontalPaneAndItsId()
        {
            var args = TerminalScripts.TmuxSplitArgs(null, "user:3", "/tmp/x", "cmd");

            Assert.Equal("split-window", args[0]);
            Assert.Contains("-h", args);
            Assert.Contains("-P", args);
            Assert.Contains("#{pane_id}", args);

            // Targeted at the window, not the session: that is the whole
            // difference between landing beside the user and landing wherever
            // their session last had current.
            var target = Array.IndexOf(args, "-t");
            Assert.Equal("user:3", args[target + 1]);
        }

        // `-c ''` fails and would take the split with it, which is the same trap
        // TmuxAttachScript's `cd` guard exists for. Omitted, not passed empty.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoCwdMeansNoDashC(string? cwd)
        {
            Assert.DoesNotContain("-c", TerminalScripts.TmuxSplitArgs(null, "user:3", cwd, "cmd"));
            Assert.DoesNotContain("-c", TerminalScripts.TmuxNewWindowArgs(null, "user", cwd, "cmd"));
        }

        [Fact]
        public void ACwdIsPassedThroughDashC()
        {
            var args = TerminalScripts.TmuxSplitArgs(null, "user:3", "/tmp/x", "cmd");
            var at = Array.IndexOf(args, "-c");

            Assert.True(at > 0);
            Assert.Equal("/tmp/x", args[at + 1]);
        }

        // "<session>:" with the colon. Bare, tmux reads the target as a *window*
        // and refuses with "index N in use" the moment that index is taken; the
        // trailing colon names the session and lets it pick the next free index.
        // That was a real failure, which is why it is asserted rather than left to
        // a comment beside an argument list.
        [Fact]
        public void TheNewWindowTargetsTheSessionWithItsColon()
        {
            var args = TerminalScripts.TmuxNewWindowArgs(null, "user", "/tmp/x", "cmd");

            Assert.Equal("new-window", args[0]);

            var target = Array.IndexOf(args, "-t");
            Assert.Equal("user:", args[target + 1]);
        }

        // Both go through TmuxArgs, so both pin the socket when there is one and
        // pass nothing when there is not — the rule that keeps a swarm socket's
        // pane from being looked for on the default server.
        [Fact]
        public void BothBuildersPinTheSocketWhenGivenOne()
        {
            var split = TerminalScripts.TmuxSplitArgs(
                "/tmp/tmux-501/claude-swarm-1", "user:3", "/tmp/x", "cmd");

            Assert.Equal("-S", split[0]);
            Assert.Equal("/tmp/tmux-501/claude-swarm-1", split[1]);
            Assert.Equal("split-window", split[2]);

            var window = TerminalScripts.TmuxNewWindowArgs(
                "/tmp/tmux-501/claude-swarm-1", "user", "/tmp/x", "cmd");

            Assert.Equal("-S", window[0]);
            Assert.Equal("new-window", window[2]);
        }
    }
}

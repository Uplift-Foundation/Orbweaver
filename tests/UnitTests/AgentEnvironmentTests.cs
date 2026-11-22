using System.Diagnostics;
using Xunit;

namespace Orbweaver.UnitTests
{
    // CB-413: one case per variable class, and the controls that must survive.
    [Collection("Settings")]
    public class AgentEnvironmentTests
    {
        [Theory]
        [InlineData("TMUX")]
        [InlineData("TMUX_PANE")]
        [InlineData("CLAUDECODE")]
        [InlineData("CLAUDE_PID")]
        [InlineData("CLAUDE_JOB_DIR")]
        [InlineData("CLAUDE_EFFORT")]
        [InlineData("CLAUDE_CODE_ENTRYPOINT")]
        [InlineData("CLAUDE_CODE_EXECPATH")]
        [InlineData("CLAUDE_CODE_SESSION_ID")]
        [InlineData("CLAUDE_CODE_SESSION_KIND")]
        [InlineData("CLAUDE_CODE_MESSAGING_SOCKET")]
        [InlineData("CLAUDE_CODE_MESSAGING_TOKEN")]
        [InlineData("CLAUDE_CODE_BRIDGE_SESSION_ID")]
        [InlineData("CLAUDE_CODE_CHILD_SESSION")]
        [InlineData("CLAUDE_CODE_CHILD_NOTES_DIR")]
        [InlineData("CLAUDE_CODE_LAUNCHED_BY_SESSION_IDS")]
        [InlineData("CLAUDE_CODE_PARENT_SESSION_ID")]
        [InlineData("CLAUDE_CODE_USER_DELEGATED_SESSION_DIRECT_API")]
        public void IdentityVariablesAreDropped(string name) =>
            Assert.True(AgentEnvironment.IsAgentVariable(name));

        [Theory]
        [InlineData("CLAUDE_CONFIG_DIR")]
        [InlineData("PATH")]
        [InlineData("HOME")]
        [InlineData("ANTHROPIC_API_KEY")]
        [InlineData("CLAUDE_CODE_USE_BEDROCK")]
        [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
        [InlineData("TMUX_TMPDIR")]
        public void DeliberateConfigurationSurvives(string name) =>
            Assert.False(AgentEnvironment.IsAgentVariable(name));

        [Fact]
        public void RemovalsAreSortedDistinctAndOnlyTheIdentitySet()
        {
            var r = AgentEnvironment.Removals(["PATH", "TMUX", "CLAUDE_JOB_DIR", "TMUX", "CLAUDE_CONFIG_DIR"]);
            Assert.Equal(["CLAUDE_JOB_DIR", "TMUX"], r);
        }

        [Fact]
        public void PaneRemovalsKeepTheTmuxClientVariablesTmuxSetsItself() =>
            Assert.Equal(["CLAUDE_JOB_DIR"], AgentEnvironment.PaneRemovals(["TMUX", "TMUX_PANE", "CLAUDE_JOB_DIR"]));

        [Fact]
        public void WrapPaneCommandIsAnEnvPrefixOverShC()
        {
            Assert.Equal("env -u A -u B sh -c 'cd /w; x'", AgentEnvironment.WrapPaneCommand("cd /w; x", ["A", "B"]));
            Assert.Equal("plain", AgentEnvironment.WrapPaneCommand("plain", []));
        }

        [Theory]
        [InlineData("exec 'x'")]
        [InlineData("cd -- '/it'\\''s' || exit 1; claude")]
        public void UnwrapInvertsTheWrap(string line)
        {
            Assert.Equal(line, AgentEnvironment.Unwrap(AgentEnvironment.WrapPaneCommand(line, ["A"])));
            Assert.Equal(line, AgentEnvironment.Unwrap(line));
        }

        [Fact]
        public void TheFixedWrapNamesTheKnownSessionVariablesAndNeverTmux()
        {
            var wrapped = AgentEnvironment.WrapPaneCommand("claude");
            Assert.Contains("-u CLAUDE_JOB_DIR", wrapped);
            Assert.Contains("-u CLAUDE_CODE_MESSAGING_TOKEN", wrapped);
            Assert.DoesNotContain("-u TMUX", wrapped);
        }

        [Fact]
        public void ScrubbedStartInfoDropsIdentityAndKeepsConfigDir()
        {
            var psi = new ProcessStartInfo("x");
            psi.Environment["CLAUDE_JOB_DIR"] = "p";
            psi.Environment["TMUX"] = "p";
            psi.Environment["CLAUDE_CONFIG_DIR"] = "/acct";
            AgentEnvironment.Scrubbed(psi);
            Assert.False(psi.Environment.ContainsKey("CLAUDE_JOB_DIR"));
            Assert.False(psi.Environment.ContainsKey("TMUX"));
            Assert.Equal("/acct", psi.Environment["CLAUDE_CONFIG_DIR"]);
        }

        [Fact]
        public void ScrubCurrentProcessClearsItsOwnCopiesAndReportsThem()
        {
            var saved = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
            var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "poison");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "/acct");
                var names = AgentEnvironment.ScrubCurrentProcess();
                Assert.Contains("CLAUDE_CODE_SESSION_ID", names);
                Assert.Null(Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID"));
                Assert.Equal("/acct", Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", saved);
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", cfg);
            }
        }

        [Fact]
        public void UnsetArgsArePairsIncludingTmuxAndTheConfigDirIsNotAdded()
        {
            var a = AgentEnvironment.UnsetArgs();
            Assert.Equal(0, a.Length % 2);
            Assert.Contains("TMUX", a);
            Assert.Contains("CLAUDE_JOB_DIR", a);
        }

        [Fact]
        public void EveryTmuxLaunchBuilderWrapsItsCommand()
        {
            var cmd = "claude";
            foreach (var args in new[]
            {
                TerminalScripts.TmuxDetachedArgs(false, "/w", cmd),
                TerminalScripts.TmuxDetachedArgs(true, "/w", cmd),
                TerminalScripts.TmuxSplitArgs(null, "s:1", "/w", cmd),
                TerminalScripts.TmuxNewWindowArgs(null, "s", "/w", cmd),
            })
                Assert.Contains("-u CLAUDE_JOB_DIR", args[^1]);
        }
    }
}

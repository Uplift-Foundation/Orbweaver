using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397: LaunchRunner's reading of the pane it just started, and its answer to
/// a Claude Code folder-trust prompt. The pane is a fake, so nothing here
/// starts or reads a real terminal.
/// </summary>
public class LaunchRunnerTrustTests
{
    private const string Folder = "/private/tmp/cb327-scratch/proj";

    private static string Trust(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrustFixtures", name + ".txt"));

    private static string Screens(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures", name + ".txt"));

    private static readonly string OnNo = Trust("tmux-claude-trustdialog");
    private static readonly string OnYes = OnNo.Replace("❯ No, exit\n   Yes, I trust this folder", "  No, exit\n ❯ Yes, I trust this folder");
    private static readonly string Idle = Screens("conhost-claude-none");

    private static LaunchRunnerTests.Desk Pane(string screen) =>
        new() { TrustOn = true, Screen = _ => screen };

    private static LaunchFields F(string cli = "claude") =>
        new(Folder, cli, "", "", "", "", "n", 0, "");

    [Fact]
    public async Task Ask_hands_an_answerable_prompt_back_after_two_polls_with_the_pane_and_the_roster()
    {
        var d = Pane(OnNo);
        d.Sessions = [("old", LaunchRunnerTests.Status(cwd: Folder))];

        var outcome = await d.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Ask);

        Assert.Equal(PeerLaunchCodes.TrustNeeded, outcome.Code);
        Assert.Equal("%1", outcome.Held!.Pane);
        Assert.Equal(["old"], outcome.Held.Before);
        Assert.Equal(2, d.Delays.Count);
        Assert.Equal(1, d.Reads);
        Assert.Empty(d.Presses);
        Assert.Empty(d.Closed);
    }

    [Fact]
    public async Task A_session_that_checks_in_wins_before_the_pane_is_ever_read()
    {
        var d = Pane(OnNo);
        d.ChecksIn("new", LaunchRunnerTests.Status(cwd: Folder));

        var outcome = await d.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Ask);

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, "new"), outcome);
        Assert.Equal(0, d.Reads);
    }

    [Fact]
    public async Task Report_says_so_and_leaves_the_pane_for_the_desk()
    {
        var d = Pane(OnNo);
        var outcome = await d.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Report);
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, "Claude Code is asking whether to trust that folder. Answer it on the desk.", null), outcome);
        Assert.Empty(d.Closed);
    }

    [Fact]
    public async Task Close_tells_an_older_desktop_to_update_and_closes_the_pane_it_started()
    {
        var d = Pane(OnNo);
        var outcome = await d.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Close);
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, "Claude Code is asking whether to trust that folder. Update this machine to answer from here.", null), outcome);
        Assert.Equal(["%1"], d.Closed);
    }

    [Theory]
    [InlineData("grok", "tmux-grok-trustdialog")]
    [InlineData("codex", "wt-codex-trustdialog")]
    public async Task Other_clis_prompts_are_reported_never_answered_and_the_pane_is_closed(string cli, string fixture)
    {
        var d = Pane(Screens(fixture));
        foreach (var policy in new[] { LaunchRunner.TrustPolicy.Ask, LaunchRunner.TrustPolicy.Close })
        {
            d.Closed.Clear();
            var outcome = await d.Runner().RunAsync(F(cli), policy);
            Assert.Equal(RelayReplies.LaunchFailed, outcome.Code);
            Assert.StartsWith(NewChatLauncher.DisplayName(LaunchGate.CliFor(cli)), outcome.Why);
            Assert.EndsWith("Answer it once on that machine.", outcome.Why);
            Assert.Equal(["%1"], d.Closed);
            Assert.Empty(d.Presses);
        }
    }

    // Somebody answered it at that desk between the read and the close: the pane is theirs.
    [Fact]
    public async Task A_pane_that_no_longer_asks_is_never_closed()
    {
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = n => n == 0 ? OnNo : Idle };
        await d.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Close);
        Assert.Empty(d.Closed);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Windows_and_a_desk_with_no_trust_seams_never_read_and_fall_to_no_session(bool windows, bool trustOn)
    {
        var d = new LaunchRunnerTests.Desk { Windows = windows, Binary = windows ? "C:\\c\\claude.exe" : "/bin/claude", TrustOn = trustOn, Screen = _ => OnNo };
        var f = windows ? F() with { Cwd = "C:\\w" } : F();
        var outcome = await d.Runner().RunAsync(f, LaunchRunner.TrustPolicy.Ask);
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.NoSession, null, null), outcome);
        Assert.Equal(0, d.Reads);
        Assert.Equal(RelayRemote.LaunchCheckIn, d.Now - DateTimeOffset.FromUnixTimeSeconds(1794111792));
    }

    [Fact]
    public async Task Policy_none_and_an_unreadable_or_unrelated_pane_carry_on_to_no_session()
    {
        var none = Pane(OnNo);
        Assert.Equal(RelayReplies.NoSession, (await none.Runner().RunAsync(F())).Code);
        Assert.Equal(0, none.Reads);

        var blank = new LaunchRunnerTests.Desk { TrustOn = true, Screen = _ => null };
        Assert.Equal(RelayReplies.NoSession, (await blank.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Ask)).Code);
        Assert.True(blank.Reads > 0);
    }

    // ---- the answer ---------------------------------------------------------------------

    private static LaunchRunner.HeldTrust Held(params string[] before) => new("%1", before.ToHashSet());

    [Fact]
    public async Task Trust_presses_down_then_enter_only_after_seeing_the_marker_on_yes_and_waits_for_the_check_in()
    {
        var d = new LaunchRunnerTests.Desk { TrustOn = true };
        var screen = OnNo;
        d.Screen = _ => screen;
        d.OnPress = key => { if (key == "Down") screen = OnYes; };
        var polls = 0;
        d.OnDelay = () => { if (++polls == 1) d.Sessions = [("new", LaunchRunnerTests.Status(cwd: Folder))]; };

        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, "new"), outcome);
        Assert.Equal(["Down", "Enter"], d.Presses);
        Assert.Empty(d.Closed);
    }

    [Fact]
    public async Task A_marker_already_on_yes_gets_enter_alone()
    {
        var d = Pane(OnYes);
        d.ChecksIn("new", LaunchRunnerTests.Status(cwd: Folder));
        Assert.Equal("new", (await d.Runner().AnswerTrustAsync(F(), Held())).SessionId);
        Assert.Equal(["Enter"], d.Presses);
    }

    [Fact]
    public async Task A_marker_that_never_reaches_yes_means_no_enter_ever_and_the_pane_is_closed()
    {
        var d = Pane(OnNo);
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, "Couldn't select Yes on that machine.", null), outcome);
        Assert.Equal(["Down"], d.Presses);
        Assert.Equal(["%1"], d.Closed);
        Assert.Equal(20, d.Delays.Count(x => x == TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task A_prompt_that_is_gone_gets_no_key_at_all()
    {
        var d = Pane(Idle);
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, "The trust question was no longer on screen.", null), outcome);
        Assert.Empty(d.Presses);
        Assert.Empty(d.Closed);
    }

    // The screen changed between the look and the press: the approved check refuses it.
    [Fact]
    public async Task An_enter_whose_approval_fails_is_not_pressed()
    {
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = n => n == 0 ? OnYes : Idle };
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, "The trust question changed before it could be answered.", null), outcome);
        Assert.Empty(d.Presses);
    }

    [Fact]
    public async Task No_check_in_after_enter_is_no_session_and_the_pane_is_left_alone()
    {
        var d = Pane(OnYes);
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.NoSession, null, null), outcome);
        Assert.Empty(d.Closed);
    }

    // A session already in that folder before the start is still not the new one.
    [Fact]
    public async Task The_roster_from_before_the_start_still_excludes_old_sessions()
    {
        var d = Pane(OnYes);
        d.Sessions = [("old", LaunchRunnerTests.Status(cwd: Folder))];
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held("old"));
        Assert.Equal(RelayReplies.NoSession, outcome.Code);
    }

    [Fact]
    public void Kill_pane_args_name_only_the_pane()
    {
        Assert.Equal(["kill-pane", "-t", "%12"], TerminalScripts.TmuxKillPaneArgs("%12"));
    }
}

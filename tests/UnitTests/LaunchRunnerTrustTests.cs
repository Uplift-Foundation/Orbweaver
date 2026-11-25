using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397: LaunchRunner's reading of the pane it just started, and its answer to
/// a Claude Code folder-trust prompt. The pane is a fake, so nothing here
/// starts or reads a real terminal.
/// </summary>
public class LaunchRunnerTrustTests
{
    private const string Folder = TrustScreens.Folder;

    private static string Screens(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures", name + ".txt"));

    private static readonly string OnNo = TrustScreens.OnNo;
    private static readonly string OnYes = TrustScreens.OnYes;
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
    public async Task Other_clis_prompts_are_reported_never_answered_and_the_pane_this_launch_started_is_closed(string cli, string fixture)
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

    [Fact]
    public async Task A_desk_with_no_trust_seams_never_reads_and_falls_to_no_session()
    {
        var d = new LaunchRunnerTests.Desk { TrustOn = false, Screen = _ => OnNo };
        var outcome = await d.Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Ask);
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.NoSession, null, null), outcome);
        Assert.Equal(0, d.Reads);
        Assert.Equal(RelayRemote.LaunchCheckIn, d.Now - DateTimeOffset.FromUnixTimeSeconds(1794111792));
    }

    // ---- Windows (CB-476) -------------------------------------------------------------

    private static LaunchRunnerTests.Desk Console(Func<int, string?> screen) =>
        new() { Windows = true, Binary = "C:\\c\\claude.exe", TrustOn = true, Screen = screen };

    private static LaunchFields Home() => F() with { Cwd = TrustScreens.WindowsHome, Account = ".claude-b" };

    [Fact]
    public async Task Windows_reads_the_console_it_started_and_hands_the_question_back()
    {
        var d = Console(_ => TrustScreens.ConsoleOnNo);

        var outcome = await d.Runner().RunAsync(Home(), LaunchRunner.TrustPolicy.Ask);

        Assert.Equal(PeerLaunchCodes.TrustNeeded, outcome.Code);
        Assert.Equal("con:42", outcome.Held!.Pane);
        Assert.True(outcome.Held.Hooks);
        Assert.Equal(1, d.Reads);
        Assert.Empty(d.Presses);
    }

    [Fact]
    public async Task Windows_answers_down_then_enter_on_the_console_and_waits_for_the_check_in()
    {
        var d = Console(n => n == 0 || n == 1 ? TrustScreens.ConsoleOnNo : TrustScreens.ConsoleOnYes);
        d.OnPress = key => { if (key == "Enter") d.ChecksIn("new", LaunchRunnerTests.Status(cwd: TrustScreens.WindowsHome)); };

        var outcome = await d.Runner().AnswerTrustAsync(Home(), new LaunchRunner.HeldTrust("con:42", new HashSet<string>(), true));

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, "new"), outcome);
        Assert.Equal(["Down", "Enter"], d.Presses);
        Assert.Empty(d.Closed);
    }

    [Fact]
    public async Task Windows_with_no_console_pid_never_reads()
    {
        var d = Console(_ => TrustScreens.ConsoleOnNo);
        d.WindowsPid = null;
        var outcome = await d.Runner().RunAsync(Home(), LaunchRunner.TrustPolicy.Ask);
        Assert.Equal(RelayReplies.LaunchFailed, outcome.Code);
        Assert.Equal(0, d.Reads);
    }

    [Fact]
    public async Task Windows_reports_to_the_phone_and_closes_for_a_desktop_that_cannot_ask()
    {
        var phone = Console(_ => TrustScreens.ConsoleOnNo);
        var reported = await phone.Runner().RunAsync(Home(), LaunchRunner.TrustPolicy.Report);
        Assert.Equal(RelayReplies.LaunchFailed, reported.Code);
        Assert.Contains("Answer it on the desk", reported.Why);
        Assert.Empty(phone.Closed);

        var old = Console(_ => TrustScreens.ConsoleOnNo);
        var closed = await old.Runner().RunAsync(Home(), LaunchRunner.TrustPolicy.Close);
        Assert.Equal(RelayReplies.LaunchFailed, closed.Code);
        Assert.Equal(["con:42"], old.Closed);
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

    // ---- the narrower guard, hooks, and the symlinked folder ---------------------------

    // The same pane id now shows somebody else's trust prompt: not ours to kill.
    [Fact]
    public async Task A_trust_prompt_for_another_folder_is_never_killed()
    {
        var d = Pane(OnNo.Replace(Folder, "/private/tmp/cb397-w/somebody-elses"));
        await d.Runner().CloseIfStillAskingAsync(Folder, NewChatCli.ClaudeCode, "%1");
        Assert.Empty(d.Closed);

        var ours = Pane(OnNo);
        await ours.Runner().CloseIfStillAskingAsync(Folder, NewChatCli.ClaudeCode, "%1");
        Assert.Equal(["%1"], ours.Closed);

        var none = new LaunchRunnerTests.Desk();
        await none.Runner().CloseIfStillAskingAsync(Folder, NewChatCli.ClaudeCode, "%1");
        Assert.Empty(none.Closed);
    }

    [Fact]
    public async Task The_hooks_warning_travels_with_the_held_question()
    {
        var plain = await Pane(OnNo).Runner().RunAsync(F(), LaunchRunner.TrustPolicy.Ask);
        Assert.False(plain.Held!.Hooks);

        var d = Pane(TrustScreens.HooksOnNo);
        var hooks = await d.Runner().RunAsync(F() with { Cwd = TrustScreens.HooksFolder }, LaunchRunner.TrustPolicy.Ask);
        Assert.True(hooks.Held!.Hooks);
    }

    private const string Asked = "/tmp/cb397-lnk";
    private const string Physical = "/private/tmp/cb397-lnk";

    private static LaunchRunnerTests.Desk Linked(string status, Func<string, string>? resolve = null)
    {
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = _ => TrustScreens.Read("tmpfolder-w100-no"), Resolve = resolve };
        d.ChecksIn("new", LaunchRunnerTests.Status(cwd: status));
        return d;
    }

    [Theory]
    [InlineData(Asked)]
    [InlineData(Physical)]
    public async Task A_session_reporting_either_spelling_of_a_linked_folder_checks_in(string reported)
    {
        var d = Linked(reported, p => p == Asked ? Physical : p);
        var outcome = await d.Runner().RunAsync(F() with { Cwd = Asked });
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, "new"), outcome);
        // What is started is the folder as asked, never the resolved one.
        Assert.Equal(Asked, d.MacStarts.Single().Cwd);
    }

    [Fact]
    public async Task Without_a_resolver_or_with_one_that_throws_only_the_asked_spelling_checks_in()
    {
        foreach (Func<string, string>? resolve in new Func<string, string>?[] { null, p => p, p => throw new IOException() })
        {
            var d = Linked(Physical, resolve);
            Assert.Equal(RelayReplies.NoSession, (await d.Runner().RunAsync(F() with { Cwd = Asked })).Code);

            var same = Linked(Asked, resolve);
            Assert.Equal(RelayReplies.Launched, (await same.Runner().RunAsync(F() with { Cwd = Asked })).Code);
        }
    }

    [Fact]
    public async Task A_prompt_that_prints_the_resolved_folder_is_answerable_and_answered()
    {
        var onNo = TrustScreens.Read("tmpfolder-w100-no");
        var screen = onNo;
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = _ => screen, Resolve = p => p == Asked ? Physical : p };
        d.OnPress = key => { if (key == "Down") screen = onNo.Replace("❯ No, exit\n   Yes, I trust this folder", "  No, exit\n ❯ Yes, I trust this folder"); };

        var asked = await d.Runner().RunAsync(F() with { Cwd = Asked }, LaunchRunner.TrustPolicy.Ask);
        Assert.Equal(PeerLaunchCodes.TrustNeeded, asked.Code);

        d.ChecksIn("new", LaunchRunnerTests.Status(cwd: Physical));
        var answered = await d.Runner().AnswerTrustAsync(F() with { Cwd = Asked }, asked.Held!);
        Assert.Equal("new", answered.SessionId);
        Assert.Equal(["Down", "Enter"], d.Presses);
    }

    // The guard is the reading that led there: an Unanswerable pane is closed only on the
    // path that read it as Unanswerable, and a held question never kills it.
    [Fact]
    public async Task The_guard_kills_only_the_reading_that_led_to_it()
    {
        var other = OnNo.Replace(Folder, "/private/tmp/cb397-w/somebody-elses");
        var d = Pane(other);
        await d.Runner().CloseIfStillAskingAsync(Folder, NewChatCli.ClaudeCode, "%1");
        Assert.Empty(d.Closed);
        await d.Runner().CloseIfStillAskingAsync(Folder, NewChatCli.ClaudeCode, "%1", TrustPrompt.Kind.Unanswerable);
        Assert.Equal(["%1"], d.Closed);

        var ours = Pane(OnNo);
        await ours.Runner().CloseIfStillAskingAsync(Folder, NewChatCli.ClaudeCode, "%1", TrustPrompt.Kind.Unanswerable);
        Assert.Empty(ours.Closed);
    }

    // ---- the answer's failure exits close a pane still on the prompt --------------------

    [Fact]
    public async Task A_read_that_merely_failed_is_closed_when_the_prompt_is_still_there()
    {
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = n => n == 0 ? null : OnNo };
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());
        Assert.Equal("The trust question was no longer on screen.", outcome.Why);
        Assert.Equal(["%1"], d.Closed);
        Assert.Empty(d.Presses);
    }

    [Fact]
    public async Task An_enter_that_was_not_approved_is_closed_when_the_prompt_is_still_there()
    {
        // The look before Enter shows the marker back on No, so Enter is refused; the pane still asks.
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = n => n == 0 ? OnYes : OnNo };
        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());
        Assert.Equal("The trust question changed before it could be answered.", outcome.Why);
        Assert.Empty(d.Presses);
        Assert.Equal(["%1"], d.Closed);
    }

    // Nothing pins the routing otherwise: a pane-only status that did not route to tmux would
    // make every real Trust end "Couldn't select Yes" while the faked suites stayed green.
    [Fact]
    public void A_status_carrying_only_a_pane_routes_to_tmux()
    {
        var status = LaunchRunner.PaneStatus("%12");
        Assert.Equal("%12", status.TmuxPane);
        Assert.Equal(TerminalTyping.Route.Tmux, TerminalTyping.RouteFor(status, onMacOS: true, onWindows: false, new TerminalTyping.Tools(true, false, false)));
        Assert.Equal(TerminalTyping.Route.Tmux, TerminalTyping.RouteFor(status, onMacOS: false, onWindows: false, new TerminalTyping.Tools(true, false, false)));
    }

    // ---- the approvals each key press is made under ------------------------------------

    private static readonly string OtherFolder = "/private/tmp/cb397-w/somebody-elses";
    private static readonly string OtherOnNo = OnNo.Replace(Folder, OtherFolder);
    private static readonly string OtherOnYes = OnYes.Replace(Folder, OtherFolder);

    // Runs an answer far enough to hand over the approval a key is pressed under: a pane showing
    // the marker on No is asked for Down, one showing it on Yes goes straight to Enter.
    private static async Task<Func<string?, bool>> ApprovalFor(string key)
    {
        Func<string?, bool>? approval = null;
        var shown = key == "Down" ? OnNo : OnYes;
        var d = new LaunchRunnerTests.Desk
        {
            TrustOverride = new LaunchRunner.TrustSeams(
                _ => Task.FromResult<string?>(shown),
                (_, k, approved) => { if (k == key) approval = approved; return Task.FromResult((false, (string?)null)); },
                _ => Task.CompletedTask)
        };
        await d.Runner().AnswerTrustAsync(F(), Held());
        return approval!;
    }

    [Fact]
    public async Task Down_is_approved_only_for_a_marker_on_no_for_this_folder()
    {
        var down = await ApprovalFor("Down");
        Assert.True(down(OnNo));
        Assert.False(down(OnYes));
        Assert.False(down(OtherOnNo));
        Assert.False(down(Idle));
        Assert.False(down(null));
    }

    [Fact]
    public async Task Enter_is_approved_only_for_a_marker_on_yes_for_this_folder()
    {
        var enter = await ApprovalFor("Enter");
        Assert.True(enter(OnYes));
        Assert.False(enter(OnNo));
        Assert.False(enter(OtherOnYes));
        Assert.False(enter(Idle));
        Assert.False(enter(null));
    }

    // Between the Down and the marker reaching Yes, a read that is nothing or another folder's
    // prompt never counts as Yes: no Enter, and a pane that is not ours is not killed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_that_is_not_our_prompt_never_counts_as_yes(bool otherFolderPrompt)
    {
        var after = otherFolderPrompt ? OtherOnYes : null;
        var d = new LaunchRunnerTests.Desk { TrustOn = true, Screen = n => n <= 1 ? OnNo : after };

        var outcome = await d.Runner().AnswerTrustAsync(F(), Held());

        Assert.Equal("Couldn't select Yes on that machine.", outcome.Why);
        Assert.Equal(["Down"], d.Presses);
        Assert.Empty(d.Closed);
    }
}

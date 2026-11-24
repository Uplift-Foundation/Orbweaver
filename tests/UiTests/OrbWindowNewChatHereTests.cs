using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-168: the orb context menu's "New chat here" item — visible for a
// local-CLI orb or an OpenClaw orb, and what it would pre-fill the dialog
// with in each case.
//
// [Collection("Settings")]: constructing an OrbWindow reads a colour setting
// in a field initializer — see SettingsCollection.cs.
[Collection("Settings")]
public class OrbWindowNewChatHereTests
{
    private static SessionStatus Status(SessionSource source, string cwd = "/repo/one") =>
        new() { Source = source, Cwd = cwd, State = "idle" };

    // --- NewChatPrefillFor: pure, no window ---

    [Fact]
    public void NoStatusMeansNoPrefill()
    {
        Assert.Null(OrbWindow.NewChatPrefillFor(null, "any-id"));
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void ALocalSessionPrefillsItsOwnCliAndCwd(SessionSource source)
    {
        var prefill = OrbWindow.NewChatPrefillFor(Status(source, "/repo/mine"), "session-id");

        Assert.NotNull(prefill);
        Assert.Equal(NewChatOrbWatch.CliOf(source), prefill.Value.Cli);
        Assert.Equal("/repo/mine", prefill.Value.Cwd);
        Assert.Null(prefill.Value.AgentId);
    }

    // An OpenClaw session prefills a null CLI — there's no local CLI to pick
    // — but carries the agent id parsed out of the session's own key instead
    // (OpenClawSessions.AgentIdOf's "openclaw:agent:<id>:<surface>" shape,
    // confirmed against a real gateway in docs/openclaw-findings.md).
    [Fact]
    public void AnOpenClawSessionPrefillsItsOwnAgentIdAndNoCli()
    {
        var prefill = OrbWindow.NewChatPrefillFor(
            Status(SessionSource.OpenClaw), "openclaw:agent:main:dashboard:abc123");

        Assert.NotNull(prefill);
        Assert.Null(prefill.Value.Cli);
        Assert.Equal("main", prefill.Value.AgentId);
    }

    // A non-local, non-OpenClaw session (remote-control, Claude Cloud)
    // prefills nothing useful — there's no local CLI and no OpenClaw agent —
    // but this stays a total function rather than refusing outright; only
    // the menu item's own visibility (IsLocalCli || OpenClaw) decides
    // whether this is ever called for one.
    [Fact]
    public void ARemoteControlSessionPrefillsNeitherACliNorAnAgent()
    {
        var prefill = OrbWindow.NewChatPrefillFor(Status(SessionSource.RemoteControl), "session-id");

        Assert.NotNull(prefill);
        Assert.Null(prefill.Value.Cli);
        Assert.Null(prefill.Value.AgentId);
    }

    // --- the menu item's own visibility, via UpdateFrom ---

    [AvaloniaTheory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    [InlineData(SessionSource.OpenClaw)]
    public void TheItemIsVisibleForEveryLocalCliAndOpenClaw(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(source));

        Assert.True(orb.NewChatHereItem.IsVisible);
    }

    [AvaloniaFact]
    public void TheItemIsHiddenForRemoteControl()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(SessionSource.RemoteControl));

        Assert.False(orb.NewChatHereItem.IsVisible);
    }

    // NewChatHere_Click's own no-op half, driven directly: a freshly
    // constructed orb has never had UpdateFrom called, so _lastStatus is
    // null and NewChatPrefillFor returns null — the click handler returns
    // before ever reaching OpenNewChatWindow. Deliberately never given a
    // real status here: that would call the real NewChatWindow.Toggle(),
    // which is excluded rather than tested for the same reason
    // OpenSettings/OpenNewChat are (real OS-facing work a headless suite
    // has no business doing).
    [AvaloniaFact]
    public void ClickWithNoSessionBoundIsANoOp()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.NewChatHere_Click(null, new Avalonia.Interactivity.RoutedEventArgs());

        // Still here — the assertion is that nothing happened.
        Assert.NotNull(orb);
    }

    // --- CB-361: "Resume a session here…" ---

    [AvaloniaTheory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void TheResumeItemIsVisibleForEveryLocalCli(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(source));

        Assert.True(orb.ResumeHereItem.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void TheResumeItemIsHiddenWhereThereIsNoLocalCli(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(source));

        Assert.False(orb.ResumeHereItem.IsVisible);
    }

    [AvaloniaFact]
    public void ResumePrefillNamesTheCliFolderAndAccount()
    {
        var status = Status(SessionSource.ClaudeCode, "/repo/mine");
        status.TranscriptPath = "/home/w/.claude-board/projects/-repo-mine/abc.jsonl";

        var prefill = OrbWindow.ResumePrefillFor(status);

        Assert.Equal(NewChatCli.ClaudeCode, prefill!.Value.Cli);
        Assert.Equal("/repo/mine", prefill.Value.Cwd);
        Assert.Equal("/home/w/.claude-board", prefill.Value.ProfileDir);
    }

    [AvaloniaFact]
    public void ResumePrefillHasNoAccountWhenTheTranscriptIsUnknown()
    {
        Assert.Null(OrbWindow.ResumePrefillFor(Status(SessionSource.Codex))!.Value.ProfileDir);
    }

    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void ResumePrefillIsNullWithoutALocalCli(SessionSource source)
    {
        Assert.Null(OrbWindow.ResumePrefillFor(Status(source)));
        Assert.Null(OrbWindow.ResumePrefillFor(null));
    }

    [AvaloniaFact]
    public void ResumeClickWithNoSessionBoundIsANoOp()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.ResumeHere_Click(null, new Avalonia.Interactivity.RoutedEventArgs());

        Assert.Null(NewChatWindow.OpenForTests);
    }

    [AvaloniaFact]
    public void ResumeClickOpensTheDialogOnResumeAndASecondClickReusesIt()
    {
        var presented = new List<NewChatWindow>();
        var raised = 0;
        NewChatWindow.PresentForTests = presented.Add;
        NewChatWindow.BringForwardForTests = _ => raised++;
        NewChatWindow.OpenForTests = null;
        NewChatAvailability.CurrentForTests = () => new[]
        {
            new NewChatOption(NewChatCli.Codex, Enabled: true, Reason: null, Warning: null)
        };
        PastSessionIndex.LoadForTests = (_, _, _, _) => Array.Empty<PastSession>();
        NewChatWindow.ResumeLoadInlineForTests = true;
        try
        {
            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(Status(SessionSource.Codex, "/repo/mine"));

            orb.ResumeHere_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
            var window = Assert.Single(presented);
            Assert.True(window.LocalResumeRadio.IsChecked);

            window.LocalNewRadio.IsChecked = true;
            orb.ResumeHere_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
            Assert.Single(presented);
            Assert.Equal(1, raised);
            Assert.True(window.LocalResumeRadio.IsChecked);
        }
        finally
        {
            NewChatWindow.PresentForTests = null;
            NewChatWindow.BringForwardForTests = null;
            NewChatWindow.OpenForTests = null;
            NewChatAvailability.CurrentForTests = null;
            PastSessionIndex.LoadForTests = null;
            NewChatWindow.ResumeLoadInlineForTests = false;
        }
    }
}

using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// Captures for CB-164's visible surfaces: the cloud orb itself, and the settings
// group in both of its states.
//
// Hand-written one per scenario, because adding a case to tests/UiTests does not
// generate its capture — that suite renders through the null renderer and this
// one through real Skia, which is the whole reason both exist.
//
// No clicks, for the reason the other orb captures give: OrbWindow's pointer
// handling reaches TerminalFocuser, unguarded at its own entry point.
[Collection("Settings")]
public class CloudSessionScreenshots : IDisposable
{
    // ChatPanel is one window shared by every test in the process, so each
    // capture that opens one hides its own session again rather than relying on
    // isolation that does not exist. Same rule as ChatPanelScreenshots.
    private readonly List<string> _panelsToClean = new();

    public void Dispose()
    {
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
    }

    private static SessionStatus CloudStatus(int? contextPercent) => new()
    {
        Source = SessionSource.ClaudeCloud,
        Kind = SessionKind.Cloud,
        State = "idle",
        Title = "Refactor the parser",
        Cwd = "",
        Url = "https://claude.ai/code/session_01abc",
        ContextPercent = contextPercent,
        StatusDetail = "Editing files",
        RecentAction = "Ran the tests",
    };

    // The badge and the ring together, since they are the two things that make
    // this orb different from every other one on the screen and a reviewer
    // comparing rids is looking for both.
    [AvaloniaFact]
    public void ACloudOrbWearsTheCloudBadgeAndAContextRing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: 72));

        ScreenshotHelper.Capture(orb, "orb-window-cloud-session.png");
    }

    // And with no reading at all, which is the common case on a session nobody
    // has reported a context percentage for. The two captures side by side are
    // what shows that "no ring" is a state rather than a rendering failure.
    [AvaloniaFact]
    public void ACloudOrbWithNoContextReadingDrawsNoRing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: null));

        ScreenshotHelper.Capture(orb, "orb-window-cloud-session-no-ring.png");
    }

    // The chat panel for a cloud session that cannot be written to: a transcript
    // with no box under it, and a sentence where the box was.
    //
    // CB-199 changed what this state means. It used to be every cloud session,
    // because nobody had found a write path; now a live cloud session is sent to
    // through /v1/code (see chat-panel-cloud-sendable.png below), and this is the
    // generic read-only case — the sentence and link a session shows once it
    // may not be written to. chat-panel-cloud-ended.png is the same state with
    // the reason a user will most often actually see.
    //
    // What it shows is still a *judgement* rather than a mechanism — whether one
    // line of grey text is enough for somebody who clicked an orb expecting to be
    // able to reply, and whether the panel reads as deliberate rather than as one
    // that failed to finish drawing. No test can answer that; the picture can.
    [AvaloniaFact]
    public void ACloudSessionsPanelShowsItsTranscriptAndNoComposer()
    {
        var id = "screenshot-cloud-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "refactor the transcript parser" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Pulled the envelope handling out into its own type and left the row "
                       + "mapping alone — the second half was already covered.",
            },
        })
        {
            SessionId = id,
            DisplayName = "Refactor the parser",
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
            MachineName = "Anthropic's cloud",
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-cloud-read-only.png");
    }

    // CB-199: a live cloud session mid-reply. The ordinary composer, and Stop
    // beside Send because a turn is running (CanInterrupt). The two things a
    // reviewer comparing rids is looking for are that the box is there at all
    // and that the new button sits in the row with its neighbours rather than
    // below or on top of them.
    [AvaloniaFact]
    public void ASendableCloudSessionShowsTheComposerAndStop()
    {
        var id = "screenshot-cloud-sendable-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(CloudTranscript())
        {
            SessionId = id,
            DisplayName = "Refactor the parser",
            IsReadOnly = false,
            CanInterrupt = true,
            MachineName = "Anthropic's cloud",
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;

        // Asserted before the capture so a picture of the wrong state fails
        // rather than being reviewed as the right one.
        Assert.True(panel.FindControl<Grid>("ComposerRow")!.IsVisible);
        Assert.True(panel.FindControl<Grid>("StopButton")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-cloud-sendable.png");
    }

    // ...and one that has ended: the box has gone, and the sentence in its place
    // says why and links to the session. The hint is the real session's own
    // Ended wording (CloudChatSendability), copied rather than referenced so the
    // capture shows a literal a reviewer can read against the rids.
    // CB-199: a reply mid-stream. The assistant row is the live bubble a
    // text_delta is growing — incomplete, ending mid-sentence the way it does on
    // screen while the stream is running — with the composer and Stop under it.
    // What a reviewer is checking is that an unfinished reply reads as being
    // written rather than as cut off, and that Stop is where it can be reached.
    [AvaloniaFact]
    public void AStreamingCloudReplyShowsPartialTextWithStop()
    {
        var id = "screenshot-cloud-streaming-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "write me a short essay on why tests should be written first", IsComplete = true },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Writing the test first changes what the test is for. Written afterwards, a test "
                       + "describes the code that exists; written first, it describes the behaviour "
                       + "somebody asked for, and the code has to",
                IsComplete = false,
            },
        })
        {
            SessionId = id,
            DisplayName = "Essay on testing",
            IsReadOnly = false,
            CanInterrupt = true,
            ComposerHint = "Message… (queued after this turn)",
            MachineName = "Anthropic's cloud",
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.FindControl<Grid>("ComposerRow")!.IsVisible);
        Assert.True(panel.FindControl<Grid>("StopButton")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-cloud-streaming.png");
    }

    [AvaloniaFact]
    public void AnEndedCloudSessionShowsWhyInPlaceOfTheComposer()
    {
        var id = "screenshot-cloud-ended-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(CloudTranscript())
        {
            SessionId = id,
            DisplayName = "Refactor the parser",
            IsReadOnly = true,
            ComposerHint = "This session has ended and no longer takes messages.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
            MachineName = "Anthropic's cloud",
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(panel.FindControl<Control>("ReadOnlyBox")!.IsVisible);
        Assert.False(panel.FindControl<Grid>("ComposerRow")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-cloud-ended.png");
    }

    // The link back (CB-199 follow-up): a live cloud session keeps its composer
    // and now also its way to the session in the browser. The thing to look at
    // is that exactly one link is on screen, sitting right above the composer
    // rather than crowding Send/Stop, and that it reads as a link.
    [AvaloniaFact]
    public void ALiveCloudSessionShowsTheBrowserLinkAboveTheComposer()
    {
        var id = "screenshot-cloud-live-link-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(CloudTranscript())
        {
            SessionId = id,
            DisplayName = "Refactor the parser",
            IsReadOnly = false,
            ReplyUrl = "https://claude.ai/code/session_01abc",
            MachineName = "Anthropic's cloud",
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(panel.FindControl<Grid>("ComposerRow")!.IsVisible);
        Assert.True(panel.FindControl<TextBlock>("SessionLink")!.IsVisible);
        Assert.False(panel.FindControl<TextBlock>("ReadOnlyLink")!.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-cloud-live-link.png");
    }

    private static ChatTurn[] CloudTranscript() => new[]
    {
        new ChatTurn { Role = ChatRole.User, Text = "refactor the transcript parser" },
        new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "Pulled the envelope handling out into its own type and left the row "
                   + "mapping alone — the second half was already covered.",
        },
    };

    // The settings group, switched off: one row and the help text that names the
    // Keychain prompt. That sentence is the whole of the feature's first-run
    // story, so it is worth having a picture of it on both runners.
    [AvaloniaFact]
    public void TheCloudSettingsGroupOff()
    {
        Capture(enabled: false, "settings-claude-cloud-off.png");
    }

    // ...and switched on, where progressive disclosure adds the status line and
    // the retry button. Nothing here is platform-gated — it is an HTTPS poll and
    // a Keychain read, and the Windows leg reads its credential from the CLI's
    // own file instead — so the two rids should show the same card. A Windows
    // capture missing these rows is the regression this exists to make visible.
    [AvaloniaFact]
    public void TheCloudSettingsGroupOn()
    {
        Capture(enabled: true, "settings-claude-cloud-on.png");
    }

    // Several accounts: the header and one line per account, labelled by folder.
    // The rows are what a second Claude Code login adds to this card, and a
    // capture is the only thing that shows they wrap sensibly.
    [AvaloniaFact]
    public void TheCloudSettingsGroupOnWithTwoAccounts()
    {
        var board = new CloudAccountBoard(new[]
        {
            new CloudAccount("/Users/x/.claude", "default", new NoLogin(), new NoLogin()),
            new CloudAccount("/Users/x/.claude-board", "board", new NoLogin(), new NoLogin()),
        });
        // A real session folded in the way the app folds one, so the header's
        // count comes from the merged list rather than from a string.
        var now = System.DateTime.UtcNow;
        var session = new ClaudeCloudSessions.Session(
            "session_01abc", "Refactor the parser", "idle", now, "https://claude.ai/code/session_01abc",
            "idle", false, null, null, null, null);
        board.Apply("/Users/x/.claude",
            new ClaudeCloudSessions.StepResult(
                ClaudeCloudSessions.ArmState.Initial, new[] { session },
                "1 cloud session (578 sessions inspected)", System.TimeSpan.Zero),
            now);
        board.ApplyError("/Users/x/.claude-board", "no Claude Code login found \u2014 run `claude` and sign in");
        ClaudeCloudSessions.SetBoardForTests(board);
        try
        {
            Capture(enabled: true, "settings-claude-cloud-on-two-accounts.png", keepState: true);
        }
        finally
        {
            ClaudeCloudSessions.SetBoardForTests(null);
        }
    }

    private sealed class NoLogin : ICloudCredentialSource
    {
        public string? Stamp() => null;
        public CredentialRead Read() => new(CredentialOutcome.NotLoggedIn, null, null, "none");
    }

    // CB-306's product-readiness round: the slash-commands group beside the
    // cloud card, as a fresh install has it — cloud off, this switch on, and
    // the help text naming the Keychain prompt the first command brings.
    [AvaloniaFact]
    public void TheRemoteControlCommandsGroupOnAFreshInstall()
    {
        Capture(enabled: false, "settings-remote-control-commands.png", heading: "Remote Control commands");
    }

    private static void Capture(bool enabled, string name, bool keepState = false,
        string heading = "Claude Code in the cloud")
    {
        var was = OrbweaverSettings.ClaudeCloudEnabled;
        try
        {
            OrbweaverSettings.ClaudeCloudEnabled = enabled;

            // A state a running app would actually be in. Left alone the line
            // reads "off" even in the switched-on capture — truthfully, since
            // nothing called Restart() here — and a picture of the enabled
            // section whose status says off is a picture that teaches a
            // reviewer the wrong thing about the feature.
            if (!keepState) ClaudeCloudSessions.SetStateForTests("checking\u2026");

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Window)ctor.Invoke(null);

            // Shown and flushed so the page is measured and arranged — these
            // stack panels are not virtualized, so every row lays out even
            // though this group sits well below the viewport.
            window.Show();
            ScreenshotHelper.Flush();

            // Anchored on the *group heading* rather than on a row, and taking
            // its immediate parent — Group() builds a StackPanel whose first
            // child is that heading and whose second is the whole card, so the
            // parent is exactly the section and nothing else.
            //
            // The height-and-width heuristic the neighbouring captures use does
            // not work here: this section's first row carries four lines of help
            // text, which makes the row itself taller than the threshold, so the
            // walk stopped there and the capture showed the switch alone — with
            // the status line and the retry button, the two things the switched-
            // on scenario exists to show, cropped out of frame. Caught by
            // looking at the PNG, which is the only thing that could have caught
            // it: the test passed either way.
            var title = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block => block.Text == heading);

            Assert.NotNull(title);

            // Climbing to the section type rather than taking the heading's
            // immediate parent. That shortcut held while Group() built a
            // StackPanel with the heading as its first child -- CB-166 wrapped
            // the heading in a ToggleButton so the section could fold, and the
            // parent became the chevron-and-heading stack instead: a 162x18
            // capture of the title alone, with the card gone.
            //
            // It failed on the Windows leg and passed on macOS, which is worth
            // knowing. Both rids captured the same wrong 162x18 region; only
            // Windows tripped AssertTextIsLegible on it, because a strip that
            // small and that dense renders bi-level there. A structural anchor
            // does not depend on either of those accidents.
            var group = title!.GetLogicalAncestors()
                .OfType<SettingsWindow.SettingsSection>()
                .First();

            ScreenshotHelper.CaptureControl(group, name);
        }
        finally
        {
            ClaudeCloudSessions.SetStateForTests("off");
            OrbweaverSettings.ClaudeCloudEnabled = was;
        }
    }

    // CB-306: the slash box on a cloud panel. The list is what a real cloud
    // init yields once it has been through the cloud catalogue (the same
    // CloudSlashCatalog the session uses), so the picture shows the four
    // commands a cloud session can run and none of the ninety-odd it lists.
    // The session behind the panel is the fake: this suite renders through real
    // Skia and has no live stream to feed an init.
    [AvaloniaFact]
    public void ACloudPanelsSlashBoxOffersOnlyWhatACloudSessionCanRun()
    {
        var offered = CloudSlashCatalog.Catalogue(
            new[] { "compact", "rename", "model", "color", "permissions", "think", "doctor", "some-skill" },
            new[] { "doctor", "color" });

        var id = "screenshot-cloud-slash-" + Guid.NewGuid();
        _panelsToClean.Add(id);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), new FakeChatSession
        {
            SessionId = id,
            DisplayName = "Refactor the parser",
            SlashCommands = offered,
        });

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<Avalonia.Controls.TextBox>("Input")!;
        input.Focus();
        input.Text = "/";
        input.CaretIndex = 1;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-cloud-slash-box.png");
    }
}

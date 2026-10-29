using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// One capture per scenario in tests/UiTests/ChatPanelTests.cs. Same
// singleton-cleanup rule as that suite: ChatPanel is one window shared by
// every test in the process, so each test here calls HideFor its own
// session id when done rather than relying on process isolation.
public class ChatPanelScreenshots : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    // displayName defaults to the placeholder every capture before this one
    // used, so none of them changes. It is worth overriding wherever the
    // *picture* names the conversation somewhere else: a header reading "Fake
    // Session" above a note about "#lobby" is two different answers to "what am
    // I looking at" inside the one artifact reviewers actually open, and the
    // fake's name costs nothing to set.
    private FakeChatSession NewFake(
        IEnumerable<ChatTurn>? history = null, string displayName = "Fake Session",
        bool isRoom = false)
    {
        var id = "screenshot-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = displayName, IsRoom = isRoom };
    }

    // Deliberately never closed — same reason as tests/UiTests's ChatPanelTests:
    // closing a headless Window here corrupts a process-wide FontManager
    // cache for every window built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // An orb whose session the gateway's heartbeat drives. The panel reads the
    // flag off the orb rather than the session (see ChatPanel.Bind), so the
    // status has to go through UpdateFrom to reach the chip.
    private static OrbWindow NewHeartbeatOrb()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/test/project",
            Title = "",
            Color = "",
            Cli = "",
            Kind = SessionKind.Channel,
            Heartbeat = true,
        });

        return orb;
    }

    // CB-173. The header chip used to type the orb's badge character, so on
    // Windows it showed the same colour emoji the orb did. One capture of the
    // whole panel for context, and one of the chip alone per drawn kind — at
    // its real size the chip is a few dozen pixels wide, which is too small to
    // judge inside a full-panel image.
    [AvaloniaFact]
    public void ACronChatsHeaderChipDrawsTheStopwatch()
    {
        ChatPanel.OpenFor(KindOrb(SessionKind.Cron), NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "Nightly digest sent." },
        }));
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-cron-kind-chip.png");
    }

    [AvaloniaTheory]
    [InlineData(SessionKind.Cron, "cron")]
    [InlineData(SessionKind.Background, "background")]
    [InlineData(SessionKind.Remote, "remote")]
    [InlineData(SessionKind.Cloud, "cloud")]
    [InlineData(SessionKind.Channel, "channel")]
    public void TheHeaderChipAlone(SessionKind kind, string name)
    {
        ChatPanel.OpenFor(KindOrb(kind), NewFake());
        ScreenshotHelper.Flush();

        var chip = ChatPanelTestAccess.Instance!.FindControl<Border>("KindChip")!;
        ScreenshotHelper.CaptureControl(chip, $"chat-panel-kind-chip-{name}.png");
    }

    // Every kind's chip on one sheet, for the same one-image comparison the
    // orb badges get. The chip is re-rendered per kind rather than collected,
    // because there is one panel and so one chip: each is captured into its
    // own bitmap before the next kind rebinds it.
    [AvaloniaFact]
    public void EveryKindsChipStacked()
    {
        var kinds = new[]
        {
            SessionKind.Cron, SessionKind.Direct, SessionKind.Channel,
            SessionKind.Remote, SessionKind.Background, SessionKind.Cloud,
        };

        var shots = new List<RenderTargetBitmap>();

        try
        {
            foreach (var kind in kinds)
            {
                ChatPanel.OpenFor(KindOrb(kind), NewFake());
                ScreenshotHelper.Flush();

                var chip = ChatPanelTestAccess.Instance!.FindControl<Border>("KindChip")!;
                var shot = new RenderTargetBitmap(new PixelSize(
                    (int)Math.Ceiling(chip.Bounds.Width), (int)Math.Ceiling(chip.Bounds.Height)));
                shots.Add(shot);
                ScreenshotHelper.Render(chip, shot);
                ScreenshotHelper.Check(chip, shot, "chat-panel-all-kind-chips.png");
            }

            const int Margin = 6;
            var width = shots.Max(s => s.PixelSize.Width) + Margin * 2;
            var height = shots.Sum(s => s.PixelSize.Height + Margin) + Margin;

            using var target = new RenderTargetBitmap(new PixelSize(width, height));
            using (var ctx = target.CreateDrawingContext())
            {
                // The panel's own header ground, so the chip's translucent fill
                // reads as it does in place rather than against transparency.
                ctx.FillRectangle(
                    new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x30)),
                    new Rect(0, 0, width, height));

                var y = Margin;
                foreach (var shot in shots)
                {
                    ctx.DrawImage(shot, new Rect(Margin, y, shot.PixelSize.Width, shot.PixelSize.Height));
                    y += shot.PixelSize.Height + Margin;
                }
            }

            target.Save(Path.Combine(ScreenshotHelper.OutputDir, "chat-panel-all-kind-chips.png"));
        }
        finally
        {
            foreach (var shot in shots) shot.Dispose();
        }
    }

    // CB-173. The panel's speak button while speech plays, which is the one
    // look of it whose glyph changed — the stop square is drawn now.
    [AvaloniaFact]
    public void ThePanelsSpeakButtonWhileSpeakingShowsTheStopSquare()
    {
        ChatPanel.OpenFor(KindOrb(SessionKind.Channel), NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "Reading this aloud." },
        }));

        try
        {
            ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Speaking);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "chat-panel-speaking-stop-square.png");
        }
        finally
        {
            ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        }
    }

    // CB-298. The header's mute button in both looks, so the crossed-out
    // speaker and the red fill are reviewed as pixels, not as a property.
    [AvaloniaTheory]
    [InlineData(true, "chat-panel-header-muted.png")]
    [InlineData(false, "chat-panel-header-unmuted.png")]
    public void ThePanelsMuteButton(bool muted, string file)
    {
        ChatPanel.OpenFor(KindOrb(SessionKind.Channel), NewFake());

        try
        {
            AudioMute.Set(muted);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, file);
        }
        finally
        {
            AudioMute.Set(false);
        }
    }

    private static OrbWindow KindOrb(SessionKind kind)
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/test/project",
            Title = "",
            Color = "",
            Cli = "",
            Kind = kind,
        });

        return orb;
    }

    [AvaloniaFact]
    public void AHeartbeatChatWearsABeatingHeartChipInItsHeader()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "No response requested." },
        });

        ChatPanel.OpenFor(NewHeartbeatOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-heartbeat-chip.png");
    }

    // The panel for a session there is nowhere to type into: the box says what it
    // is waiting for, and there is a gear beside it that opens the roster where
    // it can be answered. Captured
    // because it is a new visible surface, and because the thing worth reviewing
    // is a judgement — whether the box's wording and one small button are enough
    // for someone who clicked a grey orb expecting to be able to talk to it.
    [AvaloniaFact]
    public void AParkedSessionsPanelSaysSoAndOffersTheView()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "merge the open PRs" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Done — three merged, one had conflicts." },
        });

        fake.ComposerHint = "Needs input — attach to reply";
        fake.CanOpenElsewhere = true;

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-parked-attach.png");
    }

    // A session Claude Desktop hosts (CB-265): the transcript reads like any
    // other, and where the composer would be there is a sentence saying how to
    // send from here (CB-266: turn on Remote Control for the chat). A real
    // LocalCliChatSession rather than a fake, so the picture is what the
    // production class decides to say — and no attach button, since `claude
    // attach` cannot find a Desktop conversation.
    [AvaloniaFact]
    public void ADesktopHostedSessionIsReadOnlyAndSaysWhereToType()
    {
        WithDesktopSession(bridge: "", api: null, cloudOn: false, "chat-panel-desktop-readonly.png", _ => { });
    }

    // CB-266: the same chat once it is on Remote Control and "Show cloud
    // sessions" is on — the composer is back, its watermark says what a send
    // arrives as, and the browser link sits above it.
    [AvaloniaFact]
    public void ABridgedDesktopSessionOffersTheComposerWithTheTeammateWatermark()
    {
        WithDesktopSession(Bridge, new StubApi(200), cloudOn: true, "chat-panel-desktop-bridged.png", _ => { });
    }

    // CB-266: a send the API refused (409, the chat's bridge has ended). The
    // text stays unsent, no bubble is drawn, and the composer gives way to the
    // reason plus the link.
    [AvaloniaFact]
    public void ARefusedBridgeSendLeavesTheReasonAndTheLink()
    {
        WithDesktopSession(Bridge, new StubApi(409), cloudOn: true, "chat-panel-desktop-bridge-refused.png", session =>
        {
            var input = ChatPanelTestAccess.Instance!.FindControl<TextBox>("Input")!;
            input.Text = "rebase the third PR";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

            var readOnly = ChatPanelTestAccess.Instance!.FindControl<Control>("ReadOnlyBox")!;
            for (var i = 0; i < 600 && !readOnly.IsVisible; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Assert.True(session.IsReadOnly);
            Assert.Equal(LocalCliChatSession.DesktopBridgeGoneHint, session.ComposerHint);
        });
    }

    private const string Bridge = "session_01ScreenshotBridge123";

    private sealed class StubApi : ICloudApi
    {
        private readonly int _status;

        internal StubApi(int status) => _status = status;

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token) =>
            Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(_status, "{}"), null));
    }

    private sealed class StubCredentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp";

        public CredentialRead Read() =>
            new(CredentialOutcome.Found, "sk-ant-oat01-SCREENSHOT-FAKE", null, "a credential is present");
    }

    private void WithDesktopSession(
        string bridge, ICloudApi? api, bool cloudOn, string png, Action<LocalCliChatSession> act)
    {
        var replyBefore = OrbweaverSettings.ClaudeCodeReplyEnabled;
        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
        var dir = Path.Combine(Path.GetTempPath(), "cb-shot-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var transcript = Path.Combine(dir, "t.jsonl");
            File.WriteAllText(transcript,
                "{\"type\":\"user\",\"uuid\":\"u1\",\"timestamp\":\"2026-10-14T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"summarise the open PRs\"}}\n"
                + "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-10-14T12:00:01Z\","
                + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\","
                + "\"text\":\"Three are ready to merge; one is waiting on review.\"}]}}\n");

            var id = "screenshot-" + Guid.NewGuid();
            _sessionIdsToClean.Add(id);
            var session = new LocalCliChatSession(id, new SessionStatus
            {
                Source = SessionSource.ClaudeCode,
                Cli = "claude",
                Entrypoint = SessionStatus.DesktopEntrypoint,
                TranscriptPath = transcript,
                SessionPid = Environment.ProcessId,
                State = "idle",
                BridgeSessionId = bridge,
            },
            cloudApi: api,
            credentialsFor: _ => new StubCredentials(),
            cloudEnabled: () => cloudOn)
            {
                Delay = (_, _) => Task.CompletedTask,
            };
            session.Start();
            for (var i = 0; i < 400 && session.History.Count == 0; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            ChatPanel.OpenFor(NewOrb(), session);
            ScreenshotHelper.Flush();
            act(session);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, png);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = replyBefore;
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // CloseFor rather than HideFor since CB-110. On a transient panel the two
    // are the same call, which is every capture above; on a pinned one HideFor
    // deliberately does nothing at all, so a capture that pinned something
    // would leave a live window in the registry for whichever class runs next
    // to trip over.
    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.CloseFor(id);
    }

    [AvaloniaFact]
    public void OpenForRendersOneRowPerHistoryTurnWithMatchingText()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "hi there" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "hello back" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "room message",
                Speaker = "Nova",
                SpeakerColor = "#00AF5F"
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-three-turns.png");
    }

    [AvaloniaFact]
    public void MarkdownTurnRendersAsStyledRunsNotLiteralMarkup()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "**bold** and `code`" },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-markdown-turn.png");
    }

    [AvaloniaFact]
    public void TurnWithSpeakerShowsTheSpeakersNameOnItsRow()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "room message",
                Speaker = "Nova",
                SpeakerColor = "#00AF5F"
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-speaker-turn.png");
    }

    [AvaloniaFact]
    public void TypingAndPressingEnterSendsTheTypedTextAndClearsTheBox()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Focus();
        ScreenshotHelper.Flush();
        input.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = "hello from a test"
        });
        ScreenshotHelper.Flush();

        // Captured before Enter, deliberately: this is the one moment the
        // source test's own assertions distinguish (typed-but-not-sent vs.
        // sent-and-cleared) that a screenshot can actually show — after
        // Enter, the box is empty and the panel looks identical to the
        // three-turns capture above plus one more row.
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-typed-input-before-enter.png");
    }

    // Bytes that are not a picture, which is the one thing this suite can test
    // that tests/UiTests cannot: Avalonia's headless render interface answers
    // DecodeToWidth with a stub of the requested size for any input at all, so
    // the panel's "not an image" path is unreachable under the null renderer
    // and reachable here, where Skia is real and throws.
    //
    // Worth having because a local CLI's transcript is a file this app does not
    // write, and a half-flushed image block is a normal thing to read out of
    // one. The message keeps its text; only the picture is missing.
    [AvaloniaFact]
    public void ATurnWhoseImageBytesDoNotDecodeStillShowsItsText()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "a screenshot",
                IsComplete = true,
                ImageBytes = new byte[] { 0x4E, 0x4F, 0x50, 0x45, 0x21, 0x21, 0x21, 0x21 }
            }
        });

        ChatPanel.OpenFor(NewOrb(), fake);

        // The decode runs on a worker and its failure is swallowed on the way
        // back, so the frames are what prove the panel survived it rather than
        // stopped drawing partway through.
        for (var i = 0; i < 40; i++) ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-undecodable-image.png");
    }

    // CB-93: a picture the gateway refused, with the reason shown rather than
    // a bare MEDIA: line and no explanation. One ordinary turn above it for
    // contrast, so the judgement worth reviewing is legible — does the note
    // read as an aside about this one message, or does it compete with the
    // conversation around it.
    [AvaloniaFact]
    public void APictureTheGatewayRefusedShowsWhy()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "can you drop the render in here?" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "here you go\n\nMEDIA:/Users/user/.openclaw/workspace-render-quill/outputs/render.png",
                ImageNote = "Picture not shown — the gateway won't serve files from that folder. "
                          + "Ask the agent to write it to ~/.openclaw/media/, which is allowed for every agent.",
                ImageNoteDetail = "/Users/user/.openclaw/workspace-render-quill/outputs/render.png"
                                + " — outside-allowed-folders",
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-picture-refused.png");
    }

    // CB-272: a video the agent posted, as a tile. The meta answer is the
    // shape the gateway really sends for a portrait clip, so the tile is the
    // real proportions (clamped at 320 tall) with its play glyph and m:ss
    // label. One ordinary turn above it for contrast; the judgement worth
    // reviewing is whether the tile reads as "a clip, press to play" beside
    // the caption, and whether the label stays legible over the dark fill.
    [AvaloniaFact]
    public void AVideoTheAgentPostedDrawsATile()
    {
        var realMeta = OpenClawVideo.FetchMeta;
        OpenClawVideo.FetchMeta = (_, _) => Task.FromResult<string?>(
            "{\"available\":true,\"mimeType\":\"video/mp4\",\"sizeBytes\":1042813,\"playback\":\"native\","
            + "\"durationMs\":12750,\"width\":544,\"height\":800,\"mediaTicket\":\"v1.sample\"}");

        try
        {
            var fake = NewFake(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "send me something nice" },
                VideoTurn(),
            });

            ChatPanel.OpenFor(NewOrb(), fake);
            ScreenshotHelper.Flush();

            // A portrait clip's tile is clamped to 320, which fills the
            // default 340-high panel on its own and scrolls the caption and
            // the user's turn out of the frame — the very thing the review
            // question is about ("does it read as a clip beside its
            // caption"). The panel is a process-wide singleton, so the extra
            // height is put back afterwards rather than left for the next
            // capture to inherit.
            var panel = ChatPanelTestAccess.Instance!;
            var realHeight = panel.Height;
            try
            {
                panel.Height = 620;
                ScreenshotHelper.Flush();
                ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-video-tile.png");
            }
            finally
            {
                panel.Height = realHeight;
            }
        }
        finally
        {
            OpenClawVideo.FetchMeta = realMeta;
        }
    }

    // CB-272: the same turn when the gateway will not serve the clip — the
    // note in the slot the tile would have taken, the caption still above it.
    [AvaloniaFact]
    public void AVideoTheGatewayRefusedShowsWhy()
    {
        var realMeta = OpenClawVideo.FetchMeta;
        OpenClawVideo.FetchMeta = (_, _) => Task.FromResult<string?>(
            "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}");

        try
        {
            var fake = NewFake(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "send me something nice" },
                VideoTurn(),
            });

            ChatPanel.OpenFor(NewOrb(), fake);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "chat-panel-video-refused.png");
        }
        finally
        {
            OpenClawVideo.FetchMeta = realMeta;
        }
    }

    // CB-274: a clip the gateway could not deliver (the five cron posts on
    // the requester's Discord session). The gateway moved the filename out of
    // the caption into an attachment_error block, and refuses to serve the
    // file from the agent's folder when asked — measured, "Not an image". The
    // shape reviewed here is the caption, then the two-sentence note in the
    // slot a tile would have taken, with the user's turn above for framing.
    [AvaloniaFact]
    public void AClipTheGatewayCouldNotDeliverSaysSo()
    {
        var realMeta = OpenClawVideo.FetchMeta;
        OpenClawVideo.FetchMeta = (_, _) => Task.FromResult<string?>(
            "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}");

        try
        {
            var failed = VideoTurn();
            failed.Text = "Pink fairy lights and a little message just for you 🌸";
            failed.ImageAlt = "sample_afternoon_video.mp4";
            failed.Attachment = new ChatAttachment(AttachmentKind.Video, null,
                "sample_afternoon_video.mp4", "video/mp4", null, null, null, null, "delivery-failed");

            var fake = NewFake(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "send me something nice" },
                failed,
            });

            ChatPanel.OpenFor(NewOrb(), fake);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "chat-panel-clip-not-delivered.png");
        }
        finally
        {
            OpenClawVideo.FetchMeta = realMeta;
        }
    }

    // CB-277: the same failed-delivery clip, but its route names the session
    // of the agent whose workspace holds the file, so the gateway serves it and
    // the panel draws a playable tile instead of the note above.
    [AvaloniaFact]
    public void AClipAnotherAgentMadeDrawsAPlayableTile()
    {
        var realMeta = OpenClawVideo.FetchMeta;
        OpenClawVideo.FetchMeta = (_, _) => Task.FromResult<string?>(
            "{\"available\":true,\"mimeType\":\"video/mp4\",\"sizeBytes\":1042813,\"playback\":\"native\","
            + "\"durationMs\":12750,\"width\":544,\"height\":800,\"mediaTicket\":\"v1.sample\"}");

        try
        {
            var clip = VideoTurn();
            clip.Text = "Pink fairy lights and a little message just for you 🌸";
            clip.ImageAlt = "sample_afternoon_video.mp4";
            clip.ImageSourcePath = "/Users/sample/.openclaw/workspace-sample-zara/outputs/sample_afternoon_video.mp4";
            clip.ImageUrl = new OpenClawMediaSource(
                clip.ImageSourcePath, "agent:comfyui:discord:direct:1").Route;
            clip.Attachment = new ChatAttachment(AttachmentKind.Video, null,
                "sample_afternoon_video.mp4", "video/mp4", null, null, null, null, "delivery-failed");

            var fake = NewFake(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "send me something nice" },
                clip,
            });

            ChatPanel.OpenFor(NewOrb(), fake);
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "chat-panel-clip-via-owner-session.png");
        }
        finally
        {
            OpenClawVideo.FetchMeta = realMeta;
        }
    }

    // CB-275: an audio note, a file, and a file the gateway could not deliver.
    // Block shapes are read off the gateway's source (no real rows exist), so
    // these captures are of the app's drawing, not of a live gateway.
    private void CaptureAttachments(string file, params ChatTurn[] turns)
    {
        var realMeta = OpenClawVideo.FetchMeta;
        OpenClawVideo.FetchMeta = (_, _) => Task.FromResult<string?>(
            "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}");

        try
        {
            ChatPanel.OpenFor(NewOrb(), NewFake(turns));
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, file);
        }
        finally
        {
            OpenClawVideo.FetchMeta = realMeta;
        }
    }

    private static ChatTurn AttachmentTurn(ChatAttachment a, string text) => new()
    {
        Role = ChatRole.Assistant, Text = text, IsComplete = true,
        ImageAlt = a.Label, Attachment = a, ImageUrl = a.Url,
    };

    [AvaloniaFact]
    public void AnAudioNoteTheAgentPostedDrawsAClickToOpenTile() =>
        CaptureAttachments("chat-panel-audio-tile.png",
            new ChatTurn { Role = ChatRole.User, Text = "say something nice" },
            AttachmentTurn(new ChatAttachment(AttachmentKind.Audio, "/api/chat/media/outgoing/k/a/full",
                "good_morning_voice_note.m4a", "audio/mp4", 240000, 42000, null, null, null),
                "A little voice note for you"));

    [AvaloniaFact]
    public void AFileTheAgentPostedDrawsAChipWithItsNameAndSize() =>
        CaptureAttachments("chat-panel-file-chip.png",
            new ChatTurn { Role = ChatRole.User, Text = "send me the numbers" },
            AttachmentTurn(new ChatAttachment(AttachmentKind.Document, "/api/chat/media/outgoing/k/b/full",
                "quarterly-report.pdf", "application/pdf", 1_258_291, null, null, null, null),
                "Here is the report"));

    [AvaloniaFact]
    public void AFileTheGatewayCouldNotDeliverDrawsTheChipWithANote()
    {
        const string path = "/Users/user/.openclaw/media/quarterly-report.pdf";

        CaptureAttachments("chat-panel-file-not-delivered.png",
            new ChatTurn { Role = ChatRole.User, Text = "send me the numbers" },
            new ChatTurn
            {
                Role = ChatRole.Assistant, Text = "Here is the report", IsComplete = true,
                ImageSourcePath = path, ImageAlt = "quarterly-report.pdf",
                ImageUrl = "/__openclaw__/assistant-media?source=report",
                Attachment = new ChatAttachment(AttachmentKind.Document, null, "quarterly-report.pdf",
                    "application/pdf", null, null, null, null, "unsupported-format"),
            });
    }

    private static ChatTurn VideoTurn()
    {
        const string path = "/Users/user/.openclaw/workspace-sample/outputs/sample_afternoon_video.mp4";

        return new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "Pink fairy lights and a little message just for you 🌸\nMEDIA:" + path,
            IsComplete = true,
            ImageSourcePath = path,
            ImageAlt = "sample_afternoon_video.mp4",
            ImageUrl = "/__openclaw__/assistant-media?source=sample",
        };
    }

    // A room with everyone in it drawn as themselves — the whole of CB-27 in one
    // picture, and the reason it is a capture rather than only an assertion.
    //
    // Four kinds of turn, and the judgement worth reviewing is whether they read
    // as four different people at a glance rather than as four grey bubbles:
    //
    //   * Yours, in your own blue on the right. Before this, a message you sent
    //     to a channel came back as an anonymous grey bubble on the left,
    //     because the copies in the members' transcripts are user-role like
    //     everybody else's.
    //   * An agent in the room, in its own colour, matched to the ring on its
    //     orb.
    //   * Somebody the gateway named but this app cannot match to an agent — a
    //     relayed bot, or another person in the channel. Named, and deliberately
    //     uncoloured: a Discord display name is not an agent id, and a borrowed
    //     colour would say two speakers were one. The initials chip is what that
    //     honesty looks like, and whether it reads as deliberate rather than as
    //     a missing colour is exactly the thing a screenshot settles and a test
    //     cannot.
    //   * The room's own anonymous voice, drawn when the gateway said nothing
    //     about who sent a message. This is the *degraded* rendering and it is
    //     deliberately still here: the whole attribution rule falls back to it
    //     rather than guessing. In the capture because it is the arm most likely
    //     to regress without anyone noticing — nothing else in any suite draws
    //     it, and a change that started attributing these would look like an
    //     improvement in every test and like the app asserting something false
    //     on screen.
    //
    //     It wears no chip and no name at all (CB-36). Before that fix this
    //     bubble wore the room's own initials — "#L" for "#lobby" — because an
    //     unattributed assistant turn fell back to the panel's sole-speaker
    //     name, and for a room that name is the title, since a room has no
    //     agent identity behind its session key. ChatSpeaker's own comment
    //     already conceded the title was "the wrong one for a room"; what it
    //     did not yet say was that this bubble was where that wrongness
    //     actually reached the screen. IRemoteChatRoom.IsRoom (set true on
    //     this scenario's fake below) is what tells TurnView the fallback does
    //     not apply here, so the bubble now draws exactly what
    //     OpenClawRoomChatSession.Rebuild's own comment always meant it to:
    //     left, neutral, no name — a bare bubble, not a chip asserting a
    //     speaker nobody named.
    //   * A failure note, which is what a send with nowhere to go now leaves
    //     behind instead of silence.
    [AvaloniaFact]
    public void ARoomDrawsEveryoneInItAsThemselves()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "anyone free to look at the build?",
                IsComplete = true,
                Mine = true
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Taking it now — the arm64 leg is the slow one.",
                IsComplete = true,
                Speaker = "Quill",
                SpeakerColor = "#00AF5F"
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Nodes are loaded, so it should be quick.",
                IsComplete = true,
                Speaker = "Thistle"
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Anyone know if the runner picked that up?",
                IsComplete = true
            },
            new ChatTurn
            {
                Role = ChatRole.System,
                Text = "Couldn't post to #lobby: no member of this channel carries "
                     + "a delivery address.",
                IsComplete = true
            },
        }, displayName: "#lobby", isRoom: true);

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-room-attribution.png");
    }

    // A passage held selected in a reply.
    //
    // Captured because the selection is the whole feature and the only part of
    // it a reviewer cannot check any other way: the tests can prove the right
    // characters end up on the clipboard, but not that the highlight can
    // actually be read. That is a judgement about one colour sitting on a
    // tinted bubble, and it is the reason SelectionFill is a translucent white
    // rather than the solid system highlight — which is exactly the kind of
    // choice a picture settles and a passing assertion does not.
    [AvaloniaFact]
    public void ASelectedPassageIsHighlightedInTheReply()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "where does the hook write its status files?",
                IsComplete = true,
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "They go to `$TMPDIR/claude-buddy`, one file per session.\n\n"
                     + "Drag across any of this to select it, then copy.",
                IsComplete = true,
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();

        // The selection a person would have made by dragging. Set directly
        // rather than synthesized, because a headless drag would be testing
        // Avalonia's hit-testing rather than this app's rendering — and it is
        // the rendering the capture exists to show.
        var panel = ChatPanelTestAccess.Instance!;
        var line = panel.FindControl<ItemsControl>("Turns")!
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(b => Text(b).StartsWith("They go to", StringComparison.Ordinal));

        line.SelectionStart = "They go to ".Length;
        line.SelectionEnd = "They go to $TMPDIR/claude-buddy".Length;

        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-selected-text.png");
    }

    // A styled line keeps its words in Inlines and leaves Text null.
    private static string Text(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text!;
        if (block.Inlines is null) return "";

        return string.Concat(
            block.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }
    // Where a long transcript sits when you open it, which every capture above
    // is silent about — they all hold a handful of turns and so never overflow
    // the panel at all.
    //
    // Worth a picture rather than only the assertions in tests/UiTests'
    // ChatPanelScrollTests, because the fault it covers was reported by eye and
    // is read back by eye: the numbers in an offset-versus-extent assertion say
    // the scroll viewer is at its end, and the image says the newest message is
    // the one you are looking at. It is also the one part of this fix whose
    // behaviour is worth confirming per platform — the two-priority yield it
    // depends on is dispatcher and layout timing, and the capture runs on both
    // runners.
    //
    // Staged the way the report describes rather than on a fresh panel: a first
    // session read part way up, then a second one opened over it. The panel is a
    // process-wide singleton, so the offset left behind by the first is exactly
    // what the second used to inherit.
    [AvaloniaFact]
    public void ALongTranscriptOpensOnItsNewestTurn()
    {
        var read = NewFake(LongTranscript("an earlier conversation"));
        ChatPanel.OpenFor(NewOrb(), read);
        ScreenshotHelper.Flush();

        // Part way up the first transcript, where reading back through it
        // leaves you.
        ChatPanelTestAccess.Instance!.Scroll.Offset = new Vector(0, 300);
        ScreenshotHelper.Flush();

        var opened = NewFake(LongTranscript("this conversation"), displayName: "Long Session");
        ChatPanel.OpenFor(NewOrb(), opened);

        // Several, because the scroll deliberately settles across two dispatcher
        // priorities with a measure between them, and a capture taken before it
        // has finished is a picture of the bug rather than of the fix.
        for (var i = 0; i < 8; i++) ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-opens-at-newest-turn.png");
    }

    // Numbered, and with the last turn saying so, because the whole point of the
    // image is which end of the transcript is on screen — and "some chat
    // bubbles" looks the same at either end.
    private static List<ChatTurn> LongTranscript(string what) =>
        Enumerable.Range(0, 60).Select(i => new ChatTurn
        {
            Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
            Text = i == 59
                ? $"Message 60 of {what} — the newest turn, and the one you should be looking at."
                : $"Message {i + 1} of {what}, somewhere above the fold.",
            IsComplete = true,
        }).ToList();

    // The panel after Cmd+ has been pressed a few times.
    //
    // The tests assert the numbers; this is the half a number cannot answer.
    // Whether an enlarged conversation is still a conversation — whether the
    // bubbles still read as two people talking, whether a heading is still a
    // heading beside its prose, whether a wrapped code block at 1.75x has
    // anything left of its line — is a judgement, and it is made by looking.
    // Both runners capture it, so a font that falls back differently on
    // Windows shows up here rather than on someone's machine.
    [AvaloniaFact]
    public void AnEnlargedPanelIsStillAReadableConversation()
    {
        var was = OrbweaverSettings.ChatTextScale;

        try
        {
            OrbweaverSettings.ChatTextScale = 1.75;

            var fake = NewFake(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "why is the build red?" },
                new ChatTurn
                {
                    Role = ChatRole.Assistant,
                    Text = "## One test\n\n`ArrangementSweep` fails at the widest spacing:\n\n"
                         + "```\nExpected: 1.15\nActual:   1.0\n```\n\n"
                         + "- the ladder is uneven\n- the tick was not",
                    IsComplete = true
                },
                new ChatTurn { Role = ChatRole.System, Text = "Session went idle." },
            }, displayName: "Build");

            ChatPanel.OpenFor(NewOrb(), fake);

            // The panel is a singleton and may already have been built by an
            // earlier capture, in which case its constructor's ApplyTextScale
            // ran against the old size. This is the same hook the settings
            // slider uses.
            ChatPanel.ReapplyTextScale();
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "chat-panel-text-enlarged.png");
        }
        finally
        {
            // Every other capture in this assembly draws at the shipped size,
            // and the suite shares one process.
            OrbweaverSettings.ChatTextScale = was;
            ChatPanel.ReapplyTextScale();
        }
    }

    // --- CB-110: a chat that has been told to stay ---

    // The pin itself. One panel, pinned, so a reviewer can see what the new
    // control looks like in the row it joined — three 24pt circles, the
    // newcomer first, filled with the same blue the speak button wears while
    // it is doing something. Whether that reads as "on" rather than as a
    // fourth kind of button is a judgement made from the image and not from
    // an assertion about a brush.
    [AvaloniaFact]
    public void APinnedPanelWearsAFilledPinInItsHeader()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "keep this one open while I read it" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Pinned. This panel stays put now — it will not hide when you switch apps.",
                IsComplete = true,
            },
        }, displayName: "Pinned Session");

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.TogglePin();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-pinned-header.png");
    }

    // The feature's actual subject: more than one chat on screen at once, and
    // the new one not landing on top of the ones already there.
    //
    // Every panel's position here is the one Reposition() computed — the real
    // seam, ChatPanelPlacement.Resolve, running inside Bind. What headless
    // cannot supply is *different* anchors: an OrbWindow that is never shown
    // answers PointToScreen with its own client origin whatever its Position
    // is set to, so all three orbs anchor at the same point and all three
    // panels ask for the same rectangle. That turns out to be the case worth
    // photographing rather than a limitation to work around — it is exactly
    // the collision Resolve exists for, and the picture is of Resolve sliding
    // the second and third panels clear of the first rather than of three
    // panels that were never going to overlap anyway.
    //
    // Composited rather than captured, because a screenshot is of one window
    // and the thing being reviewed is where three of them sit relative to each
    // other. Each panel is rendered on its own and then drawn onto one bitmap
    // at its real screen position, so the gaps and the abutments in the image
    // are the app's arithmetic and not this test's.
    [AvaloniaFact]
    public void ANewPanelOpensClearOfTheOnesAlreadyPinned()
    {
        var first = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "First pinned chat.", IsComplete = true },
        }, displayName: "Pinned One");

        ChatPanel.OpenFor(NewOrb(), first);
        ScreenshotHelper.Flush();
        var one = ChatPanelTestAccess.Instance!;
        one.TogglePin();
        ScreenshotHelper.Flush();

        var second = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "Second pinned chat.", IsComplete = true },
        }, displayName: "Pinned Two");

        ChatPanel.OpenFor(NewOrb(), second);
        ScreenshotHelper.Flush();
        var two = ChatPanelTestAccess.Instance!;
        two.TogglePin();
        ScreenshotHelper.Flush();

        // The third is left unpinned — the one transient panel, placed by
        // Resolve clear of both pinned rectangles. Its header is the one with
        // an unfilled pin, which is the other half of what the first capture
        // shows.
        var third = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "And the transient one.", IsComplete = true },
        }, displayName: "Transient");

        ChatPanel.OpenFor(NewOrb(), third);
        ScreenshotHelper.Flush();
        var three = ChatPanelTestAccess.Instance!;

        Composite(new[] { one, two, three }, "chat-panels-two-pinned.png");
    }

    // Draws several panels onto one bitmap at their own screen positions.
    //
    // The ground is the same near-black OrbClusterScreenshots plots its
    // arrangements on, and there is a margin, so the rounded corner of each
    // panel is visible against it — which is how a reader tells three abutting
    // cards from one wide one.
    private static void Composite(IReadOnlyList<ChatPanel> panels, string fileName)
    {
        const int Margin = 24;

        var minX = panels.Min(p => p.Position.X);
        var minY = panels.Min(p => p.Position.Y);
        var maxX = panels.Max(p => p.Position.X + (int)p.Width);
        var maxY = panels.Max(p => p.Position.Y + (int)p.Height);

        var size = new PixelSize(maxX - minX + Margin * 2, maxY - minY + Margin * 2);

        // Rendered up front and disposed after the drawing context has been
        // flushed: a RenderTargetBitmap handed to DrawImage is read while the
        // context is still open, so disposing each one inside the loop would
        // be a use-after-free at the point the target is saved.
        var shots = new List<RenderTargetBitmap>();

        try
        {
            using var target = new RenderTargetBitmap(size);

            using (var ctx = target.CreateDrawingContext())
            {
                ctx.FillRectangle(
                    new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1f)),
                    new Rect(0, 0, size.Width, size.Height));

                foreach (var panel in panels)
                {
                    var shot = new RenderTargetBitmap(
                        new PixelSize((int)panel.Width, (int)panel.Height));
                    shots.Add(shot);

                    // Through the shared path rather than shot.Render(panel):
                    // this is the only capture in the suite that builds its own
                    // bitmaps, and rendering them here directly would leave it
                    // the one picture still drawn with LCD text on Windows and
                    // the one never checked for legibility. See
                    // ScreenshotHelper.RenderChecked.
                    ScreenshotHelper.Render(panel, shot);

                    ctx.DrawImage(shot, new Rect(
                        panel.Position.X - minX + Margin,
                        panel.Position.Y - minY + Margin,
                        panel.Width,
                        panel.Height));
                }
            }

            target.Save(Path.Combine(ScreenshotHelper.OutputDir, fileName));

            // Checked per panel after the composite is on disk, for the same
            // reason ScreenshotHelper.Save checks after writing: the run that
            // catches a corrupt capture should still leave the picture of it.
            foreach (var (panel, shot) in panels.Zip(shots))
            {
                ScreenshotHelper.Check(panel, shot, fileName);
            }
        }
        finally
        {
            foreach (var shot in shots) shot.Dispose();
        }
    }
}

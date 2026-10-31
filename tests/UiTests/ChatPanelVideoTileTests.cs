using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-272: the tile a video turn is drawn as, and what clicking it does.
//
// Everything outside the process is faked through OpenClawVideo's three seams
// — the gateway's meta answer, the clip's bytes, and the system player — so a
// click here is a real click on the real handler and never a real fetch or a
// real process launch (OpenClawMedia.Open would put a window on whoever runs
// the suite). The seams are statics, so this class is in the "Settings"
// collection, which serialises it, and Dispose puts the real ones back.
[Collection("Settings")]
public class ChatPanelVideoTileTests : IDisposable
{
    private const string ClipPath = "/Users/sample/.openclaw/workspace-sample/outputs/sample_clip.mp4";
    private const string Route = "/__openclaw__/assistant-media?source=clip";

    private const string ServableMeta =
        "{\"available\":true,\"mimeType\":\"video/mp4\",\"sizeBytes\":1042813,\"playback\":\"native\","
        + "\"durationMs\":12750,\"width\":544,\"height\":800,\"mediaTicket\":\"v1.sample\"}";

    private readonly List<string> _sessionIdsToClean = new();

    private readonly Func<string, CancellationToken, Task<string?>> _realMeta = OpenClawVideo.FetchMeta;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _realBytes = OpenClawVideo.FetchBytes;
    private readonly Action<byte[], string> _realOpen = OpenClawVideo.Open;

    private readonly List<string> _metaAsked = new();
    private readonly List<string> _bytesAsked = new();
    private readonly List<(byte[] Bytes, string Name)> _opened = new();

    private string? _metaAnswer = ServableMeta;
    private Task<byte[]?> _bytesAnswer = Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 });

    public ChatPanelVideoTileTests()
    {
        OpenClawVideo.FetchMeta = (url, _) =>
        {
            _metaAsked.Add(url);
            return Task.FromResult(_metaAnswer);
        };

        OpenClawVideo.FetchBytes = (url, _) =>
        {
            _bytesAsked.Add(url);
            return _bytesAnswer;
        };

        OpenClawVideo.Open = (bytes, name) => _opened.Add((bytes, name));
    }

    public void Dispose()
    {
        OpenClawVideo.FetchMeta = _realMeta;
        OpenClawVideo.FetchBytes = _realBytes;
        OpenClawVideo.Open = _realOpen;

        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    private FakeChatSession NewFake(IEnumerable<ChatTurn> history)
    {
        var id = "video-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static ChatTurn ClipTurn(
        MediaConfidence confidence = MediaConfidence.High, string alt = "sample_clip.mp4") => new()
    {
        Role = ChatRole.Assistant,
        Text = "a little message just for you\nMEDIA:" + ClipPath,
        IsComplete = true,
        ImageSourcePath = ClipPath,
        Confidence = confidence,
        ImageAlt = alt,
        ImageUrl = Route,
    };

    private static ChatPanel Open(FakeChatSession fake)
    {
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();
        return ChatPanelTestAccess.Instance!;
    }

    private static Border Tile(ChatPanel panel) =>
        panel.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("videoTile"));

    private static IEnumerable<string> TextsIn(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(tb =>
            !string.IsNullOrEmpty(tb.Text)
                ? tb.Text!
                : string.Concat(tb.Inlines?.OfType<Run>().Select(r => r.Text) ?? Array.Empty<string>()));

    // The note line, found by the one Opacity only it has — the same way
    // ChatPanelImageNoteTests finds it, for the same reason.
    private static TextBlock NoteBlock(ChatPanel panel) =>
        panel.GetVisualDescendants().OfType<TextBlock>().First(tb => Math.Abs(tb.Opacity - 0.6) < 0.001);

    // A real PointerPressed raised on the tile, on the element the handler is
    // registered on — the same way ChatPanelTests.Drag sidesteps hit-testing
    // against a reused window while still running the production handler.
    private static void Click(ChatPanel panel, Control target)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, panel, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));

        Flush();
    }

    // ---- the tile -------------------------------------------------------

    [AvaloniaFact]
    public void AServableClipDrawsATileSizedToItsAspectRatioWithItsDuration()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));

        var tile = Tile(panel);
        Assert.True(tile.IsVisible);
        Assert.Equal(228, tile.Width);
        Assert.Equal(320, tile.Height);
        Assert.Contains("0:13", TextsIn(tile));

        // The caption is still there beside it.
        Assert.Contains(TextsIn(panel), t => t.Contains("a little message just for you"));
    }

    [AvaloniaFact]
    public void TheTileIsDrawnFromMetaAloneAndNothingIsDownloadedToDrawIt()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));

        Assert.True(Tile(panel).IsVisible);
        Assert.Equal(new[] { Route }, _metaAsked);
        Assert.Empty(_bytesAsked);
        Assert.Empty(_opened);
    }

    [AvaloniaFact]
    public void AClipWhoseMetaCarriesNoDimensionsOrDurationGetsADefaultTileAndNoLabel()
    {
        _metaAnswer = "{\"available\":true}";

        var panel = Open(NewFake(new[] { ClipTurn() }));

        var tile = Tile(panel);
        Assert.True(tile.IsVisible);
        Assert.Equal(128.25, tile.Height, 2);

        var label = tile.GetVisualDescendants().OfType<Border>().Single();
        Assert.False(label.IsVisible);
    }

    [AvaloniaFact]
    public void ALandscapeClipDrawsAWideShortTile()
    {
        _metaAnswer = "{\"available\":true,\"durationMs\":61000,\"width\":1920,\"height\":1080}";

        var panel = Open(NewFake(new[] { ClipTurn() }));

        var tile = Tile(panel);
        Assert.Equal(128.25, tile.Height, 2);
        Assert.Contains("1:01", TextsIn(tile));
    }

    [AvaloniaFact]
    public void ATurnThatGainsItsUrlAfterItsRowExistsStillDrawsTheTileAndAsksOnce()
    {
        var turn = ClipTurn();
        turn.ImageUrl = null;

        var panel = Open(NewFake(new[] { turn }));
        Assert.False(Tile(panel).IsVisible);
        Assert.Empty(_metaAsked);

        turn.ImageUrl = Route;
        Flush();
        Assert.True(Tile(panel).IsVisible);

        // The url changing again must not send the row asking a second time.
        turn.ImageUrl = Route + "&again=1";
        Flush();
        Assert.Single(_metaAsked);
    }

    [AvaloniaFact]
    public void ATurnWithNoClipHasNoTile()
    {
        var panel = Open(NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "an ordinary reply", IsComplete = true }
        }));

        Assert.False(Tile(panel).IsVisible);
        Assert.Empty(_metaAsked);
    }

    // ---- a refused or unreachable clip ----------------------------------

    [AvaloniaFact]
    public void ARefusedClipShowsWhyInsteadOfATile()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";

        var panel = Open(NewFake(new[] { ClipTurn() }));

        Assert.False(Tile(panel).IsVisible);

        var note = NoteBlock(panel);
        Assert.True(note.IsVisible);
        Assert.Equal(
            "Video not shown — the gateway wouldn't serve that file as a video (it said: Not an image).",
            note.Text);
        Assert.Equal(ClipPath + " — unsupported-media-type", ToolTip.GetTip(note));
    }

    [AvaloniaFact]
    public void AnUnreachableGatewayShowsTheHonestDontKnowLine()
    {
        _metaAnswer = null;

        var panel = Open(NewFake(new[] { ClipTurn() }));

        Assert.False(Tile(panel).IsVisible);
        Assert.Equal("Video not shown — couldn't ask the gateway why.", NoteBlock(panel).Text);
    }

    // CB-116's rule, kept for a clip: prose that merely ends in a filename
    // gets no confident note about a video that was never real.
    [AvaloniaFact]
    public void ARefusedLowConfidenceClipStaysSilentAndKeepsItsText()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"file-not-found\"}";

        var panel = Open(NewFake(new[] { ClipTurn(MediaConfidence.Low) }));

        Assert.False(Tile(panel).IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
        Assert.Contains(TextsIn(panel), t => t.Contains("a little message just for you"));
    }

    // ---- the click ------------------------------------------------------

    [AvaloniaFact]
    public void ClickingTheTileFetchesTheClipAndHandsItToThePlayer()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        var bytes = new byte[] { 9, 8, 7, 6 };
        _bytesAnswer = Task.FromResult<byte[]?>(bytes);

        Click(panel, Tile(panel));

        Assert.Equal(new[] { Route }, _bytesAsked);
        var opened = Assert.Single(_opened);
        Assert.Equal(bytes, opened.Bytes);
        Assert.Equal("sample_clip.mp4", opened.Name);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    // macOS `open` and Windows ShellExecute pick the player by extension, so a
    // clip with no name of its own keeps its source path's extension rather
    // than being called .mp4.
    [AvaloniaFact]
    public void ANamelessClipIsOpenedWithItsOwnExtension()
    {
        var turn = ClipTurn(alt: "");
        turn.ImageSourcePath = "/Users/sample/outputs/sample_clip.mov";
        var panel = Open(NewFake(new[] { turn }));

        Click(panel, Tile(panel));

        Assert.Equal("video.mov", Assert.Single(_opened).Name);
    }

    [AvaloniaFact]
    public void AClipThatWillNotComeDownSaysSoAndOpensNothing()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        _bytesAnswer = Task.FromResult<byte[]?>(null);

        Click(panel, Tile(panel));

        Assert.Empty(_opened);
        var note = NoteBlock(panel);
        Assert.True(note.IsVisible);
        Assert.Equal("Video not opened — the gateway didn't send the clip.", note.Text);
        Assert.Equal(ClipPath, ToolTip.GetTip(note));
    }

    [AvaloniaFact]
    public void AnEmptyDownloadIsTreatedAsNoDownload()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        _bytesAnswer = Task.FromResult<byte[]?>(Array.Empty<byte>());

        Click(panel, Tile(panel));

        Assert.Empty(_opened);
        Assert.StartsWith("Video not opened", NoteBlock(panel).Text);
    }

    // A second click while the first is still downloading must not start a
    // second fetch or open a second player; once it has finished, a later
    // click works again.
    [AvaloniaFact]
    public void AClickWhileTheClipIsStillDownloadingIsIgnored()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        var pending = new TaskCompletionSource<byte[]?>();
        _bytesAnswer = pending.Task;

        Click(panel, Tile(panel));
        Click(panel, Tile(panel));
        Assert.Single(_bytesAsked);
        Assert.Empty(_opened);

        pending.SetResult(new byte[] { 1 });
        Flush();
        Assert.Single(_opened);

        _bytesAnswer = Task.FromResult<byte[]?>(new byte[] { 2 });
        Click(panel, Tile(panel));
        Assert.Equal(2, _bytesAsked.Count);
        Assert.Equal(2, _opened.Count);
    }

    // The click handler resolves its row from the sender's DataContext. A
    // tile with none (which the template never produces) must do nothing
    // rather than throw.
    [AvaloniaFact]
    public void AClickOnATileWithNoRowBehindItDoesNothing()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        var tile = Tile(panel);
        tile.DataContext = null;

        Click(panel, tile);

        Assert.Empty(_bytesAsked);
        Assert.Empty(_opened);
    }

    // ---- the 32 MiB socket ceiling ----------------------------------------

    [AvaloniaFact]
    public void AClipTheMetaSaysIsTooLargeIsNotFetchedAndSaysSo()
    {
        _metaAnswer = ServableMeta.Replace("\"sizeBytes\":1042813",
            "\"sizeBytes\":" + (OpenClawVideo.MaxOpenBytes + 1));
        var panel = Open(NewFake(new[] { ClipTurn() }));

        Click(panel, Tile(panel));

        Assert.Empty(_bytesAsked);
        Assert.Empty(_opened);
        var note = NoteBlock(panel);
        Assert.StartsWith("Video not opened — the clip is", note.Text);
        Assert.Equal(ClipPath, ToolTip.GetTip(note));
    }

    // A gateway whose meta left sizeBytes out: the post-fetch check is what
    // stops a body the socket cut at its ceiling from reaching the player.
    [AvaloniaFact]
    public void ATruncatedDownloadIsNotOpened()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        _bytesAnswer = Task.FromResult<byte[]?>(new byte[OpenClawVideo.MaxOpenBytes + 1]);

        Click(panel, Tile(panel));

        Assert.Single(_bytesAsked);
        Assert.Empty(_opened);
        Assert.Equal(OpenClawVideo.TruncatedNote, NoteBlock(panel).Text);
    }

    // A body exactly at the ceiling is complete and opens: the boundary is
    // "exceeded", not "reached".
    [AvaloniaFact]
    public void ADownloadExactlyAtTheCeilingIsOpened()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));
        _bytesAnswer = Task.FromResult<byte[]?>(new byte[OpenClawVideo.MaxOpenBytes]);

        Click(panel, Tile(panel));

        Assert.Single(_opened);
    }

    // ---- the bubble -------------------------------------------------------

    private static Border BubbleOf(Control tile) =>
        tile.GetVisualAncestors().OfType<Border>().First(b => b.CornerRadius.TopLeft == 11);

    [AvaloniaFact]
    public void ATileOnlyBubbleUsesTheTightPadding()
    {
        var turn = ClipTurn();
        turn.Text = "";
        var panel = Open(NewFake(new[] { turn }));

        Assert.Equal(new Thickness(5), BubbleOf(Tile(panel)).Padding);
    }

    [AvaloniaFact]
    public void ATileWithACaptionKeepsTheOrdinaryPadding()
    {
        var panel = Open(NewFake(new[] { ClipTurn() }));

        Assert.Equal(new Thickness(9, 6), BubbleOf(Tile(panel)).Padding);
    }

    // ---- CB-274: a clip the gateway delivered as a block ------------------

    private const string Outgoing = "/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc/full";

    private static ChatAttachment BlockClip(long size = 1042813) =>
        new(AttachmentKind.Video, Outgoing, "sample_clip.mp4", "video/mp4", size, 12750, 544, 800, null);

    // A delivered block carries no local path (the gateway strips it) and its
    // url is not an assistant-media route.
    private static ChatTurn BlockTurn(ChatAttachment clip) => new()
    {
        Role = ChatRole.Assistant,
        Text = "a clip for you",
        IsComplete = true,
        ImageSourcePath = null,
        ImageAlt = clip.Label,
        Attachment = clip,
        ImageUrl = clip.Url,
    };

    // The seq-977 shape: a clip the gateway failed to deliver, resolved to a
    // path for the assistant-media route to be asked about.
    private static ChatTurn FailedClipTurn(MediaConfidence confidence = MediaConfidence.High) => new()
    {
        Role = ChatRole.Assistant,
        Text = "a little message just for you",
        IsComplete = true,
        ImageSourcePath = ClipPath,
        Confidence = confidence,
        ImageAlt = "sample_clip.mp4",
        ImageUrl = Route,
        Attachment = new ChatAttachment(AttachmentKind.Video, null, "sample_clip.mp4", "video/mp4",
            null, null, null, null, "delivery-failed"),
    };

    [AvaloniaFact]
    public void AClipWithBlockCarriedMetaDrawsATileWithoutAskingTheGateway()
    {
        // `&meta=1` on the outgoing route would be `…/full&meta=1`: a 404 that
        // captions a working clip with "couldn't ask the gateway why".
        OpenClawVideo.FetchMeta = (_, _) => throw new InvalidOperationException("must not ask");

        var panel = Open(NewFake(new[] { BlockTurn(BlockClip()) }));

        var tile = Tile(panel);
        Assert.True(tile.IsVisible);
        Assert.Equal(228, tile.Width);
        Assert.Equal(320, tile.Height);
        Assert.Contains("0:13", TextsIn(tile));
        Assert.False(NoteBlock(panel).IsVisible);
    }

    [AvaloniaFact]
    public void ClickingABlockCarriedClipFetchesItsOutgoingUrlAndOpensItUnderItsFileName()
    {
        var panel = Open(NewFake(new[] { BlockTurn(BlockClip()) }));

        Click(panel, Tile(panel));

        Assert.Equal(new[] { Outgoing }, _bytesAsked);
        Assert.Empty(_metaAsked);
        Assert.Equal("sample_clip.mp4", Assert.Single(_opened).Name);
    }

    [AvaloniaFact]
    public void ABlockCarriedClipTheBlockSaysIsTooLargeIsNotFetched()
    {
        var panel = Open(NewFake(new[] { BlockTurn(BlockClip(OpenClawVideo.MaxOpenBytes + 1)) }));

        Click(panel, Tile(panel));

        Assert.Empty(_bytesAsked);
        Assert.Empty(_opened);
        Assert.StartsWith("Video not opened — the clip is", NoteBlock(panel).Text);
    }

    [AvaloniaFact]
    public void AClipTheGatewayCouldNotDeliverSaysSoAndThenWhyTheFetchFailed()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";

        var panel = Open(NewFake(new[] { FailedClipTurn() }));

        Assert.False(Tile(panel).IsVisible);
        var note = NoteBlock(panel);
        Assert.Equal(
            "Video not delivered — the gateway couldn't prepare sample_clip.mp4 for this chat. "
            + "It also won't serve the file from the agent's folder (it said: Not an image).",
            note.Text);
        Assert.Equal(ClipPath + " — unsupported-media-type", ToolTip.GetTip(note));
        Assert.Contains(TextsIn(panel), t => t.Contains("a little message just for you"));
    }

    // CB-277: a clip another agent made, asked for under that agent's session.
    // The turn carries the owner-key route; the panel must ask meta and fetch
    // bytes with that exact string, not a prefix of it.
    private static readonly string OwnerRoute =
        new OpenClawMediaSource(ClipPath, "agent:comfyui:discord:direct:1").Route;

    private static ChatTurn OwnerKeyClipTurn() => new()
    {
        Role = ChatRole.Assistant,
        Text = "a little message just for you",
        IsComplete = true,
        ImageSourcePath = ClipPath,
        Confidence = MediaConfidence.High,
        ImageAlt = "sample_clip.mp4",
        ImageUrl = OwnerRoute,
        Attachment = new ChatAttachment(AttachmentKind.Video, null, "sample_clip.mp4", "video/mp4",
            null, null, null, null, "delivery-failed"),
    };

    [AvaloniaFact]
    public void AClipAskedUnderTheOwnersSessionDrawsAPlayableTileFromThatExactRoute()
    {
        var panel = Open(NewFake(new[] { OwnerKeyClipTurn() }));

        Assert.Equal(OwnerRoute, Assert.Single(_metaAsked));

        var tile = Tile(panel);
        Assert.True(tile.IsVisible);
        Assert.Equal(228, tile.Width);
        Assert.Equal(320, tile.Height);
        Assert.Contains("0:13", TextsIn(tile));
        Assert.False(NoteBlock(panel).IsVisible);

        Click(panel, tile);

        Assert.Equal(OwnerRoute, Assert.Single(_bytesAsked));
        Assert.EndsWith(".mp4", Assert.Single(_opened).Name);
    }

    private FakeChatSession NewFakeWithKey(IEnumerable<ChatTurn> history, string? gatewayKey)
    {
        var id = "video-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session", GatewayKey = gatewayKey };
    }

    private const string Refusal =
        "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";

    // The owner lookup re-keyed this clip away from the conversation's own
    // session, and the gateway still refused: the tooltip says which key.
    [AvaloniaFact]
    public void ARekeyedClipTheGatewayStillRefusesSaysWhichSessionItAskedUnder()
    {
        _metaAnswer = Refusal;

        var panel = Open(NewFakeWithKey(new[] { OwnerKeyClipTurn() }, "agent:main:discord:direct:1"));

        Assert.False(Tile(panel).IsVisible);
        var note = NoteBlock(panel);
        Assert.StartsWith("Video not delivered", note.Text);
        Assert.Equal(
            ClipPath + " — unsupported-media-type — asked under agent:comfyui:discord:direct:1",
            ToolTip.GetTip(note));
    }

    // The control: the same refusal under the conversation's own key is the
    // ordinary one, and its tooltip is exactly what it was before CB-277.
    [AvaloniaFact]
    public void ARefusalUnderTheConversationsOwnKeyHasNoAskedUnderSuffix()
    {
        _metaAnswer = Refusal;

        var panel = Open(NewFakeWithKey(new[] { OwnerKeyClipTurn() }, "agent:comfyui:discord:direct:1"));

        Assert.Equal(ClipPath + " — unsupported-media-type", ToolTip.GetTip(NoteBlock(panel)));
    }

    // And with no known conversation key (a room, a fake) no claim is made.
    [AvaloniaFact]
    public void ARefusalWithNoKnownConversationKeyHasNoAskedUnderSuffix()
    {
        _metaAnswer = Refusal;

        var panel = Open(NewFake(new[] { OwnerKeyClipTurn() }));

        Assert.Equal(ClipPath + " — unsupported-media-type", ToolTip.GetTip(NoteBlock(panel)));
    }

    // A failed delivery whose recovered path the gateway *will* serve is a
    // working clip: the tile draws and nothing complains.
    [AvaloniaFact]
    public void AFailedDeliveryWhoseRecoveredPathServesStillDrawsTheTile()
    {
        var panel = Open(NewFake(new[] { FailedClipTurn() }));

        Assert.True(Tile(panel).IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AFailedDeliveryOnALowConfidenceTurnStaysSilent()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"file-not-found\"}";

        var panel = Open(NewFake(new[] { FailedClipTurn(MediaConfidence.Low) }));

        Assert.False(Tile(panel).IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    [AvaloniaFact]
    public void ABlockCarriedClipAddedLiveDrawsItsTile()
    {
        var fake = NewFake(new[] { ClipTurn() });
        var panel = Open(fake);

        fake.RaiseTurnAdded(BlockTurn(BlockClip()));
        Flush();

        Assert.Equal(2, panel.GetVisualDescendants().OfType<Border>()
            .Count(b => b.Classes.Contains("videoTile") && b.IsVisible));
    }

    [AvaloniaFact]
    public void ADeliveredClipLabelledLikeAProgramIsOpenedAsAClip()
    {
        var clip = BlockClip();
        var panel = Open(NewFake(new[] { BlockTurn(clip with { Label = "evil.exe" }) }));

        Click(panel, Tile(panel));

        Assert.Equal("evil.mp4", Assert.Single(_opened).Name);
    }

    // A failed attachment whose label cannot become a route still tells the
    // user the gateway never delivered it.
    [AvaloniaFact]
    public void AFailedClipWithAnUnusableLabelStillShowsTheNote()
    {
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "a caption", IsComplete = true, ImageAlt = "..",
            Attachment = new ChatAttachment(AttachmentKind.Video, null, "..", "video/mp4",
                null, null, null, null, "delivery-failed"),
        };

        var panel = Open(NewFake(new[] { turn }));

        Assert.Equal("Video not delivered — the gateway couldn't prepare .. for this chat.", NoteBlock(panel).Text);
        Assert.Equal("..", ToolTip.GetTip(NoteBlock(panel)));
        Assert.Empty(_metaAsked);
        Assert.False(Tile(panel).IsVisible);
    }
}

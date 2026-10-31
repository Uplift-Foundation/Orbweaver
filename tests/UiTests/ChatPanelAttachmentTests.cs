using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-275: the audio tile, the file chip, and the failure forms of both. Faked
// through the same three OpenClawVideo seams the clip tile uses (one
// transport for every kind), so a click is a real click on the real handler
// and never a real fetch or a real process launch.
[Collection("Settings")]
public class ChatPanelAttachmentTests : IDisposable
{
    private const string Outgoing = "/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc/full";
    private const string NotePath = "/Users/sample/.openclaw/workspace-sample/outputs/note.m4a";
    private const string Route = "/__openclaw__/assistant-media?source=note";

    private readonly List<string> _sessionIds = new();
    private readonly Func<string, CancellationToken, Task<string?>> _realMeta = OpenClawVideo.FetchMeta;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _realBytes = OpenClawVideo.FetchBytes;
    private readonly Action<byte[], string> _realOpen = OpenClawVideo.Open;

    private readonly List<string> _metaAsked = new();
    private readonly List<string> _bytesAsked = new();
    private readonly List<(byte[] Bytes, string Name)> _opened = new();

    private string? _metaAnswer = "{\"available\":true,\"durationMs\":42000}";
    private Task<byte[]?> _bytesAnswer = Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 });

    public ChatPanelAttachmentTests()
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

        foreach (var id in _sessionIds) ChatPanel.HideFor(id);
    }

    private FakeChatSession NewFake(IEnumerable<ChatTurn> history)
    {
        var id = "attach-" + Guid.NewGuid();
        _sessionIds.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static ChatPanel Open(FakeChatSession fake)
    {
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        Flush();
        return ChatPanelTestAccess.Instance!;
    }

    private static Border Find(ChatPanel panel, string cls) =>
        panel.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(cls));

    private static IEnumerable<string> TextsIn(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(tb =>
            !string.IsNullOrEmpty(tb.Text)
                ? tb.Text!
                : string.Concat(tb.Inlines?.OfType<Run>().Select(r => r.Text) ?? Array.Empty<string>()));

    private static TextBlock NoteBlock(ChatPanel panel) =>
        panel.GetVisualDescendants().OfType<TextBlock>().First(tb => Math.Abs(tb.Opacity - 0.6) < 0.001);

    private static void Click(ChatPanel panel, Control target)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, panel, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));

        Flush();
    }

    private static ChatAttachment AudioBlock(string label = "voice_note.m4a", long? size = 240000,
        long? duration = 42000, string? mime = "audio/mp4") =>
        new(AttachmentKind.Audio, Outgoing, label, mime, size, duration, null, null, null);

    private static ChatAttachment DocBlock(string label = "report.pdf", long? size = 1_258_291,
        string? mime = "application/pdf") =>
        new(AttachmentKind.Document, Outgoing, label, mime, size, null, null, null, null);

    private static ChatTurn BlockTurn(ChatAttachment a, string text = "here you go") => new()
    {
        Role = ChatRole.Assistant,
        Text = text,
        IsComplete = true,
        ImageAlt = a.Label,
        Attachment = a,
        ImageUrl = a.Url,
    };

    private static ChatTurn FailedTurn(AttachmentKind kind, string label, string? mime,
        string code = "delivery-failed", MediaConfidence confidence = MediaConfidence.High, string path = NotePath) => new()
    {
        Role = ChatRole.Assistant,
        Text = "a caption",
        IsComplete = true,
        ImageSourcePath = path,
        Confidence = confidence,
        ImageAlt = label,
        ImageUrl = Route,
        Attachment = new ChatAttachment(kind, null, label, mime, null, null, null, null, code),
    };

    // ---- the audio tile ---------------------------------------------------

    [AvaloniaFact]
    public void AnAudioNoteDrawsAFixedHeightTileWithItsNameAndDuration()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock()) }));

        var tile = Find(panel, "audioTile");
        Assert.True(tile.IsVisible);
        Assert.Equal(228, tile.Width);
        Assert.Equal(64, tile.Height);
        Assert.Contains("voice_note.m4a", TextsIn(tile));
        Assert.Contains("0:42", TextsIn(tile));
        Assert.Empty(_metaAsked);
        Assert.False(Find(panel, "videoTile").IsVisible);
        Assert.False(Find(panel, "fileChip").IsVisible);
    }

    [AvaloniaFact]
    public void AnAudioNoteWithNoDurationHasNoBadge()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock(duration: null)) }));

        var badge = Find(panel, "audioTile").GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == null || t.Text == "");
        Assert.False(badge.IsVisible);
    }

    [AvaloniaFact]
    public void ClickingAnAudioNoteFetchesItsOutgoingUrlAndOpensItUnderItsFileName()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock()) }));

        Click(panel, Find(panel, "audioTile"));

        Assert.Equal(new[] { Outgoing }, _bytesAsked);
        Assert.Equal("voice_note.m4a", Assert.Single(_opened).Name);
    }

    [AvaloniaFact]
    public void ANamelessAudioNoteIsOpenedAsAudioWithTheMimesExtension()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock(label: "audio")) }));

        Click(panel, Find(panel, "audioTile"));

        Assert.Equal("audio.m4a", Assert.Single(_opened).Name);
    }

    [AvaloniaFact]
    public void AnAudioNoteTooLargeIsNotFetched()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock(size: OpenClawVideo.MaxOpenBytes + 1)) }));

        Click(panel, Find(panel, "audioTile"));

        Assert.Empty(_bytesAsked);
        Assert.StartsWith("Audio not opened — it is", NoteBlock(panel).Text);
    }

    [AvaloniaFact]
    public void AnAudioNoteThatWillNotComeDownOrCameDownCutShortSaysSo()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock()) }));

        _bytesAnswer = Task.FromResult<byte[]?>(null);
        Click(panel, Find(panel, "audioTile"));
        Assert.Equal("Audio not opened — the gateway didn't send it.", NoteBlock(panel).Text);

        _bytesAnswer = Task.FromResult<byte[]?>(new byte[OpenClawVideo.MaxOpenBytes + 1]);
        Click(panel, Find(panel, "audioTile"));
        Assert.StartsWith("Audio not opened — it came back incomplete", NoteBlock(panel).Text);
        Assert.Empty(_opened);
    }

    [AvaloniaFact]
    public void AClickWhileAnAudioNoteIsStillDownloadingIsIgnored()
    {
        var panel = Open(NewFake(new[] { BlockTurn(AudioBlock()) }));
        var pending = new TaskCompletionSource<byte[]?>();
        _bytesAnswer = pending.Task;

        Click(panel, Find(panel, "audioTile"));
        Click(panel, Find(panel, "audioTile"));
        Assert.Single(_bytesAsked);

        pending.SetResult(new byte[] { 1 });
        Flush();
        Assert.Single(_opened);
    }

    // A streamed `MEDIA:/…/note.m4a` has no block: the assistant-media route
    // is asked once, like a clip's.
    [AvaloniaFact]
    public void ARecoveredAudioPathAsksTheGatewayOnceAndDrawsATile()
    {
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "listen", IsComplete = true,
            ImageSourcePath = NotePath, ImageAlt = "note.m4a", ImageUrl = Route,
        };

        var panel = Open(NewFake(new[] { turn }));

        Assert.Equal(new[] { Route }, _metaAsked);
        var tile = Find(panel, "audioTile");
        Assert.True(tile.IsVisible);
        Assert.Contains("note.m4a", TextsIn(tile));
        Assert.Contains("0:42", TextsIn(tile));

        turn.ImageUrl = Route + "&again=1";
        Flush();
        Assert.Single(_metaAsked);
    }

    [AvaloniaFact]
    public void ANamelessRecoveredAudioPathIsNamedAfterItsFile()
    {
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "listen", IsComplete = true,
            ImageSourcePath = NotePath, ImageAlt = "", ImageUrl = Route,
        };

        var panel = Open(NewFake(new[] { turn }));
        Click(panel, Find(panel, "audioTile"));

        Assert.Equal("note.m4a", Assert.Single(_opened).Name);
    }

    [AvaloniaFact]
    public void ARefusedAudioPathShowsAnAudioNoteNotAPictureOne()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "listen", IsComplete = true,
            ImageSourcePath = NotePath, ImageAlt = "note.m4a", ImageUrl = Route,
        };

        var panel = Open(NewFake(new[] { turn }));

        Assert.False(Find(panel, "audioTile").IsVisible);
        Assert.Equal(
            "Audio not shown — the gateway wouldn't serve that file that way (it said: Not an image).",
            NoteBlock(panel).Text);
    }

    [AvaloniaFact]
    public void ARefusedLowConfidenceAudioPathStaysSilent()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"file-not-found\"}";
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "I deleted note.m4a", IsComplete = true,
            ImageSourcePath = NotePath, ImageAlt = "note.m4a", ImageUrl = Route,
            Confidence = MediaConfidence.Low,
        };

        var panel = Open(NewFake(new[] { turn }));

        Assert.False(Find(panel, "audioTile").IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    // ---- the file chip ----------------------------------------------------

    [AvaloniaFact]
    public void AFileDrawsAChipWithItsNameAndSizeAndNothingIsAsked()
    {
        var panel = Open(NewFake(new[] { BlockTurn(DocBlock()) }));

        var chip = Find(panel, "fileChip");
        Assert.True(chip.IsVisible);
        Assert.Contains("report.pdf", TextsIn(chip));
        Assert.Contains("1.2 MB", TextsIn(chip));
        Assert.Equal("application/pdf", ToolTip.GetTip(chip));
        Assert.Empty(_metaAsked);
        Assert.Empty(_bytesAsked);
    }

    [AvaloniaFact]
    public void AFileOfUnknownSizeShowsTheNameAlone()
    {
        var panel = Open(NewFake(new[] { BlockTurn(DocBlock(size: null)) }));

        var chip = Find(panel, "fileChip");
        Assert.DoesNotContain(TextsIn(chip), t => t.EndsWith("MB") || t.EndsWith("KB") || t.EndsWith(" B"));
    }

    [AvaloniaFact]
    public void ClickingAFileFetchesItAndOpensItUnderTheMimeDerivedName()
    {
        var panel = Open(NewFake(new[] { BlockTurn(DocBlock("evil.exe")) }));

        Click(panel, Find(panel, "fileChip"));

        Assert.Equal(new[] { Outgoing }, _bytesAsked);
        Assert.Equal("evil.pdf", Assert.Single(_opened).Name);
    }

    [AvaloniaFact]
    public void AFileOverTheCeilingIsNotFetchedAndSaysHowBig()
    {
        var panel = Open(NewFake(new[] { BlockTurn(DocBlock(size: 100L * 1_048_576)) }));

        Click(panel, Find(panel, "fileChip"));

        Assert.Empty(_bytesAsked);
        Assert.Equal(
            "File not opened — it is 100 MB and this app fetches at most 32 MB. Open it on the gateway machine instead.",
            NoteBlock(panel).Text);
    }

    [AvaloniaFact]
    public void AFileThatDoesNotComeDownSaysSo()
    {
        var panel = Open(NewFake(new[] { BlockTurn(DocBlock()) }));
        _bytesAnswer = Task.FromResult<byte[]?>(Array.Empty<byte>());

        Click(panel, Find(panel, "fileChip"));

        Assert.Equal("File not opened — the gateway didn't send it.", NoteBlock(panel).Text);
    }

    [AvaloniaFact]
    public void AClickOnAChipWithNoUrlOrNoRowDoesNothing()
    {
        var noUrl = BlockTurn(DocBlock());
        var panel = Open(NewFake(new[] { noUrl }));
        var chip = Find(panel, "fileChip");

        noUrl.ImageUrl = null;
        Click(panel, chip);
        Assert.Empty(_bytesAsked);

        chip.DataContext = null;
        Click(panel, chip);
        Assert.Empty(_bytesAsked);
    }

    [AvaloniaFact]
    public void AChipOnlyBubbleUsesTheTightPaddingAndACaptionedOneDoesNot()
    {
        var bare = Open(NewFake(new[] { BlockTurn(DocBlock(), text: "") }));
        Assert.Equal(new Thickness(5),
            Find(bare, "fileChip").GetVisualAncestors().OfType<Border>().First(b => b.CornerRadius.TopLeft == 11).Padding);

        var captioned = Open(NewFake(new[] { BlockTurn(DocBlock()) }));
        Assert.Equal(new Thickness(9, 6),
            Find(captioned, "fileChip").GetVisualAncestors().OfType<Border>().First(b => b.CornerRadius.TopLeft == 11).Padding);
    }

    // ---- failure chips, every kind -----------------------------------------

    [AvaloniaFact]
    public void AFileTheGatewayCouldNotDeliverDrawsTheChipWithANote()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";
        var turn = FailedTurn(AttachmentKind.Document, "quarterly-report.pdf", "application/pdf",
            code: "unsupported-format", path: "/Users/sample/.openclaw/media/quarterly-report.pdf");

        var panel = Open(NewFake(new[] { turn }));

        var chip = Find(panel, "fileChip");
        Assert.True(chip.IsVisible);
        Assert.Contains("quarterly-report.pdf", TextsIn(chip));
        var note = NoteBlock(panel);
        Assert.Equal(
            "File not delivered — the gateway's attachment allowlist rejects that type. "
            + "It also won't serve the file from the agent's folder (it said: Not an image).",
            note.Text);
        Assert.Equal("/Users/sample/.openclaw/media/quarterly-report.pdf — unsupported-media-type",
            ToolTip.GetTip(note));
    }

    [AvaloniaFact]
    public void AFailedFileTheGatewayWillServeStillDrawsTheChipAndNoNote()
    {
        _metaAnswer = "{\"available\":true}";
        var panel = Open(NewFake(new[] { FailedTurn(AttachmentKind.Document, "r.pdf", "application/pdf",
            path: "/x/r.pdf") }));

        Assert.True(Find(panel, "fileChip").IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AFailedFileOnALowConfidenceTurnDrawsTheChipAndStaysSilent()
    {
        var panel = Open(NewFake(new[] { FailedTurn(AttachmentKind.Document, "r.pdf", "application/pdf",
            confidence: MediaConfidence.Low, path: "/x/r.pdf") }));

        Assert.True(Find(panel, "fileChip").IsVisible);
        Assert.Empty(_metaAsked);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AnAudioNoteTheGatewayCouldNotDeliverKeepsATileAndSaysSo()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"file-not-found\"}";

        var panel = Open(NewFake(new[] { FailedTurn(AttachmentKind.Audio, "note.m4a", "audio/mp4") }));

        Assert.True(Find(panel, "audioTile").IsVisible);
        Assert.Equal(
            "Audio not delivered — the gateway couldn't prepare note.m4a for this chat. "
            + "It also couldn't find the file in the agent's folder.",
            NoteBlock(panel).Text);
    }

    [AvaloniaFact]
    public void AFailedAudioNoteTheGatewayWillServeDrawsTheTileAndNoNote()
    {
        var panel = Open(NewFake(new[] { FailedTurn(AttachmentKind.Audio, "note.m4a", "audio/mp4") }));

        Assert.True(Find(panel, "audioTile").IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AFailedAudioNoteOnALowConfidenceTurnKeepsItsTileAndStaysSilent()
    {
        _metaAnswer = "{\"available\":false,\"code\":\"file-not-found\"}";

        var panel = Open(NewFake(new[] { FailedTurn(AttachmentKind.Audio, "note.m4a", "audio/mp4",
            confidence: MediaConfidence.Low) }));

        Assert.True(Find(panel, "audioTile").IsVisible);
        Assert.False(NoteBlock(panel).IsVisible);
    }

    // A failed *picture* goes down the picture path. The media cache is seeded
    // with a refusal (the same seam ChatPanelImageNoteTests uses) so no socket
    // opens, and no gateway is configured, so the meta answer is null and the
    // note is the delivery sentence alone.
    [AvaloniaFact]
    public async Task APictureTheGatewayCouldNotDeliverSaysSo()
    {
        var path = "/Users/sample/.openclaw/media/" + Guid.NewGuid() + ".png";
        var source = new OpenClawMediaSource(path, "agent:main:discord:direct:1");
        var field = typeof(OpenClawSessions).GetField(
            "Media", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        ((Dictionary<string, byte[]?>)field.GetValue(null)!)[source.Route] = Array.Empty<byte>();

        var turn = FailedTurn(AttachmentKind.Image, "sample.png", "image/png", code: "file-not-found", path: path);
        turn.ImageUrl = source.Route;

        Open(NewFake(new[] { turn }));

        for (var i = 0; i < 40 && turn.ImageNote is null; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Equal("Picture not delivered — the file wasn't there when the gateway looked.", turn.ImageNote);
        Assert.Equal(path, turn.ImageNoteDetail);
    }

    // A block that names nothing shows a generic word, never an invented
    // filename, and a failed one with no name has no route to ask about.
    [AvaloniaFact]
    public void ANamelessDeliveredBlockShowsAGenericWordAndOpensUnderIt()
    {
        var audio = Open(NewFake(new[] { BlockTurn(AudioBlock(label: "")) }));
        Assert.Contains("audio", TextsIn(Find(audio, "audioTile")));
        Click(audio, Find(audio, "audioTile"));
        Assert.Equal("audio.m4a", Assert.Single(_opened).Name);

        var file = Open(NewFake(new[] { BlockTurn(DocBlock(label: "")) }));
        Assert.Contains("file", TextsIn(Find(file, "fileChip")));
    }

    [AvaloniaFact]
    public void ANamelessFailedFileSaysSoWithNoGuessedPathOrGatewayQuestion()
    {
        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant, Text = "a caption", IsComplete = true,
            Attachment = new ChatAttachment(AttachmentKind.Document, null, "", "application/pdf",
                null, null, null, null, "delivery-failed"),
        };

        var panel = Open(NewFake(new[] { turn }));

        Assert.Equal("File not delivered — the gateway couldn't prepare the file for this chat.",
            NoteBlock(panel).Text);
        Assert.Null(ToolTip.GetTip(NoteBlock(panel)));
        Assert.Empty(_metaAsked);
        Assert.False(Find(panel, "fileChip").IsVisible);
    }
}

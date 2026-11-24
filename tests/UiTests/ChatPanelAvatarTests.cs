using System.Reflection;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// The chat panel's portrait: what it wears when the session it is bound to is
// a gateway agent (with or without a picture of its own), and what it wears
// when there is no OpenClaw identity at all and it borrows the orb's own
// letters and colour instead.
//
// The two sources are independent in these tests on purpose — a
// FakeChatSession's id and an OrbWindow's own id agree in production (they
// name the same conversation) but nothing here relies on that, and keeping
// them apart is what lets a "the orb has an identity, the session doesn't"
// case (BorrowedLettersFallBackToTheTitleWhenTheOrbHasNoGlyphYet) be built at
// all without one identity answering both questions at once.
[Collection("Settings")]
public class ChatPanelAvatarTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private FakeChatSession NewFake(string? sessionId = null, string displayName = "Fake Session")
    {
        var id = sessionId ?? "fake-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(null) { SessionId = id, DisplayName = displayName };
    }

    // Never closed — see ChatPanelTests' own comment on NewOrb for why:
    // closing an OrbWindow corrupts a process-wide FontManager resource shared
    // by every other headless window in the suite.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
        AvatarPopup.Close();
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // Real time, not a simulated clock: the avatar's animation timer is a
    // DispatcherTimer driven by the wall clock the same way OrbFlyoutTests'
    // PumpUntil documents ForceRenderTimerTick does not advance those.
    private static async Task PumpUntil(Func<bool> done, string what)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            await Task.Delay(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    private static byte[] Png(byte r = 0x5A, byte g = 0xF7, byte b = 0x8E)
    {
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // A minimal two-frame GIF89a — the same hand-written fixture
    // OpenClawAvatarsTests uses, for the same reason its own comment gives:
    // Skia has no GIF encoder, so there is no way to produce one at test time
    // otherwise.
    private static byte[] AnimatedGif() => new byte[]
    {
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,

        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,

        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,

        0x3B,
    };

    private static void Publish(string agent, string? emoji, byte[]? avatar) =>
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                [agent] = new(agent, emoji, avatar),
            });

    // Deals the agent a real colour the way a live poll would — Parse is what
    // actually calls AgentPalette.Assign, so a colour dealt any other way
    // could not be trusted to be the one ColourForAgent later answers with.
    private static void AssignColour(string agent)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = $$"""
            {"sessions":[{"key":"agent:{{agent}}:discord:direct:1","chatType":"direct",
                          "lastActivityAt":{{now}}}]}
            """;

        OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, DateTime.UtcNow);
    }

    private static string Agent() => "agent" + Guid.NewGuid().ToString("N")[..10];

    // --- a gateway agent with an identity but no picture ---

    // No picture and no colour assigned yet is the ordinary state right after
    // a gateway connects: the emoji (or, with none, the initials) is what
    // carries the identity, and an empty circle behind it — Avatar.Fill null,
    // Avatar.IsVisible false — is honest about there being no colour to draw
    // yet, rather than drawing a default that would look like an answer.
    [AvaloniaFact]
    public void AnAgentWithAnEmojiAndNoColourYetShowsTheEmojiOnAnEmptyCircle()
    {
        var agent = Agent();
        Publish(agent, emoji: "🤖", avatar: null);

        var fake = NewFake($"openclaw:agent:{agent}:discord:direct:1", "Some Agent");
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Equal("🤖", panel.AvatarEmoji.Text);
        Assert.True(panel.AvatarEmoji.IsVisible);
        Assert.False(panel.Avatar.IsVisible);
        Assert.Null(panel.Avatar.Fill);
    }

    // No emoji falls back to the agent's own initials rather than an empty
    // circle with nothing at all in it.
    [AvaloniaFact]
    public void AnAgentWithNoEmojiShowsItsInitialsInstead()
    {
        var agent = Agent();
        Publish(agent, emoji: null, avatar: null);

        var fake = NewFake($"openclaw:agent:{agent}:discord:direct:1", "Some Agent");
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Equal(OrbGlyph.Initials(agent), panel.AvatarEmoji.Text);
    }

    // Once the agent has been dealt a real colour (AgentPalette.Assign, via a
    // real poll's Parse), the circle behind the emoji is filled and ringed in
    // it — the "no colour yet" case above is what an agent looks like before
    // this happens, not the ordinary case.
    [AvaloniaFact]
    public void AnAgentWithAnAssignedColourFillsAndRingsTheCircleInIt()
    {
        var agent = Agent();
        Publish(agent, emoji: "🤖", avatar: null);
        AssignColour(agent);

        var fake = NewFake($"openclaw:agent:{agent}:discord:direct:1", "Some Agent");
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var hex = OpenClawSessions.ColourForAgent(agent);
        Assert.False(string.IsNullOrEmpty(hex));

        var expected = Color.Parse(hex);
        Assert.True(panel.Avatar.IsVisible);
        Assert.Equal(expected, ((ISolidColorBrush)panel.Avatar.Fill!).Color);
        Assert.Equal(expected, ((ISolidColorBrush)panel.Avatar.Stroke!).Color);
    }

    // --- a gateway agent with a real picture ---

    // A still picture becomes the circle's fill, and clicking it opens the
    // popup at four times the size — the same portrait, not a second decode.
    [AvaloniaFact]
    public void AnAgentWithAPictureShowsItAndClickingItOpensThePopup()
    {
        var agent = Agent();
        Publish(agent, emoji: null, avatar: Png());

        var fake = NewFake($"openclaw:agent:{agent}:discord:direct:1", "Pictured Agent");
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.Avatar.IsVisible);
        Assert.IsType<ImageBrush>(panel.Avatar.Fill);
        Assert.False(AvatarPopup.IsOpen);

        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, isPrimary: true);
        panel.AvatarBox.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            panel.AvatarBox, pointer, panel, new Avalonia.Point(34, 34), 0,
            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None, 1));
        FlushRender();

        Assert.True(AvatarPopup.IsOpen);
    }

    // An animated picture keeps advancing on its own timer while the panel is
    // open — the same 100ms-floor rule OpenClawAvatarsTests pins for the
    // decoder, exercised here through the panel's own tick rather than the
    // decoder directly.
    [AvaloniaFact]
    public async Task AnAnimatedPictureAdvancesItsFrameOnATimerTick()
    {
        var agent = Agent();
        Publish(agent, emoji: null, avatar: AnimatedGif());

        var fake = NewFake($"openclaw:agent:{agent}:discord:direct:1", "Animated Agent");
        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.Avatar.IsVisible);
        var brush = Assert.IsType<ImageBrush>(panel.Avatar.Fill);
        var first = brush.Source;

        await PumpUntil(() => !ReferenceEquals(brush.Source, first), "the avatar frame to advance");

        Assert.NotSame(first, brush.Source);
    }

    // --- borrowing the orb's own letters and colour ---

    // The header's borrowed identity is refreshed, not frozen at Bind — a
    // /rename after the panel opened must not leave it wearing the old
    // letters. OrbWindow.UpdateFrom calls ChatPanel.RefreshIdentityFor itself
    // once a session's title changes, which is what this drives.
    [AvaloniaFact]
    public void BorrowedLettersAreRefreshedWhenTheOrbsGlyphActuallyChanges()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, Title = "Aaa Agent" });

        var fake = NewFake();
        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var before = panel.AvatarEmoji.Text;

        orb.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, Title = "Zzz Renamed" });
        Flush();

        Assert.NotEqual(before, panel.AvatarEmoji.Text);
        Assert.Equal(orb.GlyphText, panel.AvatarEmoji.Text);
    }

    // BorrowedLetters falls back to the panel's own title when the orb has no
    // glyph of its own yet. That is not the ordinary case — OrbWindow's own
    // XAML default is a bullet, "•", not empty — but a gateway identity whose
    // emoji is genuinely "" (as opposed to absent) leaves Glyph.Text empty on
    // the orb, which is the one real way to reach it.
    [AvaloniaFact]
    public void BorrowedLettersFallBackToTheTitleWhenTheOrbHasNoGlyphYet()
    {
        var agent = Agent();
        Publish(agent, emoji: "", avatar: null);

        var orbId = $"openclaw:agent:{agent}:discord:direct:1";
        var orb = new OrbWindow(orbId);
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, Title = agent });
        Assert.Equal("", orb.GlyphText);

        // A different, unrelated session id — deliberately not the orb's own,
        // so the panel's own identity lookup (on the session, not the orb)
        // finds nothing and takes the borrowed path at all.
        var fake = NewFake(displayName: "Some Fallback Title");
        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var expected = OrbGlyph.For("Some Fallback Title", OrbweaverSettings.TwoLetterGlyphs);
        Assert.Equal(expected, panel.AvatarEmoji.Text);
    }

    // --- a Paperclip agent (CB-445) ---

    [AvaloniaFact]
    public void APaperclipAgentWithACachedPictureShowsItInTheHeader_AndOneWithoutBorrowsTheOrbsLetters()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb445-head-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "face.png");
        File.WriteAllBytes(file, Png());
        var id = Guid.NewGuid().ToString();
        PaperclipAgent Agent(string? f) => new(id, "Atlas", "r", "t", "idle", null, "x", "idle", PaperclipMark.None, null, [],
            null, null, null, null, null, DateTime.UtcNow, "u", false, AvatarFile: f);
        try
        {
            PaperclipSessions.SetSnapshotForTests([Agent(file)]);
            ChatPanel.OpenFor(NewOrb(), NewFake(PaperclipScan.KeyFor(id), "Atlas"));
            FlushRender();
            var panel = ChatPanelTestAccess.Instance!;
            Assert.True(panel.Avatar.IsVisible);
            Assert.IsType<ImageBrush>(panel.Avatar.Fill);

            // The picture's file goes missing: the header falls back to the borrowed letters.
            File.Delete(file);
            PaperclipSessions.SetSnapshotForTests([Agent(Path.Combine(dir, "gone.png"))]);
            ChatPanel.HideFor(PaperclipScan.KeyFor(id));
            ChatPanel.OpenFor(NewOrb(), NewFake(PaperclipScan.KeyFor(id), "Atlas"));
            FlushRender();
            Assert.IsNotType<ImageBrush>(ChatPanelTestAccess.Instance!.Avatar.Fill);
        }
        finally
        {
            PaperclipSessions.SetSnapshotForTests([]);
            Directory.Delete(dir, true);
        }
    }
}

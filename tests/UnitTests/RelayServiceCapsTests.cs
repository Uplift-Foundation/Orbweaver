using Xunit;

namespace Orbweaver.Tests;

// CB-447, QA batch 1 item 2: the condition that advertises paperclip_chat, and the caps a phone is sent as it moves.
// PaperclipSessions' config and actions are process-wide statics; the classes of this collection set and restore them, so
// they must not run beside each other (PaperclipPhoneThreadTests swaps ConfigSource, which turned an actions-less
// LiveActions into a real one under this class's rule).
[Collection("PaperclipSessions statics")]
public class RelayServiceCapsTests : IDisposable
{
    public void Dispose()
    {
        PaperclipSessions.Actions = null;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
    }

    private sealed class Actions : IPaperclipActions
    {
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("Connected", true, true)]
    [InlineData("Connected", false, false)]
    [InlineData("Off", true, false)]
    [InlineData("Connecting", true, false)]
    [InlineData("NeedsReconnect", true, false)]
    [InlineData("Unreachable", true, false)]
    [InlineData("Off", false, false)]
    public void The_cap_needs_a_connected_link_and_an_actions_seam_to_send_through(string linkName, bool actions, bool expected)
    {
        var link = Enum.Parse<PaperclipLink>(linkName);
        PaperclipSessions.SetLinkForTests(link, linkName);
        PaperclipSessions.Actions = actions ? new Actions() : null;
        Assert.Equal(expected, RelayService.PaperclipChatAvailable);
        Assert.Equal(BrowseFrames.CapsFor(expected), RelayService.CapsFrame());
        Assert.Equal(expected, RelayService.CapsFrame().Contains("paperclip_chat", StringComparison.Ordinal));
    }

    [Fact]
    public void The_source_can_be_replaced_and_the_caps_frame_follows_it()
    {
        var before = RelayService.PaperclipChatAvailableSource;
        try
        {
            RelayService.PaperclipChatAvailableSource = () => true;
            Assert.Contains("paperclip_chat", RelayService.CapsFrame());
            RelayService.PaperclipChatAvailableSource = () => false;
            Assert.DoesNotContain("paperclip_chat", RelayService.CapsFrame());
        }
        finally { RelayService.PaperclipChatAvailableSource = before; }
    }
}

public partial class RelayLinkTests
{
    // The name flips on the live path: re-sent every scan, dropped when unchanged, and sent again when it moves.
    [Fact]
    public void The_caps_name_appears_when_paperclip_connects_and_goes_when_it_does_not()
    {
        var (link, _, phone, _, _) = Paired();
        string[] NamesIn(string frame) => phone.OpenFromDesktop(frame).GetProperty("caps").EnumerateArray().Select(c => c.GetString()!).ToArray();

        var off = link.SendCaps(BrowseFrames.CapsFor(false)).Single();
        Assert.DoesNotContain("paperclip_chat", NamesIn(off));
        Assert.Empty(link.SendCaps(BrowseFrames.CapsFor(false)));                    // unchanged: nothing

        var on = link.SendCaps(BrowseFrames.CapsFor(true)).Single();
        Assert.Contains("paperclip_chat", NamesIn(on));
        Assert.Empty(link.SendCaps(BrowseFrames.CapsFor(true)));

        Assert.DoesNotContain("paperclip_chat", NamesIn(link.SendCaps(BrowseFrames.CapsFor(false)).Single()));   // and back
    }
}

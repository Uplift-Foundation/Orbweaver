using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-274 / CB-275: every site that copies a history turn into a live one has
// to carry the attachment block, or that path draws a clip or a pdf as a
// bitmap (the decoder does not throw on rubbish). Plus the pure helpers the
// recovery pass and the failure note are built from.
[Collection("Settings")]
public class OpenClawAttachmentCarryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static ChatAttachment Clip(string? url = "/api/chat/media/outgoing/k/a/full") =>
        new(AttachmentKind.Video, url, "clip.mp4", "video/mp4", 100, 12750, 544, 800,
            url is null ? "delivery-failed" : null);

    private static HistoryTurn Turn(ChatRole role = ChatRole.Assistant, string text = "caption",
        bool mine = false, string? speaker = null, ChatAttachment? attachment = null) =>
        new(role, text, "/route", "clip.mp4", T0, speaker, null, mine,
            Attachment: attachment ?? Clip());

    private static OpenClawChatSession Session() =>
        new("openclaw:agent:nova:discord:channel:1", "agent:nova:discord:channel:1", "nova");

    // ---- AppendNewTail, SetHistory, PrependHistory ----------------------

    [Fact]
    public void AppendNewTailCarriesTheAttachment()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Turn(text: "one", attachment: Clip(null)) });

        session.AppendNewTail(new List<HistoryTurn>
        {
            Turn(text: "one", attachment: Clip(null)),
            Turn(text: "two", attachment: Clip()),
        });

        Assert.Equal(AttachmentKind.Video, session.History[1].Attachment!.Value.Kind);
        Assert.True(session.History[1].IsVideo);
        Assert.True(session.History[0].Attachment!.Value.Failed);
    }

    [Fact]
    public void SetHistoryAndPrependHistoryCarryTheAttachment()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Turn(text: "set") });
        session.PrependHistory(new List<HistoryTurn> { Turn(text: "older") });

        Assert.All(session.History, t => Assert.True(t.IsVideo && t.Attachment is not null));
    }

    // ---- the room's four copies -----------------------------------------

    private static OpenClawRoomChatSession RoomOf(params HistoryTurn[] turns)
    {
        var nova = Session();
        nova.SetHistory(turns.ToList());
        nova.HasMore = false;

        var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
        room.SetMembers(new List<(OpenClawChatSession, string, string)> { (nova, "Nova", "#7f7") });
        room.Rebuild();
        return room;
    }

    [Fact]
    public void AnAgentsOwnAttachmentSurvivesTheRoomMerge() =>
        Assert.Equal(AttachmentKind.Video,
            Assert.Single(RoomOf(Turn()).History).Attachment!.Value.Kind);

    [Fact]
    public void YourOwnAttachmentSurvivesTheRoomMerge() =>
        Assert.True(Assert.Single(RoomOf(Turn(ChatRole.User, "mine", mine: true)).History).IsVideo);

    [Fact]
    public void ANamedSendersAttachmentSurvivesTheRoomMerge() =>
        Assert.True(Assert.Single(RoomOf(Turn(ChatRole.User, "theirs", speaker: "Pat")).History).IsVideo);

    [Fact]
    public void AnUnattributedAttachmentSurvivesTheRoomMerge() =>
        Assert.True(Assert.Single(RoomOf(Turn(ChatRole.User, "anon")).History).IsVideo);

    // ---- which filename the recovery pass looks up ----------------------

    [Fact]
    public void ADeliveredAttachmentHasNothingToRecover() =>
        Assert.Null(OpenClawSessions.RecoveryBasenameFor(Turn(text: "look at x.png")));

    [Fact]
    public void AFailedAttachmentIsLookedUpByItsLabelAheadOfTheCaption() =>
        Assert.Equal("clip.mp4",
            OpenClawSessions.RecoveryBasenameFor(Turn(text: "see other.png", attachment: Clip(null))));

    [Fact]
    public void AFailedAttachmentWithAnUnusableLabelFallsBackToTheCaption()
    {
        var odd = Clip(null) with { Label = "a b.mp4" };

        Assert.Equal("other.png",
            OpenClawSessions.RecoveryBasenameFor(Turn(text: "see other.png", attachment: odd)));
        Assert.Null(OpenClawSessions.RecoveryBasenameFor(Turn(text: "no file", attachment: odd)));
    }

    [Fact]
    public void ATurnWithNoAttachmentIsTheOriginalTrigger()
    {
        var plain = new HistoryTurn(ChatRole.Assistant, "here\nx.png", null, "", T0, null, null);

        Assert.Equal("x.png", OpenClawSessions.RecoveryBasenameFor(plain));
    }

    [Theory]
    [InlineData("x.mp4", "x.mp4")]
    [InlineData("x.png", "x.png")]
    [InlineData("x.pdf", "x.pdf")]
    [InlineData("a/b.mp4", null)]
    [InlineData("a\\b.mp4", null)]
    [InlineData("x y.pdf", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ABareMediaBasenameIsAnyExtensionWithNoSeparatorOrWhitespace(string? label, string? expected) =>
        Assert.Equal(expected, OpenClawCronRecovery.BareMediaBasename(label));

    // ---- the failure note -----------------------------------------------

    private static ChatAttachment Failed(AttachmentKind kind, string code, string label = "f.bin") =>
        new(kind, null, label, null, null, null, null, null, code);

    [Fact]
    public void TheSeqNineSevenSevenNoteIsExactlyWhatTheTicketSpecified()
    {
        var a = Failed(AttachmentKind.Video, "delivery-failed", OpenClawAttachmentFixtures.ClipName);
        const string notAnImage = "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";

        Assert.Equal(
            "Video not delivered — the gateway couldn't prepare "
            + "sample_pink_afternoon_video_2pm_1234567890.mp4 for this chat. "
            + "It also won't serve the file from the agent's folder (it said: Not an image).",
            OpenClawMediaRefusal.ExplainDelivery(a, notAnImage));
    }

    [Theory]
    [InlineData(AttachmentKind.Image, "Picture")]
    [InlineData(AttachmentKind.Video, "Video")]
    [InlineData(AttachmentKind.Audio, "Audio")]
    [InlineData(AttachmentKind.Document, "File")]
    public void TheNounFollowsTheKind(AttachmentKind kind, string noun) =>
        Assert.StartsWith(noun + " not delivered — ",
            OpenClawMediaRefusal.ExplainDelivery(Failed(kind, "delivery-failed"), null));

    [Theory]
    [InlineData("file-not-found", "the file wasn't there when the gateway looked.")]
    [InlineData("unsupported-format", "the gateway's attachment allowlist rejects that type.")]
    [InlineData("invalid-reference", "the agent gave the gateway a reference it can't open.")]
    [InlineData("omitted", "the gateway left f.bin out of this transcript.")]
    [InlineData("delivery-failed", "the gateway couldn't prepare f.bin for this chat.")]
    [InlineData("something-new", "the gateway couldn't prepare f.bin for this chat.")]
    public void EachFailureCodeIsPutIntoWords(string code, string clause) =>
        Assert.Equal("File not delivered — " + clause,
            OpenClawMediaRefusal.ExplainDelivery(Failed(AttachmentKind.Document, code), null));

    [Theory]
    [InlineData(null, "")]
    [InlineData("not json", "")]
    [InlineData("{\"available\":true}", "")]
    [InlineData("{\"available\":false,\"code\":\"file-not-found\"}",
        " It also couldn't find the file in the agent's folder.")]
    [InlineData("{\"available\":false,\"code\":\"outside-allowed-folders\",\"reason\":\"x\"}",
        " It also won't serve the file from that folder.")]
    [InlineData("{\"available\":false,\"code\":\"unsupported-media-type\"}",
        " It also won't serve the file from the agent's folder.")]
    [InlineData("{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\" Not an image \"}",
        " It also won't serve the file from the agent's folder (it said: Not an image).")]
    public void TheSecondSentenceIsOnlyAsTrueAsTheGatewaysAnswer(string? json, string suffix) =>
        Assert.Equal(
            "Video not delivered — the gateway couldn't prepare f.bin for this chat." + suffix,
            OpenClawMediaRefusal.ExplainDelivery(Failed(AttachmentKind.Video, "delivery-failed"), json));

    [Fact]
    public void AVerboseGatewayReasonIsTruncated()
    {
        var json = "{\"available\":false,\"reason\":\"" + new string('x', 500) + "\"}";

        var note = OpenClawMediaRefusal.ExplainDelivery(Failed(AttachmentKind.Video, "delivery-failed"), json);

        Assert.Contains(new string('x', 200) + ")", note);
        Assert.DoesNotContain(new string('x', 201), note);
    }

    [Theory]
    [InlineData("delivery-failed", "File not delivered — the gateway couldn't prepare the file for this chat.")]
    [InlineData("omitted", "File not delivered — the gateway left the file out of this transcript.")]
    public void ANamelessAttachmentSaysTheFileRatherThanAnInventedName(string code, string expected) =>
        Assert.Equal(expected,
            OpenClawMediaRefusal.ExplainDelivery(Failed(AttachmentKind.Document, code, ""), null));
}

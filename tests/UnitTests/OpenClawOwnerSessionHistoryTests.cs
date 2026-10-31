using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-277: the three history shapes that carry a clip made by another agent,
// each read through TurnsFromHistory with an OwnerIndex. STRUCTURE FROM THE
// REAL ROWS (attachment_error beside a cron completion; a delivery-mirror
// whose text is the bare filename; a reply ending in a MEDIA: line), VALUES
// INVENTED — the repository is public.
[Collection("Settings")]
public class OpenClawOwnerSessionHistoryTests
{
    private const string Ask = "agent:main:discord:direct:1";
    private const string Owner = "agent:comfyui:discord:direct:1";

    private const string Other = "/Users/sample/.openclaw/workspace-sample-zara/outputs/sample";
    private const string Own = "/Users/sample/.openclaw/workspace/outputs";

    private static readonly OwnerIndex Index = new(
        new Dictionary<string, string>
        {
            ["main"] = "/Users/sample/.openclaw/workspace",
            ["comfyui"] = "/Users/sample/.openclaw/workspace-sample-zara",
        },
        new[] { Ask, Owner });

    // A row on the same page that names the file by absolute path — the shape
    // of the inter-session message that carries it in real transcripts.
    private static string PathRow(string path) =>
        "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"sent " + path + " to you\"}]}";

    private static string Failed(string name) => """
    {"role":"assistant","content":[
      {"type":"text","text":"a little clip"},
      {"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"NAME","mimeType":"video/mp4"}}],
     "provider":"openclaw","model":"automation-result","timestamp":1700000000001,
     "openclawAutomation":{"kind":"cron","jobId":"0a1b2c3d-1111-2222-3333-444455556666","runId":"r"}}
    """.Replace("NAME", name);

    private static string Mirror(string name) =>
        "{\"role\":\"assistant\",\"provider\":\"openclaw\",\"model\":\"delivery-mirror\","
        + "\"content\":[{\"type\":\"text\",\"text\":\"Here's the clip\\n" + name + "\"}]}";

    private static string MediaReply(string path) =>
        "{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Here you go\\nMEDIA:" + path + "\"}]}";

    private static List<HistoryTurn> Read(OwnerIndex? index, params string[] rows) =>
        OpenClawSessions.TurnsFromHistory(
            JsonDocument.Parse("[" + string.Join(",", rows) + "]").RootElement, Ask, index);

    private static HistoryTurn Clip(List<HistoryTurn> turns) =>
        Assert.Single(turns, t => t.ImageSourcePath is not null);

    // ---- another agent's workspace: the owner's key ----------------------

    [Fact]
    public void AFailedAttachmentWhosePathIsInAnotherWorkspaceIsAskedUnderTheOwner()
    {
        var path = Other + "/clip_a.mp4";
        var turn = Clip(Read(Index, PathRow(path), Failed("clip_a.mp4")));

        Assert.Equal(path, turn.ImageSourcePath);
        Assert.Equal(new OpenClawMediaSource(path, Owner).Route, turn.ImageUrl);
        Assert.DoesNotContain(System.Uri.EscapeDataString(Ask), turn.ImageUrl);
    }

    [Fact]
    public void ADeliveryMirrorWhosePathIsInAnotherWorkspaceIsAskedUnderTheOwner()
    {
        var path = Other + "/clip_b.mp4";
        var turn = Clip(Read(Index, PathRow(path), Mirror("clip_b.mp4")));

        Assert.Equal(path, turn.ImageSourcePath);
        Assert.Equal(new OpenClawMediaSource(path, Owner).Route, turn.ImageUrl);
    }

    [Fact]
    public void AMediaReplyWhosePathIsInAnotherWorkspaceIsAskedUnderTheOwnerAtHighConfidence()
    {
        var path = Other + "/clip_c.mp4";
        var turn = Clip(Read(Index, MediaReply(path)));

        Assert.Equal(path, turn.ImageSourcePath);
        Assert.Equal(new OpenClawMediaSource(path, Owner).Route, turn.ImageUrl);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // ---- controls: nothing that works today changes ----------------------

    [Fact]
    public void ThePathsInTheAskersOwnWorkspaceKeepTheAskingKey()
    {
        var a = Own + "/clip_a.mp4";
        var b = Own + "/clip_b.mp4";
        var c = Own + "/clip_c.mp4";

        Assert.Equal(new OpenClawMediaSource(a, Ask).Route, Clip(Read(Index, PathRow(a), Failed("clip_a.mp4"))).ImageUrl);
        Assert.Equal(new OpenClawMediaSource(b, Ask).Route, Clip(Read(Index, PathRow(b), Mirror("clip_b.mp4"))).ImageUrl);
        Assert.Equal(new OpenClawMediaSource(c, Ask).Route, Clip(Read(Index, MediaReply(c))).ImageUrl);
    }

    [Fact]
    public void WithNoIndexEveryRouteIsExactlyWhatItWasBefore()
    {
        var a = Other + "/clip_a.mp4";
        var b = Other + "/clip_b.mp4";
        var c = Other + "/clip_c.mp4";

        foreach (var index in new OwnerIndex?[] { null, OwnerIndex.Empty })
        {
            Assert.Equal(new OpenClawMediaSource(a, Ask).Route, Clip(Read(index, PathRow(a), Failed("clip_a.mp4"))).ImageUrl);
            Assert.Equal(new OpenClawMediaSource(b, Ask).Route, Clip(Read(index, PathRow(b), Mirror("clip_b.mp4"))).ImageUrl);
            Assert.Equal(new OpenClawMediaSource(c, Ask).Route, Clip(Read(index, MediaReply(c))).ImageUrl);
        }
    }

    [Fact]
    public void ANoSessionPageStaysWithoutASessionEvenWithAnIndex()
    {
        var path = Other + "/clip_c.mp4";
        var turns = OpenClawSessions.TurnsFromHistory(
            JsonDocument.Parse("[" + MediaReply(path) + "]").RootElement, null, Index);

        Assert.Equal(new OpenClawMediaSource(path, null).Route, Clip(turns).ImageUrl);
    }
}

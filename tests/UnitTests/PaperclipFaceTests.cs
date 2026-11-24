using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// CB-445: where the drawing code finds a Paperclip agent's face.
[Collection("Settings")]
public sealed class PaperclipFaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb445-face-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        PaperclipSessions.SetSnapshotForTests([]);
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private string Picture()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(f, PaperclipPicturesTests.Png);
        return f;
    }

    private static PaperclipAgent Agent(string id, string? file) =>
        new(id, "Atlas", "r", "t", "idle", null, "x", "idle", PaperclipMark.None, null, [], null, null, null, null, null,
            DateTime.UtcNow, "u", false, AvatarFile: file);

    [Fact]
    public void NullOrMissingIsNoFace()
    {
        Assert.Null(PaperclipAvatars.For(null));
        Assert.Null(PaperclipAvatars.For(""));
        Assert.Null(PaperclipAvatars.For(Path.Combine(_dir, "nothing-" + Guid.NewGuid() + ".png")));
        Assert.StartsWith("paperclip:", PaperclipAvatars.Key("/x.png"));
    }

    [Fact]
    public void AnAgentWithNoPictureOrNotPublished_IsUnknown_SoTheHeaderBorrowsTheOrbsLetters()
    {
        var id = Guid.NewGuid().ToString();
        PaperclipSessions.SetSnapshotForTests([Agent(id, null)]);
        Assert.Same(SessionIdentity.Unknown, SessionIdentity.For(PaperclipScan.KeyFor(id)));
        Assert.Same(SessionIdentity.Unknown, SessionIdentity.For(PaperclipScan.KeyFor("someone-else")));
    }

    [Fact]
    public void TheScanCarriesTheFileOntoTheOrbsStatus_AndAParkedRowKeepsItsFace()
    {
        var file = Picture();
        var rows = new PaperclipScan().Entries([Agent("a", file)], PaperclipLink.Connected, "ok", "show", DateTime.UtcNow);
        Assert.Equal(file, Assert.Single(rows).Status.PaperclipAvatarFile);
        var parked = new PaperclipScan().Entries([Agent("a", file)], PaperclipLink.Unreachable, "down", "show", DateTime.UtcNow, remembered: true);
        Assert.Equal(file, Assert.Single(parked).Status.PaperclipAvatarFile);
    }
}

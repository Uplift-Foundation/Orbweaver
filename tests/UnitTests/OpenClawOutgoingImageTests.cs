using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4: a picture sent with a native session's message. Measured on
// 2026.10.5: real content is accepted and an empty payload is refused, so the
// builder never offers an empty one and only offers an image type.
public class OpenClawOutgoingImageTests
{
    [Theory]
    [InlineData("shot.png", "image/png")]
    [InlineData("shot.PNG", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("anim.gif", "image/gif")]
    [InlineData("pic.webp", "image/webp")]
    public void AnImageTypeIsRecognisedByItsExtension(string name, string mime)
    {
        var image = OpenClawOutgoingImage.From(name, new byte[] { 1, 2, 3 })!;

        Assert.Equal(mime, image.MimeType);
        Assert.Equal(Path.GetFileName(name), image.FileName);
        Assert.Equal("AQID", image.Base64);
    }

    // Only the file name goes to the gateway, never the folder it was saved in.
    [Fact]
    public void OnlyTheFileNameIsKeptNotTheFolder()
    {
        var image = OpenClawOutgoingImage.From("/tmp/some/folder/shot.png", new byte[] { 1 })!;

        Assert.Equal("shot.png", image.FileName);
    }

    // Empty content is what the gateway refuses, so it is never built.
    [Fact]
    public void AnEmptyFileIsNotAnAttachment() =>
        Assert.Null(OpenClawOutgoingImage.From("shot.png", Array.Empty<byte>()));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("")]
    public void ANonImageIsNotAnAttachment(string name) =>
        Assert.Null(OpenClawOutgoingImage.From(name, new byte[] { 1 }));

    [Fact]
    public void TheWireFormIsTheMeasuredShapeInOrder()
    {
        var wire = OpenClawOutgoingImage.From("p.png", new byte[] { 1, 2, 3 })!.ToWire();

        Assert.Equal(new[] { "type", "mimeType", "fileName", "content" }, wire.Keys.ToArray());
        Assert.Equal("image", wire["type"]);
    }

    // One over the largest frame the gateway accepts would be dropped without an
    // answer; the headroom covers the rest of the request.
    [Fact]
    public void WhetherAMessageFitsTheConnectionCountsTheEncodedSize()
    {
        var image = OpenClawOutgoingImage.From("p.png", new byte[3000])!;
        var size = image.Base64.Length;

        Assert.True(OpenClawOutgoingImage.Fits(new[] { image }, size + 4096));
        Assert.False(OpenClawOutgoingImage.Fits(new[] { image }, size + 4095));
        Assert.True(OpenClawOutgoingImage.Fits(Array.Empty<OpenClawOutgoingImage>(), 4096));
    }
}

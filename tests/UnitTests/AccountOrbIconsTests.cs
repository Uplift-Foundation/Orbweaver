using System.Text;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-287: the icon store — import, validate, load, and above all the SVG
// import's refusal to reach outside the document.
public class AccountOrbIconsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orb287i-" + Guid.NewGuid().ToString("N"));
    private string Icons => Path.Combine(_root, "icons");

    public AccountOrbIconsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] Encode(int w, int h, SKColor left, SKColor right, SKEncodedImageFormat fmt)
    {
        using var bmp = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) bmp.SetPixel(x, y, x < w / 2 ? left : right);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(fmt, 100).ToArray();
    }

    private static byte[] Png(SKColor c) => Encode(40, 20, c, c, SKEncodedImageFormat.Png);

    private string WriteSvg(string body, string name = "i.svg") =>
        Write(name, Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"20\" viewBox=\"0 0 20 20\">"
            + body + "</svg>"));

    private SKColor CentrePixel(string fileName)
    {
        using var bmp = SKBitmap.Decode(File.ReadAllBytes(Path.Combine(Icons, fileName)));
        Assert.Equal(AccountOrbIcons.Size, bmp.Width);
        Assert.Equal(AccountOrbIcons.Size, bmp.Height);
        return bmp.GetPixel(128, 128);
    }

    [Theory]
    [InlineData("0123456789ab-0123456789abcdef.png", true)]
    [InlineData("../0123456789ab-0123456789abcdef.png", false)]
    [InlineData("/abs/0123456789ab-0123456789abcdef.png", false)]
    [InlineData("sub/0123456789ab-0123456789abcdef.png", false)]
    [InlineData("sub\\0123456789ab-0123456789abcdef.png", false)]
    [InlineData("0123456789AB-0123456789abcdef.png", false)]
    [InlineData("0123456789ab-0123456789abcdef.jpg", false)]
    [InlineData("", false)]
    public void IsSafeFileName(string name, bool want) =>
        Assert.Equal(want, AccountOrbIcons.IsSafeFileName(name));

    [Fact]
    public void Import_Png_Gives256SquarePng()
    {
        var r = AccountOrbIcons.Import(Write("a.png", Png(new SKColor(255, 0, 0))), "account:~", Icons);
        Assert.Null(r.Error);
        Assert.True(AccountOrbIcons.IsSafeFileName(r.FileName));
        Assert.Equal(new SKColor(255, 0, 0), CentrePixel(r.FileName!));
    }

    [Fact]
    public void Import_Jpeg_Gives256SquarePng()
    {
        var jpg = Encode(30, 60, new SKColor(0, 0, 255), new SKColor(0, 0, 255), SKEncodedImageFormat.Jpeg);
        var r = AccountOrbIcons.Import(Write("a.jpg", jpg), "account:~", Icons);
        Assert.Null(r.Error);
        var c = CentrePixel(r.FileName!);
        Assert.True(c.Blue > 200 && c.Red < 60);
    }

    [Fact]
    public void Import_WideImage_IsCoverFitted()
    {
        // Left half red, right half blue, 40x20: cover-fit crops the sides but
        // keeps the split at the centre, so the two outer columns keep their colours.
        var r = AccountOrbIcons.Import(
            Write("w.png", Encode(40, 20, new SKColor(255, 0, 0), new SKColor(0, 0, 255), SKEncodedImageFormat.Png)),
            "k", Icons);
        using var bmp = SKBitmap.Decode(File.ReadAllBytes(Path.Combine(Icons, r.FileName!)));
        Assert.Equal(255, bmp.GetPixel(10, 128).Red);
        Assert.Equal(255, bmp.GetPixel(245, 128).Blue);
    }

    [Fact]
    public void Import_Svg_Rasterises()
    {
        var r = AccountOrbIcons.Import(
            WriteSvg("<rect width=\"20\" height=\"20\" fill=\"#00ff00\"/>"), "account:~", Icons);
        Assert.Null(r.Error);
        var c = CentrePixel(r.FileName!);
        Assert.True(c.Green > 200 && c.Red < 40);
    }

    [Fact]
    public void Import_Svg_InternalGradientReference_IsAllowed()
    {
        var r = AccountOrbIcons.Import(WriteSvg(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#f00\"/></linearGradient></defs>"
            + "<rect width=\"20\" height=\"20\" fill=\"url('#g')\"/><use xlink:href=\"#g\"/>"), "k", Icons);
        Assert.Null(r.Error);
    }

    // The negative control for "no network and no local-file reads": a real PNG
    // sits on disk and the SVG points at it. The import must refuse, and the
    // paired control below shows the same SVG shape imports when the picture is
    // embedded — so the refusal is about the reference, not about <image>.
    [Fact]
    public void Import_SvgWithExternalImage_IsRefused_AndNothingIsWritten()
    {
        var secret = Write("secret.png", Png(new SKColor(255, 0, 255)));
        var r = AccountOrbIcons.Import(
            WriteSvg($"<image width=\"20\" height=\"20\" href=\"file://{secret}\"/>"), "k", Icons);
        Assert.Equal(IconImportError.NotAnImage, r.Error);
        Assert.Null(r.FileName);
        Assert.False(Directory.Exists(Icons) && Directory.GetFiles(Icons).Length > 0);
    }

    [Fact]
    public void Import_SvgWithEmbeddedImage_IsAllowed_PairedControl()
    {
        var b64 = Convert.ToBase64String(Png(new SKColor(255, 0, 255)));
        var r = AccountOrbIcons.Import(
            WriteSvg($"<image width=\"20\" height=\"20\" href=\"data:image/png;base64,{b64}\"/>"), "k", Icons);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData("<image width=\"20\" height=\"20\" xlink:href=\"http://example.invalid/x.png\"/>")]
    [InlineData("<image width=\"20\" height=\"20\" href=\"https://example.invalid/x.png\"/>")]
    [InlineData("<image width=\"20\" height=\"20\" href=\"x.png\"/>")]
    [InlineData("<rect width=\"20\" height=\"20\" fill=\"url(http://example.invalid/g)\"/>")]
    [InlineData("<rect width=\"20\" height=\"20\" style=\"fill:url('x.svg#g')\"/>")]
    [InlineData("<style>@import url(http://example.invalid/a.css);</style><rect width=\"20\" height=\"20\"/>")]
    [InlineData("<style>rect{fill:url(http://example.invalid/g)}</style><rect width=\"20\" height=\"20\"/>")]
    [InlineData("<style><![CDATA[rect{fill:url(http://example.invalid/g)}]]></style><rect width=\"20\" height=\"20\"/>")]
    public void Import_SvgReachingOutside_IsRefused(string body)
    {
        var r = AccountOrbIcons.Import(WriteSvg(body), "k", Icons);
        Assert.Equal(IconImportError.NotAnImage, r.Error);
    }

    [Fact]
    public void Harden_TurnsOffEveryExternalSwitch()
    {
        Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.Local | Svg.ExternalType.Remote;
        Svg.SvgDocument.DisableDtdProcessing = false;
        AccountOrbIcons.Harden();
        Assert.Equal(Svg.ExternalType.None, Svg.SvgDocument.ResolveExternalImages);
        Assert.Equal(Svg.ExternalType.None, Svg.SvgDocument.ResolveExternalElements);
        Assert.Equal(Svg.ExternalType.None, Svg.SvgDocument.ResolveExternalXmlEntites);
        Assert.True(Svg.SvgDocument.DisableDtdProcessing);
    }

    [Fact]
    public void Import_SvgWithHugeDimensions_StillGives256Square()
    {
        var p = Write("h.svg", Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000000\" height=\"1000000\" viewBox=\"0 0 20 20\"><rect width=\"20\" height=\"20\" fill=\"#0f0\"/></svg>"));
        var r = AccountOrbIcons.Import(p, "k", Icons);
        Assert.Null(r.Error);
        Assert.True(CentrePixel(r.FileName!).Green > 200);
    }

    [Fact]
    public void Import_SvgWithDoctype_IsRefused()
    {
        var p = Write("d.svg", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/hosts\">]>"
            + "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\"><text>&x;</text></svg>"));
        Assert.Equal(IconImportError.NotAnImage, AccountOrbIcons.Import(p, "k", Icons).Error);
    }

    [Fact]
    public void Import_SvgThatIsNotXml_Refused()
    {
        var p = Write("b.svg", Encoding.UTF8.GetBytes("<svg><unclosed"));
        Assert.Equal(IconImportError.NotAnImage, AccountOrbIcons.Import(p, "k", Icons).Error);
    }

    [Fact]
    public void Import_SvgExtensionButNoSvgRoot_NotAnImage()
    {
        var p = Write("t.svg", Encoding.UTF8.GetBytes("just words"));
        Assert.Equal(IconImportError.NotAnImage, AccountOrbIcons.Import(p, "k", Icons).Error);
    }

    [Fact]
    public void Import_TextRenamedPng_NotAnImage()
    {
        var p = Write("t.png", Encoding.UTF8.GetBytes("not a picture"));
        Assert.Equal(IconImportError.NotAnImage, AccountOrbIcons.Import(p, "k", Icons).Error);
    }

    [Fact]
    public void Import_PngHeaderWithGarbage_NotAnImage()
    {
        var bytes = new byte[100];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        Assert.Equal(IconImportError.NotAnImage, AccountOrbIcons.Import(Write("g.png", bytes), "k", Icons).Error);
    }

    [Fact]
    public void Import_Webp_UnsupportedFormat()
    {
        var webp = Encode(8, 8, SKColors.Red, SKColors.Red, SKEncodedImageFormat.Webp);
        Assert.Equal(SKEncodedImageFormat.Webp, SKCodec.Create(new MemoryStream(webp))?.EncodedFormat);
        Assert.Equal(IconImportError.UnsupportedFormat, AccountOrbIcons.Import(Write("g.webp", webp), "k", Icons).Error);
    }

    [Fact]
    public void Import_Empty_Refused() =>
        Assert.Equal(IconImportError.Empty, AccountOrbIcons.Import(Write("e.png", []), "k", Icons).Error);

    [Fact]
    public void Import_MissingSource_Unreadable() =>
        Assert.Equal(IconImportError.Unreadable,
            AccountOrbIcons.Import(Path.Combine(_root, "nope.png"), "k", Icons).Error);

    [Fact]
    public void Import_IconsDirUnderAFile_Unreadable()
    {
        var blocker = Write("blocker", [1]);
        var r = AccountOrbIcons.Import(Write("a.png", Png(SKColors.Red)), "k", Path.Combine(blocker, "icons"));
        Assert.Equal(IconImportError.Unreadable, r.Error);
    }

    [Fact]
    public void Import_AtCap_Accepted_OverCap_Refused()
    {
        var path = Path.Combine(_root, "big.png");
        using (var fs = File.Create(path)) fs.SetLength(PersonaFiles.MaxAvatarBytes);
        // At the cap it is read and then judged on content (zeros are no image).
        Assert.Equal(IconImportError.NotAnImage, AccountOrbIcons.Import(path, "k", Icons).Error);
        using (var fs = File.OpenWrite(path)) fs.SetLength(PersonaFiles.MaxAvatarBytes + 1);
        Assert.Equal(IconImportError.TooLarge, AccountOrbIcons.Import(path, "k", Icons).Error);
    }

    [Fact]
    public void Import_SameBytesSameKey_SameName_DifferentKeyDifferentName()
    {
        var src = Write("a.png", Png(SKColors.Red));
        var a = AccountOrbIcons.Import(src, "account:~", Icons).FileName;
        var again = AccountOrbIcons.Import(src, "ACCOUNT:~", Icons).FileName;
        var other = AccountOrbIcons.Import(src, "account:/x", Icons).FileName;
        Assert.Equal(a, again);
        Assert.NotEqual(a, other);
        Assert.Equal(2, Directory.GetFiles(Icons).Length);
    }

    [Fact]
    public void Load_Outcomes()
    {
        Assert.Equal(IconLoadOutcome.None, AccountOrbIcons.Load(Icons, null).Outcome);
        Assert.Equal(IconLoadOutcome.None, AccountOrbIcons.Load(Icons, "").Outcome);
        Assert.Equal(IconLoadOutcome.UnsafeName, AccountOrbIcons.Load(Icons, "../x.png").Outcome);
        Assert.Equal(IconLoadOutcome.Missing, AccountOrbIcons.Load(Icons, "0123456789ab-0123456789abcdef.png").Outcome);

        var good = AccountOrbIcons.Import(Write("a.png", Png(SKColors.Red)), "k", Icons).FileName!;
        var loaded = AccountOrbIcons.Load(Icons, good);
        Assert.Equal(IconLoadOutcome.Loaded, loaded.Outcome);
        Assert.NotEmpty(loaded.Png!);

        File.WriteAllBytes(Path.Combine(Icons, good), [1, 2, 3]);
        Assert.Equal(IconLoadOutcome.Corrupt, AccountOrbIcons.Load(Icons, good).Outcome);
    }

    [Fact]
    public void Load_UnsafeName_NeverTouchesDisk()
    {
        // A directory that does not exist and a traversal name: the answer is
        // UnsafeName, not Missing, so the name was refused before any lookup.
        Assert.Equal(IconLoadOutcome.UnsafeName,
            AccountOrbIcons.Load(Path.Combine(_root, "no-such-dir"), Path.Combine("..", "..", "etc", "passwd")).Outcome);
    }

    [Fact]
    public void Delete_RemovesMintedName_RefusesOthers()
    {
        Directory.CreateDirectory(Icons);
        var keep = Path.Combine(Icons, "keep.png");
        File.WriteAllBytes(keep, [1]);
        AccountOrbIcons.Delete(Icons, "keep.png");
        AccountOrbIcons.Delete(Icons, null);
        Assert.True(File.Exists(keep));

        var name = AccountOrbIcons.Import(Write("a.png", Png(SKColors.Red)), "k", Icons).FileName!;
        AccountOrbIcons.Delete(Icons, name);
        Assert.False(File.Exists(Path.Combine(Icons, name)));
        AccountOrbIcons.Delete(Icons, name); // already gone: no throw
    }
}

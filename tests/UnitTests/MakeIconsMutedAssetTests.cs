using System.IO.Compression;
using System.Runtime.CompilerServices;
using Xunit;

namespace Orbweaver.Tests;

// CB-298: the muted tray PNGs are what make-icons.py wrote, and they keep the
// one property TrayController.Tinted depends on — every pixel with any alpha
// carries exactly the state's RGB — so the runtime re-tint reproduces the
// muted icon, slash and all. Decoded by hand (zlib + filter type 0, which is
// all make-icons.py writes) so the unit suite needs no Skia.
public class MakeIconsMutedAssetTests
{
    public static IEnumerable<object[]> States() =>
        new[] { "idle", "generating", "waiting" }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(States))]
    public void TheMutedIconIs64SquareAndSharesItsSiblingsColour(string state)
    {
        var plain = Decode($"tray-{state}.png");
        var muted = Decode($"tray-{state}-muted.png");

        Assert.Equal((64, 64), (muted.Width, muted.Height));
        Assert.Equal(plain.Colour, muted.Colour);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void TheSlashIsThere(string state)
    {
        var plain = Decode($"tray-{state}.png");
        var muted = Decode($"tray-{state}-muted.png");

        // The bar adds opaque pixels where the ring's centre-hole faint core
        // was, and the gap removes some from the ring: the two differ.
        Assert.NotEqual(plain.Alpha, muted.Alpha);
        // The middle of the bar is fully opaque; the plain icon's middle is
        // only the faint core.
        Assert.Equal(255, muted.Alpha[32 * 64 + 32]);
        Assert.True(plain.Alpha[32 * 64 + 32] < 255);
    }

    private sealed record Image(int Width, int Height, byte[] Alpha, (byte R, byte G, byte B) Colour);

    private static Image Decode(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "Assets", name);
        Assert.True(File.Exists(path), $"{path} does not exist");
        var bytes = File.ReadAllBytes(path);

        int w = 0, h = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos < bytes.Length)
        {
            var len = (bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
            var tag = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (tag == "IHDR")
            {
                w = (bytes[pos + 8] << 24) | (bytes[pos + 9] << 16) | (bytes[pos + 10] << 8) | bytes[pos + 11];
                h = (bytes[pos + 12] << 24) | (bytes[pos + 13] << 16) | (bytes[pos + 14] << 8) | bytes[pos + 15];
            }
            else if (tag == "IDAT") idat.Write(bytes, pos + 8, len);
            pos += 12 + len;
        }

        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var data = raw.ToArray();

        var alpha = new byte[w * h];
        (byte, byte, byte)? colour = null;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * (w * 4 + 1) + 1 + x * 4;   // +1 skips the filter byte
            alpha[y * w + x] = data[i + 3];
            if (data[i + 3] == 0) continue;
            var px = (data[i], data[i + 1], data[i + 2]);
            colour ??= px;
            Assert.Equal(colour.Value, px);        // one colour across every visible pixel
        }

        return new Image(w, h, alpha, colour ?? (0, 0, 0));
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);
        while (directory is not null && !File.Exists(Path.Combine(directory, "Orbweaver.csproj")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("no Orbweaver.csproj above " + thisFile);
    }
}

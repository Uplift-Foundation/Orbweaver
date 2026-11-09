namespace Orbweaver.Tests;

/// <summary>
/// CB-321: a real animated GIF, written here because Skia decodes GIF but
/// cannot encode one, and the repository tracks no GIF to test against. GIF89a
/// with a four-colour global table, one graphic-control block per frame (the
/// delay, in centiseconds) and an LZW stream that never compresses: a clear
/// code before every two pixels keeps the code width at three bits, so the
/// encoder is twenty lines and the decoder on the far end is Skia's own.
/// </summary>
internal static class TinyGif
{
    // frames[i][y * w + x] is a palette index 0..3; delays are centiseconds.
    // With patch set, every frame after the first is only a patch-by-patch
    // square at the origin, drawn over what came before: a delta frame, which
    // Skia can only decode by compositing onto the frame it requires.
    internal static byte[] Write(int w, int h, IReadOnlyList<byte[]> frames, IReadOnlyList<int> delays, int patch = 0)
    {
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
        o.AddRange("GIF89a"u8.ToArray());
        U16(w); U16(h);
        o.AddRange([0xF1, 0, 0]);                                  // global table, 4 entries
        o.AddRange([0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]);
        o.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 0x03, 0x01, 0x00, 0x00, 0x00]);   // loop for ever
        for (var f = 0; f < frames.Count; f++)
        {
            o.AddRange([0x21, 0xF9, 0x04, 0x00]);
            U16(delays[f]);
            o.AddRange([0x00, 0x00]);
            var (fw, fh) = patch > 0 && f > 0 ? (patch, patch) : (w, h);
            o.Add(0x2C); U16(0); U16(0); U16(fw); U16(fh); o.Add(0);
            o.Add(2);                                              // LZW minimum code size
            var data = Lzw(frames[f][..(fw * fh)]);
            for (var i = 0; i < data.Count; i += 255)
            {
                var n = Math.Min(255, data.Count - i);
                o.Add((byte)n);
                o.AddRange(data.GetRange(i, n));
            }
            o.Add(0);
        }
        o.Add(0x3B);
        return o.ToArray();
    }

    private static List<byte> Lzw(byte[] pixels)
    {
        var bytes = new List<byte>();
        int acc = 0, bits = 0;
        void Code(int c)
        {
            acc |= c << bits;
            bits += 3;
            while (bits >= 8) { bytes.Add((byte)acc); acc >>= 8; bits -= 8; }
        }
        for (var i = 0; i < pixels.Length; i++)
        {
            if (i % 2 == 0) Code(4);                               // clear
            Code(pixels[i]);
        }
        Code(5);                                                   // end of information
        if (bits > 0) bytes.Add((byte)acc);
        return bytes;
    }

    // A frame of one colour, and one of a checkerboard, for telling frames apart.
    internal static byte[] Solid(int w, int h, byte colour) => Enumerable.Repeat(colour, w * h).ToArray();
}

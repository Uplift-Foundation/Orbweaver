using System.Security.Cryptography;
using SkiaSharp;

namespace Orbweaver
{
    // A persona picture as the frames a phone draws (CB-321, frames-v5
    // persona_image_window): the source decoded the way the desktop decodes its
    // own orb, scaled to at most 216 px a side, and each frame encoded as a
    // still of at most 32,768 bytes. Never the file itself — CB-322 is what a
    // multi-megabyte GIF sent whole does to a link — and never a frame or a
    // total the contract does not allow, so what the wire carries for one
    // persona is bounded on every axis before anything is sent.
    //
    // Built off the scan and off the link (PersonaImageStore does the
    // scheduling); a bundle is immutable once built and its windows are chunked
    // once, so a second phone or a reconnect costs no re-encode.
    internal sealed class PersonaImageBundle
    {
        // A 72-point disc at 3x (frames-v5 limits_rules DECISION).
        internal const int DefaultSide = 216;

        // The quality ladder, tried in order at each side before the side is
        // halved. WebP and JPEG take it; PNG ignores it.
        internal static readonly int[] Qualities = [80, 60, 40];

        internal string Hash { get; }
        internal int W { get; }
        internal int H { get; }
        internal IReadOnlyList<PersonaFrames.Frame> Frames { get; }
        internal long Bytes { get; }

        private IReadOnlyList<PersonaFrames.ImageWindow>? _windows;

        internal PersonaImageBundle(string hash, int w, int h, IReadOnlyList<PersonaFrames.Frame> frames)
        {
            Hash = hash;
            W = w;
            H = h;
            Frames = frames;
            Bytes = frames.Sum(f => (long)f.Data.Length);
        }

        // The windows, chunked once on first ask.
        internal IReadOnlyList<PersonaFrames.ImageWindow> Windows => _windows ??= PersonaFrames.Chunk(Hash, W, H, Frames);

        internal static string HashOf(byte[] source) => Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();

        // An encoder for one format at one quality: Skia's, or a test's.
        internal delegate byte[]? Encoder(SKBitmap bitmap, SKEncodedImageFormat format, int quality);

        // Null when the source is over the cap, is not a picture Skia reads, or
        // cannot be brought under the budget at any rung of the ladder — and a
        // null is the phone drawing the initial disc, which is the contract's
        // answer for every one of those.
        internal static PersonaImageBundle? Build(byte[] source, int side = DefaultSide, Encoder? encode = null)
        {
            if (source.Length == 0 || source.Length > PersonaFrames.MaxSourceBytes) return null;
            var decoded = Decode(source, side);
            if (decoded is null) return null;
            try { return Fit(HashOf(source), decoded.Value.Bitmaps, decoded.Value.Ms, encode ?? SkiaEncode); }
            finally { foreach (var b in decoded.Value.Bitmaps) b.Dispose(); }
        }

        // The size a frame is drawn at: the source's aspect, its longer side at
        // `side` (never enlarged past the source), and both sides kept within
        // 16..256.
        internal static (int W, int H) Target(int sourceW, int sourceH, int side)
        {
            var longer = Math.Max(sourceW, sourceH);
            var goal = Math.Clamp(Math.Min(side, longer), PersonaFrames.MinSide, PersonaFrames.MaxSide);
            var scale = (double)goal / longer;
            return (Math.Clamp((int)Math.Round(sourceW * scale), PersonaFrames.MinSide, PersonaFrames.MaxSide),
                    Math.Clamp((int)Math.Round(sourceH * scale), PersonaFrames.MinSide, PersonaFrames.MaxSide));
        }

        // OpenClawAvatars.DecodeFrames' walk — SKCodec, GIF deltas composited on
        // one canvas, at most 120 frames — scaling each frame down as it is taken
        // so what is held is 216 px frames, not the source's full size times
        // 120. Delays are the sent ones (PersonaFrames.SentMs).
        private static (List<SKBitmap> Bitmaps, List<int> Ms)? Decode(byte[] source, int side)
        {
            using var data = SKData.CreateCopy(source);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;

            var count = Math.Min(Math.Max(1, codec.FrameCount), PersonaFrames.MaxFrames);
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var (w, h) = Target(info.Width, info.Height, side);
            var target = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var canvas = new SKBitmap(info);
            var bitmaps = new List<SKBitmap>();
            var raw = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var options = new SKCodecOptions(i);
                if (codec.FrameCount > 0 && i > 0 && codec.FrameInfo[i].RequiredFrame is var required and >= 0)
                    options = new SKCodecOptions(i, required);
                var result = codec.GetPixels(info, canvas.GetPixels(), info.RowBytes, options);
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) break;
                bitmaps.Add(canvas.Resize(target, SKFilterQuality.High));
                raw.Add(codec.FrameCount > 0 ? codec.FrameInfo[i].Duration : 0);
            }
            if (bitmaps.Count == 0) return null;
            return (bitmaps, raw.Select(ms => PersonaFrames.SentMs(ms, bitmaps.Count)).ToList());
        }

        // The reduction ladder (frames-v5 limits_rules): at each side, for each
        // quality, encode every frame; a frame no codec fits moves on to the next
        // quality; a total over the budget drops every other frame until it fits
        // or one frame is left. Then the side halves, down to 16. What still does
        // not fit is not offered.
        internal static PersonaImageBundle? Fit(string hash, IReadOnlyList<SKBitmap> bitmaps, IReadOnlyList<int> ms, Encoder encode)
        {
            var current = bitmaps;
            var owned = new List<SKBitmap>();
            try
            {
                while (true)
                {
                    var w = current[0].Width;
                    var h = current[0].Height;
                    foreach (var quality in Qualities)
                    {
                        var frames = new List<PersonaFrames.Frame>();
                        foreach (var (bitmap, i) in current.Select((b, i) => (b, i)))
                        {
                            if (EncodeFrame(bitmap, quality, encode) is not { } bytes) break;
                            frames.Add(new PersonaFrames.Frame(ms[i], bytes));
                        }
                        if (frames.Count < current.Count) continue;
                        // Always fits by the end: a frame is at most 32 KB, so one
                        // frame is far under the 2 MB budget and decimation stops
                        // at the first count that fits.
                        while (frames.Sum(f => (long)f.Data.Length) > PersonaFrames.MaxImageBytes)
                            frames = Decimate(frames);
                        return new PersonaImageBundle(hash, w, h, frames);
                    }
                    if (w / 2 < PersonaFrames.MinSide || h / 2 < PersonaFrames.MinSide) return null;
                    var half = new SKImageInfo(w / 2, h / 2, SKColorType.Bgra8888, SKAlphaType.Premul);
                    var next = current.Select(b => b.Resize(half, SKFilterQuality.High)).ToList();
                    owned.AddRange(next);
                    current = next;
                }
            }
            finally { foreach (var b in owned) b.Dispose(); }
        }

        // Every other frame, the dropped one's delay added to the one kept before
        // it so the loop still takes as long; one frame left is a still.
        internal static List<PersonaFrames.Frame> Decimate(IReadOnlyList<PersonaFrames.Frame> frames)
        {
            var kept = new List<PersonaFrames.Frame>();
            for (var i = 0; i < frames.Count; i += 2)
            {
                var ms = frames[i].Ms + (i + 1 < frames.Count ? frames[i + 1].Ms : 0);
                kept.Add(frames[i] with { Ms = Math.Min(ms, PersonaFrames.MaxFrameMs) });
            }
            if (kept.Count == 1) kept[0] = kept[0] with { Ms = 0 };
            return kept;
        }

        // The codec ladder for one frame: WebP, else PNG, else JPEG at the rung's
        // quality (75 at the top rung, as the contract recommends). The first
        // that produces bytes under the frame cap wins; none is null.
        internal static byte[]? EncodeFrame(SKBitmap bitmap, int quality, Encoder encode)
        {
            foreach (var (format, q) in new[]
                     {
                         (SKEncodedImageFormat.Webp, quality),
                         (SKEncodedImageFormat.Png, 100),
                         (SKEncodedImageFormat.Jpeg, quality == Qualities[0] ? 75 : quality),
                     })
            {
                if (encode(bitmap, format, q) is { } bytes && PersonaFrames.FrameAllowed(bytes.Length)) return bytes;
            }
            return null;
        }

        internal static byte[]? SkiaEncode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
        {
            using var data = bitmap.Encode(format, quality);
            return data?.ToArray();
        }
    }
}

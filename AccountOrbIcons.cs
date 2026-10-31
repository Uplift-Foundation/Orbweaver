using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using SkiaSharp;

namespace Orbweaver
{
    internal enum IconLoadOutcome { None, UnsafeName, Missing, Corrupt, Loaded }

    internal sealed record IconLoad(IconLoadOutcome Outcome, byte[]? Png);

    // CB-287: the icon store for usage orbs — import a picked file into a small
    // normalised PNG in the data dir, and load it back for drawing.
    //
    // What is stored is a 256x256 PNG rendition, not a byte copy. The runtime
    // then decodes one small, known format and the SVG parser only ever runs
    // once, on a user action, never on the draw path.
    internal static class AccountOrbIcons
    {
        internal const int Size = 256;
        internal const string DirectoryName = "account-icons";

        // The only shape Import ever mints: 12 hex of the account key's hash,
        // a dash, 16 hex of the content hash, ".png". Anything else in
        // settings.json — "../../x", an absolute path, a backslash — is refused
        // before any disk access, so a hand-edited file cannot point the app at
        // an arbitrary file.
        private static readonly Regex Minted = new(
            @"^[0-9a-f]{12}-[0-9a-f]{16}\.png\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string IconsDirectory => Path.Combine(OrbweaverSettings.Directory, DirectoryName);

        internal static bool IsSafeFileName(string? fileName) =>
            fileName is not null && Minted.IsMatch(fileName);

        // None for no icon set; UnsafeName without touching the disk; Missing
        // and Corrupt are separate so a caller can tell "gone" from "damaged"
        // — both fall back to letters, neither is cached as permanent (CB-148).
        internal static IconLoad Load(string iconsDir, string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return new(IconLoadOutcome.None, null);
            if (!IsSafeFileName(fileName)) return new(IconLoadOutcome.UnsafeName, null);

            byte[] bytes;
            try
            {
                // No File.Exists first: ReadAllBytes already says "not there"
                // (FileNotFound and DirectoryNotFound are IOExceptions), and a
                // directory squatting on the name throws too, so every way the
                // file can be unreadable lands in the one arm below.
                bytes = File.ReadAllBytes(Path.Combine(iconsDir, fileName));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new(IconLoadOutcome.Missing, null);
            }

            using var bitmap = Decode(bytes, out _);
            return bitmap is null ? new(IconLoadOutcome.Corrupt, null) : new(IconLoadOutcome.Loaded, bytes);
        }

        internal static IconImport Import(
            string sourcePath, string positionKey, string iconsDir, int fullDecodeMaxPixels = FullDecodeMaxPixels, int hardMaxPixels = HardMaxPixels)
        {
            byte[] source;
            try
            {
                var length = new FileInfo(sourcePath).Length;
                if (length == 0) return new(null, IconImportError.Empty);
                if (length > PersonaFiles.MaxAvatarBytes) return new(null, IconImportError.TooLarge);
                source = File.ReadAllBytes(sourcePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new(null, IconImportError.Unreadable);
            }

            byte[]? png;
            var svgRefused = false;
            var tooLarge = false;
            if (IsPng(source) || IsJpeg(source)) png = Normalise(source, fullDecodeMaxPixels, hardMaxPixels, out tooLarge);
            else if (LooksLikeSvg(sourcePath, source)) png = RasteriseSvg(source, out svgRefused);
            else return new(null, Skia(source) ? IconImportError.UnsupportedFormat : IconImportError.NotAnImage);

            if (tooLarge) return new(null, IconImportError.ImageDimensionsTooLarge);
            if (png is null) return new(null, svgRefused ? IconImportError.SvgReferencesExternalContent : IconImportError.NotAnImage);

            var name = NameFor(positionKey, png);
            try
            {
                Directory.CreateDirectory(iconsDir);
                var final = Path.Combine(iconsDir, name);
                if (!File.Exists(final))
                {
                    // Temp then move: a crash never leaves a torn icon under a
                    // name settings may already point at.
                    var temp = Path.Combine(iconsDir, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllBytes(temp, png);
                    File.Move(temp, final, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new(null, IconImportError.Unreadable);
            }

            return new(name, null);
        }

        // Deletes an icon file by its settings name. Refuses anything that is
        // not a minted name, so a bad settings value can never delete a stray
        // file. Best-effort: a file still open elsewhere is left for later.
        internal static void Delete(string iconsDir, string? fileName)
        {
            if (!IsSafeFileName(fileName)) return;
            try { File.Delete(Path.Combine(iconsDir, fileName!)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        internal static string NameFor(string positionKey, byte[] png)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(positionKey.ToLowerInvariant())));
            var content = Convert.ToHexString(SHA256.HashData(png));
            return $"{key[..12].ToLowerInvariant()}-{content[..16].ToLowerInvariant()}.png";
        }

        private static bool IsPng(byte[] b) =>
            b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

        private static bool IsJpeg(byte[] b) =>
            b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        private static bool Skia(byte[] b)
        {
            using var codec = SKCodec.Create(new MemoryStream(b));
            return codec is not null;
        }

        // A picture is never decoded at its own size if that would be large: a
        // 16000x16000 PNG is a gigabyte of RGBA and a 100 MP camera JPEG under
        // the byte cap is a realistic pick. Past FullDecodeMaxPixels the codec
        // is read one scanline at a time and box-averaged down as it goes, so
        // only a row and the reduced result are ever resident. Either way the
        // decode must report complete: a truncated file hands back a partial
        // bitmap that looks like success, which is how a corrupt icon would
        // otherwise be stored.
        //
        // Measured limit of that: this Skia build's PNG codec does not
        // implement scanline decoding (StartScanlineDecode answers
        // Unimplemented; JPEG does implement it), so a PNG cannot be reduced
        // on the way in. A PNG past the reduction threshold is decoded whole
        // only up to HardMaxPixels (50 MP, about 200 MB transient, once, on a
        // user action) and refused as too large beyond that.
        internal const int FullDecodeMaxPixels = 16_000_000;
        internal const int HardMaxPixels = 50_000_000;

        private static SKBitmap? Decode(
            byte[] bytes, out bool tooLarge,
            int fullDecodeMaxPixels = FullDecodeMaxPixels, int hardMaxPixels = HardMaxPixels)
        {
            tooLarge = false;
            try
            {
                using var codec = SKCodec.Create(new MemoryStream(bytes));
                if (codec is null) return null;

                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                if (info.Width <= 0 || info.Height <= 0) return null;

                var pixels = (long)info.Width * info.Height;
                if (pixels > fullDecodeMaxPixels)
                {
                    var reduced = DecodeReduced(codec, info);
                    if (reduced is not null) return reduced;

                    // Reduction unavailable (PNG) or the file ran short.
                    if (pixels > hardMaxPixels) { tooLarge = true; return null; }
                }

                var bitmap = new SKBitmap(info);
                if (codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
                bitmap.Dispose();
                return null;
            }
            catch (Exception) { return null; }
        }

        private static SKBitmap? DecodeReduced(SKCodec codec, SKImageInfo info)
        {
            // Factor chosen so the short side stays at or above Size.
            var n = Math.Max(1, Math.Min(info.Width, info.Height) / Size);
            var dw = info.Width / n;
            var dh = info.Height / n;
            if (codec.StartScanlineDecode(info) != SKCodecResult.Success) return null;
            // From here the codec is committed to scanline mode; a short file
            // returns null and the caller refuses the pixel count it would
            // otherwise decode whole.

            var result = new SKBitmap(new SKImageInfo(dw, dh, SKColorType.Rgba8888, SKAlphaType.Premul));
            var row = System.Runtime.InteropServices.Marshal.AllocHGlobal(info.RowBytes);
            try
            {
                var line = new byte[info.RowBytes];
                var sums = new long[dw * 4];
                for (var y = 0; y < info.Height; y++)
                {
                    if (codec.GetScanlines(row, 1, info.RowBytes) != 1) { result.Dispose(); return null; }

                    var dy = y / n;
                    if (dy >= dh) continue;

                    System.Runtime.InteropServices.Marshal.Copy(row, line, 0, info.RowBytes);
                    for (var x = 0; x < dw * n; x++)
                    {
                        var d = x / n * 4;
                        sums[d] += line[x * 4];
                        sums[d + 1] += line[x * 4 + 1];
                        sums[d + 2] += line[x * 4 + 2];
                        sums[d + 3] += line[x * 4 + 3];
                    }

                    if ((y + 1) % n != 0) continue;

                    var count = (long)n * n;
                    for (var x = 0; x < dw; x++)
                    {
                        result.SetPixel(x, dy, new SKColor(
                            (byte)(sums[x * 4] / count), (byte)(sums[x * 4 + 1] / count),
                            (byte)(sums[x * 4 + 2] / count), (byte)(sums[x * 4 + 3] / count)));
                    }
                    Array.Clear(sums);
                }
                return result;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(row); }
        }

        private static byte[]? Normalise(byte[] source, int fullDecodeMaxPixels, int hardMaxPixels, out bool tooLarge)
        {
            using var bitmap = Decode(source, out tooLarge, fullDecodeMaxPixels, hardMaxPixels);
            if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0) return null;

            return Render(canvas =>
            {
                // Cover-fit: scale to fill the square, centre, crop the rest —
                // the same way a persona picture is drawn.
                var scale = Math.Max((float)Size / bitmap.Width, (float)Size / bitmap.Height);
                var w = bitmap.Width * scale;
                var h = bitmap.Height * scale;
                using var image = SKImage.FromBitmap(bitmap);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(image, new SKRect((Size - w) / 2, (Size - h) / 2, (Size + w) / 2, (Size + h) / 2),
                    new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            });
        }

        private static bool LooksLikeSvg(string path, byte[] source)
        {
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return false;
            var head = Encoding.UTF8.GetString(source, 0, Math.Min(4096, source.Length));
            return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }

        // Anything that could make the parser reach outside the document is
        // refused before it is parsed for drawing: a DOCTYPE (external
        // entities), any href that is not a same-document "#fragment" or an
        // embedded data: URI, any url(...) that is not "#fragment", and any
        // @import. Refusing is airtight where "turn external loading off" is
        // only as good as a setting this package may not honour; the cost is
        // that an SVG linking an external picture is refused outright rather
        // than drawn without it.
        private static byte[]? RasteriseSvg(byte[] source, out bool refusedForReferences)
        {
            refusedForReferences = false;
            var text = Encoding.UTF8.GetString(source);

            // A DOCTYPE is an external-content refusal in its own right (it is
            // how entities reach files), and is named as one rather than as a
            // parse error.
            if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
            {
                refusedForReferences = true;
                return null;
            }

            try
            {
                if (!IsSelfContained(text))
                {
                    refusedForReferences = true;
                    return null;
                }
            }
            catch (XmlException) { return null; }

            return RenderSvg(text);
        }

        // The render step on its own, without the preflight. Separate so a test
        // can hand it a hostile document directly and prove Svg.Skia's own
        // switches hold when the preflight does not catch a reference form.
        internal static byte[]? RenderSvg(string text)
        {
            try
            {
                Harden();
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                using var svg = Svg.Skia.SKSvg.CreateFromStream(stream, Parameters);
                // A null picture or a zero-sized one throws inside Render and
                // is caught below, which is the same answer as a parse failure.
                var picture = svg.Picture!;

                return Render(canvas =>
                {
                    var r = picture.CullRect;
                    var scale = Math.Max(Size / r.Width, Size / r.Height);
                    canvas.Translate((Size - r.Width * scale) / 2, (Size - r.Height * scale) / 2);
                    canvas.Scale(scale);
                    canvas.Translate(-r.Left, -r.Top);
                    canvas.DrawPicture(picture);
                });
            }
            catch (Exception) { return null; }
        }

        // Second line of defence behind IsSelfContained, which Svg.Skia's
        // defaults would not give us on their own: external resources are
        // enabled and `file://` goes through WebRequest. Per document, the
        // strictest policy in secure mode; process-wide, no external images or
        // entities and no DTD processing. Each is pinned by a test.
        private static readonly Svg.Model.SvgParameters Parameters = new(
            null, null, null,
            new Svg.SvgDocumentLoadOptions
            {
                ProcessingMode = Svg.SvgProcessingMode.SecureStatic,
                ExternalResources = Svg.SvgExternalResourcePolicy.SameDocumentAndDataOnly
            });

        internal static void Harden()
        {
            Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalXmlEntites = Svg.ExternalType.None;
            Svg.SvgDocument.DisableDtdProcessing = true;
        }

        private static readonly Regex UrlRef = new(
            @"url\(\s*['""]?\s*[^#\s'"")]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ImportRule = new(
            @"@import", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Embedded pictures are allowed only when they are plain rasters. A
        // data:image/svg+xml is a whole second document that Svg.Skia would
        // parse and honour the references of (QA drove a nested one to a
        // network hit and a local-file read on 03243773), so it is refused
        // along with every other type rather than validated recursively.
        private static bool IsRasterDataUri(string value) =>
            value.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:image/jpg", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase);

        internal static bool IsSelfContained(string svg)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(new StringReader(svg), settings);
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    if (UrlRef.IsMatch(reader.Value) || ImportRule.IsMatch(reader.Value)) return false;
                }
                if (reader.NodeType != XmlNodeType.Element) continue;

                if (reader.HasAttributes)
                {
                    for (var i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        var value = reader.Value.Trim();
                        if (reader.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)
                            && !value.StartsWith('#')
                            && !IsRasterDataUri(value))
                            return false;
                        if (UrlRef.IsMatch(value) || ImportRule.IsMatch(value)) return false;
                    }
                    reader.MoveToElement();
                }
            }
            return true;
        }

        private static byte[]? Render(Action<SKCanvas> draw)
        {
            using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            draw(surface.Canvas);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
    }
}

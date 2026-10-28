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
            @"^[0-9a-f]{12}-[0-9a-f]{16}\.png$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
                var path = Path.Combine(iconsDir, fileName);
                if (!File.Exists(path)) return new(IconLoadOutcome.Missing, null);
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new(IconLoadOutcome.Missing, null);
            }

            using var bitmap = Decode(bytes);
            return bitmap is null ? new(IconLoadOutcome.Corrupt, null) : new(IconLoadOutcome.Loaded, bytes);
        }

        internal static IconImport Import(string sourcePath, string positionKey, string iconsDir)
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
            if (IsPng(source) || IsJpeg(source)) png = Normalise(source);
            else if (LooksLikeSvg(sourcePath, source)) png = RasteriseSvg(source);
            else return new(null, Skia(source) ? IconImportError.UnsupportedFormat : IconImportError.NotAnImage);

            if (png is null) return new(null, IconImportError.NotAnImage);

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

        private static SKBitmap? Decode(byte[] bytes)
        {
            try { return SKBitmap.Decode(bytes); }
            catch (Exception) { return null; }
        }

        private static byte[]? Normalise(byte[] source)
        {
            using var bitmap = Decode(source);
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
        private static byte[]? RasteriseSvg(byte[] source)
        {
            string text;
            try
            {
                text = Encoding.UTF8.GetString(source);
                if (!IsSelfContained(text)) return null;
            }
            catch (Exception e) when (e is XmlException or ArgumentException) { return null; }

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
                ExternalResources = Svg.SvgExternalResourcePolicy.Disabled
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
                            && !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
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

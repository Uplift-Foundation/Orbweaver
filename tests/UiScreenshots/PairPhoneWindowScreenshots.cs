using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-314's "Pair phone…" window through real Skia, the two stages a person
// actually looks at: the QR code to scan, and the check code with Allow. The
// window is built over a fake client (no relay, no socket) and never closed,
// the FontManager rule SettingsWindowSmokeTest records. The QR text is a
// fixed, realistic-length pairing URL so the code drawn is the size a real
// one is.
public class PairPhoneWindowScreenshots
{
    private sealed class Fake : IPairingClient
    {
        public Task<RelayLink.PairOffer> BeginPairingAsync(CancellationToken token = default) =>
            Task.FromResult(new RelayLink.PairOffer("off_0f1e2d3c4b5a6978", Now.AddMinutes(5), new byte[32],
                "orbweaver://pair?r=orbweaver-relay.uplifttech.org&d=dsk_5f3c9a0e1b7d2468&o=off_0f1e2d3c4b5a6978&k="
                + new string('A', 174) + "&s=" + new string('B', 43)));
        public bool Allow(RelayLink.PairRequest request) => true;
        public void Deny(RelayLink.PairRequest request) { }
        public void CancelPairing() { }
        public event Action<RelayLink.PairRequest> PairRequested = delegate { };
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [AvaloniaFact]
    public async Task ShowingTheQrCode()
    {
        var window = new PairPhoneWindow(new Fake(), () => Now.AddSeconds(13));
        await window.BeginAsync();
        ScreenshotHelper.Capture(window, "pair-phone-qr.png");
    }

    [AvaloniaFact]
    public async Task AskingWithTheCheckCode()
    {
        var window = new PairPhoneWindow(new Fake(), () => Now.AddSeconds(41));
        await window.BeginAsync();
        window.Ask(new RelayLink.PairRequest("off_0f1e2d3c4b5a6978", "ph_a1b2c3d4e5f60718", "Warren's iPhone", [], [], [], 482913));
        window.Tick();
        ScreenshotHelper.Capture(window, "pair-phone-check-code.png");
    }

    // The QR as drawn must be the QR as encoded, module for module, with no
    // light seam between two dark neighbours. The first version drew 3.2 px
    // modules at fractional offsets: it looked right, and OpenCV decoded
    // nothing from it (CB-314) while QRCoder's own PNG of the same text
    // decoded in full. This samples every module's centre and every boundary
    // between two dark modules through real Skia, which is what a camera sees.
    [AvaloniaFact]
    public void TheDrawnCodeIsTheEncodedCodeWithNoSeams()
    {
        var text = "orbweaver://pair?r=orbweaver-relay.uplifttech.org&d=dsk_5f3c9a0e1b7d2468&o=off_0f1e2d3c4b5a6978&k="
                   + new string('A', 174) + "&s=" + new string('B', 43);
        var modules = QrView.Encode(text);
        var view = new QrView { Modules = modules };
        view.Measure(new Size(320, 320));
        view.Arrange(new Rect(0, 0, 320, 320));

        using var bitmap = new RenderTargetBitmap(new PixelSize(320, 320));
        ScreenshotHelper.Render(view, bitmap);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        using var px = SKBitmap.Decode(stream);

        var count = modules.GetLength(0);
        var module = Math.Floor(320.0 / count);
        var origin = Math.Floor((320 - module * count) / 2);
        Assert.True(module >= 4, $"modules are {module} px; a phone camera needs room");
        bool Dark(double x, double y) => px.GetPixel((int)x, (int)y).Red < 128;

        var seams = 0;
        for (var y = 0; y < count; y++)
            for (var x = 0; x < count; x++)
            {
                var cx = origin + x * module + module / 2;
                var cy = origin + y * module + module / 2;
                Assert.True(modules[y, x] == Dark(cx, cy), $"module ({x},{y}) drawn wrong");
                if (x + 1 < count && modules[y, x] && modules[y, x + 1] && !Dark(origin + (x + 1) * module, cy)) seams++;
            }
        Assert.Equal(0, seams);
    }
}

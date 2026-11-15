using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// Shared by every screenshot class in this project: build a control, show
// it, force a real render, save the frame. Pulled out once every one of the
// 28 tests/UiTests scenarios got its own capture — 28 copies of the same
// four lines would have been the actual maintenance burden here, not the
// screenshots themselves.
internal static class ScreenshotHelper
{
    // dotnet test's working directory is the test assembly's own output
    // folder (tests/UiScreenshots/bin/...), not wherever the command was
    // invoked from, so Directory.GetCurrentDirectory() lands nowhere near
    // where ci.yml's artifact-upload step expects TestResults/ to be. Walk
    // up from the running assembly to the repo root instead, the same way
    // tests/IntegrationTests's hook-script tests locate OrbweaverHook.sh.
    public static readonly string OutputDir =
        Path.Combine(FindRepoRoot(), "TestResults", "screenshots");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find Orbweaver.csproj by walking up from " + AppContext.BaseDirectory);
    }

    static ScreenshotHelper()
    {
        Directory.CreateDirectory(OutputDir);
    }

    // RunJobs() alone flushes measure/arrange, but a compositor-driven render
    // (headless hit-testing has the same caveat, per tests/UiTests's
    // OrbFlyoutTests) still needs a timer tick to actually run a paint pass
    // over whatever just got invalidated.
    public static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // One control rather than a whole window, for a surface that sits below
    // the fold of a scrolling page. Capture(window) renders at the window's own
    // height, so anything scrolled out of view is simply absent from the image
    // — which is not a useful screenshot of a settings row near the bottom.
    //
    // The control must already be arranged: show its window and Flush() first,
    // or Bounds is empty and this saves a 1x1 pixel.
    public static void CaptureControl(Control control, string fileName)
    {
        Save(control, fileName);
    }

    public static void Capture(Window window, string fileName)
    {
        window.Show();
        Flush();
        CaptureAlreadyShown(window, fileName);
    }

    // For a window already Shown and flushed by its own test (OrbFlyoutTests'
    // click tests need to click before capturing, so Capture's own Show+Flush
    // would be redundant work, not wrong, just worth avoiding).
    //
    // Renders straight into a fresh RenderTargetBitmap rather than reading
    // back window.CaptureRenderedFrame() (the compositor's own last-composed
    // frame): ChatPanel is a process-wide singleton that gets rebound and
    // re-shown rather than recreated per test (see ChatPanel.Bind's own
    // `if (!IsVisible) Show()`), and CaptureRenderedFrame() came back
    // byte-identical across every test after the first real one against that
    // reused window — the compositor's cached frame never advanced past the
    // window's original Show(), no matter how many ForceRenderTimerTick()
    // calls or InvalidateVisual() calls ran first. Confirmed the underlying
    // data was correct throughout (right item counts, right IsVisible) — only
    // the frame the compositor handed back was stale. RenderTargetBitmap
    // walks the current visual tree directly instead of asking the
    // compositor for its last frame, so it has no such history to be stale
    // against. Windows built fresh per test (OrbFlyout, OrbWindow) never
    // exercised the reused-singleton path, so it never showed up there.
    public static void CaptureAlreadyShown(Window window, string fileName)
    {
        Save(window, fileName);
    }

    // Several windows rendered left to right onto one bitmap, for a
    // comparison that is only meaningful with every case in the same image —
    // CB-173's six kind badges. Each window goes through Render and Check like
    // any other capture, so the composite cannot be the one picture drawn
    // with LCD text or skipped by the legibility check.
    public static void SideBySide(IReadOnlyList<Window> windows, string fileName)
    {
        const int Gap = 8;

        var shots = windows.Select(w => new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(w.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(w.Bounds.Height))))).ToList();

        try
        {
            foreach (var (window, shot) in windows.Zip(shots)) Render(window, shot);

            var width = shots.Sum(s => s.PixelSize.Width) + Gap * (shots.Count + 1);
            var height = shots.Max(s => s.PixelSize.Height) + Gap * 2;

            using var target = new RenderTargetBitmap(new PixelSize(width, height));
            using (var ctx = target.CreateDrawingContext())
            {
                var x = Gap;
                foreach (var shot in shots)
                {
                    ctx.DrawImage(shot, new Rect(x, Gap, shot.PixelSize.Width, shot.PixelSize.Height));
                    x += shot.PixelSize.Width + Gap;
                }
            }

            target.Save(Path.Combine(OutputDir, fileName));

            foreach (var (window, shot) in windows.Zip(shots)) Check(window, shot, fileName);
        }
        finally
        {
            foreach (var shot in shots) shot.Dispose();
        }
    }

    // One render, written to disk and checked, rather than two paths that
    // could drift apart.
    private static void Save(Visual visual, string fileName)
    {
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(visual.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(visual.Bounds.Height)));

        using var bitmap = new RenderTargetBitmap(size);

        Render(visual, bitmap);

        // Written before it is checked, deliberately. A capture that fails the
        // check is the one a person most needs to look at — leaving it
        // unwritten would mean the run that finally caught the defect is also
        // the only run with no picture of it.
        bitmap.Save(Path.Combine(OutputDir, fileName));

        Check(visual, bitmap, fileName);
    }

    // The one way anything in this project puts a visual into a bitmap.
    //
    // Public because ChatPanelScreenshots composes two panels into a third
    // bitmap by rendering each one itself, which would otherwise be the single
    // capture in the suite that missed both halves of CB-171 — it would keep
    // LCD text on Windows and go unchecked into the bargain. A second way to
    // render is exactly how the first one's guarantees stop being guarantees.
    internal static void Render(Visual visual, RenderTargetBitmap bitmap)
    {
        AskForGrayscaleText(visual);

        bitmap.Render(visual);
    }

    // Split from Render so a caller can put the picture on disk in between.
    //
    // Reads the bitmap back through an encode to memory rather than through
    // the saved file: the composite path renders several panels before it
    // saves anything, so a check that could only run against a file would not
    // cover them at all.
    internal static void Check(Visual visual, RenderTargetBitmap bitmap, string label)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;

        using var decoded = SKBitmap.Decode(stream);

        if (decoded is not null) AssertTextIsLegible(visual, decoded, label);
    }

    // CB-171. A screenshot that nobody can read is worse than no screenshot,
    // because it still looks like evidence: every test passes, the PR comment
    // carries a picture, and a reviewer who cannot make the Windows one out
    // assumes the shot is just small. Two features merged that way before
    // anyone worked out the captures themselves were wrong.
    //
    // What goes wrong is specific and measurable. On win-x64 the affected text
    // comes back as a bi-level mask — two alpha values, 0 and 255 — where the
    // same text on osx-arm64 carries a hundred or more. At 11-13px that is not
    // a quality difference; stems merge, counters fill, and a sentence becomes
    // a row of black lumps. So the check is not "does this look right", which
    // no assertion can ask, but "was this text antialiased at all", which is a
    // yes or no with an enormous margin either side of it.
    //
    // Asked of each TextBlock's own rectangle rather than of the image as a
    // whole, deliberately. A whole-image statistic has to guess which pixels
    // were text, and every version of that guess this was prototyped against
    // put a real capture within one or two levels of a corrupt one — a margin
    // far too thin to gate a build on. The visual tree already knows exactly
    // where the text is, and asking it turns a statistical rule into a direct
    // one.
    private static void AssertTextIsLegible(Visual root, SKBitmap image, string fileName)
    {
        // Read the whole surface once rather than calling GetPixel per pixel:
        // that is a colour-type conversion per call, and every TextBlock in a
        // capture would pay it again over its own rectangle. Converting once
        // up front is simply the cheaper shape, not a fix for any measured
        // problem — the suite was never observed to be slow because of it.
        var luma = LumaPlane(image);

        foreach (var block in root.GetSelfAndVisualDescendants().OfType<TextBlock>())
        {
            if (!block.IsVisible || string.IsNullOrWhiteSpace(block.Text)) continue;

            // Text under an Opacity-0 ancestor draws nothing by design — the
            // New chat dialog's CB-207 ghosts are exactly that, invisible
            // copies of a section that hold its height — so it has no ink to
            // measure. Without this a ghost label that happens to sit over a
            // control the real section draws (CB-329's Open existing filter
            // row sits where the Agent ghost's label is) reads as a bi-level
            // glyph mask: two levels, the background and the border behind it.
            if (block.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, root)).OfType<Visual>().Any(a => a.Opacity == 0)) continue;
            if (block.Bounds.Width < 8 || block.Bounds.Height < 6) continue;

            if (!VisibleRect(block, root, out var visible)) continue;

            var left = (int)Math.Floor(visible.X);
            var top = (int)Math.Floor(visible.Y);
            var right = left + (int)Math.Ceiling(visible.Width);
            var bottom = top + (int)Math.Ceiling(visible.Height);

            // Skipped rather than clamped into the image, and this is the whole
            // difference between a check and a coincidence. A chat panel opens
            // at its newest turn, so earlier turns are still in the tree with
            // positions above the viewport — TranslatePoint hands back a
            // negative Y for them, quite correctly. Clamping that to zero does
            // not make the text visible; it moves the sampling window onto a
            // completely different part of the picture and then reports on
            // whatever happened to be there. It read a scrolled-out
            // "why is the build red?" against a flat patch of bubble, found
            // three levels, and failed a capture that was perfectly legible.
            //
            // A block that is not wholly inside the frame is simply not in the
            // picture this is about, and has nothing to say about how the
            // picture was drawn.
            if (left < 0 || top < 0 || right > image.Width || bottom > image.Height) continue;

            if (right - left < 8 || bottom - top < 6) continue;

            var counts = new int[256];

            for (var y = top; y < bottom; y++)
            {
                var row = y * image.Width;

                for (var x = left; x < right; x++)
                {
                    counts[luma[row + x]]++;
                }
            }

            var distinct = counts.Count(c => c > 0);

            // Text whose ink never reaches the capture — scrolled out, clipped,
            // or drawn in the background colour — has nothing to say about
            // antialiasing, and a rule that failed on it would be asserting
            // something else entirely.
            var total = counts.Sum();
            var modal = counts.Max();

            if (total - modal < 40) continue;

            // Four, not a rounder-looking number. The defect is exactly two
            // levels, always — it is a bi-level mask, not a degraded one — so
            // anything above two catches it. The headroom above that has to
            // come from the *other* side, and on Windows that side is
            // narrower than it looks: DirectWrite's grayscale is quantised,
            // and the diagnostic matrix measured a 34-character string at
            // only 9 to 17 levels there where macOS gives 148 to 169. A short
            // label has fewer pixels and fewer levels again, so a floor of six
            // or eight would eventually go red on a perfectly legible "Retry"
            // and be read as this bug coming back. Four keeps a margin of two
            // over the defect while staying well clear of the real minimum.
            Assert.True(
                distinct >= 4,
                $"{fileName}: the text \"{Trim(block.Text!)}\" rendered with {distinct} "
                + $"distinct luminance levels in its own {right - left}x{bottom - top} "
                + $"rectangle at ({left},{top}) of a {image.Width}x{image.Height} capture. Antialiased text at this size carries at least nine, and "
                + "two means a bi-level glyph mask — which at 11-13px is the "
                + "unreadable-blob defect CB-171 exists for. The capture is corrupt, not "
                + "merely ugly: do not review it, and do not lower this floor to make the "
                + "run green.");
        }
    }

    private static string Trim(string text) =>
        text.Length <= 40 ? text : text[..37] + "...";

    // Luminance rather than alpha: some captures sit on an opaque background
    // and carry a single alpha value across the whole image, which would make
    // an alpha-based reading report "one level" about a perfectly good
    // screenshot.
    private static byte[] LumaPlane(SKBitmap image)
    {
        var pixels = image.Pixels;
        var luma = new byte[pixels.Length];

        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];

            luma[i] = (byte)Math.Round(
                (0.299 * pixel.Red) + (0.587 * pixel.Green) + (0.114 * pixel.Blue));
        }

        return luma;
    }

    // CB-171, the fix. Ask for grayscale antialiasing rather than taking the
    // default, which on the Windows runner produces text nobody can read.
    //
    // Measured on the runner itself rather than reasoned about, because three
    // readings of source had each ruled a mechanism out and none had ruled one
    // in. Rendering the same string at four sizes and three weights under each
    // of the four TextRenderingMode values, on win-x64:
    //
    //     Unspecified         2 luminance levels   (bi-level: the defect)
    //     SubpixelAntialias   2 luminance levels   (byte-identical ink count)
    //     Alias               2 luminance levels
    //     Antialias           9-17 levels          (correct)
    //
    // Every size, every weight, no exceptions. Unspecified and
    // SubpixelAntialias agreeing exactly is the confirmation that
    // Unspecified *is* SubpixelAntialias — which is what Avalonia's
    // GlyphRunImpl.GetTextBlob says it resolves to. On osx-arm64 the same
    // matrix gives 148-169 levels for all three of those and 2 only for Alias,
    // which is why this has always looked like a Windows-only font problem
    // rather than what it is.
    //
    // So: an LCD (subpixel) glyph mask is three per-channel coverages, and
    // there is nowhere for them to go in a RenderTargetBitmap's alpha channel
    // — coverage collapses to opaque-or-nothing on the way in. Avalonia knows
    // this and guards its own offscreen targets against it: SurfaceRenderTarget
    // is created with DisableTextLcdRendering hard-coded true for anything that
    // is not a compositor layer (Skia's DrawingContextImpl.CreateRenderTarget).
    // A RenderTargetBitmap the test constructs directly never passes through
    // that, so it is the one text-drawing surface in the process with LCD text
    // still switched on. Asking for grayscale here is not a workaround — it is
    // the setting Avalonia would have applied itself one layer down.
    //
    // Scoped to the capture path on purpose. The app draws through the
    // compositor onto a real window surface, where LCD is ordinary ClearType
    // and correct; its only two RenderTargetBitmap uses (a chat-panel arrow
    // and a menu swatch) draw lines and ellipses, no text. Nothing a Windows
    // user sees is changed by this, and a screenshot suite that quietly
    // changed it would be showing something the app does not do.
    //
    // Set on the visual being captured rather than on the application: the
    // ImmediateRenderer that RenderTargetBitmap.Render walks pushes
    // visual.TextOptions around that visual's whole subtree, so one call
    // covers everything in frame and nothing outside it.
    private static void AskForGrayscaleText(Visual visual)
    {
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Antialias);
    }

    // Where a block's text actually lands in the captured picture, or false if
    // none of it does.
    //
    // Position alone is not enough, and the difference cost this check two
    // wrong answers before it was written. A chat panel keeps every earlier
    // turn in its tree; the scroll viewport is what stops them being drawn,
    // not their absence. One such turn translated to (132,0) of a 340x420
    // capture — inside the frame, so an in-bounds test let it through — and
    // the sampling window landed on the panel header painted across that
    // strip. Three flat luminance levels, and a perfectly legible capture
    // reported as corrupt.
    //
    // So intersect the block with every ancestor on the way up. A scroll
    // viewport clips to its own bounds, so a turn scrolled above it drops out
    // by construction, and so does anything else outside the region its
    // parents actually gave it. The intersection is conservative: a control
    // that legitimately draws outside its parent will be skipped rather than
    // measured, which loses a little coverage and cannot produce a wrong
    // failure. That is the right way round for something that gates a build.
    private static bool VisibleRect(Visual block, Visual root, out Rect visible)
    {
        visible = default;

        // Hidden is not the same as clipped, and bounds cannot tell them apart.
        //
        // Intersecting ancestors below handles a control the viewport crops.
        // It does nothing for one that was laid out and then had IsVisible set
        // false: Avalonia keeps the bounds from its last arrange, so the rect
        // still looks perfectly reasonable and still lands inside the frame —
        // it is simply not drawn there any more. The sampler then reads
        // whatever *is* drawn at those coordinates and attributes it to this
        // block.
        //
        // CB-166's settings filter is what made that reachable. A capture of
        // the window with "voice" typed in reported that "Go to the session"
        // rendered with two luminance levels at (326,367). That string belongs
        // to a combo in the "Clicking an orb" section, which the filter had
        // hidden; the pixels actually at (326,367) were the Voice section's own
        // "Full response" combo, rendering correctly. A true capture failing on
        // text that is not in it, because of text that is not drawn.
        //
        // Checked before the ancestor walk rather than inside it so the block's
        // own IsVisible is covered too, and checked with IsVisible rather than
        // IsEffectivelyVisible only as a fallback — walking it explicitly keeps
        // this honest if a block is reparented under a hidden ancestor the
        // property has not been recomputed for.
        if (!block.IsVisible) return false;

        var origin = block.TranslatePoint(default, root);

        if (origin is null) return false;

        var rect = new Rect(origin.Value, block.Bounds.Size);

        for (var ancestor = block.GetVisualParent();
             ancestor is not null;
             ancestor = ancestor.GetVisualParent())
        {
            if (!ancestor.IsVisible) return false;

            var ancestorOrigin = ancestor.TranslatePoint(default, root);

            if (ancestorOrigin is null) return false;

            rect = rect.Intersect(new Rect(ancestorOrigin.Value, ancestor.Bounds.Size));

            if (rect.Width <= 0 || rect.Height <= 0) return false;

            if (ReferenceEquals(ancestor, root)) break;
        }

        visible = rect;

        return true;
    }
}

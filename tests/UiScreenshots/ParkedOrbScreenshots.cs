using System.IO;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-307: a live session nobody has spoken to for a while is drawn dimmed as
// "parked" instead of being hidden, on the machine it runs on and on every peer.
//
// What a reviewer needs to see is the dimming itself, judged against the same
// orb at full brightness — an absolute opacity is hard to read off a lone
// capture, and 0.45 against 1.0 is obvious side by side. So each parked orb is
// captured on its own, and then stitched beside its live twin into
// parked-vs-live-orbs.png, which reads left to right: local live, local parked,
// remote live, remote parked.
//
// Three statuses, built the way the scan builds them: a Terminal-shaped Claude
// Code session whose Presence WithQuiet set to Parked, and the remote block's
// RemoteControl status carrying the host's `parked` roster flag. The parked
// tooltip word has no capture of its own — a tooltip needs a pointer, which a
// headless render never has — and is pinned in tests/UiTests instead.
[Collection("Settings")]
public class ParkedOrbScreenshots
{
    private static SessionStatus Local(OrbPresence presence, string state) => new()
    {
        Source = SessionSource.ClaudeCode,
        Shape = LocalSessionShape.Terminal,
        State = state,
        Title = "lawyer-search",
        Color = "orange",
        Cwd = "/Users/user/project",
        SessionPid = 4321,
        Presence = presence,
    };

    private static SessionStatus Remote(OrbPresence presence, string state) => new()
    {
        Source = SessionSource.RemoteControl,
        RemoteCli = MirrorProtocol.CliClaudeCode,
        Kind = SessionKind.Remote,
        State = state,
        Title = "lawyer-search",
        Color = "blue",
        Presence = presence,
    };

    private static void Draw(string key, SessionStatus status, string file)
    {
        var orb = new OrbWindow(key);
        orb.UpdateFrom(status);
        ScreenshotHelper.Capture(orb, file);
    }

    [AvaloniaFact]
    public void AParkedOrbBesideItsLiveTwinLocalAndRemote()
    {
        Draw("local-live", Local(OrbPresence.Present, "idle"), "orb-live-local.png");
        Draw("local-parked", Local(OrbPresence.Parked, "idle"), "orb-parked-local.png");
        Draw("rc:.claude:sid:live", Remote(OrbPresence.Present, "idle"), "orb-live-remote.png");
        Draw("rc:.claude:sid:parked", Remote(OrbPresence.Parked, "idle"), "orb-parked-remote.png");

        // The point of the capture, checked as well as drawn: a parked orb is
        // measurably fainter than its live twin, so a regression that stops the
        // dimming fails here rather than only looking wrong in a PR comment.
        Assert.True(
            Alpha("orb-parked-local.png") < Alpha("orb-live-local.png"),
            "the parked local orb is not fainter than the live one");
        Assert.True(
            Alpha("orb-parked-remote.png") < Alpha("orb-live-remote.png"),
            "the parked remote orb is not fainter than the live one");

        Stitch(new[] { "orb-live-local.png", "orb-parked-local.png", "orb-live-remote.png", "orb-parked-remote.png" },
            "parked-vs-live-orbs.png");
    }

    // CB-307 addendum: a remote session that is on a dialog. A remote orb has
    // never drawn `waiting` before — the roster word arrived as idle — so this
    // is a new state on a surface that shows it. Beside an idle remote orb, so
    // the amber pulse and the full brightness read against the ordinary one.
    [AvaloniaFact]
    public void ARemoteOrbOnADialogBesideAnIdleOne()
    {
        Draw("rc:.claude:sid:idle", Remote(OrbPresence.Present, "idle"), "orb-remote-idle.png");
        Draw("rc:.claude:sid:waiting", Remote(OrbPresence.Present, "waiting"), "orb-remote-waiting.png");

        // Waiting is drawn at full brightness, not dimmed like a parked orb,
        // and not the same picture as idle.
        Assert.True(
            Alpha("orb-remote-waiting.png") >= Alpha("orb-remote-idle.png") * 0.95,
            "a waiting remote orb should not be dimmed");
        Assert.NotEqual(Alpha("orb-remote-idle.png"), Alpha("orb-remote-waiting.png"));

        Stitch(new[] { "orb-remote-idle.png", "orb-remote-waiting.png" }, "remote-idle-vs-waiting-orbs.png");
    }

    // Total opacity of a capture: the sum of its alpha channel, which is what a
    // dimmed orb loses and a live one keeps.
    private static long Alpha(string file)
    {
        using var bitmap = SKBitmap.Decode(Path.Combine(ScreenshotHelper.OutputDir, file));
        long sum = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                sum += bitmap.GetPixel(x, y).Alpha;
        return sum;
    }

    // Left to right on a transparent ground at three times size, the same
    // stitch RemoteTeamOrbScreenshots uses; the native captures beside it are
    // the ones to judge pixels by.
    private static void Stitch(string[] inputs, string output)
    {
        var bitmaps = new SKBitmap[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
            bitmaps[i] = SKBitmap.Decode(Path.Combine(ScreenshotHelper.OutputDir, inputs[i]));

        try
        {
            const int gap = 16;
            const float scale = 3;
            var width = gap * (bitmaps.Length - 1);
            var height = 0;
            foreach (var b in bitmaps)
            {
                width += b.Width;
                height = System.Math.Max(height, b.Height);
            }

            using var surface = SKSurface.Create(new SKImageInfo((int)(width * scale), (int)(height * scale),
                SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(scale);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };

            var x = 0;
            foreach (var b in bitmaps)
            {
                surface.Canvas.DrawBitmap(b, x, 0, paint);
                x += b.Width + gap;
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(ScreenshotHelper.OutputDir, output));
            data.SaveTo(file);
        }
        finally
        {
            foreach (var b in bitmaps) b.Dispose();
        }
    }
}

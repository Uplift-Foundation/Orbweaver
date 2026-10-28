using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-282: where a group's name caption sits, for every shape the arrangement
// draws. The mistake it can make is a caption over an orb or off the window, and
// neither is visible from reading the arithmetic.
public class GroupCaptionTests
{
    private const double Padding = 10;
    private static readonly Size Text = new(80, 16);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var shape in new[] { "circle", "diamond", "star", "heart", "line", "vline", "grid" })
            foreach (var n in new[] { 1, 2, 5, 12 })
                foreach (var mixed in new[] { false, true })
                    yield return new object[] { shape, n, mixed };
    }

    private static List<(double X, double Y, double R)> Discs(string shape, int n, bool mixed)
    {
        var sizeOf = mixed ? Enumerable.Range(0, n).Select(i => 0.6 + 1.4 * (i % 5) / 4.0).ToArray() : null;
        var layout = new OrbArrangement.Layout(new PixelRect(0, 0, 1920, 1080), 1.0, shape, 1.0);
        var at = OrbArrangement.Compute(n, Enumerable.Repeat(-1, n).ToArray(), layout, sizeOf);

        var discs = new List<(double, double, double)>();
        for (var i = 0; i < n; i++)
        {
            var size = sizeOf?[i] ?? 1.0;
            var window = OrbArrangement.WindowFor(1.0, size);
            discs.Add((at[i].X + window / 2.0, at[i].Y + window / 2.0, OrbArrangement.CircleDip * size / 2));
        }
        return discs;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCaptionIsCentredAboveTheOutlineAndNeverOverAnOrb(string shape, int n, bool mixed)
    {
        var discs = Discs(shape, n, mixed);
        var outline = GroupOutline.Silhouette(shape, discs, Padding);
        var bounds = GroupOutline.Bounds(outline);

        var caption = GroupCaption.Place(bounds, Text, 4);

        Assert.Equal(bounds.Center.X, caption.Center.X, 6);
        Assert.Equal(bounds.Top - 4, caption.Bottom, 6);
        Assert.Equal(Text.Width, caption.Width, 6);
        Assert.Equal(Text.Height, caption.Height, 6);
        // Wholly above every point of the outline, so above every orb inside it.
        Assert.All(outline, p => Assert.True(p.Y >= caption.Bottom));
        Assert.All(discs, d => Assert.True(d.Y - d.R >= caption.Bottom));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheWindowBoxTakesInTheCaptionAndTheEdge(string shape, int n, bool mixed)
    {
        var outline = GroupOutline.Silhouette(shape, Discs(shape, n, mixed), Padding);
        var bounds = GroupOutline.Bounds(outline);
        var caption = GroupCaption.Place(bounds, Text, 4);

        var box = GroupCaption.WindowBox(bounds, caption, 4);

        Assert.True(box.Contains(caption.TopLeft) && box.Contains(caption.BottomRight));
        Assert.True(box.Contains(bounds.TopLeft) && box.Contains(bounds.BottomRight));
        Assert.Equal(caption.Top - 4, box.Top, 6);
        Assert.Equal(bounds.Bottom + 4, box.Bottom, 6);
    }

    [Fact]
    public void ACaptionWiderThanAnOutlineGrowsTheWindowSideways()
    {
        var bounds = new Rect(100, 100, 20, 20);
        var caption = GroupCaption.Place(bounds, new Size(200, 16), 4);

        var box = GroupCaption.WindowBox(bounds, caption, 4);

        Assert.True(box.Left < bounds.Left && box.Right > bounds.Right);
        Assert.Equal(110, caption.Center.X, 6);
    }

    [Fact]
    public void WithNoCaptionTheBoxIsTheOldOne()
    {
        var bounds = new Rect(10, 20, 30, 40);

        Assert.Equal(bounds.Inflate(4), GroupCaption.WindowBox(bounds, null, 4));
    }

    [Theory]
    [InlineData("Reviewers", 3, "Reviewers")]
    [InlineData("  padded ", 1, "padded")]
    [InlineData("", 2, "Group 2")]
    [InlineData("   ", 4, "Group 4")]
    [InlineData(null, 0, "Group 1")]
    public void TheTextIsTheNameOrGroupN(string? name, int ordinal, string expected) =>
        Assert.Equal(expected, GroupCaption.TextFor(name, ordinal));

    [Fact]
    public void TheNumberIsCultureIndependent()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("Group 12", GroupCaption.TextFor(null, 12));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }
}

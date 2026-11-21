using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-404: the dial's geometry, replayed from Orbweaver-Mobile's
// contracts/effort-dial-vectors.json (see Fixtures/relay/SOURCE.md), the table
// iOS and Android are checked against too. The cases after the replay are the
// desk's own: drawing points and short labels, and a step from "nothing
// selected", which the shared table does not cover.
public class EffortDialMathTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "effort-dial-vectors.json"))).RootElement;

    private static readonly double Tolerance = Vectors.GetProperty("tolerance").GetDouble();

    private static double? D(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();
    private static int? I(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    private static void Near(double? expected, double? actual, string where)
    {
        if (expected is null) { Assert.True(actual is null, $"{where}: expected null, got {actual}"); return; }
        Assert.True(actual is { } a && Math.Abs(a - expected.Value) <= Tolerance, $"{where}: expected {expected}, got {actual}");
    }

    private static JsonElement[] Table(string name) => Vectors.GetProperty(name).EnumerateArray().ToArray();

    [Fact]
    public void TheArcIsTheContracts()
    {
        Assert.Equal(EffortDialMath.ArcStart, Vectors.GetProperty("arc").GetProperty("lowest").GetDouble());
        Assert.Equal(EffortDialMath.ArcEnd, Vectors.GetProperty("arc").GetProperty("highest").GetDouble());
    }

    [Fact]
    public void AngleForReplays()
    {
        foreach (var c in Table("angle_for"))
            Near(D(c.GetProperty("angle")), EffortDialMath.AngleFor(c.GetProperty("index").GetInt32(), c.GetProperty("count").GetInt32()), c.ToString());
    }

    [Fact]
    public void AngleOfReplays()
    {
        foreach (var c in Table("angle_of"))
            Near(D(c.GetProperty("angle")), EffortDialMath.AngleOf(c.GetProperty("dx").GetDouble(), c.GetProperty("dy").GetDouble()), c.ToString());
    }

    [Fact]
    public void IndexForReplays()
    {
        var cases = Table("index_for");
        Assert.NotEmpty(cases);
        foreach (var c in cases)
            Assert.True(I(c.GetProperty("index")) == EffortDialMath.IndexFor(c.GetProperty("angle").GetDouble(), c.GetProperty("count").GetInt32()), c.ToString());
    }

    [Fact]
    public void WrapReplays()
    {
        foreach (var c in Table("wrap"))
            Near(c.GetProperty("wrapped").GetDouble(), EffortDialMath.Wrap(c.GetProperty("d").GetDouble()), c.ToString());
    }

    [Fact]
    public void StepReplays()
    {
        foreach (var c in Table("step"))
            Assert.True(I(c.GetProperty("result")) == EffortDialMath.Step(c.GetProperty("index").GetInt32(), c.GetProperty("delta").GetInt32(), c.GetProperty("count").GetInt32()), c.ToString());
    }

    [Fact]
    public void DragReplays()
    {
        var drags = Table("drag");
        Assert.Contains(drags, d => d.GetProperty("name").GetString() == "into_the_gap_and_on");
        Assert.Contains(drags, d => d.GetProperty("name").GetString() == "past_the_high_end");

        foreach (var d in drags)
        {
            var name = d.GetProperty("name").GetString()!;
            var count = d.GetProperty("count").GetInt32();
            var points = d.GetProperty("points").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToArray();
            var after = d.GetProperty("after").EnumerateArray().ToArray();

            var drag = DialDrag.Press(points[0].Item1, points[0].Item2);
            for (var i = 0; i < points.Length; i++)
            {
                if (i > 0) drag = drag.Move(points[i].Item1, points[i].Item2);
                Near(D(after[i].GetProperty("raw")), drag.Raw, $"{name}[{i}] raw");
                Assert.True(I(after[i].GetProperty("index")) == drag.Index(count), $"{name}[{i}] index");
            }

            Assert.True(I(d.GetProperty("commit")) == drag.Index(count), $"{name} commit");
        }
    }

    [Theory]
    [InlineData(-1, -1, 5, 0)]
    [InlineData(-1, 1, 5, 4)]
    [InlineData(-1, 0, 5, null)]
    public void AStepFromNothingSelectedLandsOnTheNearerEnd(int index, int delta, int count, int? expected)
    {
        Assert.Equal(expected, EffortDialMath.Step(index, delta, count));
    }

    [Fact]
    public void AngleOfAPointIsTheOffsetsAngle()
    {
        Assert.Equal(90, EffortDialMath.AngleOf(new Point(100, 50), new Point(50, 50))!.Value, 6);
        Assert.Null(EffortDialMath.AngleOf(new Point(50, 50), new Point(50, 50)));
    }

    [Theory]
    [InlineData(0, 50, 0)]
    [InlineData(90, 100, 50)]
    [InlineData(180, 50, 100)]
    public void PointAtIsTheInverseOfAngleOf(double angle, double x, double y)
    {
        var p = EffortDialMath.PointAt(new Point(50, 50), 50, angle);
        Assert.Equal(x, p.X, 6);
        Assert.Equal(y, p.Y, 6);
    }

    [Fact]
    public void AMoveWithNoPressAngleBecomesThePress()
    {
        var drag = DialDrag.Press(0, 0);
        Assert.Null(drag.Raw);
        Assert.Null(drag.Index(5));
        drag = drag.Move(0, 0);
        Assert.Null(drag.Raw);
        drag = drag.Move(100, 0);
        Assert.Equal(90, drag.Raw);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "med")]
    [InlineData("xhigh", "xhi")]
    [InlineData("high", "high")]
    [InlineData("minimal", "min")]
    [InlineData("ultra", "ult")]
    [InlineData("Extended", "Exte")]
    public void ShortLabels(string level, string label)
    {
        Assert.Equal(label, EffortDial.ShortLabel(level));
    }
}

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.UiTests;

// CB-268 S5: the pool, the style, the highlight and the press of the group
// backdrop windows, through real Avalonia windows on the headless platform.
// What cannot be reached here is the window server — window level, the poll's
// setIgnoresMouseEvents:, SetWindowRgn, SetWindowPos — which BackdropNative and
// the Windows order call own and which the spike measured on real machines.
[Collection("Settings")]
public class GroupBackdropsTests : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();

    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;

    public GroupBackdropsTests()
    {
        // The headless PointToScreen ignores Position; place orbs where the test said.
        GroupBackdrops.Measure = o => (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.SetArranged(true);
        GroupBackdrops.SetLooseOrbs(Array.Empty<OrbWindow>());
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Highlight(null);
    }

    public void Dispose()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.SetArranged(true);
        GroupBackdrops.SetLooseOrbs(Array.Empty<OrbWindow>());
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Highlight(null);
        foreach (var o in _orbs) o.Close();
        GroupBackdrops.Measure = _measure;
    }

    private OrbWindow Orb(string id, int x, int y, bool member = false)
    {
        var orb = new OrbWindow(id);
        if (member) orb.SetTeamRole(true);
        orb.Position = new PixelPoint(x, y);
        orb.Show();
        _orbs.Add(orb);
        return orb;
    }

    private static BackdropSpec Spec(string id, string shape, IReadOnlyList<OrbWindow> orbs, GroupStyle? style = null) =>
        new(id, shape, orbs, style);

    private (List<OrbWindow> A, List<OrbWindow> B) TwoGroups()
    {
        var a = new List<OrbWindow> { Orb("a1", 100, 100), Orb("a2", 200, 100), Orb("a3", 150, 200) };
        var b = new List<OrbWindow> { Orb("b1", 600, 100), Orb("b2", 700, 100) };
        return (a, b);
    }

    [AvaloniaFact]
    public void OneBackdropPerGroupThatHasOrbs()
    {
        var (a, b) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("g-b", "circle", b) });

        Assert.Equal(2, GroupBackdrops.ShownCount);
        Assert.True(GroupBackdrops.StateOf("g-a")!.Value.Shown);
        Assert.True(GroupBackdrops.StateOf("g-b")!.Value.Shown);
        Assert.Null(GroupBackdrops.StateOf("nope"));
    }

    [AvaloniaFact]
    public void TheBackdropSurroundsItsOrbs()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });

        var s = GroupBackdrops.StateOf("g-a")!.Value;
        var left = a.Min(o => o.Position.X);
        var right = a.Max(o => o.Position.X + 56);

        Assert.True(s.Position.X <= left, $"{s.Position} vs {left}");
        Assert.True(s.Position.X + s.Width >= right);
        Assert.Equal(360, s.Vertices);
    }

    [AvaloniaFact]
    public void AGroupWithNoVisibleOrbsHasNoBackdropAndAnEmptyUpdateParksEverything()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        Assert.Equal(1, GroupBackdrops.ShownCount);

        a.ForEach(o => o.Hide());
        GroupBackdrops.Refresh();
        Assert.Equal(0, GroupBackdrops.ShownCount);

        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        Assert.Equal(0, GroupBackdrops.ShownCount);
    }

    [AvaloniaFact]
    public void ABackdropThatGoesAwayIsPooledAndTheNextOneReusesIt()
    {
        var (a, b) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("g-b", "circle", b) });
        var before = GroupBackdrops.ParkedCount;

        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        Assert.Equal(1, GroupBackdrops.ShownCount);
        Assert.Equal(before + 1, GroupBackdrops.ParkedCount);

        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("g-b2", "star", b) });
        Assert.Equal(2, GroupBackdrops.ShownCount);
        Assert.Equal(before, GroupBackdrops.ParkedCount);   // reused, not constructed
    }

    [AvaloniaFact]
    public void ParkAllHidesEveryBackdropAndTheNextRefreshBringsThemBack()
    {
        var (a, b) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("g-b", "circle", b) });

        GroupBackdrops.ParkAll();
        Assert.Equal(0, GroupBackdrops.ShownCount);
        Assert.Null(GroupBackdrops.StateOf("g-a"));

        GroupBackdrops.Refresh();
        Assert.Equal(2, GroupBackdrops.ShownCount);
    }

    [AvaloniaFact]
    public void ThePoolIsBounded()
    {
        var orbs = Enumerable.Range(0, 12).Select(i => Orb($"p{i}", 50 + i * 90, 300)).ToList();
        GroupBackdrops.Update(orbs.Select((o, i) => Spec($"g{i}", "circle", new[] { o })));
        Assert.Equal(12, GroupBackdrops.ShownCount);

        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        Assert.Equal(8, GroupBackdrops.ParkedCount);
    }

    [AvaloniaFact]
    public void StyleComesFromTheGlobalsUnlessTheGroupOverridesIt()
    {
        var (a, b) = TwoGroups();
        var over = new GroupStyle("#FF0000", 0.8, null, null);
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a, over), Spec("g-b", "circle", b) });

        var sa = GroupBackdrops.StateOf("g-a")!.Value;
        var sb = GroupBackdrops.StateOf("g-b")!.Value;

        Assert.Equal(Color.FromArgb(204, 255, 0, 0), sa.Stroke);
        Assert.NotEqual(sa.Stroke, sb.Stroke);
        Assert.Equal(sa.Fill, sb.Fill);   // fill not overridden: inherited
        Assert.Equal(BackdropStyle.StrokeDip, sa.StrokeWidth);
    }

    [AvaloniaFact]
    public void ChangingTheStyleOnTheNextUpdateRestylesTheSameWindow()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a, new GroupStyle("#FF0000", 1.0, null, null)) });
        var first = GroupBackdrops.StateOf("g-a")!.Value;

        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a, new GroupStyle("#00FF00", 1.0, null, null)) });
        var second = GroupBackdrops.StateOf("g-a")!.Value;

        Assert.Equal(Color.FromArgb(255, 255, 0, 0), first.Stroke);
        Assert.Equal(Color.FromArgb(255, 0, 255, 0), second.Stroke);
        Assert.Equal(first.Position, second.Position);   // same silhouette, same window
    }

    [AvaloniaFact]
    public void HighlightIsFullOpacityAtTwiceTheStrokeAndClearsWithNull()
    {
        var (a, b) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("g-b", "circle", b) });
        var plain = GroupBackdrops.StateOf("g-a")!.Value;

        GroupBackdrops.Highlight("g-a");
        var lit = GroupBackdrops.StateOf("g-a")!.Value;
        var other = GroupBackdrops.StateOf("g-b")!.Value;

        Assert.True(lit.Highlighted);
        Assert.Equal(255, lit.Stroke.A);
        Assert.Equal(plain.StrokeWidth * 2, lit.StrokeWidth);
        Assert.False(other.Highlighted);
        Assert.Equal(plain.StrokeWidth, other.StrokeWidth);

        GroupBackdrops.Highlight(null);
        Assert.Equal(plain, GroupBackdrops.StateOf("g-a")!.Value);
    }

    [AvaloniaFact]
    public void AHighlightSurvivesTheNextRefresh()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        GroupBackdrops.Highlight("g-a");

        GroupBackdrops.Refresh();

        Assert.True(GroupBackdrops.StateOf("g-a")!.Value.Highlighted);
    }

    [AvaloniaFact]
    public void OutlinesOffLeavesTheBackdropInPlaceButInvisible()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        var visible = GroupBackdrops.StateOf("g-a")!.Value;

        GroupBackdrops.SetOutlinesVisible(false);
        var hidden = GroupBackdrops.StateOf("g-a")!.Value;

        Assert.True(hidden.Shown);                          // still there to be grabbed
        Assert.Equal(visible.Position, hidden.Position);
        Assert.Equal(visible.Width, hidden.Width);
        Assert.Equal(Colors.Transparent, hidden.Stroke);
        Assert.Equal(Colors.Transparent, hidden.Fill);
        Assert.Equal(0, hidden.StrokeWidth);

        // …and a backdrop made while outlines are off is invisible from the start.
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("g-c", "circle", new[] { Orb("c1", 900, 500) }) });
        Assert.Equal(Colors.Transparent, GroupBackdrops.StateOf("g-c")!.Value.Stroke);
        Assert.True(GroupBackdrops.StateOf("g-c")!.Value.Shown);

        // The highlight still shows with outlines off: it is the join cue.
        GroupBackdrops.Highlight("g-c");
        Assert.Equal(255, GroupBackdrops.StateOf("g-c")!.Value.Stroke.A);

        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.SetOutlinesVisible(true);   // idempotent
        Assert.Equal(visible.Stroke, GroupBackdrops.StateOf("g-a")!.Value.Stroke);
    }

    [AvaloniaFact]
    public void MovingEveryOrbTogetherMovesTheWindowWithoutRefitting()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        var before = GroupBackdrops.StateOf("g-a")!.Value;

        foreach (var o in a) o.Position = new PixelPoint(o.Position.X + 40, o.Position.Y - 15);
        GroupBackdrops.Refresh();
        var after = GroupBackdrops.StateOf("g-a")!.Value;

        Assert.Equal(before.Position.X + 40, after.Position.X);
        Assert.Equal(before.Position.Y - 15, after.Position.Y);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
    }

    // A shape change leaves the discs exactly where they were — a zero translation —
    // so the translate-instead-of-refit shortcut must not be taken for it.
    [AvaloniaFact]
    public void ChangingTheDrawnShapeRefitsEvenWhenTheOrbsDidNotMove()
    {
        var orbs = new List<OrbWindow> { Orb("s1", 100, 100), Orb("s2", 400, 100) };

        GroupBackdrops.Update(new[] { Spec("g-s", "line", orbs) });
        var line = GroupBackdrops.StateOf("g-s")!.Value;

        GroupBackdrops.Update(new[] { Spec("g-s", "heart", orbs) });
        var heart = GroupBackdrops.StateOf("g-s")!.Value;

        Assert.NotEqual((line.Width, line.Height, line.Vertices), (heart.Width, heart.Height, heart.Vertices));
        Assert.Equal(360, heart.Vertices);

        GroupBackdrops.Update(new[] { Spec("g-s", "line", orbs) });
        Assert.Equal(line.Vertices, GroupBackdrops.StateOf("g-s")!.Value.Vertices);
    }

    [AvaloniaFact]
    public void MovingOneOrbRefitsTheSilhouette()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        var before = GroupBackdrops.StateOf("g-a")!.Value;

        a[0].Position = new PixelPoint(a[0].Position.X - 300, a[0].Position.Y);
        GroupBackdrops.Refresh();
        var after = GroupBackdrops.StateOf("g-a")!.Value;

        Assert.True(after.Width > before.Width);
    }

    [AvaloniaFact]
    public void AnOrbWithNoPlatformWindowYetIsSkippedNotFatal()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Measure = o => o.Title == "skip" ? null : (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);
        a[0].Title = "skip";

        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        Assert.Equal(1, GroupBackdrops.ShownCount);

        GroupBackdrops.Measure = o => null;
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        Assert.Equal(0, GroupBackdrops.ShownCount);
    }

    [AvaloniaFact]
    public void ATeamMemberIsEnclosedAtItsOwnSmallerRadius()
    {
        var lead = Orb("lead", 400, 400);
        var members = Enumerable.Range(0, 6).Select(i => Orb($"m{i}", 400 + 120 * (i - 2), 560, member: true)).ToList();
        var all = new List<OrbWindow> { lead };
        all.AddRange(members);

        GroupBackdrops.Update(new[] { Spec("g-t", "heart", all) });

        var s = GroupBackdrops.StateOf("g-t")!.Value;
        Assert.True(s.Position.Y + s.Height >= 560 + 56);
        Assert.True(members[0].OrbRadius < lead.OrbRadius);
    }

    // Orbs on a heart of known centre and scale, as ComputeGroups will report it.
    private (List<OrbWindow> Orbs, GroupOutline.PlacedCurve Curve) OnHeart(int n)
    {
        var curve = new GroupOutline.PlacedCurve(new Point(600, 500), 9);
        var orbs = Enumerable.Range(0, n).Select(i =>
        {
            var (x, y) = OrbArrangement.HeartAt(2 * Math.PI * (i + 0.37) / n);
            return Orb($"hh{i}", (int)Math.Round(600 + 9 * x - 28), (int)Math.Round(500 + 9 * y - 28));
        }).ToList();
        return (orbs, curve);
    }

    [AvaloniaFact]
    public void ThePlacedCurveMakesTheBackdropHugItsOrbsInsteadOfFittingAStandIn()
    {
        var (orbs, curve) = OnHeart(10);

        GroupBackdrops.Update(new[] { Spec("g-h", "heart", orbs) });
        var standIn = GroupBackdrops.StateOf("g-h")!.Value;

        GroupBackdrops.Update(new[] { new BackdropSpec("g-h", "heart", orbs, null, curve) });
        var hugging = GroupBackdrops.StateOf("g-h")!.Value;

        Assert.True(hugging.Width * hugging.Height < standIn.Width * standIn.Height * 0.8,
            $"{hugging.Width}x{hugging.Height} vs {standIn.Width}x{standIn.Height}");
        Assert.Equal(240, hugging.Vertices);
    }

    [AvaloniaFact]
    public void ACurveThatMovedWithTheOrbsIsATranslationButARescaledOneRefits()
    {
        var (orbs, curve) = OnHeart(10);
        GroupBackdrops.Update(new[] { new BackdropSpec("g-h", "heart", orbs, null, curve) });
        var before = GroupBackdrops.StateOf("g-h")!.Value;

        foreach (var o in orbs) o.Position = new PixelPoint(o.Position.X + 25, o.Position.Y + 10);
        var moved = new GroupOutline.PlacedCurve(new Point(curve.Centre.X + 25, curve.Centre.Y + 10), curve.Scale);
        GroupBackdrops.Update(new[] { new BackdropSpec("g-h", "heart", orbs, null, moved) });
        var after = GroupBackdrops.StateOf("g-h")!.Value;

        Assert.Equal(before.Position.X + 25, after.Position.X);
        Assert.Equal(before.Width, after.Width);

        // Same discs, different curve: the outline is rebuilt around the new one.
        var bigger = new GroupOutline.PlacedCurve(moved.Centre, moved.Scale * 1.5);
        GroupBackdrops.Update(new[] { new BackdropSpec("g-h", "heart", orbs, null, bigger) });
        Assert.NotEqual(after.Width, GroupBackdrops.StateOf("g-h")!.Value.Width);
    }

    [AvaloniaFact]
    public void OutlineOfIsTheDrawnSilhouetteInScreenUnitsAndEmptyWithoutABackdrop()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });

        var outline = GroupBackdrops.OutlineOf("g-a");
        var s = GroupBackdrops.StateOf("g-a")!.Value;

        Assert.Equal(360, outline.Length);
        Assert.All(outline, p => Assert.True(
            p.X >= s.Position.X && p.X <= s.Position.X + s.Width && p.Y >= s.Position.Y && p.Y <= s.Position.Y + s.Height));
        // every orb centre is inside it by at least its own radius
        foreach (var o in a)
            Assert.True(GroupOutline.SignedDistanceInside(
                new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), outline) >= o.OrbRadius);

        Assert.Empty(GroupBackdrops.OutlineOf("nope"));
    }

    [AvaloniaFact]
    public void AnOrbReadAsNaNIsSkippedRatherThanBreakingTheRefresh()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Measure = o => o.Position.X == 100
            ? (new Point(double.NaN, 0), 1.0)
            : (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);

        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        Assert.Equal(1, GroupBackdrops.ShownCount);       // the other two still get one

        GroupBackdrops.Measure = o => (new Point(0, 0), double.PositiveInfinity);   // infinite radius
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a) });
        Assert.Equal(0, GroupBackdrops.ShownCount);
    }

    [AvaloniaFact]
    public void ThePollNeverCatchesOverAnOrbOnTheBackdrop()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });
        var s = GroupBackdrops.StateOf("g-a")!.Value;
        var onOrb = new Point(a[0].Position.X + a[0].CentreDip, a[0].Position.Y + a[0].CentreDip);

        var told = new List<bool>();
        GroupBackdrops.HitTestTick(onOrb, false, (_, c) => told.Add(c));

        Assert.Empty(told);   // inside the silhouette, but over an orb: still pass-through
        Assert.False(GroupBackdrops.StateOf("g-a")!.Value.Catching);
    }

    // ---- free mode: user groups are live, the predefined ones follow Arrange ---------

    [AvaloniaFact]
    public void FreeModeKeepsUserGroupBackdropsAndDropsThePredefinedOnes()
    {
        var (a, b) = TwoGroups();
        GroupBackdrops.SetArranged(false);
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("main", "circle", b) });

        Assert.True(GroupBackdrops.StateOf("g-a")!.Value.Shown);
        Assert.Null(GroupBackdrops.StateOf("main"));
        Assert.Equal(1, GroupBackdrops.ShownCount);

        GroupBackdrops.SetArranged(true);                       // Arrange comes on: main appears
        Assert.NotNull(GroupBackdrops.StateOf("main"));
        Assert.Equal(2, GroupBackdrops.ShownCount);

        GroupBackdrops.SetArranged(false);                      // and goes again
        Assert.Null(GroupBackdrops.StateOf("main"));
        Assert.NotNull(GroupBackdrops.StateOf("g-a"));
        GroupBackdrops.SetArranged(false);                      // idempotent
        Assert.Equal(1, GroupBackdrops.ShownCount);
    }

    [AvaloniaFact]
    public void ASnapHighlightWorksOnAUserGroupInFreeModeAndIsNothingOnAMissingOne()
    {
        var (a, b) = TwoGroups();
        GroupBackdrops.SetArranged(false);
        GroupBackdrops.Update(new[] { Spec("g-a", "heart", a), Spec("main", "circle", b) });

        GroupBackdrops.Highlight("g-a");
        Assert.True(GroupBackdrops.StateOf("g-a")!.Value.Highlighted);
        Assert.Equal(255, GroupBackdrops.StateOf("g-a")!.Value.Stroke.A);

        GroupBackdrops.Highlight("main");          // no backdrop in free mode: nothing to light
        Assert.Null(GroupBackdrops.StateOf("main"));
    }

    // A loose stack orb standing inside the group's bounding box — and, to be hard
    // on it, inside its silhouette — must keep its click: the backdrop is never
    // caught over it, even though it is not one of the group's own orbs.
    [AvaloniaFact]
    public void ALooseOrbInsideTheBackdropIsNeverCoveredByIt()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.SetArranged(false);
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });
        var s = GroupBackdrops.StateOf("g-a")!.Value;

        // Right at the middle of the group's silhouette, where it would be caught.
        var cx = (int)(s.Position.X + s.Width / 2);
        var cy = (int)(s.Position.Y + s.Height / 2);
        var loose = Orb("loose", cx - 28, cy - 28);
        var centre = new Point(cx, cy);

        var before = new List<bool>();
        GroupBackdrops.HitTestTick(centre, false, (_, c) => before.Add(c));
        Assert.Equal(new[] { true }, before);                    // no hole yet: it would catch

        GroupBackdrops.SetLooseOrbs(new[] { loose });
        GroupBackdrops.Refresh();

        var after = new List<bool>();
        GroupBackdrops.HitTestTick(new Point(centre.X + 1, centre.Y), false, (_, c) => after.Add(c));   // cursor moved a pixel
        Assert.Equal(new[] { false }, after);                    // now pass-through over the loose orb
        Assert.False(GroupBackdrops.StateOf("g-a")!.Value.Catching);

        // Just beyond the orb's circle, still inside the silhouette, it catches again.
        var clear = new Point(cx + loose.OrbRadius + 6, cy);
        var again = new List<bool>();
        GroupBackdrops.HitTestTick(clear, false, (_, c) => again.Add(c));
        Assert.Equal(new[] { true }, again);

        // A loose orb in a far corner of the box, outside the silhouette, changes nothing.
        GroupBackdrops.SetLooseOrbs(new[] { loose, Orb("far", 4000, 4000), Orb("alsoLoose", cx, cy) });
        GroupBackdrops.Refresh();
        Assert.True(GroupBackdrops.StateOf("g-a")!.Value.Shown);
    }

    // ---- the Pair preview --------------------------------------------------------

    [AvaloniaFact]
    public void APairPreviewIsAHighlightedCircleAroundBothTeamsAndNotAGroup()
    {
        var team1 = new[] { Orb("p1", 200, 200), Orb("p1m", 300, 240, member: true) };
        var team2 = new[] { Orb("p2", 420, 220) };
        var all = team1.Concat(team2).ToList();

        GroupBackdrops.PreviewPair(all);

        var p = GroupBackdrops.PreviewState!.Value;
        Assert.True(p.Shown);
        Assert.True(p.Highlighted);
        Assert.Equal(255, p.Stroke.A);                              // full opacity
        Assert.Equal(BackdropStyle.StrokeDip * 2, p.StrokeWidth);   // twice the width
        Assert.Equal(360, p.Vertices);                              // the circle

        // It is the minimum enclosing circle plus padding, not a window-centred one:
        // its width is that circle's diameter plus the slack the window keeps.
        var discs = all.Select(o => (X: (double)o.Position.X + o.CentreDip, Y: (double)o.Position.Y + o.CentreDip, R: o.OrbRadius)).ToList();
        var (_, radius) = GroupOutline.MinimumEnclosingCircle(discs);
        var expected = 2 * (radius + GroupBackdrops.PaddingDip) + 2 * GroupBackdrops.EdgeDip;
        Assert.InRange(p.Width, expected - 3, expected + 3);

        // Every orb is inside it by at least its radius plus the padding.
        var outline = GroupBackdrops.PreviewWindow!;
        Assert.NotNull(outline);
        Assert.True(p.Position.X < 200 && p.Position.X + p.Width > 420 + 56);

        // It is not a group: no backdrop, nothing in OutlineOf, not in the poll.
        Assert.Equal(0, GroupBackdrops.ShownCount);
        Assert.Empty(GroupBackdrops.OutlineOf("preview"));
        var told = new List<bool>();
        GroupBackdrops.HitTestTick(new Point(p.Position.X + p.Width / 2, p.Position.Y + p.Height / 2), false, (_, c) => told.Add(c));
        Assert.Empty(told);
    }

    [AvaloniaFact]
    public void APairPreviewIsNeverPressable()
    {
        var all = new[] { Orb("q1", 200, 200), Orb("q2", 400, 200) };
        GroupBackdrops.PreviewPair(all);
        var p = GroupBackdrops.PreviewState!.Value;

        var raised = 0;
        Action<string, PointerPressedEventArgs, Avalonia.Controls.Window> handler = (_, _, _) => raised++;
        GroupBackdrops.BackdropPressed += handler;
        try
        {
            GroupBackdrops.PreviewWindow!.MouseDown(new Point(p.Width / 2, p.Height / 2), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(0, raised);
        }
        finally { GroupBackdrops.BackdropPressed -= handler; }
    }

    [AvaloniaFact]
    public void APairPreviewFollowsTheOrbsAndGoesAwayOnNullOrAnEmptyListOrParkAll()
    {
        var a = Orb("r1", 200, 200);
        var b = Orb("r2", 400, 200);
        GroupBackdrops.PreviewPair(new[] { a, b });
        var first = GroupBackdrops.PreviewState!.Value;

        b.Position = new PixelPoint(700, 200);                       // the dragged team moves
        GroupBackdrops.Refresh();
        Assert.True(GroupBackdrops.PreviewState!.Value.Width > first.Width);

        GroupBackdrops.PreviewPair(null);
        Assert.Null(GroupBackdrops.PreviewState);

        GroupBackdrops.PreviewPair(new[] { a, b });
        Assert.NotNull(GroupBackdrops.PreviewState);
        GroupBackdrops.PreviewPair(Array.Empty<OrbWindow>());
        Assert.Null(GroupBackdrops.PreviewState);

        GroupBackdrops.PreviewPair(new[] { a, b });
        GroupBackdrops.ParkAll();
        Assert.Null(GroupBackdrops.PreviewState);

        GroupBackdrops.Measure = o => null;                          // orbs with nothing to measure
        GroupBackdrops.PreviewPair(new[] { a, b });
        Assert.Null(GroupBackdrops.PreviewState);
    }

    // The preview has a window of its own, never taken from or returned to the
    // backdrop pool, so no backdrop can inherit its click-through style (Windows
    // keeps an extended style on the HWND across Hide and Show).
    [AvaloniaFact]
    public void ThePreviewHasItsOwnWindowAndNoBackdropIsEverThatWindow()
    {
        var (a, _) = TwoGroups();
        var pool = GroupBackdrops.ParkedCount;

        GroupBackdrops.PreviewPair(a);
        var preview = GroupBackdrops.PreviewWindow!;
        Assert.Equal(pool, GroupBackdrops.ParkedCount);        // taken from nowhere

        GroupBackdrops.PreviewPair(null);
        Assert.Null(GroupBackdrops.PreviewWindow);
        Assert.Equal(pool, GroupBackdrops.ParkedCount);        // and returned nowhere

        // Many backdrops, drawn from a pool that has been through the preview.
        var orbs = Enumerable.Range(0, 12).Select(i => Orb($"n{i}", 50 + i * 90, 300)).ToList();
        GroupBackdrops.Update(orbs.Select((o, i) => Spec($"g-n{i}", "circle", new[] { o })));
        Assert.Equal(12, GroupBackdrops.ShownCount);
        for (var i = 0; i < 12; i++)
            Assert.NotSame(preview, GroupBackdrops.WindowOf($"g-n{i}"));

        // And it is the same window each time it is previewed again.
        GroupBackdrops.PreviewPair(a);
        Assert.Same(preview, GroupBackdrops.PreviewWindow);

        // Every one of those backdrops is still pressable: none went click-through.
        var s = GroupBackdrops.StateOf("g-n0")!.Value;
        var raised = 0;
        Action<string, PointerPressedEventArgs, Avalonia.Controls.Window> handler = (_, _, _) => raised++;
        GroupBackdrops.BackdropPressed += handler;
        try
        {
            GroupBackdrops.WindowOf("g-n0")!.MouseDown(new Point(s.Width / 2, s.Height / 2), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(1, raised);
        }
        finally { GroupBackdrops.BackdropPressed -= handler; }
    }

    // ---- pointer: only inside the silhouette ------------------------------------

    [AvaloniaFact]
    public void APressInsideTheSilhouetteIsRaisedAndACornerPressIsNot()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });

        var raised = new List<string>();
        Action<string, PointerPressedEventArgs, Avalonia.Controls.Window> handler = (id, _, _) => raised.Add(id);
        GroupBackdrops.BackdropPressed += handler;
        try
        {
            var w = OnlyBackdrop();
            var s = GroupBackdrops.StateOf("g-a")!.Value;

            w.MouseDown(new Point(s.Width / 2, s.Height / 2), MouseButton.Left, RawInputModifiers.None);
            w.MouseUp(new Point(s.Width / 2, s.Height / 2), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(new[] { "g-a" }, raised);

            w.MouseDown(new Point(1, 1), MouseButton.Left, RawInputModifiers.None);          // corner of the bounding box
            w.MouseUp(new Point(1, 1), MouseButton.Left, RawInputModifiers.None);
            Assert.Single(raised);

            w.MouseDown(new Point(s.Width / 2, s.Height / 2), MouseButton.Right, RawInputModifiers.None);   // not a drag
            w.MouseUp(new Point(s.Width / 2, s.Height / 2), MouseButton.Right, RawInputModifiers.None);
            Assert.Single(raised);
        }
        finally { GroupBackdrops.BackdropPressed -= handler; }
    }

    // Nobody listening is not an error: a press inside the silhouette with no
    // subscriber (tests, or before SessionManager.Start wires the drag) does
    // nothing at all. And a group with no backdrop has no window.
    [AvaloniaFact]
    public void APressWithNobodyListeningIsHarmlessAndAnUnknownGroupHasNoWindow()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });

        var s = GroupBackdrops.StateOf("g-a")!.Value;
        OnlyBackdrop().MouseDown(new Point(s.Width / 2, s.Height / 2), MouseButton.Left, RawInputModifiers.None);
        OnlyBackdrop().MouseUp(new Point(s.Width / 2, s.Height / 2), MouseButton.Left, RawInputModifiers.None);

        Assert.NotNull(GroupBackdrops.WindowOf("g-a"));
        Assert.Null(GroupBackdrops.WindowOf("g-nobody"));
    }

    [AvaloniaFact]
    public void APressStillReachesAnInvisibleBackdrop()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.SetOutlinesVisible(false);
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });

        var raised = 0;
        Action<string, PointerPressedEventArgs, Avalonia.Controls.Window> handler = (_, _, _) => raised++;
        GroupBackdrops.BackdropPressed += handler;
        try
        {
            var s = GroupBackdrops.StateOf("g-a")!.Value;
            OnlyBackdrop().MouseDown(new Point(s.Width / 2, s.Height / 2), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(1, raised);
        }
        finally { GroupBackdrops.BackdropPressed -= handler; }
    }

    private static Avalonia.Controls.Window OnlyBackdrop() => GroupBackdrops.WindowOf("g-a")!;

    // ---- the hit-test poll (macOS), driven without a window server ---------------

    [AvaloniaFact]
    public void ThePollCatchesInsideSkipsWhileAButtonIsDownAndOnlyReportsChanges()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });
        var s = GroupBackdrops.StateOf("g-a")!.Value;
        var inside = new Point(s.Position.X + s.Width / 2, s.Position.Y + s.Height / 2);
        var corner = new Point(s.Position.X + 1, s.Position.Y + 1);

        var calls = new List<bool>();
        void Apply(Avalonia.Controls.Window _, bool catches) => calls.Add(catches);

        // First tick classifies the never-classified window, explicitly.
        GroupBackdrops.HitTestTick(corner, false, Apply);
        Assert.Empty(calls);   // already pass-through from show: nothing to change

        GroupBackdrops.HitTestTick(inside, false, Apply);
        Assert.Equal(new[] { true }, calls);

        GroupBackdrops.HitTestTick(inside, false, Apply);       // cursor has not moved
        GroupBackdrops.HitTestTick(corner, true, Apply);        // button down: frozen
        Assert.Equal(new[] { true }, calls);

        GroupBackdrops.HitTestTick(corner, false, Apply);       // button released, cursor outside
        Assert.Equal(new[] { true, false }, calls);
        Assert.False(GroupBackdrops.StateOf("g-a")!.Value.Catching);
    }

    [AvaloniaFact]
    public void ANewBackdropUnderAStillCursorIsClassifiedOnTheNextTick()
    {
        var (a, _) = TwoGroups();
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });
        var s = GroupBackdrops.StateOf("g-a")!.Value;
        var inside = new Point(s.Position.X + s.Width / 2, s.Position.Y + s.Height / 2);
        GroupBackdrops.HitTestTick(inside, false, (_, _) => { });

        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.Update(new[] { Spec("g-a", "circle", a) });   // reshown: pooled window, state reset

        var told = new List<bool>();
        GroupBackdrops.HitTestTick(inside, false, (_, c) => told.Add(c));   // same cursor position
        Assert.Equal(new[] { true }, told);
    }
}

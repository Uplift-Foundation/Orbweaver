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
        GroupBackdrops.Highlight(null);
    }

    public void Dispose()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.SetOutlinesVisible(true);
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

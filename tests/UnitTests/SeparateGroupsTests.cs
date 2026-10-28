using Avalonia;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-268: SeparateGroups, the pass that gets whole groups' slot boxes
    // apart. Pure rectangles in, rectangles out — no orbs, no shapes — so each
    // rule it states is a case here rather than something to be inferred from
    // the sweep, which only ever sees it through ComputeGroups.
    public class SeparateGroupsTests
    {
        private static readonly PixelRect Work = new(0, 0, 1000, 600);

        private static bool Clear(PixelRect a, PixelRect b, int gap) =>
            a.Right + gap <= b.X || b.Right + gap <= a.X || a.Bottom + gap <= b.Y || b.Bottom + gap <= a.Y;

        [Fact]
        public void NothingMovesWhenNothingIsInTheWay()
        {
            var boxes = new[] { new PixelRect(10, 10, 100, 100), new PixelRect(300, 300, 100, 100) };

            var (moved, separated) = OrbArrangement.SeparateGroups(boxes, Work, 8);

            Assert.True(separated);
            Assert.Equal(boxes, moved);
        }

        [Fact]
        public void NoBoxesAndOneBoxAreTrivial()
        {
            Assert.Empty(OrbArrangement.SeparateGroups(Array.Empty<PixelRect>(), Work, 8).Boxes);

            var (moved, separated) = OrbArrangement.SeparateGroups(new[] { new PixelRect(5, 5, 50, 50) }, Work, 8);

            Assert.True(separated);
            Assert.Equal(new PixelRect(5, 5, 50, 50), moved[0]);
        }

        [Fact]
        public void TheLaterBoxMovesAndTheEarlierOneStays()
        {
            var first = new PixelRect(400, 200, 100, 100);
            var second = new PixelRect(420, 210, 100, 100);

            var (moved, separated) = OrbArrangement.SeparateGroups(new[] { first, second }, Work, 8);

            Assert.True(separated);
            Assert.Equal(first, moved[0]);
            Assert.True(Clear(moved[0], moved[1], 8));
            Assert.Equal(second.Width, moved[1].Width);
            Assert.Equal(second.Height, moved[1].Height);
        }

        [Fact]
        public void ItIsPushedAlongTheAxisItHasToGoLeast()
        {
            // 80 deep in x, 10 deep in y: straight down is the short way out.
            var first = new PixelRect(400, 200, 100, 100);
            var second = new PixelRect(420, 290, 100, 100);

            var (moved, _) = OrbArrangement.SeparateGroups(new[] { first, second }, Work, 0);

            Assert.Equal(420, moved[1].X);
            Assert.Equal(300, moved[1].Y);
        }

        [Fact]
        public void ItIsPushedAwayFromTheBoxThatWasInTheWayNotAlwaysRightOrDown()
        {
            // The later box is to the left and above: out through the left (or
            // top), not through the middle of the other one.
            var first = new PixelRect(400, 200, 100, 100);
            var second = new PixelRect(380, 190, 100, 100);

            var (moved, separated) = OrbArrangement.SeparateGroups(new[] { first, second }, Work, 0);

            Assert.True(separated);
            Assert.True(moved[1].Right <= first.X || moved[1].Bottom <= first.Y);
        }

        [Fact]
        public void AnEqualWayOutPrefersRightThenLeftThenDownThenUp()
        {
            var first = new PixelRect(400, 200, 100, 100);
            var same = new PixelRect(400, 200, 100, 100);

            var (moved, _) = OrbArrangement.SeparateGroups(new[] { first, same }, Work, 0);

            Assert.Equal(new PixelRect(500, 200, 100, 100), moved[1]);
        }

        [Fact]
        public void ADirectionThatLeavesTheWorkAreaIsNotTakenWhenAnotherStaysInside()
        {
            // The earlier box is jammed against the right edge, so "right" is
            // off the screen; the later box has to go left, not be slid back on
            // top of the box it was pushed away from.
            var first = new PixelRect(900, 250, 100, 100);
            var second = new PixelRect(910, 260, 80, 80);

            var (moved, separated) = OrbArrangement.SeparateGroups(new[] { first, second }, Work, 8);

            Assert.True(separated);
            Assert.True(Clear(moved[0], moved[1], 8));
            Assert.True(moved[1].X >= 0 && moved[1].Right <= Work.Right);
        }

        [Fact]
        public void BoxesOutsideTheWorkAreaAreSlidBackInEvenWithNothingToCollideWith()
        {
            var (moved, separated) = OrbArrangement.SeparateGroups(
                new[] { new PixelRect(-300, -100, 100, 100), new PixelRect(2000, 900, 100, 100) }, Work, 8);

            Assert.True(separated);
            Assert.Equal(new PixelRect(0, 0, 100, 100), moved[0]);
            Assert.Equal(new PixelRect(900, 500, 100, 100), moved[1]);
        }

        [Fact]
        public void ABoxWiderThanTheWorkAreaIsAlignedToItsNearEdge()
        {
            var (moved, _) = OrbArrangement.SeparateGroups(new[] { new PixelRect(300, 0, 1400, 100) }, Work, 8);

            Assert.Equal(0, moved[0].X);
        }

        [Fact]
        public void ManyBoxesOnTheSamePointAllEndUpApart()
        {
            var boxes = Enumerable.Repeat(new PixelRect(450, 250, 120, 120), 5).ToArray();

            var (moved, separated) = OrbArrangement.SeparateGroups(boxes, Work, 10);

            Assert.True(separated);
            for (var a = 0; a < moved.Length; a++)
            {
                Assert.True(moved[a].X >= 0 && moved[a].Y >= 0 && moved[a].Right <= Work.Right && moved[a].Bottom <= Work.Bottom);
                for (var b = a + 1; b < moved.Length; b++) Assert.True(Clear(moved[a], moved[b], 10));
            }

            Assert.Equal(boxes[0], moved[0]);   // priority: slot 0 never gives way
        }

        [Fact]
        public void BoxesThatCannotAllFitReportNotSeparated()
        {
            // Three boxes each as big as the whole work area.
            var boxes = Enumerable.Repeat(new PixelRect(0, 0, 1000, 600), 3).ToArray();

            var (moved, separated) = OrbArrangement.SeparateGroups(boxes, Work, 8);

            Assert.False(separated);
            Assert.Equal(3, moved.Length);
        }

        [Fact]
        public void TheSameBoxesGiveTheSameAnswerEveryTime()
        {
            var boxes = Enumerable.Range(0, 6).Select(i => new PixelRect(300 + i * 7, 200 + i * 3, 140, 130)).ToArray();

            var a = OrbArrangement.SeparateGroups(boxes, Work, 8);
            var b = OrbArrangement.SeparateGroups(boxes, Work, 8);

            Assert.Equal(a.Boxes, b.Boxes);
            Assert.Equal(a.Separated, b.Separated);
        }

        [Fact]
        public void AMoveIsATranslationAndNeverResizesABox()
        {
            var boxes = new[] { new PixelRect(100, 100, 200, 150), new PixelRect(150, 120, 90, 60), new PixelRect(160, 130, 40, 300) };

            var (moved, _) = OrbArrangement.SeparateGroups(boxes, Work, 6);

            for (var i = 0; i < boxes.Length; i++)
            {
                Assert.Equal(boxes[i].Width, moved[i].Width);
                Assert.Equal(boxes[i].Height, moved[i].Height);
            }
        }

        [Fact]
        public void WhenNothingIsClearOfEveryEarlierBoxItTakesTheNearestWayOutOfTheFirstInTheWay()
        {
            // Three 90-wide boxes fill a 300-wide strip with 8px gaps; a fourth
            // has nowhere clear. Beside the first box is inside the work area
            // but on top of the second, so no candidate is clear of all of
            // them and it falls back to the first box's own nearest exit — and
            // the answer honestly says they are not separated.
            var strip = new PixelRect(0, 0, 300, 100);
            var boxes = new[]
            {
                new PixelRect(0, 0, 90, 100), new PixelRect(98, 0, 90, 100),
                new PixelRect(196, 0, 90, 100), new PixelRect(100, 0, 90, 100),
            };

            var (moved, separated) = OrbArrangement.SeparateGroups(boxes, strip, 8);

            Assert.False(separated);
            Assert.Equal(boxes.Take(3), moved.Take(3));
            Assert.All(moved, box => Assert.True(box.X >= 0 && box.Right <= 300));
        }
    }
}

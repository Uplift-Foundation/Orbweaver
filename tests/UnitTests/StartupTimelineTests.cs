using System.Collections.Generic;
using Orbweaver;
using Xunit;

namespace UnitTests
{
    public class StartupTimelineTests
    {
        private static (StartupTimeline Timeline, List<string> Lines) Make(double[] clock)
        {
            var lines = new List<string>();
            var i = 0;
            return (new StartupTimeline(() => clock[i++ % clock.Length], lines.Add), lines);
        }

        [Fact]
        public void A_step_is_written_with_its_offset_from_process_start()
        {
            var (t, lines) = Make([1234.9]);
            t.Mark("first scan requested");
            Assert.Equal(["startup: +1234 ms first scan requested"], lines);
        }

        [Fact]
        public void It_stops_once_the_first_apply_is_done_and_an_orb_has_shown()
        {
            var (t, lines) = Make([1]);
            t.FirstApplyDone();
            Assert.False(t.Finished);
            t.OrbShown(1);
            Assert.True(t.Finished);
            var written = lines.Count;
            t.Mark("later step");
            t.OrbShown(2);
            Assert.Equal(written, lines.Count);
            Assert.Contains("orb window shown (#1)", lines[^1]);
        }

        [Fact]
        public void It_writes_at_most_MaxLines()
        {
            var (t, lines) = Make([1]);
            for (var n = 0; n < StartupTimeline.MaxLines + 10; n++) t.Mark("step");
            Assert.Equal(StartupTimeline.MaxLines, lines.Count);
            Assert.True(t.Finished);
        }
    }
}

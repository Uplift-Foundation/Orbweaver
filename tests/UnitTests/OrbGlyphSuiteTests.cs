using Xunit;

namespace Orbweaver.Tests
{
    // OrbGlyph and ChatSpeaker, as tests rather than as an exe you have to
    // remember to run.
    //
    // The tables live in tests/GlyphTests/GlyphSuite.cs, which this project
    // compiles in, so `dotnet run --project tests/GlyphTests` and these tests
    // check the identical cases. Only the reporting differs, and here it differs
    // deliberately: unlike the arrangement sweep's 20736 cases, these tables are
    // small enough that one xUnit case per row is worth the discovery cost, and
    // a named failing row is a better message than a list printed by a Fact.
    //
    // The rows are projected to plain strings and bools rather than passed as
    // the suite's own record structs. xUnit has to serialize theory data to
    // report and re-run an individual case, and a struct it cannot serialize
    // turns every row into the same unhelpful display name.
    public class OrbGlyphSuiteTests
    {
        public static IEnumerable<object[]> GlyphRows()
            => GlyphSuite.Glyphs.Select(c => new object[] { c.Group, c.Input, c.TwoLetter, c.Want });

        public static IEnumerable<object[]> InitialsRows()
            => GlyphSuite.Initials.Select(c => new object[] { c.Input, c.Want });

        public static IEnumerable<object?[]> SpeakerRows()
            => GlyphSuite.Speakers.Select(c => new object?[] { c.Why, c.Identity, c.Title, c.Previous, c.Want });

        public static IEnumerable<object[]> FallbackRows()
            => GlyphSuite.Fallbacks.Select(c => new object[] { c.Why, c.IsRoom, c.Want });

        public static IEnumerable<object?[]> ClipRows()
            => GlyphSuite.Clips.Select(c => new object?[] { c.Why, c.Input, c.TwoLetter, c.Want });

        [Theory]
        [MemberData(nameof(ClipRows))]
        public void ClipKeepsWholeGraphemes(string why, string? input, bool twoLetter, string want)
        {
            Assert.Null(GlyphSuite.CheckClip(new GlyphSuite.ClipCase(why, input, twoLetter, want)));
        }

        [Theory]
        [MemberData(nameof(GlyphRows))]
        public void OrbWearsTheRightLetters(string group, string input, bool twoLetter, string want)
        {
            var failure = GlyphSuite.CheckGlyph(new GlyphSuite.GlyphCase(group, input, twoLetter, want));
            Assert.Null(failure);
        }

        [Theory]
        [MemberData(nameof(InitialsRows))]
        public void HeaderWearsTheRightInitials(string input, string want)
        {
            var failure = GlyphSuite.CheckInitials(new GlyphSuite.InitialsCase(input, want));
            Assert.Null(failure);
        }

        [Theory]
        [MemberData(nameof(SpeakerRows))]
        public void MessageBelongsToTheRightSpeaker(
            string why, string? identity, string? title, string? previous, string? want)
        {
            var failure = GlyphSuite.CheckSpeaker(
                new GlyphSuite.SpeakerCase(why, identity, title, previous, want));
            Assert.Null(failure);
        }

        // CB-36: whether an unattributed assistant turn's chip may borrow the
        // sole speaker's name — false only for a room, where an unattributed
        // turn means several agents talk here and none could be named, not
        // that the one agent said it.
        [Theory]
        [MemberData(nameof(FallbackRows))]
        public void UnattributedTurnFallsBackOnlyOutsideARoom(string why, bool isRoom, bool want)
        {
            var failure = GlyphSuite.CheckFallback(new GlyphSuite.FallbackCase(why, isRoom, want));
            Assert.Null(failure);
        }

        // A separate test rather than a row, for the reason the suite gives: the
        // Initials table is not nullable, and widening it to hold one null would
        // weaken the type of every other row.
        [Fact]
        public void HeaderInitialsOfNullIsEmpty()
        {
            Assert.Null(GlyphSuite.CheckNullInitials());
        }
    }
}

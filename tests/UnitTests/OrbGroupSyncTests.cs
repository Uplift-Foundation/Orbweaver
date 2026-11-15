using Xunit;

namespace Orbweaver.Tests;

// CB-268 S7: CB-10's key change, noticed so a membership can be carried to the
// new key. (T3's late-member write is OrbweaverSettings.EnsureTeamMembership,
// tested in OrbGroupsTests; SessionManagerGroupsTests covers the scan calling it.)
public class OrbGroupSyncTests
{
    [Fact]
    public void TheKeyTrackerReportsAChangedKeyOnceAndOnlyThat()
    {
        var tracker = new OrbGroupSync.KeyTracker();

        // First sight: nothing to carry.
        Assert.Empty(tracker.Observe(new[] { ("a", "session-a"), ("b", "/dir\nb") }));

        // CB-10: the title arrives and the key changes.
        Assert.Equal(new[] { ("session-a", "/dir\nnamed") },
            tracker.Observe(new[] { ("a", "/dir\nnamed"), ("b", "/dir\nb") }));

        // Unchanged, or changed only in case (keys compare ignoring case), is no carry.
        Assert.Empty(tracker.Observe(new[] { ("a", "/DIR\nNAMED"), ("b", "/dir\nb") }));
    }

    [Fact]
    public void AnEmptyKeyIsSkippedAndAForgottenSessionStartsAgain()
    {
        var tracker = new OrbGroupSync.KeyTracker();
        tracker.Observe(new[] { ("a", "first") });

        // A momentarily empty key neither carries nor overwrites the last real one.
        Assert.Empty(tracker.Observe(new[] { ("a", "") }));
        Assert.Equal(new[] { ("first", "second") }, tracker.Observe(new[] { ("a", "second") }));

        // Gone from the screen is forgotten, so coming back is a first sighting.
        Assert.Empty(tracker.Observe(Array.Empty<(string, string)>()));
        Assert.Empty(tracker.Observe(new[] { ("a", "third") }));
    }

    // CB-281: an orb's group changing with nobody arriving.
    [Fact]
    public void TheGroupTrackerReportsAMovedOrbWithBothGroupsAndNothingOnFirstSight()
    {
        var tracker = new OrbGroupSync.GroupTracker();

        Assert.Empty(tracker.Observe(new[] { ("a", "main"), ("b", "g-1") }));

        Assert.Equal(new[] { ("a", "main", "a-cli-000000000001") },
            tracker.Observe(new[] { ("a", "a-cli-000000000001"), ("b", "g-1") }));

        // Ids compare exactly: unchanged is no change.
        Assert.Empty(tracker.Observe(new[] { ("a", "a-cli-000000000001"), ("b", "g-1") }));
    }

    [Fact]
    public void TheGroupTrackerForgetsAnOrbThatLeftSoItsReturnIsAFirstSighting()
    {
        var tracker = new OrbGroupSync.GroupTracker();
        tracker.Observe(new[] { ("a", "main") });

        Assert.Empty(tracker.Observe(Array.Empty<(string, string)>()));
        Assert.Empty(tracker.Observe(new[] { ("a", "a-dir-000000000002") }));
    }
}

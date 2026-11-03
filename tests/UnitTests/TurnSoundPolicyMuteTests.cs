using Xunit;

namespace Orbweaver.Tests;

// CB-298: the decision layer of the global mute. A muted snapshot has to
// answer Silent from both Decide and ResolveOne, for every shape of signal the
// unmuted tests already use, and an unmuted one has to answer exactly what it
// always did — asserted against the unmuted call rather than a hand-written
// expectation, so this can't drift from TurnSoundPolicyTests.
public class TurnSoundPolicyMuteTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SoundSettingsSnapshot Snapshot(
        bool muted, string? finished = null, string? attention = null,
        OrbweaverSettings.OrbTurnSound? over = null)
    {
        Func<string?, string?> resolve = s => s is null ? "/default/sound.aiff" : "/sounds/" + s + ".aiff";
        return new SoundSettingsSnapshot(
            true, finished, attention, _ => over, resolve, resolve, Muted: muted);
    }

    public static IEnumerable<object?[]> Shapes()
    {
        foreach (var attention in new[] { false, true })
        foreach (var finished in new string?[] { null, "off", "summary", "Glass" })
        foreach (var busy in new[] { false, true })
            yield return new object?[] { attention, finished, busy };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void MutedIsSilentFromDecideAndResolveOne(bool attention, string? setting, bool busy)
    {
        var ev = new TurnSoundEvent(attention ? TurnSignal.NeedsAttention : TurnSignal.Finished, "k", "s");

        Assert.Equal(SoundActionKind.Silent,
            TurnSoundPolicy.Decide(new[] { ev }, Snapshot(true, setting, setting), Now, DateTime.MinValue, busy).Kind);
        Assert.Equal(SoundActionKind.Silent,
            TurnSoundPolicy.ResolveOne(ev, Snapshot(true, setting, setting), busy).Kind);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void UnmutedIsExactlyWhatItWasBefore(bool attention, string? setting, bool busy)
    {
        var ev = new TurnSoundEvent(attention ? TurnSignal.NeedsAttention : TurnSignal.Finished, "k", "s");

        // The default of the new parameter is the old behaviour, so a snapshot
        // built without it is the control.
        var control = new SoundSettingsSnapshot(true, setting, setting, _ => null,
            s => s is null ? "/default/sound.aiff" : "/sounds/" + s + ".aiff",
            s => s is null ? "/default/sound.aiff" : "/sounds/" + s + ".aiff");

        Assert.Equal(
            TurnSoundPolicy.Decide(new[] { ev }, control, Now, DateTime.MinValue, busy),
            TurnSoundPolicy.Decide(new[] { ev }, Snapshot(false, setting, setting), Now, DateTime.MinValue, busy));
        Assert.Equal(
            TurnSoundPolicy.ResolveOne(ev, control, busy),
            TurnSoundPolicy.ResolveOne(ev, Snapshot(false, setting, setting), busy));
    }

    [Fact]
    public void AnOverrideDoesNotEscapeTheMute()
    {
        var over = new OrbweaverSettings.OrbTurnSound("Glass", null);
        var ev = new TurnSoundEvent(TurnSignal.Finished, "k", "s");

        Assert.Equal(SoundActionKind.Silent,
            TurnSoundPolicy.ResolveOne(ev, Snapshot(true, over: over), false).Kind);
    }
}

using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-298: the mute switch, its persistence, what it stops, and every place
// that has to honour it. One class, in the Settings collection, because every
// case flips the process-wide Muted flag and a parallel class reading it would
// see a different app. Nothing here makes a sound: StopEverythingPlaying is
// observed through StopGeneration and a fake chime process, and the real-
// process arm of ChimePlayer is only ever asked about a path that does not
// exist.
[Collection("Settings")]
public class AudioMuteTests : IDisposable
{
    private readonly ScopedSettingsDir _dir = new("audiomute");

    public AudioMuteTests()
    {
        SpeechRequest.UtteranceForTests = null;
        SpeechSummary.SummarizerForTests = null;
    }

    public void Dispose()
    {
        TextToSpeech.SilenceForTests = true;
        SpeechRequest.UtteranceForTests = null;
        SpeechSummary.SummarizerForTests = null;
        ChimePlayer.PlayForTests = null;
        ChimePlayer.SilenceForTests = true;
        AudioMute.Set(false);
        _dir.Dispose();
    }

    private sealed class FakeChime : ChimePlayer.IChimeProcess
    {
        public int Id => 1;
        public bool HasExited { get; private set; }
        public int Kills;
        public bool WaitForExit(int milliseconds) => HasExited;
        public void Kill() { Kills++; HasExited = true; }
        public void Dispose() { }
    }

    [Fact]
    public void AllowsIsTheNegationOfMuted()
    {
        Assert.True(AudioMute.Allows(false));
        Assert.False(AudioMute.Allows(true));
    }

    [AvaloniaFact]
    public void FreshInstallIsNotMuted() => Assert.False(AudioMute.IsMuted);

    [AvaloniaFact]
    public void SetPersistsBothWaysAndRaisesChangedWithTheNewState()
    {
        var seen = new List<bool>();
        void Handler(bool m) => seen.Add(m);
        AudioMute.Changed += Handler;
        try
        {
            AudioMute.Set(true);
            Assert.True(AudioMute.IsMuted);
            OrbweaverSettings.ReloadForTests();
            Assert.True(OrbweaverSettings.Muted);

            AudioMute.Set(false);
            OrbweaverSettings.ReloadForTests();
            Assert.False(OrbweaverSettings.Muted);

            Assert.Equal(new[] { true, false }, seen);
        }
        finally { AudioMute.Changed -= Handler; }
    }

    [AvaloniaFact]
    public void SettingTheStateItAlreadyHasRaisesNothingAndStopsNothing()
    {
        var raised = 0;
        void Handler(bool _) => raised++;
        AudioMute.Changed += Handler;
        try
        {
            var stop = TextToSpeech.StopGeneration;
            AudioMute.Set(false);
            Assert.Equal(0, raised);
            Assert.Equal(stop, TextToSpeech.StopGeneration);
        }
        finally { AudioMute.Changed -= Handler; }
    }

    [AvaloniaFact]
    public void ToggleFlipsEachTime()
    {
        AudioMute.Toggle();
        Assert.True(AudioMute.IsMuted);
        AudioMute.Toggle();
        Assert.False(AudioMute.IsMuted);
    }

    [AvaloniaFact]
    public void MutingCancelsSpeechAndKillsEveryLiveChimeButUnmutingDoesNot()
    {
        var chime = new FakeChime();
        ChimePlayer.TrackForTests(chime);
        var stop = TextToSpeech.StopGeneration;

        AudioMute.Set(true);

        Assert.True(TextToSpeech.StopGeneration > stop);
        Assert.Equal(1, chime.Kills);

        var after = TextToSpeech.StopGeneration;
        AudioMute.Set(false);
        Assert.Equal(after, TextToSpeech.StopGeneration);
    }

    // StopAll is sticky by contract and would leave the app deaf after unmute.
    [AvaloniaFact]
    public void StopPlayingIsNotSticky()
    {
        ChimePlayer.StopPlaying();
        Assert.False(ChimePlayer.IsStopped);

        var played = new List<string>();
        ChimePlayer.PlayForTests = played.Add;
        ChimePlayer.Play("/x.aiff");
        Assert.Single(played);
    }

    [AvaloniaFact]
    public void MuteChangesExactlyOneKeyInTheSettingsFile()
    {
        OrbweaverSettings.SpeechVolume = 0.37;
        OrbweaverSettings.AlertVolume = 0.61;
        OrbweaverSettings.TurnSoundsEnabled = false;
        OrbweaverSettings.FlushPendingSave();
        var before = JsonNode.Parse(File.ReadAllText(OrbweaverSettings.Path_))!.AsObject();

        AudioMute.Set(true);
        OrbweaverSettings.FlushPendingSave();
        var after = JsonNode.Parse(File.ReadAllText(OrbweaverSettings.Path_))!.AsObject();

        var differing = after.Select(kv => kv.Key)
            .Union(before.Select(kv => kv.Key))
            .Where(k => before[k]?.ToJsonString() != after[k]?.ToJsonString())
            .ToList();
        Assert.Equal(new[] { "muted" }, differing);
        Assert.Equal(0.37, OrbweaverSettings.SpeechVolume);
        Assert.Equal(0.61, OrbweaverSettings.AlertVolume);
        Assert.False(OrbweaverSettings.TurnSoundsEnabled);
    }

    // The Post arm: a caller off the UI thread still gets Changed on it.
    [AvaloniaFact]
    public async Task ChangedIsRaisedOnTheUiThreadWhenSetFromAnotherThread()
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(bool _) => tcs.TrySetResult(Dispatcher.UIThread.CheckAccess());
        AudioMute.Changed += Handler;
        try
        {
            await Task.Run(() => AudioMute.Set(true));
            Dispatcher.UIThread.RunJobs();
            Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { AudioMute.Changed -= Handler; }
    }

    // The null-subscriber arms of `Changed?.Invoke`, on the UI thread and in
    // the posted lambda. Other tests' controllers and panels subscribe to this
    // static event and are never unsubscribed, so the invocation list is taken
    // off for the duration and put back after.
    private static Action<bool>? DetachAllSubscribers()
    {
        var field = typeof(AudioMute).GetField(
            "Changed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var all = (Action<bool>?)field.GetValue(null);
        field.SetValue(null, null);
        return all;
    }

    private static void Reattach(Action<bool>? all) =>
        typeof(AudioMute).GetField("Changed", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, all);

    [AvaloniaFact]
    public void SetWithNoSubscriberOnTheUiThreadStillFlipsTheState()
    {
        var all = DetachAllSubscribers();
        try
        {
            AudioMute.Set(true);
            Assert.True(AudioMute.IsMuted);
        }
        finally { Reattach(all); }
    }

    [AvaloniaFact]
    public async Task SetWithNoSubscriberFromAPoolThreadStillFlipsTheState()
    {
        var all = DetachAllSubscribers();
        try
        {
            await Task.Run(() => AudioMute.Set(true));
            Dispatcher.UIThread.RunJobs();
            Assert.True(AudioMute.IsMuted);
        }
        finally { Reattach(all); }
    }

    // --- the process boundary ---

    [Fact]
    public void SuppressedIsTheTestGuardOrTheMute()
    {
        TextToSpeech.SilenceForTests = false;
        Assert.False(TextToSpeech.Suppressed);

        TextToSpeech.SilenceForTests = true;
        Assert.True(TextToSpeech.Suppressed);

        TextToSpeech.SilenceForTests = false;
        OrbweaverSettings.Muted = true;
        Assert.True(TextToSpeech.Suppressed);
    }

    // With SilenceForTests off, only the mute stands between this call and a
    // real afplay — so the path is one that does not exist.
    [Fact]
    public void MutedChimePlayerBuildsNoProcess()
    {
        ChimePlayer.SilenceForTests = false;
        ChimePlayer.ProcessFactoryForTests = null;
        OrbweaverSettings.Muted = true;
        var start = typeof(ChimePlayer).GetMethod(
            "StartProcess", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Null(start.Invoke(null, new object[] { "/nonexistent/orbweaver-mute-test.aiff" }));
    }

    // The factory returns before the guard on purpose: a fake makes no sound.
    [Fact]
    public void AFakeProcessStillStartsWhileMuted()
    {
        var fake = new FakeChime();
        ChimePlayer.ProcessFactoryForTests = _ => fake;
        try
        {
            OrbweaverSettings.Muted = true;
            var start = typeof(ChimePlayer).GetMethod(
                "StartProcess", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Same(fake, start.Invoke(null, new object[] { "/x.aiff" }));
        }
        finally { ChimePlayer.ProcessFactoryForTests = null; }
    }

    [Fact]
    public void TheTurnSoundSnapshotCarriesTheMute()
    {
        var snapshot = typeof(TurnSounds).GetMethod(
            "Snapshot", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.False(((SoundSettingsSnapshot)snapshot.Invoke(null, null)!).Muted);
        OrbweaverSettings.Muted = true;
        Assert.True(((SoundSettingsSnapshot)snapshot.Invoke(null, null)!).Muted);
    }

    // --- the request layer ---

    [AvaloniaFact]
    public void MutedSpeakAndSpeakTurnSummaryUtterNothingAndAskNoSummariser()
    {
        var uttered = new List<string>();
        var summariserCalls = 0;
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        SpeechSummary.SummarizerForTests = _ => { summariserCalls++; return Task.FromResult<string?>("s"); };
        OrbweaverSettings.Muted = true;
        var state = TextToSpeech.State;
        var longReply = string.Join(" ", Enumerable.Repeat("A sentence of some length.", 60));

        SpeechRequest.Speak("Hello there, this is a reply.", "s1");
        SpeechRequest.SpeakTurnSummary(longReply, "s1");

        Assert.Empty(uttered);
        Assert.Equal(0, summariserCalls);
        Assert.Equal(state, TextToSpeech.State);
    }

    // The control for the case above: the same call, unmuted, is heard.
    [AvaloniaFact]
    public void UnmutedSpeakStillUtters()
    {
        var uttered = new List<string>();
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        OrbweaverSettings.SpeakScope = SpeakScope.Full;

        SpeechRequest.Speak("Hello there, this is a reply.", "s1");

        Assert.Single(uttered);
    }

    // A summary whose round trip is still out when the mute lands is dropped
    // by Cancel's StopGeneration bump — no new code, pinned here.
    [AvaloniaFact]
    public async Task ASummaryInFlightWhenTheMuteLandsIsNeverSpoken()
    {
        var uttered = new List<string>();
        var release = new TaskCompletionSource<string?>();
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        SpeechSummary.SummarizerForTests = _ => release.Task;

        var pending = SpeechRequest.SpeakSummaryAsync("A reply to summarise.", "s1");
        AudioMute.Set(true);
        release.SetResult("the summary");
        await pending;

        Assert.Empty(uttered);
    }
}

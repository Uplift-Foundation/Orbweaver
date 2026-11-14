using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Speak = Orbweaver.TextToSpeech.SpeakState;

namespace Orbweaver.Tests;

// CB-222: the settings window's voice preview. The decisions are pure and named,
// so most of this needs no window and no process; what it does need is the real
// TextToSpeech state and generation counter, because "is this preview mine" is
// answered by them. TextToSpeech.SilenceForTests is true in this assembly, and
// every speak and cancel below goes through a VoicePreview seam besides, so no
// audio is possible.
//
// [Collection("Settings")] only for its serialising effect: TextToSpeech's state
// and StopGeneration are process-wide, and a neighbour calling Enter() or
// Cancel() in the middle of a case here would move what it asserts.
[Collection("Settings")]
public class VoicePreviewTests : IDisposable
{
    private static readonly TextToSpeech.VoiceOption Kokoro =
        new(TextToSpeech.SpeakEngine.Neural, "af_heart", "af_heart (Kokoro)");
    private static readonly TextToSpeech.VoiceOption Sapi =
        new(TextToSpeech.SpeakEngine.System, "Microsoft Zira Desktop", "Microsoft Zira Desktop (system)");

    private readonly List<(string Text, TextToSpeech.VoiceOption Voice)> _spoken = new();
    private int _cancels;
    private int _changed;

    public VoicePreviewTests()
    {
        Reset();
        VoicePreview.UiPostForTests = a => a();
        VoicePreview.Changed += CountChange;

        // What the real Speak does to the shared state, minus the process: it
        // cancels first (moving the generation) and then reports audio.
        VoicePreview.SpeakForTests = (text, voice) =>
        {
            _spoken.Add((text, voice));
            TextToSpeech.Cancel();
            TextToSpeech.Enter(Speak.Speaking);
        };
        VoicePreview.CancelForTests = () => _cancels++;
    }

    public void Dispose()
    {
        VoicePreview.Changed -= CountChange;
        Reset();
    }

    private void CountChange() => Interlocked.Increment(ref _changed);

    private static void Reset()
    {
        VoicePreview.ResetForTests();
        TextToSpeech.Cancel();
        TextToSpeech.Enter(Speak.Idle);
    }

    // ---- LookFor / Ended --------------------------------------------------------

    [Theory]
    [InlineData(false, null, 5, Speak.Speaking, Speak.Idle)]     // not requested at all
    [InlineData(false, 5, 5, Speak.Speaking, Speak.Idle)]        // ...even if a stale owner is left
    [InlineData(true, null, 5, Speak.Idle, Speak.Preparing)]     // queued: instant feedback
    [InlineData(true, null, 5, Speak.Speaking, Speak.Preparing)] // queued behind someone else's speech
    [InlineData(true, 4, 5, Speak.Speaking, Speak.Idle)]         // superseded by other speech
    [InlineData(true, 5, 5, Speak.Idle, Speak.Idle)]             // owned, finished
    [InlineData(true, 5, 5, Speak.Preparing, Speak.Preparing)]   // owned, engine warming up
    [InlineData(true, 5, 5, Speak.Speaking, Speak.Speaking)]     // owned, audio playing
    public void LookForCoversEveryArm(bool requested, int? owned, int current, Speak global, Speak expected)
    {
        Assert.Equal(expected, VoicePreview.LookFor(requested, owned, current, global));
    }

    [Theory]
    [InlineData(false, 5, 5, Speak.Idle, false)]      // nothing wanted
    [InlineData(true, null, 5, Speak.Idle, false)]    // queued: Speak's own Cancel() reports Idle before it starts
    [InlineData(true, 5, 5, Speak.Idle, true)]        // finished by itself
    [InlineData(true, 4, 5, Speak.Speaking, true)]    // superseded
    [InlineData(true, 5, 5, Speak.Speaking, false)]   // still playing
    [InlineData(true, 5, 5, Speak.Preparing, false)]  // still warming up
    public void EndedOnlyOnceAStartedPreviewLooksIdle(
        bool requested, int? owned, int current, Speak global, bool expected)
    {
        Assert.Equal(expected, VoicePreview.Ended(requested, owned, current, global));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    public void AQueuedStartRunsOnlyWhileItsRequestIsCurrent(int enqueued, int current, bool expected)
    {
        Assert.Equal(expected, VoicePreview.ShouldStartNow(enqueued, current));
    }

    [Theory]
    [InlineData(null, 5, false)]  // never got going: nothing of ours to stop
    [InlineData(5, 5, true)]      // ours and still current
    [InlineData(4, 5, false)]     // somebody else has spoken since
    public void StoppingOnlyCancelsSpeechThePreviewStarted(int? owned, int current, bool expected)
    {
        Assert.Equal(expected, VoicePreview.ShouldCancel(owned, current));
    }

    [Fact]
    public void PreviewTargetIsNullUntilThePickerHasBeenScanned()
    {
        var options = new List<TextToSpeech.VoiceOption> { Sapi, Kokoro };

        Assert.Null(VoicePreview.PreviewTarget(null, 0));
        Assert.Null(VoicePreview.PreviewTarget(options, -1));
        Assert.Null(VoicePreview.PreviewTarget(options, 2));
        Assert.Same(Kokoro, VoicePreview.PreviewTarget(options, 1));
    }

    // ---- the chain --------------------------------------------------------------

    [Fact]
    public async Task ToggleFromIdleSpeaksTheSampleInTheChosenVoice()
    {
        await VoicePreview.Toggle(Kokoro);

        var (text, voice) = Assert.Single(_spoken);
        Assert.Equal(VoicePreview.SampleText, text);
        Assert.Same(Kokoro, voice);
        Assert.Equal(Speak.Speaking, VoicePreview.Look);
    }

    [Fact]
    public async Task ANullTargetIsResolvedFromTheSavedSelection()
    {
        VoicePreview.ResolveSavedForTests = () => Sapi;

        await VoicePreview.Toggle(null);

        Assert.Same(Sapi, Assert.Single(_spoken).Voice);
    }

    // The worker is held inside resolve until the Preparing assertion has run.
    // Without the hold, the test thread racing the queued worker decides the
    // answer: a loaded machine preempting it for long enough lets the worker
    // finish first, and the snapshot reads Idle (CB-280).
    [Fact]
    public async Task NothingToSpeakWithEndsThePreviewWithoutSpeaking()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        VoicePreview.ResolveSavedForTests = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        };

        var task = VoicePreview.Toggle(null);
        try
        {
            Assert.Equal(Speak.Preparing, VoicePreview.Look);   // answered before any engine ran
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(Speak.Preparing, VoicePreview.Look);   // and still so while the worker resolves
        }
        finally
        {
            // Released and drained on every path, so a failed assertion above can
            // neither dispose the gate under a waiting worker nor leave that worker
            // running into the next case of this serialised collection.
            release.Set();
            await task;
        }

        Assert.Empty(_spoken);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    [Fact]
    public async Task ToggleWhileLiveStopsInsteadOfStartingAgain()
    {
        await VoicePreview.Toggle(Kokoro);
        await VoicePreview.Toggle(Kokoro);

        Assert.Single(_spoken);
        Assert.Equal(1, _cancels);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    [Fact]
    public async Task StoppingAPreviewThatNeverStartedNeverStartsItsEngine()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "first", "first");
        var second = new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "second", "second");

        VoicePreview.SpeakForTests = (_, voice) =>
        {
            _spoken.Add((VoicePreview.SampleText, voice));
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        _ = VoicePreview.Toggle(first);           // A: running, held inside Speak
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        _ = VoicePreview.Toggle(second);          // stop A (Preparing, so this is a stop)
        _ = VoicePreview.Toggle(second);          // B: queued behind A
        Assert.Equal(Speak.Preparing, VoicePreview.Look);
        var last = VoicePreview.Toggle(second);   // stop B before it ever ran

        release.Set();
        await last;

        // A was already inside Speak, so it did start — and the stop that landed
        // mid-start cancelled it. B never reached its engine.
        Assert.Equal(new[] { "first" }, _spoken.ConvertAll(s => s.Voice.Name));
        Assert.Equal(1, _cancels);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    [Fact]
    public async Task ANewerStartThatReplacedAStopMidStartIsNotCancelledByTheOlderOne()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "first", "first");
        var calls = 0;

        VoicePreview.SpeakForTests = (_, voice) =>
        {
            _spoken.Add((VoicePreview.SampleText, voice));
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                TextToSpeech.Cancel();
                TextToSpeech.Enter(Speak.Speaking);
            }
        };

        _ = VoicePreview.Toggle(first);           // A: running, held
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        _ = VoicePreview.Toggle(first);           // stop A
        var b = VoicePreview.Toggle(Kokoro);      // B: wanted, queued

        release.Set();
        await b;

        // A finished Speak after B had been asked for, so A leaves it to B's own
        // Speak (which cancels first) instead of cancelling on its own account.
        Assert.Equal(0, _cancels);
        Assert.Equal(2, _spoken.Count);
        Assert.Equal(Speak.Speaking, VoicePreview.Look);
    }

    [Fact]
    public async Task StopIfLiveCancelsASpeakingPreview()
    {
        await VoicePreview.Toggle(Kokoro);

        await VoicePreview.StopIfLive();

        Assert.Equal(1, _cancels);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    [Fact]
    public async Task StopIfLiveWithNothingLiveCancelsNothing()
    {
        await VoicePreview.StopIfLive();

        Assert.Equal(0, _cancels);
    }

    // The stop path runs on the same serial chain as every start, so a Cancel()
    // that throws must not fault it: the caller's await, and every preview queued
    // behind, would inherit the exception.
    [Fact]
    public async Task AStopWhoseCancelThrowsDoesNotFaultTheChain()
    {
        await VoicePreview.Toggle(Kokoro);
        VoicePreview.CancelForTests = () => throw new InvalidOperationException("taskkill failed");

        await VoicePreview.StopIfLive();   // would rethrow if the chain had faulted

        Assert.Equal(Speak.Idle, VoicePreview.Look);

        VoicePreview.CancelForTests = () => _cancels++;
        await VoicePreview.Toggle(Sapi);   // the chain still works afterwards
        Assert.Equal(Speak.Speaking, VoicePreview.Look);
    }

    // AC 9 and AC 11 together: another read-aloud takes the speaker, the preview
    // is over, and stopping it afterwards must not stop the read-aloud.
    [Fact]
    public async Task ASupersededPreviewIsIdleAndIsNotCancelled()
    {
        await VoicePreview.Toggle(Kokoro);
        Assert.Equal(Speak.Speaking, VoicePreview.Look);

        // A read-aloud starting: Speak begins with Cancel, then reports its own.
        TextToSpeech.Cancel();
        TextToSpeech.Enter(Speak.Speaking);

        Assert.Equal(Speak.Idle, VoicePreview.Look);   // does not mirror the read-aloud

        await VoicePreview.StopIfLive();

        Assert.Equal(0, _cancels);
        Assert.Equal(Speak.Speaking, TextToSpeech.State);   // the read-aloud carries on
    }

    [Fact]
    public async Task SpeechThatFollowsAFinishedPreviewIsNotMirrored()
    {
        await VoicePreview.Toggle(Kokoro);

        TextToSpeech.Enter(Speak.Idle);         // it finished by itself
        TextToSpeech.Enter(Speak.Speaking);     // someone else, without even a Cancel

        Assert.Equal(Speak.Idle, VoicePreview.Look);
        await VoicePreview.StopIfLive();
        Assert.Equal(0, _cancels);
    }

    [Fact]
    public async Task LookFollowsTheSharedStateWhileThePreviewIsLive()
    {
        await VoicePreview.Toggle(Kokoro);

        TextToSpeech.Enter(Speak.Preparing);
        Assert.Equal(Speak.Preparing, VoicePreview.Look);

        TextToSpeech.Enter(Speak.Speaking);
        Assert.Equal(Speak.Speaking, VoicePreview.Look);

        TextToSpeech.Enter(Speak.Idle);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    // The engine that fails at once: no state change ever arrives after Speak
    // returns, so the worker has to look again itself or the button would sit on
    // Preparing for good. And nothing was substituted — Speak was called once.
    [Fact]
    public async Task ACommandThatFailsAtOnceLeavesTheButtonIdle()
    {
        VoicePreview.SpeakForTests = (text, voice) =>
        {
            _spoken.Add((text, voice));
            TextToSpeech.Cancel();
            TextToSpeech.Enter(Speak.Preparing);
            TextToSpeech.Enter(Speak.Idle);
        };

        await VoicePreview.Toggle(new TextToSpeech.VoiceOption(
            TextToSpeech.SpeakEngine.Custom, "", "Custom command"));

        Assert.Single(_spoken);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    [Fact]
    public async Task ASpeakThatThrowsEndsThePreview()
    {
        VoicePreview.SpeakForTests = (_, _) => throw new InvalidOperationException("no engine");

        await VoicePreview.Toggle(Kokoro);

        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    // ---- change notification ----------------------------------------------------

    [Fact]
    public async Task ChangedIsRaisedOnStartOnceStartedAndOnStop()
    {
        var task = VoicePreview.Toggle(Kokoro);
        Assert.True(Volatile.Read(ref _changed) >= 1);   // start: synchronous, before the engine

        await task;
        var afterStart = Volatile.Read(ref _changed);
        Assert.True(afterStart >= 2);                     // the worker looked again once it owned the speech

        await VoicePreview.StopIfLive();
        Assert.True(Volatile.Read(ref _changed) > afterStart);
    }

    [Fact]
    public async Task SpeechEventsAreNotReportedWhenNoPreviewIsInPlay()
    {
        await VoicePreview.Toggle(Kokoro);
        await VoicePreview.StopIfLive();

        var before = Volatile.Read(ref _changed);
        TextToSpeech.Enter(Speak.Speaking);
        TextToSpeech.Enter(Speak.Idle);

        Assert.Equal(before, Volatile.Read(ref _changed));
    }

    // End() must only retire the request that asked for it: a worker that finds
    // nothing to speak with, after a newer Toggle has replaced its request, must
    // leave the newer preview alone.
    [Fact]
    public async Task ASupersededWorkerThatResolvesNoVoiceDoesNotRetireTheNewerRequest()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        VoicePreview.ResolveSavedForTests = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        };

        _ = VoicePreview.Toggle(null);            // A: held inside resolve
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        _ = VoicePreview.StopIfLive();
        var b = VoicePreview.Toggle(Kokoro);      // B: newer request, queued

        release.Set();
        await b;

        Assert.Same(Kokoro, Assert.Single(_spoken).Voice);
        Assert.Equal(Speak.Speaking, VoicePreview.Look);
    }

    [Fact]
    public async Task ResetForTestsClearsAPreviewInFlight()
    {
        await VoicePreview.Toggle(Kokoro);

        VoicePreview.ResetForTests();

        Assert.Equal(Speak.Idle, VoicePreview.Look);
        Assert.Null(VoicePreview.SpeakForTests);
    }
}

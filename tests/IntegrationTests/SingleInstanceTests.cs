using System.Diagnostics;
using System.Runtime.InteropServices;
using Orbweaver;
using Xunit;

namespace Orbweaver.IntegrationTests;

// SingleInstance.Claim against a real named mutex — the seam
// tests/UnitTests' SingleInstanceTests cannot reach, because that suite only
// asserts the pure ShouldProceed rule against a SingleInstanceClaim someone
// already computed. This is the "someone" — a real OS-backed named
// synchronization object, exactly like the one Program.cs takes on
// MutexName, and it is the reason Claim takes the name as a parameter rather
// than reading MutexName itself: a per-test, GUID-suffixed name here means
// this suite can never collide with a real running Buddy on this machine, nor
// with a parallel CI leg exercising the same code (CB-178).
//
// The first four run in-process. The CB-206 cases at the bottom do not: the
// defect there was that the mutex was scoped to one POSIX session, so a Buddy
// launched from another session never saw it, and only a second process in a
// session of its own can show that. tests/SingleInstanceProbe is that second
// process; it compiles SingleInstance.cs in and makes one claim.
//
// Not covered here: SingleInstanceClaim.AbandonedByPreviousOwner. That arm
// only fires when `WaitOne` throws `AbandonedMutexException`, and
// SingleInstance.cs's own comment records the actual measurement: on this
// machine (macOS, .NET 10.0.400) that exception was never thrown, for
// either way a holder can go away without releasing — killed outright, or
// exited normally past the point where it should have called
// ReleaseMutex. `Release_lets_a_later_claimant_succeed` and
// `Recovers_after_the_previous_holder_never_released_it` below exercise
// exactly those two shapes and both land on Acquired, which is the real,
// measured crash-recovery path on this platform. Reproducing the
// AbandonedMutexException arm itself would mean asserting behaviour this
// runtime does not exhibit; the CB-178 PR body has the harness (a second
// real process, `kill -9`'d) and its output instead.
public class SingleInstanceTests
{
    private static string FreshName() => "CB178_IT_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void First_claimant_acquires_an_uncontested_mutex()
    {
        var name = FreshName();

        var (claim, mutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, claim);
            Assert.True(SingleInstance.ShouldProceed(claim));
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    [Fact]
    public void Second_claimant_observes_it_held_while_the_first_is_still_alive()
    {
        // Deliberately a real, separate Thread — not a second Claim() call
        // on this same thread. A Mutex's ownership in .NET is per *thread*,
        // not per handle instance, and recursive for the thread that already
        // owns it: this test's first draft called Claim() twice in a row on
        // the test method's own thread and got Acquired both times, because
        // the second WaitOne was the same thread re-entering a lock it
        // already held — exactly the kind of false pass CLAUDE.md warns
        // about, measuring something adjacent to the real question. A
        // background thread that actually blocks on the mutex is what makes
        // "someone else holds it" a fact about a different owner rather than
        // an artifact of how the test called the API.
        var name = FreshName();
        using var holderReady = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);

        // The holder's own failure has to be carried back to the test
        // thread by hand. An exception thrown in here — including a failed
        // Assert — is swallowed by the thread it happens on, and the only
        // symptom on the test thread is that holderReady never gets set:
        // the test then fails with "holder thread never acquired the mutex",
        // which is a true statement about a symptom and a false one about
        // the cause. A test that misreports why it failed costs more than a
        // test that fails, because the next person debugs the wrong thing —
        // so the exception is captured here and rethrown below, where xUnit
        // can see it.
        Exception? holderFailure = null;

        var holder = new Thread(() =>
        {
            try
            {
                var (claim, mutex) = SingleInstance.Claim(name);
                Assert.Equal(SingleInstanceClaim.Acquired, claim);
                holderReady.Set();
                releaseHolder.Wait();
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
            catch (Exception ex)
            {
                holderFailure = ex;
                // Unblock the test thread rather than leaving it to time out
                // on a five-second wait for a signal that is never coming.
                holderReady.Set();
            }
        });
        holder.Start();

        try
        {
            Assert.True(holderReady.Wait(TimeSpan.FromSeconds(5)), "holder thread never signalled");
            if (holderFailure is not null)
            {
                throw new Xunit.Sdk.XunitException(
                    "the holder thread failed before it could hold the mutex: " + holderFailure);
            }

            var (secondClaim, secondMutex) = SingleInstance.Claim(name);
            try
            {
                Assert.Equal(SingleInstanceClaim.HeldByAnother, secondClaim);
                Assert.False(SingleInstance.ShouldProceed(secondClaim));
            }
            finally
            {
                // The loser never owns the mutex, so there is nothing to
                // release — only the handle to close. This mirrors exactly
                // what Program.cs's claimSingleInstance delegate does on the
                // HeldByAnother arm.
                secondMutex.Dispose();
            }
        }
        finally
        {
            releaseHolder.Set();
            holder.Join();
        }
    }

    [Fact]
    public void Release_lets_a_later_claimant_succeed()
    {
        var name = FreshName();

        var (firstClaim, firstMutex) = SingleInstance.Claim(name);
        Assert.Equal(SingleInstanceClaim.Acquired, firstClaim);
        firstMutex.ReleaseMutex();
        firstMutex.Dispose();

        // With the first claimant's handle closed and the mutex released,
        // the name is free again — a later launch (the ordinary case: the
        // running Buddy quit, and the next login-item launch takes the same
        // name) should acquire cleanly, not read the prior claimant's now-
        // closed handle as still holding it.
        var (secondClaim, secondMutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, secondClaim);
            Assert.True(SingleInstance.ShouldProceed(secondClaim));
        }
        finally
        {
            secondMutex.ReleaseMutex();
            secondMutex.Dispose();
        }
    }

    [Fact]
    public void Recovers_after_the_previous_holder_never_released_it()
    {
        // The actual crash-recovery path on this platform (CB-178's
        // acceptance criteria: "the keep-alive still restarts Buddy after a
        // genuine crash"). A real crash is a process disappearing without
        // ever calling ReleaseMutex; the closest in-process stand-in is
        // disposing the Mutex without releasing it first, which was checked
        // by hand against a real second, kill -9'd process before writing
        // this test (see SingleInstance.cs's comments and the CB-178 PR
        // body) and behaves identically here: the next claimant still gets
        // Acquired, not an exception and not HeldByAnother. This is the arm
        // that would go silently missing if AbandonedByPreviousOwner were
        // mistaken for where crash recovery lives — it does not fire on this
        // runtime at all (see SingleInstance's own comments), so this test,
        // not that arm, is what actually guards the acceptance criterion.
        var name = FreshName();

        var (firstClaim, firstMutex) = SingleInstance.Claim(name);
        Assert.Equal(SingleInstanceClaim.Acquired, firstClaim);

        // Deliberately no ReleaseMutex() here — that omission is the point.
        firstMutex.Dispose();

        var (secondClaim, secondMutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, secondClaim);
            Assert.True(SingleInstance.ShouldProceed(secondClaim));
        }
        finally
        {
            secondMutex.ReleaseMutex();
            secondMutex.Dispose();
        }
    }

    // --- CB-255: ClaimAll, the legacy name and the new one ------------------
    //
    // Every "is it free?" check below is made from a thread of its own, for the
    // reason Second_claimant_observes_it_held_while_the_first_is_still_alive
    // gives: ownership is per thread and recursive, so a check from the thread
    // that ran ClaimAll would answer Acquired whether or not ClaimAll had let
    // go — a false pass of exactly the kind this section exists to catch.

    // One claim of `name` from a fresh thread, released again before the
    // thread ends, so the answer describes the name's state at that moment and
    // leaves nothing behind.
    private static SingleInstanceClaim ClaimFromAnotherThread(string name)
    {
        SingleInstanceClaim? answer = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (claim, mutex) = SingleInstance.Claim(name);
                if (SingleInstance.ShouldProceed(claim)) mutex.ReleaseMutex();
                mutex.Dispose();
                answer = claim;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the checking thread never finished");
        if (failure is not null) throw new Xunit.Sdk.XunitException("the checking thread failed: " + failure);
        return answer!.Value;
    }

    // A thread that holds `name` until told to let go — a running build of the
    // other generation, in-process. Dispose releases it and joins.
    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private Exception? _failure;

        internal Holder(string name)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    var (claim, mutex) = SingleInstance.Claim(name);
                    Assert.Equal(SingleInstanceClaim.Acquired, claim);
                    _ready.Set();
                    _release.Wait();
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    _ready.Set();
                }
            });
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)), "holder thread never signalled");
            if (_failure is not null)
            {
                throw new Xunit.Sdk.XunitException("the holder failed before it could hold the mutex: " + _failure);
            }
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _ready.Dispose();
            _release.Dispose();
        }
    }

    private static void ReleaseAll(IReadOnlyList<Mutex> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
        {
            held[i].ReleaseMutex();
            held[i].Dispose();
        }
    }

    [Fact]
    public void ClaimAll_acquires_and_holds_both_names_when_neither_is_held()
    {
        var legacy = FreshName();
        var current = FreshName();

        var (proceed, held) = SingleInstance.ClaimAll([legacy, current]);
        try
        {
            Assert.True(proceed);
            Assert.Equal(2, held.Count);

            // And both really are held — the positive control for every
            // "released" assertion below, showing this thread-based check
            // does see a held name as held.
            Assert.Equal(SingleInstanceClaim.HeldByAnother, ClaimFromAnotherThread(legacy));
            Assert.Equal(SingleInstanceClaim.HeldByAnother, ClaimFromAnotherThread(current));
        }
        finally
        {
            ReleaseAll(held);
        }

        // Given back, both are free again.
        Assert.Equal(SingleInstanceClaim.Acquired, ClaimFromAnotherThread(legacy));
        Assert.Equal(SingleInstanceClaim.Acquired, ClaimFromAnotherThread(current));
    }

    [Fact]
    public void ClaimAll_stops_when_an_old_build_holds_the_legacy_name_and_leaves_the_new_name_free()
    {
        // An old build is running. Legacy is claimed first, so ClaimAll loses
        // there and never takes the new name at all — and a later claimant
        // (the negative control) must be able to take it.
        var legacy = FreshName();
        var current = FreshName();

        using (new Holder(legacy))
        {
            var (proceed, held) = SingleInstance.ClaimAll([legacy, current]);

            Assert.False(proceed);
            Assert.Empty(held);
            Assert.Equal(SingleInstanceClaim.Acquired, ClaimFromAnotherThread(current));
        }
    }

    [Fact]
    public void ClaimAll_gives_the_legacy_name_back_when_a_new_build_holds_the_new_name()
    {
        // The arm that actually exercises the giving-back: legacy is acquired
        // first, the new name is held elsewhere, so ClaimAll has to release
        // the legacy name it already took. If it leaked it, the third claimant
        // here would answer HeldByAnother — and an old build launched after
        // this duplicate exited would be locked out by a dead process.
        var legacy = FreshName();
        var current = FreshName();

        using (new Holder(current))
        {
            var (proceed, held) = SingleInstance.ClaimAll([legacy, current]);

            Assert.False(proceed);
            Assert.Empty(held);
            Assert.Equal(SingleInstanceClaim.Acquired, ClaimFromAnotherThread(legacy));
        }
    }

    [Fact]
    public void ClaimAll_refuses_an_empty_list_of_names()
    {
        Assert.Throws<ArgumentException>(() => SingleInstance.ClaimAll([]));
    }

    // CB-206's probe, for CB-255's claim: this process runs as the new build
    // holding both names, and a probe in a POSIX session of its own claims the
    // legacy name — which is what an old build launched from a terminal, an
    // ssh login or a stale login item does. It must find the name held, or
    // the old and new builds run side by side.
    [Fact]
    public void An_old_build_in_a_new_session_finds_the_legacy_name_held_by_ClaimAll()
    {
        var legacy = FreshName();
        var current = FreshName();

        var (proceed, held) = SingleInstance.ClaimAll([legacy, current]);
        try
        {
            Assert.True(proceed);

            var (sid, probeClaim) = RunProbe(legacy, "--new-session");
            if (!OperatingSystem.IsWindows()) Assert.NotEqual(getsid(0), sid);

            Assert.Equal(SingleInstanceClaim.HeldByAnother, probeClaim);
        }
        finally
        {
            ReleaseAll(held);
        }
    }

    // The same shape the other way round: an old build in another session
    // holds the legacy name, and ClaimAll here must stop — leaving the new
    // name free for a probe in that other session to take (the control).
    [Fact]
    public void ClaimAll_stops_when_an_old_build_in_another_session_holds_the_legacy_name()
    {
        var legacy = FreshName();
        var current = FreshName();

        using var holder = StartProbe(legacy, "--new-session", "--hold");
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, ReadProbe(holder).Claim);

            var (proceed, held) = SingleInstance.ClaimAll([legacy, current]);

            Assert.False(proceed);
            Assert.Empty(held);
            Assert.Equal(SingleInstanceClaim.Acquired, RunProbe(current, "--new-session").Claim);
        }
        finally
        {
            holder.Kill();
            Assert.True(holder.WaitForExit(30_000), "the holder did not die");
        }
    }

    // --- CB-206: one per user, across sessions ------------------------------

    // The probe beside this assembly, run under the same dotnet host the test
    // is running under — DOTNET_HOST_PATH is what the dotnet CLI hands its
    // children for exactly this.
    private static Process StartProbe(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SingleInstanceProbe.dll"));
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    // The probe's one line, parsed: its session id and what its claim was.
    private static (int Sid, SingleInstanceClaim Claim) ReadProbe(Process probe)
    {
        var lineTask = probe.StandardOutput.ReadLineAsync();
        Assert.True(lineTask.Wait(TimeSpan.FromSeconds(30)), "the probe never answered");
        var line = lineTask.Result ?? throw new Xunit.Sdk.XunitException(
            "the probe printed nothing: " + probe.StandardError.ReadToEnd());

        var parts = line.Split(' ');
        Assert.True(parts.Length == 2 && parts[0].StartsWith("sid=") && parts[1].StartsWith("claim="),
            "unexpected probe output: " + line);
        return (int.Parse(parts[0][4..]), Enum.Parse<SingleInstanceClaim>(parts[1][6..]));
    }

    private static (int Sid, SingleInstanceClaim Claim) RunProbe(params string[] args)
    {
        using var probe = StartProbe(args);
        var answer = ReadProbe(probe);
        Assert.True(probe.WaitForExit(30_000), "the probe did not exit");
        return answer;
    }

    [DllImport("libc")]
    private static extern int getsid(int pid);

    // The defect, closed. This process holds the mutex; a probe that has put
    // itself in a new POSIX session — what a terminal, agent shell, ssh or
    // setsid launch gives a real Buddy — must find it held. On Windows there is
    // no setsid and the probe shares this logon session, which is the scope
    // Windows has always had.
    [Fact]
    public void A_claim_from_a_new_session_finds_the_mutex_held()
    {
        var name = FreshName();
        var (claim, mutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, claim);

            var (sid, probeClaim) = RunProbe(name, "--new-session");

            // That the probe really is in another session, without which a
            // HeldByAnother below would prove nothing about sessions at all.
            if (!OperatingSystem.IsWindows()) Assert.NotEqual(getsid(0), sid);

            Assert.Equal(SingleInstanceClaim.HeldByAnother, probeClaim);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    // The paired control that would have caught a setup that only looks right:
    // the same pair of processes, the same new session, the old session-scoped
    // claim — and on macOS the probe gets its own mutex and Acquires it, which
    // is the bug exactly as it was found. If this ever answers HeldByAnother on
    // Unix, the probe is not really in another session and the test above has
    // stopped measuring anything. On Windows that scope *is* the platform's, so
    // the probe finds it held there.
    [Fact]
    public void The_old_session_scoped_claim_is_blind_across_sessions()
    {
        var name = FreshName();
        var (claim, mutex) = SingleInstance.Claim(name, onWindows: true);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, claim);

            var (_, probeClaim) = RunProbe(name, "--new-session", "--windows-scope");

            Assert.Equal(
                OperatingSystem.IsWindows() ? SingleInstanceClaim.HeldByAnother : SingleInstanceClaim.Acquired,
                probeClaim);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    // And the plain negative control: with this process holding one name, a
    // new-session probe claiming another acquires it. Without this, a scope so
    // wide that everything blocked everything would pass the first test.
    [Fact]
    public void A_different_name_from_a_new_session_is_still_acquired()
    {
        var name = FreshName();
        var (claim, mutex) = SingleInstance.Claim(name);
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, claim);

            var (_, probeClaim) = RunProbe(FreshName(), "--new-session");

            Assert.Equal(SingleInstanceClaim.Acquired, probeClaim);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    // Crash recovery under the new scope, with a real crash: a probe in its own
    // session holds the mutex, is killed outright, and the next claim here is
    // Acquired — the keep-alive's restart after a genuine crash still gets to
    // run. Measured by hand three times before this test existed; this is that
    // measurement, kept.
    [Fact]
    public void A_killed_holder_in_another_session_does_not_strand_the_mutex()
    {
        var name = FreshName();

        using var holder = StartProbe(name, "--new-session", "--hold");
        try
        {
            Assert.Equal(SingleInstanceClaim.Acquired, ReadProbe(holder).Claim);

            var (whileHeld, heldMutex) = SingleInstance.Claim(name);
            heldMutex.Dispose();
            Assert.Equal(SingleInstanceClaim.HeldByAnother, whileHeld);
        }
        finally
        {
            holder.Kill();
            Assert.True(holder.WaitForExit(30_000), "the holder did not die");
        }

        // Acquired on macOS, where a dead holder's claim is simply gone (see
        // AbandonedByPreviousOwner's comment). Windows' kernel does track
        // abandonment, so there the same crash may surface as that arm
        // instead; either way the app proceeds, which is the property the
        // keep-alive depends on.
        var (claim, mutex) = SingleInstance.Claim(name);
        try
        {
            Assert.True(SingleInstance.ShouldProceed(claim));
            if (!OperatingSystem.IsWindows()) Assert.Equal(SingleInstanceClaim.Acquired, claim);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    // --- CB-306: peer-probe asks whether the app holds the claim ----------

    [Fact]
    public void AppIsRunning_is_true_while_another_holder_has_the_claim()
    {
        var name = FreshName();

        using (new Holder(name))
        {
            Assert.True(PeerProbeGuard.AppIsRunning([name]));
        }
    }

    [Fact]
    public void AppIsRunning_is_false_when_nobody_holds_it_and_the_probe_gives_it_straight_back()
    {
        // The negative control for the test above, and the half that matters
        // for the app: a probe that kept the claim would stop Orbweaver starting.
        var name = FreshName();

        Assert.False(PeerProbeGuard.AppIsRunning([name]));
        Assert.Equal(SingleInstanceClaim.Acquired, ClaimFromAnotherThread(name));
    }
}

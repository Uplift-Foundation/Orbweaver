using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-393: the phone's roster no longer honours orb lifetime. RelayService
// builds it with RelayScan, which is HeadlessSnapshot with the lifetime
// switched off; the desktop's own orbs still go through the same snapshot with
// it on, which is what the first case pins as the contrast. Every case hands in
// all the seams, so the machine running the test is not consulted; the default
// arms of those seams are the same named gap HeadlessSnapshotTests records.
[Collection("Settings")]
public class RelayHeadlessScanTests
{
    private static readonly Func<Dictionary<string, string>?> NoJobs =
        () => new Dictionary<string, string>();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string id, SessionStatus s, DateTime? written = null)
    {
        var path = Path.Combine(dir, id + ".txt");
        File.WriteAllText(path, JsonSerializer.Serialize(s));
        if (written is { } w) File.SetLastWriteTimeUtc(path, w);
    }

    private static SessionStatus Idle(int pid) =>
        new() { State = "idle", Title = "job-hunter", Cwd = "/tmp/somewhere", SessionPid = pid };

    private static readonly DateTime Long = DateTime.UtcNow.AddDays(3);

    [Fact]
    public void ALiveSessionPastOrbLifetimeIsOnThePhoneButNotOnTheDesktop()
    {
        var dir = NewDir();
        try
        {
            Write(dir, "alive", Idle(4242));

            var roster = RelayService.RelayScan(dir, NoJobs, _ => true, Long, onWindows: false);
            Assert.Equal("alive", Assert.Single(roster).SessionId);

            // The same directory, the same clock, the orbs' rule: gone.
            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: Long, honourOrbLifetime: true, onWindows: false));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ADeadProcessIsNotOnThePhone()
    {
        var dir = NewDir();
        try
        {
            Write(dir, "dead", Idle(4242));
            Assert.Empty(RelayService.RelayScan(dir, NoJobs, _ => false, Long, onWindows: false));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ASupersededTwinIsNotOnThePhone()
    {
        var dir = NewDir();
        try
        {
            Write(dir, "old", Idle(4242), DateTime.UtcNow.AddHours(-5));
            Write(dir, "new", Idle(4242), DateTime.UtcNow.AddHours(-4));

            var roster = RelayService.RelayScan(dir, NoJobs, _ => true, Long, onWindows: false);
            Assert.Equal("new", Assert.Single(roster).SessionId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ABackgroundedHuskIsNotOnThePhone()
    {
        var dir = NewDir();
        try
        {
            var transcript = Path.Combine(dir, "t.jsonl");
            File.WriteAllLines(transcript, new[]
            {
                @"{""type"":""assistant"",""message"":{""role"":""assistant"",""content"":[{""type"":""text"",""text"":""on it""}]},""timestamp"":""2026-08-28T17:53:11.000Z""}",
                @"{""type"":""system"",""subtype"":""informational"",""content"":""Backgrounding after the current tool finishes…"",""timestamp"":""2026-08-28T17:53:15.295Z"",""level"":""warning"",""sessionId"":""husk""}",
            });
            var s = Idle(4242);
            s.State = "generating";
            s.TranscriptPath = transcript;
            Write(dir, "husk", s);

            Assert.Empty(RelayService.RelayScan(dir, NoJobs, _ => true, DateTime.UtcNow, onWindows: false));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ResettingTheHeadlessScanRestoresTheRelayRuleOverATestsFake()
    {
        var was = RelayService.HeadlessScan;
        try
        {
            // Both assignments are one-liners over RelayScan; what is pinned
            // here is that the reset restores a delegate rather than leaving a
            // test's fake behind.
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> fake = () => [];
            RelayService.HeadlessScan = fake;
            RelayService.ResetHeadlessForTests();
            Assert.NotSame(fake, RelayService.HeadlessScan);
        }
        finally { RelayService.HeadlessScan = was; }
    }
}

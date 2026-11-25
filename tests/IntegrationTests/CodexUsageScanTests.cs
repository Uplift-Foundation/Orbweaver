using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// CodexUsagePoller's walk of $CODEX_HOME/sessions, against real files on disk.
//
// Integration rather than unit because the defect this covers was never in the
// parsing — every line involved parsed perfectly. It was in the *choosing*: two
// files, two mtimes, two timestamps, and a rule that used the wrong one. That
// is a seam with the filesystem, and it needs real files with real mtimes to
// mean anything. The fixtures below are copied from this machine's rollouts on
// 2 Sep 2026, per the repo's rule that fixtures come from captures rather than
// from memory.
[Collection("Settings")]
public class CodexUsageScanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-codex-scan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A real token_count with both windows.
    //
    // Built by substitution rather than interpolation: a rollout line ends in a
    // run of closing braces, and a raw interpolated literal would need more '$'
    // than it has characters worth reading.
    private static string Windowed(string timestamp, double primary, double weekly) =>
        """
        {"timestamp":"@TS@","ordinal":183,"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":@P@,"window_minutes":300,"resets_at":1788391232},"secondary":{"used_percent":@W@,"window_minutes":10080,"resets_at":1788807866},"plan_type":"team"}}}
        """
            .Replace("@TS@", timestamp)
            .Replace("@P@", primary.ToString(CultureInfo.InvariantCulture))
            .Replace("@W@", weekly.ToString(CultureInfo.InvariantCulture));

    // The one Codex broadcasts to every live session when the workspace runs
    // out of credits. A legible snapshot carrying no usage at all.
    private static string Depleted(string timestamp) =>
        """
        {"timestamp":"@TS@","ordinal":184,"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"premium","primary":null,"secondary":null,"credits":{"has_credits":false,"unlimited":false,"balance":null},"plan_type":null,"rate_limit_reached_type":"workspace_owner_credits_depleted"}}}
        """.Replace("@TS@", timestamp);

    private static string Chatter(string timestamp) =>
        """
        {"timestamp":"@TS@","ordinal":12,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"Text","text":"done"}]}}}
        """.Replace("@TS@", timestamp);

    private string Sessions => Path.Combine(_root, "sessions");

    // Rollouts really do live under sessions/<yyyy>/<mm>/<dd>/, so write them
    // there — the walk is recursive and a flat directory would not prove it.
    private string Rollout(string name, DateTimeOffset modified, params string[] lines)
    {
        var dir = Path.Combine(Sessions, "2026", "09", "02");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{name}.jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    // No live answer, so these exercise the rollout path they were written for.
    //
    // **Explicit, never omitted.** CB-85 gave ReadFrom a default that spawns
    // `codex app-server` for real, so a call that leaves the argument off runs
    // the CLI: a second per case, a different result on a machine that has Codex
    // than on one that does not, and a test asserting about a rollout while
    // something else answered. Passing it costs one argument and removes all
    // three.
    private static readonly Func<string, string?> NoLiveAnswer = _ => null;

    private static AccountUsage? Read(CodexSnapshot? snapshot) =>
        CodexUsageParse.FromRateLimits(
            snapshot?.Json, "/Users/w/.codex", "codex",
            DateTimeOffset.Parse("2026-09-02T19:20:00Z"), snapshot?.WrittenAt);

    // The exact situation on the machine that reported this: four sessions
    // running, all four handed the same credits-depleted event within a second
    // of each other, so the newest-modified rollout holds nothing but that. The
    // old scan stopped at that file and reported an account with no limits,
    // while a file one place down the mtime list held 99% of a five-hour window.
    [Fact]
    public void AWindowlessNewestFileDoesNotHideTheRealReading()
    {
        var t = DateTimeOffset.Parse("2026-09-02T18:57:00Z");

        Rollout("busy", t.AddMinutes(-1),
            Windowed("2026-09-02T18:55:00Z", 97, 38),
            Windowed("2026-09-02T18:56:00Z", 99, 38),
            Depleted("2026-09-02T18:56:30Z"));
        Rollout("depleted-only", t,
            Chatter("2026-09-02T18:56:50Z"),
            Depleted("2026-09-02T18:57:00Z"));

        var usage = Read(CodexUsagePoller.LatestSnapshotFrom(Sessions));

        Assert.True(usage!.Available);
        Assert.Equal(99.0, usage.Session!.Percent);
        Assert.Equal(38.0, usage.Weekly!.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T18:56:00Z"), usage.AsOf);
    }

    // Same rule inside one file: a rollout that ends on a depleted event still
    // has its real snapshot two lines up.
    [Fact]
    public void ATrailingDepletedLineDoesNotBlankTheFileItIsIn()
    {
        var path = Rollout("single", DateTimeOffset.Parse("2026-09-02T18:57:00Z"),
            Windowed("2026-09-02T18:55:00Z", 12, 30),
            Windowed("2026-09-02T18:56:00Z", 44, 31),
            Depleted("2026-09-02T18:57:00Z"));

        var line = CodexUsagePoller.LatestWindowedLine(path);

        Assert.NotNull(line);
        Assert.Contains("\"used_percent\":44", line);
    }

    // mtime says which file was touched last, which is not the same question as
    // which file holds the newest usage — several sessions write constantly and
    // only some of them are being told about rate limits.
    [Fact]
    public void TheNewestSnapshotWinsEvenWhenTheNewestFileIsAnother()
    {
        var t = DateTimeOffset.Parse("2026-09-02T19:00:00Z");

        Rollout("touched-last", t, Windowed("2026-09-02T18:00:00Z", 20, 25));
        Rollout("knows-most", t.AddMinutes(-30), Windowed("2026-09-02T18:50:00Z", 71, 33));

        var usage = Read(CodexUsagePoller.LatestSnapshotFrom(Sessions));

        Assert.Equal(71.0, usage!.Session!.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T18:50:00Z"), usage.AsOf);
    }

    // The scan stops early, and this is the assumption that lets it: no line in
    // a file can post-date the file's last write, so once the best snapshot so
    // far is newer than the next file's mtime, nothing further down can beat
    // it. The fixture below is physically impossible on purpose — a snapshot an
    // hour in the future of the file holding it — because that is the only way
    // to prove the older file was skipped rather than read and rejected.
    [Fact]
    public void AFileOlderThanTheBestSnapshotIsNotOpenedAtAll()
    {
        var t = DateTimeOffset.Parse("2026-09-02T19:00:00Z");

        Rollout("recent", t, Windowed("2026-09-02T18:59:00Z", 55, 30));
        Rollout("ancient", t.AddHours(-2), Windowed("2026-09-02T20:00:00Z", 99, 99));

        var usage = Read(CodexUsagePoller.LatestSnapshotFrom(Sessions));

        Assert.Equal(55.0, usage!.Session!.Percent);
    }

    // A snapshot with no timestamp cannot be ordered by one, so the file's
    // mtime stands in for the comparison — but the reading still carries no
    // ObservedAt, because dating a number by when its file was touched would be
    // inventing the very thing this ticket removed.
    [Fact]
    public void ASnapshotWithNoTimestampIsOrderedByItsFileAndDatedByTheRead()
    {
        var t = DateTimeOffset.Parse("2026-09-02T19:00:00Z");

        Rollout("stamped", t.AddMinutes(-10), Windowed("2026-09-02T18:40:00Z", 20, 25));
        Rollout("bare", t,
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":66,"window_minutes":300}}}}""");

        var snapshot = CodexUsagePoller.LatestSnapshotFrom(Sessions);
        var usage = Read(snapshot);

        Assert.Null(snapshot!.Value.WrittenAt);
        Assert.Equal(66.0, usage!.Session!.Percent);
        Assert.Null(usage.ObservedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T19:20:00Z"), usage.AsOf);
    }

    [Fact]
    public void ARolloutWithNothingButChatterIsSkipped()
    {
        Rollout("quiet", DateTimeOffset.Parse("2026-09-02T19:00:00Z"),
            Chatter("2026-09-02T18:58:00Z"),
            Chatter("2026-09-02T18:59:00Z"));

        Assert.Null(CodexUsagePoller.LatestSnapshotFrom(Sessions));
    }

    // A rollout carries whole tool outputs, and a single line can run to
    // megabytes. The cap is there so the scan cannot be made to hold one in
    // memory; a snapshot past it is skipped rather than read.
    [Fact]
    public void ALineBeyondTheCapIsSkippedAndTheOneBeforeItStands()
    {
        var padded = Windowed("2026-09-02T18:59:00Z", 88, 40)
            .Replace("\"plan_type\":\"team\"",
                     "\"plan_type\":\"" + new string('x', CodexUsagePoller.MaxLineBytes) + "\"");

        var path = Rollout("huge", DateTimeOffset.Parse("2026-09-02T19:00:00Z"),
            Windowed("2026-09-02T18:58:00Z", 15, 22),
            padded);

        var line = CodexUsagePoller.LatestWindowedLine(path);

        Assert.NotNull(line);
        Assert.Contains("\"used_percent\":15", line);
    }

    [Fact]
    public void AMissingSessionsDirectoryIsNoReadingRatherThanAThrow()
    {
        Assert.Null(CodexUsagePoller.LatestSnapshotFrom(
            Path.Combine(_root, "never-existed")));
    }

    [Fact]
    public void AnEmptySessionsDirectoryIsNoReading()
    {
        Directory.CreateDirectory(Sessions);

        Assert.Null(CodexUsagePoller.LatestSnapshotFrom(Sessions));
    }

    [Fact]
    public void AFileThatIsNotARolloutIsIgnoredEvenWithASnapshotInIt()
    {
        Directory.CreateDirectory(Sessions);
        File.WriteAllText(
            Path.Combine(Sessions, "history.jsonl"),
            Windowed("2026-09-02T18:59:00Z", 90, 40) + "\n",
            Encoding.UTF8);

        Assert.Null(CodexUsagePoller.LatestSnapshotFrom(Sessions));
    }

    // The whole per-account path, from a directory to a reading: the label out
    // of auth.json, the snapshot out of the rollout tree, and the two dated
    // together. Driven against a temp $CODEX_HOME rather than the real one —
    // see the comment on ReadFrom.
    [Fact]
    public void AHomeBecomesOneReadingLabelledFromItsAuthFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "auth.json"),
            """{"tokens":{"access_token":"never-read"},"email":"user@example.org"}""");
        Rollout("live", DateTimeOffset.Parse("2026-09-02T19:00:00Z"),
            Windowed("2026-09-02T18:58:00Z", 62, 34));

        var readings = CodexUsagePoller.ReadFrom(
            new[] { _root }, DateTimeOffset.Parse("2026-09-02T19:20:00Z"), NoLiveAnswer);

        var usage = Assert.Single(readings);
        Assert.Equal("user", usage.Label);
        Assert.Equal(_root, usage.ConfigDir);
        Assert.Equal(AccountUsageSource.Codex, usage.Source);
        Assert.Equal(62.0, usage.Session!.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T18:58:00Z"), usage.AsOf);
    }

    // A configured home that has never run Codex contributes nothing rather
    // than an empty orb, and does not stop the homes after it being read.
    [Fact]
    public void AHomeWithNoRolloutsIsSkippedWithoutStoppingTheRest()
    {
        var empty = Path.Combine(_root, "unused-home");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(_root);
        Rollout("live", DateTimeOffset.Parse("2026-09-02T19:00:00Z"),
            Windowed("2026-09-02T18:58:00Z", 5, 9));

        var readings = CodexUsagePoller.ReadFrom(
            new[] { empty, _root }, DateTimeOffset.Parse("2026-09-02T19:20:00Z"), NoLiveAnswer);

        var usage = Assert.Single(readings);
        Assert.Equal(_root, usage.ConfigDir);
    }

    // No auth.json at all: the label falls back to the directory name, and the
    // reading still stands.
    [Fact]
    public void AHomeWithNoAuthFileIsLabelledByItsDirectory()
    {
        Rollout("live", DateTimeOffset.Parse("2026-09-02T19:00:00Z"),
            Windowed("2026-09-02T18:58:00Z", 5, 9));

        var readings = CodexUsagePoller.ReadFrom(
            new[] { _root }, DateTimeOffset.Parse("2026-09-02T19:20:00Z"), NoLiveAnswer);

        Assert.Equal(
            CodexUsageAccounts.FallbackLabel(_root),
            Assert.Single(readings).Label);
    }

    // The switch is off by default, and off means no reading rather than an
    // empty one — the distinction AccountOrbs.Apply's keep-stale rule depends
    // on to tell "not asked" from "asked and failed".
    //
    // CB-451: the switch is process-wide, and HeadlessUsageSettingsTests points
    // it at a file with codexAccountUsageEnabled true. This test therefore
    // points settings at an empty directory of its own rather than trusting
    // whatever the last test left, and the class is in the Settings collection
    // so none of those writers can run beside it.
    [Fact]
    public void TheSwitchBeingOffMeansNoReadingsAtAll()
    {
        OffMeansNoReadings();
    }

    // The same, after a writer has left the switch on, which is the interleaving
    // CI hit on osx-arm64 (run 45330452977).
    [Fact]
    public void TheSwitchBeingOffMeansNoReadings_EvenAfterAnotherTestLeftItOn()
    {
        var was = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var left = Path.Combine(_root, "left-on");
        Directory.CreateDirectory(left);
        File.WriteAllText(Path.Combine(left, "settings.json"), """{ "codexAccountUsageEnabled": true }""");
        try
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", left);
            OrbweaverSettings.ReloadForTests();
            Assert.True(OrbweaverSettings.CodexAccountUsageEnabled);

            OffMeansNoReadings();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", was);
            OrbweaverSettings.ReloadForTests();
        }
    }

    private void OffMeansNoReadings()
    {
        var was = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var fresh = Path.Combine(_root, "settings-off");
        Directory.CreateDirectory(fresh);
        try
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", fresh);
            OrbweaverSettings.ReloadForTests();

            Assert.False(OrbweaverSettings.CodexAccountUsageEnabled);
            Assert.Empty(new CodexUsagePoller().Read());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", was);
            OrbweaverSettings.ReloadForTests();
        }
    }

    // ---- CB-85: live first, rollout when live says nothing ----------------
    //
    // `ask` stands in for the `codex app-server` subprocess, which is the only
    // part of the live path that cannot be driven from a test. Everything on
    // either side of it — choosing between the two answers, dating them, the
    // fallback — is here.

    private const string LiveResult = """
    {"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1788391232},"secondary":{"usedPercent":38,"windowDurationMins":10080,"resetsAt":1788807866},"credits":{"hasCredits":false,"unlimited":false},"planType":"team"}}
    """;

    // Both answers available and they disagree, which is the normal case rather
    // than a contrived one: the rollout holds whatever the last session wrote
    // and the live call holds what is true now. Measured on the machine this
    // was built on as 99% on disk against 100% live, three hours apart.
    [Fact]
    public void TheLiveAnswerWinsOverTheRolloutOnDisk()
    {
        Rollout("stale", DateTimeOffset.Parse("2026-09-02T16:00:00Z"),
            Windowed("2026-09-02T15:58:00Z", 61, 30));

        var readings = CodexUsagePoller.ReadFrom(
            new[] { _root }, DateTimeOffset.Parse("2026-09-02T19:20:00Z"), _ => LiveResult);

        var usage = Assert.Single(readings);
        Assert.Equal(100.0, usage.Session!.Percent);
        Assert.Equal(38.0, usage.Weekly!.Percent);
        Assert.Equal("team", usage.SubscriptionType);
    }

    // A live number is current, so it carries no ObservedAt and the orb does not
    // dim. CB-83's rule pointing the other way — that ticket was about a number
    // pretending to be fresh; this is a number that genuinely is.
    [Fact]
    public void TheLiveAnswerIsDatedByTheReadAndIsNotStale()
    {
        var readAt = DateTimeOffset.Parse("2026-09-02T19:20:00Z");

        var usage = Assert.Single(
            CodexUsagePoller.ReadFrom(new[] { _root }, readAt, _ => LiveResult));

        Assert.Null(usage.ObservedAt);
        Assert.Equal(readAt, usage.AsOf);
        Assert.False(usage.IsStale(readAt));
    }

    // Codex not installed where CodexBinary looks, an app-server too old to know
    // the method, a spawn that failed: the rollout still answers, and still says
    // how old it is.
    [Fact]
    public void NoLiveAnswerFallsBackToTheRolloutAndKeepsItsAge()
    {
        Rollout("stale", DateTimeOffset.Parse("2026-09-02T16:00:00Z"),
            Windowed("2026-09-02T15:58:00Z", 61, 30));

        var readAt = DateTimeOffset.Parse("2026-09-02T19:20:00Z");
        var usage = Assert.Single(
            CodexUsagePoller.ReadFrom(new[] { _root }, readAt, _ => null));

        Assert.Equal(61.0, usage.Session!.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T15:58:00Z"), usage.AsOf);
        Assert.True(usage.IsStale(readAt));
    }

    // The live call can answer with both windows null when the workspace has run
    // out of credits — the same shape CB-83 found in the rollouts, arriving over
    // the other transport. A real number from an hour ago beats an account drawn
    // as having no limits.
    [Fact]
    public void AWindowlessLiveAnswerFallsBackRatherThanBlankingTheOrb()
    {
        Rollout("real", DateTimeOffset.Parse("2026-09-02T16:00:00Z"),
            Windowed("2026-09-02T15:58:00Z", 61, 30));

        var depleted = """
        {"rateLimits":{"limitId":"premium","primary":null,"secondary":null,"rateLimitReachedType":"workspace_owner_credits_depleted"}}
        """;

        var usage = Assert.Single(CodexUsagePoller.ReadFrom(
            new[] { _root }, DateTimeOffset.Parse("2026-09-02T19:20:00Z"), _ => depleted));

        Assert.True(usage.Available);
        Assert.Equal(61.0, usage.Session!.Percent);
    }

    // Neither transport has anything: no orb, rather than an empty one.
    [Fact]
    public void NoLiveAnswerAndNoRolloutIsNoReading()
    {
        Directory.CreateDirectory(Sessions);

        Assert.Empty(CodexUsagePoller.ReadFrom(
            new[] { _root }, DateTimeOffset.Parse("2026-09-02T19:20:00Z"), _ => null));
    }

    // Each home is asked about itself. Getting this wrong would report one
    // account's usage under another's name, which is worse than reporting none.
    [Fact]
    public void EachHomeIsAskedAboutItself()
    {
        var second = Path.Combine(_root, "second-home");
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(_root);

        var asked = new System.Collections.Generic.List<string>();

        var readings = CodexUsagePoller.ReadFrom(
            new[] { _root, second },
            DateTimeOffset.Parse("2026-09-02T19:20:00Z"),
            home => { asked.Add(home); return LiveResult; });

        Assert.Equal(new[] { _root, second }, asked);
        Assert.Equal(new[] { _root, second }, readings.Select(r => r.ConfigDir).ToArray());
    }

    // The default `ask` is the real subprocess, so it cannot be exercised by
    // asking about a home — but it can be exercised by asking about none. An
    // empty list runs the defaulting and then nothing, which pins that omitting
    // the argument still resolves to something rather than throwing on a null
    // delegate, without a CLI being launched anywhere near this suite.
    //
    // The branch report counts **four** arms on `ask ??= LiveAsk` and only two
    // are reachable from a test. The other two belong to the compiler: a
    // method-group conversion is cached in a hidden static behind its own null
    // check, and both of those arms are attributed to this source line. Calling
    // the method twice was tried and did not move the number, so the arms are
    // named in the PR rather than chased — walking a percentage up by adding
    // calls that assert nothing is the habit this repo's coverage notes warn
    // about. What *is* covered is the behaviour: a caller that supplies `ask`
    // and a caller that does not.
    [Fact]
    public void TheDefaultLiveReaderIsResolvedWithoutBeingCalled()
    {
        Assert.Empty(CodexUsagePoller.ReadFrom(
            Array.Empty<string>(), DateTimeOffset.Parse("2026-09-02T19:20:00Z")));
    }
}

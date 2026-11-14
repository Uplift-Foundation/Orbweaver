using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-276's core, through a real scan over real status files: that a hide takes
// the orb away and keeps it away through everything that would otherwise bring
// it back (another scan, the hook rewriting the file, a restart), that Restore
// brings it back, that the tray and the phone still hear about it, and that
// only a session known to have ended is forgotten.
//
// The scan-level suite proper — every source, teams, rooms, sound — is QA's
// (HiddenOrbScanTests). This one covers the manager's own lines, with local
// sessions only, so nothing here needs a gateway, an account or another
// machine.
//
// Same harness rules as SessionScanTests: a scratch status directory handed to
// the internal constructor, Start() never called, every file naming this
// process's own pid and a terminal so nothing shells out.
[Collection("Settings")]
public class HiddenOrbCoreScanTests
{
    private static readonly int LivePid = Environment.ProcessId;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-hidden-" + Guid.NewGuid());
        public string SettingsDir { get; } = Path.Combine(Path.GetTempPath(), "cb-hidden-settings-" + Guid.NewGuid());

        public Scratch()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(SettingsDir);
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", SettingsDir);
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.ClaudeCodeEnabled = true;
            OrbweaverSettings.CodexEnabled = true;
            OrbweaverSettings.GrokEnabled = true;
        }

        // Each id gets its own CLI. Every file names this process's pid, and
        // Superseded keeps one file per pid *and* CLI, so three sessions on
        // one pid need three CLIs to all be drawn — without spawning anything.
        private static string CliFor(string sessionId) => sessionId switch
        {
            "b" => "codex",
            "c" => "grok",
            _ => ""
        };

        public void Write(string sessionId, string state = "idle", string title = "")
        {
            File.WriteAllText(Path.Combine(Dir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = state,
                    Title = title,
                    Cli = CliFor(sessionId),
                    Cwd = "/Users/user/project",
                    SessionPid = LivePid,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));
        }

        public void Delete(string sessionId) => File.Delete(Path.Combine(Dir, sessionId + ".txt"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
            try { Directory.Delete(SettingsDir, recursive: true); } catch { }
        }
    }

    private static SessionManager Scan(Scratch scratch)
    {
        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();
        return manager;
    }

    private static T Field<T>(SessionManager manager, string name)
    {
        var field = typeof(SessionManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(manager)!;
    }

    private static List<string> OrbIds(SessionManager manager) =>
        Field<System.Collections.IDictionary>(manager, "_windows").Keys.Cast<string>().OrderBy(x => x).ToList();

    private static List<string> HiddenIds(SessionManager manager) =>
        Field<List<(string SessionId, SessionStatus Status)>>(manager, "_hidden").Select(h => h.SessionId).ToList();

    [AvaloniaFact]
    public void HidingAnOrbTakesItAwayAndRecordsWhatItWas()
    {
        using var scratch = new Scratch();
        scratch.Write("a", title: "fix the tray");
        scratch.Write("b");
        var manager = Scan(scratch);

        manager.HideSession("a");
        manager.ScanAndUpdate();

        Assert.Equal(new[] { "b" }, OrbIds(manager));
        Assert.Null(manager.StatusFor("a"));
        Assert.Equal(new[] { "a" }, HiddenIds(manager));

        var entry = Assert.Single(OrbweaverSettings.HiddenOrbList());
        Assert.Equal(("a", "fix the tray", "Claude Code"), (entry.SessionId, entry.Title, entry.Source));
        Assert.Null(entry.Peer);
    }

    [AvaloniaFact]
    public void AHiddenOrbStaysAwayThroughARescanAHookWriteAndARestart()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);
        manager.HideSession("a");
        manager.ScanAndUpdate();

        manager.ScanAndUpdate();
        Assert.Empty(OrbIds(manager));

        scratch.Write("a", state: "generating");    // the hook firing again
        manager.ScanAndUpdate();
        Assert.Empty(OrbIds(manager));

        OrbweaverSettings.ReloadForTests();         // a restart reads the file
        var restarted = Scan(scratch);
        Assert.Empty(OrbIds(restarted));
        Assert.Equal(new[] { "a" }, HiddenIds(restarted));
    }

    [AvaloniaFact]
    public void RestoreBringsOneBackAndRestoreAllBringsTheRest()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        scratch.Write("b");
        scratch.Write("c");
        var manager = Scan(scratch);
        foreach (var id in new[] { "a", "b", "c" }) manager.HideSession(id);
        manager.ScanAndUpdate();
        Assert.Empty(OrbIds(manager));

        manager.RestoreHiddenOrb("b");
        manager.RestoreHiddenOrb("not-hidden");     // false from settings; no rescan, no throw
        manager.ScanAndUpdate();
        Assert.Equal(new[] { "b" }, OrbIds(manager));

        manager.RestoreAllHiddenOrbs();
        manager.ScanAndUpdate();
        Assert.Equal(new[] { "a", "b", "c" }, OrbIds(manager));
        Assert.Empty(HiddenIds(manager));
    }

    [AvaloniaFact]
    public void HidingAnIdWithNoOrbDoesNothing()
    {
        using var scratch = new Scratch();
        var manager = Scan(scratch);

        manager.HideSession("nobody");

        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void ASessionWithNoTeamLeadsNoTeam()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        scratch.Write("b");
        var manager = Scan(scratch);

        Assert.Equal(new[] { "a", "b" }, OrbIds(manager));
        Assert.False(manager.LeadsATeam("a"));
    }

    [AvaloniaFact]
    public void ThePhoneStillHearsAboutAHiddenSession()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        scratch.Write("b");
        var manager = Scan(scratch);
        manager.HideSession("a");

        IReadOnlyList<(string SessionId, SessionStatus Status)>? heard = null;
        manager.SessionsScanned += list => heard = list;
        manager.ScanAndUpdate();

        Assert.NotNull(heard);
        Assert.Equal(new[] { "a", "b" }, heard!.Select(h => h.SessionId).OrderBy(x => x).ToArray());
    }

    [AvaloniaFact]
    public void ALocalHideIsForgottenOnlyOnceItsFileHasStayedGone()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);
        manager.HiddenPruneGrace = TimeSpan.Zero;
        manager.HideSession("a");
        manager.ScanAndUpdate();

        // Listed: the clock never starts however many passes go by.
        manager.ScanAndUpdate();
        manager.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());

        // Gone: the first pass starts the clock, the next one prunes.
        scratch.Delete("a");
        manager.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
        manager.ScanAndUpdate();
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void AnUnreadableStatusFolderPrunesNothing()
    {
        // The negative control for the test above: the same zero grace and
        // the same missing file, but the folder itself cannot be read, which
        // is "cannot say" rather than "ended".
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);
        manager.HiddenPruneGrace = TimeSpan.Zero;
        manager.HideSession("a");
        manager.ScanAndUpdate();

        Directory.Delete(scratch.Dir, recursive: true);
        for (var i = 0; i < 4; i++) manager.ScanAndUpdate();

        Assert.Single(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void AHideForAnotherSourceIsNotPrunedByTheLocalFolder()
    {
        // Until the remote seams land every remote listing answers null; a
        // remote id must never be judged against local files.
        using var scratch = new Scratch();
        var manager = Scan(scratch);
        manager.HiddenPruneGrace = TimeSpan.Zero;
        OrbweaverSettings.HideOrb(new HiddenOrb("cloud:x", DateTime.UtcNow, "x", "Claude cloud", ""));
        OrbweaverSettings.HideOrb(new HiddenOrb("openclaw:agent:main:main", DateTime.UtcNow, "m", "OpenClaw", ""));
        OrbweaverSettings.HideOrb(new HiddenOrb("rc:work:abc", DateTime.UtcNow, "r", "Other machine", "", "mini"));

        for (var i = 0; i < 4; i++) manager.ScanAndUpdate();

        Assert.Equal(3, OrbweaverSettings.HiddenOrbList().Count);
    }

    [AvaloniaFact]
    public void ARemoteHideIsForgottenWhenItsPeerAnswersWithoutItAndKeptWhenThePeerIsAway()
    {
        // Through the real seam rather than HiddenOrbs.Listed alone, so the
        // wiring from the scan to RemoteControlSessions.ListedByPeer is what is
        // being tested. The control is the same entry against a peer that is
        // not answering at all.
        using var scratch = new Scratch();
        OrbweaverSettings.PeerLinkEnabled = true;
        try
        {
            var manager = Scan(scratch);
            manager.HiddenPruneGrace = TimeSpan.Zero;
            OrbweaverSettings.HideOrb(new HiddenOrb("rc:work:ended", DateTime.UtcNow, "e", "Other machine", "", "mini"));
            OrbweaverSettings.HideOrb(new HiddenOrb("rc:work:away", DateTime.UtcNow, "a", "Other machine", "", "laptop"));
            RemoteControlSessions.SetListedByPeerForTests(new Dictionary<string, IReadOnlySet<string>>
            {
                ["mini"] = new HashSet<string> { "rc:work:still-running" }
            });

            manager.ScanAndUpdate();
            manager.ScanAndUpdate();

            Assert.Equal(new[] { "rc:work:away" }, OrbweaverSettings.HiddenOrbIds().ToArray());
        }
        finally
        {
            RemoteControlSessions.SetListedByPeerForTests(new Dictionary<string, IReadOnlySet<string>>());
            OrbweaverSettings.PeerLinkEnabled = false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using static Orbweaver.Tests.PaperclipFx;

namespace Orbweaver.Tests;

// CB-431: the roster the relay serves while the UI is parked is
// RelayService.RelayScan, which is HeadlessSnapshot with orb lifetime off. The
// live scan adds Paperclip rows (SessionManager.ScanAndUpdateCore); this pins
// that the headless one does too, because a parked or unattended host is the
// Paperclip user's usual host and a phone that never sees the company can never
// be sent an answerable prompt or a broke push for it.
//
// Written against develop at 8bfc5e7b9 and expected RED there: HeadlessSnapshot
// reads only the status directories. The local row is the positive control, so
// a red result means "Paperclip rows are missing", not "the scan saw nothing".
[Collection("Settings")]
public class RelayHeadlessPaperclipTests
{
    private static readonly Func<Dictionary<string, string>?> NoJobs = () => new Dictionary<string, string>();
    private static readonly DateTime Long = DateTime.UtcNow.AddDays(3);

    private static PaperclipAgent A(string id, string? lead = null, IReadOnlyList<PaperclipNeed>? needs = null) =>
        new(id, id, "r", "t", "idle", lead, "paperclip_runner", needs is { Count: > 0 } ? "waiting" : "idle",
            PaperclipMark.None, null, needs ?? [], null, null, null, null, null, Now.AddHours(-3),
            "http://127.0.0.1:3100/EX/agents/" + id, false, null, null, 5000, 0, null);

    private static IReadOnlyList<(string SessionId, SessionStatus Status)> ScanWith(IReadOnlyList<PaperclipAgent> agents)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb431-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "local.txt"), JsonSerializer.Serialize(
                new SessionStatus { State = "idle", Title = "local", Cwd = "/tmp/x", SessionPid = 4242 }));
            PaperclipSessions.SetSnapshotForTests(agents);
            PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected");
            return RelayService.RelayScan(dir, NoJobs, _ => true, Long, onWindows: false);
        }
        finally
        {
            PaperclipSessions.SetSnapshotForTests([]);
            PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "");
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ThePhonesHeadlessRoster_HasTheCompany_AndTheLocalSessionBesideIt()
    {
        var roster = ScanWith([A("lead"), A("w", "lead", needs: [new("approval", "p1", "Hire QA", "http://x/p1")])]);
        var ids = roster.Select(r => r.SessionId).ToList();
        Assert.Contains("local", ids);                       // control: the scan did read the directory
        Assert.Contains("paperclip:lead", ids);
        Assert.Contains("paperclip:w", ids);
        var w = roster.Single(r => r.SessionId == "paperclip:w").Status;
        Assert.Equal(SessionSource.Paperclip, w.Source);
        Assert.Equal("paperclip:lead", w.Lead);
        Assert.Equal("waiting", w.State);
    }

    [Fact]
    public void WithNoCompany_TheHeadlessRosterHasNoPaperclipRow()
    {
        var ids = ScanWith([]).Select(r => r.SessionId).ToList();
        Assert.Contains("local", ids);
        Assert.DoesNotContain(ids, i => i.StartsWith("paperclip:"));
    }
}

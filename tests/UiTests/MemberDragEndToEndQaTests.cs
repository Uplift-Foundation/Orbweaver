using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// QA (Nadia): Warren's bug end to end. A member dragged inside a laid-out group keeps
// its place: the offset is stored, a re-arrange puts the member back at lead + offset,
// and without the stored offset it goes back to the fan (the negative control).
[Collection("Settings")]
public class MemberDragEndToEndQaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-qa-member-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly List<string> _offsetKeys = new();
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;

    public MemberDragEndToEndQaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var key in _offsetKeys) OrbweaverSettings.SetOrbMemberOffset(key, null);
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SessionManager Scan(params (string Id, string Cli)[] sessions)
    {
        foreach (var (id, cli) in sessions)
            File.WriteAllText(Path.Combine(_dir, id + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    Cli = cli, Cwd = "/Users/user/qa-member", Title = "",
                    SessionPid = Environment.ProcessId, TermProgram = "iTerm.app", Tty = "/dev/ttys004",
                }));
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var manager = new SessionManager(_dir);
        manager.ScanAndUpdate();
        return manager;
    }

    private static Dictionary<string, OrbWindow> Windows(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static void Glide(SessionManager m)
    {
        typeof(SessionManager).GetField("_arrangeAnimStart", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(m, Environment.TickCount64 - 700);
        typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(m, new object?[] { null, EventArgs.Empty });
    }

    private static (double X, double Y) Rel(IOrbDragHost host, string id, string lead)
    {
        var a = host.DiscOf(id); var b = host.DiscOf(lead);
        return (a.X - b.X, a.Y - b.Y);
    }

    private (SessionManager m, OrbWindow lead, OrbWindow member) ArrangedTeam()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var m = Scan(("lead", ""), ("member", "codex"), ("solo", "grok"));
        m.StatusFor("member")!.Lead = "lead";
        m.ArrangeOrbsInPattern();
        Glide(m);
        Assert.True(m.IsArranged);
        var w = Windows(m);
        _offsetKeys.Add(w["member"].SoundKey);
        _keys.Add(OrbGroupMenu.KeyOf("member", m.AllStatuses));
        _keys.Add(OrbGroupMenu.KeyOf("lead", m.AllStatuses));
        return (m, w["lead"], w["member"]);
    }

    [AvaloniaFact]
    public void ADraggedMemberLandsAtLeadPlusItsOffsetAfterARearrange()
    {
        var (m, lead, member) = ArrangedTeam();
        IOrbDragHost host = m;
        var fan = Rel(host, "member", "lead");
        var units = OrbWindow.UnitsPerDip(member);

        // Drag the member 90 px right and 70 px down of its fan place, then release.
        member.Position = new PixelPoint(member.Position.X + 90, member.Position.Y + 70);
        var dropped = Rel(host, "member", "lead");
        host.SaveMemberOffset("member");

        var stored = OrbweaverSettings.OrbMemberOffsetFor(member.SoundKey);
        Assert.NotNull(stored);
        Assert.Equal(dropped.X / units, stored!.Value.Dx, 3);
        Assert.Equal(dropped.Y / units, stored.Value.Dy, 3);

        m.ReapplyArrangement();
        Glide(m);

        var landed = Rel(host, "member", "lead");
        Assert.True(Math.Abs(landed.X - dropped.X) <= 3 && Math.Abs(landed.Y - dropped.Y) <= 3,
            $"fan {fan}, dropped {dropped}, landed {landed}");
        // The drag moved it: the landing is not the fan (guards a vacuous pass).
        Assert.True(Math.Abs(dropped.X - fan.X) > 40 || Math.Abs(dropped.Y - fan.Y) > 40);
    }

    [AvaloniaFact]
    public void WithoutAStoredOffsetARearrangePutsTheMemberBackInTheFan()
    {
        var (m, lead, member) = ArrangedTeam();
        IOrbDragHost host = m;
        var fan = Rel(host, "member", "lead");

        member.Position = new PixelPoint(member.Position.X + 90, member.Position.Y + 70);
        // no SaveMemberOffset: the negative control
        m.ReapplyArrangement();
        Glide(m);

        var landed = Rel(host, "member", "lead");
        Assert.True(Math.Abs(landed.X - fan.X) <= 3 && Math.Abs(landed.Y - fan.Y) <= 3, $"fan {fan}, landed {landed}");
    }

    [AvaloniaFact]
    public void ADraggedMemberKeepsItsGroupAndItsTeam()
    {
        var (m, lead, member) = ArrangedTeam();
        IOrbDragHost host = m;
        var groupBefore = host.GroupIdOf("member");
        var teamBefore = host.TeamIdsOf("member").ToList();

        member.Position = new PixelPoint(member.Position.X + 90, member.Position.Y + 70);
        OrbDragCommit.Release(host, "member", false, 90, 70, 1);

        Assert.Equal(groupBefore, host.GroupIdOf("member"));
        Assert.Equal(teamBefore, host.TeamIdsOf("member").ToList());
        Assert.Equal(host.GroupIdOf("lead"), host.GroupIdOf("member"));
        Assert.NotNull(OrbweaverSettings.OrbMemberOffsetFor(member.SoundKey));
    }

    [AvaloniaFact]
    public void ADraggedMemberInAFreeModeUserGroupKeepsItsOffsetToo()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var m = Scan(("lead", ""), ("member", "codex"));
        m.StatusFor("member")!.Lead = "lead";
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        var leadKey = OrbGroupMenu.KeyOf("lead", m.AllStatuses); _keys.Add(leadKey);
        var memberKey = OrbGroupMenu.KeyOf("member", m.AllStatuses); _keys.Add(memberKey);
        OrbweaverSettings.AssignTeamToGroup(leadKey, new[] { memberKey }, group.Id);
        m.ReapplyArrangement();
        Glide(m);
        Assert.False(m.IsArranged);
        var w = Windows(m);
        _offsetKeys.Add(w["member"].SoundKey);
        IOrbDragHost host = m;
        Assert.True(host.IsLaidOut("member"));

        w["member"].Position = new PixelPoint(w["member"].Position.X + 80, w["member"].Position.Y + 60);
        var dropped = Rel(host, "member", "lead");
        OrbDragCommit.Release(host, "member", false, 80, 60, 1);
        m.ReapplyArrangement();
        Glide(m);

        var landed = Rel(host, "member", "lead");
        Assert.True(Math.Abs(landed.X - dropped.X) <= 3 && Math.Abs(landed.Y - dropped.Y) <= 3,
            $"dropped {dropped}, landed {landed}");
    }
}

using System.Text.Json;
using Xunit;
using static Orbweaver.Tests.PaperclipFx;
using static Orbweaver.PaperclipSessions;

namespace Orbweaver.Tests;

// CB-421: the roster the app remembers between launches, so a key refused (or an
// unreachable Paperclip) at the very first poll after a restart still draws the
// company, dimmed, with the reason, instead of drawing nothing.
[Collection("Settings")]
public class PaperclipRosterTests : IDisposable
{
    private const string Url = "http://192.0.2.10:3100";
    private const string Co = "co1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb421-roster-" + Guid.NewGuid().ToString("N"));
    private readonly IRosterStore _was = RosterStore;
    private readonly FileRosterStore _store;

    public PaperclipRosterTests()
    {
        _store = new FileRosterStore(_dir);
        RosterStore = _store;
        ForgetRoster();
        Publish([], PaperclipLink.Off, "off");
    }

    public void Dispose()
    {
        RosterStore = _was;
        Publish([], PaperclipLink.Off, "off");
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static PaperclipConfig Cfg(string url = Url, string company = Co) => new(true, url, company, "KEY", null, "fold");

    private static PaperclipAgent Live(string id, string? lead = null, string state = "idle", string name = "",
        string? task = null, PaperclipMark mark = PaperclipMark.None, string? reason = null, int? budget = null) =>
        new(id, name == "" ? "Agent " + id : name, "engineer", "Engineer", "idle", lead, "paperclip_runner", state, mark, reason,
            [], task, "Bash", Now, "failed", budget, Now, Url + "/EX/agents/" + id, state == "idle" && mark == PaperclipMark.None,
            null, null, 5000, 4200);

    private static PollState LiveState(params PaperclipAgent[] agents) =>
        PollState.Initial with { Published = agents, IssuePrefix = "EX", Link = PaperclipLink.Connected, Status = "Connected" };

    private static PollState NoReadState(PaperclipLink link, string status) =>
        PollState.Initial with { Published = null, Link = link, Status = status };

    private IReadOnlyList<PaperclipAgent> Remember(params PaperclipAgent[] agents)
    {
        PublishStep(LiveState(agents), Cfg(), [], _store, Now);
        return LoadRemembered(Cfg(), _store);
    }

    private static IReadOnlyList<SessionManager.ScanEntry> Rows() =>
        new PaperclipScan().Entries(Snapshot(), Link, StatusText, "fold", Now, Remembered);

    private static readonly PaperclipAgent[] Team = [Live("lead", name: "Lead"), Live("x", "lead", name: "X"), Live("y", "lead", name: "Y")];

    // ---- the refused / unreachable first poll, with a remembered roster ----

    [Fact]
    public async Task ARefusedKeyAtTheFirstPoll_DrawsTheRememberedCompanyDimmed_WithTheReconnectLine()
    {
        var remembered = Remember(Team);
        Assert.Equal(3, remembered.Count);
        Publish([], PaperclipLink.Off, "off");   // a fresh launch

        var f = new FakePaperclip();
        f.Gets["/api/companies"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        f.Gets[$"/api/companies/{Co}/agents"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        f.Gets[$"/api/companies/{Co}/live-runs"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        var step = await StepAsync(f, Cfg(), PollState.Initial, Now, default);
        Assert.Equal(PaperclipLink.NeedsReconnect, step.Next.Link);
        Assert.Null(step.Next.Published);

        PublishStep(step.Next, Cfg(), remembered, _store, Now);

        Assert.True(Remembered);
        Assert.Equal(3, Snapshot().Count);
        var rows = Rows();
        Assert.Equal(new[] { "paperclip:lead", "paperclip:x", "paperclip:y" }, rows.Select(r => r.SessionId).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(OrbPresence.Parked, r.Status.Presence);
            Assert.Equal("idle", r.Status.State);
            Assert.Equal(PaperclipMark.None, r.Status.Mark);
            Assert.Null(r.Status.BudgetPercent);
            Assert.Equal(new[] { "Reconnect to Paperclip" }, r.Status.HoverLines.ToArray());
        });
        Assert.Equal("paperclip:lead", rows.Single(r => r.SessionId == "paperclip:x").Status.Lead);   // the picture is kept
    }

    [Fact]
    public async Task AnUnreachablePaperclipAtTheFirstPoll_DrawsThemLikewise_WithTheCantReachLine()
    {
        var remembered = Remember(Team);
        var f = new FakePaperclip();
        f.Gets["/api/companies"] = FakePaperclip.Status(PaperclipHttpKind.Unreachable, 0, "refused");
        f.Gets[$"/api/companies/{Co}/agents"] = FakePaperclip.Status(PaperclipHttpKind.Unreachable, 0, "refused");
        var step = await StepAsync(f, Cfg(), PollState.Initial, Now, default);

        PublishStep(step.Next, Cfg(), remembered, _store, Now);

        Assert.True(Remembered);
        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(OrbPresence.Parked, r.Status.Presence);
            Assert.Equal(new[] { "Can't reach 192.0.2.10:3100." }, r.Status.HoverLines.ToArray());
        });
    }

    [Fact]
    public void BeforeTheFirstPoll_TheRememberedCompanyAlreadyDraws_ParkedWithConnecting()
    {
        var remembered = Remember(Team);
        Publish(remembered, PaperclipLink.Connecting, "Connecting to Paperclip…", remembered: true);
        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(OrbPresence.Parked, r.Status.Presence);
            Assert.Equal(new[] { "Connecting to Paperclip…" }, r.Status.HoverLines.ToArray());
        });
    }

    [Fact]
    public async Task TheControl_WithNoFile_NoOrbsAreDrawn_AsToday()
    {
        Assert.False(File.Exists(_store.Path_));
        var remembered = LoadRemembered(Cfg(), _store);
        Assert.Empty(remembered);

        var f = new FakePaperclip();
        f.Gets["/api/companies"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        var step = await StepAsync(f, Cfg(), PollState.Initial, Now, default);
        PublishStep(step.Next, Cfg(), remembered, _store, Now);

        Assert.False(Remembered);
        Assert.Empty(Snapshot());
        Assert.Empty(Rows());
    }

    // ---- the live snapshot takes over, and is never displaced ----

    [Fact]
    public void TheFirstSuccessfulRead_ReplacesTheRememberedRows_AndAgentsThatAreGoneDisappear()
    {
        var remembered = Remember(Team);
        Publish(remembered, PaperclipLink.Connecting, "Connecting to Paperclip…", remembered: true);
        Assert.Equal(3, Rows().Count);

        // "y" no longer exists in Paperclip.
        PublishStep(LiveState(Live("lead", name: "Lead"), Live("x", "lead", name: "X")), Cfg(), remembered, _store, Now);

        Assert.False(Remembered);
        Assert.Equal(new[] { "lead", "x" }, Snapshot().Select(a => a.Id).ToArray());
        // and the picture on disk follows
        Assert.Equal(new[] { "lead", "x" }, _store.Load()!.Agents.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task OnceALiveSnapshotExists_ALaterRefusal_KeepsTheLiveRows_NotTheRememberedOnes()
    {
        var remembered = Remember(Team);
        var live = LiveState(Live("lead", name: "Lead"), Live("x", "lead", name: "X", mark: PaperclipMark.Broke, reason: "boom"));
        PublishStep(live, Cfg(), remembered, _store, Now);
        Assert.False(Remembered);

        // The loop empties its remembered list once live; modelled here as the caller does.
        var refused = live with { Link = PaperclipLink.NeedsReconnect, Status = "Reconnect to Paperclip" };
        PublishStep(refused, Cfg(), [], _store, Now);

        Assert.False(Remembered);
        Assert.Equal(new[] { "lead", "x" }, Snapshot().Select(a => a.Id).ToArray());   // two, not the remembered three
        await Task.CompletedTask;
    }

    [Fact]
    public void ARememberedPublish_IsAChangeTheScanIsToldAbout_AndTheLiveOneAfterItToo()
    {
        var count = 0;
        Action handler = () => Interlocked.Increment(ref count);
        Changed += handler;
        try
        {
            var rows = Remember(Team);
            count = 0;
            Publish(rows, PaperclipLink.Connecting, "Connecting to Paperclip…", remembered: true);
            Assert.Equal(1, count);
            Publish(rows, PaperclipLink.Connecting, "Connecting to Paperclip…", remembered: true);
            Assert.Equal(1, count);                                                    // unchanged: nothing
            Publish(rows, PaperclipLink.Connecting, "Connecting to Paperclip…", remembered: false);
            Assert.Equal(2, count);                                                    // same rows, now live
        }
        finally { Changed -= handler; }
    }

    // ---- what is written, and how ----

    [Fact]
    public void TheFileHoldsOnlyWhatDrawsSleepingOrbs_NoTaskSpendErrorKeyOrAdapter()
    {
        var secretKey = "KEY";
        var agents = new[]
        {
            Live("lead", name: "Lead", task: "SECRET TASK TEXT", mark: PaperclipMark.Broke, reason: "SECRET ERROR REASON", budget: 84),
            Live("x", "lead", name: "X"),
        };
        PublishStep(LiveState(agents), Cfg(), [], _store, Now);

        var json = File.ReadAllText(_store.Path_);
        Assert.DoesNotContain("SECRET TASK TEXT", json);
        Assert.DoesNotContain("SECRET ERROR REASON", json);
        Assert.DoesNotContain(secretKey, json);
        Assert.DoesNotContain("paperclip_runner", json);   // adapterType, whose config carries local paths
        Assert.DoesNotContain("4200", json);               // spend
        Assert.DoesNotContain("Bash", json);               // activity

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "Agents", "CompanyId", "IssuePrefix", "SavedUtc", "Url" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        var first = doc.RootElement.GetProperty("Agents")[0];
        Assert.Equal(new[] { "Id", "Name", "ReportsTo", "Role", "Title" },
            first.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("http://192.0.2.10:3100", doc.RootElement.GetProperty("Url").GetString());
        Assert.Equal("EX", doc.RootElement.GetProperty("IssuePrefix").GetString());
    }

    [Fact]
    public void RememberedRows_AreSleepingOrbs_WithPageUrlsFromThePrefix_StampedWithTheSaveTime()
    {
        var rows = Remember(Team);
        var a = rows.Single(r => r.Id == "x");
        Assert.Equal(("idle", PaperclipMark.None, true, "lead"), (a.State, a.Mark, a.Sleeping, a.ReportsTo));
        Assert.Empty(a.Needs);
        Assert.Null(a.BudgetPercent);
        Assert.Equal("http://192.0.2.10:3100/EX/agents/x", a.Url);
        Assert.Equal(Now, a.LastActivity);                // the picture's own time, never "now"
        Assert.Equal("X", a.Name);
        Assert.Equal(("engineer", "Engineer"), (a.Role, a.Title));
    }

    [Fact]
    public void AnUnchangedRoster_IsNotRewritten_AChangedOneIs()
    {
        var counting = new CountingStore();
        PublishStep(LiveState(Team), Cfg(), [], counting, Now);
        PublishStep(LiveState(Team), Cfg(), [], counting, Now.AddMinutes(1));
        PublishStep(LiveState(Live("lead", name: "Lead"), Live("x", "lead", name: "X"), Live("y", "lead", name: "Y", task: "task text only")), Cfg(), [], counting, Now.AddMinutes(2));
        Assert.Equal(1, counting.Saves);                                  // task text is not part of the picture

        PublishStep(LiveState(Live("lead", name: "Lead"), Live("x", "lead", name: "X Renamed"), Live("y", "lead", name: "Y")), Cfg(), [], counting, Now.AddMinutes(3));
        Assert.Equal(2, counting.Saves);
    }

    private sealed class CountingStore : IRosterStore
    {
        internal int Saves;
        public RosterFile? Load() => null;
        public void Save(RosterFile roster) => Saves++;
        public void Delete() { }
    }

    [Fact]
    public void NoCompanyChosen_NothingIsRemembered()
    {
        var counting = new CountingStore();
        PublishStep(LiveState(Team), Cfg(company: ""), [], counting, Now);
        Assert.Equal(0, counting.Saves);
    }

    [UnixModeFact]
    public void TheFileIsCreatedOwnerOnly_0600()
    {
        Remember(Team);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.Path_));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [WindowsAclFact]
    public void TheFileIsCreatedOwnerOnly_AclInheritanceOff()
    {
        Remember(Team);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var security = new FileInfo(_store.Path_).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>());
        Assert.Equal(me, rule.IdentityReference);
    }

    // ---- ignored, and removed ----

    [Fact]
    public void ACorruptOrUnreadableFile_IsIgnoredSilently()
    {
        Directory.CreateDirectory(_dir);
        foreach (var junk in new[] { "{{{ not json", "", "null", "[]", "{\"Url\":5}" })
        {
            File.WriteAllText(_store.Path_, junk);
            Assert.Empty(LoadRemembered(Cfg(), _store));
        }
        Assert.Null(new FileRosterStore(Path.Combine(_dir, "nope")).Load());
    }

    [Fact]
    public void AStoreThatThrows_IsIgnoredToo()
    {
        Assert.Empty(PaperclipRoster.Remembered(new ThrowingStore(), Url, Co, out var file));
        Assert.Null(file);
    }

    private sealed class ThrowingStore : IRosterStore
    {
        public RosterFile? Load() => throw new IOException();
        public void Save(RosterFile roster) => throw new IOException();
        public void Delete() => throw new IOException();
    }

    [Fact]
    public void AFileForAnotherAddressOrCompany_IsIgnored_AndRemoved()
    {
        Remember(Team);
        Assert.Empty(LoadRemembered(Cfg(url: "http://elsewhere:3100"), _store));
        Assert.False(File.Exists(_store.Path_));

        Remember(Team);
        Assert.Empty(LoadRemembered(Cfg(company: "other"), _store));
        Assert.False(File.Exists(_store.Path_));

        Remember(Team);
        Assert.Empty(LoadRemembered(Cfg(company: ""), _store));   // nothing chosen yet: nothing to match
    }

    [Fact]
    public void TheAddressIsMatchedSpellingInsensitively()
    {
        Remember(Team);
        Assert.Equal(3, LoadRemembered(Cfg(url: "HTTP://192.0.2.10:3100/"), _store).Count);
    }

    [Fact]
    public void ARowWithNoId_IsDropped_TheRestAreKept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.Path_, JsonSerializer.Serialize(new RosterFile("http://192.0.2.10:3100", Co, "EX", Now,
            [new("", "No id", "r", "t", null), new("a", "A", "r", "t", null)])));
        Assert.Equal("a", Assert.Single(LoadRemembered(Cfg(), _store)).Id);
    }

    [Fact]
    public void ForgetRoster_RemovesTheFile_AndAnAbsentOneIsNotAnError()
    {
        Remember(Team);
        Assert.True(File.Exists(_store.Path_));
        ForgetRoster();
        Assert.False(File.Exists(_store.Path_));
        ForgetRoster();
    }

    [Fact]
    public void FileStoreOperations_NeverThrow_WhenTheDirectoryCannotBeUsed()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "cb421-roster-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where a directory should be");
        try
        {
            var store = new FileRosterStore(Path.Combine(blocker, "sub"));
            store.Save(new RosterFile(Url, Co, "EX", Now, []));
            Assert.Null(store.Load());
            store.Delete();
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void TheNullStore_RemembersNothing()
    {
        var n = new NullRosterStore();
        n.Save(new RosterFile(Url, Co, "EX", Now, []));
        Assert.Null(n.Load());
        n.Delete();
    }
}

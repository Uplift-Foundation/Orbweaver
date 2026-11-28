using System.Text.Json;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-357 (reopened): browsing a far desktop's folders over the peer link. The
// codec, the answering gate in its order, the asking client's failure values,
// the host's routing and the refusal copy.
public class PeerListDirTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PeerDirResult Listing(int items = 2) => new(true, "ok", new PeerDirListing(
        "/Users/dev/Source", "/Users/dev",
        true, Enumerable.Range(0, items).Select(i => new PeerDirItem("d" + i)).ToList()));

    // --- the codec -------------------------------------------------------------

    [Fact]
    public void ARequestRoundTripsAndTheTopLevelIsAnEmptyPath()
    {
        Assert.Equal("/a/b", PeerLaunchWire.ParseListDirRequest(PeerLaunchWire.EncodeListDirRequest("/a/b")));
        Assert.Equal("", PeerLaunchWire.ParseListDirRequest(PeerLaunchWire.EncodeListDirRequest("")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"path\":5}")]
    [InlineData("{\"path\":null}")]
    [InlineData("{\"path\":\"a\\u0001b\"}")]
    [InlineData("\"x\"")]
    public void AMalformedRequestIsNull(string json) =>
        Assert.Null(PeerLaunchWire.ParseListDirRequest(J(json)));

    [Fact]
    public void ATooLongPathIsNull() =>
        Assert.Null(PeerLaunchWire.ParseListDirRequest(PeerLaunchWire.EncodeListDirRequest(new string('a', 5000))));

    [Fact]
    public void AListingRoundTripsWithItsRootsMarkers()
    {
        var sent = new PeerDirResult(true, "ok", new PeerDirListing("", "", false,
            [new PeerDirItem("dev", "/Users/dev", null, true), new PeerDirItem("proj", "D:\\proj", "D:")]));

        var back = PeerLaunchWire.ParseDir(PeerLaunchWire.EncodeDir(sent))!;

        Assert.True(back.Ok);
        Assert.Equal(sent.Listing!.Items, back.Listing!.Items);
        Assert.Equal("", back.Listing.Path);
        Assert.False(back.Listing.Truncated);
        Assert.True(PeerLaunchWire.ParseDir(PeerLaunchWire.EncodeDir(Listing()))!.Listing!.Truncated);
    }

    [Fact]
    public void ARefusalCarriesItsCodeAndNothingElse()
    {
        var body = PeerLaunchWire.EncodeDir(new PeerDirResult(false, "forbidden_path", Listing().Listing));

        Assert.Equal(["code", "ok", "truncated"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(PeerDirResult.Fail("forbidden_path"), PeerLaunchWire.ParseDir(body));
    }

    [Fact]
    public void AnOkWithoutAListingIsAnEmptyOne() =>
        Assert.Empty(PeerLaunchWire.ParseDir(J("{\"ok\":true,\"code\":\"ok\"}"))!.Listing!.Items);

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":\"yes\",\"code\":\"ok\"}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"path\":\"a\\u0001\"}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"parent\":\"a\\u0001\"}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"items\":[null]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"items\":[{\"name\":\"\"}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"items\":[{\"name\":\"a\",\"path\":\"x\\u0001\"}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"items\":[{\"name\":\"a\",\"drive\":\"123456789\"}]}")]
    public void AMalformedListingIsNull(string json) =>
        Assert.Null(PeerLaunchWire.ParseDir(J(json)));

    [Fact]
    public void AnOkResultWithNoListingEncodesAsARefusalWithItsCode() =>
        Assert.Equal(PeerDirResult.Fail("ok"), PeerLaunchWire.ParseDir(PeerLaunchWire.EncodeDir(new PeerDirResult(true, "ok", null))));

    [Fact]
    public void ARefusalWithAnUnsaneCodeIsNull() =>
        Assert.Null(PeerLaunchWire.ParseDir(J("{\"ok\":false,\"code\":\"a\\u0001\"}")));

    [Fact]
    public void TooManyItemsIsNull() =>
        Assert.Null(PeerLaunchWire.ParseDir(PeerLaunchWire.EncodeDir(Listing(PeerLaunchWire.MaxDirItems + 1))));

    // --- the answering gate ------------------------------------------------------

    // The in-memory filesystem answers at once, so a test that is not about the read bound gives the
    // server none: a finite one is a race against the thread pool (CB-496, a loaded runner gave
    // `unreadable` for a forbidden path). The tests of the bound itself pass their own, with a gate.
    private static readonly TimeSpan NoReadBound = Timeout.InfiniteTimeSpan;

    private static (PeerBrowseServer Server, FakeBrowseFileSystem Fs, List<string> Calls) Server(bool allowed)
    {
        var fs = new FakeBrowseFileSystem("/Users/dev", false, ["/", "/Users"], ["/System"]);
        fs.Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Source").Add("/Users/dev/Source/app")
            .Add("/Users/dev/Source/.hidden").Add("/Users/dev/Other").Add("/System").Add("/System/Library");
        var calls = new List<string>();
        var settings = new BrowseSettings(["/Users/dev/Source"], false);
        var server = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => allowed, fs, () => { calls.Add("settings"); return settings; }, NoReadBound));
        return (server, fs, calls);
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsParsedOrListed()
    {
        var (server, fs, calls) = Server(allowed: false);

        // A request that would otherwise list, and one that is not even a request.
        foreach (var body in new[] { PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source"), J("[]") })
            Assert.Equal(PeerDirResult.Fail(PeerLaunchCodes.PeerLaunchOff), await server.HandleAsync(body));

        Assert.Empty(calls);
        Assert.Empty(fs.Enumerated);
    }

    [Fact]
    public async Task ABadShapeIsABadFrameAndIsNotListed()
    {
        var (server, fs, calls) = Server(true);

        Assert.Equal(PeerDirResult.Fail(RelayReplies.BadFrame), await server.HandleAsync(J("{}")));
        Assert.Empty(calls);
        Assert.Empty(fs.Enumerated);
    }

    [Fact]
    public async Task TheTopLevelIsTheRootsAndNoMore()
    {
        var (server, _, _) = Server(true);

        var result = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest(""));

        Assert.True(result.Ok);
        Assert.Equal("", result.Listing!.Path);
        Assert.Equal(["/Users/dev/Source"], result.Listing.Items.Select(i => i.Path).ToArray());
    }

    [Fact]
    public async Task AFolderInsideTheRootsListsItsFoldersOnlyAndNotTheHiddenOnes()
    {
        var (server, _, _) = Server(true);

        var result = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source"));

        Assert.True(result.Ok);
        Assert.Equal("/Users/dev/Source", result.Listing!.Path);
        Assert.Equal("", result.Listing.Parent);
        Assert.Equal(["app"], result.Listing.Items.Select(i => i.Name).ToArray());
    }

    [Theory]
    [InlineData("/Users/dev/Other")]   // a real folder outside the roots
    [InlineData("/System/Library")]    // a system folder
    [InlineData("relative/path")]
    public async Task APathOutsideTheRootsIsForbiddenAndNamesNothing(string path)
    {
        var (server, _, _) = Server(true);

        var result = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest(path));

        Assert.Equal(PeerDirResult.Fail(RelayReplies.ForbiddenPath), result);
    }

    [Theory]
    [InlineData("/Users/dev/Other")]            // exists, outside the roots
    [InlineData("/Users/dev/Nowhere")]          // does not exist, outside the roots
    [InlineData("/Users/dev/Source/../Other")]  // traversal out of a root
    [InlineData("~/Other")]                     // home form, outside the roots
    public async Task ForbiddenReadsTheSameWhetherOrNotThePathExists(string path)
    {
        var (server, _, _) = Server(true);

        var result = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest(path));

        // One code and one shape: no way to tell a real folder from an absent one.
        Assert.Equal(PeerDirResult.Fail(RelayReplies.ForbiddenPath), result);
    }

    [Fact]
    public async Task ALinkOutOfARootIsForbiddenAndALinkWithinItIsListed()
    {
        var (server, fs, _) = Server(true);
        fs.Add("/Users/dev/Source/escape", BrowseKind.Link, target: "/Users/dev/Other");
        fs.Add("/Users/dev/Source/inside", BrowseKind.Link, target: "/Users/dev/Source/app");

        Assert.Equal(PeerDirResult.Fail(RelayReplies.ForbiddenPath),
            await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source/escape")));

        var listing = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source"));
        Assert.Contains("inside", listing.Listing!.Items.Select(i => i.Name));
        Assert.DoesNotContain("escape", listing.Listing.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task TheServerFollowsTheSettingsItIsGivenOnEveryRequest()
    {
        var (_, fs, _) = Server(true);
        var settings = new BrowseSettings(["/Users/dev/Source"], false);
        var server = new PeerBrowseServer(new PeerBrowseServer.Seams(() => true, fs, () => settings, NoReadBound));

        Assert.Equal(RelayReplies.ForbiddenPath,
            (await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Other"))).Code);

        settings = new BrowseSettings(["/Users/dev/Source", "/Users/dev/Other"], false);

        Assert.True((await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Other"))).Ok);
    }

    [Fact]
    public async Task AMissingFolderInsideTheRootsIsUnknownPath()
    {
        var (server, _, _) = Server(true);

        Assert.Equal(RelayReplies.UnknownPath,
            (await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source/nope"))).Code);
    }

    // Wraps the model filesystem and counts the reads made inside listed children, which
    // is what the per-folder markers cost.
    private sealed class CountingFs(FakeBrowseFileSystem inner) : IBrowseFileSystem
    {
        public int Lookups, PersonaReads;
        public string Home => inner.Home;
        public bool IsWindows => inner.IsWindows;
        public bool CaseInsensitive => inner.CaseInsensitive;
        public IReadOnlyList<string> DeniedExact => inner.DeniedExact;
        public IReadOnlyList<string> DeniedTrees => inner.DeniedTrees;
        public BrowseEntry? Lookup(string parent, string name) { Lookups++; return inner.Lookup(parent, name); }
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap) => inner.Enumerate(folder, cap);
        public bool HiddenAttribute(string folder, string name) => inner.HiddenAttribute(folder, name);
        public bool PersonaAt(string folder) { PersonaReads++; return inner.PersonaAt(folder); }
    }

    // The reads made inside a folder, by the folder they were made in.
    private sealed class ReadLog(FakeBrowseFileSystem inner) : IBrowseFileSystem
    {
        public readonly List<string> LookedIn = [];
        public readonly List<string> PersonaAtFolders = [];
        public string Home => inner.Home;
        public bool IsWindows => inner.IsWindows;
        public bool CaseInsensitive => inner.CaseInsensitive;
        public IReadOnlyList<string> DeniedExact => inner.DeniedExact;
        public IReadOnlyList<string> DeniedTrees => inner.DeniedTrees;
        public BrowseEntry? Lookup(string parent, string name) { LookedIn.Add(parent); return inner.Lookup(parent, name); }
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap) => inner.Enumerate(folder, cap);
        public bool HiddenAttribute(string folder, string name) => inner.HiddenAttribute(folder, name);
        public bool PersonaAt(string folder) { PersonaAtFolders.Add(folder); return inner.PersonaAt(folder); }
    }

    private static (ReadLog Fs, PeerBrowseServer Server) HomeWithDocuments()
    {
        var inner = new FakeBrowseFileSystem("/Users/dev", false, ["/", "/Users"], ["/System"]);
        inner.Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Documents").Add("/Users/dev/app")
            .Add("/Users/dev/Documents/.git").Add("/Users/dev/app/.git")
            .Add("/Users/dev/Library").Add("/Users/dev/Documents/notes")
            .Add("/Users/dev/linked", BrowseKind.Link, target: "/Users/dev/Documents");
        var fs = new ReadLog(inner);
        var server = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => true, fs, () => new BrowseSettings(["/Users/dev"], true), NoReadBound));
        return (fs, server);
    }

    [Fact]
    public async Task AProtectedFolderListsWithNoBadgeAndIsNeverReadInsideWhileAPlainOneKeepsItsBadge()
    {
        var (fs, server) = HomeWithDocuments();

        var home = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev"));

        var items = home.Listing!.Items.ToDictionary(i => i.Name);
        Assert.True(items["app"].Git);                       // the control: a plain child is read
        Assert.False(items["Documents"].Git);
        Assert.False(items["Library"].Git);
        Assert.False(items["linked"].Git);                   // a link whose target is Documents
        Assert.Contains("/Users/dev/app", fs.LookedIn);
        Assert.DoesNotContain(fs.LookedIn, f => f.StartsWith("/Users/dev/Documents") || f.StartsWith("/Users/dev/Library"));
        Assert.DoesNotContain(fs.PersonaAtFolders, f => f.StartsWith("/Users/dev/Documents") || f.StartsWith("/Users/dev/Library"));
    }

    [Fact]
    public async Task OpeningAProtectedFolderStillListsItAndItsChildrenAreNotReadInsideEither()
    {
        var (fs, server) = HomeWithDocuments();

        var docs = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Documents"));

        Assert.True(docs.Ok);
        Assert.Equal(["notes"], docs.Listing!.Items.Select(i => i.Name).ToArray());
        Assert.DoesNotContain(fs.LookedIn, f => f.StartsWith("/Users/dev/Documents/notes"));
    }

    [Theory]
    [InlineData("/Users/dev/Desktop", true)]
    [InlineData("/Users/dev/Documents", true)]
    [InlineData("/Users/dev/Downloads", true)]
    [InlineData("/Users/dev/Library", true)]
    [InlineData("/Users/dev/Library/Mail", true)]
    [InlineData("/Users/dev/Movies", true)]
    [InlineData("/Users/dev/Music", true)]
    [InlineData("/Users/dev/Pictures", true)]
    [InlineData("/Users/dev/documents", true)]            // the filesystem's own case rule
    [InlineData("/Volumes", true)]
    [InlineData("/Volumes/Backup/app", true)]
    [InlineData("/Users/dev/app", false)]
    [InlineData("/Users/dev/Documents2", false)]          // a prefix is not the folder
    [InlineData("/Users/other/Documents", false)]         // another user's home is not this one's
    [InlineData("/VolumesX", false)]
    public void TheProtectedFoldersAreTheMacOsPrivacyOnesUnderHomeAndTheVolumes(string path, bool expected) =>
        Assert.Equal(expected, ProtectedFolders.IsProtected(
            new FakeBrowseFileSystem("/Users/dev", false, [], []), path, []));

    [Fact]
    public void AVolumeTheUserChoseAsARootIsNotHeldBackButOtherVolumesAre()
    {
        var fs = new FakeBrowseFileSystem("/Users/dev", false, [], []);
        string[] roots = ["/Volumes/Ext/projects"];

        Assert.False(ProtectedFolders.IsProtected(fs, "/Volumes/Ext/projects/alpha", roots));
        Assert.True(ProtectedFolders.IsProtected(fs, "/Volumes/Other/x", roots));
        Assert.True(ProtectedFolders.IsProtected(fs, "/Users/dev/Documents/x", ["/Users/dev/Documents/x"]));   // home names are not exempted
    }

    [Fact]
    public void WindowsHasNoSuchPromptSoAFolderCalledDocumentsIsReadAsUsual()
    {
        var fs = new FakeBrowseFileSystem("C:\\Users\\dev", true, [], []);
        Assert.False(ProtectedFolders.IsProtected(fs, "C:\\Users\\dev\\Documents", []));
        Assert.False(ProtectedFolders.IsProtected(fs, "/Volumes/x", []));
    }

    [Fact]
    public async Task APeerListingCarriesTheBadgesAcrossTheWire()
    {
        var (_, server) = HomeWithDocuments();

        var home = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev"));
        var back = PeerLaunchWire.ParseDir(PeerLaunchWire.EncodeDir(home))!;

        Assert.True(back.Listing!.Items.Single(i => i.Name == "app").Git);
        Assert.False(back.Listing.Items.Single(i => i.Name == "Documents").Git);
    }

    [Fact]
    public async Task AnUnreadableAnswerLogsWhetherItWasATimeoutOrAThrowAndNeverThePath()
    {
        var (_, fs, _) = Server(true);
        var log = new List<string>();
        var settings = new BrowseSettings(["/Users/dev/Source"], false);

        fs.OnRead = _ => new IOException("gone");
        var throws = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => true, fs, () => settings, NoReadBound, log.Add));
        await throws.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source"));

        // Held until released, not slept: a loaded runner can delay the timeout's own timer past any
        // sleep, and the read must not be able to finish before the bound fires.
        using var gate = new ManualResetEventSlim();
        var slow = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => true, fs, () => { gate.Wait(); return settings; }, TimeSpan.FromMilliseconds(20), log.Add));
        await slow.HandleAsync(PeerLaunchWire.EncodeListDirRequest(""));
        gate.Set();

        Assert.Equal(["peer-list-dir-unreadable IOException", "peer-list-dir-unreadable TimeoutException"], log);
    }

    [Fact]
    public async Task AReadThatThrowsOrRunsOutOfTimeIsUnreadable()
    {
        var (server, fs, _) = Server(true);
        fs.OnRead = _ => new IOException("gone");

        Assert.Equal(PeerDirResult.Fail(RelayReplies.Unreadable),
            await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/Users/dev/Source")));

        using var gate = new ManualResetEventSlim();
        var slow = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => true, fs, () => { gate.Wait(); return new BrowseSettings(["/Users/dev/Source"], false); },
            TimeSpan.FromMilliseconds(20)));
        Assert.Equal(PeerDirResult.Fail(RelayReplies.Unreadable),
            await slow.HandleAsync(PeerLaunchWire.EncodeListDirRequest("")));
        gate.Set();
    }

    [Fact]
    public async Task AListingLongerThanTheWireAllowsIsCutAtTheWiresBound()
    {
        // BrowseRules already caps at 500; the server still never sends more
        // than the codec accepts.
        var fs = new FakeBrowseFileSystem("/h", false, [], []);
        fs.Add("/").Add("/h").Add("/h/r");
        for (var i = 0; i < 600; i++) fs.Add("/h/r/d" + i.ToString("000"));
        var server = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => true, fs, () => new BrowseSettings(["/h/r"], false), NoReadBound));

        var result = await server.HandleAsync(PeerLaunchWire.EncodeListDirRequest("/h/r"));

        Assert.Equal(PeerLaunchWire.MaxDirItems, result.Listing!.Items.Count);
        Assert.True(result.Listing.Truncated);
        Assert.NotNull(PeerLaunchWire.ParseDir(PeerLaunchWire.EncodeDir(result)));
    }

    // --- the asking client --------------------------------------------------------

    private sealed class Rig
    {
        public readonly List<PeerProtocol.PeerMessage> Sent = new();
        public bool SendResult = true;
        public PeerLaunchClient Client;
        public Action<PeerProtocol.PeerMessage>? OnSend;

        public Rig(TimeSpan? wait = null) => Client = new PeerLaunchClient(new PeerLaunchClient.Seams(
            (_, m) => { Sent.Add(m); OnSend?.Invoke(m); return Task.FromResult(SendResult); },
            () => [], (_, _) => Task.FromResult<(string, string)?>(null), () => DateTimeOffset.UtcNow)
        {
            ListWait = wait ?? TimeSpan.FromSeconds(10),
        });
    }

    [Fact]
    public async Task AListingIsAskedOfTheMachineAndAnsweredByIt()
    {
        var rig = new Rig();
        rig.OnSend = m => rig.Client.OnReply("mini", PeerProtocol.Message(
            PeerProtocol.ListDir, m.Id, body: PeerLaunchWire.EncodeDir(Listing())));

        var result = await rig.Client.ListDirAsync("mini", "/Users/dev/Source", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Listing!.Items.Count);
        var sent = Assert.Single(rig.Sent);
        Assert.Equal(PeerProtocol.ListDirGet, sent.Type);
        Assert.Equal("/Users/dev/Source", PeerLaunchWire.ParseListDirRequest(sent.Body!.Value));
    }

    [Fact]
    public async Task ASilentPeerIsNoAnswerAfterTheTimeout()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(50));

        Assert.Equal(PeerDirResult.Fail(PeerLaunchCodes.NoAnswer),
            await rig.Client.ListDirAsync("mini", "", CancellationToken.None));
    }

    [Fact]
    public async Task ASendThatFailsAndALinkThatDropsAreLinkLost()
    {
        var rig = new Rig();
        rig.SendResult = false;
        Assert.Equal(PeerDirResult.Fail(PeerLaunchCodes.LinkLost), await rig.Client.ListDirAsync("mini", "", CancellationToken.None));

        var rig2 = new Rig();
        rig2.OnSend = _ => rig2.Client.OnDisconnected("mini");
        Assert.Equal(PeerDirResult.Fail(PeerLaunchCodes.LinkLost), await rig2.Client.ListDirAsync("mini", "", CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreadableReplyIsNoAnswer()
    {
        var rig = new Rig();
        rig.OnSend = m => rig.Client.OnReply("mini", PeerProtocol.Message(PeerProtocol.ListDir, m.Id, body: J("[]")));
        Assert.Equal(PeerDirResult.Fail(PeerLaunchCodes.NoAnswer), await rig.Client.ListDirAsync("mini", "", CancellationToken.None));

        var rig2 = new Rig();
        rig2.OnSend = m => rig2.Client.OnReply("mini", PeerProtocol.Message(PeerProtocol.ListDir, m.Id));
        Assert.Equal(PeerDirResult.Fail(PeerLaunchCodes.NoAnswer), await rig2.Client.ListDirAsync("mini", "", CancellationToken.None));
    }

    [Fact]
    public async Task AReplyFromAnotherMachineIsNotBelieved()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(100));
        rig.OnSend = m => rig.Client.OnReply("pc", PeerProtocol.Message(
            PeerProtocol.ListDir, m.Id, body: PeerLaunchWire.EncodeDir(Listing())));

        Assert.Equal(PeerLaunchCodes.NoAnswer, (await rig.Client.ListDirAsync("mini", "", CancellationToken.None)).Code);
    }

    // --- the host's routing ---------------------------------------------------------

    [Fact]
    public async Task AStrangerIsNeverListedForAndAReplyFromOneIsNotDelivered()
    {
        using var host = new PeerMirrorHost();
        var gets = 0;
        var fs = new FakeBrowseFileSystem("/h", false, [], []);
        fs.Add("/").Add("/h");
        var server = new PeerBrowseServer(new PeerBrowseServer.Seams(
            () => { gets++; return true; }, fs, () => new BrowseSettings(["/h"], false), TimeSpan.FromSeconds(1)));
        host.ServeLaunch(null, null, null, server);

        await host.DeliverAsync("ghost", PeerProtocol.Message(PeerProtocol.ListDirGet, "id1",
            body: PeerLaunchWire.EncodeListDirRequest("")));
        await host.DeliverAsync("ghost", PeerProtocol.Message(PeerProtocol.ListDir, "id1",
            body: PeerLaunchWire.EncodeDir(Listing())));
        await Task.Delay(50);

        Assert.Equal(0, gets);
    }

    [Fact]
    public async Task AnUnboundHostStaysSilentToAListDirGet()
    {
        using var host = new PeerMirrorHost();

        // Nothing to assert beyond "does not throw": an unwired desk is an
        // older one, and the asker's clock is the answer.
        await host.DeliverAsync("ghost", PeerProtocol.Message(PeerProtocol.ListDirGet, "id1",
            body: PeerLaunchWire.EncodeListDirRequest("")));
    }

    // --- the copy ---------------------------------------------------------------------

    [Theory]
    [InlineData("forbidden_path", "mini doesn't share that folder.")]
    [InlineData("unknown_path", "That folder doesn't exist on mini.")]
    [InlineData("unreadable", "mini could not read that folder.")]
    [InlineData("rate_limited", "mini is busy. Try again in a moment.")]
    [InlineData("peer_launch_off", "mini doesn't allow other desktops to start sessions. Turn that on in its Settings.")]
    [InlineData("no_answer", "mini didn't answer. It may be asleep, or running a version that can't start sessions for another desktop.")]
    [InlineData("link_lost", "The link to mini dropped before it answered.")]
    [InlineData("bad_frame", "mini could not read that request.")]
    public void EachBrowseRefusalReadsInTheMachinesName(string code, string text) =>
        Assert.Equal(text, PeerLaunchCopy.ExplainBrowse(code, "mini"));
}

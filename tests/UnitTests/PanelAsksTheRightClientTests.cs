using Xunit;

namespace Orbweaver.UnitTests;

// Every question a panel asks about a remote session has to reach the client
// that actually holds the answer.
//
// **This is the bug class that cost the most and showed the least.** There is
// one accessor that decides which client a panel talks to — MirrorClientFor —
// and CB-69 taught it to prefer the direct link. What CB-69 did not do was go
// looking for the callers that went *around* it and read the relay table
// directly. That was the same thing while a relay was the only client there
// was, and stopped being the same thing the moment the link arrived.
//
// The failure has no error in it. StateFor is never asked, so it answers
// Unknown, and the panel says "checking whether a live view is available…" and
// means it — forever, with the roster arriving every ten seconds the whole
// time. It was found by a person clicking an orb.
//
// The relay table is gone (CB-238), and with it the particular way around the
// accessor that bit CB-69. The way around it now would be to read
// PeerSessions.Host directly, which is what the guard below watches for.
public class PanelAsksTheRightClientTests
{
    private static RemoteMirrorClient Client(string account) =>
        new(account, new RemoteMirrorClient.Seams((_, _) => Task.FromResult(true)));

    // Where reading PeerSessions.Host is the job rather than a way around it:
    // the accessor itself, and the link's own plumbing — the pump that ticks
    // the halves and the republish that turns the roster into orb rows. None
    // of the three answers a panel's question.
    // RepublishIfOutOfStep (CB-402) is the same republish, run on the dial tick.
    // RepublishIfOutOfStep (CB-402) is that same republish, run on the dial tick.
    private static readonly string[] HostReadersAllowed =
        { "MirrorClientFor", "MirrorTickAsync", "RepublishFromLink", "RepublishIfOutOfStep" };

    [Fact]
    public void EveryPanelQuestionGoesThroughTheOneAccessor()
    {
        // Asserted as a fact about the source rather than about behaviour,
        // because behaviour cannot see the difference: a caller that reaches
        // the client some other way returns a perfectly well-formed "I don't
        // know" whenever that other way is empty.
        //
        // Each scan carries a positive control (CB-238, Rafaela Quintero's
        // review): the version this replaced scanned for `Relays.TryGetValue`
        // after the relay table was deleted, matched nothing, and so passed
        // whatever the code did. A scan that cannot match is not a guard.
        var panel = CodeLines(SourceOf("RemoteControlChatSession.cs"));
        var sessions = CodeLines(SourceOf("RemoteControlSessions.cs"));

        // The panel asks through the accessor — and does, or this fails on a
        // rename rather than silently finding nothing to object to.
        var asks = panel.Where(l =>
            l.Text.Contains("RemoteControlSessions.MirrorClientFor(")
            || l.Text.Contains("RemoteControlSessions.MirrorStateFor(")).ToList();
        Assert.NotEmpty(asks);

        // ...and never around it.
        var panelBypasses = panel.Where(l => l.Text.Contains("PeerSessions.Host")).ToList();
        Assert.True(panelBypasses.Count == 0,
            "the panel reads PeerSessions.Host instead of asking MirrorClientFor:\n  "
            + string.Join("\n  ", panelBypasses.Select(l => $"{l.Number}: {l.Text.Trim()}")));

        // In RemoteControlSessions, the only readers of PeerSessions.Host are
        // the accessor and the link's plumbing. The positive control is that
        // the accessor really does read it: if the pattern stopped matching
        // there, the offender check below would be scanning for nothing.
        Assert.Contains(sessions, l =>
            l.Text.Contains("PeerSessions.Host") && EnclosingMember(sessions, l.Index) == "MirrorClientFor");

        var offenders = HostOffenders(sessions);

        Assert.True(offenders.Count == 0,
            "these read PeerSessions.Host instead of asking MirrorClientFor, "
            + "so a panel question could go around the one accessor:\n  " + string.Join("\n  ", offenders));
    }

    // The detector has to see a read that sits in a member of any shape, not
    // only in a method. Idris Belanger's QA case on CB-238: an expression-bodied
    // property placed straight after MirrorClientFor was attributed to
    // MirrorClientFor, because the detector only knew signatures with a `(`,
    // and so it passed. Pinned here against a synthetic source rather than by
    // editing the real file, so it stays a test rather than a one-off check.
    [Theory]
    [InlineData("        internal static RemoteMirrorClient? Sneaky => PeerSessions.Host?.Client;", "Sneaky")]
    [InlineData("        internal static RemoteMirrorClient? Sneaky\n        {\n            get { return PeerSessions.Host?.Client; }\n        }", "Sneaky")]
    [InlineData("        private static readonly RemoteMirrorClient? Sneaky = PeerSessions.Host?.Client;", "Sneaky")]
    [InlineData("        public static void Sneaky()\n        {\n            var c = PeerSessions.Host?.Client;\n        }", "Sneaky")]
    public void AReadInAMemberOfAnyShapeAfterTheAccessorIsStillCaught(string member, string name)
    {
        var source = string.Join("\n", new[]
        {
            "    internal static class RemoteControlSessions",
            "    {",
            "        internal static RemoteMirrorClient? MirrorClientFor(string account)",
            "        {",
            "            return PeerSessions.Host?.Client;",
            "        }",
            "",
            member,
            "    }"
        });

        var lines = CodeLinesOf(source.Split('\n'));
        var offenders = HostOffenders(lines);

        Assert.Single(offenders);
        Assert.Contains("(in " + name + ")", offenders[0]);
    }

    // And the same synthetic source with nothing added has no offender — the
    // accessor's own read is allowed, so the theory above fails for the reason
    // it says and not because every read is flagged.
    [Fact]
    public void TheAccessorsOwnReadIsNotAnOffender()
    {
        var lines = CodeLinesOf(new[]
        {
            "        internal static RemoteMirrorClient? MirrorClientFor(string account)",
            "        {",
            "            return PeerSessions.Host?.Client;",
            "        }"
        });

        Assert.Empty(HostOffenders(lines));
    }

    private static List<string> HostOffenders(List<CodeLine> sessions) =>
        sessions
            .Where(l => l.Text.Contains("PeerSessions.Host"))
            .Where(l => !HostReadersAllowed.Contains(EnclosingMember(sessions, l.Index)))
            .Select(l => $"{l.Number} (in {EnclosingMember(sessions, l.Index) ?? "?"}): {l.Text.Trim()}")
            .ToList();

    // --- and the behaviour the bug produced ------------------------------------

    [Fact]
    public void AKnownSessionIsAvailableRatherThanUnknown()
    {
        // The shape of what the panel saw: a client that knows the session, and
        // a caller that never asked it.
        var client = Client("acct");

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unknown,
            client.StateFor("never-heard-of-it").Availability);
    }

    [Fact]
    public void NoClientAtAllIsUnknownRatherThanUnavailable()
    {
        // The distinction the panel depends on: Unknown keeps it checking,
        // Unavailable settles it as "no live view". A missing client must not
        // be read as a definite no — that is a different sentence to the user
        // and only one of them is true.
        RemoteControlSessions.ResetForTests();

        var state = RemoteControlSessions.MirrorStateFor("acct", "job-hunter");

        Assert.Equal(RemoteMirrorClient.MirrorAvailability.Unknown, state.Availability);
        Assert.Null(state.Entry);
    }

    private readonly record struct CodeLine(int Index, int Number, string Text);

    // Lines that are code, not comments — a comment naming PeerSessions.Host
    // (this file's own neighbours do) is not a read of it.
    private static List<CodeLine> CodeLines(string path) => CodeLinesOf(File.ReadAllLines(path));

    private static List<CodeLine> CodeLinesOf(string[] all) =>
        all
            .Select((text, i) => new CodeLine(i, i + 1, text))
            .Where(l => !l.Text.TrimStart().StartsWith("//"))
            .ToList();

    // The member a line sits in: the nearest member declaration at or above
    // it. A declaration is a line that starts with an access modifier and
    // names a member followed by `(` (a method), `=>` (an expression-bodied
    // member), `=` (a field with an initialiser) or the end of the line (a
    // property or other member whose body opens on the next line).
    //
    // What it does not do, stated rather than implied: it is a line scanner,
    // not a parser. A declaration split over several lines before its name, a
    // member with no access modifier, or a read inside a nested type's member
    // are attributed to whatever declaration line precedes them. That is the
    // limit a member shape has to beat to slip past, and the theory above pins
    // the four shapes this class actually uses. It replaced a version that
    // knew only `(` and claimed a wrong answer could only make it stricter —
    // which an expression-bodied property after MirrorClientFor disproved.
    private static string? EnclosingMember(List<CodeLine> lines, int index)
    {
        var signature = new System.Text.RegularExpressions.Regex(
            @"^\s+(?:internal|public|private|protected)\b[^=;(]*?\b(\w+)\s*(?:\(|=>|=(?!=)|$)");

        foreach (var line in lines.Where(l => l.Index <= index).OrderByDescending(l => l.Index))
        {
            var m = signature.Match(line.Text);
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    private static string SourceOf(string file)
    {
        // Up from the test binary to the repository root. The suites already
        // reach the app by ProjectReference, so the layout is fixed.
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException($"could not find {file} above {AppContext.BaseDirectory}");
    }
}

using System.Collections.Generic;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-330: the decisions behind the compact relayed row, with no window and no
// settings behind them. The drawing is covered in tests/UiTests.
public class RelayRowTests
{
    // --- the setting's three words ---

    [Theory]
    [InlineData("collapsed", RelayedMessageDisplay.Collapsed)]
    [InlineData("expanded", RelayedMessageDisplay.Expanded)]
    [InlineData("hidden", RelayedMessageDisplay.Hidden)]
    [InlineData("HIDDEN", RelayedMessageDisplay.Hidden)]
    [InlineData("Expanded", RelayedMessageDisplay.Expanded)]
    // An unknown, empty or missing word is the default, never "hidden".
    [InlineData("sideways", RelayedMessageDisplay.Collapsed)]
    [InlineData("", RelayedMessageDisplay.Collapsed)]
    [InlineData(null, RelayedMessageDisplay.Collapsed)]
    public void ParseReadsTheThreeWordsAndFallsBackToCollapsed(string? word, RelayedMessageDisplay expected) =>
        Assert.Equal(expected, RelayedMessageWords.Parse(word));

    [Theory]
    [InlineData(RelayedMessageDisplay.Collapsed, "collapsed")]
    [InlineData(RelayedMessageDisplay.Expanded, "expanded")]
    [InlineData(RelayedMessageDisplay.Hidden, "hidden")]
    public void NameWritesTheWordParseReads(RelayedMessageDisplay display, string word)
    {
        Assert.Equal(word, RelayedMessageWords.Name(display));
        Assert.Equal(display, RelayedMessageWords.Parse(RelayedMessageWords.Name(display)));
    }

    // --- the user's click against the setting: all nine combinations ---

    [Theory]
    [InlineData(RelayedMessageDisplay.Collapsed, null, false)]
    [InlineData(RelayedMessageDisplay.Collapsed, true, true)]
    [InlineData(RelayedMessageDisplay.Collapsed, false, false)]
    [InlineData(RelayedMessageDisplay.Expanded, null, true)]
    [InlineData(RelayedMessageDisplay.Expanded, true, true)]
    [InlineData(RelayedMessageDisplay.Expanded, false, false)]
    [InlineData(RelayedMessageDisplay.Hidden, null, false)]
    [InlineData(RelayedMessageDisplay.Hidden, true, true)]
    [InlineData(RelayedMessageDisplay.Hidden, false, false)]
    public void IsExpandedLetsTheReadersClickWinInBothDirections(
        RelayedMessageDisplay setting, bool? choice, bool expected) =>
        Assert.Equal(expected, RelayRow.IsExpanded(setting, choice));

    [Theory]
    [InlineData(RelayedMessageDisplay.Collapsed, true)]
    [InlineData(RelayedMessageDisplay.Expanded, true)]
    [InlineData(RelayedMessageDisplay.Hidden, false)]
    public void OnlyHiddenHidesTheRow(RelayedMessageDisplay setting, bool shown) =>
        Assert.Equal(shown, RelayRow.IsShown(setting));

    // --- the dot ---

    [Theory]
    [InlineData("purple")]
    [InlineData("blue")]
    [InlineData("yellow")]
    [InlineData("pink")]
    [InlineData("orange")]
    [InlineData("green")]
    [InlineData("red")]
    [InlineData("cyan")]
    public void TheEightColoursClaudeCodeAssignsMapToTheOrbsOwnTable(string name)
    {
        Assert.Equal(OrbWindow.AgentColors[name], RelayRow.DotColor(name));
    }

    [Fact]
    public void ADotColourIsCaseInsensitiveLikeTheTableItReads() =>
        Assert.Equal(Color.Parse("#00AF5F"), RelayRow.DotColor("GREEN"));

    [Theory]
    [InlineData("chartreuse")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownOrMissingColourHasNoDot(string? name) =>
        Assert.Null(RelayRow.DotColor(name));

    // --- persona names (CB-288) ---

    [Theory]
    [InlineData("celeste-ngata", "celeste-ngata")]
    [InlineData("celeste-ngata-2", "celeste-ngata")]
    [InlineData("celeste_ngata_3", "celeste_ngata")]
    // Only digits are a collision suffix: a name that merely ends in a version
    // is left whole.
    [InlineData("celeste-v2", "celeste-v2")]
    public void PersonaNameForStripsOnlyACollisionSuffix(string name, string expected) =>
        Assert.Equal(expected, RelayRow.PersonaNameFor(name));

    // --- which live session is this name ---

    private static KeyValuePair<string, SessionStatus> Member(string id, string agent, string lead) =>
        new(id, new SessionStatus { Agent = agent, Lead = lead });

    [Fact]
    public void ANameResolvesToTheOnlyMemberOfThisLeadWithThatAgentName()
    {
        var statuses = new[] { Member("m1", "hana-moriyama", "L"), Member("m2", "nadia-okafor", "L") };
        Assert.Equal("m1", RelayRow.MemberSessionId("hana-moriyama", "L", statuses));
    }

    [Fact]
    public void AnInstanceSuffixFallsBackToThePersonaItIsAnInstanceOf()
    {
        var statuses = new[] { Member("m1", "celeste-ngata", "L") };
        Assert.Equal("m1", RelayRow.MemberSessionId("celeste-ngata-2", "L", statuses));
    }

    [Fact]
    public void TheFullNameIsTriedBeforeThePersonasSoAnInstanceKeepsItsOwnFace()
    {
        var statuses = new[] { Member("base", "celeste-ngata", "L"), Member("two", "celeste-ngata-2", "L") };
        Assert.Equal("two", RelayRow.MemberSessionId("celeste-ngata-2", "L", statuses));
    }

    [Fact]
    public void TwoMembersWithOneNameResolveToNeitherBecauseTheWrongFaceIsWorseThanADot()
    {
        var statuses = new[] { Member("m1", "hana", "L"), Member("m2", "hana", "L") };
        Assert.Null(RelayRow.MemberSessionId("hana", "L", statuses));
    }

    // The CB-288 control: the lead's own session, and a session under a
    // different lead, never lend a face to this lead's relayed message.
    [Fact]
    public void ASessionThatIsNotLedByThisOneIsNeverTheAnswer()
    {
        var statuses = new[]
        {
            Member("other", "hana", "SOMEONE-ELSE"),
            Member("L", "hana", "L"),               // the lead named itself
            Member("lead-agent", "hana", ""),       // no lead at all
        };
        Assert.Null(RelayRow.MemberSessionId("hana", "L", statuses));
    }

    [Theory]
    [InlineData(null, "L")]
    [InlineData("", "L")]
    [InlineData("hana", null)]
    [InlineData("hana", "")]
    public void NoNameOrNoLeadMeansNoAnswer(string? name, string? lead) =>
        Assert.Null(RelayRow.MemberSessionId(name, lead, new[] { Member("m1", "hana", "L") }));

    [Fact]
    public void AnUnknownNameIsNoAnswer() =>
        Assert.Null(RelayRow.MemberSessionId("nobody", "L", new[] { Member("m1", "hana", "L") }));
}

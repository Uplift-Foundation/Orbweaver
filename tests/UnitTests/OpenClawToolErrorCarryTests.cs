using System;
using System.Collections.Generic;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270: a tool line that failed is tinted, and the flag travels from the
// history parser to the panel through the three sites that copy a HistoryTurn
// into a live ChatTurn. A copy that dropped it would show a failed tool as a
// clean one with no error anywhere, which is the one thing the tint is for.
[Collection("Settings")]
public class OpenClawToolErrorCarryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static HistoryTurn Tool(string text, bool isError) =>
        new(ChatRole.System, text, null, "", T0, null, null, IsError: isError);

    private static OpenClawChatSession Session() =>
        new("openclaw:agent:nova:discord:channel:1", "agent:nova:discord:channel:1", "nova");

    [Fact]
    public void ATurnIsNotAnErrorUnlessSaidSo() =>
        Assert.False(new HistoryTurn(ChatRole.System, "· x", null, "", T0, null, null).IsError);

    [Fact]
    public void SetHistoryCarriesIsError()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Tool("· ok", false), Tool("· bad", true) });

        Assert.False(session.History[0].IsError);
        Assert.True(session.History[1].IsError);
    }

    [Fact]
    public void PrependHistoryCarriesIsError()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Tool("· newer", false) });
        session.PrependHistory(new List<HistoryTurn> { Tool("· bad", true), Tool("· ok", false) });

        Assert.True(session.History[0].IsError);
        Assert.False(session.History[1].IsError);
        Assert.False(session.History[2].IsError);
    }

    [Fact]
    public void AppendNewTailCarriesIsError()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Tool("· one", false) });

        session.AppendNewTail(new List<HistoryTurn> { Tool("· one", false), Tool("· two", true) });

        Assert.Equal(2, session.History.Count);
        Assert.True(session.History[1].IsError);
        Assert.False(session.History[0].IsError);
    }
}

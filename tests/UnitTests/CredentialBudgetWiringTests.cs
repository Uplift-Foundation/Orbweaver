using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// Every credential read a cloud chat session makes goes through the budget that
// session was handed (CB-269).
//
// The invariant is stated as a count rather than per call site: the source
// counts the reads it actually served and the spy budget counts the races it
// was asked to run, and the two must agree. A site that fell back to
// CredentialBudget.Unmeasured would be a read the spy never saw, so the
// numbers would split. `Reads > 0` is asserted alongside so an agreement of
// zero cannot pass.
public partial class ClaudeCloudEventsTests
{
    // Waits for everything the session started to finish: the interrupt, and the
    // live loop and stream a delivered interrupt or an open panel start behind it.
    // Without it the comparison below is taken with a read still in flight, and
    // the spy (counted when the race starts) and the source (counted when the pool
    // runs the read) legitimately disagree for a moment. That was a failure here
    // once in nine soak runs: Expected 1 / Actual 2 (CB-269). Compared after
    // settling, both counts are final.
    private static async Task SettleAsync(ClaudeCloudChatSession chat)
    {
        if (chat.InterruptTask is { } interrupt) await interrupt;
        if (chat.LiveTask is { } live) await live;
        if (chat.StreamTask is { } stream) await stream;
    }

    [Fact]
    public async Task ALoadReadsThroughTheSessionsOwnBudget()
    {
        var (budget, calls) = TestBudgets.Spy();
        var creds = new CountingCredentials();
        var chat = Sender(new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello") }))), creds: creds, budget: budget);

        Assert.True(await chat.LoadAsync(CancellationToken.None));
        await SettleAsync(chat);

        Assert.True(creds.Reads > 0);
        Assert.Equal(creds.Reads, calls());
    }

    // The send, and the live loop it starts, which reads on every tick.
    [Fact]
    public async Task ASendAndItsLiveLoopReadThroughTheSessionsOwnBudget()
    {
        var (budget, calls) = TestBudgets.Spy();
        var creds = new CountingCredentials();
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), creds: creds, budget: budget);

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("hello"));
        await SettleAsync(chat);

        Assert.True(creds.Reads >= 2, "expected the send's read and at least one live read");
        Assert.Equal(creds.Reads, calls());
    }

    [Fact]
    public async Task AnInterruptReadsThroughTheSessionsOwnBudget()
    {
        var (budget, calls) = TestBudgets.Spy();
        var creds = new CountingCredentials();
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), creds: creds, session: BusyRow(),
            budget: budget);

        chat.Cancel();
        await SettleAsync(chat);

        Assert.True(creds.Reads > 0);
        Assert.Equal(creds.Reads, calls());
    }

    [Fact]
    public async Task TheStreamReadsThroughTheSessionsOwnBudget()
    {
        var (budget, calls) = TestBudgets.Spy();
        var creds = new CountingCredentials();
        var (chat, _, stream, _) = Streaming(creds: creds, budget: budget);

        chat.PanelOpened();
        await stream.NextOpenAsync();
        await Close(chat);
        await SettleAsync(chat);

        Assert.True(creds.Reads > 0);
        Assert.Equal(creds.Reads, calls());
    }
}

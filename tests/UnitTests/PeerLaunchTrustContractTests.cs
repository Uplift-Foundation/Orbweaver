using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397 contract, pushed before the behaviour so QA and the window build
/// against one shape: the codes, the frame type, the result's optional trust
/// question, and the interface's default answer.
/// </summary>
public class PeerLaunchTrustContractTests
{
    [Fact]
    public void The_codes_and_frame_type_are_the_wire_strings_and_stay_out_of_the_phone_contract()
    {
        Assert.Equal("trust_needed", PeerLaunchCodes.TrustNeeded);
        Assert.Equal("trust_cancelled", PeerLaunchCodes.TrustCancelled);
        Assert.Equal("trust_gone", PeerLaunchCodes.TrustGone);
        Assert.Equal("launch-trust", PeerProtocol.LaunchTrust);
        Assert.Equal(1, PeerProtocol.Version);

        foreach (var code in new[] { PeerLaunchCodes.TrustNeeded, PeerLaunchCodes.TrustCancelled, PeerLaunchCodes.TrustGone })
            Assert.DoesNotContain(code, RelayReplies.ErrCodes);
    }

    [Fact]
    public void A_result_carries_no_trust_question_unless_given_one()
    {
        Assert.Null(new PeerLaunchResult(true, PeerLaunchCodes.Launched, "s", null).Trust);
        var trust = new PeerLaunchTrust("tok", "claude", "/w", 1794111792);
        Assert.Equal(trust, new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null, trust).Trust);
    }

    [Fact]
    public async Task A_launcher_that_cannot_ask_answers_no_answer()
    {
        IPeerLauncher launcher = new Bare();
        var r = await launcher.TrustAsync("mini", new PeerLaunchTrust("t", "claude", "/w", 0), true, CancellationToken.None);
        Assert.Equal(new PeerLaunchResult(false, PeerLaunchCodes.NoAnswer, null, null), r);
    }

    private sealed class Bare : IPeerLauncher
    {
        public IReadOnlyList<PeerMachine> Machines() => [];
        public Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct) => Task.FromResult<PeerLaunchInfo?>(null);
        public Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct) => throw new NotSupportedException();
    }
}

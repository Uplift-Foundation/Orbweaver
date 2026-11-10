using Xunit;

namespace Orbweaver.Tests;

/// <summary>CB-314: the words Settings › Paired phones shows, one case per arm.</summary>
public class PairedPhonesTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // The status enum is internal, so the theory takes its name.
    [Theory]
    [InlineData("Connected", null, "Connected to the relay.")]
    [InlineData("Registering", null, "Registering this computer with the relay…")]
    [InlineData("Connecting", null, "Connecting to the relay…")]
    [InlineData("Connecting", "timed out", "Reconnecting to the relay — last attempt: timed out")]
    [InlineData("Off", null, "Not connected to the relay.")]
    public void Relay_line(string state, string? error, string expected) =>
        Assert.Equal(expected, PairedPhonesText.Relay(Enum.Parse<RelayClient.Status>(state), error));

    [Fact]
    public void A_refused_version_says_to_update() =>
        Assert.Contains("Update to pair phones", PairedPhonesText.Relay(RelayClient.Status.Refused, "x"));

    [Fact]
    public void Phone_line_rounds_to_what_a_person_would_say()
    {
        Assert.Equal("Online", PairedPhonesText.Phone(true, Now.AddDays(-9), Now));
        Assert.Equal("Offline", PairedPhonesText.Phone(false, null, Now));
        Assert.Equal("Last seen just now", PairedPhonesText.Phone(false, Now.AddSeconds(-20), Now));
        Assert.Equal("Last seen 7 min ago", PairedPhonesText.Phone(false, Now.AddMinutes(-7.5), Now));
        Assert.Equal("Last seen 3 h ago", PairedPhonesText.Phone(false, Now.AddHours(-3.2), Now));
        Assert.StartsWith("Last seen ", PairedPhonesText.Phone(false, Now.AddDays(-4), Now));
        Assert.DoesNotContain("ago", PairedPhonesText.Phone(false, Now.AddDays(-4), Now));
    }
}

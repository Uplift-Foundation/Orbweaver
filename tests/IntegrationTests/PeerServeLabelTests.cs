using Xunit;

namespace Orbweaver.Tests;

// CB-300: Serve now takes the dirs as a function, evaluated once for the label
// (a label, not a scope). For an install whose old list leads, the label both
// halves of the link carry must be the same as before the union existed.
[Collection("Settings")]
public class PeerServeLabelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb300l-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public PeerServeLabelTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dir);
        OrbweaverSettings.ReloadForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _previous);
        OrbweaverSettings.ReloadForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string LabelFor(Func<IReadOnlyList<string>> dirs)
    {
        using var host = new PeerMirrorHost();
        host.Serve(dirs, () => Array.Empty<(string, SessionStatus)>());

        Assert.Equal(host.Client!.Account, host.Server!.Account);
        return host.Server.Account;
    }

    [Fact]
    public void TheLabelIsTheFirstEntryOfTheOldListForAnRcFirstInstall()
    {
        var union = PeerRosterDirs.For("/home/u",
            new[] { ".claude-board", ".claude" }, new[] { ".claude-loginid" });

        Assert.Equal(".claude-board", LabelFor(() => union));
    }

    // The control: had the profiles led, the label would have moved.
    [Fact]
    public void AProfilesFirstOrderWouldHaveChangedTheLabel()
    {
        Assert.Equal(".claude-loginid", LabelFor(() => new[] { ".claude-loginid", ".claude-board" }));
    }

    [Fact]
    public void AnEmptyListLabelsAsTheDefault()
    {
        Assert.Equal(".claude", LabelFor(() => Array.Empty<string>()));
    }
}

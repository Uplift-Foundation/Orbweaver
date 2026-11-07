using Xunit;

namespace Orbweaver.Tests;

// CB-307: the shadow switch against real marker files in a real folder, read
// with File.Exists exactly as both gates read them in the app — the local
// panel's (LocalCliChatSession) and the mirror's remote typing paths
// (RemoteMirrorServer.LiveSeams → TypingGate.GuardedTypeAsync). Log-only by
// default; the enforce marker opts in; the shadow marker keeps meaning
// log-only and wins over it.
public class ShadowSwitchMarkerFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb307-shadow-" + Guid.NewGuid().ToString("N"));

    public ShadowSwitchMarkerFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "");

    [Fact]
    public void AnEmptySettingsFolderIsLogOnly() =>
        Assert.True(ShadowSwitch.IsOn(_dir, null, File.Exists));

    [Fact]
    public void TheEnforceMarkerOptsIntoEnforcing()
    {
        Touch(ShadowSwitch.EnforceMarkerFileName);
        Assert.False(ShadowSwitch.IsOn(_dir, null, File.Exists));
    }

    [Fact]
    public void AnInstalledShadowMarkerStaysLogOnlyEvenBesideAnEnforceMarker()
    {
        Touch(ShadowSwitch.MarkerFileName);
        Assert.True(ShadowSwitch.IsOn(_dir, null, File.Exists));

        Touch(ShadowSwitch.EnforceMarkerFileName);
        Assert.True(ShadowSwitch.IsOn(_dir, null, File.Exists));
    }

    [Fact]
    public void TheVariableWinsOverEitherFile()
    {
        Touch(ShadowSwitch.MarkerFileName);
        Assert.False(ShadowSwitch.IsOn(_dir, "0", File.Exists));

        File.Delete(Path.Combine(_dir, ShadowSwitch.MarkerFileName));
        Touch(ShadowSwitch.EnforceMarkerFileName);
        Assert.True(ShadowSwitch.IsOn(_dir, "1", File.Exists));
    }
}

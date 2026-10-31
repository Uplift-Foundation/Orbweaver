using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-270: the native-mode handler with a manager running. Like every OpenClaw mode
// handler it writes the setting and then tells the manager to redraw
// (`SessionManager.Instance?.ReapplyArrangement()`), and the other Settings tests
// run with no manager, so only the arm that makes the call is missed there.
// Instance has a private setter because only Start() assigns it, and Start()
// builds a tray icon and a gateway connection, so — as GroupLiveManagerTests
// does — it is set by reflection for the length of one test and put back.
[Collection("Settings")]
public class SettingsWindowNativeLiveTests : IDisposable
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(SessionManager).GetProperty("Instance")!;

    private readonly string _dir = Directory.CreateTempSubdirectory("native-live-").FullName;
    private readonly ClusterMode _mode = OrbweaverSettings.OpenClawNativeMode;

    public void Dispose()
    {
        InstanceProperty.SetValue(null, null);
        OrbweaverSettings.OpenClawNativeMode = _mode;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SettingsWindow NewSettings() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    [AvaloniaTheory]
    [InlineData(ClusterMode.Hidden)]
    [InlineData(ClusterMode.OwnShape)]
    [InlineData(ClusterMode.WithChats)]
    public void ChangingTheModeWithAManagerRunningWritesItAndRedraws(ClusterMode mode)
    {
        InstanceProperty.SetValue(null, new SessionManager(_dir));
        Assert.NotNull(SessionManager.Instance);

        NewSettings().OnOpenClawNativeModeChanged(mode);

        Assert.Equal(mode, OrbweaverSettings.OpenClawNativeMode);
    }
}

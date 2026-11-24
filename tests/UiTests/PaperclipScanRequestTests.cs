using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-421: SessionManager wires ScanNudge to the debounce the status-file watcher
// uses and to ScheduleScan's in-flight guard. The decision is ScanNudgeTests'; this
// is the wiring, driven without a timer firing: what is asserted is whether the
// debounce has been started, and whether a follow-up is queued behind a real scan.
[Collection("Settings")]
public class PaperclipScanRequestTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb421-nudge-" + Guid.NewGuid());

    public PaperclipScanRequestTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private SessionManager Manager() =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { _scratch });

    private static DispatcherTimer Field(SessionManager m, string name) =>
        (DispatcherTimer)typeof(SessionManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    [AvaloniaFact]
    public void ARequest_StartsTheWatchersDebounce_AndABurstIsStillOneTimer()
    {
        var m = Manager();
        var debounce = Field(m, "_debounce");
        Assert.False(debounce.IsEnabled);

        m.RequestScan(); m.RequestScan(); m.RequestScan();

        Assert.True(debounce.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(150), debounce.Interval);
    }

    [AvaloniaFact]
    public async Task ARequestDuringARunningScan_StartsNothingNow_AndQueuesOneFollowUpBehindIt()
    {
        var m = Manager();
        var debounce = Field(m, "_debounce");

        var running = m.ScheduleScan();      // in flight from its first await
        m.RequestScan();
        Assert.False(debounce.IsEnabled);    // not dropped, but not started on top of it either

        await running;
        Assert.True(debounce.IsEnabled);     // exactly the one follow-up
    }

    [AvaloniaFact]
    public async Task TheControl_WithNothingRequested_AScanLeavesNoFollowUp_AndTheTwoSecondTimerIsStillThere()
    {
        var m = Manager();
        await m.ScheduleScan();
        Assert.False(Field(m, "_debounce").IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(2), Field(m, "_pollTimer").Interval);
    }
}

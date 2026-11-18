using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Orbweaver.Tests.TestAppBuilder))]

namespace Orbweaver.Tests;

// Unlike tests/UiTests, this project turns real rendering ON
// (UseHeadlessDrawing = false, plus .UseSkia()) — the whole point of this
// suite is producing an actual bitmap of what a control looks like, which
// the null headless renderer tests/UiTests uses never draws at all.
//
// A separate assembly, a separate AppBuilder, deliberately: switching the
// existing 28-test suite to real rendering would risk reopening the
// Avalonia.Headless font-manager/dispatcher race that suite's tests fought
// hard to stabilize (see tests/UiTests/TestAppBuilder.cs's own comment) —
// under a different, heavier rendering path that hasn't been stress-tested
// against it at all. Keeping this isolated means a screenshot-capture flake
// can only ever affect screenshot capture.
//
// No FontManager warm-up here, unlike tests/UiTests: this project (and the
// app it hosts) is pinned to Avalonia 12.1.1 specifically to reach
// AvaloniaUI/Avalonia#21269, the real upstream fix for the concurrent-
// FontManager-access race that made real-rendering screenshot capture
// unreliable under 11.3.x (never backported there — backported-12.0.x
// only). Confirmed clean across 23 back-to-back fresh runs of all 30 tests
// here (690 test executions, 0 failures) with no warm-up at all — the prior
// 11.3.x mitigations tried here (a throwaway Window, then a direct
// FontManager.Current.SystemFonts touch) are gone because the race they
// were fighting no longer exists at this version.
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Orbweaver.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            // After the platform exists, so ChatPanel's type initialiser (brushes
            // and all) runs against a set-up Avalonia rather than before one.
            .AfterSetup(_ => ChatPanelTestAccess.PinSuiteMachineName());
}

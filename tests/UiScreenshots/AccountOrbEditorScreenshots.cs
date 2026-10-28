using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-287's two editing surfaces through real Skia: the Customize… window, and
// the per-account rows in Settings. Hand-written, one per scenario, because a
// UiTests case does not add its screenshot. No clicks and no picker: the file
// dialog is the one thing a headless run cannot answer, so the icon variants
// import a generated PNG through the same seam the UiTests use.
[Collection("Settings")]
public class AccountOrbEditorScreenshots : IDisposable
{
    private readonly Func<IReadOnlyList<AccountOrbIdentity>> _accounts = SettingsWindow.UsageAccounts;
    private readonly AccountOrbIdentity _work =
        new("/tmp/cb287-shot-work", AccountOrbs.PositionKey("/tmp/cb287-shot-work"), AccountUsageSource.ClaudeCode, "acme-work");
    private readonly AccountOrbIdentity _home =
        new("/tmp/cb287-shot-home", AccountOrbs.PositionKey("/tmp/cb287-shot-home"), AccountUsageSource.ClaudeCode, "personal");

    public AccountOrbEditorScreenshots()
    {
        AccountOrbCustomization.Reset(_work.PositionKey);
        AccountOrbCustomization.Reset(_home.PositionKey);
    }

    public void Dispose()
    {
        SettingsWindow.UsageAccounts = _accounts;
        AccountOrbCustomization.Reset(_work.PositionKey);
        AccountOrbCustomization.Reset(_home.PositionKey);
        OrbweaverSettings.AccountUsageEnabled = false;
    }

    [AvaloniaFact]
    public void TheCustomizeEditor()
    {
        AccountOrbCustomization.SetInitials(_work.PositionKey, "AW");
        AccountOrbCustomization.SetName(_work.PositionKey, "Acme Corp (work)");

        AccountOrbEditorWindow.Open(_work, () => { });
        var window = AccountOrbEditorWindow.Find(_work.PositionKey)!;
        try
        {
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(window, "account-orb-editor.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheSettingsAccountRows()
    {
        AccountOrbCustomization.SetInitials(_work.PositionKey, "AW");
        AccountOrbCustomization.SetName(_work.PositionKey, "Acme Corp (work)");
        SettingsWindow.UsageAccounts = () => new[] { _work, _home };
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.AccountUsageEnabled = true;

        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!;
        var window = (SettingsWindow)ctor.Invoke(null);
        window.Show();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureControl(window.Sections["claude-code"], "settings-account-orb-rows.png");
    }
}

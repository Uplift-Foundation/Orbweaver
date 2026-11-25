using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-466: the "Share an anonymous install count" row in Settings → Updates. In the
// Settings collection because it reads and writes the process-wide settings static.
[Collection("Settings")]
public class InstallPingSettingsRowTests : IDisposable
{
    public void Dispose()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.InstallPingEnabled = true;
    }

    private static SettingsWindow NewWindow()
    {
        OrbweaverSettings.ReloadForTests();
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        return (SettingsWindow)ctor.Invoke(null);
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        if (root is T self) yield return self;
        var children = root switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Decorator decorator when decorator.Child is Control only => new[] { only },
            ContentControl holder when holder.Content is Control only => new[] { only },
            _ => Enumerable.Empty<Control>()
        };
        foreach (var child in children)
        foreach (var found in Descendants<T>(child))
            yield return found;
    }

    // The row whose title is the install-count one: the row control that holds that text.
    private static Control TheRow(IEnumerable<Control> rows) =>
        rows.First(r => Descendants<TextBlock>(r).Any(t => t.Text == "Share an anonymous install count"));

    [AvaloniaFact]
    public void TheRowExistsWithTheDocumentedCopyAndBrandName()
    {
        var rows = NewWindow().UpdateRows();
        var text = string.Join(" | ", TheRow(rows).Let(r => Descendants<TextBlock>(r).Select(t => t.Text)));

        Assert.Contains("Share an anonymous install count", text);
        Assert.Contains($"{Brand.DisplayName} sends a random install ID, its version and your system type", text);
        Assert.Contains("(for example macOS arm64)", text);
        Assert.Contains("Nothing about you, your files or your sessions is sent. With this off, nothing is sent.", text);
    }

    [AvaloniaFact]
    public void TheRowSitsAfterThePrereleaseRowAndBeforeCheckNow()
    {
        var rows = NewWindow().UpdateRows().ToList();
        var titles = rows.Select(r => Descendants<TextBlock>(r).FirstOrDefault()?.Text).ToList();
        var pre = titles.IndexOf("Include prerelease versions");
        var ping = titles.IndexOf("Share an anonymous install count");
        var check = titles.IndexOf("Check now");
        Assert.True(pre >= 0 && pre < ping && ping < check, string.Join(" | ", titles));
    }

    [AvaloniaFact]
    public void TheSwitchReflectsTheSetting()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.InstallPingEnabled = false;
        var off = ToggleOf(TheRow(NewWindow().UpdateRows()));
        Assert.False(off);

        OrbweaverSettings.InstallPingEnabled = true;
        Assert.True(ToggleOf(TheRow(NewWindow().UpdateRows())));
    }

    private static bool ToggleOf(Control row) =>
        Descendants<ToggleSwitch>(row).Select(t => t.IsChecked ?? false).Concat(
        Descendants<CheckBox>(row).Select(c => c.IsChecked ?? false)).Single();

    private static void Flip(Control row, bool to)
    {
        foreach (var t in Descendants<ToggleSwitch>(row)) t.IsChecked = to;
        foreach (var c in Descendants<CheckBox>(row)) c.IsChecked = to;
    }

    [AvaloniaFact]
    public void FlippingTheRealControlOffClearsTheIdAndOnRestoresTheSetting()
    {
        var window = NewWindow();
        OrbweaverSettings.InstallId = "abc";
        OrbweaverSettings.LastInstallPingUtc = DateTimeOffset.UtcNow;
        var row = TheRow(window.UpdateRows());

        Flip(row, false);
        Assert.False(OrbweaverSettings.InstallPingEnabled);
        Assert.Null(OrbweaverSettings.InstallId);
        Assert.Null(OrbweaverSettings.LastInstallPingUtc);

        Flip(row, true);
        Assert.True(OrbweaverSettings.InstallPingEnabled);
    }

    [AvaloniaFact]
    public void TheHandlerWritesTheSettingBothWays()
    {
        var window = NewWindow();
        window.OnInstallPingToggled(false);
        Assert.False(OrbweaverSettings.InstallPingEnabled);
        window.OnInstallPingToggled(true);
        Assert.True(OrbweaverSettings.InstallPingEnabled);
    }

    [AvaloniaFact]
    public void TheAutoCheckRowStillMakesItsOwnPromise()
    {
        var text = string.Join(" | ", NewWindow().UpdateRows().SelectMany(Descendants<TextBlock>).Select(t => t.Text));
        Assert.Contains("with this off nothing is contacted at all", text);
    }
}

internal static class InstallPingTestExtensions
{
    internal static TOut Let<TIn, TOut>(this TIn self, Func<TIn, TOut> f) => f(self);
}

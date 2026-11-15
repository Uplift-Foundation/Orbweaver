using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-281's "Auto-group orbs by" row at the top of the Group outlines card. The
// picker shows the saved value, choosing an item writes it, and the filter box
// finds the row by the words a person would type.
[Collection("Settings")]
public class SettingsAutoGroupRowTests : IDisposable
{
    private readonly AutoGroupBy _saved = OrbweaverSettings.AutoGroupBy;
    private readonly Action _reapply = SettingsWindow.ReapplyAutoGroups;

    public void Dispose()
    {
        OrbweaverSettings.AutoGroupBy = _saved;
        SettingsWindow.ReapplyAutoGroups = _reapply;
    }

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    [AvaloniaFact]
    public void TheRowLeadsTheCardWithItsLabelAndHelp()
    {
        var row = NewWindow().GroupOutlineRows()[0];
        var texts = row.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Auto-group orbs by", texts);
        Assert.Contains(texts, t => t != null && t.Contains("Outline groups"));
        Assert.NotNull(row.GetLogicalDescendants().OfType<ComboBox>().SingleOrDefault());
    }

    [AvaloniaFact]
    public void ThePickerOffersTheFiveChoicesInOrder()
    {
        var combo = (ComboBox)NewWindow().AutoGroupPicker();

        Assert.Equal(new[] { "Off", "Directory", "CLI", "Claude profile", "Account" },
            combo.ItemsSource!.Cast<string>().ToArray());
    }

    [AvaloniaTheory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    public void ThePickerShowsTheSavedValue(int saved, int index)
    {
        OrbweaverSettings.AutoGroupBy = (AutoGroupBy)saved;

        Assert.Equal(index, ((ComboBox)NewWindow().AutoGroupPicker()).SelectedIndex);
    }

    [AvaloniaTheory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(0, 0)]
    public void ChoosingAnItemWritesTheSetting(int index, int expectedValue)
    {
        var expected = (AutoGroupBy)expectedValue;
        OrbweaverSettings.AutoGroupBy = expected == AutoGroupBy.Off ? AutoGroupBy.Cli : AutoGroupBy.Off;
        var combo = (ComboBox)NewWindow().AutoGroupPicker();

        combo.SelectedIndex = index;

        Assert.Equal(expected, OrbweaverSettings.AutoGroupBy);
    }

    [AvaloniaFact]
    public void ClearingTheSelectionChangesNothing()
    {
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Cli;
        var combo = (ComboBox)NewWindow().AutoGroupPicker();

        combo.SelectedIndex = -1;

        Assert.Equal(AutoGroupBy.Cli, OrbweaverSettings.AutoGroupBy);
    }

    [AvaloniaFact]
    public void ReSelectingTheSavedValueIsANoOp()
    {
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Account;

        NewWindow().OnAutoGroupByChanged(AutoGroupBy.Account);

        Assert.Equal(AutoGroupBy.Account, OrbweaverSettings.AutoGroupBy);
    }

    [AvaloniaFact]
    public void ARowBuiltFromTheCardIsFoundByTheWordsAPersonWouldType()
    {
        var row = (SettingsWindow.SettingsRow)NewWindow().GroupOutlineRows()[0];

        foreach (var word in new[] { "group", "directory", "repo", "CLI", "profile", "account" })
            Assert.True(SettingsFilter.Matches(word, row.SearchText), word);
    }

    [AvaloniaFact]
    public void AChangeReachesTheManagerOnceAndAReselectDoesNot()
    {
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Off;
        var calls = 0;
        SettingsWindow.ReapplyAutoGroups = () => calls++;
        var combo = (ComboBox)NewWindow().AutoGroupPicker();
        Assert.Equal(0, calls);

        combo.SelectedIndex = 2;
        Assert.Equal(1, calls);

        NewWindow().OnAutoGroupByChanged(AutoGroupBy.Cli);
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public void TheDefaultHookIsSafeWithNoManager()
    {
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Off;

        NewWindow().OnAutoGroupByChanged(AutoGroupBy.Directory);

        Assert.Equal(AutoGroupBy.Directory, OrbweaverSettings.AutoGroupBy);
    }

    // The default hook's other arm: with a manager registered, it reaches it.
    // Start() is the only thing that sets Instance and it opens the tray, the
    // watcher and the gateway, so the test sets the auto-property's backing
    // field instead and puts it back. The manager sees an empty scratch
    // directory, so nothing changes on screen; the assertion is that the call
    // goes through to a live manager without throwing and leaves the setting alone.
    [AvaloniaFact]
    public void TheDefaultHookReachesALiveManager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb281-hook-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var field = typeof(SessionManager).GetField(
            "<Instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
        var before = field.GetValue(null);
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Cli;
        try
        {
            field.SetValue(null, new SessionManager(dir));

            _reapply();

            Assert.Equal(AutoGroupBy.Cli, OrbweaverSettings.AutoGroupBy);
        }
        finally
        {
            field.SetValue(null, before);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

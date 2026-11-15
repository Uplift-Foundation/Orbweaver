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

    public void Dispose() => OrbweaverSettings.AutoGroupBy = _saved;

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
    [InlineData(AutoGroupBy.Off, 0)]
    [InlineData(AutoGroupBy.Directory, 1)]
    [InlineData(AutoGroupBy.Cli, 2)]
    [InlineData(AutoGroupBy.Profile, 3)]
    [InlineData(AutoGroupBy.Account, 4)]
    public void ThePickerShowsTheSavedValue(AutoGroupBy saved, int index)
    {
        OrbweaverSettings.AutoGroupBy = saved;

        Assert.Equal(index, ((ComboBox)NewWindow().AutoGroupPicker()).SelectedIndex);
    }

    [AvaloniaTheory]
    [InlineData(1, AutoGroupBy.Directory)]
    [InlineData(2, AutoGroupBy.Cli)]
    [InlineData(3, AutoGroupBy.Profile)]
    [InlineData(4, AutoGroupBy.Account)]
    [InlineData(0, AutoGroupBy.Off)]
    public void ChoosingAnItemWritesTheSetting(int index, AutoGroupBy expected)
    {
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
}

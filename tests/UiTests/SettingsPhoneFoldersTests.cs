using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-339's "Folders the phone can browse" card, on a real folder tree under a scratch home so the
// rows, the refusals and the saved settings are all the real thing. The folder dialog is the one
// seam: an OS dialog cannot run headless, so a test answers it.
[Collection("Settings")]
public class SettingsPhoneFoldersTests : IDisposable
{
    private readonly ScopedSettingsDir _settings = new("phone-folders");
    private readonly Func<IBrowseFileSystem> _fs = SettingsWindow.BrowseFs;
    private readonly Func<Control, Task<string?>> _pick = SettingsWindow.PickBrowseFolder;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "cb339-ui-home-" + Guid.NewGuid());
    private readonly string _canonicalHome;

    public SettingsPhoneFoldersTests()
    {
        foreach (var d in new[] { "Source/app", "Notes", "Library/Caches", "Elsewhere" })
            Directory.CreateDirectory(Path.Combine(_home, d));
        _canonicalHome = BrowseRules.Canonical(new BrowseFileSystem(_home, [], []), _home)!;
        // The scratch tree stands in for a real home: its Library is the system folder, as a user's is.
        SettingsWindow.BrowseFs = () => new BrowseFileSystem(_canonicalHome, [], [Path.Combine(_canonicalHome, "Library")]);
    }

    public void Dispose()
    {
        SettingsWindow.BrowseFs = _fs;
        SettingsWindow.PickBrowseFolder = _pick;
        _settings.Dispose();
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine(new[] { _canonicalHome }.Concat(parts).ToArray());

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    private static IEnumerable<T> All<T>(Control[] rows) where T : Control =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<T>().Concat(r is T t ? new[] { t } : []));

    private static string Mode(Control[] rows) => All<TextBlock>(rows).Single(t => t.Name == "BrowseRootsMode").Text!;

    private static IEnumerable<string> Folders(Control[] rows) =>
        All<Button>(rows).Where(b => b.Name?.StartsWith("RemoveBrowseRoot:") == true).Select(b => b.Name![17..]);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string? Refusal(Control[] rows) => All<TextBlock>(rows).SingleOrDefault(t => t.Name == "BrowseRefusal")?.Text;

    [AvaloniaFact]
    public void The_card_is_in_the_paired_phones_group_after_the_pairing_rows()
    {
        var window = NewWindow();
        Assert.True(window.Sections.ContainsKey("paired-phones"));
        var texts = window.Sections["paired-phones"].GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Folders the phone can browse", texts);
        Assert.Contains("Let the phone browse my whole home folder", texts);
        Assert.True(texts.IndexOf("Pair a phone") < texts.IndexOf("Folders the phone can browse"));
    }

    [AvaloniaFact]
    public void Until_it_is_edited_the_list_is_the_automatic_one_from_recent_sessions_and_has_no_reset()
    {
        OrbweaverSettings.SetNewChatRecentFolders([P("Source", "app")]);
        var rows = NewWindow().PhoneFolderRows();

        Assert.Equal("Automatic, from recent sessions", Mode(rows));
        Assert.Equal([P("Source")], Folders(rows));
        Assert.DoesNotContain(All<Button>(rows), b => b.Name == "ResetBrowseRootsButton");
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);                      // looking is not editing
    }

    [AvaloniaFact]
    public void Adding_a_folder_starts_from_what_was_on_screen_then_it_is_your_list_and_reset_goes_back()
    {
        OrbweaverSettings.SetNewChatRecentFolders([P("Source", "app")]);
        var window = NewWindow();

        Assert.Null(window.AddBrowseRoot(P("Notes")));

        var rows = window.PhoneFolderRows();
        Assert.Equal("Your list", Mode(rows));
        Assert.Equal([P("Source"), P("Notes")], Folders(rows));
        Assert.Equal([P("Source"), P("Notes")], OrbweaverSettings.PhoneBrowseRoots);

        Click(All<Button>(rows).Single(b => b.Name == "ResetBrowseRootsButton"));
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);
        Assert.Equal("Automatic, from recent sessions", Mode(window.PhoneFolderRows()));
    }

    [AvaloniaFact]
    public void A_folder_is_stored_in_its_canonical_spelling_however_it_was_typed()
    {
        var window = NewWindow();
        Assert.Null(window.AddBrowseRoot("  " + P("Notes") + Path.DirectorySeparatorChar + "  "));
        Assert.Equal([P("Notes")], OrbweaverSettings.PhoneBrowseRoots);
    }

    [AvaloniaFact]
    public void Each_refusal_says_why_leaves_the_list_alone_and_a_good_folder_beside_it_is_accepted()
    {
        var window = NewWindow();
        OrbweaverSettings.SetPhoneBrowseRoots([P("Source")]);

        Assert.Contains("doesn't exist", window.AddBrowseRoot(P("Nope"))!);
        Assert.Contains("system folder", window.AddBrowseRoot(P("Library", "Caches"))!);
        Assert.Contains("home folder", window.AddBrowseRoot(_canonicalHome)!);
        Assert.Contains("already in the list", window.AddBrowseRoot(P("Source"))!);
        Assert.Equal([P("Source")], OrbweaverSettings.PhoneBrowseRoots);

        var rows = window.PhoneFolderRows();
        Assert.Contains("already in the list", Refusal(rows)!);                // the last one stays on screen

        Assert.Null(window.AddBrowseRoot(P("Elsewhere")));                     // the control
        Assert.Null(Refusal(window.PhoneFolderRows()));                        // and the sentence goes away with it
        Assert.Equal([P("Source"), P("Elsewhere")], OrbweaverSettings.PhoneBrowseRoots);
    }

    [AvaloniaFact]
    public void Removing_a_folder_drops_it_and_an_empty_list_says_the_phone_sees_nothing()
    {
        OrbweaverSettings.SetPhoneBrowseRoots([P("Source"), P("Notes")]);
        var window = NewWindow();

        Click(All<Button>(window.PhoneFolderRows()).Single(b => b.Name == "RemoveBrowseRoot:" + P("Source")));
        Assert.Equal([P("Notes")], OrbweaverSettings.PhoneBrowseRoots);

        Click(All<Button>(window.PhoneFolderRows()).Single(b => b.Name == "RemoveBrowseRoot:" + P("Notes")));
        var rows = window.PhoneFolderRows();
        Assert.Equal(0, OrbweaverSettings.PhoneBrowseRoots!.Count);           // explicit and empty, not automatic
        Assert.Equal("Your list", Mode(rows));
        Assert.Contains(All<TextBlock>(rows), t => t.Name == "BrowseNoRoots");
    }

    [AvaloniaFact]
    public void Removing_a_row_of_the_automatic_list_makes_it_your_list_minus_that_row()
    {
        OrbweaverSettings.SetNewChatRecentFolders([P("Source", "app"), P("Notes", "x")]);
        var window = NewWindow();
        Assert.Equal([P("Source"), P("Notes")], Folders(window.PhoneFolderRows()));

        Click(All<Button>(window.PhoneFolderRows()).Single(b => b.Name == "RemoveBrowseRoot:" + P("Source")));
        Assert.Equal([P("Notes")], OrbweaverSettings.PhoneBrowseRoots);
    }

    [AvaloniaFact]
    public void The_add_button_adds_what_the_folder_dialog_answers_and_a_cancelled_dialog_adds_nothing()
    {
        var window = NewWindow();
        SettingsWindow.PickBrowseFolder = _ => Task.FromResult<string?>(null);
        Click(All<Button>(window.PhoneFolderRows()).Single(b => b.Name == "AddBrowseRootButton"));
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);

        SettingsWindow.PickBrowseFolder = _ => Task.FromResult<string?>(P("Notes"));
        Click(All<Button>(window.PhoneFolderRows()).Single(b => b.Name == "AddBrowseRootButton"));
        Assert.Equal([P("Notes")], OrbweaverSettings.PhoneBrowseRoots);
    }

    [AvaloniaFact]
    public void The_home_switch_shows_the_saved_value_and_writes_it_and_there_is_no_off_switch_for_browsing()
    {
        var rows = NewWindow().PhoneFolderRows();
        var toggle = All<ToggleButton>(rows).Single();
        Assert.False(toggle.IsChecked);
        Assert.False(OrbweaverSettings.PhoneBrowseHome);

        toggle.IsChecked = true;
        Assert.True(OrbweaverSettings.PhoneBrowseHome);
        Assert.True(All<ToggleButton>(NewWindow().PhoneFolderRows()).Single().IsChecked);
        toggle.IsChecked = false;
        Assert.False(OrbweaverSettings.PhoneBrowseHome);

        // CB-339 refinement decision 3: the only switch on the card is the home one.
        Assert.Single(All<ToggleButton>(rows));
    }
}

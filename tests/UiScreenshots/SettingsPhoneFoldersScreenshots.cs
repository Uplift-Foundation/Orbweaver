using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-339's "Folders the phone can browse" card, rendered through real Skia: the automatic list a
// user has not touched, a list they edited, the home switch on beside a refusal, and an empty list.
// The disk is a stand-in where every folder exists and ~/Library is a system folder, so the
// captures show the same paths on every runner instead of a scratch directory's.
[Collection("Settings")]
public class SettingsPhoneFoldersScreenshots : IDisposable
{
    private sealed class StandInDisk : IBrowseFileSystem
    {
        public string Home => "/Users/example";
        public bool IsWindows => false;
        public bool CaseInsensitive => true;
        public IReadOnlyList<string> DeniedExact => ["/", "/Users", "/Volumes"];
        public IReadOnlyList<string> DeniedTrees => ["/System", "/Users/example/Library"];
        public BrowseEntry? Lookup(string parent, string name) => new(name, BrowseKind.Dir);
        public bool DirectoryExists(string path) => !path.Contains("gone");
        public (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap) => ([], false);
        public bool HiddenAttribute(string folder, string name) => false;
        public bool PersonaAt(string folder) => false;
    }

    private readonly Func<IBrowseFileSystem> _fs = SettingsWindow.BrowseFs;

    public SettingsPhoneFoldersScreenshots()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb339-screens-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        SettingsWindow.BrowseFs = () => new StandInDisk();
    }

    public void Dispose() => SettingsWindow.BrowseFs = _fs;

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    private static void Capture(SettingsWindow window, string name)
    {
        window.Show();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureControl(window.Sections["paired-phones"], name);
    }

    [AvaloniaFact]
    public void AutomaticFromRecentSessions()
    {
        OrbweaverSettings.SetNewChatRecentFolders(["/Users/example/Source/orbweaver", "/Users/example/Clients/acme/site"]);
        OrbweaverSettings.PhoneBrowseHome = false;
        Capture(NewWindow(), "settings-phone-folders-automatic.png");
    }

    [AvaloniaFact]
    public void EditedList()
    {
        OrbweaverSettings.SetPhoneBrowseRoots(["/Users/example/Source", "/Users/example/Clients/acme"]);
        OrbweaverSettings.PhoneBrowseHome = false;
        Capture(NewWindow(), "settings-phone-folders-edited.png");
    }

    [AvaloniaFact]
    public void HomeSwitchOnWithARefusalShown()
    {
        OrbweaverSettings.SetPhoneBrowseRoots(["/Users/example/Source"]);
        OrbweaverSettings.PhoneBrowseHome = true;
        var window = NewWindow();
        Assert.NotNull(window.AddBrowseRoot("/Users/example/Library/Caches"));
        Capture(window, "settings-phone-folders-home-on-refusal.png");
    }

    [AvaloniaFact]
    public void EmptyList()
    {
        OrbweaverSettings.SetPhoneBrowseRoots([]);
        OrbweaverSettings.PhoneBrowseHome = false;
        Capture(NewWindow(), "settings-phone-folders-empty.png");
    }
}

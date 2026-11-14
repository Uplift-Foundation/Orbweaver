namespace Orbweaver.Tests;

// Points OrbweaverSettings at a fresh directory for the life of one test, and
// on Dispose puts back exactly what it found: the hides cleared, the variable
// restored, and the model reloaded from the directory every other test uses.
//
// CB-276 needed it because deleting a scratch settings directory is not the
// same as leaving it. The in-memory model outlives the directory, and a test
// that never reloads — most of them — reads whatever the previous one left,
// hides included. HiddenOrbsLeakGuard catches a test that forgets; this is how
// a test doesn't forget.
internal sealed class SettingsDirScope : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-settings-scope-" + Guid.NewGuid());

    public SettingsDirScope()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", Dir);
        OrbweaverSettings.ReloadForTests();
    }

    public void Dispose()
    {
        OrbweaverSettings.RestoreAllHiddenOrbs();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _previous);
        OrbweaverSettings.ReloadForTests();
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// The web as the default, and the thread style, through a real settings file.
//
// Both defaults only matter for someone who has never chosen: a saved shape or a
// saved style must come back exactly as it was written, and an upgrade must not
// quietly move a person off the shape they picked.
[Collection("Settings")]
public class ThreadStyleSettingsRoundTripTests
{
    private static string Stage(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "web-thread-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static void Reload(string dir)
    {
        OrbweaverSettings.FlushPendingSave();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void WithNoFileTheShapeIsTheWebAndTheThreadsAreSilk()
    {
        Stage();

        Assert.Equal("web", OrbweaverSettings.ArrangeShape);
        Assert.Equal("silk", OrbweaverSettings.ThreadStyle);
    }

    [Fact]
    public void ASavedShapeIsNotMovedToTheWeb()
    {
        Stage("{ \"arrangeShape\": \"heart\" }");

        Assert.Equal("heart", OrbweaverSettings.ArrangeShape);
    }

    [Fact]
    public void TheThreadStyleSurvivesARestart()
    {
        var dir = Stage();

        OrbweaverSettings.ThreadStyle = "arrows";
        Reload(dir);

        Assert.Equal("arrows", OrbweaverSettings.ThreadStyle);

        var onDisk = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.Equal("arrows", onDisk["threadStyle"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownSavedStyleReadsAsSilk()
    {
        Stage("{ \"threadStyle\": \"a-style-from-a-later-build\" }");

        Assert.Equal("silk", OrbweaverSettings.ThreadStyle);
    }

    [Fact]
    public void SettingAnUnknownStyleStoresSilk()
    {
        var dir = Stage();

        OrbweaverSettings.ThreadStyle = "nonsense";
        Reload(dir);

        Assert.Equal("silk", OrbweaverSettings.ThreadStyle);
    }
}

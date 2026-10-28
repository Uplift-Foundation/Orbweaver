using Avalonia.Headless.XUnit;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// QA (Nadia): a slider or colour picker raises a change on every pointer move, which
// is why the global outline setters defer their write (SaveSoon). The per-group
// dialog sits behind the same controls; it should not rewrite settings.json on every tick.
[Collection("Settings")]
public class GroupStyleWindowQaTests
{
    [AvaloniaFact]
    public void DraggingAGroupsOpacitySliderDefersTheFileWriteLikeTheGlobalCardDoes()
    {
        using var scope = new ScopedSettingsDir("qa-group-slider");
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#112233", 0.5, "#445566", 0.5));
        var window = new GroupStyleWindow(group.Id, focusName: false);
        var path = Path.Combine(scope.Dir, "settings.json");

        window.StrokeOpacitySlider!.Value = 0.9;

        var onDisk = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var stored = onDisk["orbGroups"]!["groups"]![group.Id]!["style"]!["strokeOpacity"]!.GetValue<double>();
        // Deferred means the file still holds the OLD value until a flush.
        Assert.Equal(0.5, stored, 3);

        OrbweaverSettings.FlushPendingSave();
        onDisk = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(0.9, onDisk["orbGroups"]!["groups"]![group.Id]!["style"]!["strokeOpacity"]!.GetValue<double>(), 3);
    }
}

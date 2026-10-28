using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SkiaSharp;

namespace Orbweaver.Tests;

// Shared by the CB-287 editor tests: an account identity, a real picture on
// disk, and the two ways a text box gets committed.
internal static class AccountOrbEditorTestSupport
{
    internal static AccountOrbIdentity Account(
        string name, AccountUsageSource source = AccountUsageSource.ClaudeCode, string label = "acme-work") =>
        new($"/tmp/cb287-{name}", AccountOrbs.PositionKey($"/tmp/cb287-{name}"), source, label);

    // A solid-colour PNG, generated rather than committed so no fixture can
    // drift from what the test says it is.
    internal static string WritePng(string dir, string name = "icon.png", int size = 64)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var bitmap = new SKBitmap(size, size);
        bitmap.Erase(new SKColor(0x5F, 0xD7, 0x9B));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    internal static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb287-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void PressEnter(TextBox box) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    internal static void LoseFocus(TextBox box) =>
        box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
}

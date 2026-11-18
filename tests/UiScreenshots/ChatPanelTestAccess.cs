using System.Reflection;

namespace Orbweaver.Tests;

// Same seam as tests/UiTests/ChatPanelTestAccess.cs, copied for the same
// reason FakeChatSession is: this project stays isolated from tests/UiTests,
// and a two-line forwarder is cheaper to duplicate than to share.
//
// `Instance` resolves to the transient panel — the one every OpenFor in this
// suite binds, and the one dismiss-on-deactivate still applies to. It was a
// reflected private field until CB-110 turned the singleton into a registry;
// see the UiTests copy for the longer version.
internal static class ChatPanelTestAccess
{
    public static ChatPanel? Instance => ChatPanel.Transient;

    public static ChatPanel? PanelFor(string sessionId) => ChatPanel.PanelFor(sessionId);

    public static IReadOnlyList<ChatPanel> All => ChatPanel.All;

    // What this machine is called, as the panel's header reads it. Same seam as
    // the UiTests copy: ChatPanel asks the host once and caches it, so a capture
    // of a session with no far machine would otherwise print whatever machine
    // ran the suite - a CI runner's name, or a developer's, into a published
    // PNG (CB-374). Process-wide, so every caller is in [Collection("Settings")]
    // and the name is put back on dispose.
    //
    // The name every capture in this suite shows for this machine (CB-374). Pinned
    // once, for the whole process, by TestAppBuilder - see PinSuiteMachineName.
    public const string SuiteMachineName = "fixture-machine";

    // Where a panel with no far machine would otherwise print the host's own name
    // (a CI runner's, or a developer's) into a published PNG. Done once at startup
    // rather than per test: a name pinned in a class could not cover a capture in
    // another, and ScreenshotHelper.Check fails any capture that shows the host's
    // name, so the first one left unpinned is found by the run rather than by a
    // reviewer. The assembly runs its tests serially, so nothing reads the field
    // while a test holds a different value through WithMachineName.
    public static void PinSuiteMachineName() => MachineField.SetValue(null, SuiteMachineName);

    public static IDisposable WithMachineName(string? name)
    {
        var held = MachineField.GetValue(null);
        MachineField.SetValue(null, name);

        return new Restore(() => MachineField.SetValue(null, held));
    }

    private static readonly FieldInfo MachineField =
        typeof(ChatPanel).GetField("_thisMachine", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("ChatPanel", "_thisMachine");

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }
}

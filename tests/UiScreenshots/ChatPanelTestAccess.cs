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

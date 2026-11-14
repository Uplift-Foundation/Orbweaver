using System;

namespace Orbweaver
{
    // CB-276: one session the user asked not to see as an orb, as Settings
    // lists it. Everything but SessionId and HiddenAtUtc is captured at the
    // moment of hiding, because by the time anyone reads the list the session
    // has no orb to ask: the row has to say what it was without one.
    //
    // HiddenAtUtc is DateTime.MinValue when it is not known, which only a
    // hand-edited file produces. A bad timestamp costs the "hidden 2 h ago"
    // text, never the hide itself.
    //
    // Peer is the far machine's ref for a Remote Control session and null for
    // everything else. Pruning reads it: a remote session is only known to
    // have ended when the machine it ran on is answering and no longer lists
    // it, and a `rc:` key does not say which machine that is.
    internal sealed record HiddenOrb(
        string SessionId,
        DateTime HiddenAtUtc,
        string Title,
        string Source,
        string Detail,
        string? Peer = null);
}

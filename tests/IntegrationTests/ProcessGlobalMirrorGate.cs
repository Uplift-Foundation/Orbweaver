namespace Orbweaver.Tests;

// RemoteControlSessions holds the installed mirror client and its roster as process-global state, and
// MirrorEdgeCaseTests clears it in Dispose() while MirrorRoundTripTests (a different collection, so
// running at the same time) is mid-test on it. A session-file test then saw the far side as no longer
// serving files and got "update" instead of the far side's refusal: an existing race, failing on develop
// as often as here (CB-407's CI run on 35ea18097 exposed it). Both classes take this gate for the whole
// of each test, so they cannot overlap, without moving either out of its collection (MirrorRoundTripTests
// belongs with the other tests that set CLAUDE_CONFIG_DIR). Those two are the only classes in
// IntegrationTests that reset the installed mirror or install a client (grep, CB-407).
//
// A semaphore, not a monitor: xUnit may construct and dispose an instance on different threads.
internal sealed class MirrorGate
{
    internal static readonly MirrorGate Shared = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    // True when nobody holds it; for the test of the gate itself, which uses its own instance.
    internal bool IsFree => _gate.CurrentCount == 1;

    internal void Leave() => _gate.Release();

    // For a constructor: takes the gate, runs the rest of the setup, and gives the gate back if that
    // setup throws, because xUnit does not call Dispose on an instance whose constructor threw and the
    // gate would stay held for every test after it.
    internal void Hold(Action setup)
    {
        _gate.Wait();
        try { setup(); }
        catch { _gate.Release(); throw; }
    }
}

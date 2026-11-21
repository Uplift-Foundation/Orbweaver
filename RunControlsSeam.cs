namespace Orbweaver
{
    // CB-404: how a set came out, in the terms both wires answer in. The
    // desktop panel reads only Why; the phone relay and the mirror need the
    // kind as well, because frames-v13 separates "not a choice" (bad_value),
    // "cannot be done from here" (unavailable) and "the CLI said no" (failed).
    internal enum RunSetKind { Sent, BadValue, Unavailable, Failed }

    internal sealed record RunSetResult(RunSetKind Kind, string? Why)
    {
        internal static readonly RunSetResult Sent = new(RunSetKind.Sent, null);
    }

    // What the phone relay and the mirror server ask of the run controls on
    // this desk. RunControlsHost is the real one; the relay and mirror tests
    // drive a fake, and their own seams default to null, which reads as "this
    // desk has no run controls" — no state frame, and unavailable to a set.
    internal interface IRunControls
    {
        // The wire's view of a local session, or null when the session offers
        // no run controls at all (never the case for a local CLI session once
        // the host is wired, which shows disabled controls with a reason).
        RunState? Read(SessionStatus status);

        // Whether a change to this session is being run right now: the busy
        // check frames-v13 makes before bad_value.
        bool IsRunning(SessionStatus status);

        // field is RunFrames.FieldModel or FieldEffort, already shape-checked.
        Task<RunSetResult> SetAsync(SessionStatus status, string field, string value);
    }
}

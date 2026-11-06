namespace Orbweaver
{
    // The shared shapes of the auto-updater (CB-308). Kept in one file of plain
    // records and one interface so the three halves that are built in parallel —
    // the core check/download, the per-OS installers, and the UI — compile
    // against the same contract and none of them has to guess at another's types.
    // No behaviour lives here on purpose: anything with a rule in it belongs in
    // UpdateFeed/UpdateAssets/UpdateChecksums where it can be tested alone.

    /// <summary>One downloadable file attached to a GitHub release.</summary>
    internal sealed record UpdateAsset(string Name, string Url, long Size);

    /// <summary>
    /// A published release, reduced to what the updater needs. <see cref="Version"/>
    /// is the tag with its leading "v" stripped; <see cref="Prerelease"/> is
    /// GitHub's own flag rather than a guess from the version text.
    /// </summary>
    internal sealed record UpdateRelease(
        string Tag,
        string Version,
        bool Prerelease,
        string Body,
        IReadOnlyList<UpdateAsset> Assets);

    internal enum UpdateDecisionKind
    {
        /// <summary>Nothing newer than the running build (or the feed was empty).</summary>
        UpToDate,
        /// <summary>A newer release exists and should be offered.</summary>
        Available,
        /// <summary>A newer release exists but the user chose to skip exactly it.</summary>
        Skipped,
    }

    internal sealed record UpdateDecision(UpdateDecisionKind Kind, UpdateRelease? Release);

    internal enum UpdateStage { Downloading, Verifying, Staging, Installing, Restarting }

    /// <summary>
    /// Progress for the notice window. <see cref="BytesTotal"/> is 0 when the size
    /// is unknown, so the UI shows an indeterminate bar rather than dividing by it.
    /// </summary>
    internal sealed record UpdateProgress(UpdateStage Stage, long BytesDone, long BytesTotal, string? Detail = null);

    internal enum InstallStatus
    {
        /// <summary>The installer took over; this process is about to exit and be replaced.</summary>
        HandedOff,
        /// <summary>Refused before touching anything (not writable, translocated, not admin…). Nothing changed.</summary>
        Refused,
        /// <summary>Tried and failed. <see cref="InstallOutcome.Message"/> says why and what state was left.</summary>
        Failed,
    }

    internal sealed record InstallOutcome(InstallStatus Status, string Message);

    /// <summary>
    /// What the UI calls to apply an update. The core downloads and verifies the
    /// installer asset; each platform implements the swap and the relaunch.
    /// </summary>
    internal interface IUpdateInstaller
    {
        Task<InstallOutcome> InstallAsync(
            UpdateRelease release,
            IProgress<UpdateProgress> progress,
            CancellationToken cancellationToken);
    }
}

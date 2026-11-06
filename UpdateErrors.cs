namespace Orbweaver
{
    internal enum UpdateFailure
    {
        /// <summary>GitHub answered 403/429: the unauthenticated limit. Automatic checks stay silent on this.</summary>
        RateLimited,
        /// <summary>The feed or a download could not be reached, or answered an error status.</summary>
        Network,
        /// <summary>The feed answered with something that is not a release list.</summary>
        Malformed,
        /// <summary>The release carries no installer for this platform.</summary>
        NoInstaller,
        /// <summary>The release carries no SHA256SUMS.txt, so nothing could be verified.</summary>
        NoChecksums,
        /// <summary>SHA256SUMS.txt has no entry for the installer, or is itself unparseable.</summary>
        ChecksumUnavailable,
        /// <summary>The downloaded bytes do not hash to the published value.</summary>
        ChecksumMismatch,
        /// <summary>The disk refused: no room, no permission, or the file vanished mid-write.</summary>
        Storage,
    }

    /// <summary>
    /// The one exception the update core throws. The message is written for the
    /// notice window to show as-is, so it says what happened and what (if
    /// anything) was left behind, never a stack-trace phrase.
    /// </summary>
    internal sealed class UpdateException : Exception
    {
        internal UpdateFailure Kind { get; }

        internal UpdateException(UpdateFailure kind, string message, Exception? inner = null)
            : base(message, inner) => Kind = kind;
    }
}

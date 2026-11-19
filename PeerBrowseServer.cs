namespace Orbweaver
{
    // The answering half of `list-dir-get` (CB-357, reopened): lets a paired
    // desktop walk this machine's folders in New Chat so it can start a session
    // somewhere that is not in the recents.
    //
    // Gate order is the point, and each step is a unit test:
    //   1. the switch ("Allow other desktops to start sessions here"): off
    //      answers peer_launch_off and nothing is parsed or listed;
    //   2. the shape of the request: anything but a bounded path is bad_frame;
    //   3. BrowseRules.Resolve under the same roots, system-folder denials and
    //      Home toggle the phone's list_dir is held to, with a read timeout.
    // A refusal from Resolve passes through by code, so a path outside the
    // roots reads exactly as it does on the phone.
    internal sealed class PeerBrowseServer
    {
        internal sealed record Seams(
            Func<bool> Allowed,
            IBrowseFileSystem Fs,
            Func<BrowseSettings> Settings,
            TimeSpan ReadTimeout,
            Action<string>? Log = null);

        private readonly Seams _seams;

        internal PeerBrowseServer(Seams seams) => _seams = seams;

        internal async Task<PeerDirResult> HandleAsync(System.Text.Json.JsonElement body)
        {
            if (!_seams.Allowed()) return PeerDirResult.Fail(PeerLaunchCodes.PeerLaunchOff);

            if (PeerLaunchWire.ParseListDirRequest(body) is not { } path)
                return PeerDirResult.Fail(RelayReplies.BadFrame);

            BrowseOutcome outcome;
            try
            {
                outcome = await Task.Run(() => BrowseRules.Resolve(path, false, _seams.Settings(), _seams.Fs, markers: false))
                    .WaitAsync(_seams.ReadTimeout).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Which of a timeout and a throw it was, never the path.
                _seams.Log?.Invoke("peer-list-dir-unreadable " + e.GetType().Name);
                return PeerDirResult.Fail(RelayReplies.Unreadable);
            }

            if (outcome.Err is { } code) return PeerDirResult.Fail(code);

            var l = outcome.Listing!;
            var items = l.Items.Take(PeerLaunchWire.MaxDirItems)
                .Select(i => new PeerDirItem(i.Name, i.Path, i.Drive, i.Home)).ToList();

            return new PeerDirResult(true, "ok", new PeerDirListing(l.Path, l.Parent, l.Truncated, items));
        }

        // The real desk: the same disk, settings and read bound the phone's
        // list_dir uses (RelayClient.DefaultBrowseSeams). Excluded as that one
        // is: each lambda is one line into the filesystem or the settings.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static PeerBrowseServer ForThisDesk(Func<bool> allowed)
        {
            var fs = new BrowseFileSystem();
            return new PeerBrowseServer(new Seams(
                allowed, fs, () => BrowseSettingsSource.Current(fs), TimeSpan.FromSeconds(8),
                line => MirrorLog.Say("peer", line)));
        }
    }
}

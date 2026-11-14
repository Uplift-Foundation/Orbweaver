using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace Orbweaver
{
    // The Claude Code accounts on this machine, as the cloud arm sees them.
    //
    // An account is a config root. Which roots exist is ClaudeConfigRoots' answer
    // (the default ~/.claude plus each extra the user listed in settings); this
    // class only turns that list into credential sources and remembers the result,
    // so the poll loop and the chat panels share **the same instances**. That
    // matters on macOS, where a second source per panel would be a second consent
    // surface for the same Keychain item.
    internal static class CloudAccounts
    {
        private static readonly object Gate = new();
        private static IReadOnlyList<CloudAccount>? _current;
        private static CloudReadCoordinator _coordinator = new();
        private static Dictionary<string, CloudAccountSource> _gated = new(StringComparer.Ordinal);

        internal static IReadOnlyList<CloudAccount> Current
        {
            get
            {
                lock (Gate) return EnsureBuilt();
            }
        }

        // The one gate and latch every read shares — the poll loops and the chat
        // panels alike — so at most one consent dialog is ever on screen.
        internal static CloudReadCoordinator Coordinator
        {
            get
            {
                lock (Gate) { EnsureBuilt(); return _coordinator; }
            }
        }

        // Rebuilt on every Restart so a root added in settings is picked up
        // without relaunching, and a declined prompt is forgotten: Retry means
        // ask again.
        internal static IReadOnlyList<CloudAccount> Rebuild()
        {
            lock (Gate) return Install(Build());
        }

        internal static void SetForTests(IReadOnlyList<CloudAccount>? accounts)
        {
            lock (Gate)
            {
                if (accounts is null) _current = null;
                else Install(accounts);
            }
        }

        private static IReadOnlyList<CloudAccount> EnsureBuilt() => _current ?? Install(Build());

        private static IReadOnlyList<CloudAccount> Install(IReadOnlyList<CloudAccount> accounts)
        {
            _coordinator = new CloudReadCoordinator();
            _gated = accounts.ToDictionary(a => a.Root, a => new CloudAccountSource(a, _coordinator),
                StringComparer.Ordinal);
            return _current = accounts;
        }

        // The gated source for one account: what the poll loop and that account's
        // chat panels read through.
        internal static CloudAccountSource GatedFor(CloudAccount account)
        {
            lock (Gate)
            {
                EnsureBuilt();
                return _gated[account.Root];
            }
        }

        // Excluded from coverage: constructs the platform's real stores. What it
        // decides is in ClaudeCliCredentials.SourcesFor, which is covered.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<CloudAccount> Build() =>
            ClaudeCliCredentials.SourcesFor(
                OperatingSystem.IsMacOS(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

        // The credential a session's owner logged in with.
        //
        // **A chat send must use its owner's login, never "whichever is first".**
        // An unknown or missing root falls back to the first account (the default
        // root) rather than to nothing, because a session recorded before this
        // existed has no owner and refusing it would be a regression.
        //
        // Gated: a chat read goes through the same gate and latch as the poll, so
        // opening a panel cannot stack a second dialog on one the poll raised.
        internal static ICloudCredentialSource SourceFor(string? ownerRoot)
        {
            var accounts = Current;
            if (ownerRoot is not null)
            {
                var root = ClaudeCliCredentials.TrimRoot(ownerRoot);
                foreach (var account in accounts)
                {
                    if (string.Equals(account.Root, root, StringComparison.Ordinal)) return GatedFor(account);
                }
            }

            return GatedFor(accounts[0]);
        }
    }

    // The one read gate and the prompt latch, shared by every account and every
    // chat panel.
    //
    // A declined or unanswered prompt latches: while it stands, other accounts
    // read their files only, and say they were not asked. The latch clears when
    // the declining account's stamp moves — the user signed in again, or
    // re-approved us — and clears for that account alone.
    internal sealed class CloudReadCoordinator
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (Func<string?> Current, string? At)> _declined =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal bool KeychainSkippedFor()
        {
            lock (_lock)
            {
                foreach (var key in _declined.Keys.ToList())
                {
                    var (current, at) = _declined[key];
                    if (!string.Equals(current(), at, StringComparison.Ordinal))
                    {
                        _declined.Remove(key);
                    }
                }

                // Any decline, the account's own included: a prompt someone just
                // refused is not asked again — not by the poll, not by a chat panel
                // — until that account's login changes or Retry rebuilds this.
                return _declined.Count > 0;
            }
        }

        internal void Record(string root, bool filesOnly, CredentialRead read, Func<string?> stamp)
        {
            lock (_lock)
            {
                if (filesOnly) _skipped.Add(root); else _skipped.Remove(root);
                if (read.Outcome is CredentialOutcome.Denied or CredentialOutcome.NoAnswer)
                {
                    _declined[root] = (stamp, stamp());
                }
            }
        }

        // Whether the account's last read was files-only because of a latch.
        internal bool WasSkipped(string root)
        {
            lock (_lock) return _skipped.Contains(root);
        }
    }

    // One account as a gated source. Stamp() folds the latch in, so an account
    // parked as "not asked" un-parks by itself when the latch clears: its stamp
    // changes, and the arm's Halted gate opens.
    internal sealed class CloudAccountSource : IGatedCredentialSource
    {
        private readonly CloudAccount _account;
        private readonly CloudReadCoordinator _coordinator;
        private bool _filesOnly;

        internal CloudAccount Account => _account;

        internal CloudAccountSource(CloudAccount account, CloudReadCoordinator coordinator)
        {
            _account = account;
            _coordinator = coordinator;
        }

        public SemaphoreSlim Gate => _coordinator.Gate;

        public string? Stamp()
        {
            var stamp = _account.Source.Stamp();
            return _coordinator.KeychainSkippedFor() ? stamp + "|files-only" : stamp;
        }

        public ICloudCredentialSource Choose()
        {
            _filesOnly = _coordinator.KeychainSkippedFor();
            return _filesOnly ? _account.FileSource : _account.Source;
        }

        public void Report(CredentialRead read) =>
            _coordinator.Record(_account.Root, _filesOnly, read, _account.Source.Stamp);

        // Not reached through ReadWithinAsync, which reads what Choose returned;
        // present because the interface requires it, and correct if called.
        public CredentialRead Read() => Choose().Read();
    }

    // What every account's poll loop has said, folded into the one thing the rest
    // of the app reads: a merged, deduplicated session list and a status text.
    //
    // A class rather than statics for the reason ArmState is a record: every
    // decision here is testable by constructing one. The loops that feed it are
    // excluded from coverage and are as thin as they can be made.
    internal sealed class CloudAccountBoard
    {
        // Authoritative: this account's last tick produced a listing that reached
        // the end of its roster. False before the first tick, after a failed or
        // halted one, and after a walk the page cap cut short (CB-276).
        private sealed record Slot(
            IReadOnlyList<ClaudeCloudSessions.Session> Sessions, string Status, bool Authoritative = false);

        private readonly object _gate = new();
        private readonly IReadOnlyList<CloudAccount> _accounts;
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
        private IReadOnlyList<ClaudeCloudSessions.Session> _merged =
            Array.Empty<ClaudeCloudSessions.Session>();
        private DateTime _holdUntilUtc;

        internal const string NotAsked = "not asked — a Keychain prompt was declined";

        private readonly CloudReadCoordinator _coordinator;

        internal CloudAccountBoard(IReadOnlyList<CloudAccount> accounts, CloudReadCoordinator? coordinator = null)
        {
            _accounts = accounts;
            _coordinator = coordinator ?? new CloudReadCoordinator();
            foreach (var account in accounts)
            {
                _slots[account.Root] = new Slot(Array.Empty<ClaudeCloudSessions.Session>(), "checking…");
            }
        }

        internal IReadOnlyList<CloudAccount> Accounts => _accounts;

        internal IReadOnlyList<ClaudeCloudSessions.Session> Merged
        {
            get { lock (_gate) return _merged; }
        }

        internal string StatusText
        {
            get
            {
                lock (_gate)
                {
                    return ClaudeCloudRoster.DescribeAccounts(
                        _accounts.Select(a => (a.Label, _slots[a.Root].Status)).ToList(),
                        _merged.Count);
                }
            }
        }

        // How long every account should wait before its next request, because one
        // of them was rate limited. Nobody has measured whether the limit is per
        // token or per address; if it is per address, N accounts polling on
        // their own schedules would keep re-tripping it, so a 429 on one backs off
        // all of them. A placeholder for a measurement, and named as one.
        internal TimeSpan HoldRemaining(DateTime nowUtc)
        {
            lock (_gate) return _holdUntilUtc > nowUtc ? _holdUntilUtc - nowUtc : TimeSpan.Zero;
        }

        // Fold one account's tick in. Returns the merged snapshot to publish.
        //
        // A null Snapshot keeps that account's slot: a failed page-four fetch on
        // one account must not empty its orbs. A Halted account arrives with an
        // empty snapshot and clears only its own slot.
        internal IReadOnlyList<ClaudeCloudSessions.Session> Apply(
            string root, ClaudeCloudSessions.StepResult step, DateTime nowUtc)
        {
            lock (_gate)
            {
                var status = _coordinator.WasSkipped(root) && step.Next.Halted ? NotAsked : step.Status;
                var previous = _slots[root];

                var sessions = previous.Sessions;
                if (step.Snapshot is { } snapshot)
                {
                    sessions = snapshot.Select(s => s with { OwnerRoot = root }).ToList();
                }

                // A tick that published nothing (a failed fetch) leaves the old
                // sessions on screen on purpose, and says nothing about whether they
                // are still the roster: not authoritative until a tick that did.
                var authoritative = step.Snapshot is not null && !step.Next.Halted && step.Next.Complete;
                _slots[root] = new Slot(sessions, status, authoritative);

                if (step.RateLimited) _holdUntilUtc = nowUtc + step.Wait;

                _merged = ClaudeCloudRoster.MergeAccounts(
                    _accounts.Select(a => _slots[a.Root].Sessions).ToList(), _merged);
                return _merged;
            }
        }

        // An unexpected exception in one account's loop: say so on that account's
        // line and touch nothing else.
        internal void ApplyError(string root, string message)
        {
            lock (_gate) _slots[root] = _slots[root] with { Status = message, Authoritative = false };
        }

        // CB-276: every session id any account lists, or null when this board
        // cannot say. It can say only when every account is authoritative, because
        // a cloud id does not carry which account owns it: one halted account
        // publishes an empty list, which is "no access", and would otherwise make
        // that account's sessions read as ended. No accounts at all is also null.
        internal IReadOnlySet<string>? ListedIds()
        {
            lock (_gate)
            {
                if (_accounts.Count == 0 || _accounts.Any(a => !_slots[a.Root].Authoritative)) return null;

                return _merged.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            }
        }
    }
}

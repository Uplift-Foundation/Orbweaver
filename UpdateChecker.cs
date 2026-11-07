namespace Orbweaver
{
    internal enum UpdateCheckOutcome { UpToDate, Available, Failed }

    internal sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateRelease? Release, string? Error, bool Manual);

    // The slice of OrbweaverSettings the checker reads and writes, so a test needs
    // no settings.json and every arm of the schedule is reachable.
    internal interface IUpdateSettings
    {
        bool AutoCheck { get; }
        bool IncludePrerelease { get; }
        string? SkippedVersion { get; set; }
        DateTimeOffset? LastCheck { get; set; }
    }

    internal sealed class OrbweaverUpdateSettings : IUpdateSettings
    {
        public bool AutoCheck => OrbweaverSettings.AutoCheckForUpdates;
        public bool IncludePrerelease => OrbweaverSettings.IncludePrereleaseUpdates;
        public string? SkippedVersion
        {
            get => OrbweaverSettings.SkippedUpdateVersion;
            set => OrbweaverSettings.SkippedUpdateVersion = value;
        }
        public DateTimeOffset? LastCheck
        {
            get => OrbweaverSettings.LastUpdateCheckUtc;
            set => OrbweaverSettings.LastUpdateCheckUtc = value;
        }
    }

    // A one-shot timer behind a Func so tests can capture the delay and fire it by
    // hand. System.Threading.Timer, not DispatcherTimer: the check must run (and
    // keep its schedule) with no window and no dispatcher.
    internal static class UpdateTimers
    {
        internal static IDisposable Threading(TimeSpan delay, Action action) =>
            new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
    }

    // What one check fetched, kept with its answer so a manual click that joins an
    // automatic check in flight can re-decide from the same releases (skip ignored)
    // instead of inheriting the automatic check's quieter answer. Releases is null
    // when the fetch failed.
    internal sealed record CheckSnapshot(IReadOnlyList<UpdateRelease>? Releases, bool IncludePrerelease, UpdateCheckResult Result);

    // Checks GitHub for a newer release, on a schedule and on demand (CB-308).
    //
    // Reporting rule (the PM's): a MANUAL check always answers — up to date,
    // available, or the failure — because the user asked and silence reads as a
    // dead menu item. An AUTOMATIC check says something only when there is an
    // update, and otherwise only writes the log: a user on a plane should not get
    // a dialog about GitHub being unreachable.
    //
    // Skip applies to automatic checks. A manual check offers the skipped version
    // anyway, because a user who clicked "Check for updates" and was told "you're
    // up to date" while one was waiting would be misled.
    //
    // Events fire on a pool thread; the UI marshals.
    internal sealed class UpdateChecker : IDisposable
    {
        private readonly IReleaseFeed _feed;
        private readonly IUpdateSettings _settings;
        private readonly string _currentVersion;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string> _log;
        private readonly Func<TimeSpan, Action, IDisposable> _schedule;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private IDisposable? _timer;
        private Task<CheckSnapshot>? _inFlight;
        private UpdateRelease? _pending;
        private bool _disposed;

        internal UpdateChecker(
            IReleaseFeed feed,
            IUpdateSettings settings,
            string currentVersion,
            Func<DateTimeOffset> now,
            Action<string> log,
            Func<TimeSpan, Action, IDisposable> schedule)
        {
            _feed = feed;
            _settings = settings;
            _currentVersion = currentVersion;
            _now = now;
            _log = log;
            _schedule = schedule;
        }

        /// <summary>A newer release the user has not skipped, awaiting their decision.</summary>
        internal UpdateRelease? Pending { get { lock (_gate) return _pending; } }

        /// <summary>Raised when <see cref="Pending"/> changes (the tray item reads it).</summary>
        internal event Action? PendingChanged;

        /// <summary>Raised with every manual result and every automatic "available".</summary>
        internal event Action<UpdateCheckResult>? Completed;

        // The version this build says it is, with the test lever applied. The
        // override is logged as a warning because a build that lies about its
        // version is exactly the thing that must never go unnoticed in a log.
        internal static string ResolveCurrentVersion(string? overrideValue, string? informational, out string? warning)
        {
            var real = NeuralSpeech.ResolveVersion(informational);
            warning = null;
            if (string.IsNullOrWhiteSpace(overrideValue)) return real;

            var wanted = overrideValue.Trim();
            if (!UpdateVersion.TryParse(wanted, out _))
            {
                warning = $"WARNING: {BrandEnv.Name(BrandEnv.UpdateCurrentVersion)}='{wanted}' is not a version; ignoring it and using {real}.";
                return real;
            }

            warning = $"WARNING: {BrandEnv.Name(BrandEnv.UpdateCurrentVersion)} is set; this build is pretending to be {wanted} instead of its real {real}.";
            return wanted;
        }

        // The real wiring: GitHub (or the fixture named by ORBWEAVER_UPDATE_FEED_URL),
        // the real settings, the real clock and a real timer. Not started.
        internal static UpdateChecker Create(Action<string> log, Func<string, string?>? env = null)
        {
            env ??= Environment.GetEnvironmentVariable;
            // EngineVersion is the build's informational version already cut at "+",
            // read once from the assembly for the speech-engine URL; one source.
            var current = ResolveCurrentVersion(
                BrandEnv.Get(BrandEnv.UpdateCurrentVersion, env), NeuralSpeech.EngineVersion, out var warning);
            if (warning is not null) log(warning);

            var feedUrl = BrandEnv.Get(BrandEnv.UpdateFeedUrl, env);
            if (feedUrl is not null) log($"WARNING: {BrandEnv.Name(BrandEnv.UpdateFeedUrl)} is set; checking {feedUrl} instead of GitHub.");

            return new UpdateChecker(
                new GitHubReleaseFeed(new HttpUpdateHttp(), UpdateFeed.ResolveBaseUrl(feedUrl)),
                new OrbweaverUpdateSettings(),
                current,
                TimeProvider.System.GetUtcNow,
                log,
                UpdateTimers.Threading);
        }

        internal string CurrentVersion => _currentVersion;

        /// <summary>Begin the automatic schedule (a no-op while auto-check is off).</summary>
        internal void Start() => Reschedule();

        /// <summary>Re-read the auto-check setting; call when the Settings switch changes.</summary>
        internal void Reschedule()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_disposed || !_settings.AutoCheck) return;
                _timer = _schedule(UpdateSchedule.DelayUntilNextCheck(_settings.LastCheck, _now()), OnTimer);
            }
        }

        private void OnTimer() => _ = RunAutomaticAsync();

        internal async Task RunAutomaticAsync()
        {
            if (!_settings.AutoCheck) return;

            UpdateCheckResult? result = null;
            try
            {
                result = (await CheckAsync(manual: false).ConfigureAwait(false)).Result;
            }
            finally
            {
                // However the check ended, the next one is armed: a schedule that
                // depends on every line above behaving is one surprise from silence.
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _timer?.Dispose();
                        _timer = _schedule(
                            result is { Outcome: UpdateCheckOutcome.Available or UpdateCheckOutcome.UpToDate }
                                ? UpdateSchedule.Interval
                                : UpdateSchedule.FailureRetry,
                            OnTimer);
                    }
                }
            }
        }

        /// <summary>"Check for updates…". Always reports; coalesces with a check already running.</summary>
        //
        // If it joins an automatic check already running, it does not inherit that
        // check's answer: automatic checks honour the skip and stay quiet about up
        // to date and failures, and a manual click must not be told "up to date"
        // while a skipped release exists. It re-runs Pick on the releases that
        // check already fetched, ignoring the skip. No second request.
        internal async Task<UpdateCheckResult> CheckNowAsync()
        {
            var snap = await CheckAsync(manual: true).ConfigureAwait(false);
            if (snap.Result.Manual) return snap.Result;

            UpdateCheckResult result;
            if (snap.Releases is null)
                result = snap.Result with { Manual = true };
            else
            {
                var decision = UpdateFeed.Pick(snap.Releases, _currentVersion, snap.IncludePrerelease, null);
                result = decision.Kind == UpdateDecisionKind.Available
                    ? new UpdateCheckResult(UpdateCheckOutcome.Available, decision.Release, null, true)
                    : new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null, true);
                SetPending(result.Release);
            }

            // The automatic check already announced an Available; don't repeat it.
            if (!(snap.Result.Outcome == UpdateCheckOutcome.Available && result.Outcome == UpdateCheckOutcome.Available))
                RaiseCompleted(result);
            return result;
        }

        private Task<CheckSnapshot> CheckAsync(bool manual)
        {
            lock (_gate)
            {
                // A second click (or the timer landing during a manual check)
                // joins the running one rather than doubling the request.
                if (_inFlight is { IsCompleted: false } running) return running;
                return _inFlight = RunAsync(manual);
            }
        }

        private async Task<CheckSnapshot> RunAsync(bool manual)
        {
            UpdateCheckResult result;
            IReadOnlyList<UpdateRelease>? fetched = null;
            var includePre = false;
            try
            {
                includePre = _settings.IncludePrerelease;
                var releases = await _feed.FetchAsync(includePre, _lifetime.Token).ConfigureAwait(false);
                fetched = releases;
                var decision = UpdateFeed.Pick(releases, _currentVersion, includePre, _settings.SkippedVersion);
                _settings.LastCheck = _now();

                result = decision.Kind switch
                {
                    UpdateDecisionKind.Available => new UpdateCheckResult(UpdateCheckOutcome.Available, decision.Release, null, manual),
                    UpdateDecisionKind.Skipped when manual => new UpdateCheckResult(UpdateCheckOutcome.Available, decision.Release, null, manual),
                    _ => new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null, manual),
                };
                if (decision.Kind == UpdateDecisionKind.Skipped && !manual)
                    _log($"update check: {decision.Release!.Version} is available but skipped by the user; staying quiet.");
            }
            catch (Exception ex)
            {
                _log($"update check failed: {ex.Message}");
                result = new UpdateCheckResult(UpdateCheckOutcome.Failed, null, ex.Message, manual);
            }

            SetPending(result.Outcome == UpdateCheckOutcome.Failed ? Pending : result.Release);
            if (manual || result.Outcome == UpdateCheckOutcome.Available) RaiseCompleted(result);
            return new CheckSnapshot(fetched, includePre, result);
        }

        // Each subscriber in its own try/catch, so one bad handler cannot starve the
        // ones after it in the invocation list.
        private void RaiseCompleted(UpdateCheckResult result)
        {
            foreach (var handler in Completed?.GetInvocationList() ?? [])
                Guard("Completed", () => ((Action<UpdateCheckResult>)handler)(result));
        }

        // Subscribers are UI code on someone else's schedule. One that throws must
        // not unwind into RunAutomaticAsync, which would skip its reschedule and
        // end the automatic checks silently until restart (the fire-and-forget
        // caller swallows the exception), nor surface in a manual caller's await.
        private void Guard(string what, Action raise)
        {
            try { raise(); }
            catch (Exception ex) { _log($"update {what} subscriber threw: {ex.Message}"); }
        }

        private void SetPending(UpdateRelease? next)
        {
            bool changed;
            lock (_gate)
            {
                changed = _pending?.Tag != next?.Tag;
                _pending = next;
            }
            if (!changed) return;
            foreach (var handler in PendingChanged?.GetInvocationList() ?? [])
                Guard("PendingChanged", () => ((Action)handler)());
        }

        /// <summary>"Skip this version": quiet until a different release is published.</summary>
        internal void Skip(UpdateRelease release)
        {
            _settings.SkippedVersion = release.Version;
            if (Pending?.Tag == release.Tag) SetPending(null);
        }

        internal void ClearSkipped() => _settings.SkippedVersion = null;

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}

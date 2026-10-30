namespace Orbweaver
{
    // Every environment variable the app, its installers and its tests use to
    // redirect something, read in one place (CB-256).
    //
    // They were CLAUDE_BUDDY_<name> until the rename and are ORBWEAVER_<name>
    // now. The old spelling still works: a developer's shell profile, a CI
    // job somebody wrote by hand or a launchd plist can carry the old name for
    // years, and a test seam that silently stops redirecting does not fail —
    // it writes into the real settings.json, the real log directory, the real
    // bundle cache. So the new name wins and the old one is the fallback,
    // here and in the copies the shell installers carry (their brand_env
    // function), and nowhere else.
    //
    // One helper rather than nine copies of a two-name read, so the fallback
    // can be deleted by one grep for LegacyPrefix: nothing outside this file
    // spells the old prefix in C#, and LogDirSingleReadSiteTests holds every
    // read of either spelling to this file.
    //
    // An empty value counts as unset on both spellings, matching every read
    // this replaced (`is { Length: > 0 }`) and CrashLogFormatTests' case that
    // sets "" to mean "not redirected". Without that an exported-but-empty
    // ORBWEAVER_SETTINGS_DIR would shadow a real legacy value with nothing.
    internal static class BrandEnv
    {
        internal const string Prefix = "ORBWEAVER_";
        internal const string LegacyPrefix = "CLAUDE_BUDDY_";

        // The suffixes, one per variable. The first eight are read by the app,
        // the two *Tests ones by the test suites' opt-in gates, and the
        // shell-script ones (install-hooks.sh, stop-installed-buddy.sh, the team
        // resolver) only by those scripts, listed here so BrandEnvTests can hold
        // the whole set to one list.
        internal const string SettingsDir = "SETTINGS_DIR";
        internal const string LogDir = "LOG_DIR";
        internal const string StatusRoot = "STATUS_ROOT";
        internal const string BundleRoot = "BUNDLE_ROOT";
        internal const string ProfileRoot = "PROFILE_ROOT";
        internal const string NoCredentialStore = "NO_CREDENTIAL_STORE";
        internal const string PersonaWalkCeiling = "PERSONA_WALK_CEILING";
        internal const string MirrorLog = "MIRROR_LOG";
        internal const string LiveBridgeTests = "LIVE_BRIDGE_TESTS";
        internal const string LaunchTests = "LAUNCH_TESTS";
        internal const string LiveDaemonTests = "LIVE_DAEMON_TESTS";
        internal const string KeepAliveDryRun = "KEEPALIVE_DRY_RUN";
        internal const string LaunchAgentsDir = "LAUNCHAGENTS_DIR";
        internal const string KeepAliveAppCandidates = "KEEPALIVE_APP_CANDIDATES";
        internal const string StopGraceSeconds = "STOP_GRACE_SECONDS";

        // CB-291. PERSONAS is read by the app (PersonaPool) and by the team
        // resolver script: the folder holding a user's shared profiles/, roles/
        // and teams/. The other two are the resolver script's own test seams:
        // where its built-in roles are, and which instance names count as live
        // (so a test never depends on what happens to be running). No older
        // spelling of any of them ever existed; they are listed so the one
        // list is complete, and so BrandEnv.Get gives them the same precedence
        // as the rest.
        internal const string Personas = "PERSONAS";
        internal const string TeamBuiltinRoles = "BUILTIN_ROLES";
        internal const string TeamLiveNames = "LIVE_NAMES";
        internal const string TeamWindowsUsers = "WINDOWS_USERS";

        // CB-270 Stage 6. The probe's own switches (tools/openclaw-probe): the
        // scopes and caps it asks for in place of the app's, and the opt-in that
        // lets its `approvals resolve` mode answer a real approval. No older
        // spelling ever existed; they are here for the one list and the same
        // precedence. ALLOW_RESOLVE is read as exactly "1" and nothing else.
        internal const string ProbeScopes = "PROBE_SCOPES";
        internal const string ProbeCaps = "PROBE_CAPS";
        internal const string ProbeAllowResolve = "PROBE_ALLOW_RESOLVE";

        internal static readonly IReadOnlyList<string> All =
        [
            SettingsDir, LogDir, StatusRoot, BundleRoot, ProfileRoot,
            NoCredentialStore, PersonaWalkCeiling, MirrorLog,
            LiveBridgeTests, LaunchTests, LiveDaemonTests,
            KeepAliveDryRun, LaunchAgentsDir, KeepAliveAppCandidates, StopGraceSeconds,
            Personas, TeamBuiltinRoles, TeamLiveNames, TeamWindowsUsers,
            ProbeScopes, ProbeCaps, ProbeAllowResolve,
        ];

        // Pure: the environment is a parameter, so the precedence is a test
        // with no process state behind it, and so the two callers that already
        // took an injected reader (DataDirMigration, LegacyHookCleanup) keep
        // taking one.
        internal static string? Get(string suffix, Func<string, string?> env) =>
            NonEmpty(env(Prefix + suffix)) ?? NonEmpty(env(LegacyPrefix + suffix));

        internal static string? Get(string suffix) => Get(suffix, Environment.GetEnvironmentVariable);

        // The spelling to set. Tests and the app's own subprocess launches use
        // this rather than a literal, so a setter cannot drift onto the old name.
        internal static string Name(string suffix) => Prefix + suffix;

        private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}

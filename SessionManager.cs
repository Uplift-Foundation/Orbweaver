using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Threading;

namespace Orbweaver
{
    public class SessionStatus
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "idle";

        [JsonPropertyName("cwd")]
        public string Cwd { get; set; } = "";

        // What Claude Code calls this chat (its own generated title), empty
        // until a session has been going long enough to be named. Everything
        // user-facing prefers it and falls back to the cwd's folder name.
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        // The session's /color, as a name ("green"). Empty when it hasn't been
        // given one. Drives the orb's border and letter — never its fill,
        // which stays reserved for state. See OrbWindow.ApplyAccent.
        [JsonPropertyName("color")]
        public string Color { get; set; } = "";

        // Set only on an agent-team member: the session id of the team lead
        // this session answers to. Empty for a lead and for any session that
        // isn't in a team, so a non-empty value is also the app's only "this
        // is a team member" signal — see OrbWindow.SetTeamRole and TeamLinks.
        //
        // Filled in by the app during the scan, from the member process's own
        // command line (AgentTeam.LeadOf) — the hooks know nothing about teams
        // and write no such field. It lives here anyway because everything that
        // consumes a session's state consumes it through this object.
        //
        // Nothing guarantees the lead is on screen: it can have ended, or been
        // filtered out, and the member outlives it either way. So this is a
        // hint, not a reference — everything that follows it checks.
        [JsonIgnore]
        public string Lead { get; set; } = "";

        // What this agent is called inside its team — MenuUX, HitReactSpec.
        // Empty for everything that isn't a team member, and filled in beside
        // Lead from the same read of the process. Everything user-facing
        // prefers it over the title, because a team's members all inherit the
        // team session's title and would otherwise be indistinguishable.
        [JsonIgnore]
        public string Agent { get; set; } = "";

        // The colour Claude Code assigned this team member, kept beside Color
        // rather than folded into it so a peer can be told both (CB-223): the
        // session's own /color and the team's assignment are different facts,
        // and the near machine applies the same precedence the local scan
        // does. Empty for everything that isn't a team member.
        [JsonIgnore]
        public string AgentColor { get; set; } = "";

        // Which CLI wrote this file, in its own words: "codex", or absent for
        // Claude Code.
        //
        // Serialized, unlike Lead and Agent and Kind, because unlike them the
        // hook is what knows the answer. That distinction is the whole of it:
        // ResetSessionToIdle reads a status file, changes the state and writes
        // the object back over it, so a field the *app* derives would appear in
        // a hook-owned file and vanish again on the next write — while a field
        // the *hook* owns has to survive exactly that round trip or a reset
        // turns a Codex session into a Claude Code one.
        //
        // Absent means Claude Code rather than unknown, so every status file
        // already on disk and every hook older than this reads correctly with
        // no migration.
        //
        // Not to be confused with Agent three fields up, which is what an
        // agent-team member is called. Both words are right and neither is
        // available for the other.
        [JsonPropertyName("cli")]
        public string Cli { get; set; } = "";

        // How Claude Code was started, in its own words: CLAUDE_CODE_ENTRYPOINT,
        // which the hook copies through for Claude Code sessions only. "cli"
        // for a terminal, "sdk-cli" for `claude -p` and other SDK callers,
        // "claude-desktop" for a session the Claude Desktop app hosts.
        //
        // Serialized for the same reason Cli directly above is: the hook is
        // what knows it, and ResetSessionToIdle writes this object back over
        // the hook's file, so a hook-owned fact has to survive that round trip.
        // Absent reads as "", which every file older than CB-265 does.
        [JsonPropertyName("entrypoint")]
        public string Entrypoint { get; set; } = "";

        public const string DesktopEntrypoint = "claude-desktop";

        // A Claude Code session the Claude Desktop app started and owns
        // (CB-265). It has no terminal by nature, not because it is headless:
        // clicking its orb brings the hosting app forward, and its stdin is
        // the app's, so the chat panel reads it but cannot type into it.
        // Claude Code only — a Codex process spawned from a Desktop session's
        // Bash tool inherits the variable, and is not hosted by anything.
        [JsonIgnore]
        public bool IsDesktopHosted =>
            Source == SessionSource.ClaudeCode
            && string.Equals(Entrypoint, DesktopEntrypoint, StringComparison.OrdinalIgnoreCase);

        // Claude Desktop's own id for the chat, CLAUDE_CODE_HOST_SESSION_ID
        // ("local_<uuid>"), which names the per-chat record Desktop keeps under
        // its profile's claude-code-sessions folder (CB-266). Hook-owned and
        // serialized, for the round-trip reason Entrypoint gives.
        [JsonPropertyName("host_session_id")]
        public string HostSessionId { get; set; } = "";

        // The claude.ai session a Desktop chat is bridged to by Remote Control,
        // read from that record each scan — so it can appear or vanish while the
        // session runs (`/rc`). App-derived, so never written to the file.
        [JsonIgnore]
        public string BridgeSessionId { get; set; } = "";

        // A Desktop chat the panel can send into through claude.ai. What it
        // sends arrives as a peer message, not as the user's own typing —
        // measured, see CB-266 — and the composer says so.
        [JsonIgnore]
        public bool IsBridgedDesktop => IsDesktopHosted && !string.IsNullOrEmpty(BridgeSessionId);

        // Which kind of thing this session is. Derived from Cli rather than
        // stored, by SourceOf, and never serialized — an enum here because it
        // is entirely internal and a typo should not compile, a string on the
        // wire because that is what a shell script can write.
        //
        // Defaulting to ClaudeCode means an object nobody has run SourceOf over
        // behaves as everything did before this existed.
        [JsonIgnore]
        public SessionSource Source { get; set; } = SessionSource.ClaudeCode;

        // The CLI behind a remote-control session. Source remains RemoteControl
        // so no local terminal or persona path is accidentally enabled.
        [JsonIgnore]
        public string RemoteCli { get; set; } = "";

        // Whether this session is a CLI running in a terminal on this machine.
        //
        // Most of the rules in this file that name ClaudeCode mean this and not
        // that: it has a process, it has a terminal you can be sent to, and it
        // has a transcript file on disk. Codex is all three. The ones that
        // genuinely mean Claude Code — agent teams, background jobs, the
        // projects directory — still say so, and each says why.
        [JsonIgnore]
        public bool IsLocalCli => Source is SessionSource.ClaudeCode or SessionSource.Codex or SessionSource.Grok;

        // What kind of gateway conversation this is. [JsonIgnore] for the same
        // reason Source is: it is derived from the gateway's answer during the
        // scan, and ResetSessionToIdle rewrites a status file from this object.
        [JsonIgnore]
        public SessionKind Kind { get; set; } = SessionKind.Unknown;

        // Whether the gateway's heartbeat is what wakes this session. [JsonIgnore]
        // for the same reason Kind is — derived during the scan, and
        // ResetSessionToIdle rewrites a status file from this object.
        //
        // Separate from Kind because it is a separate question: a heartbeat is
        // how a session gets woken, not what kind of conversation it is. See
        // OpenClawHeartbeat.
        [JsonIgnore]
        public bool Heartbeat { get; set; }

        // A stand-in orb for a channel, invented by this app rather than
        // reported by anything. It has no conversation of its own — it is the
        // thing the agents in that channel point at, so a room reads as one
        // place instead of as eight unrelated orbs that happen to share a badge.
        [JsonIgnore]
        public bool IsRoom { get; set; }

        // Which shape of local session this is — a terminal, a background job,
        // or a team member. Derived during the scan from the daemon's job
        // listing and the Lead above (see SessionPresence.ShapeOf), and
        // [JsonIgnore] for the reason Source and Kind are: it is the app's
        // conclusion rather than the hook's, and ResetSessionToIdle writes this
        // object back over a hook-owned file.
        //
        // Terminal for everything that is not a local CLI session, since the
        // question does not arise for a gateway conversation — and Terminal is
        // also the shape that changes nothing about how an orb is drawn.
        [JsonIgnore]
        public LocalSessionShape Shape { get; set; } = LocalSessionShape.Terminal;

        // What the orb should say about anything being on the other end of this
        // session: present, quiet, holding a question, or finished. Derived
        // beside Shape and [JsonIgnore] for the same reason.
        //
        // Deliberately not folded into State, which is what the *session* is
        // doing and is the hook's to write. This is a third axis — the orb's
        // colour is its identity, its fill is its state, and its opacity and its
        // marks are its presence — and a parked session's state is a truthful
        // "idle" that should keep meaning what it means.
        [JsonIgnore]
        public OrbPresence Presence { get; set; } = OrbPresence.Present;

        // Where the session's terminal lives (macOS hook only; empty on
        // Windows or with an older hook script). See TerminalFocuser.
        [JsonPropertyName("term_program")]
        public string TermProgram { get; set; } = "";

        [JsonPropertyName("term_id")]
        public string TermId { get; set; } = "";

        [JsonPropertyName("tty")]
        public string Tty { get; set; } = "";

        // Set only when the session runs inside tmux (macOS hook). The pane id
        // ("%3") is server-unique and outlives window/session renames; the
        // socket pins which tmux server, and tmux_bin is where the hook found
        // the tmux binary (the app can't rely on PATH — launched from Finder
        // it doesn't have Homebrew's). See TerminalFocuser.
        [JsonPropertyName("tmux_socket")]
        public string TmuxSocket { get; set; } = "";

        [JsonPropertyName("tmux_pane")]
        public string TmuxPane { get; set; } = "";

        [JsonPropertyName("tmux_bin")]
        public string TmuxBin { get; set; } = "";

        // Windows hook only: PID of the terminal process that owns a window.
        [JsonPropertyName("term_pid")]
        public int TermPid { get; set; }

        // The claude process running this session. Recorded by both hooks; 0
        // from a hook older than this field, in which case liveness can't be
        // checked and only the lifetime timer applies. See SessionGone.
        [JsonPropertyName("session_pid")]
        public int SessionPid { get; set; }

        // The filename is the session id, not a hook payload property. Kept on
        // the in-memory status so a pane operation can verify that the Claude
        // process it found is still this conversation before it focuses or
        // types. Never serialize it back into a hook-owned status file.
        [JsonIgnore]
        public string SessionId { get; set; } = "";

        // Absolute path to the session's JSONL transcript file. The hooks
        // receive it from Claude Code's hook payload and pass it through so
        // the app can read conversation content (e.g. to speak the latest
        // turn aloud). Empty from hooks older than this field.
        [JsonPropertyName("transcript_path")]
        public string TranscriptPath { get; set; } = "";

        // Where this session lives when it does not live on this machine: a
        // https://claude.ai/code/session_… address for a cloud session, empty
        // for everything else. [JsonIgnore] for the reason Source and Kind are
        // — it is the scan's conclusion rather than a hook's, and
        // ResetSessionToIdle writes this object back over a hook-owned file.
        //
        // Carried rather than rebuilt at the click, because the address is made
        // out of the session id and the orb is the only thing that still holds
        // it by then. See ClaudeCloudSessions.Session.Url.
        [JsonIgnore]
        public string Url { get; set; } = "";

        // How full this session's context window is, as a percentage, when
        // whatever lists the session says so. Null is "nobody said", which is
        // not the same as zero and must not draw an empty ring — see
        // OrbWindow.ApplyContextRing.
        //
        // Deliberately not folded into State. State is what the session is
        // doing; this is how much room it has left to keep doing it, which is
        // the one fact about a session you cannot see by looking at it and
        // cannot find out without opening it.
        [JsonIgnore]
        public int? ContextPercent { get; set; }

        // Two lines of hover text for a session whose roster says more than its
        // state does: what it is doing in its own words, and the last thing it
        // was seen to do. Null where nothing was said, and the tooltip simply
        // omits the line rather than printing a placeholder — an orb claiming
        // "unknown" is worse than an orb claiming nothing.
        [JsonIgnore]
        public string? StatusDetail { get; set; }

        [JsonIgnore]
        public string? RecentAction { get; set; }
    }

    // What produced a session. ClaudeCode, Codex and Grok are local processes
    // that fire the hook; OpenClaw is a conversation on a remote gateway with
    // no process, no terminal and no transcript file here. Almost every rule in
    // this file was written for the first and is wrong for OpenClaw, which is
    // why this exists rather than being inferred from which fields happen to be
    // empty.
    //
    // The split that matters most is not one-of-four but local-against-the-rest
    // — see SessionStatus.IsLocalCli. Codex and Grok differ from Claude Code in
    // what their transcripts look like and in what they can be asked to do, not
    // in whether there is a terminal behind them.
    public enum SessionSource
    {
        ClaudeCode,
        Codex,
        Grok,
        OpenClaw,

        // A session on another machine, served over the direct link by the
        // Orbweaver running there (see PeerSessions). It is not local
        // and there is no terminal here to focus, which is the distinction
        // IsLocalCli draws and the only one the rest of the app cares about.
        RemoteControl,

        // A Claude Code session running in Anthropic's cloud, listed by the
        // account API rather than by a hook (see ClaudeCloudSessions). Closest
        // to OpenClaw of anything here: no process, no terminal and no
        // transcript file on this disk, so the local-against-the-rest split is
        // again the one that matters.
        //
        // Deliberately not RemoteControl, which models a session on another
        // machine *of yours* reached through a relay that can type into it.
        // There is no machine of yours behind this one and nothing here can
        // type into it, so a click opens the session in a browser instead.
        ClaudeCloud
    }

    // Watches %TEMP%\orbweaver\<session_id>.txt (one per running Claude
    // Code session, written by OrbweaverHook.ps1) and keeps one OrbWindow
    // per session in sync. A session is considered gone once its file is
    // deleted (SessionEnd hook, on graceful exit) or hasn't been touched in
    // StaleAfter (fallback for Ctrl+C and other ungraceful termination,
    // which SessionEnd isn't reliably delivered for).
    public class SessionManager : IOrbDragHost
    {
        public static SessionManager? Instance { get; private set; }

        // How long an orb outlives its session's last hook write. Settable in the
        // settings window, and null means never — an orb then lasts until its
        // status file is deleted (SessionEnd) or you reset it by hand. Read per
        // scan rather than cached, so a change applies on the next tick.
        internal static TimeSpan? StaleAfter
        {
            get
            {
                var minutes = OrbweaverSettings.OrbLifetimeMinutes;
                return minutes == OrbweaverSettings.OrbLifetimeForever
                    ? null
                    : TimeSpan.FromMinutes(minutes);
            }
        }

        private readonly string _statusDir;

        // The folder the pre-Orbweaver hooks write (StatusDirectory.LegacyFolderName),
        // watched beside _statusDir so a session that was alive across the
        // upgrade — still calling the old script, which still writes the old
        // folder — keeps its orb (CB-255 §1). Null means "one folder only",
        // which is what every test gets unless it passes one, and what the
        // cleanup after phase 3 turns this back into.
        private readonly string? _legacyStatusDir;

        // Which file each session's status came from on the last read, for
        // ResetSessionToIdle, which rewrites a session's file by id. With two
        // folders watched, "<status dir>/<id>.txt" is no longer the answer:
        // resetting a legacy session that way would write a second, newer copy
        // into the new folder, which would then win every scan and hide the
        // file the session's own hook is still writing. (DeleteStatusFile does
        // not need it — it removes the session from both folders.)
        //
        // Replaced whole by each read rather than edited, because the read
        // runs on ScheduleScan's background thread and the two users run on
        // the UI thread: one reference swap is the whole of the hand-over.
        private volatile IReadOnlyDictionary<string, string> _statusFiles =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // What the persona files looked like the last time this session's
        // persona was actually read, and what was read from them:
        // LocalPersona.Signature is existence, length and mtime and nothing
        // else.
        //
        // The scan runs on the UI thread every two seconds, so the per-tick cost
        // of this feature has to be a handful of stats and no more. Reading a
        // CLAUDE.md and decoding a portrait happen on the tick a stat moves and
        // on no other. Keyed by session rather than by directory because two
        // sessions in one repo are two registry entries, and each has to be
        // filled once even though they will agree.
        //
        // The Persona is kept beside the signature for one reason, and it is
        // not caching: its Watched set is the canonical paths actually read —
        // whatever the candidates `@`-imported, and the portrait itself.
        // Neither is a candidate, so a candidate list alone cannot notice an
        // edit to either; the next tick's signature is taken over both, and a
        // persona kept in a `docs/persona.md` or a face replaced in place
        // refreshes like a name written in the CLAUDE.md itself.
        private readonly Dictionary<string, (string Signature, LocalPersona.Persona Persona)>
            _personas = new(StringComparer.Ordinal);

        // Which user-level config directories the persona scan asks about.
        //
        // A seam rather than a direct call, and CB-143 is what bought it.
        // LocalPersona.UserConfigDirs reads two pieces of process-wide mutable
        // state — the CLAUDE_CONFIG_DIR environment variable, and
        // ClaudeCodeProfileDirs by way of ClaudeConfigRoots — and ApplyPersona
        // asked it afresh on every pass. The answer is part of the candidate
        // list, the candidate list is part of the signature, and a signature
        // that differs is the scan's definition of "something moved": so
        // anything at all that changed either of those between two passes
        // handed the registry a brand-new Persona with identical content, and
        // LocalPersonas.Set threw away the decoded portrait for it. The whole
        // settling invariant was hostage to two process-wide globals.
        //
        // In this process that is a test-isolation failure and nothing worse —
        // the app never calls SetEnvironmentVariable, so CLAUDE_CONFIG_DIR is
        // fixed for its lifetime, and a user adding an account in Settings
        // genuinely *should* make the scan look again, because that account's
        // own CLAUDE.md is a user-level persona file it was not reading a
        // moment ago. One re-resolve when the setting changes is correct; one
        // per tick is the bug, and nothing in production produces one.
        //
        // The reason it is a seam anyway is that a test cannot say that. A
        // sibling class in another xUnit collection setting CLAUDE_CONFIG_DIR
        // to a sentinel runs *in parallel* with this one, and every test here
        // whose claim is "the same Persona object came back" fails with nothing
        // wrong with it and nothing to point at. Pinning the provider closes
        // both routes at once and closes them structurally, which a third
        // [Collection] attribute would not: the attribute is a promise that
        // every future writer of either global remembers to join, and this is a
        // scan that no longer reads either.
        //
        // Same pattern, and the same argument, as ORBWEAVER_SETTINGS_DIR and
        // ORBWEAVER_PROFILE_ROOT.
        private readonly Func<IReadOnlyList<string>> _userConfigDirs;

        public SessionManager()
            : this(StatusDirectory.Path(), null, legacyStatusDir: StatusDirectory.LegacyPath())
        {
        }

        // The status directory, said out loud.
        //
        // It has always come from the temp path, and CLAUDE.md documents
        // TMPDIR=<dir> as the way to give a second instance its own fake
        // sessions — which still works, because that is what Path.GetTempPath
        // reads. This overload only makes the same seam nameable, so a test can
        // hand over a scratch directory without reaching for an environment
        // variable that the rest of the process is also reading.
        // jobListing is the second seam, and the reason it is a constructor
        // parameter rather than a call is that the real one shells out to
        // `claude agents --json`. Every rule that consults it is already pure
        // and covered by being handed a listing (ScanVerdictTests,
        // SessionPresenceTests); this is what lets a test of the *scan* hand one
        // over too, instead of asking whatever daemon happens to be running on
        // the machine — which for this suite would be the user's own, with their
        // own real background jobs on it.
        internal SessionManager(string statusDir)
            : this(statusDir, null)
        {
        }

        // Two overloads rather than one with a default, because the one-argument
        // shape is reached by reflection from two test suites (RemoteScanTests,
        // GatewayScanTests) that look it up by exact signature. A default
        // parameter would satisfy the compiler and hand both of them a null
        // constructor at runtime.
        internal SessionManager(
            string statusDir,
            Func<Dictionary<string, string>?>? jobListing,
            Func<HashSet<string>?>? attachClients = null,
            Func<string, string?>? transcriptHunt = null,
            Func<IReadOnlyList<string>>? userConfigDirs = null,
            Func<int, SessionDependents.Verdict>? dependents = null,
            bool? onWindows = null,
            Func<IReadOnlyList<SessionStatus>, IReadOnlyDictionary<TmuxPaneKey, string?>>? paneOwners = null,
            Func<string, AgentViewer?>? agentViewer = null,
            Func<IReadOnlyList<int>, IReadOnlyDictionary<int, AgentTeam.Membership>>? teams = null,
            Action<int, SessionDependents.Verdict>? terminate = null,
            string? legacyStatusDir = null,
            Func<string, string?, bool>? stopJob = null,
            Func<SessionStatus, string?>? desktopBridge = null)
        {
            _statusDir = statusDir;
            _legacyStatusDir = legacyStatusDir;
            _jobListing = jobListing ?? BackgroundJobs.SnapshotForScan;
            // Wrapped rather than handed over as a method group: AttachedJobIds
            // grew an optional `fresh` parameter for the click path, and an
            // optional parameter stops a method group converting to a
            // zero-argument Func. The scan wants the cached form, which is the
            // default, so the lambda says so explicitly.
            _attachClients = attachClients ?? (() => AgentTeamViewer.AttachedJobIds());
            // Wrapped for the same optional-parameter reason: FindTranscriptFor
            // takes a home override the scan never passes.
            _transcriptHunt = transcriptHunt ?? (id => TranscriptReader.FindTranscriptFor(id));
            // Wrapped for the same optional-parameter reason the two above are:
            // UserConfigDirs takes a config-dir override and a home override
            // that the scan never passes, and an optional parameter stops a
            // method group converting to a zero-argument Func.
            _userConfigDirs = userConfigDirs ?? (() => LocalPersona.UserConfigDirs());
            _dependents = dependents ?? SessionDependents.Of;
            _onWindows = onWindows ?? OperatingSystem.IsWindows();
            _paneOwners = paneOwners ?? TerminalFocuser.TmuxPaneOwners;
            _agentViewer = agentViewer ?? AgentTeamViewer.For;
            _teams = teams ?? AgentTeam.OfAll;
            _terminate = terminate ?? SessionTerminator.Terminate;
            _stopJob = stopJob ?? BackgroundJobs.Stop;

            // One lookup for the life of the manager, because what makes it
            // cheap is its memo of each record's mtime. A lambda over the
            // clock, since BridgeFor takes "now" so its re-look interval is
            // testable without waiting.
            var bridges = new DesktopBridgeLookup();
            _desktopBridge = desktopBridge ?? (status => bridges.BridgeFor(status, DateTime.UtcNow));

            // AccountOrbs starts out visible, and nothing used to tell it
            // otherwise until a switch was flipped — so launching with every
            // orb hidden still put the usage orbs up on the first poll.
            _accountOrbs.SetVisible(UsageOrbsVisible);
        }

        // Who is running in each claimed tmux pane, and which `claude agents`
        // window is watching a directory. Seams for the reason the two above
        // are: the real ones run tmux, ps and lsof against this machine, and
        // since this change they are asked from the scan's background half (see
        // ScanProbes), so a test of that half needs to be able to count and
        // answer them.
        private readonly Func<IReadOnlyList<SessionStatus>, IReadOnlyDictionary<TmuxPaneKey, string?>> _paneOwners;
        private readonly Func<string, AgentViewer?> _agentViewer;

        // Which agent team each pid is in, read off its command line — a WMI
        // query on Windows (CB-212). A seam for the same reason as the two
        // above, and so the scan suites can prove it is asked on the background
        // half only: the UI half reads the answer out of ScanProbes.TeamOf.
        private readonly Func<IReadOnlyList<int>, IReadOnlyDictionary<int, AgentTeam.Membership>> _teams;

        // What ends a session's process once EndSession has decided it may
        // (CB-228). A seam so the scan suites can prove the kill runs off the
        // UI thread and that a refused session is never handed to it, without
        // a test signalling anything real.
        private readonly Action<int, SessionDependents.Verdict> _terminate;

        // What asks the daemon to stop a background job, by session id and
        // transcript path, answering whether it did (CB-264). A seam for the
        // reason _terminate is one: the real one runs `claude stop` against
        // whatever daemon is on the machine running the suite, which on a
        // developer's Mac is their own, with their own jobs in it.
        private readonly Func<string, string?, bool> _stopJob;

        // Which claude.ai session a Claude Desktop chat is bridged to by Remote
        // Control, read from the chat's own record (CB-266). A seam because the
        // real one reads the machine's Claude Desktop profiles, and the scan
        // suites must neither depend on nor touch whatever chats the developer
        // running them happens to have.
        private readonly Func<SessionStatus, string?> _desktopBridge;

        // The cloud roster lists a Desktop chat that is on Remote Control a
        // second time, as a session of its own, under the id its record names.
        // That one chat must be one orb, and the local one wins: it has the
        // process, the transcript and the click target. True when a local
        // Desktop chat is bridged to cloudId, so the cloud block skips it.
        // (ClaudeCloudRoster.Keep already drops `bridge`-kind rows; this is the
        // belt for a row it does not recognise.)
        internal static bool CloudShadowedByLocal(string cloudId, IEnumerable<SessionStatus> locals) =>
            locals.Any(local => local.IsBridgedDesktop
                                && string.Equals(local.BridgeSessionId, cloudId, StringComparison.Ordinal));

        // Every subprocess question this pass needs, asked. Runs on
        // ScheduleScan's background thread in production, and inline for
        // ScanAndUpdate's callers; ScanProbePlan has the argument for why its
        // gates ask no more and no less than the reconciliation half reads.
        //
        // CB-217: first it tells the job listing which transcripts the Claude
        // Code sessions here have, so `claude agents --json` is only run for
        // the accounts that own one of them. See BackgroundJobs.AccountsToAsk.
        private ScanProbes GatherProbes(List<ScanEntry> found)
        {
            BackgroundJobs.NoteLiveTranscripts(ClaudeTranscripts(found));

            // CB-212: every pid's team first, in one read, because the plan's
            // gates need the leads and the UI half needs the rest.
            var teams = _teams(ScanProbePlan.TeamPids(found));

            return ScanProbes.Gather(
                ScanProbePlan.For(found, pid => teams.TryGetValue(pid, out var m) ? m.Lead : ""),
                teams, _paneOwners, _jobListing, _attachClients, _agentViewer);
        }

        internal static IEnumerable<string?> ClaudeTranscripts(IEnumerable<ScanEntry> found) =>
            found.Where(e => e.Status.Source == SessionSource.ClaudeCode).Select(e => e.Status.TranscriptPath);

        private readonly Func<Dictionary<string, string>?> _jobListing;

        // Which platform's rules the scan applies, rather than whichever one it
        // happens to be running on — a seam for SessionPresence.LocalDaemonCanAnswerFor,
        // whose whole question is "is this Windows looking at a WSL session".
        // Handed over so the scan suites can ask it on both CI legs: on a Mac
        // runner the real answer is always false, and the case CB-194 fixed
        // would otherwise only ever be exercised on the Windows one.
        private readonly bool _onWindows;

        // What is running underneath a session's pid, for the one irreversible
        // action in the app — a seam for exactly the reason _attachClients is
        // one, and more sharply. The real implementation walks this machine's
        // process table, and the machine this suite runs on has the user's own
        // daemon and their own background jobs on it: a test that asked the real
        // one would be asserting about whatever happened to be running beside
        // it, and would answer differently on a CI runner and on a developer's
        // Mac. Handed over instead, so every arm of the refusal has a case.
        private readonly Func<int, SessionDependents.Verdict> _dependents;

        // How a transcript that is not where the status file says is re-found —
        // TranscriptReader.FindTranscriptFor, behind a seam because the real one
        // walks this machine's own projects directories. The memo deciding when
        // it is worth calling again is TranscriptHunts.
        private readonly Func<string, string?> _transcriptHunt;

        // Every `claude attach` client running on this machine, for the rule that
        // un-dims a parked session somebody is sitting in. A seam for the same
        // reason the listing is one: the real one walks the process table, and a
        // test of the scan has no business asking what is running on the machine
        // it happens to be on — least of all on this one, where the answer
        // includes the user's own attached sessions.
        private readonly Func<HashSet<string>?> _attachClients;

        // How long a status file has to keep looking dead before the scan
        // deletes it. See SessionPresence.EvidenceOfDeath for what "dead" is
        // allowed to mean and why the clock is this process's rather than the
        // file's mtime.
        //
        // Per manager rather than static, so a test that shortens it cannot
        // change what another test class running beside it does — the same
        // hazard OrbweaverSettings' process-wide statics create for the UI
        // suites, and cheaper to avoid here than to serialise around.
        internal TimeSpan SweepGrace { get; set; } = TimeSpan.FromMinutes(10);

        // Session ids whose file has looked dead since a given moment, and the
        // sweep's whole memory. An id leaves this the moment the evidence goes
        // away — a resumed job, a pid that answers again — so the grace period
        // starts over rather than accumulating across the gap.
        private readonly Dictionary<string, DateTime> _deadSince = new(StringComparer.Ordinal);

        private readonly Dictionary<string, OrbWindow> _windows = new();

        // The account orbs, held apart from the session ones on purpose — see
        // the header of AccountOrbs for why they are not entries in _windows.
        // Kept here rather than owned by App so they ride the scan tick this
        // class already runs, instead of starting a second timer.
        private readonly AccountOrbs _accountOrbs = new(
            new CompositeUsageSource(new UsagePoller(), new GrokUsagePoller(), new CodexUsagePoller()));

        // Whether it is time to start and stop Grok to force a fresh usage
        // reading — see GrokUsageRefresh.cs for why that is the only way to
        // get one. Held apart from _accountOrbs because it runs on its own,
        // much longer floor: GrokUsageRefresher.MinimumInterval, not the five
        // minutes the account-orb read itself uses.
        private readonly GrokUsageRefreshScheduler _grokRefresh = new();
        private readonly Dictionary<string, SessionStatus> _statuses = new();
        private readonly List<string> _order = new(); // stable stacking order

        // CB-167's memory of what state each session was last seen in, so the
        // scan can tell a turn actually finishing from a poll that simply
        // found the same idle orb again. Per manager rather than static for
        // the identical reason SweepGrace is: a test that scans a session
        // through generating → idle must not have that transition also
        // observed by some other test's tracker running in the same process.
        private readonly TurnSignalTracker _turnSignals = new();

        private TrayController? _tray;

        // Orbs can be hidden from the tray menu; sessions keep being tracked
        // either way, so the tray icon and its menu stay accurate.
        public bool OrbsVisible { get; private set; } = OrbweaverSettings.ShowOrbs;

        // The account orbs' own switch (CB-220), independent of OrbsVisible:
        // they can be hidden while the session orbs stay, and shown while the
        // session orbs are hidden. SetOrbsVisible still moves both.
        public bool UsageOrbsVisible { get; private set; } = OrbweaverSettings.ShowUsageOrbs;

        // For tests that need to put account orbs on screen without a real
        // usage poll behind them.
        internal AccountOrbs AccountOrbsForTests => _accountOrbs;

        private DeferredWatcher? _watcher;
        private DeferredWatcher? _legacyWatcher;
        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

        // The file the hooks look for to decide whether to colour a session.
        //
        // A marker rather than an argument in the hook command, because the
        // command is part of Codex's hooks.json and Codex hashes that file —
        // changing it marks the entries `modified` and stops them running until
        // the user re-approves the review. A setting that silently switched
        // every Codex orb off until someone noticed is not a setting.
        //
        // Written from the app rather than by the installer so it also needs no
        // re-wiring: the hooks stay exactly as they were and simply see a
        // different answer on their next call.
        private const string AutoColorMarkerName = ".auto-color";

        // Reconciled on every scan rather than only when the toggle is flipped.
        // The status directory lives in the temp path, which the OS is entitled
        // to clear out; a marker that vanished would turn the feature off with
        // nothing said. Two cheap file operations against a directory already
        // being enumerated.
        // internal: the marker file is how the hook learns about the colour
        // setting — the hook runs on every tool call and reading a setting there
        // would be an osascript each time — so what this writes is a contract
        // with a script, not an implementation detail.
        //
        // Into both folders while the legacy one is watched (CB-255 §1): the
        // old hook a long-running session is still calling reads the marker
        // beside the files it writes, so a marker in the new folder alone
        // would silently switch auto-colour off for exactly those sessions.
        // Each folder on its own try, so a legacy folder that does not exist —
        // the usual case, once every old session has restarted — cannot stop
        // the new one being reconciled. Nothing here creates the legacy
        // folder: a marker is not a reason to resurrect it.
        internal void SyncAutoColorMarker()
        {
            var wanted = OrbweaverSettings.AutoColorSessions;

            SyncAutoColorMarkerIn(_statusDir, wanted);
            if (_legacyStatusDir is not null) SyncAutoColorMarkerIn(_legacyStatusDir, wanted);
        }

        private static void SyncAutoColorMarkerIn(string statusDir, bool wanted)
        {
            try
            {
                var marker = Path.Combine(statusDir, AutoColorMarkerName);
                var present = File.Exists(marker);

                if (wanted && !present) File.WriteAllText(marker, "");
                else if (!wanted && present) File.Delete(marker);
            }
            catch
            {
                // Worst case the colour setting does not take effect, which is
                // not worth interrupting a scan for.
            }
        }

        // Excluded from coverage: creates the tray icon, subscribes the speech
        // engine, starts a FileSystemWatcher and a two-second Avalonia timer, and
        // opens the gateway connection. What it schedules is ScanAndUpdate, which
        // is tested directly against a scratch status directory.
        [ExcludeFromCodeCoverage]
        public void Start()
        {
            Instance = this;
            OrbDragHost.Current = this;
            Directory.CreateDirectory(_statusDir);
            SyncAutoColorMarker();

            // Subscribed once for the app's lifetime, so no unsubscribe: this
            // object outlives every orb, which is the point — an orb closing must
            // not stop the other flyouts hearing about speech.
            TextToSpeech.StateChanged += OnSpeakStateChanged;

            _tray = new TrayController();

            StartWatching();

            // CB-106: goes through ScheduleScan, not ScanAndUpdate directly, so
            // the file reads it triggers every two seconds run off this thread.
            _pollTimer.Tick += (_, _) => _ = ScheduleScan();

            // Piggybacks the scan tick rather than adding a timer of its own.
            // AccountOrbs decides whether there is anything to do — it holds the
            // five-minute floor, which is the interval Claude Code's own cache
            // makes meaningful, not this two-second one.
            _pollTimer.Tick += (_, _) => _accountOrbs.Tick(DateTimeOffset.UtcNow);

            // Off the UI thread for the reason AccountOrbs.Tick already is:
            // this starts a real process — the user's actual Grok Build
            // application — and holds it open for several seconds. On the
            // dispatcher that would freeze every orb on screen for the whole
            // hold.
            _pollTimer.Tick += (_, _) =>
            {
                if (_grokRefresh.Tick(
                        DateTimeOffset.UtcNow,
                        OrbweaverSettings.GrokAccountUsageEnabled,
                        OrbweaverSettings.GrokAutoRefreshEnabled))
                {
                    Task.Run(GrokUsageRefresher.Refresh);
                }
            };

            _pollTimer.Start();

            // OpenClawSessions.Restart() used to be called here, but here is
            // after the screen-unlock wait — see Program.cs's serveOnLaunch,
            // where it starts now, for why a machine that stays locked can no
            // longer leave it never called at all (CB-130).


            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _ = ScheduleScan();
            };

            _ = ScheduleScan();
        }

        // Excluded from coverage: creates a FileSystemWatcher on the status
        // directory. The poll timer covers the same job — the comment in the catch
        // below says so — and what either of them schedules is ScanAndUpdate,
        // which is tested directly against a scratch directory.
        [ExcludeFromCodeCoverage]
        private void StartWatching()
        {
            // Off this thread: Start() runs on the UI thread at launch, and on
            // macOS turning a watcher on can wait on a machine-wide disk flush
            // (see DeferredWatcher, CB-234). The poll timer covers us until it
            // arrives, or for good if it never does.
            _watcher = new DeferredWatcher(
                () => new FileSystemWatcher(_statusDir, "*.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                },
                watcher =>
                {
                    watcher.Changed += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                    watcher.Created += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                    watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                },
                "SessionManager.StatusWatcher");

            // The legacy folder too, but only if it is there: a watcher on a
            // folder that does not exist throws, and the old hook creating it
            // later is caught by the poll timer, which reads both regardless.
            if (_legacyStatusDir is { } legacy && Directory.Exists(legacy))
            {
                _legacyWatcher = new DeferredWatcher(
                    () => new FileSystemWatcher(legacy, "*.txt")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    },
                    watcher =>
                    {
                        watcher.Changed += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                        watcher.Created += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                        watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
                    },
                    "SessionManager.LegacyStatusWatcher");
            }
        }

        // Excluded from coverage: restarts an Avalonia timer, and is only ever
        // called from the watcher's own events.
        [ExcludeFromCodeCoverage]
        private void RestartDebounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // One status file, already parsed. Written is the file's mtime, which is
        // what "how long since this session last said anything" means throughout.
        internal sealed record ScanEntry(string SessionId, SessionStatus Status, DateTime Written);

        // Whether the user wants this kind of session tracked at all.
        //
        // OpenClaw's switch predates this and means something stronger — while
        // it is off the app opens no socket and generates no key — so it is
        // consulted where the gateway is asked, not here. These two are display
        // switches over files that are being written regardless.
        internal static bool EnabledFor(SessionSource source) => source switch
        {
            SessionSource.Codex => OrbweaverSettings.CodexEnabled,
            SessionSource.Grok => OrbweaverSettings.GrokEnabled,
            SessionSource.ClaudeCode => OrbweaverSettings.ClaudeCodeEnabled,
            _ => true
        };

        // Which CLI a status file came from, as the enum the rest of the app
        // branches on.
        //
        // One function, called at every point a SessionStatus is deserialized —
        // there are two, the scan below and ResetSessionToIdle — because Source
        // is [JsonIgnore] and therefore arrives as its default no matter what
        // the file said. Missing one of the two does not fail loudly; it
        // produces a Codex session that claims to be a Claude Code one until
        // the next scan corrects it, which is long enough to send a click to
        // the wrong place.
        //
        // Anything unrecognised is Claude Code, deliberately. A status file
        // written before this key existed has no "cli" at all and was Claude
        // Code, and a hook from some future version naming something this build
        // has never heard of is still, at worst, a local session in a terminal.
        // "grok" and "codex" are the two named exceptions.
        // Whether this session's own transcript is worth asking what it is
        // called. See TranscriptIdentity for the case this exists for — a status
        // file whose title was caught empty in a one-off race and never
        // corrected, because the hook only runs when something happens.
        //
        // Claude Code only: those three records are its own, and a Codex rollout
        // contains none of them, so asking would be a tail read per scan that
        // could only ever answer nothing. Same reason the hook's own branch is
        // gated on the CLI.
        //
        // Asked only when something is actually missing, which is what keeps
        // this off the path of every healthy session. A title the hook did
        // record is never second-guessed: it read the same file with the same
        // precedence, and if the two ever disagreed the status file is the more
        // recent reading.
        // The answer for one transcript, remembered against the file's length.
        //
        // Keyed on length rather than on a timer, because length is what decides
        // whether the answer *could* have changed: these records are appended,
        // never rewritten, so a file that has not grown cannot have gained a
        // name. A stat per untitled session per scan is nothing; a 256KB tail
        // read every two seconds for every unnamed session would not be.
        //
        // The negative answer is cached the same way as the positive one, which
        // is the half that matters for cost — a session that genuinely has no
        // title yet is exactly the one this would otherwise re-read forever.
        // It still notices the title arriving, because arriving means the file
        // grew.
        //
        // Excluded from coverage: the two file-system calls and the dictionary
        // around them. What is worth asserting is which sessions get asked
        // (WantsIdentityFromTranscript), what is done with the answer
        // (ApplyIdentity) and how a transcript is read (TranscriptIdentity.From)
        // — all three pure, all three tested. This is the stat and the cache.
        [ExcludeFromCodeCoverage]
        private TranscriptIdentity IdentityFor(string path)
        {
            long length;
            try { length = new FileInfo(path).Length; }
            catch { return TranscriptIdentity.None; }

            if (_identityCache.TryGetValue(path, out var cached) && cached.Length == length)
                return cached.Identity;

            var identity = TranscriptIdentity.From(TranscriptReader.TailLines(path));
            _identityCache[path] = (length, identity);
            return identity;
        }

        private readonly Dictionary<string, (long Length, TranscriptIdentity Identity)>
            _identityCache = new(StringComparer.Ordinal);

        internal static bool WantsIdentityFromTranscript(SessionStatus status) =>
            status.Source == SessionSource.ClaudeCode
            && !string.IsNullOrEmpty(status.TranscriptPath)
            && (string.IsNullOrEmpty(status.Title) || string.IsNullOrEmpty(status.Color));

        // Fills in only what the status file is missing, and only from a
        // non-empty answer. Never an overwrite: a name or colour the hook
        // recorded stays exactly as it was, so this can never move an orb that
        // was already right.
        internal static void ApplyIdentity(SessionStatus status, TranscriptIdentity identity)
        {
            if (string.IsNullOrEmpty(status.Title) && !string.IsNullOrEmpty(identity.Title))
                status.Title = identity.Title;

            if (string.IsNullOrEmpty(status.Color) && !string.IsNullOrEmpty(identity.Color))
                status.Color = identity.Color;
        }

        // Whether a status file's transcript_path is a claim worth re-checking:
        // it names a file, and the file is not there.
        //
        // That happens for real, and not only in the first moments of a session.
        // The daemon respawns a finished job's worker from the job's *original*
        // directory, and Claude Code computes the hook's transcript_path from
        // the cwd it launched with — while the conversation itself lives in the
        // projects directory keyed by wherever the session actually ran. Seen
        // live: job b0633b77 ran in a worktree, its respawned worker's status
        // file pointed at the parent checkout's projects dir, and the file
        // there had never existed. Every consumer of the path — the identity
        // read, the nothing-to-show rule, the chat panel — then agreed the
        // conversation was gone, and a 3.6MB transcript rendered as a blank
        // panel.
        //
        // Claude Code only: the hunt walks Claude Code's own config roots, so
        // for a Codex session it is a directory walk that can only answer
        // nothing. An *empty* path is not repaired here — that is the shape the
        // chat panel already hunts for itself, and the rules that read the path
        // treat empty as "unknown", which is already the right reading.
        internal static bool WantsTranscriptRepair(SessionStatus status, Func<string, bool> exists) =>
            status.Source == SessionSource.ClaudeCode
            && !string.IsNullOrEmpty(status.TranscriptPath)
            && !exists(status.TranscriptPath);

        private readonly TranscriptHunts _transcriptHunts = new();

        // File.Exists as a delegate, once. The repair asks it twice per file in
        // the scan loop, and a method group converted at a call site compiles
        // to a hidden per-site cache with a null-check branch in it — a branch
        // no test can pin both arms of on purpose. One named field, no branch.
        private static readonly Func<string, bool> FileExists = File.Exists;

        internal static SessionSource SourceOf(SessionStatus status)
        {
            if (string.Equals(status.Cli, "grok", StringComparison.OrdinalIgnoreCase))
                return SessionSource.Grok;
            if (string.Equals(status.Cli, "codex", StringComparison.OrdinalIgnoreCase))
                return SessionSource.Codex;
            return SessionSource.ClaudeCode;
        }

        // Session ids one process has already moved on from.
        //
        // A Claude Code process mints a new session id every time you /clear,
        // resume, or start a new conversation, and the hook writes a *new*
        // <session-id>.txt for each. Nothing deletes the old ones: SessionEnd only
        // fires when the process exits, and it hasn't. So one terminal accumulates
        // several files that all name a live pid — the process-gone rule can't
        // touch them, and the lifetime timer was the only thing that ever would.
        //
        // That put duplicate orbs on screen for a whole lifetime. Observed with
        // four files on one pid: three orbs for one terminal, two of them showing
        // `generating`, because a superseded id keeps whatever state it was last
        // written with and no further hook ever corrects it. The stale one is
        // indistinguishable from real work.
        //
        // Within one process only the newest file is the live session, so the rest
        // go now. Two genuinely concurrent sessions are two processes with two
        // pids, so this can't collapse them into one — or so it was until Agent
        // View. Dispatching a background session from `←` doesn't fork a new OS
        // process; it starts a second conversation inside the one `claude` process
        // already running, so that process's pid now names several *simultaneously
        // live* status files, not a trail of ones the terminal has moved on from.
        // Observed directly: one pid, four files, three different titles, one of
        // them in a different cwd entirely (a worktree) — plainly not the same
        // conversation re-minting its session id.
        //
        // The two cases can't be told apart by mtime alone — a live background
        // session and last week's abandoned /clear both just sit there once
        // nothing is happening to them. `isLiveJob` (the caller passes
        // BackgroundJobs.IsLiveJob) is what actually knows: it asks the daemon's
        // own job list, which a leftover /clear id was never on and a running
        // background session still is. A non-newest entry only goes stale if that
        // check also says it's not live — so the original single-terminal case is
        // untouched, and a pid hosting several genuine Agent View sessions keeps
        // an orb for each.
        //
        // Passed in rather than called directly so this stays what the rest of
        // the file's rules are — pure and fast to test — instead of shelling out
        // to `claude agents --json` from inside a unit test.
        //
        // A pid of 0 means a hook older than the session_pid field. Grouping those
        // would put every such file in one bucket and drop all but one, so they're
        // left alone and keep the old behaviour — the same reason
        // ProcessLiveness.IsRunning treats 0 as alive.
        internal static HashSet<string> Superseded(List<ScanEntry> found, Func<string, bool> isLiveJob)
        {
            // Keyed by pid *and* which CLI, not by pid alone.
            //
            // A pid is only unique among the files one CLI wrote. Claude Code
            // running `codex exec` as a Bash tool is the case that breaks the
            // assumption: the nested codex process sits in a pipe with no tty
            // of its own, so the hook's walk can record the pid of the Claude
            // Code session that started it, and a Codex status file lands in
            // the same bucket as a live Claude Code one. Being newer it would
            // win, and this rule would delete the Claude orb — an orb the user
            // is watching, for the session that is doing the work.
            //
            // Two CLIs never share a real process, so pairing the source with
            // the pid costs nothing and cannot collapse two genuine sessions.
            var newest = new Dictionary<(int Pid, SessionSource Source), ScanEntry>();

            foreach (var entry in found)
            {
                var pid = (entry.Status.SessionPid, entry.Status.Source);
                if (pid.SessionPid <= 0) continue;

                // The ordinal tie-break only matters if two files somehow share an
                // mtime, and exists so the choice doesn't depend on the order the
                // directory happened to enumerate in.
                if (!newest.TryGetValue(pid, out var best)
                    || entry.Written > best.Written
                    || (entry.Written == best.Written
                        && string.CompareOrdinal(entry.SessionId, best.SessionId) > 0))
                {
                    newest[pid] = entry;
                }
            }

            var stale = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in found)
            {
                var pid = (entry.Status.SessionPid, entry.Status.Source);
                if (pid.SessionPid <= 0) continue;

                if (newest.TryGetValue(pid, out var best) && best.SessionId != entry.SessionId
                    && !isLiveJob(entry.SessionId))
                {
                    stale.Add(entry.SessionId);
                }
            }

            return stale;
        }

        // Whether a status file says anything about where its session can be
        // seen. A tty alone doesn't count here: it's the one field the walk
        // always fills in, and on its own it can name a tmux pane's pty, which
        // belongs to a detached server rather than to any window.
        internal static bool KnowsATerminal(SessionStatus status) =>
            !string.IsNullOrEmpty(status.TmuxPane)
            || !string.IsNullOrEmpty(status.TermProgram)
            || !string.IsNullOrEmpty(status.TermId)
            || status.TermPid != 0;

        // Terminal coordinates belong to the *process*, not to the session id,
        // so a status file that has none can take them from a sibling with the
        // same pid.
        //
        // Background sessions are why this is needed. Claude Code spawns one
        // through `claude daemon run`, which has no controlling terminal, so
        // the hook's walk up the process tree goes straight past it to the
        // interactive session that started it — recording *that* pid and tty
        // while carrying none of its environment, because the daemon has no
        // TMUX or TERM_PROGRAM to inherit. The result is a second, thinner file
        // for a pid that already had a good one, and being newer it wins the
        // superseded rule below and replaces a clickable orb with a dead one.
        //
        // Observed exactly that way: one pid with three files, the oldest
        // naming tmux pane %7 and the two newest naming nothing, and an orb
        // that had quietly stopped going anywhere. The inheritance is sound
        // because the pid is the same process: same window, same tmux pane.
        //
        // Only the empty fields are filled, so a file that knows its own
        // terminal is never overwritten by an older one's idea of it.
        internal static void InheritTerminalInfo(List<ScanEntry> found)
        {
            // Grouped by pid and source together, for the reason Superseded
            // is: a nested `codex exec` can record the pid of the Claude Code
            // session that spawned it, and this would then hand one CLI's
            // session the tmux pane of the other's. Clicking that orb would go
            // somewhere plausible and wrong, which is worse than a dead click.
            foreach (var group in found.Where(e => e.Status.SessionPid > 0)
                                       .GroupBy(e => (e.Status.SessionPid, e.Status.Source)))
            {
                var donor = group.Where(e => KnowsATerminal(e.Status))
                                 .OrderByDescending(e => e.Written)
                                 .FirstOrDefault();
                if (donor is null) continue;

                foreach (var entry in group)
                {
                    var status = entry.Status;
                    if (KnowsATerminal(status)) continue;

                    status.TermProgram = donor.Status.TermProgram;
                    status.TermId = donor.Status.TermId;
                    status.TermPid = donor.Status.TermPid;
                    status.TmuxSocket = donor.Status.TmuxSocket;
                    status.TmuxPane = donor.Status.TmuxPane;
                    status.TmuxBin = donor.Status.TmuxBin;

                    if (string.IsNullOrEmpty(status.Tty)) status.Tty = donor.Status.Tty;
                }
            }
        }

        // Reconcile a pane claim with the session id in that pane's current
        // Claude process. This is deliberately narrower than a liveness rule:
        // it says only that a particular coordinate is no longer this status's
        // coordinate. We remove the stale claimant only when the verified owner
        // is another status in the same scan, so a failed or partial hook pass
        // never makes a live conversation disappear.
        internal static HashSet<string> ReconcileTmuxPaneClaims(
            List<ScanEntry> found, Func<ScanEntry, string?> paneOwner)
        {
            var stale = new HashSet<string>(StringComparer.Ordinal);

            foreach (var claim in found.Where(entry =>
                entry.Status.Source == SessionSource.ClaudeCode
                && !string.IsNullOrEmpty(entry.Status.TmuxPane)))
            {
                var owner = paneOwner(claim);
                if (string.IsNullOrEmpty(owner)
                    || string.Equals(owner, claim.SessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var recipients = found.Where(entry =>
                    string.Equals(entry.SessionId, owner, StringComparison.OrdinalIgnoreCase)).ToList();
                if (recipients.Count != 1) continue;

                var recipient = recipients[0];
                if (!KnowsATerminal(recipient.Status))
                {
                    recipient.Status.TermProgram = claim.Status.TermProgram;
                    recipient.Status.TermId = claim.Status.TermId;
                    recipient.Status.TermPid = claim.Status.TermPid;
                    recipient.Status.TmuxSocket = claim.Status.TmuxSocket;
                    recipient.Status.TmuxPane = claim.Status.TmuxPane;
                    recipient.Status.TmuxBin = claim.Status.TmuxBin;
                    if (string.IsNullOrEmpty(recipient.Status.Tty)) recipient.Status.Tty = claim.Status.Tty;
                }

                stale.Add(claim.SessionId);
            }

            return stale;
        }

        // Ids whose file shares its (pid, source) with another file in this scan.
        //
        // The domain InheritTerminalInfo donates within, and the situation
        // Superseded exists to resolve — stated once so the rule that consults it
        // cannot drift from either. Grouped by pid *and* source for the reason
        // both of those give: a nested `codex exec` can record the pid of the
        // Claude Code session that started it, and two CLIs never share a real
        // process.
        //
        // A pid of 0 shares nothing. It means a hook older than the session_pid
        // field, and bucketing those together would say every such file shares a
        // pid with every other — the same trap Superseded's own comment names.
        internal static HashSet<string> SharingAPid(List<ScanEntry> found)
        {
            var counts = new Dictionary<(int Pid, SessionSource Source), int>();
            foreach (var entry in found)
            {
                if (entry.Status.SessionPid <= 0) continue;

                var key = (entry.Status.SessionPid, entry.Status.Source);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

            var shared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in found)
            {
                if (entry.Status.SessionPid <= 0) continue;

                if (counts[(entry.Status.SessionPid, entry.Status.Source)] > 1)
                {
                    shared.Add(entry.SessionId);
                }
            }

            return shared;
        }

        // Why a status file gets no orb this scan, or Keep when it does.
        //
        // Named answers rather than a bool, because every one of these five
        // reasons has been wrong in a build somebody shipped — a Ctrl+C'd
        // session whose orb never went away, an orb per subagent, a Codex file
        // nothing on any path could remove. "Should this orb exist" is only
        // half a rule; which rule dropped it is the half that can be checked,
        // and the half a regression shows up in.
        internal enum ScanVerdict
        {
            Keep,

            // An id this process has already moved on from — see Superseded.
            Superseded,

            // The CLI process that wrote the file has exited.
            ProcessGone,

            // The conversation was handed to a background job — either
            // mid-turn (see TranscriptHandoff) or from an idle window by
            // opening agents mode (see SessionPark) — and this session is not
            // the one showing it any more. The fork wears the conversation now,
            // badge, title and all; this file is what the handoff left behind,
            // and no hook will ever rewrite it.
            Backgrounded,

            // Quiet for longer than the user's "Keep orbs for" allows.
            Expired,

            // Nothing recorded that a click could be sent to.
            NoTerminal,

            // No process named, and the daemon does not know it as a job.
            NotALiveJob,

            // No terminal to jump to and no transcript to read: an orb that
            // can't be clicked anywhere and whose chat can only ever be blank.
            NothingToShow,
        }

        // The rules that need nothing but the file, its mtime, whether its
        // process is alive, and whether its transcript says the conversation
        // was handed away.
        //
        // isRunning is passed in for the reason Superseded's isLiveJob is: the
        // real one is a kill(2) against a pid this machine may or may not have,
        // which is not a thing a unit test should be deciding.
        //
        // handedToBackground is a closure rather than a bool for the same
        // reason isRunning is not a bool: it costs a stat (and on a change, a
        // read) against a transcript on this machine's disk, and a session the
        // rules above have already dropped must never pay for it. The gates on
        // when it is even worth asking live in
        // SessionPresence.CouldBeABackgroundedHusk; the answer itself is
        // TranscriptHandoff's.
        internal static ScanVerdict JudgeLiveness(
            string sessionId, SessionStatus status, DateTime written, DateTime now,
            TimeSpan? staleAfter, ISet<string> superseded, Func<int, bool> isRunning,
            Func<bool> handedToBackground)
        {
            // Dropped here rather than left to the lifetime timer, which is the
            // only other thing that would ever catch it.
            if (superseded.Contains(sessionId)) return ScanVerdict.Superseded;

            // Gone is gone: if the claude process that wrote this file has
            // exited, no lifetime setting should keep its orb — that's the
            // Ctrl+C case, which fires no SessionEnd and so leaves the file
            // behind. This applies to `waiting` as well, which the timer
            // below deliberately never touches; an unanswered prompt whose
            // session was killed used to sit on screen indefinitely.
            if (status.SessionPid > 0 && !isRunning(status.SessionPid)) return ScanVerdict.ProcessGone;

            // A turn the user backgrounded leaves this file behind: frozen at
            // whatever the hook last wrote — "generating", usually, and
            // sometimes "waiting" — while the conversation carries on under
            // the fork's session id and the fork's own orb. Two orbs wearing
            // one title, one of them a lie that no hook will ever correct,
            // which is why this outranks the state exemptions below: a husk
            // frozen at "waiting" is not waiting on anyone.
            if (handedToBackground()) return ScanVerdict.Backgrounded;

            // A session waiting on you (permission prompt / question) never
            // goes stale on its own — no further hook fires until you
            // respond, so the file's mtime is frozen for as long as you're
            // away. Pruning it would hide the orb exactly when it matters
            // most. Use "Reset this session to idle" to clear a genuinely
            // abandoned one manually.
            // "generating" is exempt for gateway sessions for the same reason
            // "waiting" is exempt for local ones: it is the state where hiding
            // the orb is worst. A local session can't be caught by this because
            // its file is being rewritten as it works, which a gateway session
            // has no equivalent of — it is a roster read on a timer, so "still
            // working" and "nothing has been heard for a while" look identical
            // from here.
            //
            // **A cloud session is exempt in every state, not just that one**
            // (CB-182). "Keep orbs for" is a rule about local sessions, where a
            // quiet status file means the process has probably exited and the
            // orb left behind is a husk nothing will ever clean up. A cloud
            // session has no process to have exited: it is a durable
            // server-side resource that can be resumed at any time, so a clock
            // measuring silence here is measuring the wrong thing entirely.
            // Retention for these is already decided by ClaudeCloudRoster.Keep,
            // whose `!archived` half is the user's own signal in the product
            // that owns the sessions — and a second retention rule, on a clock,
            // is what made an idle cloud session nineteen hours quiet draw no
            // orb at all until somebody typed into the web UI to move its
            // `updated_at` back inside the window.
            if (staleAfter is not null
                && status.Source != SessionSource.ClaudeCloud
                && status.State != "waiting"
                && !(status.Source == SessionSource.OpenClaw && status.State == "generating")
                && now - written > staleAfter)
            {
                return ScanVerdict.Expired;
            }

            return ScanVerdict.Keep;
        }

        // One status file, read and parsed but not yet judged: what both
        // folders contribute before NewestPerSession picks one per session.
        internal sealed record StatusFileRead(
            string SessionId, string Path, SessionStatus Status, DateTime Written);

        // Every parseable status file in one folder, or null if the folder
        // itself cannot be listed. Null rather than empty because
        // HeadlessSnapshot has to tell "no folder" from "an empty one" when
        // neither folder can be listed, which is the one case it logs.
        //
        // A file that cannot be read or parsed is skipped, the same as the
        // scan always has — mid-write or vanished, retried next tick — and
        // skipping it *before* the merge is the point: a session whose copy in
        // one folder is half-written is answered by its copy in the other
        // rather than by nothing.
        internal static List<StatusFileRead>? ReadStatusDirectory(string statusDir)
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(statusDir, "*.txt").ToList();
            }
            catch
            {
                return null;
            }

            var reads = new List<StatusFileRead>(files.Count);
            foreach (var file in files)
            {
                SessionStatus? status;
                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(file);
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    status = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(stream);
                }
                catch
                {
                    continue; // mid-write or vanished; retried next tick
                }

                if (status is null) continue;

                reads.Add(new StatusFileRead(
                    Path.GetFileNameWithoutExtension(file), file, status, written));
            }

            return reads;
        }

        // One file per session out of the new folder and the legacy one
        // (CB-255 §1): **the newest write wins, and the loser is ignored, not
        // deleted.** The realistic duplicate is a resumed session —
        // `claude --resume` keeps the id — whose old hook wrote the legacy
        // folder before the upgrade and whose new one writes the new folder
        // after it; the newer file is the one that knows what it is doing now.
        //
        // Not deleted, because a scan is not allowed side effects on a file a
        // hook may be in the middle of writing, and a loser is by definition a
        // file somebody else's hook owns. It goes the way it always would
        // have: a SessionEnd from whichever hook wrote it, or the temp cleaner.
        //
        // A tie goes to the new folder, so the answer never depends on
        // enumeration order. Order out is the new folder's own, then whatever
        // only the legacy folder had — so a caller handed one folder sees it
        // exactly as it was. Pure: no disk, no clock.
        internal static List<StatusFileRead> NewestPerSession(
            IReadOnlyList<StatusFileRead> current, IReadOnlyList<StatusFileRead> legacy)
        {
            var winners = new List<StatusFileRead>(current.Count + legacy.Count);
            var at = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var read in current.Concat(legacy))
            {
                if (!at.TryGetValue(read.SessionId, out var index))
                {
                    at[read.SessionId] = winners.Count;
                    winners.Add(read);
                }
                else if (read.Written > winners[index].Written)
                {
                    winners[index] = read;
                }
            }

            return winners;
        }

        // The scan, reduced to what another machine's Buddy needs before this
        // one has a UI.
        //
        // Exists for exactly one caller: RemoteControlSessions.LocalSessions,
        // in the window between the relay starting to serve (Program.Main,
        // before the screen-lock wait — see CB-24) and SessionManager.Start
        // handing over the real provider. A headless machine whose screen never
        // unlocks lives in that window permanently, so the roster served from
        // it has to be right rather than a placeholder.
        //
        // Composed from the same rules the live scan applies, in the same order
        // — SourceOf, EnabledFor, the own-relay drop, Superseded, JudgeLiveness
        // — so the two cannot disagree about which sessions exist. What it
        // deliberately skips are the live scan's two refinements, transcript
        // re-hunting and the identity read: both lean on per-instance memos
        // that belong to the scan loop, so a session in this window serves
        // under the name and paths its status file already carries, and the
        // real provider replaces this one the moment the UI starts.
        //
        // Every input is a parameter with the real one as its default — the
        // same pattern, for the same reason, as ClaudeBinary.Locate: a test
        // hands in a scratch directory and fixed answers, and the machine the
        // test runs on stops mattering. The default arms themselves shell out
        // (`claude agents --json`) or read this machine's real temp directory,
        // so tests always pass all four; those four `??` arms are the named
        // coverage gap, exactly as the live scan's constructor defaults are.
        //
        // legacyStatusDir is the pre-Orbweaver hooks' folder (CB-255 §1), read
        // beside statusDir and merged by NewestPerSession exactly as the live
        // scan merges them. It follows statusDir's default: left out with
        // statusDir also left out, it is the real legacy folder, because that
        // is the production call and a headless machine serving a session
        // that predates the upgrade must not report it missing; left out with
        // a statusDir handed in, it is no second folder at all, so every test
        // written before the second folder existed still reads exactly one.
        internal static List<(string SessionId, SessionStatus Status)> HeadlessSnapshot(
            string? statusDir = null,
            Func<Dictionary<string, string>?>? jobListing = null,
            Func<int, bool>? isRunning = null,
            DateTime? nowUtc = null,
            bool honourOrbLifetime = true,
            bool? onWindows = null,
            Func<IReadOnlyList<int>, IReadOnlyDictionary<int, AgentTeam.Membership>>? teams = null,
            string? legacyStatusDir = null)
        {
            if (statusDir is null)
            {
                statusDir = StatusDirectory.Path();
                legacyStatusDir ??= StatusDirectory.LegacyPath();
            }

            var windows = onWindows ?? OperatingSystem.IsWindows();
            var now = nowUtc ?? DateTime.UtcNow;

            var current = ReadStatusDirectory(statusDir);
            var legacy = legacyStatusDir is null ? null : ReadStatusDirectory(legacyStatusDir);
            if (current is null && legacy is null)
            {
                // No directory yet means no sessions yet, which is an answer.
                MirrorLog.SayOnce("headless-scan", $"dir={statusDir} unreadable");
                return new List<(string, SessionStatus)>();
            }

            var reads = NewestPerSession(current ?? new(), legacy ?? new());

            // Says where it looked and what it found, once. Two halves of this
            // app agreeing on a directory is not something either can check
            // alone, and when they disagreed the symptom was a machine calmly
            // reporting no sessions while running two.
            MirrorLog.SayOnce("headless-scan",
                $"dir={statusDir} legacy={legacyStatusDir ?? "none"} files={reads.Count}");

            var found = new List<ScanEntry>();
            foreach (var read in reads)
            {
                var status = read.Status;

                status.Source = SourceOf(status);
                if (!EnabledFor(status.Source)) continue;
                if (MachineNames.LooksLikeALeftoverRelay(status.Cwd)) continue;

                // And here too, so a peer asking this machine what it is running
                // is told the same thing the screen shows. A summariser
                // advertised over the mirror would draw its orb on somebody
                // else's desk instead of this one, which is the same defect
                // wearing a different machine.
                if (InternalSessions.IsInternal(status.SessionPid)) continue;

                found.Add(new ScanEntry(read.SessionId, status, read.Written));
            }

            var jobs = (jobListing ?? BackgroundJobs.SnapshotForScan)();
            Func<string, bool> isLiveJob = id => BackgroundJobs.IsLive(jobs, id);
            var superseded = Superseded(found, isLiveJob);
            var running = isRunning ?? ProcessLiveness.IsRunning;

            var kept = new List<(string, SessionStatus)>();
            foreach (var entry in found)
            {
                // The husk check, asked the same way the live scan asks it.
                //
                // This call site was left behind when JudgeLiveness grew the
                // parameter, and the two changes met in a merge that had no
                // textual conflict to report — so develop stopped compiling
                // with nothing pointing at either branch.
                //
                // Answered rather than stubbed with `() => false`, which would
                // have compiled and been wrong: this method's own comment above
                // says it is composed from the same rules as the live scan "so
                // the two cannot disagree about which sessions exist", and a
                // stub disagrees about exactly one thing — a backgrounded husk,
                // which the machine asking for this roster would then draw as a
                // live session that no hook will ever correct. A closure for the
                // same reason the live scan uses one: only a session the earlier
                // rules kept ever pays for the stat behind it.
                //
                // The live scan gates the phase on `worthAsking` as well as on
                // the source, and its absence here is deliberate rather than an
                // omission. `worthAsking` is a whole-scan decision about whether
                // to *spend the subprocess* that fetches the daemon's listing —
                // its own comment says so — and this method has already fetched
                // that listing unconditionally, a few lines up. There is no
                // subprocess left to gate, so the condition could only suppress
                // an answer already paid for.
                //
                // That leaves one case where the two genuinely differ, and it is
                // worth stating because the contract above says they cannot.
                // WorthAskingTheDaemon can be false for a session that *is* a
                // job — the $TERM_PROGRAM hole its own comment admits to, where
                // a daemon started inside a terminal passes one down to every
                // job under it — and there the live scan sees Unknown where this
                // sees the real phase. The disagreement is in this method's
                // favour: Unknown passes the husk gate and the real phase
                // (Working, Parked, Done) does not, so the live scan can read a
                // live job's inherited marker as a husk where this one cannot.
                // It is a difference in accuracy, not in the rule, and it can
                // only ever keep a session this method should keep.
                //
                // The one guard the live scan has that is not about cost: a
                // session the listing cannot know about — WSL, seen from
                // Windows — reads Unknown here as it does there, or a paired
                // machine would be told this one's WSL sessions do not exist.
                var status = entry.Status;
                var daemonCanAnswer = SessionPresence.LocalDaemonCanAnswerFor(status, windows);
                var phase = status.Source == SessionSource.ClaudeCode && daemonCanAnswer
                    ? BackgroundJobs.Phase(jobs, entry.SessionId)
                    : JobPhase.Unknown;

                Func<bool> handedToBackground = () =>
                    SessionPresence.CouldBeABackgroundedHusk(status, phase)
                    && (SessionPark.IsParked(status.SessionPid, entry.SessionId)
                        || TranscriptHandoff.EndsBackgrounded(status.TranscriptPath, entry.SessionId));

                // **Orb lifetime is a display preference and does not belong in
                // an answer to another machine.** Its own definition says "how
                // long an *orb* outlives its session's last hook write" — it
                // exists so a screen full of yesterday's orbs tidies itself up.
                // A far machine asking what sessions this one has is asking a
                // question of fact, and "I have stopped drawing it" is not an
                // answer to it.
                //
                // Measured on the mini, which is the whole point of the feature:
                // two Claude Code sessions alive and idle, their status files
                // last written when the user last typed into them, both dropped
                // as Expired — so every roster it served said it had nothing,
                // while the far panel showed a session it could not open.
                //
                // Everything else JudgeLiveness decides still applies, and the
                // distinction is exactly right: superseded, process-gone and
                // backgrounded-husk are *facts* about whether the session is
                // there. Only expiry is a preference about how long to keep
                // showing one, and only expiry is dropped here.
                //
                // Except for a session with no fact left to go on. A WSL
                // session seen from Windows names no process this machine can
                // probe, and the daemon that could vouch for it is in the VM —
                // so once the listing stops being asked (above), the lifetime
                // is the only thing that can ever say it is gone. Dropping it
                // here too would offer a WSL session that was killed without a
                // SessionEnd to every peer forever, which is the exact failure
                // the pid-less rule below exists to prevent. It is the same
                // rule the visible scan applies to that session, so the two
                // still agree about which sessions exist.
                var verdict = JudgeLiveness(
                    entry.SessionId, entry.Status, entry.Written,
                    now, honourOrbLifetime || !daemonCanAnswer ? StaleAfter : null,
                    superseded, running, handedToBackground);

                // Ignoring orb lifetime answers a narrower question than
                // "keep every file forever". A positively live pid is evidence
                // that an old idle terminal still exists, but a pid-less
                // Claude status has no such proof: it is either a current
                // daemon job or a leftover from a finished subagent. Apply the
                // same daemon-backed reachability rule the visible scan uses so
                // a headless machine never advertises the latter to a peer.
                if (verdict == ScanVerdict.Keep && entry.Status.SessionPid <= 0)
                {
                    verdict = JudgeReachability(
                        entry.SessionId, entry.Status, new HashSet<string>(), phase, FileExists);
                }

                if (verdict == ScanVerdict.Keep) kept.Add((entry.SessionId, entry.Status));
                else
                {
                    // Per session and per verdict, so a machine reporting no
                    // sessions says which rule dropped them. Every one of these
                    // is a legitimate answer on its own and a mystery in
                    // aggregate — "0 sessions" is the same output for a stale
                    // file, a dead process, a superseded twin and a husk.
                    // Sliced with a bound rather than a literal 8: a session id
                    // is a uuid in the app and four characters in a fixture, and
                    // a diagnostic that throws on the short one turns "explain
                    // this drop" into "lose the whole scan".
                    var shortId = entry.SessionId.Length > 8
                        ? entry.SessionId[..8]
                        : entry.SessionId;

                    MirrorLog.SayOnce($"headless-drop:{entry.SessionId}",
                        $"{shortId} {verdict} pid={entry.Status.SessionPid} "
                        + $"written={entry.Written:HH:mm} phase={phase}");
                }
            }

            MirrorLog.SayOnce("headless-kept", $"found={found.Count} kept={kept.Count}");

            // CB-223: which of these are agent-team members, and whose. The
            // live scan has always known (ScanProbes.TeamOf, read off each
            // process's command line); this snapshot is what a peer is served
            // from, and without it a member's team was unknowable over the
            // link. One batched read for every Claude Code pid kept, the same
            // read the live scan's background half makes — and none at all
            // when nothing kept is Claude Code.
            var claudePids = kept
                .Where(k => k.Item2.Source == SessionSource.ClaudeCode && k.Item2.SessionPid > 0)
                .Select(k => k.Item2.SessionPid)
                .Distinct()
                .ToList();

            // A direct call, not `(teams ?? AgentTeam.OfAll)(pids)`: that form
            // makes the compiler cache a delegate for the method group, which
            // is an allocation for nothing and, to one of the two coverage
            // engines, two more branch arcs on a line no UI suite runs.
            if (claudePids.Count > 0)
                ApplyTeams(kept, teams is null ? AgentTeam.OfAll(claudePids) : teams(claudePids));

            return kept;
        }

        // Team membership onto snapshot statuses, by the live scan's own rule
        // (see the block that sets Lead and Agent in the scan): a process whose
        // lead is itself is the lead, not a member, and a member's name is
        // kept only while it has a lead. Claude Code only — a gateway session
        // has no process to ask and uses Lead for its room.
        internal static void ApplyTeams(IEnumerable<(string SessionId, SessionStatus Status)> sessions,
            IReadOnlyDictionary<int, AgentTeam.Membership> teams)
        {
            foreach (var (sessionId, status) in sessions)
            {
                if (status.Source != SessionSource.ClaudeCode) continue;

                var membership = teams.TryGetValue(status.SessionPid, out var m) ? m : AgentTeam.None;
                status.Lead = membership.Lead == sessionId ? "" : membership.Lead;
                var member = !string.IsNullOrEmpty(status.Lead);
                status.Agent = member ? membership.Name : "";
                status.AgentColor = member ? membership.Color : "";
            }
        }

        // Whether this session is one of the shapes that is watched through a
        // `claude agents` window rather than through a terminal of its own, and
        // so worth hunting for one. See AgentTeamViewer, which finds it by
        // directory.
        //
        // A team lead can be a background session with no terminal of its own —
        // run inside `claude daemon run`, which has none, and reparented to
        // launchd once the session that started it went away. You watch it
        // through `claude agents`, a separate process in a real window that
        // nothing in the lead's own process tree points at, so the hook could
        // never have recorded it. A session with no pid of its own is in the
        // same bind and for the same reason, and the machinery is identical —
        // TryAdopt no-ops off macOS, without a cwd, and when no viewer for that
        // directory is running.
        //
        // Not for a gateway session: TryAdopt matches on cwd string equality
        // alone, and the machine running the gateway usually has the same
        // repositories checked out at the same paths. It would hand a remote
        // session the tmux pane of unrelated local work, and clicking that orb
        // would jump to it — worse than a dead click, because it looks like it
        // worked.
        //
        // Not for Codex either, for a plainer reason: the viewer this adopts is
        // a `claude agents` window, and there is no such thing to find for a
        // Codex session. The cwd-collision hazard above applies just as much,
        // and here both repositories would be on this machine.
        //
        // "pid <= 0" here was standing in for "this is a background agent", and
        // it stopped being true the moment the hook learned to record a
        // background agent's own pid instead of whatever ancestor owned a
        // terminal. The proxy is replaced by the thing it was approximating
        // rather than kept alongside it: a session with no pid is either a
        // background job — in which case isLiveJob says so — or a leftover that
        // should not be adopting a viewer window in the first place.
        //
        // Getting this wrong is not subtle. With the pid recorded and this
        // condition unchanged, adoption stopped running for every background
        // agent, so none of them found the `claude agents` window they are
        // watched through, and JudgeReachability below then dropped them for
        // having no terminal. One orb vanished on this machine before the cause
        // was obvious.
        // The isLiveJob arm is gone, and the reason is a two-line trace from a
        // real machine. Adoption grafts a viewer pane onto a session by cwd
        // match, and with every live background job eligible, a parked job in
        // the same directory as the user's own viewer adopted the pane the
        // user was sitting in: FocusCore then "focused" it, returned true, and
        // the click on that orb did visibly nothing — the dead background
        // click this whole branch was opened for, wearing its last disguise.
        //
        //   Focus id=ed54b99d… shape=Background pane='%2' bin='' tty='ttys008'
        //     FocusCore -> True, detached=False
        //
        // bin='' is TryAdopt's own signature: an adopted pane is the app's
        // guess about where somebody is watching, not the session's recorded
        // location, and a guess must not answer a click. The arm existed to
        // rescue background orbs from the no-terminal rule before the phase
        // exemptions did that properly (see RuledOutAsAJob below); its orb-
        // rescue job is superseded and its click behaviour was the bug.
        //
        // What remains is the case adoption was built for: a lead whose
        // agents are on screen gets the viewer's pane, so clicking the lead
        // lands on the roster that shows its work. Plus the legacy pid-less
        // arm, for hooks older than the session_pid field.
        internal static bool WantsAgentViewer(
            string sessionId, SessionStatus status,
            ISet<string> leadsWithLiveAgents) =>
            status.Source == SessionSource.ClaudeCode
            && !status.IsDesktopHosted
            && !KnowsATerminal(status)
            && (leadsWithLiveAgents.Contains(sessionId)
                || status.SessionPid <= 0);

        // Whether an orb for this session would go anywhere when it was
        // clicked. Asked after adoption above, which is what can give a
        // background lead a terminal it had none of a moment earlier.
        // phase is what the daemon said about this session, rather than the
        // is-it-live bool this used to take. Two reasons, and the first is only
        // tidiness: the caller has the phase already, so passing a closure that
        // reduces it to a bool was a second question about the same answer.
        //
        // The second is behaviour. "Live" collapsed the two ways a job can be
        // finished-with — over, and never a job at all — and only one of them
        // should cost an orb its place on screen. See
        // SessionPresence.RuledOutAsAJob, which is now what both exemptions below
        // ask, and which keeps the fail-open direction: a listing that could not
        // be read rules nothing out.
        internal static ScanVerdict JudgeReachability(
            string sessionId, SessionStatus status,
            ISet<string> leadsWithLiveAgents, JobPhase phase,
            Func<string, bool> transcriptExists)
        {
            // An orb is good for two things: clicking it to reach the session's
            // terminal, and opening its chat to read the conversation. A session
            // with neither a terminal nor a transcript has nothing behind it in
            // either direction — the click goes nowhere and the chat opens
            // blank, showing an amber "connecting" dot that never turns green
            // and a composer disabled with "No terminal to type into".
            //
            // This is what a background job that has not been given a prompt
            // looks like. Measured on a real machine: job "evidence", `state:
            // blocked` and `needs: send a prompt to start` in the daemon's own
            // record, live in `claude agents --json`, forty minutes old, and no
            // transcript written anywhere under either config root. The
            // no-terminal rule below would have caught it, but the live-job
            // exemption there — which exists because a background job has no
            // terminal *by nature* — waved it through. That exemption is right
            // about the terminal and says nothing about whether there is a
            // conversation, which is what this rule adds.
            //
            // Deliberately narrow, and it has to be:
            //
            // - A session with a terminal is never touched. It may have no
            //   transcript yet, but the click still lands, so the orb still
            //   earns its place. A tty alone is not a terminal here, exactly as
            //   KnowsATerminal already says it isn't: the daemon runs every
            //   background worker under a pty host, so its sessions all record
            //   a real /dev/ttysNN that no window anywhere is showing. This
            //   rule shipped with an extra "and no tty" clause anyway, and the
            //   pty host is what that clause turned into a hole — every worker
            //   the daemon parks or pre-warms sailed through on its pty, and a
            //   never-prompted one sat on screen as an untitled orb whose chat
            //   could only ever open blank. Seen live as session de995bd9:
            //   idle, no name, tty ttys006, no transcript anywhere, absent from
            //   the daemon's own listing.
            // - A lead with live agents is exempt for the same reason it is
            //   exempt below: agents drawn on screen pointing at nothing is a
            //   worse lie than an orb whose chat is thin.
            // - An *empty* transcript path answers "keep", not "drop". A path
            //   the hook never recorded means this app does not know where the
            //   conversation would be, not that there isn't one — Codex's
            //   transcript_path is nullable, and the chat panel itself falls
            //   back to hunting the projects directories by session id. Same
            //   reasoning as BackgroundJobs.IsLive answering true for a listing
            //   it could not read: the rule only fires on a positive answer,
            //   because the failure the user cannot see is the one worth being
            //   careful about. It is also what keeps this O(1) per session per
            //   scan rather than a deep directory walk every two seconds.
            //
            // The cost is that a background job's orb appears a scan or two
            // late — the status file is written before the first transcript row
            // — and a job sitting unprompted stays hidden until it is prompted.
            // Both are the intended reading of "nothing to show". A session in
            // a terminal that sets no TERM_PROGRAM — ssh in from elsewhere, an
            // emulator that doesn't announce itself — now pays the same scan or
            // two of lateness, because its file is tty-only and this rule can
            // no longer tell it from a daemon worker until its transcript
            // appears. It appears within the first exchange, and the trade is a
            // brand-new session's orb arriving late against a phantom's never
            // leaving.
            //
            // Claude Desktop's sessions are exempt (CB-265): the conversation is
            // in Desktop's Code tab and the click opens Desktop, so neither a
            // terminal nor a readable transcript is what the orb is for.
            if (status.IsLocalCli
                && !status.IsDesktopHosted
                && !KnowsATerminal(status)
                && !leadsWithLiveAgents.Contains(sessionId)
                && !string.IsNullOrEmpty(status.TranscriptPath)
                && !transcriptExists(status.TranscriptPath))
            {
                return ScanVerdict.NothingToShow;
            }

            // A session with no terminal recorded at all can't be jumped
            // to, so an orb for it is a dead click. This is what headless and
            // bridged invocations look like: no tty, no terminal program, no
            // tmux pane, no Windows terminal pid. An interactive session
            // always has at least one of those.
            //
            // Except a lead with live agents, which is exempt whether or not
            // the viewer above was found: agents on screen pointing at
            // nothing is a worse lie than an orb you might not be able to
            // click. It's also a session you can see is running, which is
            // what an orb is for.
            // A gateway session has none of these and never will — it has
            // no terminal anywhere, which is the point of it. Left ungated
            // this rule alone drops every OpenClaw orb, every scan.
            // A live background job is exempt for the same reason a lead
            // with live agents is: it has no terminal of its own by nature,
            // not because it is a leftover. That used to be covered for free
            // by the pid test below — a background agent recorded no pid — and
            // is now stated, because the hook records its real pid.
            // Claude Desktop hosts its sessions with no tty, TERM_PROGRAM or pane
            // by design (CB-265, measured), so it is exempt here too.
            if (status.IsLocalCli
                && !status.IsDesktopHosted
                && string.IsNullOrEmpty(status.Tty)
                && string.IsNullOrEmpty(status.TermProgram)
                && string.IsNullOrEmpty(status.TmuxPane)
                && status.TermPid == 0
                && !leadsWithLiveAgents.Contains(sessionId)
                && !(status.Source == SessionSource.ClaudeCode
                     && !SessionPresence.RuledOutAsAJob(phase))
                && status.SessionPid > 0)
            {
                return ScanVerdict.NoTerminal;
            }

            // A session recording no pid is only worth an orb if it's a
            // background job that is still running.
            //
            // "No pid" is a wider net than it first appears. A background
            // agent has none — which is what the exemption above is for —
            // but neither does a subagent, and neither does a status file
            // whose session ended without clearing it. All three write the
            // same "idle" state, so on disk they're identical; only the
            // daemon knows which is which. Taking pid-less to mean
            // "background agent" put a permanent orb on screen for every
            // subagent anyone spawned, and with the lifetime set to forever
            // they only accumulated.
            //
            // Asked of nothing else, so an ordinary session never pays for
            // the lookup, and a listing that can't be read keeps every orb
            // — see BackgroundJobs.
            // Likewise: a gateway session records no pid, so this would ask
            // the local daemon about a session it has never heard of — once
            // per scan, per session — and drop the orb when the answer came
            // back "not a job", which it always would.
            if (status.Source == SessionSource.ClaudeCode
                && status.SessionPid <= 0
                && SessionPresence.RuledOutAsAJob(phase))
            {
                return ScanVerdict.NotALiveJob;
            }

            // The same rule for Codex, with the exemption removed rather
            // than reused. `claude agents` is what makes a pid-less Claude
            // Code session worth an orb, and Codex has no equivalent — no
            // background job to attach to, nothing to ask. So a Codex file
            // naming no process is a session that ended without clearing
            // up, and an orb for it is a permanent dead click.
            //
            // Written out rather than folded into the rule above because
            // leaving Codex to fall through it was a real hole: the
            // no-terminal rule two blocks up requires a pid to fire, and
            // the liveness check treats pid 0 as alive, so nothing else
            // would ever have removed it. With the orb lifetime set to
            // "forever", nothing would have removed it at all.
            if ((status.Source == SessionSource.Codex || status.Source == SessionSource.Grok)
                && status.SessionPid <= 0)
            {
                return ScanVerdict.NotALiveJob;
            }

            return ScanVerdict.Keep;
        }

        // Read this session's persona, if the files it could be written in have
        // moved since the last time we looked.
        //
        // Internal so a test can drive one session's read without a whole scan;
        // the caching is the part worth exercising directly, because "it is only
        // stats unless something changed" is the entire reason this is allowed
        // to run on the UI thread twice a second and is not visible from
        // outside any other way.
        internal void ApplyPersona(
            string sessionId,
            SessionStatus status,
            Dictionary<(string Cwd, SessionSource Source, string Agent), IReadOnlyList<LocalPersona.Candidate>> candidatesByCwd)
        {
            // A gateway session's identity comes from the gateway, and a
            // remote-control relay is not a conversation at all. Neither has a
            // working directory on this machine whose CLAUDE.md would be about
            // *it* — a relay's cwd is wherever the app was started from, and
            // reading a persona out of that would put the developer's own
            // repository name on somebody else's orb.
            //
            // IsNullOrWhiteSpace rather than IsNullOrEmpty: this is now the
            // only guard in front of the resolution, where it used to be one of
            // two and LocalPersona.ResolveForSession carried the stricter half.
            // A cwd of " " walks nowhere, so its candidate list would be the
            // user-level files alone — a persona from ~/.claude on an orb whose
            // session has no working directory at all.
            if (!status.IsLocalCli || string.IsNullOrWhiteSpace(status.Cwd)) return;

            // Source is part of the key, not just the directory: Claude Code
            // consults the user-level config directories and Codex and Grok
            // deliberately do not, so two CLIs open on one repo have two
            // different candidate lists and one cache entry would hand the
            // second one the first one's.
            //
            // A tuple rather than the two glued together with a separator. A
            // directory name on macOS or Linux may legally contain very nearly
            // any byte, so every separator is one somebody could have in a path
            // — and the first attempt at this reached for a NUL, which is
            // exactly the byte that makes a source file read as binary to grep.
            //
            // Agent is part of the key too (CB-154): every member of an agent
            // team shares one cwd, and a key without the agent's own name
            // would hand the first member's candidate list — and therefore its
            // persona — to every sibling that scans after it, regardless of
            // whether CandidateFiles found a member-specific file for any of
            // them. status.Agent is "" for anything that isn't a team member,
            // which reproduces today's one-entry-per-cwd behaviour exactly.
            //
            // Candidates rather than bare paths (CB-187): each carries the
            // walk level it was found at, which the fold needs to resolve a
            // picture profile-gen wrote relative to the project directory.
            // The signature below stats the paths alone, exactly as before.
            var key = (status.Cwd, status.Source, status.Agent);
            if (!candidatesByCwd.TryGetValue(key, out var candidates))
            {
                candidates = LocalPersona.Candidates(
                    status.Cwd, _userConfigDirs(), status.Source, status.Agent);
                candidatesByCwd[key] = candidates;
            }

            var known = _personas.TryGetValue(sessionId, out var cached) ? cached : default;

            // Over the candidates *and* whatever the last read actually
            // followed. The two differ in two ways, and both were found by a
            // change that went unnoticed rather than reasoned about in
            // advance.
            //
            // A CLAUDE.md `@`-imports another file: the import is where the
            // persona often lives and is never a candidate itself, so watching
            // the candidate list alone leaves an edited `docs/persona.md`
            // invisible until something else in the tree happens to move.
            //
            // And a portrait is replaced in place — same filename, new bytes,
            // markdown untouched. Nothing in any *markdown* file has changed,
            // so a signature over markdown alone is identical, the cached
            // persona is re-Set, the registry's reference check sees the same
            // object and keeps the decoded bitmap, and the orb wears the old
            // face until the app restarts. Persona.Watched is Files plus the
            // picture for exactly that reason, and it costs one extra stat per
            // tick and only for a session that has a portrait at all.
            var candidatePaths = candidates.Select(candidate => candidate.Path);
            var watched = known.Persona is null
                ? candidatePaths
                : candidatePaths.Concat(known.Persona.Watched);

            var signature = LocalPersona.Signature(watched);

            // Nothing moved. Re-Set anyway, because this is also the path a
            // session takes on the tick after another session in the same
            // repository resolved the identical files — the registry's own
            // reference check makes that free, and skipping it outright would
            // leave a session whose entry was dropped elsewhere with no persona
            // until one of its files changed.
            if (known.Persona is not null && known.Signature == signature)
            {
                LocalPersonas.Set(sessionId, known.Persona);
                return;
            }

            // Set drops the decoded picture for this session itself, so an
            // edited portrait is re-decoded rather than served from the cache
            // forever. Deliberately not repeated here: the registry owns that
            // invariant, and a second call site is a second place for it to be
            // half-removed later.
            //
            // This is the only path that resolves, so it is the only path that
            // hands Set an object it has not seen — which is what makes Set's
            // reference check the right one rather than a value comparison.
            //
            // ResolveFrom(candidates) rather than a second walk from the cwd.
            // The list stat'd for the signature above and the list read here
            // are now one list; they used to be two, each built from its own
            // reading of CLAUDE_CONFIG_DIR and ClaudeCodeProfileDirs, with the
            // whole signature comparison in between them.
            //
            // status.Cwd passed as the workspace root (CB-147): it is already
            // guarded non-blank above, and it is what lets a picture written
            // relative to the workspace root — rather than to the directory
            // of the markdown that named it — still resolve.
            var persona = LocalPersona.ResolveFrom(candidates, status.Cwd);

            // Stored over the set the *next* tick will compare, not the one
            // just compared. On the very first pass there is no previous read
            // to take imports from, so `watched` above was the candidates
            // alone — and storing that would make the second tick's longer
            // signature differ for no reason and resolve a second time, every
            // time, forever. One more round of stats here is what stops that.
            _personas[sessionId] =
                (LocalPersona.Signature(candidatePaths.Concat(persona.Watched)), persona);

            LocalPersonas.Set(sessionId, persona);
        }

        // One pass over the status directory: read everything, decide what
        // deserves an orb, and reconcile the windows, the arrows and the tray
        // with the answer.
        //
        // Internal rather than private so a test can drive a pass directly
        // instead of waiting on the two-second timer that normally calls it.
        // Nothing outside this class should: it is idempotent, but it is also
        // the whole of the scan, and calling it from anywhere but the timer, the
        // watcher's debounce or Start would mean two passes racing over the same
        // dictionaries on the same thread's re-entrancy.
        //
        // Split into ReadStatusFiles (the disk-bound half) and ScanAndUpdateCore
        // (the reconciliation half) for CB-106: ScheduleScan below runs the
        // former on a background thread so the Avalonia dispatcher is never the
        // thread waiting on a file open. This wrapper still does both, inline
        // and synchronous, on whichever thread calls it — which is what the
        // many tests that scan a scratch directory and assert in the same line
        // rely on, and is fine for them since a test's own status directory
        // never sees the ambient disk contention CB-106 was about.
        internal void ScanAndUpdate()
        {
            SyncAutoColorMarker();
            var now = DateTime.UtcNow;
            var found = ReadStatusFiles(now);
            ScanAndUpdateCore(found, GatherProbes(found), now);
        }

        // Guards ScheduleScan against a tick landing while the previous scan's
        // background read is still in flight.
        private bool _scanInFlight;

        // The production entry point: Start() wires the poll timer and the
        // file-watcher debounce to this instead of to ScanAndUpdate directly.
        //
        // CB-106: a live sample caught the main thread pinned for 8-11 seconds
        // inside a raw sync() syscall, reached from Avalonia's native macOS
        // run-loop signaller straight through to unsymbolized managed frames —
        // i.e. from managed code the dispatcher was running. ScanAndUpdate's
        // file-reading half (Directory.EnumerateFiles, File.GetLastWriteTimeUtc,
        // FileStream opens, and the transcript-repair hunt, which reads more
        // files still) was, until this change, the one thing this app ran
        // unconditionally on that thread every two seconds, forever, uncached —
        // and it was the only candidate that needed no specific user action to
        // land inside the disk-pressure window the ticket described (heavy
        // concurrent dotnet build/swiftc activity from unrelated processes on
        // the same machine): it runs on every tick, not just the rare one where
        // someone happens to flip a settings toggle or click "reset to idle".
        // An ordinary small read can block for seconds waiting on kernel
        // writeback under that kind of system-wide contention regardless of
        // which syscall a stack sample happens to land on, which is why this
        // fix does not chase the literal symbol sync() into Avalonia's native
        // (and unsymbolized) macOS backend — nothing in this repository calls
        // it directly, and moving *our* blocking I/O off the UI thread is the
        // part actually within reach here.
        //
        // Guarded rather than queued: a tick that lands mid-scan means the
        // previous scan is still waiting on the very contention this exists to
        // survive, and starting a second read on top of it would only add to
        // it. Skipping it is safe — nothing here is lost, only deferred to the
        // next tick two seconds later.
        //
        // async/await rather than a manual ContinueWith: called from the
        // Avalonia UI thread, `await` resumes on the SynchronizationContext it
        // captured — Avalonia's own — so ScanAndUpdateCore below still runs on
        // the UI thread it always has, with the only change being that the
        // thread is free to pump other work while Task.Run's file reads are
        // in flight instead of blocked inside them.
        internal async Task ScheduleScan()
        {
            if (_scanInFlight) return;
            _scanInFlight = true;
            try
            {
                var now = DateTime.UtcNow;
                var (found, probes) = await Task.Run(() =>
                {
                    SyncAutoColorMarker();
                    var read = ReadStatusFiles(now);
                    return (read, GatherProbes(read));
                }).ConfigureAwait(true);

                ScanAndUpdateCore(found, probes, now);
            }
            finally
            {
                _scanInFlight = false;
            }
        }

        // The disk-bound half of a scan: opens and parses every status file,
        // repairs a missing transcript path and reads an identity off it when
        // one is needed. Pure aside from the small per-session caches
        // (_transcriptHunts, _personas) it reads and writes, which only this
        // method and its caller's single-threaded continuation touch — see
        // ScheduleScan's guard for why two of these never run concurrently.
        //
        // `now` is a parameter rather than DateTime.UtcNow read here, so a
        // background read and the reconciliation pass that follows it agree on
        // one instant — ScanAndUpdate's callers already expected that when this
        // was one method, and JudgeLiveness/SweepDeadFiles downstream compare
        // against it too.
        private List<ScanEntry> ReadStatusFiles(DateTime now)
        {
            // Both folders while the legacy one is watched, one file per
            // session — see NewestPerSession for which one and why the other
            // is left alone.
            var reads = NewestPerSession(
                ReadStatusDirectory(_statusDir) ?? new(),
                _legacyStatusDir is null ? new() : ReadStatusDirectory(_legacyStatusDir) ?? new());

            _statusFiles = reads.ToDictionary(r => r.SessionId, r => r.Path, StringComparer.Ordinal);

            // Read everything before judging any of it: whether a file is live can
            // depend on the *other* files — see Superseded.
            var found = new List<ScanEntry>();
            foreach (var read in reads)
            {
                var status = read.Status;
                var written = read.Written;
                var sessionId = read.SessionId;

                status.Source = SourceOf(status);
                status.SessionId = sessionId;

                // A transcript that is not where the hook said gets re-found by
                // session id, before anything reads the path: the identity read
                // below, the nothing-to-show rule, and the chat panel all
                // receive this same status object, and all of them believing a
                // wrong path is exactly the blank-orb bug — see
                // WantsTranscriptRepair for the respawned-job shape that
                // produces one. In memory only, deliberately: the file is the
                // hook's record of what Claude Code said, and the next event
                // will rewrite it anyway.
                if (WantsTranscriptRepair(status, FileExists))
                {
                    var hunted = _transcriptHunts.Locate(
                        sessionId, now, _transcriptHunt, FileExists);
                    if (hunted is not null) status.TranscriptPath = hunted;
                }

                // A name the status file never caught, read from the transcript
                // it already names. Costs nothing for a session that has one.
                if (WantsIdentityFromTranscript(status))
                    ApplyIdentity(status, IdentityFor(status.TranscriptPath!));

                // A CLI switched off is ignored, not unwired. Its hooks keep
                // writing status files — they are the user's own config, and a
                // display switch that rewrote it would be a surprise, and for
                // Codex would cost them their hook trust on top. Skipping here
                // rather than later means everything downstream, including the
                // pid grouping and the tray, behaves as though those sessions
                // were not running.
                if (!EnabledFor(status.Source)) continue;

                // This app's own remote-control relay is plumbing, not a
                // conversation, and it was drawing itself a grey orb with an empty
                // chat behind it. A relay is a Claude Code session like any other
                // — its hook fires and it writes a status file — so nothing before
                // this branch distinguished it, and it hid inside the dead-orb
                // noise the earlier rounds cleared out.
                //
                // Dropped here rather than later, for exactly the reason the CLI
                // switch above is: everything downstream — the pid grouping, the
                // team links, the tray, the sweep, and the right-click menu with
                // End and Dismiss on it — then behaves as though the relay were
                // not running, which is the truth as far as the user is concerned.
                // Suppressing the orb further down would have left a session the
                // menu could still be pointed at.
                //
                // The prefix test that recognises a relay this app once started —
                // see MachineNames.LooksLikeALeftoverRelay for why it is the
                // prefix and not the live tag, and why the cwd rather than argv.
                if (MachineNames.LooksLikeALeftoverRelay(status.Cwd)) continue;

                // A CLI this app started for its own purposes — the throwaway
                // `claude -p` that writes a spoken summary, and the usage poll.
                // Same argument as the relay directly above: it is plumbing
                // wearing a session's clothes, its hook fires like anyone's, and
                // nothing before this branch told it apart.
                //
                // Dropped here for the same reason too. Suppressing the orb
                // further down would leave a session the pid grouping, the team
                // links, the tray and the right-click menu could all still be
                // pointed at, which is a worse shape than not seeing it at all.
                if (InternalSessions.IsInternal(status.SessionPid)) continue;

                found.Add(new ScanEntry(sessionId, status, written));
            }

            return found;
        }

        // The reconciliation half of a scan: joins the gateway's and the
        // remote-control bridge's sessions onto the file-derived list ReadStatusFiles
        // produced, then decides what deserves an orb and reconciles the
        // windows, the arrows and the tray with the answer. Everything from
        // here on touches Avalonia windows and the tray, so — unlike
        // ReadStatusFiles — this stays on whichever thread calls it:
        // ScanAndUpdate above calls it inline; ScheduleScan calls it after
        // hopping back onto the UI thread its await resumed on.
        private void ScanAndUpdateCore(List<ScanEntry> found, ScanProbes probes, DateTime now)
        {
            var seen = new HashSet<string>();
            bool setChanged = false;

            // What CB-167's tracker noticed this pass. Collected rather than
            // acted on inline, because the decision is coalesced across the
            // whole scan — four orbs finishing at once is one sound, not
            // four — so nothing plays until every session in this pass has
            // been observed.
            var turnSoundEvents = new List<TurnSoundEvent>();

            // Which files a persona could be written in, per working directory,
            // for the length of this one pass. Local rather than a field on
            // purpose: building the list is pure string work over a path walk —
            // cheap enough to redo every couple of seconds — where a field
            // keyed by directory would accumulate an entry for every directory
            // any session has ever run in, for the life of the process, to save
            // nothing measurable. Several sessions in one repo is the common
            // case and is what this actually saves.
            var candidatesByCwd =
                new Dictionary<(string Cwd, SessionSource Source, string Agent), IReadOnlyList<LocalPersona.Candidate>>();

            // Which Remote Control bridge each Claude Desktop chat is on, before
            // anything below reads it: the cloud block asks whether a cloud row
            // is one of these chats, and the chat panel reads it off the status
            // this pass publishes. Re-derived every pass — `/rc` can switch it on
            // or off while the session runs — and cheap, because the lookup
            // memoises on the record's mtime.
            foreach (var entry in found)
            {
                if (entry.Status.IsDesktopHosted)
                {
                    entry.Status.BridgeSessionId = _desktopBridge(entry.Status) ?? "";
                }
            }

            // The gateway's sessions join the same list the status files
            // produced, so everything downstream — ordering, stacking, pinning,
            // the tray, the removal pass — stays a single code path. Empty and
            // free when the feature is off.
            //
            // Written is the session's own last activity, deliberately, not the
            // time of this scan. A gateway lists every conversation it has ever
            // had (59 on the machine this was built against, of which two had
            // been touched in the last five minutes), so stamping "now" would
            // put an orb on screen for all of them forever. Using real activity
            // lets the user's own "Keep orbs for" setting do the filtering, with
            // no new concept and no second timeout to reason about.
            var gatewaySessions = OpenClawSessions.Snapshot();

            // Channel -> the room orb to stand for it. Keyed by
            // OpenClawSessionKind.RoomOf, so every agent in a channel agrees.
            var rooms = new Dictionary<string, (string Title, DateTime Activity, bool Working)>(
                StringComparer.Ordinal);

            foreach (var session in gatewaySessions)
            {
                var status = new SessionStatus
                {
                    Source = SessionSource.OpenClaw,
                    State = session.State,
                    Title = session.Title,

                    // Per *agent*, not per session, so an agent's DM and its two
                    // channels read as one thing in three places rather than as
                    // three unrelated orbs. Derived from the id, so it is the
                    // same colour next launch without anything being stored.
                    //
                    // This field used to be handed session.Channel, which is a
                    // channel name and matches no colour Claude Code knows — so
                    // every gateway orb fell through to the plain ring, and six
                    // of them were indistinguishable.
                    // Asked for rather than computed here: an agent's ring and
                    // a chat bubble from that agent have to agree, so exactly
                    // one place decides. See OpenClawSessions.ColourForAgent.
                    Color = OpenClawSessions.ColourForAgent(
                        OpenClawSessions.AgentIdOf(session.Key) ?? session.Key),
                    Kind = session.Kind,
                    Heartbeat = session.Heartbeat,
                };

                // Namespaced because these ids share a dictionary with Claude
                // Code's UUIDs, and because a gateway key contains colons and
                // slashes that ResetSessionToIdle would otherwise splice into a
                // file path.
                // Which room this is standing in, if any. The room orb itself is
                // added below, once, however many agents point at it.
                // ...but only once there is more than one agent in it. One agent
                // talking in a channel drew two orbs wearing the same face, an
                // arrow between them, and a merged conversation with one member
                // — every one of which says "these are the same thing" about a
                // thing that was never two. The room orb earns its place by
                // gathering several agents; with one, the agent's own orb
                // already is the channel, and its # badge already says so.
                //
                // Counted in agents rather than sessions, since one agent can
                // hold two sessions in the same channel and that is still one
                // face. Asked of the participants, so an agent that has been
                // quiet for longer than the recency window does not conjure a
                // room orb around somebody who is on their own — the same list
                // the picture is cut from, so the arrows and the faces cannot
                // disagree about who is here.
                var room = OpenClawSessionKind.RoomOf(session.Key);
                if (room is not null && OpenClawSessions.AgentsInRoom(room).Count > 1)
                {
                    status.Lead = RoomId(room);

                    if (!rooms.TryGetValue(room, out var seenRoom)
                        || session.LastActivity > seenRoom.Activity)
                    {
                        rooms[room] = (RoomTitle(session.Title), session.LastActivity,
                            (seenRoom.Working || session.State == "generating"));
                    }
                    else if (session.State == "generating")
                    {
                        rooms[room] = (seenRoom.Title, seenRoom.Activity, true);
                    }
                }

                found.Add(new ScanEntry("openclaw:" + session.Key, status, session.LastActivity));
            }

            // One orb per channel, for the agents in it to point at. Invented
            // here rather than reported by the gateway, which has no notion of a
            // room as a thing — it has a session per agent per channel, and
            // eight of those on screen is eight orbs with nothing saying they
            // are the same conversation.
            foreach (var (key, room) in rooms)
            {
                found.Add(new ScanEntry(
                    RoomId(key),
                    new SessionStatus
                    {
                        Source = SessionSource.OpenClaw,
                        IsRoom = true,
                        Title = room.Title,

                        // For the panel's header chip. The orb itself skips the
                        // badge — it *is* the channel — but the panel is a
                        // window onto a conversation and saying which kind is
                        // the same help it is anywhere else.
                        Kind = SessionKind.Channel,

                        // Busy while anyone in it is, which is what a room
                        // being "active" means.
                        State = room.Working ? "generating" : "idle",

                        // And its own colour. This used to stay empty on the
                        // reasoning that a ring identifies an agent and a room
                        // is not one — true while one room was on screen, and
                        // wrong with several, where every room is a dark circle
                        // with a # on it and only the badge distinguishes them,
                        // which says what they are rather than which. See
                        // OpenClawSessions.ColourForRoom for why it is keyed on
                        // the room rather than dealt from the agents' pool.
                        Color = OpenClawSessions.ColourForRoom(key),
                    },
                    room.Activity));
            }

            // Claude Code sessions on the user's other machines, seen through
            // the bridge. Empty unless the feature is on *and* something has
            // asked for the bridge — see RemoteControlSessions.EnsureStarted for
            // why merely enabling it isn't enough.
            //
            // Simpler than the gateway branch above: there are no rooms and no
            // colour pool here. There are leads, since CB-223 — a far Buddy
            // says which of its sessions are agent-team members and whose, and
            // offers a member only alongside its lead, so the lead's key here
            // is the namespaced key of an orb this scan is drawing too. That is
            // what lets TeamLinks pair them on the dictionary key, exactly as
            // it pairs a local team, with nothing translated. A relay-only far
            // machine sends no team shape, and its rows stay flat.
            foreach (var remote in RemoteControlSessions.Snapshot())
            {
                // Received data only. A remote cwd is intentionally absent
                // from this ScanEntry, so ApplyPersona below returns before any
                // local candidate path can be constructed for it.
                PeerPersonas.Set(remote.Key, remote.Persona);
                found.Add(new ScanEntry(
                    remote.Key,
                    new SessionStatus
                    {
                        Source = SessionSource.RemoteControl,
                        RemoteCli = remote.Cli ?? MirrorProtocol.CliClaudeCode,

                        // The peer list's own word, translated into the two
                        // states an orb draws. Anything that isn't recognisably
                        // work counts as idle: an orb that spins forever because
                        // a label changed upstream is worse than one that never
                        // spins.
                        State = remote.Working ? "generating" : "idle",

                        // Its name on the other machine, which is all the peer
                        // list gives us — no hostname, no path. That is thin for
                        // a title, and deliberately not padded out with a guess:
                        // see docs/remote-control-findings.md.
                        Title = remote.Name,

                        // What the session itself said, when it has been asked
                        // and answered — a remote session's colour cannot be
                        // derived here, because a peer row carries neither the
                        // transcript /color writes into nor the cwd auto-colour
                        // hashes. See RemoteControlSessions.AskForMissingColorsAsync.
                        //
                        // Falls back to a colour hashed from the name, which is
                        // stable per session and unrelated to whatever it wears
                        // at home — better than every remote orb being identical
                        // while the answer is still in flight, or if it never
                        // comes.
                        //
                        // A team member with no colour of its own wears the one
                        // Claude Code assigned it, the precedence the local scan
                        // uses (a /color inside the agent outranks the team's).
                        Color = remote.Color ?? remote.AgentColor ?? OpenClawSessions.ColourForAgent(remote.Name),

                        // CB-223. Lead is the lead's own orb key, Agent the
                        // member's name in its team — which everything
                        // user-facing prefers over the title every member
                        // inherits from its lead.
                        Lead = remote.LeadKey ?? "",
                        Agent = remote.LeadKey is null ? "" : remote.Agent ?? "",
                        AgentColor = remote.LeadKey is null ? "" : remote.AgentColor ?? "",

                        Kind = SessionKind.Remote,
                    },
                    remote.Seen));
            }

            // Claude Code sessions running in Anthropic's cloud. Empty and free
            // unless claudeCloudEnabled is on — ClaudeCloudSessions.Snapshot
            // holds that gate itself, so this block needs no second one.
            //
            // Simpler than either branch above, and for the same reason the
            // remote-control one is: a cloud session is one conversation in one
            // place. There are no rooms, no leads, and no local anything.
            foreach (var session in ClaudeCloudSessions.Snapshot())
            {
                // A Desktop chat on Remote Control is already an orb of its own.
                if (CloudShadowedByLocal(session.Id, found.Select(entry => entry.Status))) continue;

                found.Add(new ScanEntry(
                    "cloud:" + session.Id,
                    new SessionStatus
                    {
                        Source = SessionSource.ClaudeCloud,
                        Kind = SessionKind.Cloud,
                        State = session.State,
                        Title = session.Title,

                        // Deliberately absent, exactly as the remote-control
                        // block above leaves it: ApplyPersona returns early on
                        // an empty cwd, so no local candidate path is ever
                        // constructed for a session that has no directory on
                        // this machine — or on any machine the user owns.
                        Cwd = "",

                        // Hashed from the session id, so the orb wears the same
                        // colour next launch with nothing stored. A cloud
                        // session has no /color to read and no cwd to
                        // auto-colour from, so this is the only stable answer
                        // available — the same reasoning, and the same
                        // function, the remote-control fallback uses.
                        Color = OpenClawSessions.ColourForAgent(session.Id),

                        Url = session.Url,
                        ContextPercent = session.ContextPercent,
                        StatusDetail = session.StatusDetail,
                        RecentAction = session.RecentAction,

                        // The roster's "this one wants you" flag, spent on the
                        // channel that already means it. NeedsInput dims the orb
                        // as well as marking it, which reads oddly at first for
                        // something asking for attention — but it is the same
                        // treatment a local background job holding a question
                        // gets, the "?" is the loud half of that pair, and a
                        // second presence value meaning almost this one is how
                        // two orbs end up saying the same thing differently.
                        Presence = session.NeedsAction
                            ? OrbPresence.NeedsInput
                            : OrbPresence.Present,
                    },

                    // The session's own last activity, never `now` — a scan
                    // entry's stamp is a claim about when something last
                    // happened, and the headless-drop diagnostic prints it as
                    // one. Inventing a time would make every cloud session log
                    // as having been worked in this second.
                    //
                    // **The argument this used to carry was the wrong one, and
                    // it had a bug behind it (CB-182):** that stamping real
                    // activity is what lets the user's "Keep orbs for" setting
                    // filter these, on the strength of the account API listing
                    // 578 rows. Those 578 are the *unfiltered* roster; 573 of
                    // them are `bridge` rows that ClaudeCloudRoster.Keep drops
                    // long before this loop sees one, so the volume being
                    // defended against is a set that never reaches the sweep.
                    // What does reach it is a handful of non-archived cloud
                    // sessions — five on that account, one of them not archived
                    // — and letting a lifetime clock filter those took the orb
                    // away from the only one there was. JudgeLiveness exempts
                    // cloud sessions from expiry outright now, so nothing on
                    // this path reads the stamp as a retention decision any
                    // more; it stays because it is true.
                    session.LastActivity));
            }

            // Before InheritTerminalInfo, so this describes the files as the
            // hooks wrote them. It happens to be immune to the donation — pid and
            // source are not among the fields moved — but this ordering is the one
            // that needs no argument.
            var sharingAPid = SharingAPid(found);

            // Whether anything on this machine is worth spending a subprocess on.
            // Decided over the whole set before any of it is judged, so it cannot
            // depend on which file the directory happened to enumerate first. The
            // three shapes are in SessionPresence.WorthAskingTheDaemon.
            var worthAsking = found.Any(e => SessionPresence.WorthAskingTheDaemon(
                e.Status, KnowsATerminal(e.Status), sharingAPid.Contains(e.SessionId)));

            // A tmux pane is reusable infrastructure, not a session identity.
            // Before ordinary same-pid inheritance can spread a recorded pane,
            // ask the pane's live Claude child which session it is actually
            // running. A positive mismatch is enough to remove the obsolete
            // claimant when this scan also has the current owner; an absent or
            // ambiguous answer changes nothing.
            //
            // The answers were gathered before this method was entered, off the
            // UI thread and in one listing (see ScanProbes); this only reads
            // them.
            var stalePaneClaims = ReconcileTmuxPaneClaims(found, entry =>
                probes.PaneOwner(entry.Status));
            found.RemoveAll(entry => stalePaneClaims.Contains(entry.SessionId));

            InheritTerminalInfo(found);

            // One listing for the whole pass, where this used to ask three
            // separate times — and still only if something asks at all.
            //
            // The old shape was three calls to BackgroundJobs.IsLiveJob, each
            // going through a ten-second cache while the scan runs every two —
            // so the cache could expire between the superseded check and the
            // reachability one and the same session could be a live job for the
            // first and not for the second. Nothing visible came of that while
            // every caller wanted the same bool. It stops being harmless now
            // that the listing also decides whether an orb is *dimmed*, because
            // then one pass can keep an orb and describe it wrongly in the same
            // breath.
            //
            // Only when something asks, though, and that is not a
            // micro-optimisation: fetching it on every pass runs `claude agents
            // --json` as a subprocess for every machine, including one with
            // nothing but ordinary terminal sessions on it — which the rules
            // below never ask the daemon about, deliberately (see
            // JudgeReachability's own note that "an ordinary session never pays
            // for the lookup"). Fetching it unconditionally also broke both scan
            // suites the moment it landed, which is a fair description of what
            // it would have done to a quiet machine.
            //
            // That decision used to be a lazy closure here, which put the
            // subprocess on the UI thread whenever the ten-second cache behind
            // it had run out. It is ScanProbePlan's now, made from the files
            // before this method runs, and the listing arrives already fetched
            // off-thread. The three readers below are the three cases that
            // plan's gate names.
            var jobs = probes.Jobs;

            Func<string, bool> isLiveJob = id => BackgroundJobs.IsLive(jobs, id);

            // Who is sitting in a job, asked once per pass and only when the
            // answer could change a rendering — which is to say only when
            // something here is a background session at all. Cached behind the
            // seam as well (five seconds, one `ps`), so the cost of the question
            // is per-machine rather than per-session.
            //
            // Null is a scan that could not be done, which is a third answer and
            // not the same as "nobody is attached" —
            // SessionPresence.HasAttachClient is where that direction is decided
            // and why.
            //
            // Null unless worthAsking, by construction: ScanProbePlan's attach
            // gate is worthAsking itself, computed from the same files, so the
            // scan never had the answer to throw away.
            var attachClients = probes.AttachClients;

            var superseded = Superseded(found, isLiveJob);

            // Sessions that live agents name as their lead. Used for two things
            // below, and for nothing else — in particular *not* to excuse a
            // lead from the lifetime timer, which is the user's setting to make.
            var leadsWithLiveAgents = new HashSet<string>(StringComparer.Ordinal);

            // Every session this scan can actually see: the ids that survived the
            // superseded tie-break and whose process is still answering. Read by
            // the orphan rule below — a team member whose lead is not in here and
            // is not a job the daemon still lists is answering to something that
            // has gone.
            //
            // Folded into the pass that was already walking `found` and already
            // asking both of those questions, rather than added as a fourth
            // traversal: this loop costs a kill(2) per entry, and the point of
            // doing it once is that they agree with each other.
            var presentIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in found)
            {
                if (superseded.Contains(entry.SessionId)) continue;
                if (!ProcessLiveness.IsRunning(entry.Status.SessionPid)) continue;

                presentIds.Add(entry.SessionId);

                // From the background half's read, never AgentTeam itself: on
                // Windows that is a WMI query (CB-212, see ScanProbes.TeamOf).
                var agentLead = probes.LeadOf(entry.Status.SessionPid);
                if (!string.IsNullOrEmpty(agentLead) && agentLead != entry.SessionId)
                {
                    leadsWithLiveAgents.Add(agentLead);
                }
            }

            // Note on what is deliberately *not* here: agent teams get no
            // exemption from the lifetime timer, in either direction. A quiet
            // lead is pruned like any other quiet session, and so is a member
            // whose agent has finished and gone silent — even though its
            // process is still alive and its file's mtime is frozen because
            // nothing fires a hook for a session that isn't doing anything.
            //
            // Both were tried and taken back out. "Keep orbs for" is the user's
            // statement about how long a quiet session stays on screen, and a
            // team is not a special enough case to quietly overrule it; a team
            // that should stay visible is a reason to set a longer lifetime,
            // not a reason for the app to keep its own exceptions. The visible
            // consequence is that a team shows the agents that are working,
            // and an arrow disappears with the lead it pointed at.

            // Files this scan has evidence are over — collected here rather than
            // in the sweep below because the evidence is a by-product of a loop
            // that is already computing it, and because two of the three places
            // it can be found are followed immediately by `continue`.
            var dead = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (sessionId, status, written) in found)
            {
                // What the daemon says about this session — read from the pass's
                // own listing, for every Claude Code session, once anything on
                // the machine has made that listing worth fetching.
                //
                // The gate decides whether to *spend the subprocess*, not whether
                // to read the answer. Once some session has paid for it a lookup
                // is a dictionary hit, and asking about everything is what closes
                // the one hole the gate cannot: the hook interpolates
                // $TERM_PROGRAM out of its environment, so a daemon started from
                // inside a terminal passes it down to every job under it, and
                // those files name a terminal in every field while still being
                // jobs the daemon knows about.
                //
                // On a machine with nothing background-ish on it every session
                // keeps Unknown, which is the answer that means "nobody asked"
                // and which no rule below acts on.
                //
                // A session this machine's daemon cannot know about keeps
                // Unknown too, however readable the listing is — a WSL session
                // seen from Windows is never on it, and reading its absence as
                // NotAJob dropped every one of them. See
                // SessionPresence.LocalDaemonCanAnswerFor.
                var phase = worthAsking && status.Source == SessionSource.ClaudeCode
                            && SessionPresence.LocalDaemonCanAnswerFor(status, _onWindows)
                    ? BackgroundJobs.Phase(jobs, sessionId)
                    : JobPhase.Unknown;

                // Whether this file is the husk a handoff to a background job
                // leaves behind — a closure so that only a session the earlier
                // rules kept ever pays the stat, gated in
                // SessionPresence.CouldBeABackgroundedHusk (which is also what
                // keeps the fork itself, listed by the daemon as a live job,
                // from reading its own inherited marker).
                //
                // CB-22: read that gate for what it actually admits, not for
                // "husks". NotAJob or Unknown passes it, and on a machine with
                // nothing background-ish running — no `worthAsking` case above
                // — every ClaudeCode session reads Unknown, so every live
                // session with a transcript path pays this stat every scan,
                // ordinary terminal sessions included, not only a genuine
                // husk. That is CB-20's own doing (the gate used to require a
                // daemon-confirmed job), reasoned at the time to be
                // sub-millisecond and left unmeasured. HuskScanCostTests now
                // measures it: 15 live sessions with transcripts named, one of
                // them mid-generation so its cached answer can never be
                // reused, scanned 300 times on the Mac this was written on —
                // 1.1664-1.1665ms/scan against 1.0211-1.0236ms/scan for the
                // same fifteen with no transcript path at all, so roughly
                // 0.14-0.15ms/scan is this check, comfortably inside the
                // two-second poll interval. Left ungated rather than
                // restricted to a narrower status-file state, on the strength
                // of that number rather than the original estimate.
                //
                // Two sources, asked cheapest-and-surest first. SessionPark
                // reads Claude Code's own session record, which names the job
                // that took the conversation and is cleared when the window
                // gets it back — it is the fact rather than a trace of it, and
                // it is the only one of the two that catches an *idle*
                // conversation forked by opening agents mode, which writes no
                // transcript marker at all (CB-30). TranscriptHandoff stays
                // because it answers for a case the record cannot: a session
                // whose process is gone leaves no record to read, and its
                // transcript still says what happened.
                Func<bool> handedToBackground = () =>
                    SessionPresence.CouldBeABackgroundedHusk(status, phase)
                    && (SessionPark.IsParked(status.SessionPid, sessionId)
                        || TranscriptHandoff.EndsBackgrounded(status.TranscriptPath, sessionId));

                // Two verdicts rather than one, with the viewer hunt sitting
                // between them, because the order is load-bearing in both
                // directions: adoption can hand a background lead the very
                // terminal JudgeReachability is about to ask for, and it walks
                // the process table to do it, which is far too expensive to
                // spend on a session JudgeLiveness has already dropped.
                var liveness = JudgeLiveness(sessionId, status, written, now, StaleAfter,
                                             superseded, ProcessLiveness.IsRunning,
                                             handedToBackground);

                // Asked before the drop below rather than after, and of the
                // liveness verdict rather than the reachability one, because the
                // two facts that count as evidence arrive at different points: a
                // gone process is this verdict, and a finished job is the phase,
                // whose orb is dropped further down for having no terminal. See
                // SessionPresence.EvidenceOfDeath for why nothing else counts.
                if (SessionPresence.EvidenceOfDeath(liveness, phase)) dead.Add(sessionId);

                if (liveness != ScanVerdict.Keep)
                {
                    continue;   // removed in the pass below
                }

                if (WantsAgentViewer(sessionId, status, leadsWithLiveAgents))
                {
                    probes.AdoptViewer(status);
                }

                if (JudgeReachability(sessionId, status, leadsWithLiveAgents, phase, File.Exists)
                        != ScanVerdict.Keep)
                {
                    continue;   // removed in the pass below
                }

                seen.Add(sessionId);

                // Whether this session is an agent-team member, and whose. Read
                // from its process rather than its status file — see AgentTeam
                // — by the background half, which asked about every pid in the
                // pass in one read (CB-212). This only looks the answer up.
                var membership = status.Source == SessionSource.ClaudeCode
                    ? probes.TeamOf(status.SessionPid)
                    : AgentTeam.None;

                // Guarded, where it used to run for everything. A gateway
                // session has no process to ask, so `default` came back and this
                // assigned Lead = "" — which silently erased the room a channel
                // session had already been put in, a few lines earlier and in
                // another file. Teams and rooms are different things that happen
                // to use the same field.
                if (status.Source == SessionSource.ClaudeCode)
                {
                    status.Lead = membership.Lead == sessionId ? "" : membership.Lead;
                    status.Agent = string.IsNullOrEmpty(status.Lead) ? "" : membership.Name;
                }

                // What shape of session this is, and whether anything is on the
                // other end of it. After the team membership above, deliberately:
                // the orphan rule reads Lead, and Lead is only known once that
                // block has run.
                //
                // Claude Code's alone, because both inputs are: the job listing
                // is the local daemon's, and agent teams are a Claude Code
                // concept. Everything else keeps the defaults, which are the
                // shape and the presence that change nothing about the orb.
                if (status.Source == SessionSource.ClaudeCode)
                {
                    status.Shape = SessionPresence.ShapeOf(status, phase);

                    var hasLead = !string.IsNullOrEmpty(status.Lead);
                    status.Presence = SessionPresence.PresenceOf(
                        status.Shape, status.State, phase,
                        hasLead && presentIds.Contains(status.Lead),
                        hasLead && isLiveJob(status.Lead),
                        SessionPresence.HasAttachClient(attachClients, sessionId));

                    // The badge, which says what this session *is*. Assigned
                    // here rather than in OrbWindow because Kind is the one
                    // channel every source shares — a gateway session's comes
                    // from the gateway, a bridged one's from the bridge, and a
                    // background job's from the daemon — and the orb draws
                    // whichever it is handed from one switch.
                    if (status.Shape == LocalSessionShape.Background)
                    {
                        status.Kind = SessionKind.Background;
                    }
                }

                // The colour Claude Code gave this agent when the team was
                // built. Only used when the session hasn't set one itself: a
                // `/color` run inside the agent is a deliberate choice and
                // outranks the assigned one. This is not the automatic per-
                // process accent the README declines to guess at — that one is
                // nowhere on disk, whereas this was passed to the process on
                // its command line and is what Claude Code's own team UI shows.
                if (string.IsNullOrEmpty(status.Color)) status.Color = membership.Color;

                // Joining or leaving a team changes where this orb belongs in
                // the stack, not just what it looks like, so it has to reflow.
                // It isn't a set change and would otherwise go unnoticed — a
                // team member can appear before its lead does.
                if (_statuses.TryGetValue(sessionId, out var previous)
                    && previous.Lead != status.Lead)
                {
                    setChanged = true;
                }

                // Who this session says it is, from the CLAUDE.md files beside
                // its work. Before UpdateFrom, deliberately: the orb's label and
                // its portrait both read the registry this fills, so doing it
                // after would draw one tick of the old answer every time a
                // persona changed.
                ApplyPersona(sessionId, status, candidatesByCwd);

                // Immediately before the dictionary is overwritten, so the
                // tracker is comparing against exactly the state this
                // session was in on the previous tick — the same
                // TryGetValue(_statuses, ...) above is reading, just for a
                // different question. After the liveness and reachability
                // continues above, which is what makes CB-167's "husks can't
                // chime" guarantee true by construction: a session that
                // never reaches this line is never observed at all.
                var turnSignal = _turnSignals.Observe(sessionId, status.State);
                if (turnSignal != TurnSignal.None)
                {
                    turnSoundEvents.Add(new TurnSoundEvent(
                        turnSignal, SoundKeyFor(status, sessionId), sessionId));
                }

                _statuses[sessionId] = status;

                var isNew = !_windows.TryGetValue(sessionId, out var window);
                if (isNew)
                {
                    window = new OrbWindow(sessionId);
                    window.SizeRelayoutRequested += ReapplyOrbSizes;
                    _windows[sessionId] = window;
                    _order.Add(sessionId);
                    if (OrbsVisible) window.Show();
                    setChanged = true;
                }

                window!.UpdateFrom(status);

                // The chat panel's view of "waiting for a permission prompt"
                // comes from here rather than from the transcript, because the
                // dialog never reaches the transcript. This is the only place
                // that state arrives.
                if (_chats.TryGetValue(sessionId, out var chat)) chat.UpdateStatus(status);

                // The same push for an open cloud panel, from the roster row the
                // orb was drawn from — so Stop and the composer follow the
                // session's real state without the panel spending a request.
                if (_cloudChats.TryGetValue(sessionId, out var cloudChat)) PushCloudStatus(cloudChat, sessionId);

                // After UpdateFrom, so the window is already showing something
                // if the position turns out to be unusable. Before the reflow
                // below, which steps over whatever this pins.
                if (isNew) RestoreOrbPosition(window, status);

                // After RestoreOrbPosition, which is what fills in
                // window.PositionKey — the same key a pinned panel's saved
                // spot is filed under, for the same reason ChatPanelSizes
                // shares it with OrbPositions.
                if (isNew) RestorePinnedChatPanel(window, window!.PositionKey);
            }

            var gone = _windows.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (var id in gone)
            {
                _windows[id].Close();
                _windows.Remove(id);
                _statuses.Remove(id);
                _order.Remove(id);
                setChanged = true;

                // The watcher goes with the orb. A session that has ended has a
                // transcript that will never grow again, and a FileSystemWatcher
                // per dead session is a handle leak measured in days.
                if (_chats.Remove(id, out var chat)) chat.Dispose();

                // A cloud panel is kept (see _cloudChats), but it hears that its
                // row has gone — the orb can go for reasons of its own, so the
                // roster is asked directly rather than the orb's absence trusted.
                if (_cloudChats.TryGetValue(id, out var goneCloud)) PushCloudStatus(goneCloud, id);

                // Same argument for the persona, one size up: a decoded portrait
                // is a Bitmap per frame, held by a process-wide cache that has
                // no other reason to ever let one go. The registry entry is
                // small; the picture behind it is not, and Forget drops both.
                _personas.Remove(id);
                LocalPersonas.Forget(id);
                PeerPersonas.Forget(id);
            }

            // Drops anything this pass never saw, so a session that vanished
            // mid-generation and comes back later starts from a baseline
            // rather than being compared against whatever it was doing when
            // it dropped out of sight. After the removal pass above rather
            // than before it, though the two do not actually interact: this
            // just keeps every "after the loop, once" step grouped together.
            _turnSignals.Prune(seen);

            // QA (CB-167): a deferred signal still waiting out the rate
            // limit for a session that has just dropped out of `seen` — a
            // backgrounded husk, one genuinely gone — must not fire once its
            // two seconds are up. The signal was real when it was raised,
            // but by the time the timer would fire the session it was about
            // is no longer one the scan is willing to say anything about.
            TurnSounds.CancelPendingUnlessSeen(seen);

            // One sound for the whole pass, decided from everything the loop
            // above noticed. The callback is how this reaches an orb without
            // TurnSounds ever holding a window reference of its own — see its
            // own header comment. Async and bool-returning (QA, CB-167): the
            // window is the only thing that can say whether it actually had
            // something to speak, and TurnSounds needs that answer, off this
            // thread, to decide whether to fall back to a chime.
            //
            // The dictionary lookup itself goes through Dispatcher.UIThread.
            // InvokeAsync rather than reading _windows directly, because this
            // callback is not always called from the UI thread: a deferred
            // decision (the rate-limit gap was still closed) fires later off
            // TurnSounds' own timer, and _windows is otherwise only ever
            // touched from the scan itself. Reading it from a background
            // thread at the same moment a live scan mutates it is exactly
            // the kind of race that would show up once in a great while and
            // be unreproducible.
            //
            // currentStateFor (QA round 2, finding 5) exists for the same
            // reason: a deferred event's session may have moved on — reset,
            // pruned, or an attention resolved some way other than the
            // reactive Cancel calls above catch — by the time TurnSounds'
            // timer actually fires, and this is how FirePending asks "does
            // that still hold" instead of trusting whatever was true when
            // the event was first deferred. TurnSounds only ever calls this
            // off its own timer thread, never the UI thread, so it dispatches
            // the same way the summary callback above does — synchronously
            // here rather than awaited, since FirePending's validation loop
            // is itself synchronous and a Dictionary this scan owns is not
            // safe to read from a second thread without going through it.
            TurnSounds.Deliver(turnSoundEvents, async id =>
            {
                var window = await Dispatcher.UIThread.InvokeAsync(
                    () => _windows.TryGetValue(id, out var w) ? w : null);
                if (window is null) return false;
                return await window.SpeakTurnSummaryAsync().ConfigureAwait(false);
            }, currentStateFor: id => Dispatcher.UIThread.Invoke(
                () => _statuses.TryGetValue(id, out var s) ? s.State : null), now: now);

            // After the removal pass, so an orb has already gone before its file
            // does and the two never disagree on screen. Inside the scan rather
            // than on a timer of its own, which would race this one over the same
            // directory and the same dictionary.
            SweepDeadFiles(found, dead, now);

            // Before the reflow, so an orb that just arrived is arranged in the
            // group its team is already in.
            SyncOrbGroups();

            if (setChanged)
            {
                ReflowPositions();
            }

            RescueOffscreenOrbs();

            // Not only on setChanged: an orb that was already on screen can
            // gain or lose a lead, and the arrows have to follow either way.
            UpdateTeamLinks();

            UpdateTray();
        }

        // Delete the status files of sessions that are demonstrably over.
        //
        // Until this existed, nothing but the SessionEnd hook's own `rm -f` ever
        // removed one — and SessionEnd only fires on a graceful exit. So a
        // Ctrl+C'd session's file stayed for good, and a finished background
        // job's stayed *and could never be caught*: its pooled worker is kept
        // alive on purpose, so the pid answers forever and no liveness rule can
        // reach it. Six dead-pid files and a file per finished job were sitting
        // in one real status directory when this was written, and with "Keep orbs
        // for" set to Forever nothing in the app would ever have removed any of
        // them. Each one is a session the app has to read, judge and consider
        // drawing on every tick, and each is a potential orb for a conversation
        // that ended days ago.
        //
        // Three rules keep the delete safe, and all three are worth stating
        // because the thing on the other side of them is File.Delete:
        //
        // - Only file-backed local sessions. A gateway or bridged session has no
        //   file, and the path this would build from its namespaced key is not
        //   one this app should be writing to at all.
        // - Only on evidence — see SessionPresence.EvidenceOfDeath. A superseded
        //   file is deliberately never swept: its pid is alive by construction,
        //   and an Agent View pid legitimately hosts several live sessions at
        //   once, so "not the newest for this pid" says nothing about whether the
        //   session is over.
        // - Only after the grace period, timed from when *this process* first
        //   saw the evidence. Evidence that goes away resets the clock, which is
        //   what makes a resumed job immune.
        //
        // Self-correcting in the one direction it can be wrong: if a session this
        // deleted a file for turns out to still be running, its next hook event
        // writes the file again and the orb comes back.
        //
        // One consequence of the superseded rule worth stating, because it looks
        // like a bug from the outside: a terminal that was /cleared several times
        // and then killed leaves a trail of files on one dead pid, and Superseded
        // outranks ProcessGone, so only the newest of them is ever evidence. The
        // trail therefore drains one file per grace period rather than all at
        // once. Slow, self-healing, and cheaper than teaching the sweep to
        // second-guess which of several verdicts a file "really" deserved.
        private void SweepDeadFiles(List<ScanEntry> found, ISet<string> dead, DateTime now)
        {
            foreach (var (sessionId, status, _) in found)
            {
                if (!status.IsLocalCli) continue;

                if (!dead.Contains(sessionId))
                {
                    // Whatever was wrong with it has stopped being wrong. The
                    // grace period starts over from the next sighting rather
                    // than carrying on from where it left off, which is what
                    // stops a job that resumes every few minutes from being
                    // swept by the accumulated total of its quiet spells.
                    _deadSince.Remove(sessionId);
                    continue;
                }

                if (!_deadSince.TryGetValue(sessionId, out var since))
                {
                    _deadSince[sessionId] = now;
                    continue;
                }

                if (!SessionPresence.SweepDue(since, now, SweepGrace)) continue;

                DeleteStatusFile(sessionId);
                _deadSince.Remove(sessionId);
            }

            // Ids whose file has gone — swept here, deleted by SessionEnd, or
            // removed by hand — are forgotten too. Without this the dictionary
            // is a slow leak keyed on every session the app has ever seen die.
            if (_deadSince.Count == 0) return;

            var present = found.Select(e => e.SessionId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _deadSince.Keys.Where(id => !present.Contains(id)).ToList())
            {
                _deadSince.Remove(id);
            }
        }

        private void UpdateTray()
        {
            // Feed the tray in stacking order so the menu reads top-to-bottom
            // like the orbs do on screen.
            _tray?.Update(DisplayOrder()
                .Select(id => new TrayController.SessionEntry(id, _statuses[id]))
                .ToList());
        }

        // --- agent teams ------------------------------------------------------

        // Stacking order with each team gathered: a lead, then its members,
        // then whatever came next. _order stays in first-seen order and is
        // still the tie-breaker between teams — this only moves members up
        // behind their lead, which keeps a team's arrows short and stops them
        // crossing the unrelated sessions that happened to appear in between.
        //
        // A member whose lead isn't on screen (ended, or filtered out) is left
        // exactly where it was: there's nothing to gather it under.
        private List<string> DisplayOrder() =>
            GatherTeams(_order, _statuses.ContainsKey,
                        id => _statuses.TryGetValue(id, out var status) ? status.Lead : null);

        // The gathering itself, with the dictionaries behind it reduced to two
        // questions: is this id still tracked, and what does it say its lead is?
        //
        // Two questions and not one, which is the whole shape of it. "Tracked"
        // and "names a lead" are separate facts and this rule turns on both: an
        // id in _order with no status behind it — which happens for one pass
        // during the removal pass — is not laid out at all, while a member
        // whose *lead* has ended stays exactly where it was, because there is
        // nothing to gather it under. Folding them into one nullable answer
        // reads tidier and drops an orb: SessionStatus.Lead is itself allowed
        // to be null for a session with no pid to ask about, and a null lead
        // would then be indistinguishable from a session that is gone.
        //
        // Separated out so the ordering can be checked without windows,
        // statuses or a scan behind it; the shape it produces is what decides
        // which orb sits where in the stack and how far a team's arrows have to
        // reach.
        internal static List<string> GatherTeams(
            IReadOnlyList<string> order, Func<string, bool> tracked, Func<string, string?> leadOf)
        {
            var byLead = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var id in order)
            {
                if (!tracked(id)) continue;

                var lead = leadOf(id);
                if (string.IsNullOrEmpty(lead) || lead == id) continue;   // leads nobody, or itself
                if (!tracked(lead)) continue;                             // its lead isn't on screen

                if (!byLead.TryGetValue(lead, out var members))
                {
                    byLead[lead] = members = new List<string>();
                }
                members.Add(id);
            }

            var gathered = new HashSet<string>(byLead.Values.SelectMany(m => m), StringComparer.Ordinal);

            var gatheredOrder = new List<string>(order.Count);
            foreach (var id in order)
            {
                if (!tracked(id)) continue;
                if (gathered.Contains(id)) continue;   // emitted under its lead below

                gatheredOrder.Add(id);
                if (byLead.TryGetValue(id, out var members)) gatheredOrder.AddRange(members);
            }

            // A team whose lead is itself a member of another team would leave
            // its members unemitted above; nesting isn't a thing today, but a
            // dropped orb would be a silent one, so sweep up anything missed.
            foreach (var id in order)
            {
                if (tracked(id) && !gatheredOrder.Contains(id)) gatheredOrder.Add(id);
            }

            return gatheredOrder;
        }

        // The last known state of another session — what an orb needs to hand
        // its team lead to TerminalFocuser. Null for a session that isn't
        // tracked, including the empty id a non-member passes in.
        // A session this orb can hold a conversation with, or null when its
        // click means something else. Null is the whole of the "not one of
        // those" signal the orb understands — it deliberately knows nothing
        // about which source a session came from, or whether the feature is on.
        public IRemoteChatSession? RemoteChatFor(string sessionId)
        {
            if (!_statuses.TryGetValue(sessionId, out var status)) return null;

            // A room's conversation is its members' transcripts merged, since
            // the gateway has no room to ask about. Assembled from the same
            // Lead field the arrows are drawn from, so what opens is exactly
            // what the orb is pointing at.
            if (status.IsRoom)
            {
                // Asked of the gateway's own list rather than assembled from
                // the orbs on screen. The orbs are filtered by "show sessions
                // active within", which is a question about what is worth
                // drawing — and an agent that spoke an hour ago is still in the
                // room. Built from the orbs, its half of the conversation went
                // missing and its messages showed up as yours.
                const string RoomPrefix = "openclaw:room:";
                var members = OpenClawSessions.MembersOfRoom(sessionId[RoomPrefix.Length..]);

                return OpenClawSessions.RoomChatFor(sessionId, status.Title, members);
            }

            if (status.Source == SessionSource.OpenClaw)
                return OpenClawSessions.ChatFor(sessionId, status.Title);

            // A session on another machine. Cached like the local ones below,
            // and for a stronger reason: this conversation exists *only* here.
            // A local panel rebuilt from scratch re-reads the transcript and
            // loses nothing but scroll position, whereas rebuilding this one
            // would throw away the entire exchange — there is no file on this
            // machine to read it back from.
            if (status.Source == SessionSource.RemoteControl)
            {
                if (_remoteChats.TryGetValue(sessionId, out var existingRemote)) return existingRemote;

                // The peer name, recovered from the id the scan minted. Taken
                // from the id rather than the title so it survives a title that
                // gets prettied up later — the name is what SendMessage
                // addresses, and it has to stay exact.
                // "rc:<account>:<name>". Split from the right, because a session
                // name can itself contain a colon and an account directory
                // cannot — so the *first* separator after the prefix is the one
                // that divides them.
                var rest = sessionId.StartsWith("rc:", StringComparison.Ordinal) ? sessionId[3..] : "";
                var split = rest.IndexOf(':');
                var account = split > 0 ? rest[..split] : OrbweaverSettings.DefaultRemoteControlProfileDir;
                var remoteName = split > 0 ? rest[(split + 1)..] : status.Title;

                var remote = new RemoteControlChatSession(sessionId, account, remoteName);
                _remoteChats[sessionId] = remote;


                return remote;
            }

            // A session in Anthropic's cloud. Cached for the reason the
            // remote-control branch above gives and then some: there is no file
            // on this machine to rebuild it from *and* re-reading costs a
            // network round trip against a rate-limited endpoint, so a rebuilt
            // panel would be slower as well as emptier.
            if (status.Source == SessionSource.ClaudeCloud)
            {
                // The roster row, which is what the session is constructed from
                // — it carries the title and the id in the shape the events
                // endpoint wants. Matched on the same "cloud:" key the scan
                // minted rather than by slicing the prefix off, so the two
                // cannot drift apart.
                var row = ClaudeCloudSessions.Snapshot()
                    .FirstOrDefault(s => "cloud:" + s.Id == sessionId);

                if (_cloudChats.TryGetValue(sessionId, out var existingCloud))
                {
                    // Re-read on every open. Reconciliation is by uuid, so this
                    // is the refresh path rather than a second way in: a turn
                    // that grew while the panel was shut updates in place, and
                    // nothing is duplicated.
                    //
                    // **A panel left open does not refresh itself**, and that is
                    // a real gap rather than an oversight being hidden. The only
                    // ticker available here is the two-second scan, and pointing
                    // a rate-limited events endpoint at it would spend the
                    // account's budget on a window nobody is looking at.
                    //
                    // CB-199 narrowed that gap without closing it: while a turn
                    // runs in an open panel — one sent from it, or one the roster
                    // reports — the session watches it live for a bounded time,
                    // and the scan's push below (PushCloudStatus) triggers one
                    // read when a turn ends. An idle panel still reads only on
                    // open.
                    //
                    // And re-pointed at the login that owns the session *now*:
                    // ownership can move between polls (see MergeAccounts), and
                    // a panel cached under the old owner would otherwise keep
                    // reading, and later sending, with the wrong account.
                    if (row is not null) existingCloud.UseCredentials(CloudChatCredentialsFor(row.OwnerRoot));

                    // The row is re-read too, so a session that was archived or
                    // deleted while its panel was shut opens read-only rather than
                    // offering a box the server will refuse.
                    PushCloudStatus(existingCloud, sessionId);
                    StartCloudLoad(existingCloud);
                    return existingCloud;
                }

                // No row and no cached session means the roster has dropped it —
                // the orb is on its way out, and there is nothing to read.
                if (row is null) return null;

                var cloud = new ClaudeCloudChatSession(row, CloudChatApi, CloudChatCredentialsFor(row.OwnerRoot))
                {
                    Stream = CloudChatStream,
                };
                _cloudChats[sessionId] = cloud;
                StartCloudLoad(cloud);
                return cloud;
            }

            // Both local CLIs from here down. Which transcript format to read
            // and which pair of settings governs it is the whole of the
            // difference, and it lives in CliChatFormat.
            if (!CliChatFormat.For(status.Source).ChatEnabled()) return null;

            // Cached rather than made per click: the session owns a file watcher
            // and a byte offset into a transcript, and rebuilding it every time
            // the panel opened would re-read the tail and lose the scrollback
            // someone had already paged in.
            if (_chats.TryGetValue(sessionId, out var existing))
            {
                existing.UpdateStatus(status);
                return existing;
            }

            // CB-266: the shared cloud client and credential lookup, so a bridged
            // Claude Desktop chat sends through the same HttpClient and Keychain
            // consent surface as the cloud panels. Neither is touched until a
            // send, and "Show cloud sessions" gates that.
            var chat = new LocalCliChatSession(sessionId, status,
                cloudApi: CloudChatApi, credentialsFor: CloudChatCredentialsFor);
            _chats[sessionId] = chat;
            chat.Start();
            return chat;
        }

        // Local chat sessions, by session id. Only ever populated by a click —
        // there is no reason to watch a transcript nobody is reading — and
        // emptied with the orb.
        private readonly Dictionary<string, LocalCliChatSession> _chats = new(StringComparer.Ordinal);

        // Separate from _chats above because these are not disposable and are
        // not keyed to a status file's lifetime. A remote conversation outlives
        // the orb: the bridge idling out empties the snapshot and the orb goes,
        // but what was said should still be there when it comes back.
        private readonly Dictionary<string, RemoteControlChatSession> _remoteChats =
            new(StringComparer.Ordinal);

        // Cloud chat sessions, kept for the same reason the remote ones above
        // are: the transcript came over the network and there is nothing on this
        // disk to read it back from.
        private readonly Dictionary<string, ClaudeCloudChatSession> _cloudChats =
            new(StringComparer.Ordinal);

        // One HTTP client and one credential source for every cloud panel, not
        // one each. HttpCloudApi owns an HttpClient, which .NET wants reused, and
        // on macOS the credential source is a Keychain read — a second one per
        // panel would be a second consent surface for the same item.
        //
        // Built on first use rather than in the constructor, so a Buddy with the
        // feature switched off never constructs either. Excluded from coverage
        // for what they are rather than what they decide: an HttpClient and, on
        // this platform, a Keychain query. Which of the two credential sources
        // SourceFor returns is covered where it lives.
        [ExcludeFromCodeCoverage]
        private ICloudApi CloudChatApi => _cloudChatApi ??= new HttpCloudApi();

        // One live-event stream client for every cloud panel, for CloudChatApi's
        // reason: it owns an HttpClient. Each open panel holds one connection on
        // it, and only while it is open (CB-199). A test that goes through the
        // seam below has chosen its stream, a null one included, so no test can
        // reach this real one by forgetting to.
        [ExcludeFromCodeCoverage]
        private ICloudEventStream? CloudChatStream
        {
            get
            {
                if (!_cloudChatStreamChosen)
                {
                    _cloudChatStream = new HttpCloudEventStream();
                    _cloudChatStreamChosen = true;
                }

                return _cloudChatStream;
            }
        }

        private ICloudEventStream? _cloudChatStream;
        private bool _cloudChatStreamChosen;

        // The credential for a session's *owner*. Several accounts can be
        // listed, and a cloud session belongs to whichever one created it: sending
        // with another's login is at best refused and at worst reads a
        // conversation as the wrong person. The instances come from CloudAccounts,
        // the same ones the poll loop uses.
        private ICloudCredentialSource CloudChatCredentialsFor(string? ownerRoot) =>
            _cloudChatCredentialsFor(ownerRoot);

        private ICloudApi? _cloudChatApi;
        private Func<string?, ICloudCredentialSource> _cloudChatCredentialsFor = CloudAccounts.SourceFor;

        // The only way into RemoteChatFor's ClaudeCloud arm from a test.
        //
        // The properties above build the real thing on first use: HttpClients
        // pointed at api.anthropic.com — one of them holding a live event stream
        // open — and, on this platform, a Keychain query that puts a consent
        // dialog in front of whoever is running the suite. None is something a
        // headless run may do, so the arm that constructs a cloud session was
        // unreachable and therefore uncovered, which is what this seam is for.
        // It sets the same fields the properties memoise into,
        // so production still builds each of them exactly once and nothing about
        // the app's behaviour changes when nobody calls this.
        internal void UseCloudChatDependenciesForTests(
            ICloudApi api, ICloudCredentialSource credentials, ICloudEventStream? stream = null)
        {
            UseCloudChatDependenciesForTests(api, _ => credentials, stream);
        }

        internal void UseCloudChatDependenciesForTests(
            ICloudApi api, Func<string?, ICloudCredentialSource> credentialsFor, ICloudEventStream? stream = null)
        {
            _cloudChatApi = api;
            _cloudChatCredentialsFor = credentialsFor;
            _cloudChatStream = stream;
            _cloudChatStreamChosen = true;
        }

        // Hand an open cloud panel what the roster says about its session now.
        //
        // Skipped while the feature is off, because the snapshot is then empty
        // by construction and a missing row would read as a deleted session —
        // a false sentence in the one place that explains why the box went.
        private static void PushCloudStatus(ClaudeCloudChatSession chat, string sessionId)
        {
            if (!OrbweaverSettings.ClaudeCloudEnabled) return;

            chat.UpdateStatus(ClaudeCloudSessions.Snapshot()
                .FirstOrDefault(s => "cloud:" + s.Id == sessionId));
        }

        // Kick off the read and walk away.
        //
        // Not awaited, because RemoteChatFor is what a click calls and a click
        // must not block on a network round trip. The session publishes its own
        // state to the panel — Connecting until this finishes, then Connected or
        // Error — so there is nothing for a caller to do with the answer that the
        // panel is not already told.
        //
        // A false return means nothing could be read, and it is deliberately not
        // turned into an empty transcript: the session leaves itself in Error, and
        // the panel says so. An unreadable conversation and an empty one look
        // identical on screen, and only one of them is worth explaining.
        //
        // Excluded from coverage: its body is a fire-and-forget Task over a real
        // HTTP call. What it would exercise — LoadAsync's own arms — is covered
        // directly against a fake ICloudApi.
        [ExcludeFromCodeCoverage]
        private static void StartCloudLoad(ClaudeCloudChatSession session)
        {
            _ = Task.Run(() => session.LoadAsync(CancellationToken.None));
        }

        // Namespaced away from both Claude Code's UUIDs and the gateway's own
        // keys, because it is neither: nothing on the gateway answers to it.
        internal static string RoomId(string roomKey) => "openclaw:room:" + roomKey;

        // A member's title is "Aurora — general"; the room is just "general".
        internal static string RoomTitle(string sessionTitle)
        {
            var dash = sessionTitle.IndexOf(" — ", StringComparison.Ordinal);
            return dash > 0 ? sessionTitle[(dash + 3)..].Trim() : sessionTitle;
        }

        public SessionStatus? StatusFor(string? sessionId) =>
            string.IsNullOrEmpty(sessionId) ? null : _statuses.GetValueOrDefault(sessionId);

        // The orb window for a session, if the scan has created one — CB-168's
        // OpenClaw new-chat flow uses this to find the real, scan-owned orb
        // for a session it just asked the gateway to create, rather than
        // building its own OrbWindow: this dictionary is the only place an
        // orb is created and tracked, and a second one built outside it would
        // be a genuine duplicate the moment the next scan discovers the same
        // session and creates its own.
        public OrbWindow? OrbFor(string? sessionId) =>
            string.IsNullOrEmpty(sessionId) ? null : _windows.GetValueOrDefault(sessionId);

        // A snapshot copy, keyed by session id — CB-168's "New chat…" dialog
        // uses this twice: to seed its folder combo (RecentFolders.Merge
        // wants the live cwds) and to tell which orb, if any, is new once a
        // launch has gone out (NewChatOrbWatch wants the ids that already
        // existed). A copy rather than the live dictionary, the same
        // reasoning ClaudeCodeProfileDirs' own accessor gives: a caller
        // holding a reference to _statuses directly could observe — or in a
        // future change, mutate — state out from under the next scan.
        public IReadOnlyDictionary<string, SessionStatus> AllStatuses =>
            new Dictionary<string, SessionStatus>(_statuses);

        // Make this session's orb acknowledge a click that was answered without
        // creating anything.
        //
        // The chat panel's ⚙ shares its destination with the orb click through
        // RunFallback, so it has to share the acknowledgment too — otherwise the
        // one gesture whose success is invisible is invisible from exactly one of
        // the two places it can be made. The orb click needs no hop like this
        // because it already has itself; a chat session knows only its id.
        //
        // Silent when there is no orb: a session can be typed at from the panel
        // after its orb has gone, and there is nothing to flash.
        internal void AcknowledgeClickOn(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            if (_windows.TryGetValue(sessionId, out var window)) window.Acknowledge();
        }

        // Every tmux pane some *other* session's status file claims as its own.
        //
        // The disambiguator the pane-title viewer scan needs, and it needs it for
        // one branch only. A conversation title is shared by every member of an
        // agent team, so several panes can match one session's title — measured on
        // the machine this was built for, four panes with one identical title for
        // three sessions. Answering "the user is already looking at it" survives
        // that, because whatever the matched pane holds the user is reading it.
        // Answering "go to that pane" does not: focus the wrong same-titled pane
        // and it silently shows another conversation.
        //
        // A pane another session records is exactly the pane most likely to
        // collide, and excluding it costs nothing: a session that records its own
        // pane is reached by FocusCore long before any of this, so it never needs
        // the viewer path and never loses anything by being kept out of another
        // session's candidates.
        //
        // Answered here rather than by the scan itself, for the same reason
        // StatusFor is handed to TerminalFocuser rather than looked up inside it:
        // which files exist and what they claim is this class's knowledge, and
        // AgentTeamViewer deliberately depends on nothing in it.
        // Returns pane id to the claimant's own conversation title, because a
        // claim is only worth honouring while it is still true — see
        // SessionPresence.ClaimStillHolds, which the scan asks with the pane's
        // *current* title beside this. A claim that outlives the client that made
        // it pushes a click into making a duplicate, which is the very failure the
        // exclusion exists to prevent, arriving from the other side.
        public IReadOnlyDictionary<string, string> PaneClaimsByOthers(string? sessionId)
        {
            var claimed = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (id, status) in _statuses)
            {
                if (string.Equals(id, sessionId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(status.TmuxPane)) continue;

                // Last writer wins, and it does not matter which: two sessions
                // claiming one pane means at least one of them is stale, and the
                // corroboration step is what settles that — not the order this
                // dictionary happened to be built in.
                claimed[status.TmuxPane] = status.Title ?? "";
            }

            return claimed;
        }

        // One arrow per orb with a lead on screen. The lead is TeamTree's, so
        // a member whose own lead ended points at its nearest surviving
        // ancestor (CB-268 T4) — the orb it now fans off in the arrangement
        // and moves with in a drag — rather than losing its arrow.
        private void UpdateTeamLinks()
        {
            TeamLinks.SetVisible(OrbsVisible);

            var tree = Teams();
            var pairs = new List<(OrbWindow Member, OrbWindow Lead)>();
            foreach (var id in tree.Present)
            {
                if (tree.LeadOf(id) is not { } leadId) continue;
                pairs.Add((_windows[id], _windows[leadId]));
            }

            TeamLinks.Update(pairs);
        }

        public void SetOrbsVisible(bool visible)
        {
            // "Show orbs" means all of them, usage orbs included — and it says
            // so even when the session orbs are already where they were asked
            // to be, since the usage orbs may not be. Hence before the early
            // return rather than after it.
            SetUsageOrbsVisible(visible);

            if (OrbsVisible == visible) return;
            OrbsVisible = visible;
            OrbweaverSettings.ShowOrbs = visible;

            foreach (var window in _windows.Values)
            {
                if (visible) window.Show();
                else window.Hide();
            }

            // Arrows go with the orbs they join — two invisible orbs joined by a
            // visible arrow is a line from nowhere to nowhere.
            TeamLinks.SetVisible(visible);

            if (visible) ReflowPositions();
            UpdateTray();
        }

        // The account orbs alone. An account orb left floating over a desktop
        // somebody cleared with "Show orbs" would be the one thing that switch
        // failed to turn off, which is why SetOrbsVisible still calls this —
        // but hiding only these is its own switch, so the session orbs are
        // left exactly as they are.
        public void SetUsageOrbsVisible(bool visible)
        {
            if (UsageOrbsVisible == visible) return;
            UsageOrbsVisible = visible;
            OrbweaverSettings.ShowUsageOrbs = visible;
            _accountOrbs.SetVisible(visible);
            UpdateTray();
        }

        // A colour change isn't a session change, so nothing on the scan path
        // would notice one: ScanAndUpdate calls UpdateFrom, and UpdateFrom only
        // calls ApplyState when the state actually differs. Same shape as
        // SetOrbsVisible above — whoever changed the setting says so.
        //
        // Hidden orbs get walked too. They're still loaded windows, and they'll
        // be right when they come back.
        public void ReapplyStateColors()
        {
            foreach (var window in _windows.Values)
            {
                window.ReapplyStateColors();
            }

            _tray?.ReapplyStateColors();
        }

        // Turning the account orbs on or off, said out loud by whoever changed
        // the setting — the same convention as SetOrbsVisible and
        // ReapplyStateColors above, and for the same reason: nothing on the scan
        // path would otherwise notice, because a setting change is not a session
        // change. The decision about which CLI's orbs stay lives in
        // AccountOrbs.SyncToSettings; this used to look only at the Claude Code
        // flag, which is how turning Grok usage on could close the orbs instead.
        public void ReapplyAccountOrbs() => _accountOrbs.SyncToSettings(UsageOrbsVisible);

        // Same shape as ReapplyStateColors, for the "Two-letter initials"
        // toggle: a cosmetic setting change isn't a session change, so
        // nothing on the scan path would otherwise notice it until whatever
        // orb's session next fires a hook.
        public void ReapplyGlyphs()
        {
            foreach (var window in _windows.Values)
            {
                window.ReapplyGlyph();
            }
        }

        // CB-268: the Group outlines card and the group style dialog land here
        // when a stroke or fill changed, the same shape as ReapplyGlyphs. The
        // backdrops that draw the outlines are S5's and the wiring is S7's, so
        // until they exist there is nothing to repaint and this is a safe no-op;
        // S7 fills it in.
        public void ReapplyGroupStyles()
        {
        }

        // What an orb's Group submenu asks for: the group the orb's team is in
        // and the keys a move has to write. The root and the team are
        // TeamTree's — the same answer the drag and the arrangement use, T4's
        // surviving ancestor included — and the rest is OrbGroupMenu.ContextFor.
        internal GroupMenuContext GroupMenuContextFor(string sessionId)
        {
            var tree = Teams();
            return OrbGroupMenu.ContextFor(
                tree.RootOf(sessionId), tree.TeamOf(sessionId), AllStatuses,
                OrbweaverSettings.OpenClawHeartbeatMode, OrbweaverSettings.OpenClawCronMode);
        }

        // CB-198: the settings slider and the Size menu both land here, the
        // same shape as ReapplyGlyphs — one change, every orb. Each orb takes
        // its own override if it has one, the slider if not; then everything
        // that is placed *relative* to an orb's size moves with it: the
        // arrangement or the stack, anything the new sizes pushed off a
        // screen, the team arrows, and a chat panel open beside an orb.
        //
        // Goes through ApplyOrbSize directly rather than anything on the
        // UpdateFrom path, so SetTeamRole's early return (which only knows
        // about the team role) cannot swallow a size change.
        public void ReapplyOrbSizes()
        {
            foreach (var window in _windows.Values)
            {
                window.ApplyEffectiveOrbSize();
            }

            if (_isArranged) ReapplyArrangement();
            else ReflowPositions();

            RescueOffscreenOrbs();
            RememberResizedPins();
            TeamLinks.Refresh();
            ChatPanel.FollowOrbResize();
        }

        // A pinned orb that just resized kept its centre, so its saved corner and
        // size are stale; write the new pair. Only for a key no other pinned orb
        // shares — the drag code's rule (OrbWindow.OnPointerReleased) that a team
        // member must never overwrite its lead's spot with an offset copy. A
        // shared key's saved entry stays right anyway: it carries the size it
        // was saved at, and RestoredTopLeft corrects from that.
        private void RememberResizedPins()
        {
            var pinned = _windows.Values.Where(w => w.IsPinned && !string.IsNullOrEmpty(w.PositionKey)).ToList();

            foreach (var window in pinned)
            {
                if (pinned.Count(other => other.PositionKey == window.PositionKey) == 1)
                    RememberOrbPosition(window);
            }
        }

        // An orb's window in physical pixels at a screen's scaling — what
        // every bare `56 * scale` in this file meant before orbs had a size.
        internal static int OrbPixels(OrbWindow window, double scaling) =>
            (int)(OrbSizing.WindowDip(window.OrbSize) * scaling);

        // Speech is one global thing, not one per orb: whichever orb started it,
        // every open flyout's speak button has to agree about whether something
        // is being read. Broadcasting from here rather than from the orb that
        // clicked, for the same reason ReapplyGlyphs lives here — this class is
        // already what owns "one change, every orb".
        //
        // Posted to the UI thread because the state changes on whatever thread
        // the speech engine's process exited on.
        private void OnSpeakStateChanged(TextToSpeech.SpeakState state)
        {
            // Inside the Post, not before it. TextToSpeech raises this from
            // whichever thread noticed the speech engine change state — the
            // reason the orb updates below were already marshalled — and the
            // panel's own glyph is an Avalonia control like any other, so
            // touching it from there took the app down mid-sentence.
            Dispatcher.UIThread.Post(() =>
            {
                ChatPanel.SetSpeakState(state);

                foreach (var window in _windows.Values)
                {
                    window.SetFlyoutSpeakState(state);
                }
            });
        }

        private void ReflowPositions()
        {
            if (_order.Count == 0 || !OrbsVisible) return;

            if (_isArranged)
            {
                AbsorbIntoArrangement();
                return;
            }

            var first = _windows[_order[0]];
            var screen = first.Screens.Primary ?? first.Screens.All.FirstOrDefault();
            if (screen is null) return;

            // WorkingArea and Window.Position are in physical pixels; the
            // 12/24 design sizes and each orb's own window are DIPs, so scale
            // them.
            var work = screen.WorkingArea;
            var scale = screen.Scaling;
            int spacing = (int)(12 * scale);
            int margin = (int)(24 * scale);

            // Orbs the user has placed by hand keep their spot and don't take up
            // a slot, so the rest of the stack closes up behind them.
            //
            // CB-198: each orb advances the stack by its own size rather than a
            // shared 56, so a 2x orb pushes the next one down by what it
            // actually occupies. Right edges line up, the column keeps the
            // same margin from the screen edge at every size.
            int y = work.Y + margin;
            foreach (var id in DisplayOrder())
            {
                var window = _windows[id];
                if (window.IsPinned) continue;

                int size = OrbPixels(window, scale);
                window.Position = new PixelPoint(work.Right - size - margin, y);
                y += size + spacing;
            }

            // Every arrow's geometry just moved.
            TeamLinks.Refresh();
        }

        // A new orb appeared or an old one vanished while the shape is active.
        // An arrival still re-fits the whole shape and glides everything into
        // it; a departure just drops that one orb and leaves everyone else
        // exactly where they were. CB-161 is why the two are no longer the
        // same branch — read the rest of this comment as the record of both
        // decisions, since the first is still in force and the second reverses
        // what used to be here.
        //
        // Only the newcomer used to move, because re-fitting means an orb that
        // was sitting still moves for a reason that has nothing to do with it —
        // measured against the real geometry, an orb already on screen shifts
        // 33px on average when a sixth joins a circle, 111px in a heart and up
        // to 161px in a grid. The judgement was that a display which
        // rearranges itself because something unrelated started is a display
        // you stop trusting.
        //
        // Then "living with it" said the opposite, and extended that reversal
        // to a departure as well — "a gap in a ring is the same wrongness as a
        // stray orb beside it". CB-161 is a user filing exactly the complaint
        // the first paragraph predicted, for the departure half of that: an
        // orb ending relocated every survivor, which reads as the whole
        // display being unreliable rather than as one gap in a ring. Ending a
        // session is not a drag, and OrbArrangement.Layout.Center's own
        // doc comment already promised a shape does not recentre "every time
        // an orb joins or leaves" — a promise the removal path was not
        // keeping. So departures go back to leaving everyone else alone;
        // arrivals keep re-fitting, because that half of the reversal was
        // never the complaint and a shape that never absorbs a newcomer has
        // the opposite problem.
        private void AbsorbIntoArrangement()
        {
            // Orbs gone since the pattern was drawn — drop their saved state.
            foreach (var id in _preArrangeState.Keys
                         .Where(id => !_windows.ContainsKey(id)).ToList())
            {
                _preArrangeState.Remove(id);
            }

            // Orbs that arrived after the pattern was drawn — record where
            // they would have stacked so Restore can put them back there.
            foreach (var id in _windows.Keys)
            {
                if (_preArrangeState.ContainsKey(id)) continue;
                var w = _windows[id];
                _preArrangeState[id] = (w.Position, w.IsPinned);
                w.SetFlyoutArranged(true);
            }

            // Mid-glide. Asking for a second shape while the first is still
            // being flown to would fight it, and dropping the request would
            // strand whichever orb arrived during the half-second — which is
            // the whole complaint. It gets picked up when this one lands.
            if (_arrangeAnimTargets is not null)
            {
                _refitPending = true;
                return;
            }

            var allOrbs = DisplayOrder()
                .Where(id => _windows.ContainsKey(id) && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();

            if (allOrbs.Count < 1) return;

            // Every remaining orb was already placed by the shape currently on
            // screen, and none of them is new — this scan's only change was
            // one or more orbs disappearing. Nothing here needs to move: the
            // gap left behind is the honest picture of who is still running,
            // and re-fitting around it is exactly the relocation CB-161
            // reported. Just forget the id that left and leave the rest be.
            if (allOrbs.All(o => _arrangedIds.Contains(o.SessionId)))
            {
                _arrangedIds.IntersectWith(allOrbs.Select(o => o.SessionId));
                TeamLinks.Refresh();
                return;
            }

            var positioned = ComputeClusteredPositions(allOrbs);

            // Whoever is in this set now has a position the current shape
            // actually assigned, whichever branch below runs — settled or
            // animated, an arrival is "arranged" the moment it has a target.
            _arrangedIds.UnionWith(allOrbs.Select(o => o.SessionId));

            // Nothing to do if every orb is already where the new shape wants
            // it. Worth the check: this runs on every scan that changes the set
            // at all, including changes that do not move anybody.
            if (positioned.All(p => p.Orb.Position == p.Target))
            {
                TeamLinks.Refresh();
                return;
            }

            _arrangeAnimTargets = new();
            foreach (var (orb, target) in positioned)
                _arrangeAnimTargets[orb.SessionId] = (orb.Position, target);

            AnimateArrangement();
        }

        // There used to be a PinEveryOrbAtItsTarget here, passed as the glide's
        // completion. It never ran — it read _arrangeAnimTargets after
        // OnArrangeAnimTick had cleared it — and CB-268 retires it rather than
        // fixing it: an arranged orb is held by its group's anchor, and a pin
        // would make the next arrange treat it as an exception again, which is
        // the pull-back the plan removes. A dragged-out orb is not pinned
        // either; it becomes a group of its own at the drop point
        // (OrbDragCommit.Release). OrbArrangementAnimationTests pins "not
        // pinned" as the intent now.

        // --- dragged orb positions -------------------------------------------
        // A dragged orb stays where it was put. Within a run that's the pinned
        // flag above; across runs it's settings.json, keyed by whichever part of
        // a session survives a restart. For Claude Code that is its directory,
        // because its session id is new every run and would remember nothing;
        // for a gateway session it is the id itself, which is not.
        //
        // A local key is the directory *and* the session's name, because two
        // sessions in one directory are common and sharing a slot meant neither
        // stayed put. The name can change under you — Claude Code writes an
        // automatic title that follows the conversation — so a lookup falls back
        // to the directory alone, which also covers positions saved before names
        // were part of this.

        // A gateway session has no directory to be keyed by — the findings doc
        // notes the absence of `cwd` as a *simplification*, since there is no
        // local checkout for a key to collide with. What it missed is that the
        // key was doing a second job: an empty one is never saved and never
        // restored, so every agent orb went back to the stack on every launch
        // while local ones stayed put.
        //
        // Its session id is the stable thing instead. Unlike a Claude Code
        // session id, which is new every run, a gateway key is derived from the
        // agent and the channel and is the same string next week — which is what
        // made it a good room key and makes it a good position key.
        internal static string PositionKeyFor(SessionStatus status, string sessionId)
        {
            if (!status.IsLocalCli) return sessionId;

            var cwd = DirectoryKeyFor(status);
            if (cwd.Length == 0) return "";

            // Which CLI, in the key, for Codex only.
            //
            // Not decoration: a Codex session and a Claude Code session open in
            // one directory would otherwise share a slot whenever the Claude
            // one has not been auto-titled yet, which is exactly the collision
            // ccfee1d fixed for two Claude sessions. First orb to appear claims
            // the position, the other stacks, and whichever is dragged last
            // overwrites the one entry.
            //
            // Only Codex is prefixed, so every position already saved under a
            // bare directory still matches the session it was saved for. A
            // scheme that renamed both would have been tidier and would have
            // moved every pinned orb on this machine back to the stack once.
            var prefix = status.Source switch
            {
                SessionSource.Codex => "codex\n",
                SessionSource.Grok => "grok\n",
                _ => ""
            };

            // The directory alone is not enough when two sessions are open in
            // one: "makayla-lawyer" and "job-lawyer" both live in Evidence, so
            // they shared a slot — first orb to appear claimed it, the other
            // stacked, and whichever was dragged last overwrote the one entry.
            // Two orbs that would not stay where they were put, while every
            // other orb did.
            //
            // The session's name is what separates them, and it is the right
            // thing rather than a convenient one: it is what *you* called that
            // session, so an orb follows the name you gave it rather than the
            // folder it happens to share.
            // An untitled session keys on its own id rather than on the bare
            // directory, which is what the paragraph above only half fixed.
            // Adding the title separates two *titled* sessions in one folder and
            // does nothing at all when the title is empty — every untitled
            // session in a directory fell back to the same bare-cwd key and
            // collided exactly as before. Measured on a real machine: three
            // live untitled sessions in Documents/GTD/Evidence sharing one key,
            // so RestoreOrbPosition refused a slot to two of them and three
            // orbs read as two on screen.
            //
            // Empty is not a rare case. Claude Code names a session only once it
            // has something to name it from, so every session is untitled for
            // its first stretch, and a background job's status file can stay
            // that way for its whole life.
            //
            // The id is per-session rather than per-directory, so an orb dragged
            // while untitled does not find that position again — a fresh id next
            // run, and a different key again once the session is finally named.
            // That is the right way round: an untitled orb's saved slot is
            // precisely the one that could not be trusted to belong to it, and
            // landing in the auto-arranged stack is better than landing on top
            // of a sibling. A titled session's key is byte-for-byte what it was,
            // so every position already saved on this machine still matches.
            var title = (status.Title ?? "").Trim();
            if (title.Length == 0) return sessionId;

            return prefix + cwd + "\n" + title;
        }

        internal static string DirectoryKeyFor(SessionStatus status) =>
            string.IsNullOrEmpty(status.Cwd) ? "" : status.Cwd.TrimEnd('\\', '/');

        // CB-167's per-orb key for turn sounds. Built on PositionKeyFor
        // rather than invented fresh, because PositionKeyFor is already this
        // app's answer to "which agent is this across scans and restarts" —
        // it carries the CB-10 fix for an untitled session keying on its own
        // id, and reusing it means a sound override and a pinned position
        // agree about which orb they are talking about, the same way
        // ChatPanelSizes already agrees with OrbPositions.
        //
        // The one gap PositionKeyFor leaves open is team members: two agents
        // sharing one cwd and one auto-generated title collide under it,
        // which is fine for a *position* — stacking rules already handle two
        // orbs wanting one slot — but wrong for a sound override, where
        // "make agent A quiet" silently muting agent B too is a real
        // correctness bug, not a cosmetic one. Appending the agent name (set
        // from AgentTeam membership earlier in the scan, before this is ever
        // called) closes that gap without touching PositionKeyFor itself,
        // since nothing about orb placement cares which teammate it is.
        internal static string SoundKeyFor(SessionStatus status, string sessionId)
        {
            var key = PositionKeyFor(status, sessionId);

            // QA (CB-167): an empty PositionKeyFor is a deliberate "no key"
            // — a local session with no cwd, per PositionKeyFor's own early
            // return — and the accessors on OrbweaverSettings already read
            // an empty key as "no override, don't bother looking." Appending
            // the agent name onto that empty string used to turn it into
            // "\n<agent>", a real, non-empty key with no cwd in it at all —
            // shared by every session anywhere naming that same agent, in
            // every project. Muting one teammate would have silently muted
            // them everywhere. Staying empty whenever PositionKeyFor does is
            // what keeps this a per-orb key rather than a per-agent-name one.
            if (key.Length == 0 || string.IsNullOrEmpty(status.Agent)) return key;

            return key + "\n" + status.Agent;
        }

        private void RestoreOrbPosition(OrbWindow window, SessionStatus status)
        {
            var key = PositionKeyFor(status, window.SessionId);
            window.PositionKey = key;

            // window.SoundKey is no longer set here. QA round 2 (HIGH):
            // SoundKeyFor depends on Title, which can arrive after this
            // method's one-time call (RestoreOrbPosition only runs for a
            // brand-new orb) — an untitled session keys on its own id until
            // Claude Code names it, so a value set only here would go stale
            // the moment a title showed up. OrbWindow.UpdateFrom now
            // recomputes it on every poll instead, and this same status
            // already reached UpdateFrom before this method ever runs
            // (SessionManager.cs's per-session loop calls UpdateFrom first),
            // so window.SoundKey is already correct by the time execution
            // gets here.

            if (string.IsNullOrEmpty(key)) return;

            // A sibling session in the same directory already sits there;
            // stacking a second orb on top of it would just hide one.
            if (_windows.Values.Any(other => other != window
                                             && other.IsPinned
                                             && other.PositionKey == key))
            {
                return;
            }

            var saved = OrbweaverSettings.OrbPositionFor(key);

            // Nothing under the name, so try the directory on its own. That
            // covers a position saved before names were part of the key, and a
            // session whose title has changed since — Claude Code writes an
            // automatic one that moves as a conversation does, and losing your
            // placement to a retitle would be a worse bug than the one this
            // fixes.
            // Claude Code only, and it has to stay that way. This exists for
            // positions saved before names were part of the key, and there are
            // no such Codex positions — every one of them has been written by a
            // build that prefixes the CLI. Widening it would hand a Codex orb
            // the place a Claude Code orb was put.
            if (saved is null && status.Source == SessionSource.ClaudeCode)
            {
                var directory = DirectoryKeyFor(status);
                if (directory.Length > 0 && directory != key)
                {
                    saved = OrbweaverSettings.OrbPositionFor(directory);
                }
            }

            if (saved is null) return;

            // The monitor it was dragged onto may be gone, or its layout
            // changed. Anything that no longer lands on a screen falls back to
            // the default stack rather than being stranded off-canvas. Asked of
            // the saved corner itself, before the size correction below, which
            // is at most half a 2x orb and should not decide which screen this
            // was.
            var screen = window.Screens.ScreenFromPoint(new PixelPoint(saved.X, saved.Y));
            if (screen is null) return;

            var point = RestoredTopLeft(saved, window.OrbSize, screen.Scaling);
            window.PinAt(ClampIntoWork(point, screen.WorkingArea, OrbPixels(window, screen.Scaling)));
        }

        // CB-111: bring back a chat panel that was pinned before the app last
        // quit. Same key, same "does it still land on a screen" guard, and
        // the same sibling rule as RestoreOrbPosition just above — only here
        // the sibling question is answered by ChatPanel.IsPinnedFor rather
        // than by reading _windows, since two orbs sharing a key would each
        // try to reopen the one saved panel and the panel, not the orb, is
        // the thing that must not be duplicated.
        //
        // Deliberately no directory-only fallback the way RestoreOrbPosition
        // has for an orb position saved before names were part of the key:
        // PinnedChatPanels is a CB-111 key from day one, so there is no older
        // shape of it to fall back to.
        private void RestorePinnedChatPanel(OrbWindow window, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (ChatPanel.IsPinnedFor(key)) return;

            var saved = OrbweaverSettings.PinnedChatPanelPositionFor(key);
            if (saved is null) return;

            var point = new PixelPoint(saved.X, saved.Y);
            if (window.Screens.ScreenFromPoint(point) is null) return;

            // Nothing to bind the panel to if this session doesn't resolve to
            // a live conversation (a room member removed between runs, a
            // gateway id the daemon no longer recognises). Silently skipped,
            // the same as RestoreOrbPosition silently skips an orb whose
            // saved monitor is gone — a missing prerequisite, not an error.
            var chat = RemoteChatFor(window.SessionId);
            if (chat is null) return;

            ChatPanel.RestorePinned(window, chat, point);
        }

        // An orb's top-left corner, pulled back until the whole orb is inside
        // the working area.
        //
        // The Math.Max guards are not belt-and-braces: a work area narrower than
        // an orb makes `Right - orbSize` smaller than `X`, and Math.Clamp throws
        // rather than picking a side when its bounds are inverted. Shared by the
        // two callers — a position restored from settings and one rescued from a
        // display that has changed shape — because they clamp for the same
        // reason and drifting apart would mean one of them stranding an orb the
        // other would have saved.
        internal static PixelPoint ClampIntoWork(PixelPoint point, PixelRect work, int orbSize) =>
            new(Math.Clamp(point.X, work.X, Math.Max(work.X, work.Right - orbSize)),
                Math.Clamp(point.Y, work.Y, Math.Max(work.Y, work.Bottom - orbSize)));

        // Bring back any orb that is no longer on a screen.
        //
        // Nothing in the app could do this, and the gap was reported the way
        // gaps like this always are: "I seem to have lost all my orbs." They
        // were sitting in a neat row 223 points above the top of the display,
        // which is exactly where they had been put while the desktop was a
        // different shape.
        //
        // Nothing had moved them. A restored position is already checked
        // against the screens it lands on — see RestoreOrbPosition — but an orb
        // *already placed* was checked once and never again, and the thing that
        // changes underneath it is the desktop: a monitor unplugged, an
        // arrangement rearranged, a resolution changed. The orb keeps
        // coordinates that were true when it got them and stops being anywhere
        // a person can click.
        //
        // Judged on the orb's centre rather than its corner, so one hanging
        // half off an edge is left alone — that is a placement somebody may
        // have chosen, and dragging it back would be the app overruling them.
        // Only an orb with nothing under its middle is unreachable, and only
        // those are moved.
        //
        // The position is not written back to settings. If it came from there
        // it is still a good position for the display it was saved on, and
        // RestoreOrbPosition already declines to use it anywhere else.
        private void RescueOffscreenOrbs()
        {
            foreach (var window in _windows.Values)
            {
                if (!window.IsVisible) continue;

                // Half the orb's own window in Position's units, rather than
                // the bare 56 this used to add: that was a DIP half added to a
                // physical position, right only at 100% scaling, and blind to
                // the orb's size (CB-198).
                var half = OrbPixels(window, window.DesktopScaling) / 2;
                var centre = new PixelPoint(window.Position.X + half, window.Position.Y + half);

                if (window.Screens.ScreenFromPoint(centre) is not null) continue;

                var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
                if (screen is null) continue;

                window.PinAt(ClampIntoWork(
                    window.Position, screen.WorkingArea, OrbPixels(window, screen.Scaling)));
            }
        }

        public void RememberOrbPosition(OrbWindow window)
        {
            if (string.IsNullOrEmpty(window.PositionKey)) return;

            var position = window.Position;
            OrbweaverSettings.SetOrbPosition(window.PositionKey, position.X, position.Y, window.OrbSize);
        }

        // CB-198: a saved spot is a top-left corner, and the same *centre* at a
        // different size has a different corner. Shifted by half the difference
        // between the window it was saved at and the window it is now, so the
        // orb comes back centred where it was left. No saved size means it was
        // saved before orbs had one, which is 1.0.
        internal static PixelPoint RestoredTopLeft(
            OrbweaverSettings.OrbPlacement saved, double currentSize, double scaling)
        {
            var shift = (OrbSizing.WindowDip(saved.Size ?? OrbSizing.Default) - OrbSizing.WindowDip(currentSize))
                        / 2 * scaling;
            var d = (int)Math.Round(shift);
            return new PixelPoint(saved.X + d, saved.Y + d);
        }

        public void ReturnOrbToStack(string sessionId)
        {
            if (!_windows.TryGetValue(sessionId, out var window)) return;

            window.Unpin();
            if (!string.IsNullOrEmpty(window.PositionKey))
            {
                OrbweaverSettings.ClearOrbPosition(window.PositionKey);
            }

            ReflowPositions();
        }

        public void ResetSessionToIdle(string sessionId)
        {
            // There is no status file to rewrite for a gateway session, and the
            // path this would build from its key ("openclaw:agent:main:…")
            // is not one this app should be writing at all. The gateway owns
            // that session's state; we only display it.
            if (_statuses.TryGetValue(sessionId, out var known) && !known.IsLocalCli)
            {
                return;
            }

            var file = StatusFileFor(sessionId);
            SessionStatus? existing = null;
            try
            {
                existing = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(file));
            }
            catch { }

            // Keep everything but the state (cwd, terminal info) intact.
            var reset = existing ?? new SessionStatus();
            reset.State = "idle";

            // The second of the two places a status file is read, and the
            // reason SourceOf exists rather than the scan resolving this
            // inline. Without it the object handed to UpdateFrom below claims
            // to be Claude Code — Source is [JsonIgnore], so it arrives as its
            // default — and the orb would say so, and TerminalFocuser would
            // believe it, until the next scan put it right.
            //
            // `existing ?? new SessionStatus()` is the case that makes this
            // more than tidiness: a file that could not be read has no Cli
            // either, so the fallback has to go through the same resolution
            // rather than inheriting whatever the caller assumed.
            reset.Source = SourceOf(reset);
            try
            {
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(reset));
            }
            catch { }

            _statuses[sessionId] = reset;

            // Silent, not Observe: a manual reset is a person clearing a
            // stuck orb, not a turn finishing, and the tracker has to agree
            // or the next real scan would find "idle" already on record and
            // never notice the actual generating → idle transition that
            // follows. See TurnSignalTracker.Settle's own comment.
            _turnSignals.Settle(sessionId, "idle");

            // QA (CB-167): the same silence Settle just gave the tracker
            // applies to a sound still waiting on this session — a person
            // clearing a stuck orb should not have it ding a couple of
            // seconds later for a turn they just told the app to forget
            // about.
            TurnSounds.CancelPendingFor(sessionId);

            if (_windows.TryGetValue(sessionId, out var window))
            {
                window.UpdateFrom(reset);
            }

            UpdateTray();
        }

        // "Dismiss this orb": take this session off the screen, without touching
        // the session itself.
        //
        // Deleting the status file is the whole of it. The FileSystemWatcher on
        // the status directory sees the Deleted event and schedules the debounced
        // rescan, which removes the orb through exactly the path a SessionEnd
        // hook takes — no direct ScanAndUpdate call from here, because that
        // method's own comment says why two passes racing over the same
        // dictionaries on the same thread is not a thing to arrange deliberately.
        //
        // Self-correcting by design, and that is the reason this is offered
        // alongside "End this session" rather than instead of it. A file dismissed
        // from under a session that is still going is rewritten by that session's
        // next hook event and the orb comes back — so dismissing is a statement
        // about the screen, not about the session, and the user does not have to
        // be sure which one they are looking at before clicking.
        //
        // Guarded the way ResetSessionToIdle is, and against the same thing: a
        // gateway session's orb comes from a socket, so there is nothing on disk
        // to delete and its namespaced key ("openclaw:agent:main:…") is not a
        // path this app should be building. See SessionPresence.CanDismiss, which
        // is also what hides the menu item.
        public void DismissSession(string sessionId)
        {
            if (_statuses.TryGetValue(sessionId, out var known)
                && !SessionPresence.CanDismiss(known))
            {
                return;
            }

            DeleteStatusFile(sessionId);
        }

        // One place that knows how a status file is removed, and the only place
        // that swallows the failure. Both callers want exactly the same thing:
        // the file gone if it can be, and no complaint if it cannot.
        //
        // Nothing is reported because there is nowhere to report it. A file that
        // vanished under us has already had the effect the caller asked for; one
        // this process may not delete will simply be found again on the next
        // scan, which is a better outcome than a message this app has no
        // vocabulary for.
        //
        // In both folders while the legacy one is watched (CB-255 §1). The
        // scan ignores the losing copy of a session rather than deleting it,
        // but this is not the scan choosing a winner — it is the session being
        // removed, by a click or by the sweep having judged it dead — and
        // deleting only the winner would hand the screen straight back to the
        // older copy on the next scan: a dismissed orb that reappears, still
        // dead, until the sweep's grace runs out a second time.
        private void DeleteStatusFile(string sessionId)
        {
            DeleteQuietly(Path.Combine(_statusDir, sessionId + ".txt"));
            if (_legacyStatusDir is not null) DeleteQuietly(Path.Combine(_legacyStatusDir, sessionId + ".txt"));
        }

        private static void DeleteQuietly(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }

        // The file a session's status was last read from — which, with the
        // legacy folder watched, may not be in _statusDir (CB-255 §1). A
        // session the last read never saw falls back to the new folder, which
        // is where any file this app creates for it belongs.
        private string StatusFileFor(string sessionId) =>
            _statusFiles.TryGetValue(sessionId, out var path)
                ? path
                : Path.Combine(_statusDir, sessionId + ".txt");

        // "End this session": stop the process behind it.
        //
        // The only irreversible thing in this app, and the only thing in it that
        // ends work the user might still want — so it is a deliberate click and
        // never automatic, and it happens exactly once, to exactly one pid. No
        // sweep, no timer and no rule anywhere else calls this.
        //
        // Nothing is removed from screen here. The orb goes when the next scan
        // sees the pid stop answering (ScanVerdict.ProcessGone), and the file
        // goes when SessionEnd fires or the sweep catches it — the same two paths
        // any other ending session takes. Removing the orb optimistically would
        // mean a session that ignored the signal disappeared from the screen while
        // carrying on working, which is the failure this whole ticket is about.
        //
        // Requires a known session rather than falling back on the id, unlike
        // Dismiss above: the guard needs a pid, and the only place a pid is is in
        // the status this scan read.
        //
        // And refuses outright for one shape, which is CB-26. A session whose
        // pid is the ancestor of a live `claude daemon run` is not an ordinary
        // session: it is the husk a backgrounded turn left behind, it is the
        // window the user is reading that job's conversation in, and on Windows
        // the tree kill would take the daemon and every other job on the machine
        // with it. The orb presents itself as stale, so the gesture looks free
        // and is not.
        //
        // Refused rather than warned-and-proceeded, and the difference is what
        // the app can actually say. There is no dialog vocabulary anywhere in it
        // — OrbWindow.axaml's own comment says so, and says that is a decision
        // rather than an omission — so "warn" here could only mean acting first
        // and explaining afterwards, on the one action that cannot be undone.
        // The explanation instead goes where the user is already looking: the
        // menu row is disabled and re-worded with the count of jobs at stake,
        // the same shape ResetIdleItem already uses for a session it cannot
        // serve. This guard is what makes that row's promise true, because a
        // menu can be read from a snapshot taken a moment before the click.
        //
        // The status is read here, on the UI thread that owns _statuses, and
        // everything after it runs on a pool thread (CB-228). Both halves of
        // what follows read the machine: the guard is a full process-table read
        // — a WMI query on Windows, measured at 218-273 ms on the Windows PC —
        // and Windows' tree kill walks the table again. Neither belongs on the
        // thread that draws every orb. The guard moves with the kill rather
        // than staying behind on the UI thread, because the order is the whole
        // point of it: ask, then refuse or act, on one reading.
        //
        // Three hops rather than one: read on a pool thread, back to the UI
        // thread to check the session is still the one that was clicked, then
        // the kill on a pool thread. The middle check is new with the move.
        // While the read runs — a quarter of a second on Windows — a scan can
        // drop the session or hand its id a new pid, and a kill aimed at the
        // pid the click saw would then land on whatever holds it now.
        //
        // A read that throws ends the task with nothing signalled: the
        // exception escapes before the kill is reached, which is the direction
        // an irreversible action should fail in.
        //
        // Returned so a test can wait for the outcome; the click handler
        // discards it, since the orb going away on the next scan is the only
        // feedback this gesture has ever had.
        //
        // One end per session at a time. The synchronous version serialised two
        // clicks by blocking; this one would have let a second click start its
        // own read while the first was still reading, and both pass
        // StillEndable — the status stays in _statuses until the next scan —
        // so the kill ran twice. QA found it with a test. A second call while
        // one is in flight now does nothing: the first is already doing what
        // it asked. UI-thread state, like _statuses, and released in a finally
        // that also runs on the UI thread, because every await above it
        // resumes there.
        //
        // A background job is asked to stop by its daemon before anything is
        // signalled (CB-264). Signalling the worker of a parked job only makes
        // the daemon resume it on a new pid, so the orb the user ended came
        // straight back; BackgroundJobs.Stop has the measurement. Same hop and
        // same re-check as the kill: the stop runs on a pool thread, and when
        // the daemon could not be asked, the session is checked again before
        // falling back to the signal it always got.
        public async Task EndSession(string sessionId)
        {
            if (!_statuses.TryGetValue(sessionId, out var status)) return;
            if (!SessionPresence.CanEndSession(status)) return;
            if (!_ending.Add(sessionId)) return;

            try
            {
                var pid = status.SessionPid;
                var read = _dependents;

                var dependents = await Task.Run(() => read(pid));
                if (SessionDependents.BlocksTermination(dependents)) return;
                if (!StillEndable(sessionId, pid)) return;

                var source = status.Source;
                var transcript = status.TranscriptPath;
                var listing = _jobListing;
                var stop = _stopJob;
                var stopped = await Task.Run(() =>
                    BackgroundJobs.WorthAskingTheDaemon(source, listing(), sessionId)
                    && stop(sessionId, transcript));
                if (stopped) return;
                if (!StillEndable(sessionId, pid)) return;

                var terminate = _terminate;
                await Task.Run(() => terminate(pid, dependents));
            }
            finally
            {
                _ending.Remove(sessionId);
            }
        }

        private readonly HashSet<string> _ending = new(StringComparer.Ordinal);

        // Whether the session a click named is still there, still on the pid
        // the read was about, and still endable.
        private bool StillEndable(string sessionId, int pid) =>
            _statuses.TryGetValue(sessionId, out var now)
            && now.SessionPid == pid
            && SessionPresence.CanEndSession(now);

        // What the orb's menu asks before it draws "End this session", so the
        // row and EndSession's refusal apply one rule to the same machine. See
        // OrbWindow.RefreshEndSessionGuardAsync.
        //
        // Asynchronous because the answer is a process-table read (CB-228): the
        // pid is looked up here on the UI thread and the read itself runs on a
        // pool thread, so a right-click never waits on WMI. There is
        // deliberately no synchronous version beside it — one would be the
        // easy thing for the next caller on the UI thread to reach for.
        //
        // A session the menu does not offer the row for is answered without a
        // read, and without a thread hop, so the cost a gateway orb's menu
        // pays is nothing at all.
        internal Task<SessionDependents.Verdict> DependentsOfAsync(string sessionId)
        {
            if (!_statuses.TryGetValue(sessionId, out var status) || !SessionPresence.CanEndSession(status))
                return Task.FromResult(SessionDependents.Nothing);

            var pid = status.SessionPid;
            var read = _dependents;

            return Task.Run(() => read(pid));
        }

        public void ResetAllSessionsToIdle()
        {
            foreach (var sessionId in _order.ToList())
            {
                ResetSessionToIdle(sessionId);
            }
        }

        // --- orb arrangement patterns ----------------------------------------
        // Clicking the arrange button on any orb's flyout gathers every
        // visible orb into a heart shape, centred on the primary screen.
        // Clicking again restores them — a toggle, not a one-way trip.

        private readonly Dictionary<string, (PixelPoint Position, bool Pinned)> _preArrangeState = new();
        private bool _isArranged;

        // Session ids the current shape has actually placed an orb for — not
        // the same job as _preArrangeState, which is keyed the same way but
        // remembers where an orb was *before* arranging, for RestoreFromPattern
        // to put it back. This one exists only for AbsorbIntoArrangement to
        // tell a pure departure (every remaining id already in here) from a
        // real arrival (an id that is not), which is the distinction CB-161
        // needed: an orb ending must not move anyone else, but an orb joining
        // still re-fits the whole shape around it, as before.
        private readonly HashSet<string> _arrangedIds = new();

        public bool IsArranged => _isArranged;

        private DispatcherTimer? _arrangeAnimTimer;
        private Dictionary<string, (PixelPoint From, PixelPoint To)>? _arrangeAnimTargets;
        private long _arrangeAnimStart;
        private Action? _arrangeAnimComplete;

        // A membership change that arrived mid-glide, to be re-fitted once the
        // current one lands. See AbsorbIntoArrangement.
        private bool _refitPending;
        private const int ArrangeAnimMs = 600;

        public void ArrangeOrbsInPattern()
        {
            if (_arrangeAnimTargets is not null) return;

            if (_isArranged)
            {
                RestoreFromPattern();
                return;
            }

            var allOrbs = DisplayOrder()
                .Where(id => _windows.ContainsKey(id) && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();

            if (allOrbs.Count < 1) return;

            foreach (var w in _windows.Values)
            {
                w.HideFlyout();
                ChatPanel.HideFor(w.SessionId);
            }

            _preArrangeState.Clear();
            foreach (var orb in allOrbs)
                _preArrangeState[orb.SessionId] = (orb.Position, orb.IsPinned);

            var positioned = ComputeClusteredPositions(allOrbs);

            _arrangeAnimTargets = new();
            foreach (var (orb, target) in positioned)
                _arrangeAnimTargets[orb.SessionId] = (orb.Position, target);

            _isArranged = true;
            _arrangedIds.Clear();
            foreach (var orb in allOrbs) _arrangedIds.Add(orb.SessionId);
            AnimateArrangement();

            foreach (var w in _windows.Values)
                w.SetFlyoutArranged(true);
        }

        // Leads and solo orbs define the shape; members radiate outward
        // from their lead, away from the shape's centre — so the pattern
        // reads cleanly and the small member orbs fan out like spokes.
        // Maps the orbs on screen onto OrbArrangement's inputs and its answer
        // back onto them. All the geometry lives there, where it can be tested —
        // see tests/ArrangementTests, which walks every shape at every spacing
        // across the team shapes that occur and checks that nothing leaves the
        // screen or lands on top of anything else.
        private List<(OrbWindow Orb, PixelPoint Target)> ComputeClusteredPositions(List<OrbWindow> allOrbs)
        {
            if (allOrbs.Count == 0) return new List<(OrbWindow, PixelPoint)>();

            // CB-268: the team tree and the group table replace the raw Lead
            // lookup and the fixed three-shape list. leadOf is TeamTree's, so a
            // member whose lead ended fans off its nearest surviving ancestor
            // (T4); groupOf is each orb's *root's* group as a slot in the group
            // table's order (T1), which is fixed by the table and never by which
            // sessions exist — main, heartbeats, crons, then user groups by id.
            var tree = new TeamTree(allOrbs.Select(o => o.SessionId).ToList(), KnownLeads());
            var leadOf = tree.LeadIndices();

            var groups = OrbweaverSettings.OrbGroupList();
            var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < groups.Count; i++) slotOf[groups[i].Id] = i;

            // GroupIdOf only ever answers a predefined id or a membership
            // MembershipFor already checked against this table, so every id is
            // in slotOf; slot 0 (main) is the answer for one that somehow is
            // not, the same fallback OrbArrangement gives an out-of-range slot.
            var groupOf = allOrbs
                .Select(orb => slotOf.GetValueOrDefault(GroupIdOf(tree, orb.SessionId)))
                .ToArray();

            var screen = allOrbs[0].Screens.Primary ?? allOrbs[0].Screens.All.FirstOrDefault();
            var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

            // No Center: each group brings its own anchor (GroupLayout), and
            // ComputeGroups ignores Layout's. Nothing is seeded before the
            // layout either — with no anchor anywhere ComputeGroups draws
            // today's bands, which is what a first arrange must look like, and
            // every group's landed anchor is saved from that.
            var layout = new OrbArrangement.Layout(
                work,
                screen?.Scaling ?? 1.0,
                groups[0].Shape,
                OrbweaverSettings.ArrangeSpacing);

            // CB-198: each orb's effective size, in the same index order as
            // leadOf and groupOf. Read off the window rather than recomputed
            // from settings, so the arrangement lays out what is actually drawn
            // — ReapplyOrbSizes applies the sizes before it arranges.
            var sizeOf = allOrbs.Select(orb => orb.OrbSize).ToArray();

            var placed = PlaceGroups(allOrbs.Count, leadOf, groupOf, groups, layout, sizeOf);
            return allOrbs.Select((orb, i) => (orb, placed[i])).ToList();
        }

        // Where every orb goes, given the groups: S2's ComputeGroups, with one
        // GroupLayout per entry of the group table — empty ones included, so a
        // slot index means the same group whoever is on screen.
        //
        // CB-211, per group: save where each group actually landed, not where it
        // was asked to go. ComputeGroups reports it already clamped and exact —
        // fed back as the group's Center it reproduces the same layout — so it
        // is saved whenever it differs from the anchor asked for, including
        // the first time, when none was. A drag that carried a group past what
        // the screen honours is repaired here on the next arrange instead of
        // swallowing the next drag back.
        private static PixelPoint[] PlaceGroups(
            int count, int[] leadOf, int[] groupOf, IReadOnlyList<OrbGroup> groups,
            OrbArrangement.Layout layout, double[] sizeOf)
        {
            var layouts = groups
                .Select(g => new OrbArrangement.GroupLayout(
                    g.Shape, g.Anchor is { } a ? new PixelPoint(a.X, a.Y) : null))
                .ToList();

            var (positions, placed) = OrbArrangement.ComputeGroups(count, leadOf, groupOf, layouts, layout, sizeOf);

            foreach (var group in placed)
            {
                if (layouts[group.Slot].Center == group.Landed) continue;
                OrbweaverSettings.SetOrbGroupAnchor(
                    groups[group.Slot].Id,
                    new OrbweaverSettings.OrbPlacement(group.Landed.X, group.Landed.Y));
            }

            return positions;
        }

        // A whole-group drag (OrbDragCommit.Release) moves every orb of the
        // group by the same delta, so the group's saved centre needs the same
        // nudge or the next membership change would snap it back to wherever it
        // was before the drag. `main` by default, which is the one shape there
        // was before groups.
        //
        // Unbounded here on purpose. A drag can carry the shape toward an edge
        // further than the screen will draw it, and clamping at this point
        // would mean repeating the whole arrangement's geometry to know where
        // "too far" is. The next arrange does know, and saves the anchor it
        // actually honoured (CB-211) — so the overshoot lasts only until then.
        // A group with no anchor yet has nothing to shift: it is placed by the
        // first-draw layout, which saves one.
        public void ShiftArrangementAnchor(int dx, int dy, string groupId = OrbGroups.Main)
        {
            if (dx == 0 && dy == 0) return;
            if (OrbweaverSettings.OrbGroupFor(groupId)?.Anchor is not { } anchor) return;

            OrbweaverSettings.SetOrbGroupAnchor(
                groupId, new OrbweaverSettings.OrbPlacement(anchor.X + dx, anchor.Y + dy));
        }

        private void RestoreFromPattern()
        {
            if (_arrangeAnimTargets is not null) return;

            foreach (var w in _windows.Values)
            {
                w.HideFlyout();
                ChatPanel.HideFor(w.SessionId);
            }

            var targets = new Dictionary<string, (PixelPoint From, PixelPoint To)>();
            foreach (var (id, (origPos, _)) in _preArrangeState)
            {
                if (!_windows.TryGetValue(id, out var window)) continue;
                targets[id] = (window.Position, origPos);
            }

            _arrangeAnimTargets = targets;
            _isArranged = false;
            _arrangedIds.Clear();

            AnimateArrangement(() =>
            {
                foreach (var (id, (origPos, wasPinned)) in _preArrangeState)
                {
                    if (!_windows.TryGetValue(id, out var window)) continue;
                    if (wasPinned)
                        window.PinAt(origPos);
                    else
                        window.Unpin();
                }
                _preArrangeState.Clear();
                ReflowPositions();
            });

            foreach (var w in _windows.Values)
                w.SetFlyoutArranged(false);
        }

        private void AnimateArrangement(Action? onComplete = null)
        {
            _arrangeAnimComplete = onComplete;
            _arrangeAnimStart = Environment.TickCount64;

            if (_arrangeAnimTimer is null)
            {
                _arrangeAnimTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / 60)
                };
                _arrangeAnimTimer.Tick += OnArrangeAnimTick;
            }
            _arrangeAnimTimer.Start();
        }

        private void OnArrangeAnimTick(object? sender, EventArgs e)
        {
            if (_arrangeAnimTargets is null)
            {
                _arrangeAnimTimer?.Stop();
                return;
            }

            var elapsed = Environment.TickCount64 - _arrangeAnimStart;
            var t = Math.Min(1.0, elapsed / (double)ArrangeAnimMs);
            var eased = 1 - Math.Pow(1 - t, 3);

            foreach (var (id, (from, to)) in _arrangeAnimTargets)
            {
                if (!_windows.TryGetValue(id, out var window)) continue;
                window.Position = new PixelPoint(
                    (int)Math.Round(from.X + (to.X - from.X) * eased),
                    (int)Math.Round(from.Y + (to.Y - from.Y) * eased));
            }
            TeamLinks.Refresh();

            if (t < 1.0) return;

            _arrangeAnimTimer!.Stop();
            var complete = _arrangeAnimComplete;
            _arrangeAnimComplete = null;
            var targets = _arrangeAnimTargets;
            _arrangeAnimTargets = null;
            complete?.Invoke();

            // An orb arrived or left while that was flying. Fit it in now,
            // rather than leaving it as the one orb outside the shape.
            if (!_refitPending) return;

            _refitPending = false;
            if (_isArranged) AbsorbIntoArrangement();
        }

        // Called from the settings slider so the user sees orbs reposition
        // in real time while dragging. Only acts when orbs are already arranged;
        // if they're not, the new spacing is just saved for next time.
        public void ReapplyArrangement()
        {
            if (!_isArranged) return;
            if (_arrangeAnimTargets is not null) return;

            var allOrbs = DisplayOrder()
                .Where(id => _windows.ContainsKey(id) && _windows[id].IsVisible)
                .Select(id => _windows[id])
                .ToList();

            if (allOrbs.Count < 1) return;

            var positioned = ComputeClusteredPositions(allOrbs);
            foreach (var (orb, target) in positioned)
                orb.Position = target;

            TeamLinks.Refresh();
        }

        // --- CB-268 S7: teams and groups -----------------------------------------

        // The last Lead every session reported, kept after it ends while
        // something on screen still descends through it — what lets TeamTree
        // find T4's nearest surviving ancestor. Refreshed and pruned once per
        // scan by SyncOrbGroups.
        private readonly Dictionary<string, string> _knownLead = new(StringComparer.Ordinal);

        // Each session's last membership key, to notice CB-10's key change.
        private readonly OrbGroupSync.KeyTracker _groupKeys = new();

        // The remembered leads, with every live status's current Lead laid over
        // them, so a tree built between scans is never a scan behind.
        private Dictionary<string, string> KnownLeads()
        {
            var leads = new Dictionary<string, string>(_knownLead, StringComparer.Ordinal);
            foreach (var (id, status) in _statuses)
            {
                if (string.IsNullOrEmpty(status.Lead)) leads.Remove(id);
                else leads[id] = status.Lead;
            }

            return leads;
        }

        // The team tree over every orb that has a window, in display order.
        internal TeamTree Teams() =>
            new(DisplayOrder().Where(_windows.ContainsKey).ToList(), KnownLeads());

        // The group an orb is in: its root's (T1). Explicit membership first,
        // then the CB-14 classification — OrbGroupMenu.ContextFor's rule.
        private string GroupIdOf(TeamTree tree, string id) =>
            OrbGroupMenu.ContextFor(
                tree.RootOf(id), Array.Empty<string>(), _statuses,
                OrbweaverSettings.OpenClawHeartbeatMode, OrbweaverSettings.OpenClawCronMode).GroupId;

        // What every scan does for groups, before it lays anything out:
        //  - remembers this scan's leads and forgets ones nothing descends from;
        //  - carries a membership across a key that changed (CB-10);
        //  - gives a team member with no group of its own its root's (T3), so a
        //    member that outlives its lead keeps the group.
        // None of these moves an orb, so none of them breaks CB-161.
        private void SyncOrbGroups()
        {
            foreach (var (id, status) in _statuses)
            {
                if (string.IsNullOrEmpty(status.Lead)) _knownLead.Remove(id);
                else _knownLead[id] = status.Lead;
            }

            var tree = Teams();
            var keep = TeamTree.Reachable(tree.Present, _knownLead);
            foreach (var stale in _knownLead.Keys.Where(id => !keep.Contains(id)).ToList())
                _knownLead.Remove(stale);

            foreach (var (old, renamed) in _groupKeys.Observe(
                         tree.Present.Select(id => (id, OrbGroupMenu.KeyOf(id, _statuses)))))
            {
                OrbweaverSettings.CarryOrbGroupMembership(old, renamed);
            }

            // T3: every team of more than one, root key first. Idempotent and
            // conservative — it fills only member keys with no entry of their
            // own, and does nothing for a root in its group by classification —
            // so asking every scan costs a lookup, not a write. A team move
            // that should overwrite goes through AssignTeamToGroup instead.
            foreach (var root in tree.Present.Select(tree.RootOf).Distinct(StringComparer.Ordinal).ToList())
            {
                var team = tree.TeamOf(root);
                if (team.Count < 2) continue;

                OrbweaverSettings.EnsureTeamMembership(
                    OrbGroupMenu.KeyOf(root, _statuses),
                    team.Skip(1).Select(id => OrbGroupMenu.KeyOf(id, _statuses)));
            }
        }

        // --- IOrbDragHost: what the drag (S4) asks of the roster ----------------
        // Explicit, so none of it widens SessionManager's public surface; the
        // drag reaches it through OrbDragHost.Current, which Start assigns.
        // Every list is over visible orbs, the ones a pointer can be on.

        private TeamTree VisibleTeams() =>
            new(DisplayOrder().Where(id => _windows.TryGetValue(id, out var w) && w.IsVisible).ToList(),
                KnownLeads());

        IReadOnlyList<string> IOrbDragHost.TeamIdsOf(string sessionId) => VisibleTeams().TeamOf(sessionId);

        IReadOnlyList<string> IOrbDragHost.OrbIdsInGroup(string groupId)
        {
            var tree = VisibleTeams();
            return tree.Present.Where(id => GroupIdOf(tree, id) == groupId).ToList();
        }

        OrbWindow? IOrbDragHost.WindowOf(string sessionId) =>
            _windows.TryGetValue(sessionId, out var window) ? window : null;

        string IOrbDragHost.GroupIdOf(string sessionId) => GroupIdOf(VisibleTeams(), sessionId);

        string IOrbDragHost.PositionKeyOf(string sessionId) => OrbGroupMenu.KeyOf(sessionId, _statuses);

        // The orb's visible circle, in Position units: its centre is CentreDip
        // into the window and its radius OrbRadius (member scale and user size
        // already in it), both scaled by OrbWindow.UnitsPerDip — the factor the
        // drag divides back out (OrbDragCommit), so the two are one factor by
        // construction. Measured on a Retina MacBook (tools/backdrop-probe
        // --scales): UnitsPerDip 1, DesktopScaling 1, RenderScaling 2 — so
        // RenderScaling is the one that would have been wrong.
        (double X, double Y, double R) IOrbDragHost.DiscOf(string sessionId) =>
            _windows.TryGetValue(sessionId, out var w) ? DiscOf(w) : (0, 0, 0);

        internal static (double X, double Y, double R) DiscOf(OrbWindow w)
        {
            var scaling = OrbWindow.UnitsPerDip(w);
            return (w.Position.X + w.CentreDip * scaling,
                    w.Position.Y + w.CentreDip * scaling,
                    w.OrbRadius * scaling);
        }

        // Every group with an orb on screen outside `excludedIds`, with those
        // orbs' discs. Outline stays empty until the backdrops (S5) can say
        // what they drew — the contract allows it, and OrbSnap measures from
        // the discs alone meanwhile.
        IReadOnlyList<SnapCandidate> IOrbDragHost.CandidatesFor(IReadOnlyCollection<string> excludedIds)
        {
            var tree = VisibleTeams();
            var discs = new Dictionary<string, List<(double X, double Y, double R)>>(StringComparer.Ordinal);

            foreach (var id in tree.Present)
            {
                if (excludedIds.Contains(id)) continue;

                var group = GroupIdOf(tree, id);
                if (!discs.TryGetValue(group, out var list)) discs[group] = list = new();
                list.Add(DiscOf(_windows[id]));
            }

            return discs
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new SnapCandidate(p.Key, Array.Empty<Point>(), p.Value))
                .ToList();
        }

        // Which group the drag would join, for the backdrops to draw. Recorded
        // until S5's GroupBackdrops.Highlight exists to hand it to.
        internal string? HighlightedGroup { get; private set; }

        void IOrbDragHost.HighlightGroup(string? groupId) => HighlightedGroup = groupId;

        void IOrbDragHost.ShiftGroupAnchor(string groupId, int dx, int dy) =>
            ShiftArrangementAnchor(dx, dy, groupId);
    }
}

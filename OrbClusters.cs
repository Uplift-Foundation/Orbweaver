namespace Orbweaver
{
    // Which of the three groups an orb belongs to, when the arrangement is
    // allowed to draw more than one shape at a time.
    //
    // Not the same question as SessionKind, and deliberately a second enum
    // rather than more cases on that one. SessionKind answers "what kind of
    // conversation is this", which is about who can read it and is drawn as a
    // badge. This answers "which shape does this orb get gathered into", which
    // is about who is on the other end — nobody, in both of the cases below
    // that are not Chats. An agent's main session is a Direct-shaped thing that
    // a timer drives; a scheduled job is a Cron-shaped thing that a timer
    // drives; and for the purpose of arranging them those two belong together
    // and apart from the conversations somebody is actually having.
    public enum OrbCluster
    {
        // Everything a person might be talking in, plus everything this app
        // shows that has nothing to do with a gateway — local terminals,
        // background jobs, remote sessions, room stand-ins. The default, and
        // the group that always exists.
        Chats,

        // Sessions the gateway's heartbeat drives. See OpenClawHeartbeat for
        // which those are and why the detection is structural rather than
        // per-turn.
        Heartbeats,

        // Scheduled jobs. See OpenClawSessionKind, which reads these off the
        // session key's third segment.
        Crons,

        // Sessions with no chat surface behind them: an agent's main session
        // reached through none, and a New Chat. See OpenClawNative.
        Native
    }

    // What the user has asked for one of the two timer-driven clusters.
    //
    // Three answers rather than the two a switch can hold, because the switch
    // this replaces made the wrong pair of them the only options. Eleven orbs
    // nobody is talking to, mixed into the same heart as the conversations that
    // matter, is not much better than not seeing the timers at all — and those
    // were the only two things `openclawShowHeartbeats` could say.
    public enum ClusterMode
    {
        // No orb at all. What `openclawShowHeartbeats: false` did.
        Hidden,

        // An orb, gathered into the same shape as everything else. What the app
        // has always done, and still the default — see OrbweaverSettings.
        WithChats,

        // An orb, gathered into a shape of its own, drawn beside the chats'
        // shape rather than inside it.
        OwnShape
    }

    // Turning "is this a heartbeat", "is this a cron" and the two settings into
    // the two things the rest of the app needs: whether an orb exists, and which
    // shape it joins.
    //
    // Pure and argument-fed for the same reason OrbArrangement and
    // OpenClawSessionKind are — the mistake it can make is a session shown in
    // the wrong shape or not shown at all, and neither is visible from reading
    // the code that calls it. Two callers depend on it agreeing with itself:
    // OpenClawSessions decides whether to keep a session during the scan, and
    // SessionManager decides which band its orb lands in. A classifier that said
    // Hidden in one place and group 1 in the other would put an orb in a shape
    // and then take it off the screen.
    public static class OrbClusters
    {
        // Heartbeat wins over cron, and the order matters for a real payload
        // rather than a hypothetical one: the gateway's own heartbeat job is
        // listed by `openclaw cron list --all` as "Heartbeat (<agent-id>)", so a
        // session carrying that label is a cron *and* a heartbeat by both
        // detectors at once. Counting it twice is not possible — an orb is in
        // one shape — so one of the two has to lose, and the heartbeat is the
        // more specific answer: every gateway with heartbeats enabled has one of
        // these per agent, whereas a cron is whatever somebody scheduled.
        //
        // Also the safer direction if the user has set the two modes
        // differently. Hiding the heartbeat when the user asked to hide
        // heartbeats is what they asked for; showing it among the crons they
        // wanted separated is a surprise.
        //
        // CB-270: native comes last of the three. An agent's own main session is a
        // heartbeat target by OpenClawHeartbeat's structural rule, and the
        // gateway's heartbeat job wakes it every few minutes, so it is a heartbeat
        // first and native only if it is not one. CB-270's first version put
        // native ahead of heartbeat so the native setting could govern those
        // mains, and the result was the opposite of what a person who hid
        // heartbeats wanted: the main session kept coming back because the very
        // timer they had hidden kept waking it. So "Heartbeat sessions" governs an
        // agent's main sessions, and "Agents' own sessions" governs the native
        // sessions that are not heartbeats, in practice a New Chat. A session the
        // gateway reports pinned is shown whatever its cluster says; see
        // OpenClawSessions.Parse.
        public static OrbCluster Of(bool heartbeat, SessionKind kind, bool native)
        {
            if (heartbeat) return OrbCluster.Heartbeats;
            if (kind == SessionKind.Cron) return OrbCluster.Crons;
            if (native) return OrbCluster.Native;

            return OrbCluster.Chats;
        }

        // What the user asked for this cluster. Chats have no mode of their own
        // — there is no setting that hides every conversation, and the shape
        // they gather into is the main ArrangeShape — so they always answer
        // WithChats, which is exactly what "group 0, and it exists" means below.
        //
        // The native mode is a required parameter, not a defaulted one: two
        // callers disagreeing about it puts an orb in a shape and then takes it
        // off the screen, and a required parameter makes the compiler find every
        // caller.
        public static ClusterMode ModeOf(
            OrbCluster cluster, ClusterMode heartbeats, ClusterMode crons, ClusterMode native)
            => cluster switch
            {
                OrbCluster.Heartbeats => heartbeats,
                OrbCluster.Crons => crons,
                OrbCluster.Native => native,
                _ => ClusterMode.WithChats
            };

        // Whether this orb exists at all.
        public static bool Visible(
            OrbCluster cluster, ClusterMode heartbeats, ClusterMode crons, ClusterMode native)
            => ModeOf(cluster, heartbeats, crons, native) != ClusterMode.Hidden;

        // Which shape this orb joins, as an index into the shapes array
        // OrbArrangement is handed: 0 chats, 1 heartbeats, 2 crons, 3 native. User groups follow at 4.
        //
        // Fixed slots rather than a dense count of the groups actually in use,
        // because a dense index would mean group 1 is heartbeats on a screen
        // that has some and crons on one that does not — so the shape at index 1
        // would depend on the sessions rather than on the setting. The
        // arrangement drops the empty slots itself (see OrbArrangement.Bands),
        // which is the right place for that: it is the only code that knows how
        // many bands there is room for.
        //
        // A cluster the user hid answers 0 rather than throwing. Nothing should
        // reach here with a hidden orb — Visible above is checked during the
        // scan, before an orb is made — and if something does, being drawn with
        // the chats is a better failure than a crash or a fourth empty band.
        //
        // Two statements rather than a switch over all three clusters, and not
        // for brevity: Chats can never pass the first line, because ModeOf
        // answers WithChats for them whatever the settings say. Written as a
        // switch, its Chats arm is a line nothing can execute — which is a line
        // that will read as a coverage gap forever and can never be defended
        // with a test. Asking the question ModeOf already answers is the way not
        // to have one.
        public static int GroupOf(
            OrbCluster cluster, ClusterMode heartbeats, ClusterMode crons, ClusterMode native)
        {
            if (ModeOf(cluster, heartbeats, crons, native) != ClusterMode.OwnShape) return 0;

            return cluster switch
            {
                OrbCluster.Heartbeats => 1,
                OrbCluster.Crons => 2,
                _ => 3
            };
        }

        // --- settings wire format ---------------------------------------------
        // Strings on the wire, an enum in the app: the same split
        // SessionStatus.Cli makes, and for the same reason — settings.json is
        // read and written by hand often enough that a number would be
        // unreadable, and a value from a newer version has to mean something in
        // an older one rather than throwing.

        public static string Name(ClusterMode mode) => mode switch
        {
            ClusterMode.Hidden => "hidden",
            ClusterMode.OwnShape => "own",
            _ => "chats"
        };

        // Anything unrecognised — a typo, or a mode a later version invented —
        // reads as WithChats, which is the default and the behaviour the app has
        // always had. Hidden would be the wrong fallback for a value nobody
        // meant: it takes orbs off the screen, and an orb that is missing for no
        // stated reason is the hardest kind of bug to notice.
        public static ClusterMode Parse(string? value, ClusterMode fallback = ClusterMode.WithChats)
            => (value ?? "").Trim().ToLowerInvariant() switch
            {
                "hidden" => ClusterMode.Hidden,
                "own" => ClusterMode.OwnShape,
                "chats" => ClusterMode.WithChats,
                _ => fallback
            };
    }
}

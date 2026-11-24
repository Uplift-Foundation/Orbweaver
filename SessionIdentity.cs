using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Who a session is, whoever it happens to be talking to.
    //
    // There are two answers to that question in this app and they arrived a
    // year apart. A gateway session's name and picture come from OpenClaw's
    // agent list; a local one's now come from the CLAUDE.md sitting beside its
    // work. The chat panel had the first of those wired straight into it — it
    // asked OpenClawSessions directly — which is why a local persona would
    // have needed the same three questions asked a second way in the same
    // method, with the two answers diverging at whichever call site somebody
    // forgot.
    //
    // So the panel asks one function and this decides which registry knows.
    // The orb does not: it is handed a SessionStatus and already knows its own
    // source, and the branch it wants is about *drawing* (a picture replaces
    // the letters) rather than about who the session is.
    internal static class SessionIdentity
    {
        // Every gateway session id carries this, agents and rooms alike — see
        // OpenClawSessions.AvatarForSession, which splits the two below it.
        // Keying on the prefix rather than on SessionSource is deliberate: the
        // chat panel is bound to an IRemoteChatSession and has no status to
        // read a source off, and this is the discriminator it already has.
        private const string GatewayPrefix = "openclaw:";
        private const string PeerPrefix = "rc:";

        // A cloud session has no registry behind it at all: no agent list, no
        // peer persona, and no CLAUDE.md on this disk. It is named here so the
        // panel can tell it apart from a local id rather than to look anything
        // up — IsCloud answers "borrow the orb's letters", which is what the
        // Unknown face already does for everything unrecognised.
        private const string CloudPrefix = "cloud:";

        // Emoji is a gateway-only field: OpenClaw keeps one per agent, and a
        // CLAUDE.md persona deliberately has no equivalent — the grammar names
        // a picture or nothing (see PersonaMarkdown), because an emoji in
        // prose is a decoration far more often than it is an identity.
        internal sealed record Face(
            string? Name, string? Emoji, OpenClawAvatars.Avatar? Avatar, bool Gateway)
        {
            // Whether this session has a face of its own to draw, as opposed to
            // a header that should borrow the letters and colour off the orb it
            // opened from.
            //
            // A picture always counts. A *name* counts only for a gateway
            // agent, and that asymmetry is the point: a local persona's name is
            // already on its orb, as the letters in the glyph, so borrowing
            // gives the header "Le" on the orb's own colour. Treating the name
            // as a face instead would send it down the path that asks
            // OpenClawSessions for an agent colour, get null — there is no
            // agent — and draw those same letters on an invisible circle. The
            // worse-looking of two answers to a question that was already
            // answered correctly.
            internal bool DrawsItsOwnCircle =>
                Avatar is not null
                || (Gateway && (!string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(Emoji)));
        }

        internal static readonly Face Unknown = new(null, null, null, false);

        internal static bool IsGateway(string? sessionId) =>
            sessionId is not null && sessionId.StartsWith(GatewayPrefix, StringComparison.Ordinal);

        internal static bool IsPeer(string? sessionId) =>
            sessionId is not null && sessionId.StartsWith(PeerPrefix, StringComparison.Ordinal);

        internal static bool IsCloud(string? sessionId) =>
            sessionId is not null && sessionId.StartsWith(CloudPrefix, StringComparison.Ordinal);

        // Just the name, without asking anybody for a picture.
        //
        // Its own entry point rather than For().Name because the two are asked
        // at very different rates: the header's title and the speaker chip are
        // recomputed on every poll tick, twice a second, where the portrait is
        // applied once at bind. Both halves of the name are a dictionary
        // lookup; the picture half is not — a *room's* avatar is a composite
        // built from whoever is in the room, and reaching it means sorting a
        // member list and assembling a cache key before the cache can answer.
        // Small work, done pointlessly, forever.
        internal static string? NameFor(string? sessionId) =>
            string.IsNullOrEmpty(sessionId) ? null
                : IsGateway(sessionId) ? OpenClawSessions.IdentityForSession(sessionId)?.Name
                : IsPeer(sessionId) ? PeerPersonas.For(sessionId)?.Name
                : LocalPersonas.For(sessionId)?.Name;

        // The local half of that alone: what a CLAUDE.md persona is called, and
        // null for a gateway session.
        //
        // The asymmetry is not an oversight. A gateway session's display name is
        // already built from its identity — "Nova — #general", who and where —
        // and a *room's* display name is the room while its identity is
        // whichever agent is in the session key, so substituting the identity
        // name there would put one agent's name on the header of a channel four
        // of them are talking in. See RefreshSoleSpeaker's own comment, which
        // has drawn that distinction since long before personas existed.
        internal static string? LocalNameFor(string? sessionId) =>
            IsGateway(sessionId) ? null : NameFor(sessionId);

        internal static Face For(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return Unknown;

            if (IsGateway(sessionId))
            {
                var identity = OpenClawSessions.IdentityForSession(sessionId);

                // Asked even when there is no identity, because a room has no
                // identity and still has a picture — the composite of everyone
                // in it. That is the one case where these two disagree.
                return new Face(
                    identity?.Name,
                    identity?.Emoji,
                    OpenClawSessions.AvatarForSession(sessionId),
                    Gateway: true);
            }

            if (IsPeer(sessionId))
            {
                var persona = PeerPersonas.For(sessionId);
                if (persona is null) return Unknown;
                return new Face(
                    persona.Name,
                    null,
                    persona.Avatar is null ? null : OpenClawAvatars.For(PeerPersonas.AvatarKey(sessionId), persona.Avatar),
                    Gateway: false);
            }

            // CB-445: a Paperclip agent's face is the picture Paperclip holds for it, found through the
            // published snapshot by its session key. No picture, or an agent not published: Unknown,
            // so the header borrows the orb's letters as it did before.
            if (sessionId.StartsWith(PaperclipScan.KeyPrefix, StringComparison.Ordinal))
            {
                var agent = PaperclipSessions.Snapshot().FirstOrDefault(a => PaperclipScan.KeyFor(a.Id) == sessionId);
                var avatar = PaperclipAvatars.For(agent?.AvatarFile);
                return avatar is null ? Unknown : new Face(agent!.Name, null, avatar, Gateway: false);
            }

            var localPersona = LocalPersonas.For(sessionId);
            if (localPersona is null) return Unknown;

            // Decoded through the same cache the gateway's pictures use, under
            // a key that cannot collide with an agent id — which is what
            // LocalPersonas.AvatarKey exists to promise. Re-decoding a portrait
            // on every poll tick is what that cache was built to avoid, and a
            // persona is read on the same two-second cadence.
            return new Face(
                localPersona.Name,
                null,
                localPersona.AvatarPath is null
                    ? null
                    : OpenClawAvatars.ForFile(LocalPersonas.AvatarKey(sessionId), localPersona.AvatarPath),
                Gateway: false);
        }
        // --- Whose voice reads a reply out loud ---------------------------------

        // The same question as For() above, asked about speech instead of a
        // face, and answered in the same one place for the same reason.
        //
        // It was not answered in one place until CB-165 came back. The orb's
        // speak button asked LocalPersonas directly; the chat panel's asked
        // PeerPersonas and OpenClawSessions and nothing else, so a *local*
        // session — every ordinary Claude Code session, the common case —
        // resolved to null there and spoke in the user's global voice however
        // plainly its CLAUDE.md named one. Two buttons labelled the same thing,
        // one honouring a persona and one ignoring it, is not a defect either
        // button can be blamed for; it is the absence of this function.
        internal enum VoiceSource
        {
            // A mirrored peer session: the persona travelled over the wire with
            // it, so the sender's own voice is the right one.
            Peer,

            // A gateway agent, whose voice is in its workspace identity.
            GatewayAgent,

            // The user's own selection, deliberately. Three unrelated kinds land
            // here and none of them is an oversight: a gateway *room* has no
            // single agent to borrow a voice from (see AvatarForSession, which
            // draws a composite for exactly the same reason), a cloud session
            // has no persona registry on this disk at all (CB-164), and a
            // session with no id is not a session yet.
            Global,

            // Everything else, which is a local Claude Code session and whose
            // persona comes from the CLAUDE.md beside its work.
            Local,
        }

        // Pure, and separate from the resolution below it, because this is the
        // part that was wrong and the part a test can pin without a voice engine
        // on the machine. A case per arm costs nothing; the arm that was missing
        // cost this ticket a second trip through QA.
        internal static VoiceSource VoiceSourceFor(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return VoiceSource.Global;
            if (IsPeer(sessionId)) return VoiceSource.Peer;

            // Agent or room, told apart by the key rather than by a second
            // prefix constant: "agent:<id>:…" is what AgentIdOf reads, and a
            // room key has no agent in that position.
            if (IsGateway(sessionId))
            {
                return OpenClawSessions.AgentIdOf(sessionId) is null
                    ? VoiceSource.Global
                    : VoiceSource.GatewayAgent;
            }

            return IsCloud(sessionId) ? VoiceSource.Global : VoiceSource.Local;
        }

        // The voice a session speaks in, or null for "the user's own setting".
        //
        // Null rather than a resolved global voice, because every caller already
        // has to handle a persona naming a voice this machine cannot build — see
        // LocalPersonas.VoiceForSession, whose three ways of answering null all
        // mean the same thing. Collapsing them here would make "no persona" and
        // "a persona whose voice is missing" different, and they are not.
        //
        // The options list is injected for the reason both of the resolvers this
        // replaces injected it: enumerating the real ones asks Kokoro to list
        // itself and runs the user's own listing command, which is two process
        // launches nobody wants in a test.
        internal static TextToSpeech.VoiceOption? VoiceFor(
            string? sessionId, IEnumerable<TextToSpeech.VoiceOption> options) =>
            VoiceSourceFor(sessionId) switch
            {
                VoiceSource.Peer => PeerPersonas.VoiceForSession(sessionId, options),
                VoiceSource.GatewayAgent => OpenClawSessions.VoiceForSession(sessionId!, options),
                VoiceSource.Local => LocalPersonas.VoiceForSession(sessionId, options),
                _ => null,
            };

        // CB-200: which engines orbs' own voices run on, for the settings
        // window's Speech volume note — so the row can say "a custom command
        // only gets the level if it reads it" when an orb's persona speaks
        // through one while the global engine does not.
        //
        // The same resolution VoiceFor does, with one deliberate difference: a
        // blend is judged by VoiceBlend.Resolve rather than VoiceBlends.Option,
        // because Option writes the blended voice file to disk and a settings
        // row must not. A blend is always Kokoro, so resolving is enough to
        // know the engine; if writing the file later failed, that orb would
        // speak in the global voice instead, which the global line covers.
        //
        // Empty when no orb has a voice of its own. Null when some do but no
        // voice list has been built yet (TextToSpeech.CachedVoiceOptions), so
        // the engines are unknown — AudioVolume.SpeechVolumeNote states both
        // rules then rather than guessing.
        internal static IReadOnlyCollection<TextToSpeech.SpeakEngine>? OrbEngines(
            IReadOnlyCollection<string> requestedVoices, IReadOnlyList<TextToSpeech.VoiceOption>? options)
        {
            if (requestedVoices.Count == 0) return Array.Empty<TextToSpeech.SpeakEngine>();
            if (options is null) return null;

            var engines = new HashSet<TextToSpeech.SpeakEngine>();
            foreach (var requested in requestedVoices)
            {
                if (EngineOf(requested, options) is { } engine) engines.Add(engine);
            }

            return engines;
        }

        internal static TextToSpeech.SpeakEngine? EngineOf(
            string requested, IEnumerable<TextToSpeech.VoiceOption> options)
        {
            var blend = VoiceBlend.Parse(requested);
            if (blend is null) return TextToSpeech.MatchVoiceOption(requested, options)?.Engine;
            return VoiceBlend.Resolve(blend, options) is null ? null : TextToSpeech.SpeakEngine.Neural;
        }

        // Every voice an orb's persona asks for, from all three places a
        // persona can come from: this machine's markdown (LocalPersonas), a
        // paired Buddy (PeerPersonas), and the OpenClaw gateway's agents. What
        // is known right now, in memory — the settings window reads it when
        // it builds the row.
        internal static IReadOnlyCollection<string> PersonaVoiceRequests() =>
            LocalPersonas.Voices()
                .Concat(PeerPersonas.Voices())
                .Concat(OpenClawSessions.PersonaVoices())
                .Where(voice => !string.IsNullOrWhiteSpace(voice))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Excluded from coverage: AllVoiceOptions is the two process launches
        // above. The decision it feeds is the overload above, which is tested.
        [ExcludeFromCodeCoverage]
        internal static TextToSpeech.VoiceOption? VoiceFor(string? sessionId) =>
            VoiceFor(sessionId, TextToSpeech.AllVoiceOptions());

        // Same arms as VoiceFor, for the same reasons: a rate with no voice
        // behind it has nothing to qualify, and only the neural engine has such
        // a knob at all.
        internal static double? RateFor(string? sessionId) =>
            VoiceSourceFor(sessionId) switch
            {
                VoiceSource.Peer => PeerPersonas.RateForSession(sessionId),
                VoiceSource.GatewayAgent => OpenClawSessions.RateForSession(sessionId!),
                VoiceSource.Local => LocalPersonas.RateForSession(sessionId),
                _ => null,
            };
    }
}

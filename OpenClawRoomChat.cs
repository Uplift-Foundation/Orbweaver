using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace Orbweaver
{
    // A channel as one conversation, instead of as one conversation per agent
    // standing in it.
    //
    // The gateway has no such object. It keeps a session per (agent, channel),
    // so a room with six agents in it is six transcripts, each of them that
    // agent's own view: their own replies, and everyone else's messages arriving
    // as input. Opening any one of them shows a sixth of the room and calls it
    // the conversation.
    //
    // Merging them is also what makes attribution free, which is the part worth
    // knowing. Inside `agent:nova:discord:channel:X`, every assistant message
    // *is Nova* — there is nothing to parse and nothing to guess. So a speaker
    // and their colour come from which transcript a message was found in, and
    // the machine header OpenClaw glues onto agent-to-agent messages, which an
    // earlier attempt leaned on, is not needed for this at all.
    //
    // Owns nothing: it subscribes to the member sessions that do, and rebuilds
    // its view when any of them changes.
    internal sealed class OpenClawRoomChatSession :
        IRemoteChatSession, IRemoteChatComposer, IRemoteChatBacklog, IRemoteChatRoom
    {
        // Always true: a room is the one implementation IRemoteChatRoom
        // exists to mark. See that interface's own comment for why this is a
        // property rather than a bare marker, and ChatPanel.TurnView.SpeakerName
        // (CB-36) for the one place that reads it.
        public bool IsRoom => true;


        private readonly List<ChatTurn> _history = new();
        private readonly List<Member> _members = new();

        // The room's own turns: its System notes, and the message you just
        // typed, before either exists anywhere else.
        //
        // Held separately because Rebuild discards everything and re-merges from
        // the members, and a room owns neither of these. Until now that meant
        // both were lost — the "Replying is off" note this class has always
        // written survived only until the next background rebuild, which any
        // member event triggers, so a note explaining why nothing had been sent
        // vanished on its own a moment later. Nobody noticed because the only
        // note that existed appeared while nothing was arriving to rebuild for.
        //
        // Writing them into a member's transcript instead was the obvious
        // alternative and does not work: the merge reads assistant turns and
        // user turns and drops System ones, so a note put there is invisible in
        // the room it is about.
        //
        // Bounded, like every other transcript here. Small, because these are
        // only ever the last few things this window did — a note per failed
        // send, and a message per send until the gateway's own copy comes back
        // and the merge dedupes against it.
        private readonly List<ChatTurn> _local = new();

        private const int KeepLocal = 32;

        private sealed record Member(OpenClawChatSession Chat, string Agent, string Colour);

        public OpenClawRoomChatSession(string sessionId, string displayName)
        {
            SessionId = sessionId;
            DisplayName = displayName;
        }

        public string SessionId { get; }

        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;

        // IRemoteChatBacklog is implemented for one member of it: the panel
        // subscribes to HistoryReplaced through this interface and nowhere else,
        // so without it a merged transcript is assembled and never drawn.
        //
        // There is more as long as any member is still holding some back — that
        // member is what stops the window opening further, so it is exactly what
        // reaching the top should fetch.
        //
        // This was declined at first, on the grounds that pulling a page from
        // six transcripts would interleave six unrelated stretches of time into
        // the middle of what you were reading. That was true while the room
        // showed everything it had, and stopped being true once the view is cut
        // to where every member reaches: paging then has one meaning, which is
        // to move that line back, and the interleaving happens above where you
        // are reading rather than through it.
        public bool HasMore => _members.Any(m => m.Chat.HasMore);

        public async Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            var before = TrustworthyFrom();

            await PageUntilTheWindowMovesAsync(before, ct);

            return TrustworthyFrom() != before;
        }

        // A few rounds per scroll, not one. Paging the constraining member once
        // often just makes a different member the constraint, and a scroll that
        // fetched a page and moved the window by nothing would read as the top of
        // the conversation.
        //
        // Excluded from coverage: the loop only goes round a second time when a
        // page actually came back, which needs a gateway answering a chat.history
        // request. With none, the first round breaks — which is the behaviour
        // LoadOlderAsync's tests assert, through the answer it returns rather
        // than through this.
        [ExcludeFromCodeCoverage]
        private async Task PageUntilTheWindowMovesAsync(
            DateTimeOffset? before, CancellationToken ct)
        {
            for (var round = 0; round < 3; round++)
            {
                if (!await PageBindingMemberAsync(ct)) break;
                if (TrustworthyFrom() != before) break;
            }
        }

        public event Action<int>? HistoryPrepended;

        public string ComposerHint => OrbweaverSettings.OpenClawReplyEnabled
            ? "Message the channel…"
            : "Replying is off";

        // How many panels are open on this room — CB-92.
        //
        // A room owns no transcript of its own worth measuring: `_history` is a
        // merge of its members', rebuilt from them, so what it costs is what
        // they cost. What it does own is the answer to "is anybody looking at
        // this member", which for a member of an open room is yes even though
        // no panel is bound to that member directly. So the count is forwarded
        // rather than accounted for: an open room holds its members resident,
        // and closing it lets them start ageing.
        private int _panels;

        internal void PanelOpened()
        {
            _panels++;
            if (_panels != 1) return;

            foreach (var member in _members) member.Chat.PanelOpened();
        }

        internal void PanelClosed()
        {
            if (_panels == 0) return;

            _panels--;
            if (_panels != 0) return;

            foreach (var member in _members) member.Chat.PanelClosed();
        }

        // Called on every scan, because who is in a room changes: an agent that
        // has not spoken lately drops out of the session list, and one that
        // joins has to start being listened to.
        public void SetMembers(IReadOnlyList<(OpenClawChatSession Chat, string Agent, string Colour)> members)
        {
            var wanted = members.Select(m => m.Chat.GatewayKey).ToHashSet(StringComparer.Ordinal);
            var have = _members.Select(m => m.Chat.GatewayKey).ToHashSet(StringComparer.Ordinal);

            if (wanted.SetEquals(have))
            {
                // The same people. Their names and colours can still have
                // improved — agents.list lands after the first connection — so
                // those are refreshed without tearing the subscriptions down.
                for (var i = 0; i < _members.Count; i++)
                {
                    var match = members.FirstOrDefault(m => m.Chat.GatewayKey == _members[i].Chat.GatewayKey);
                    if (match.Chat is null) continue;

                    // The same key can now arrive on a *different* object:
                    // CB-92 lets an idle conversation be dropped from
                    // OpenClawSessions.Chats, and the next lookup builds a
                    // fresh session for that key. Matching on the key alone and
                    // swapping the record — which is all this did — would leave
                    // the room subscribed to the instance nobody feeds any more
                    // and unsubscribed from the one that now carries the
                    // conversation, so the channel would quietly stop updating.
                    // Nothing before eviction existed could produce that, which
                    // is why the check was not needed until now.
                    if (!ReferenceEquals(match.Chat, _members[i].Chat))
                    {
                        Unsubscribe(_members[i].Chat);
                        if (_panels > 0) _members[i].Chat.PanelClosed();

                        Subscribe(match.Chat);
                        if (_panels > 0) match.Chat.PanelOpened();
                    }

                    _members[i] = new Member(match.Chat, match.Agent, match.Colour);
                }

                return;
            }

            foreach (var member in _members)
            {
                Unsubscribe(member.Chat);

                // A member leaving a room this panel is open on is that
                // member's panel closing, as far as residency is concerned —
                // otherwise a session that dropped out of a channel would be
                // held open by a room that no longer shows it.
                if (_panels > 0) member.Chat.PanelClosed();
            }

            _members.Clear();

            foreach (var (chat, agent, colour) in members)
            {
                _members.Add(new Member(chat, agent, colour));
                Subscribe(chat);
                if (_panels > 0) chat.PanelOpened();
            }

            Rebuild();
        }

        private void Subscribe(OpenClawChatSession chat)
        {
            chat.TurnAdded += OnMemberChanged;
            chat.TurnUpdated += OnMemberChanged;
            chat.HistoryReplaced += ScheduleRebuild;
            chat.HistoryPrepended += OnMemberPrepended;
            chat.TurnsRemoved += OnMemberRemoved;

            // CB-270 Stage 6: an approval belongs to one member's session and is
            // answered in that member's own panel, but the room is not silent about
            // it. Said once per request, from the same record.
            Action announce = () => AnnounceApprovals(chat);
            _approvalHandlers[chat] = announce;
            chat.ApprovalsChanged += announce;
            announce();
        }

        private void Unsubscribe(OpenClawChatSession chat)
        {
            chat.TurnAdded -= OnMemberChanged;
            chat.TurnUpdated -= OnMemberChanged;
            chat.HistoryReplaced -= ScheduleRebuild;
            chat.HistoryPrepended -= OnMemberPrepended;
            chat.TurnsRemoved -= OnMemberRemoved;

            if (_approvalHandlers.Remove(chat, out var announce)) chat.ApprovalsChanged -= announce;
        }

        private readonly Dictionary<OpenClawChatSession, Action> _approvalHandlers = new();
        private readonly HashSet<string> _announcedApprovals = new(StringComparer.Ordinal);

        // One System line per pending request, naming who is waiting.
        private void AnnounceApprovals(OpenClawChatSession chat)
        {
            var agent = _members.FirstOrDefault(m => ReferenceEquals(m.Chat, chat))?.Agent ?? chat.DisplayName;

            foreach (var approval in OpenClawSessions.PendingFor(chat.GatewayKey))
            {
                if (_announcedApprovals.Add(approval.Id)) Note(OpenClawApprovalText.RoomLine(agent));
            }
        }

        private void OnMemberChanged(ChatTurn _) => ScheduleRebuild();

        // CB-270: a member's live rows replaced by history at the end of a run.
        // The turns that take their place raise TurnAdded, but a replacement that
        // only removes would otherwise leave the merge showing what is gone.
        private void OnMemberRemoved(int _, int __) => ScheduleRebuild();

        private bool _rebuildQueued;

        // Coalesced to one rebuild per pass of the dispatcher.
        //
        // A streaming reply raises TurnUpdated per snapshot, several times a
        // second, and each one would otherwise re-merge and re-sort every
        // transcript in the room — for a single row whose text changed. The
        // panel cannot draw faster than this anyway.
        private void ScheduleRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;

            Dispatcher.UIThread.Post(() =>
            {
                _rebuildQueued = false;
                Rebuild();
            }, DispatcherPriority.Background);
        }

        // Paging a member back is the one thing that widens the window this can
        // be trusted over, so it has to redraw — and it was missing, which meant
        // Deepen's work would not have shown until something else happened.
        private void OnMemberPrepended(int _) => ScheduleRebuild();

        // The whole view is rebuilt, rather than the one turn that changed being
        // inserted in the right place.
        //
        // A merge has no append: a message from an agent whose backlog arrives
        // late belongs in the middle, and working out where costs more than
        // redoing a few hundred rows of objects that are already in memory. The
        // panel is told the transcript was replaced, which it already handles
        // for a backlog landing after it opened.
        // internal: the merge is where five shipped bugs have now been fixed,
        // and it needs no dispatcher of its own — only ScheduleRebuild above
        // does, and that exists to coalesce, not to decide anything.
        internal void Rebuild()
        {
            // Every agent's own words, and the text of them.
            //
            // The text set is the whole trick for telling a person's message
            // apart from an agent's. Both arrive as user-role turns in the
            // transcripts of the agents that *received* them, and nothing in the
            // payload says which is which — an earlier version assumed user-role
            // meant human, and every agent's message was drawn twice: once
            // attributed in its own colour, and once as a blue bubble from you.
            //
            // What separates them is that an agent's message is *also* an
            // assistant turn somewhere in this room, and a person's is not. So
            // the assistant turns are collected first and everything matching
            // one of them is dropped on the second pass.
            var merged = new List<ChatTurn>();

            // A list rather than a set, because matching is no longer equality:
            // a relayed copy can be cut short. See SaidByAnAgent.
            var agentTexts = new List<string>();

            foreach (var member in _members)
            {
                foreach (var turn in member.Chat.History)
                {
                    if (turn.Role != ChatRole.Assistant) continue;

                    agentTexts.Add(Normalise(turn.Text));

                    merged.Add(new ChatTurn
                    {
                        Role = ChatRole.Assistant,
                        Text = turn.Text,

                        // ImageSourcePath travels with ImageUrl through every
                        // one of the four copies in this method, and not
                        // because a room needs it to fetch: the url already
                        // carries the session baked into it by
                        // TurnsFromHistory, so the picture loads either way.
                        // It is the *refusal* that needs it — CB-93's "why
                        // didn't that load" line captions itself with this
                        // path, and a merged copy that dropped it would go
                        // back to an empty slot with no explanation in exactly
                        // the view where several agents' pictures sit side by
                        // side.
                        ImageSourcePath = turn.ImageSourcePath,
                        Attachment = turn.Attachment,
                        ImageUrl = turn.ImageUrl,
                        ImageAlt = turn.ImageAlt,
                        At = turn.At,
                        IsComplete = turn.IsComplete,

                        // Free, and the whole reason to merge: a message found
                        // in Nova's transcript, in the assistant role, is Nova.
                        Speaker = member.Agent,
                        SpeakerColor = member.Colour
                    });
                }
            }

            // Anything a person said. It appears in every transcript that
            // received it and as an assistant turn in none, which is what is
            // left once the agents' own words are accounted for.
            //
            // Gathered per member first and drawn afterwards, rather than drawn
            // as it is walked. How many bubbles one text is worth is a question
            // about every member at once — see WhoseCopiesToDraw — and it cannot
            // be answered while still part-way through the first transcript.
            var candidates = new List<List<(ChatTurn Turn, string Text)>>();

            foreach (var member in _members)
            {
                var theirs = new List<(ChatTurn Turn, string Text)>();

                foreach (var turn in member.Chat.History)
                {
                    if (turn.Role != ChatRole.User) continue;

                    var text = Normalise(turn.Text);
                    if (text.Length == 0) continue;

                    // Said by an agent in this room, and already in the list
                    // attributed to whichever one. This is the same message,
                    // seen from the other side.
                    //
                    // Your own turns skip this test, and that is the CB-27 order
                    // preserved: your words are not an agent's however closely
                    // they happen to match one, and a message swallowed for
                    // coincidentally opening the way an agent opened a paragraph
                    // would be your message, gone, with the app having decided
                    // somebody else said it. The gateway said this one was
                    // yours — see OpenClawSender for the four shapes that answer
                    // it and the one that is assumed.
                    if (!turn.Mine && SaidByAnAgent(agentTexts, text)) continue;

                    theirs.Add((turn, text));
                }

                candidates.Add(theirs);
            }

            // Which member's copies of each text the room draws. Everything one
            // member holds and the winner does not is a copy of a message the
            // winner also received, so skipping it is the room taking each
            // message once.
            var carrier = WhoseCopiesToDraw(
                candidates.Select(m => (IReadOnlyList<string>)m.Select(c => c.Text).ToList())
                          .ToList());

            for (var member = 0; member < candidates.Count; member++)
            {
                foreach (var (turn, text) in candidates[member])
                {
                    if (carrier[text] != member) continue;

                    // Yours, and said so by the gateway rather than guessed.
                    //
                    // Kept at ChatRole.User with no Speaker, which is what the
                    // panel already draws in your colour and on your side; the
                    // flag is what the *transcript* needed, not the panel.
                    //
                    // Counted alongside everything else, and that is the whole
                    // reason the three copies normalise to the same string: the
                    // carrier's transcript holds what you typed, everybody
                    // else's holds the mirror with its prefix already taken off
                    // by the parser, and the optimistic copy this window added
                    // when you pressed return is the same text again. Before the
                    // prefix came off, the last two matched nothing and a
                    // successful send drew twice.
                    if (turn.Mine)
                    {
                        merged.Add(new ChatTurn
                        {
                            Role = ChatRole.User,
                            Text = turn.Text,
                            ImageSourcePath = turn.ImageSourcePath,
                            Attachment = turn.Attachment,
                            ImageUrl = turn.ImageUrl,
                            ImageAlt = turn.ImageAlt,
                            At = turn.At,
                            IsComplete = true,
                            Mine = true
                        });

                        continue;
                    }

                    // Somebody the gateway named: an agent relayed through the
                    // channel whose own session is not in this room, or (assumed
                    // — see OpenClawSender) another person in it. Both are
                    // "somebody who is not you", which is what this draws.
                    //
                    // Assistant-role with a name and no colour, so it sits on
                    // the left with an initials chip. The colour is withheld
                    // rather than invented: this is a Discord display name and
                    // the ring colours are keyed by agent id, so borrowing one
                    // would say two different speakers were the same agent —
                    // which is the class of mistake this whole file exists to
                    // avoid.
                    if (turn.Speaker is not null)
                    {
                        merged.Add(new ChatTurn
                        {
                            Role = ChatRole.Assistant,
                            Text = turn.Text,
                            ImageSourcePath = turn.ImageSourcePath,
                            Attachment = turn.Attachment,
                            ImageUrl = turn.ImageUrl,
                            ImageAlt = turn.ImageAlt,
                            At = turn.At,
                            IsComplete = true,
                            Speaker = turn.Speaker
                        });

                        continue;
                    }

                    // Nobody said who, and drawing it in your own blue was the
                    // app asserting that you did.
                    //
                    // The metadata above was consulted first and came back
                    // empty, which is a real answer rather than an omission: a
                    // gateway that has stopped sending it, or a message old
                    // enough to predate it. Three things then arrive here and
                    // look identical — something you said, something another
                    // person in the channel said, and something an agent said
                    // whose own transcript is not available to match against.
                    // Only the first belongs in your blue, and at this point
                    // nothing distinguishes it.
                    //
                    // So it is drawn as the room's own voice: left, neutral, no
                    // name. "Somebody said this" is true of all three, where
                    // "you said this" is true of one.
                    merged.Add(new ChatTurn
                    {
                        Role = ChatRole.Assistant,
                        Text = turn.Text,
                        ImageSourcePath = turn.ImageSourcePath,
                        Attachment = turn.Attachment,
                        ImageUrl = turn.ImageUrl,
                        ImageAlt = turn.ImageAlt,
                        At = turn.At,
                        IsComplete = true
                    });
                }
            }

            // Sorted once, at the bottom, after the room's own turns have gone
            // on. It used to happen here, which was correct while nothing was
            // appended afterwards; the trim below cares about times rather than
            // order, so moving it costs nothing and sorting twice would.

            // Cut back to where every member's transcript actually reaches.
            //
            // Each member loads a page at a time, and they do not cover the same
            // stretch of time — one agent's page can reach back an hour further
            // than another's. In the part only some of them cover, the missing
            // members' messages exist solely as echoes in the others, with no
            // assistant turn to attribute them against, so they came out as blue
            // bubbles from you. Historical messages were wrong far more often
            // than recent ones for exactly this reason.
            //
            // Showing less is the honest answer: before this point the room does
            // not know who was talking, and saying so by omission beats saying
            // it was you. Deepen widens it.
            var from = TrustworthyFrom();
            if (from is { } start) merged.RemoveAll(t => t.At < start);

            // The room's own turns go back on, after the trim rather than before
            // it: a note about a send that has just failed must not be cut off
            // for sitting outside a window drawn by how far the members' backlogs
            // happen to reach.
            //
            // A sent message is *dropped from this list*, not merely skipped,
            // once the gateway's own copy of it turns up in the merge — matched
            // the way every other duplicate in this file is, on the words alone,
            // because the copy that comes back is timestamped by the gateway and
            // this one was timestamped here.
            //
            // Removing rather than skipping is the fix to a leak the first
            // version had. This list is capped, and a skipped entry went on
            // occupying a slot in it forever while contributing nothing to any
            // rebuild — so a busy room quietly filled the cap with messages that
            // had already arrived, and the thirty-second one evicted a *note*
            // that was still the only record of why something had failed. The
            // fact that an entry is finished with is computed right here; using
            // it to prune costs nothing beyond saying so.
            //
            // It does mean a sent message reverts to the members' copy for good,
            // so if the carrier later drops out of the room the message goes
            // with it. That is what happens to every other message in a room —
            // all of them are the members' — and keeping this one pinned would
            // make it the exception rather than the rule.
            //
            // Notes are never matched against anything: nothing else in the
            // conversation is a System turn, so there is nothing they could
            // duplicate, and nothing that would ever prune them but the cap.
            //
            // One optimistic copy retired per copy that came back, not every
            // copy retired because one did. Saying the same thing twice used to
            // leave both local copies matching the single merged one, so the
            // second send disappeared off the screen the moment the first
            // round-tripped and stayed gone until the gateway's copy of it
            // arrived — a message the person can see they sent, missing, which
            // is the same failure CB-32 is about in the other half of this
            // method.
            //
            // Earliest first, which is what the budget below does by walking the
            // list in order: the copies that have come back are the older sends,
            // so the one left on screen is the newest and its optimistic
            // timestamp is the closest to right.
            var returned = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var turn in merged)
            {
                if (!turn.Mine) continue;

                var text = Normalise(turn.Text);
                returned[text] = returned.GetValueOrDefault(text) + 1;
            }

            _local.RemoveAll(local =>
            {
                if (!local.Mine) return false;

                var text = Normalise(local.Text);
                if (returned.GetValueOrDefault(text) <= 0) return false;

                returned[text] -= 1;
                return true;
            });

            merged.AddRange(_local);

            merged.Sort((a, b) => a.At.CompareTo(b.At));

            // Deliberately uncapped. A cap here trimmed the *front*, which is
            // the end paging adds to — so scrolling up fetched older messages
            // and then dropped them again, and the window could never open past
            // the cap however far you scrolled. What bounds this is how far the
            // members have been paged back, which is the reader's own doing.

            // Whether this is the same conversation with older messages on the
            // front, or a different one.
            //
            // It matters because the panel treats them differently: a replaced
            // transcript scrolls to the bottom, which is right when a room first
            // opens and exactly wrong when you have just scrolled to the top and
            // asked for more. Prepending keeps you where you were reading.
            var prepended = PrependedCount(merged);

            _history.Clear();
            _history.AddRange(merged);

            if (prepended > 0) HistoryPrepended?.Invoke(prepended);
            else HistoryReplaced?.Invoke();
        }

        // How many turns were added to the front, if that is all that happened.
        // Zero when anything else changed, which the caller reads as "replaced".
        //
        // Compared by value rather than by reference: an assistant turn is
        // copied on every merge to carry its speaker, so the objects differ even
        // when the conversation has not.
        private int PrependedCount(List<ChatTurn> merged)
        {
            if (_history.Count == 0 || merged.Count <= _history.Count) return 0;

            var offset = merged.Count - _history.Count;

            for (var i = 0; i < _history.Count; i++)
            {
                if (!Same(_history[i], merged[i + offset])) return 0;
            }

            return offset;
        }

        private static bool Same(ChatTurn a, ChatTurn b) =>
            a.Role == b.Role
            && a.At == b.At
            && a.Speaker == b.Speaker
            && a.Text == b.Text;

        // The earliest moment every member's loaded history covers, or null if
        // no member is holding anything back.
        //
        // A member that has reached the beginning of its transcript constrains
        // nothing — it has everything there is, and its silence before some
        // point is real rather than unloaded. Only a member with more to fetch
        // draws the line, at the oldest message it has so far.
        private DateTimeOffset? TrustworthyFrom()
        {
            DateTimeOffset? start = null;

            foreach (var member in _members)
            {
                if (!member.Chat.HasMore) continue;

                var earliest = Earliest(member.Chat);
                if (earliest is null) continue;

                if (start is null || earliest > start) start = earliest;
            }

            return start;
        }

        private static DateTimeOffset? Earliest(OpenClawChatSession chat)
        {
            DateTimeOffset? earliest = null;

            foreach (var turn in chat.History)
                if (earliest is null || turn.At < earliest) earliest = turn.At;

            return earliest;
        }

        private bool _deepening;

        // Pages members back until they cover a common stretch of time.
        //
        // Without it the window is set by whichever member happens to have the
        // shallowest page, which in a busy room can be a few minutes — correct,
        // but not much of a conversation. Each round pushes back the member that
        // is currently the binding constraint, so the requests go where they buy
        // the most rather than being spread evenly.
        //
        // Bounded, because a room with a member that has thousands of messages
        // behind it would otherwise page the whole way there on open.
        public async Task DeepenAsync()
        {
            if (_deepening) return;
            _deepening = true;

            try
            {
                await PageBackHardAsync();
            }
            finally
            {
                _deepening = false;
            }
        }

        // Excluded from coverage: eight rounds of a chat.history request, and the
        // catch for a gateway that stops answering partway through — which is not
        // worth failing an open conversation over, so the window simply stays
        // where it is. With no gateway the first round returns and neither the
        // loop nor the catch has anything to do, which is what DeepenAsync's
        // tests exercise: that it runs once, releases its flag, and disturbs
        // nothing.
        [ExcludeFromCodeCoverage]
        private async Task PageBackHardAsync()
        {
            try
            {
                for (var round = 0; round < 8; round++)
                {
                    if (!await PageBindingMemberAsync(CancellationToken.None)) return;
                }
            }
            catch
            {
                // See above: a gateway that stops answering leaves the window
                // where it is rather than failing the conversation.
            }
        }

        // Fetches one page for whichever member is currently stopping the
        // window opening further — the one whose oldest loaded message is the
        // most recent. Sending every request to where it buys the most beats
        // spreading them evenly over members that are already deeper than the
        // view can show.
        private async Task<bool> PageBindingMemberAsync(CancellationToken ct)
        {
            var binding = _members
                .Where(m => m.Chat.HasMore && Earliest(m.Chat) is not null)
                .OrderByDescending(m => Earliest(m.Chat))
                .FirstOrDefault();

            if (binding is null) return false;

            return await OpenClawSessions.LoadOlderAsync(binding.Chat, ct);
        }

        // Compared on the words alone. A relayed copy can differ in surrounding
        // whitespace from the one the agent wrote, and matching has to survive
        // that or the duplicate comes back.
        private static string Normalise(string text) =>
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Shortest run of characters that is allowed to identify an echo on its
        // own. Below this a match means very little — "yes" is the opening of
        // plenty of sentences — and dropping a person's short reply because an
        // agent happened to start a paragraph the same way is a worse failure
        // than showing one duplicate.
        private const int EchoPrefix = 16;

        // Whether this is an agent's message arriving from the other side.
        //
        // Not equality, which is what the first attempt used and what left the
        // duplicates in place: the copy that reaches the other agents is
        // sometimes **cut short**, so Aurora's full sentence appeared once
        // attributed and once as a blue bubble ending at its first colon. So a
        // long enough prefix of what an agent said counts as that message.
        //
        // In one direction only, and that is CB-33. This used to match a prefix
        // either way round, on the grounds that nothing says the stored original
        // is the longer of the two — which is true, and was never observed,
        // where the truncating relay was. What the other direction did match,
        // reliably, was a person quoting an agent and adding their own words: a
        // quote-and-add always opens with what it quotes and is always longer
        // than it, so the whole message was read as that agent's echo and
        // dropped. Not a duplicate suppressed — the person's entire message,
        // gone, with nothing on screen to say there had been one.
        //
        // Gating that arm on the person's text being no longer than the agent's
        // was the shape the ticket suggested, and it collapses to equality,
        // which the first line already covers. So the arm goes rather than
        // growing a condition that can never be true on its own.
        //
        // The trade is deliberate and it is not symmetric. Keeping the arm costs
        // a whole message, silently, every time somebody quotes; dropping it
        // costs one visible duplicate bubble in a case nobody has yet seen,
        // where an agent's own transcript holds *less* than the relay carried.
        //
        // Deciding this by who sent it rather than by the words was the other
        // shape considered, and the metadata cannot carry it. A relayed agent
        // copy arrives with senderIsOwner false plus the bot's Discord display
        // name, and so does — by assumption, see OpenClawSender — another person
        // in the channel, so having a name separates neither from the other. Nor
        // does the name reach this room's members: it is a Discord display name
        // where a member is an agent id, and the fixture this suite has always
        // used says so, standing "Quillbot" in for the agent "Quill". A name
        // test would be a second heuristic over a namespace nobody here
        // controls, which is what the one being narrowed above already was.
        private static bool SaidByAnAgent(List<string> agentTexts, string text)
        {
            foreach (var said in agentTexts)
            {
                if (said == text) return true;

                if (text.Length >= EchoPrefix && said.StartsWith(text, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // How many bubbles one message text is worth, expressed as which
        // member's copies of it the room draws — CB-32.
        //
        // The room has to hold two facts apart that a set cannot: the same
        // message reaches every agent in the channel and appears in all of their
        // transcripts, and a person can say the same thing twice. Membership of
        // a set of texts answers the first and silently swallows the second, so
        // saying "ok" and then "ok" again drew one bubble carrying the earlier
        // timestamp — while an agent repeating *itself* was drawn twice, because
        // assistant turns never went through that set at all. The transcript
        // disagreed with what had actually been sent, in one direction only.
        //
        // A count per member, and the maximum across them, is what tells the two
        // apart. Two members each holding one copy is one message that reached
        // both; one member holding two copies is two messages, whoever else
        // received them. Keying on the text and the time instead would be the
        // obvious alternative and does not work — the same message is timestamped
        // per delivery, and two agents can record it either side of a minute
        // boundary, which is why this was keyed on text alone in the first place.
        //
        // Returning the winning member rather than a bare count is what keeps the
        // timestamps coherent. Drawing "the first two copies found" can take both
        // of them from one member's record of one message, where a member that
        // received both says when each was actually delivered. Ties go to the
        // earliest member, so a room whose members all agree draws the same
        // copies every rebuild rather than shuffling which transcript it quotes.
        //
        // Pure, and taking the texts rather than the members, for the reason
        // PickCarrier below is: it is a rule about how many of something to show,
        // and it should be decidable without a gateway, a transcript or a window.
        internal static Dictionary<string, int> WhoseCopiesToDraw(
            IReadOnlyList<IReadOnlyList<string>> perMember)
        {
            var best = new Dictionary<string, (int Member, int Count)>(StringComparer.Ordinal);

            for (var member = 0; member < perMember.Count; member++)
            {
                var held = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var text in perMember[member])
                {
                    held[text] = held.GetValueOrDefault(text) + 1;
                }

                foreach (var (text, count) in held)
                {
                    if (best.TryGetValue(text, out var winner) && winner.Count >= count) continue;

                    best[text] = (member, count);
                }
            }

            return best.ToDictionary(e => e.Key, e => e.Value.Member, StringComparer.Ordinal);
        }

        // Which member carries a room send: the one that spoke most recently,
        // among those that have somewhere to deliver.
        //
        // Pure, and taking three facts per member rather than the members
        // themselves, for the reason OrbArrangement and OpenClawSessionKind are
        // pure: this is a rule about precedence and it should be decidable
        // without a gateway, a transcript or a window.
        //
        // Having an address is the only hard requirement, and it is what the old
        // rule was missing. The gateway has no room to send to, so the message
        // goes out through one member's session with delivery on — and a member
        // with no delivery context cannot post to the channel at all, which is
        // the state CB-27 found every member of a quiet room in.
        //
        // Among those, the most recent speaker rather than the first by key.
        // First-by-key was chosen for stability, on the grounds that which
        // member carries it is not a routing decision. That is still true of the
        // *channel* post, which everyone sees either way — but the chat.send
        // half wakes exactly one agent, and waking whichever agent happens to
        // sort first is a worse answer than waking the one currently talking to
        // you. The old rule survives as the tiebreak, so a room where nobody has
        // spoken still picks the same member every time.
        //
        // Staleness is not a filter. A member the recency window dropped is
        // still standing in the channel, still has an address, and is still
        // exactly as able to post; filtering on it is what this ticket is about.
        //
        // Nobody is addressed and there is no "replying to" in the composer, on
        // purpose: the agent you meant hears the channel post through the relay
        // whether or not it is the carrier, so an addressee would be a control
        // that changed nothing anyone could see.
        //
        // Returns an index into the list it was given, or -1 for "nobody in this
        // room can post" — a refusal, which the caller says out loud rather than
        // quietly sending to one agent.
        internal static int PickCarrier(
            IReadOnlyList<(bool HasDelivery, DateTimeOffset? LastSpoke, string GatewayKey)> members)
        {
            var best = -1;

            for (var i = 0; i < members.Count; i++)
            {
                if (!members[i].HasDelivery) continue;
                if (best < 0) { best = i; continue; }

                var mine = members[i];
                var theirs = members[best];

                // A member that has never spoken loses to one that has, whenever
                // the times are comparable at all; with neither having spoken the
                // key decides, which is the old rule intact.
                var newer = Compare(mine.LastSpoke, theirs.LastSpoke);

                if (newer > 0
                    || (newer == 0
                        && string.CompareOrdinal(mine.GatewayKey, theirs.GatewayKey) < 0))
                {
                    best = i;
                }
            }

            return best;
        }

        // Null sorts below any real time rather than throwing, and two nulls are
        // a tie — which is what sends the decision to the key.
        private static int Compare(DateTimeOffset? a, DateTimeOffset? b) =>
            a is null && b is null ? 0
            : a is null ? -1
            : b is null ? 1
            : a.Value.CompareTo(b.Value);

        // The newest thing this member said itself. Assistant turns only: a user
        // turn in a member's transcript is somebody else's message arriving, so
        // counting those would make "who spoke last" mean "who was spoken to
        // last", which is the same answer for every member in the room.
        //
        // internal rather than private, and the reason is worth stating because
        // it was found the hard way. PickCarrier is pure and exhaustively
        // covered — but it is covered with timestamps handed straight to it,
        // and this is the function that *produces* those timestamps. With this
        // private, every test of the carrier rule reached it through SendAsync
        // with empty member histories, so the loop below never ran with more
        // than nothing in it and "newest" was verified nowhere. The half of the
        // fix that decides which agent receives the message rested on it.
        internal static DateTimeOffset? LastSpoke(OpenClawChatSession chat)
        {
            DateTimeOffset? latest = null;

            foreach (var turn in chat.History)
            {
                if (turn.Role != ChatRole.Assistant) continue;
                if (latest is null || turn.At > latest) latest = turn.At;
            }

            return latest;
        }

        // Posting to the channel, and then handing the message to one agent in
        // it. Both halves — see OpenClawSessions.SendToRoomAsync, which explains
        // why either alone is broken.
        //
        // Everything this can say, it says in this transcript. A note written
        // into a member's transcript would be invisible here, because the merge
        // drops System turns, and the failure being reported is a failure of the
        // room rather than of that agent.
        public async Task<ChatSendOutcome> SendAsync(string text)
        {
            // Yours, before anything is attempted, because the interface says
            // SendAsync raises TurnAdded for the user's own turn — so exactly one
            // thing owns the transcript and a send that fails leaves the message
            // on screen with the reason underneath it instead of a ghost.
            //
            // CB-35: this used to run after the two early refusals below,
            // which meant "replying is off" and "nobody is in this channel"
            // left no trace of the message at all — a note with nothing above
            // it, reading as the app having decided on its own not to send
            // anything, where the no-address refusal further down always
            // showed the message first. All three refusals are the same kind
            // of fact (nobody heard this) and now read the same way.
            AddLocal(new ChatTurn
            {
                Role = ChatRole.User, Text = text, IsComplete = true, Mine = true
            });

            if (!OrbweaverSettings.OpenClawReplyEnabled)
            {
                Note("Replying is off. Turn on \"Allow replying to agents\" in Settings.");
                return ChatSendOutcome.Failed;
            }

            if (_members.Count == 0)
            {
                Note("Nobody is in this channel right now.");
                return ChatSendOutcome.Failed;
            }

            var index = PickCarrier(_members
                .Select(m => (m.Chat.Delivery is not null, LastSpoke(m.Chat), m.Chat.GatewayKey))
                .ToList());

            if (index < 0)
            {
                Note(OpenClawSessions.NoAddressInRoom(DisplayName));
                return ChatSendOutcome.Failed;
            }

            var carrier = _members[index];

            var failure = await OpenClawSessions.SendToRoomAsync(
                carrier.Chat, DisplayName, carrier.Agent, text, CancellationToken.None);

            if (failure is not null)
            {
                Note(failure);
                return ChatSendOutcome.Failed;
            }

            return ChatSendOutcome.Sent;
        }

        private void Note(string text)
        {
            // CB-35 removed a dedupe check that used to live here: three
            // attempts with replying switched off left three identical
            // notes stacked with nothing between them, which read as three
            // separate problems and was one, so a repeat with no message
            // above it was suppressed.
            //
            // That shape can no longer occur. SendAsync now adds the user's
            // own turn before every refusal it can produce — see its own
            // comment on why, which is the transcript-consistency half of
            // CB-35 — so a note is never written without a fresh message
            // immediately above it, and "the last thing in this room's own
            // list is an identical System turn" was the entire condition
            // this check tested for. Removed rather than left dead: a check
            // that can never fire reads as protecting something, and nothing
            // here needs protecting any more.

            AddLocal(new ChatTurn { Role = ChatRole.System, IsComplete = true, Text = text });
        }

        // Into both lists, deliberately.
        //
        // _history so the panel shows it now, through the TurnAdded every other
        // implementation of this interface raises for the same reason; _local so
        // it is still there after the next rebuild throws _history away. Adding
        // it only to _local and rebuilding would work too and would flash the
        // whole transcript for one row.
        private void AddLocal(ChatTurn turn)
        {
            _local.Add(turn);
            if (_local.Count > KeepLocal) _local.RemoveRange(0, _local.Count - KeepLocal);

            _history.Add(turn);
            TurnAdded?.Invoke(turn);
        }

        public void Cancel()
        {
            // Nothing to stop here: a room is a view, and an agent's own run is
            // stopped from its own orb.
        }

        public void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }
    }
}

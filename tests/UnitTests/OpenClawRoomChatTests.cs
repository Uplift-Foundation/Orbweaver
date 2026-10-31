using System.Linq;
using Xunit;

namespace Orbweaver.Tests
{
    // Merging several agents' transcripts into one room.
    //
    // At 0% across 213 instrumented lines, and reachable with nothing but
    // in-memory sessions: Rebuild touches lists and raises events, and only the
    // coalescing wrapper around it needs a dispatcher.
    //
    // Worth the effort out of proportion to its size, because three separate
    // shipped bugs were fixed in here and all three had the same shape — the room
    // asserting who said something when it did not know:
    //
    //   * Every agent's message drawn twice, once attributed in its own colour
    //     and once as a blue bubble from you. Both arrive as user-role turns in
    //     the transcripts of the agents that *received* them, and nothing in the
    //     payload says which is which.
    //   * The relayed copy sometimes arriving cut short, so equality missed it
    //     and the duplicate came back — one full sentence attributed, one blue
    //     bubble ending at its first colon.
    //   * Historical messages wrong far more often than recent ones, because in
    //     the stretch only some members had paged back to, the missing members'
    //     messages existed solely as echoes with nothing to attribute them
    //     against.
    [Collection("Settings")]
    public class OpenClawRoomChatTests
    {
        private static OpenClawChatSession Member(string agent) =>
            new($"openclaw:agent:{agent}:discord:channel:1", $"agent:{agent}:discord:channel:1", agent);

        private static readonly DateTimeOffset T0 =
            new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        private static void Give(
            OpenClawChatSession session,
            params (ChatRole Role, string Text, int Minute)[] turns)
        {
            session.SetHistory(turns
                .Select(t => new HistoryTurn(t.Role, t.Text, null, "", T0.AddMinutes(t.Minute),
                              null, null))
                .ToList());
        }

        // The same, for the cases that turn on who the gateway said sent a turn.
        // Kept separate rather than widening Give: every case above is about the
        // rules that hold when nothing said, and adding two more slots to their
        // tuples would bury that.
        private static void GiveAttributed(
            OpenClawChatSession session,
            params (ChatRole Role, string Text, int Minute, bool Mine, string? Speaker)[] turns)
        {
            session.SetHistory(turns
                .Select(t => new HistoryTurn(t.Role, t.Text, null, "", T0.AddMinutes(t.Minute),
                              t.Speaker, null, t.Mine))
                .ToList());
        }

        private static OpenClawSessions.Delivery Address(string account) =>
            new("discord", "channel:900", account);

        // Every member has everything there is, so nothing constrains the
        // window — which keeps a test about attribution from also being a test
        // about the trust cutoff.
        private static OpenClawRoomChatSession Room(
            params (OpenClawChatSession Chat, string Agent, string Colour)[] members)
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            foreach (var (chat, _, _) in members) chat.HasMore = false;

            room.SetMembers(members.ToList());
            room.Rebuild();

            return room;
        }

        // --- attribution ---

        // A message found in Nova's transcript in the assistant role is Nova.
        // That is the whole reason to merge rather than show one transcript.
        [Fact]
        public void AnAgentsOwnWordsAreAttributedToIt()
        {
            var nova = Member("nova");
            Give(nova, (ChatRole.Assistant, "Nodes loaded", 1));

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Single(room.History);
            Assert.Equal("Nova", room.History[0].Speaker);
            Assert.Equal("#7f7", room.History[0].SpeakerColor);
        }

        // CB-272: the merged copy carries ImageSourcePath, and IsVideo is read
        // off it, so a clip in a room stays a clip. A future copy site that
        // forgot the path would turn it into a picture-shaped turn with no tile.
        [Fact]
        public void ARoomMergedVideoTurnIsStillAVideo()
        {
            var nova = Member("nova");
            nova.SetHistory(new List<HistoryTurn>
            {
                new(ChatRole.Assistant, "here you go", "/route?path=clip.mp4", "clip.mp4",
                    T0.AddMinutes(1), null, null, ImageSourcePath: "/a/b/clip.mp4"),
            });

            var room = Room((nova, "Nova", "#7f7"));

            var turn = Assert.Single(room.History);
            Assert.True(turn.IsVideo);
            Assert.Equal("/a/b/clip.mp4", turn.ImageSourcePath);
        }

        // The first bug. Nova's message is an assistant turn in her own
        // transcript and a *user* turn in Aurora's, because Aurora received
        // it — so a naive merge shows it twice, once as Nova and once as a blue
        // bubble from the user.
        [Fact]
        public void AnAgentsMessageEchoedIntoAnotherTranscriptIsNotDrawnTwice()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            Give(aurora, (ChatRole.User, "Nodes loaded and ready", 1));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Single(room.History);
            Assert.Equal("Nova", room.History[0].Speaker);
        }

        // The second bug. The relayed copy is sometimes cut short, so equality
        // misses it — a long enough prefix of what the agent said has to count
        // as that message.
        [Fact]
        public void ATruncatedEchoIsStillRecognisedAsTheSameMessage()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Nodes loaded and ready: 41 of them", 1));
            Give(aurora, (ChatRole.User, "Nodes loaded and ready", 1));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Single(room.History);
            Assert.Equal("Nova", room.History[0].Speaker);
        }

        // The fourth bug, CB-33, and the reason the rule above runs one way
        // round rather than both. It used to match a prefix in either direction,
        // on the grounds that nothing says the stored original is the longer of
        // the two — and what that reliably matched was a person quoting an agent
        // and adding their own words, which is ordinary chat behaviour. The
        // whole message went, not a duplicate: the room drew nothing at all for
        // the person.
        //
        // This is the ticket's confirmed failing input, verbatim.
        [Fact]
        public void QuotingAnAgentAndAddingYourOwnWordsIsKept()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Build is green on both legs.", 1));
            Give(aurora,
                (ChatRole.User, "Build is green on both legs. Are we sure about the arm64 one?", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
            Assert.Contains(room.History,
                t => t.Text == "Build is green on both legs. Are we sure about the arm64 one?");
        }

        // The floor on prefix matching, and the reason for it: below sixteen
        // characters a match means very little, and dropping a person's short
        // reply because an agent happened to open a paragraph the same way is a
        // worse failure than showing one duplicate.
        [Fact]
        public void AShortReplyIsNotSwallowedByAnAgentThatStartedTheSameWay()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Yes, and here is the long explanation", 1));
            Give(aurora, (ChatRole.User, "Yes", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
        }

        // Matching is on the words alone, because a relayed copy can differ in
        // surrounding whitespace from the one the agent wrote.
        [Fact]
        public void SurroundingWhitespaceDoesNotDefeatTheMatch()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            Give(aurora, (ChatRole.User, "  Nodes   loaded\n and ready  ", 1));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Single(room.History);
        }

        // The quote that carries a name. CB-27's Mine check already rescued the
        // operator's own quote; another person in the channel had no such
        // rescue, and this is the half of CB-33 that survived that fix.
        [Fact]
        public void ANamedPersonQuotingAnAgentIsKept()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Build is green on both legs.", 1));
            GiveAttributed(aurora,
                (ChatRole.User, "Build is green on both legs. Are we sure?", 2, false, "Thistle"));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
            Assert.Contains(room.History, t => t.Speaker == "Thistle");
        }

        // ...and the operator's own quote still is, which is CB-27's behaviour
        // and must not regress: it is rescued by the Mine test running ahead of
        // the echo test, not by anything CB-33 changed.
        [Fact]
        public void YourOwnQuoteOfAnAgentIsStillKept()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Build is green on both legs.", 1));
            GiveAttributed(aurora,
                (ChatRole.User, "Build is green on both legs. Are we sure?", 2, true, null));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
            Assert.Contains(room.History, t => t.Mine);
        }

        // The threshold, in the direction that still matches. Sixteen characters
        // of an agent's sentence, and nothing after them, is the truncated relay
        // the rule was written for.
        [Fact]
        public void ExactlySixteenCharactersOfAnAgentsSentenceIsATruncatedEcho()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Nodes are loaded and ready", 1));
            Give(aurora, (ChatRole.User, "Nodes are loaded", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Single(room.History);
        }

        // One character below it is not, which is the floor doing its job rather
        // than a coincidence of this fixture.
        [Fact]
        public void FifteenCharactersOfAnAgentsSentenceIsNotAnEcho()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Nodes are loaded and ready", 1));
            Give(aurora, (ChatRole.User, "Nodes are loade", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
        }

        // The same threshold in the direction that no longer matches. The ticket
        // measured this exactly: fourteen characters survived and sixteen was
        // swallowed, which says the constant was never the problem — what was
        // above it went with no trace.
        [Fact]
        public void SixteenCharactersOfAnAgentFollowedByYourOwnWordsIsKept()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Nodes are loaded", 1));
            Give(aurora, (ChatRole.User, "Nodes are loaded — how many of them?", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
        }

        // An agent's message relayed *whole* is still one bubble. Removing the
        // longer-than arm cost nothing here, because equality was always the
        // first test and is what the ordinary relay matches.
        [Fact]
        public void AWholeRelayedCopyIsStillOneBubble()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "Build is green on both legs.", 1));
            GiveAttributed(aurora,
                (ChatRole.User, "Build is green on both legs.", 2, false, "Novabot"));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Single(room.History);
            Assert.Equal("Nova", room.History[0].Speaker);
        }

        // --- attribution the gateway supplied ---

        // Your own message, drawn as yours. Everything above this line is about
        // the rules that hold when nothing said who — and they still hold, which
        // is why every one of those cases passes unchanged. This is what happens
        // when something does.
        [Fact]
        public void YourOwnMessageIsDrawnAsYours()
        {
            var quill = Member("quill");
            GiveAttributed(quill, (ChatRole.User, "what is the status", 1, true, null));

            var room = Room((quill, "Quill", "#7f7"));

            var turn = Assert.Single(room.History);
            Assert.True(turn.Mine);
            Assert.Equal(ChatRole.User, turn.Role);
            Assert.Null(turn.Speaker);
        }

        // The three copies of one message you sent, which is the shape a real
        // room send leaves behind: the carrier's transcript holds what you
        // typed, every other member's holds the mirror with its prefix already
        // taken off by the parser, and this window's own optimistic copy is the
        // same text again. All three normalise to one string, so one bubble.
        //
        // Before the prefix came off, the last two matched nothing and a
        // successful send drew twice — once plain, once prefixed.
        [Fact]
        public void TheCarrierAndMirrorCopiesOfYourMessageAreOneBubble()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            GiveAttributed(quill, (ChatRole.User, "anyone free to look at the build?", 1, true, null));
            GiveAttributed(aster, (ChatRole.User, "anyone free to look at the build?", 2, true, null));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            var turn = Assert.Single(room.History);
            Assert.True(turn.Mine);
        }

        // Your message is not an agent's, however closely it happens to match
        // one. The echo test runs after the Mine test on purpose: a message
        // swallowed for coincidentally opening the way an agent opened a
        // paragraph would be your message, gone, with the app having decided
        // somebody else said it.
        [Fact]
        public void YourMessageIsNotSwallowedByAnAgentThatSaidTheSameThing()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            Give(quill, (ChatRole.Assistant, "Rebuilding the index now", 1));
            GiveAttributed(aster, (ChatRole.User, "Rebuilding the index now", 2, true, null));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            Assert.Equal(2, room.History.Count);
            Assert.Contains(room.History, t => t.Mine);
        }

        // Somebody the gateway named: an agent relayed through the channel whose
        // own session is not in this room, or another person in it. Both are
        // "somebody who is not you", and both are better than the anonymous turn
        // this used to draw.
        [Fact]
        public void ANamedSenderIsAttributedRatherThanDrawnAnonymously()
        {
            var quill = Member("quill");
            GiveAttributed(quill, (ChatRole.User, "Nodes are loaded.", 1, false, "Thistle"));

            var room = Room((quill, "Quill", "#7f7"));

            var turn = Assert.Single(room.History);
            Assert.Equal("Thistle", turn.Speaker);
            Assert.False(turn.Mine);
            Assert.Equal(ChatRole.Assistant, turn.Role);
        }

        // ...with no colour. It is a Discord display name and the ring colours
        // are keyed by agent id, so borrowing one would say two different
        // speakers were the same agent. An initials chip is the honest answer.
        [Fact]
        public void ANamedSenderGetsNoBorrowedColour()
        {
            var quill = Member("quill");
            GiveAttributed(quill, (ChatRole.User, "Nodes are loaded.", 1, false, "Thistle"));

            var room = Room((quill, "Quill", "#7f7"));

            Assert.Null(Assert.Single(room.History).SpeakerColor);
        }

        // A named echo of an agent that *is* in this room is still an echo. The
        // first pass has already attributed that message in the agent's own
        // colour, from its own transcript, which is a better answer than the
        // display name on the copy — so the copy is dropped exactly as it was
        // before names existed.
        [Fact]
        public void ANamedEchoOfAnAgentInThisRoomIsStillDropped()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            Give(quill, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            GiveAttributed(aster, (ChatRole.User, "Nodes loaded and ready", 1, false, "Quillbot"));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            var turn = Assert.Single(room.History);
            Assert.Equal("Quill", turn.Speaker);
            Assert.Equal("#7f7", turn.SpeakerColor);
        }

        // One named person's message reaching every agent in the room is still
        // one message. The dedupe set is shared with everything else here, which
        // is what keeps that true without a second rule.
        [Fact]
        public void ANamedSendersMessageIsTakenOnceHoweverManyAgentsReceivedIt()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            GiveAttributed(quill, (ChatRole.User, "Morning all.", 1, false, "Thistle"));
            GiveAttributed(aster, (ChatRole.User, "Morning all.", 2, false, "Thistle"));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            Assert.Single(room.History);
        }

        // The same message reaches every agent in the room, so it is taken once —
        // keyed on the text rather than the time, because the same message is
        // timestamped per delivery and two agents can record it either side of a
        // minute boundary.
        [Fact]
        public void APersonsMessageIsTakenOnceHoweverManyAgentsReceivedIt()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.User, "what is the status", 1));
            Give(aurora, (ChatRole.User, "what is the status", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Single(room.History);
        }

        // The third rule, and the most carefully argued: a message that is not
        // any agent's own is drawn as the room's own voice — left, neutral, no
        // name — rather than in the user's blue. Three things arrive looking
        // identical, and only one of them is the user, so "somebody said this"
        // is true where "you said this" is not.
        [Fact]
        public void AMessageNobodyCanBeCreditedWithIsNotDrawnAsYours()
        {
            var nova = Member("nova");
            Give(nova, (ChatRole.User, "who is handling the deploy", 1));

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Single(room.History);
            Assert.Equal(ChatRole.Assistant, room.History[0].Role);
            Assert.Null(room.History[0].Speaker);
        }

        // Blank turns contribute nothing rather than an unattributed empty
        // bubble.
        [Fact]
        public void BlankTurnsAreDropped()
        {
            var nova = Member("nova");
            Give(nova, (ChatRole.User, "   ", 1), (ChatRole.User, "real", 2));

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Single(room.History);
            Assert.Equal("real", room.History[0].Text);
        }

        // --- saying the same thing twice ---

        // CB-32, in all three flavours of user turn. Saying "ok" and then "ok"
        // again is two messages, and the room drew one of them — the earlier,
        // carrying the earlier timestamp — because a set of texts cannot tell
        // "the same message reaching a second agent" from "the person said it
        // twice".
        [Fact]
        public void APersonSayingTheSameThingTwiceIsDrawnTwice()
        {
            var nova = Member("nova");
            Give(nova, (ChatRole.User, "ok", 1), (ChatRole.User, "ok", 2));

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Equal(2, room.History.Count);
            Assert.Equal(new[] { T0.AddMinutes(1), T0.AddMinutes(2) }, room.History.Select(t => t.At));
        }

        [Fact]
        public void ANamedPersonSayingTheSameThingTwiceIsDrawnTwice()
        {
            var nova = Member("nova");
            GiveAttributed(nova,
                (ChatRole.User, "ok", 1, false, "Thistle"),
                (ChatRole.User, "ok", 2, false, "Thistle"));

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Equal(2, room.History.Count);
            Assert.All(room.History, t => Assert.Equal("Thistle", t.Speaker));
        }

        [Fact]
        public void YourOwnMessageSaidTwiceIsDrawnTwice()
        {
            var nova = Member("nova");
            GiveAttributed(nova,
                (ChatRole.User, "ok", 1, true, null),
                (ChatRole.User, "ok", 2, true, null));

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Equal(2, room.History.Count);
            Assert.All(room.History, t => Assert.True(t.Mine));
        }

        // ...while one message reaching two agents is still one bubble, which is
        // the fact the set was there for and the whole reason this is a count
        // per member rather than a count.
        [Fact]
        public void OneMessageInTwoTranscriptsIsStillOneBubbleWhenItIsSaidTwice()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.User, "ok", 1), (ChatRole.User, "ok", 3));
            Give(aurora, (ChatRole.User, "ok", 2), (ChatRole.User, "ok", 4));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            // Two messages, four deliveries. Not four bubbles, and not one.
            Assert.Equal(2, room.History.Count);
        }

        // The copies come from one member's transcript rather than being taken
        // as they are found, so the two bubbles carry the two times that member
        // actually recorded. Taking the first two found would quote the first
        // message twice and never reach the second.
        [Fact]
        public void TheMemberThatHeardItMostOftenIsTheOneQuoted()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            // Nova dropped out after the first one; Aurora heard both.
            Give(nova, (ChatRole.User, "ok", 1));
            Give(aurora, (ChatRole.User, "ok", 2), (ChatRole.User, "ok", 9));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(
                new[] { T0.AddMinutes(2), T0.AddMinutes(9) },
                room.History.Select(t => t.At));
        }

        // The deliveries need not line up in time across members — the same
        // message is timestamped per delivery, and this is the shape that used
        // to argue for keying on text alone. It still is: the count is per
        // member, and nothing here compares one member's times with another's.
        [Fact]
        public void CopiesArrivingOutOfOrderAcrossMembersAreStillTwoMessages()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.User, "ok", 1), (ChatRole.User, "ok", 8));
            Give(aurora, (ChatRole.User, "ok", 3), (ChatRole.User, "ok", 5));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
        }

        // The asymmetry CB-32 also names: an agent repeating itself was always
        // drawn twice, because assistant turns never went through the dedupe at
        // all. That stays true, and it is now what the human side does too — if
        // a person's repeat must be preserved, an agent's must be as well.
        [Fact]
        public void AnAgentRepeatingItselfIsDrawnTwiceAndItsEchoesStillAreNot()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova,
                (ChatRole.Assistant, "Nodes loaded and ready", 1),
                (ChatRole.Assistant, "Nodes loaded and ready", 2));

            // Aurora received both, as user turns, the way every member does.
            Give(aurora,
                (ChatRole.User, "Nodes loaded and ready", 1),
                (ChatRole.User, "Nodes loaded and ready", 2));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(2, room.History.Count);
            Assert.All(room.History, t => Assert.Equal("Nova", t.Speaker));
        }

        // --- how many copies to draw ---

        // The rule itself, without a transcript behind it. Two members each
        // holding one copy is one message that reached both.
        [Fact]
        public void OneCopyEachIsOneMessage()
        {
            var whose = OpenClawRoomChatSession.WhoseCopiesToDraw(
                new IReadOnlyList<string>[] { new[] { "ok" }, new[] { "ok" } });

            Assert.Equal(0, whose["ok"]);
        }

        // ...and the member holding two of them is the one the room quotes,
        // however many members hold one.
        [Fact]
        public void TheMemberHoldingTheMostCopiesWins()
        {
            var whose = OpenClawRoomChatSession.WhoseCopiesToDraw(
                new IReadOnlyList<string>[]
                {
                    new[] { "ok" },
                    new[] { "ok", "ok" },
                    new[] { "ok" }
                });

            Assert.Equal(1, whose["ok"]);
        }

        // A tie goes to the earliest member, so a room whose members all agree
        // quotes the same transcript on every rebuild rather than shuffling
        // which one it reads from.
        [Fact]
        public void ATieGoesToTheEarliestMember()
        {
            var whose = OpenClawRoomChatSession.WhoseCopiesToDraw(
                new IReadOnlyList<string>[]
                {
                    new[] { "ok", "ok" },
                    new[] { "ok", "ok" }
                });

            Assert.Equal(0, whose["ok"]);
        }

        // Each text is decided on its own: the member that heard the most of one
        // message is not necessarily the member that heard the most of another.
        [Fact]
        public void EachTextPicksItsOwnMember()
        {
            var whose = OpenClawRoomChatSession.WhoseCopiesToDraw(
                new IReadOnlyList<string>[]
                {
                    new[] { "ok", "ok", "fine" },
                    new[] { "ok", "fine", "fine" }
                });

            Assert.Equal(0, whose["ok"]);
            Assert.Equal(1, whose["fine"]);
        }

        // A room with nobody in it asks nothing of this, and gets an empty
        // answer rather than a throw.
        [Fact]
        public void NoMembersIsNoCopies()
        {
            Assert.Empty(OpenClawRoomChatSession.WhoseCopiesToDraw(
                Array.Empty<IReadOnlyList<string>>()));
        }

        // A member with nothing to say contributes no entry, rather than an
        // entry saying zero.
        [Fact]
        public void AMemberWithNoUserTurnsContributesNothing()
        {
            var whose = OpenClawRoomChatSession.WhoseCopiesToDraw(
                new IReadOnlyList<string>[] { Array.Empty<string>(), new[] { "ok" } });

            Assert.Equal(1, whose["ok"]);
            Assert.Single(whose);
        }

        // --- order ---

        // Merged by time, not by member, because that is the conversation. A
        // merge has no append: a message from an agent whose backlog arrives late
        // belongs in the middle.
        [Fact]
        public void TheRoomIsInTimeOrderAcrossMembers()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "first", 1), (ChatRole.Assistant, "third", 3));
            Give(aurora, (ChatRole.Assistant, "second", 2), (ChatRole.Assistant, "fourth", 4));

            var room = Room((nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f"));

            Assert.Equal(
                new[] { "first", "second", "third", "fourth" },
                room.History.Select(t => t.Text));
        }

        // --- the trust window ---

        // The third bug. A member still holding history back draws a line at the
        // oldest message it has: before that point the room does not know who was
        // talking, and showing less is the honest answer.
        [Fact]
        public void HistoryOlderThanEveryMemberHasLoadedIsNotShown()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "old", 1), (ChatRole.Assistant, "recent", 50));
            Give(aurora, (ChatRole.Assistant, "mine", 40));

            // Aurora has more to fetch, so nothing before her oldest message
            // can be trusted.
            nova.HasMore = false;
            aurora.HasMore = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f") }.ToList());
            room.Rebuild();

            Assert.DoesNotContain(room.History, t => t.Text == "old");
            Assert.Contains(room.History, t => t.Text == "mine");
            Assert.Contains(room.History, t => t.Text == "recent");
        }

        // A member that has reached the beginning of its transcript constrains
        // nothing: it has everything there is, so its silence before some point
        // is real rather than unloaded.
        [Fact]
        public void AMemberWithNothingLeftToFetchConstrainsNothing()
        {
            var nova = Member("nova");
            var aurora = Member("aurora");

            Give(nova, (ChatRole.Assistant, "old", 1));
            Give(aurora, (ChatRole.Assistant, "newer", 40));

            nova.HasMore = false;
            aurora.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (nova, "Nova", "#7f7"), (aurora, "Aurora", "#77f") }.ToList());
            room.Rebuild();

            Assert.Contains(room.History, t => t.Text == "old");
        }

        // --- what the panel is told ---

        // A replaced transcript scrolls the panel to the bottom, which is right
        // when a room opens and exactly wrong when you have just scrolled to the
        // top and asked for more. So older turns on the front are reported as a
        // prepend instead.
        [Fact]
        public void OlderTurnsArrivingOnTheFrontAreReportedAsAPrepend()
        {
            var nova = Member("nova");
            Give(nova, (ChatRole.Assistant, "recent", 50));
            nova.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (nova, "Nova", "#7f7") }.ToList());
            room.Rebuild();

            var prepended = 0;
            var replaced = 0;
            room.HistoryPrepended += n => prepended = n;
            room.HistoryReplaced += () => replaced++;

            // A page of older history arrives.
            Give(nova,
                (ChatRole.Assistant, "older", 10),
                (ChatRole.Assistant, "middling", 20),
                (ChatRole.Assistant, "recent", 50));
            nova.HasMore = false;
            room.Rebuild();

            Assert.Equal(2, prepended);
            Assert.Equal(0, replaced);
        }

        // Anything else is a replacement, which is what a room opening looks
        // like.
        [Fact]
        public void ADifferentConversationIsReportedAsAReplacement()
        {
            var nova = Member("nova");
            Give(nova, (ChatRole.Assistant, "one", 1));
            nova.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (nova, "Nova", "#7f7") }.ToList());
            room.Rebuild();

            var prepended = 0;
            var replaced = 0;
            room.HistoryPrepended += n => prepended = n;
            room.HistoryReplaced += () => replaced++;

            Give(nova, (ChatRole.Assistant, "something else entirely", 2));
            nova.HasMore = false;
            room.Rebuild();

            Assert.Equal(0, prepended);
            Assert.Equal(1, replaced);
        }

        // The merge is deliberately uncapped, and this is why: a cap here trimmed
        // the *front*, which is the end paging adds to — so scrolling up fetched
        // older messages and then dropped them again, and the window could never
        // open past the cap however far you scrolled.
        [Fact]
        public void TheMergeIsNotCappedAtTheFront()
        {
            var nova = Member("nova");
            Give(nova, Enumerable.Range(0, 700)
                .Select(i => (ChatRole.Assistant, $"turn {i}", i))
                .ToArray());
            nova.HasMore = false;

            var room = Room((nova, "Nova", "#7f7"));

            Assert.Equal(700, room.History.Count);
            Assert.Equal("turn 0", room.History[0].Text);
        }

        [Fact]
        public void ARoomWithNoMembersIsEmptyRatherThanAThrow()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            room.SetMembers(Array.Empty<(OpenClawChatSession, string, string)>());
            room.Rebuild();

            Assert.Empty(room.History);
        }

        // "Message the channel…", not the plain "Message…" a single agent's
        // composer shows. Worth pinning rather than loosening to a Contains:
        // sending here posts to the channel itself rather than to one agent, and
        // the hint is the only thing on screen that says so.
        [Fact]
        public void TheComposerHintSaysWhetherReplyingIsOn()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            OrbweaverSettings.OpenClawReplyEnabled = true;
            Assert.Equal("Message the channel…", room.ComposerHint);

            OrbweaverSettings.OpenClawReplyEnabled = false;
            Assert.Equal("Replying is off", room.ComposerHint);
        }

        // --- who is in the room ---

        // SetMembers runs on every scan, because membership genuinely changes: an
        // agent that has not spoken lately drops out of the session list, and one
        // that joins has to start being listened to.

        // The same set of people, with better names and colours. agents.list
        // lands after the first connection, so the first SetMembers often has
        // placeholders — and refreshing those must not tear the subscriptions
        // down, or a streaming reply in flight stops redrawing.
        [Fact]
        public void TheSameMembersWithBetterNamesAreRefreshedInPlace()
        {
            var nova = Member("nova");
            nova.HasMore = false;
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            room.SetMembers([(nova, "agent-1", "#111111")]);
            room.SetMembers([(nova, "Nova", "#ff0000")]);

            // Still subscribed: a turn arriving still reaches the room. Rebuild
            // is called directly because the coalescing wrapper needs a
            // dispatcher this suite does not run.
            Give(nova, (ChatRole.Assistant, "still here", 1));
            room.Rebuild();

            var turn = Assert.Single(room.History);
            Assert.Equal("still here", turn.Text);
            Assert.Equal("Nova", turn.Speaker);
            Assert.Equal("#ff0000", turn.SpeakerColor);
        }

        // A member leaving is a different set, so the room is rebuilt from
        // scratch and their messages go with them.
        [Fact]
        public void AMemberThatLeavesStopsBeingShown()
        {
            var nova = Member("nova");
            var kai = Member("kai");
            nova.HasMore = false;
            kai.HasMore = false;
            Give(nova, (ChatRole.Assistant, "from nova", 1));
            Give(kai, (ChatRole.Assistant, "from kai", 2));

            var room = Room((nova, "Nova", "#ff0000"), (kai, "Kai", "#00ff00"));
            Assert.Equal(2, room.History.Count);

            room.SetMembers([(nova, "Nova", "#ff0000")]);
            room.Rebuild();

            var turn = Assert.Single(room.History);
            Assert.Equal("from nova", turn.Text);
        }

        // And a member that leaves is unsubscribed, or its transcript keeps
        // driving rebuilds of a room it is no longer part of.
        [Fact]
        public void AMemberThatLeavesStopsBeingListenedTo()
        {
            var nova = Member("nova");
            var kai = Member("kai");
            nova.HasMore = false;
            kai.HasMore = false;

            var room = Room((nova, "Nova", "#ff0000"), (kai, "Kai", "#00ff00"));
            room.SetMembers([(nova, "Nova", "#ff0000")]);

            // Kai speaks after leaving. Nothing it says belongs in the room.
            Give(kai, (ChatRole.Assistant, "shouting from outside", 5));
            room.Rebuild();

            Assert.DoesNotContain(room.History, t => t.Text == "shouting from outside");
        }

        // A member that joins and whose loaded history reaches back at least as
        // far as everyone else's contributes its messages, and both are shown.
        [Fact]
        public void AMemberThatJoinsBringsItsBacklogWithIt()
        {
            var nova = Member("nova");
            var kai = Member("kai");
            nova.HasMore = false;
            kai.HasMore = false;
            Give(nova, (ChatRole.Assistant, "from nova", 1));
            Give(kai, (ChatRole.Assistant, "kai was here first", 0),
                      (ChatRole.Assistant, "from kai", 2));

            var room = Room((nova, "Nova", "#ff0000"));
            Assert.Single(room.History);

            room.SetMembers([(nova, "Nova", "#ff0000"), (kai, "Kai", "#00ff00")]);
            room.Rebuild();

            Assert.Contains(room.History, t => t.Text == "from nova");
            Assert.Contains(room.History, t => t.Text == "from kai");
        }

        // The other way round, and the case that caught me writing the test
        // above: a member joining with a SHORTER loaded history narrows the
        // window rather than widening it, so a message that was on screen a
        // moment ago is no longer trustworthy and goes away.
        //
        // That is correct — the room must not claim to know who said what in a
        // stretch the new member has not loaded, which is the third of the three
        // shipped bugs named at the top of this file — but it is surprising
        // enough to be worth an assertion of its own rather than being left as
        // an emergent property of two other tests.
        [Fact]
        public void AMemberJoiningWithLessHistoryNarrowsTheWindow()
        {
            var nova = Member("nova");
            var kai = Member("kai");
            nova.HasMore = false;
            kai.HasMore = false;
            Give(nova, (ChatRole.Assistant, "from nova", 1));
            Give(kai, (ChatRole.Assistant, "from kai", 2));

            var room = Room((nova, "Nova", "#ff0000"));
            Assert.Single(room.History);

            room.SetMembers([(nova, "Nova", "#ff0000"), (kai, "Kai", "#00ff00")]);
            room.Rebuild();

            // Nova's earlier message is now below the line every member reaches.
            var turn = Assert.Single(room.History);
            Assert.Equal("from kai", turn.Text);
        }

        // An empty room is a real state — every agent in a channel can go quiet
        // at once — and it must not throw on the way there.
        [Fact]
        public void ARoomEmptiedOfEveryoneIsEmptyRatherThanBroken()
        {
            var nova = Member("nova");
            nova.HasMore = false;
            Give(nova, (ChatRole.Assistant, "from nova", 1));

            var room = Room((nova, "Nova", "#ff0000"));

            room.SetMembers([]);
            room.Rebuild();

            Assert.Empty(room.History);
            Assert.False(room.HasMore);
        }

        // --- HasMore ---

        // Any one member with more to fetch means the room has more, because
        // paging that member is what moves the trustworthy line back.
        [Fact]
        public void TheRoomHasMoreIfAnySingleMemberDoes()
        {
            var nova = Member("nova");
            var kai = Member("kai");
            nova.HasMore = false;
            kai.HasMore = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(nova, "Nova", "#ff0000"), (kai, "Kai", "#00ff00")]);

            Assert.True(room.HasMore);
        }

        [Fact]
        public void TheRoomHasNoMoreWhenEveryMemberIsExhausted()
        {
            var nova = Member("nova");
            var kai = Member("kai");
            nova.HasMore = false;
            kai.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(nova, "Nova", "#ff0000"), (kai, "Kai", "#00ff00")]);

            Assert.False(room.HasMore);
        }

        [Fact]
        public void ARoomWithNoMembersHasNoMore()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            Assert.False(room.HasMore);
        }

        // --- paging ---

        // Nothing to page and nothing to talk to: LoadOlderAsync must report that
        // the window did not move rather than throwing, because the panel reads
        // the answer to decide whether it has hit the top.
        [Fact]
        public async Task PagingARoomWithNothingToFetchReportsNoMovement()
        {
            var nova = Member("nova");
            nova.HasMore = false;
            Give(nova, (ChatRole.Assistant, "from nova", 1));
            var room = Room((nova, "Nova", "#ff0000"));

            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        [Fact]
        public async Task PagingAnEmptyRoomReportsNoMovement()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        // --- sending to the room ---

        // Sending here is not addressed to a member. It goes out through one
        // member's transcript but with delivery on, which posts it to the channel
        // itself, so every agent there receives it the way they receive anything
        // else said in the room. Which member carries it is therefore not a
        // routing decision at all — only a question of whose transcript the send
        // sits in.
        // CB-35: the user's own message goes on first, then the note — the
        // same shape the no-address refusal below always had. This test used
        // to assert the note was the *only* thing added; that disagreed with
        // the no-address case about where the user's own words go for what is
        // otherwise the same kind of failure, which CB-35 asks to fix by
        // aligning this path (and the no-members one below) to the
        // no-address shape rather than the other way round.
        [Fact]
        public async Task WithReplyingOffTheMessageIsRefusedInTheRoom()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = false;

            var nova = Member("nova");
            nova.HasMore = false;
            var room = Room((nova, "Nova", "#ff0000"));
            var before = room.History.Count;

            var outcome = await room.SendAsync("anyone about?");

            var added = room.History.Skip(before).ToList();
            Assert.Collection(added,
                mine =>
                {
                    Assert.Equal(ChatRole.User, mine.Role);
                    Assert.Equal("anyone about?", mine.Text);
                },
                note =>
                {
                    Assert.Equal(ChatRole.System, note.Role);
                    Assert.Contains("Replying is off", note.Text);
                });

            // CB-35: the return value is what lets ChatPanel.Send() retain the
            // typed text in the composer — the note above is only ever a
            // record of the attempt, not a signal a caller can act on.
            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }

        // A channel every agent has gone quiet in has nobody to send through.
        // Said plainly rather than failing silently: from the user's side an
        // empty channel and a broken one look identical otherwise.
        [Fact]
        public async Task ARoomWithNobodyInItSaysSoRatherThanFailingQuietly()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            var outcome = await room.SendAsync("anyone about?");

            // CB-35: same reordering as the replying-off case above — the
            // message goes on before the note explaining why nobody heard it.
            Assert.Collection(room.History,
                mine =>
                {
                    Assert.Equal(ChatRole.User, mine.Role);
                    Assert.Equal("anyone about?", mine.Text);
                },
                note =>
                {
                    Assert.Equal(ChatRole.System, note.Role);
                    Assert.Contains("Nobody is in this channel", note.Text);
                });

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }

        // --- picking the carrier ---

        // Which member carries a room send, as a rule with no gateway, no
        // transcript and no window behind it. It used to be "first by gateway
        // key", chosen for stability on the grounds that the choice is not a
        // routing decision. Half of that survives — the channel post reaches
        // everyone whoever carries it — and half of it did not: the chat.send
        // half wakes exactly one agent, and the member that has been talking to
        // you is a better one to wake than whichever sorts first.

        // Having somewhere to deliver is the only hard requirement, and it is
        // the one the old rule did not have. A member with no address cannot
        // post to the channel at all, so the first-by-key member being the one
        // without an address is precisely how a room send ended up private.
        [Fact]
        public void ThePickSkipsAMemberWithNowhereToDeliver()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (false, (DateTimeOffset?)T0.AddMinutes(9), "agent:aster:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // ...including when it is the one that spoke most recently. Recency
        // orders the members that can post; it does not qualify one that can't.
        [Fact]
        public void TheLatestSpeakerIsSkippedIfItCannotPost()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:aster:discord:channel:900"),
                (false, (DateTimeOffset?)T0.AddMinutes(30), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(0, pick);
        }

        // Among members that can post, the one that spoke last.
        [Fact]
        public void TheMostRecentSpeakerCarriesIt()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:aster:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(30), "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(12), "agent:thorn:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // A member that has never spoken loses to one that has, whatever the
        // order it was listed in — member order changes between scans and must
        // not change the answer.
        [Fact]
        public void AMemberThatHasNeverSpokenLosesToOneThatHas()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)null, "agent:aster:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // With nobody having spoken, the old rule intact: first by gateway key,
        // so a freshly opened room picks the same member every time rather than
        // whichever the list happened to arrive in.
        [Fact]
        public void WithNobodyHavingSpokenTheKeyDecides()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)null, "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)null, "agent:aster:discord:channel:900"),
            });

            // "agent:aster:…" sorts before "agent:quill:…" whichever order they
            // were listed in.
            Assert.Equal(1, pick);
        }

        // ...and the same when two members last spoke at the same moment, which
        // is what the key is a tiebreak for.
        [Fact]
        public void TwoMembersThatSpokeAtOnceAreSeparatedByTheKey()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(5), "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(5), "agent:aster:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // Nobody can post. A refusal rather than a fallback: sending through a
        // member with no address is the silent private delivery this ticket is
        // about, and -1 is what makes the caller say so out loud.
        [Fact]
        public void ARoomWhereNobodyCanPostRefuses()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (false, (DateTimeOffset?)T0.AddMinutes(1), "agent:aster:discord:channel:900"),
                (false, (DateTimeOffset?)T0.AddMinutes(2), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(-1, pick);
        }

        // A member that has never spoken, listed *after* one that has. The same
        // answer as the case above and reached down the other side of the
        // comparison, which is the whole reason both are here: member order
        // changes between scans, and a rule that only holds in one order is a
        // rule that holds half the time.
        [Fact]
        public void AMemberThatHasNeverSpokenLosesFromEitherPosition()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)null, "agent:aster:discord:channel:900"),
            });

            Assert.Equal(0, pick);
        }

        [Fact]
        public void AnEmptyRoomRefusesToo()
        {
            Assert.Equal(-1, OpenClawRoomChatSession.PickCarrier(
                Array.Empty<(bool, DateTimeOffset?, string)>()));
        }

        // --- who spoke last, from a real transcript ---

        // The function that *produces* the timestamps PickCarrier orders by.
        //
        // Covered separately because covering PickCarrier is not covering this:
        // every carrier case above hands timestamps straight in, and every
        // SendAsync case builds members with no history at all — so the loop in
        // here only ever ran over an empty list and "newest" was asserted
        // nowhere. This is the half of the fix that decides which agent actually
        // receives the message.

        // Several turns, and the newest wins — not the first walked past, and
        // not the last. Deliberately out of order in the transcript, because a
        // loop that returned either end would pass with them sorted.
        [Fact]
        public void TheNewestThingAMemberSaidIsWhatCounts()
        {
            var quill = Member("quill");
            Give(quill,
                (ChatRole.Assistant, "starting", 3),
                (ChatRole.Assistant, "still going", 40),
                (ChatRole.Assistant, "nearly there", 12));

            Assert.Equal(T0.AddMinutes(40), OpenClawRoomChatSession.LastSpoke(quill));
        }

        // ...and with the newest first in the list, so neither end of the walk
        // is the answer by accident.
        [Fact]
        public void TheOrderTheTurnsAreStoredInDoesNotDecideIt()
        {
            var quill = Member("quill");
            Give(quill,
                (ChatRole.Assistant, "nearly there", 40),
                (ChatRole.Assistant, "starting", 3),
                (ChatRole.Assistant, "still going", 12));

            Assert.Equal(T0.AddMinutes(40), OpenClawRoomChatSession.LastSpoke(quill));
        }

        // A user turn in a member's transcript is somebody else's message
        // arriving, not that member speaking. Counting those would make "who
        // spoke last" mean "who was spoken to last", which is the same answer
        // for every member in the room and so no answer at all.
        [Fact]
        public void BeingSpokenToIsNotSpeaking()
        {
            var quill = Member("quill");
            Give(quill,
                (ChatRole.Assistant, "on it", 5),
                (ChatRole.User, "any update?", 50));

            Assert.Equal(T0.AddMinutes(5), OpenClawRoomChatSession.LastSpoke(quill));
        }

        // A member that has only ever been spoken to has not spoken, which is
        // null rather than "a long time ago" — the distinction PickCarrier's
        // Compare exists for.
        [Fact]
        public void AMemberThatHasOnlyListenedHasNotSpoken()
        {
            var quill = Member("quill");
            Give(quill, (ChatRole.User, "anyone about?", 1));

            Assert.Null(OpenClawRoomChatSession.LastSpoke(quill));
        }

        [Fact]
        public void AMemberWithNoTranscriptAtAllHasNotSpoken()
        {
            Assert.Null(OpenClawRoomChatSession.LastSpoke(Member("quill")));
        }

        // --- what a failure says ---

        // Three sentences, because there are three different truths and the
        // difference is what the person needs. One "couldn't send" covering all
        // three would have somebody re-type a message that is already in the
        // channel.
        [Fact]
        public void NoAddressSaysNothingWasSentAndWhy()
        {
            Assert.Equal(
                "Couldn't post to #general: no member of this channel carries a delivery address.",
                OpenClawSessions.NoAddressInRoom("#general"));
        }

        [Fact]
        public void AFailedPostSaysNothingWasSent()
        {
            Assert.Equal(
                "Couldn't post to #general: the gateway said no. Nothing was sent.",
                OpenClawSessions.PostFailed("#general", "the gateway said no"));
        }

        // The one that must not say "nothing was sent", because something was:
        // the channel has the message and only the handoff to an agent failed.
        [Fact]
        public void AFailedHandoffSaysTheChannelAlreadyHasIt()
        {
            var note = OpenClawSessions.HandoffFailed("#general", "Quill", "timed out");

            Assert.Equal("Posted to #general, but couldn't hand it to Quill: timed out.", note);
            Assert.DoesNotContain("Nothing was sent", note);
        }

        // --- sending, end to end, with no gateway behind it ---

        // Your own message goes into the *room's* transcript, not into whichever
        // member's session carried it. It used to go into the carrier's, which
        // read as the right thing while the room was a view over its members —
        // but a note about a failure written there is invisible in the merge,
        // and the message and its explanation belong together.
        [Fact]
        public async Task WhatYouTypeAppearsInTheRoomRatherThanInAMembersTranscript()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            Assert.Contains(room.History, t => t.Role == ChatRole.User && t.Text == "anyone about?");
            Assert.Empty(quill.History);
        }

        // ...and it is yours, so the panel draws it in your colour and on your
        // side. ChatRole.User with no Speaker is exactly what does that.
        [Fact]
        public async Task WhatYouTypeIsMarkedAsYours()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            var mine = Assert.Single(room.History, t => t.Text == "anyone about?");
            Assert.True(mine.Mine);
            Assert.Equal(ChatRole.User, mine.Role);
            Assert.Null(mine.Speaker);
        }

        // The refusal this ticket exists for. Every member is in the channel and
        // none of them has an address — which is what a room whose members have
        // all been quiet longer than the recency window looked like — so nothing
        // is sent, and it says so instead of delivering privately to one agent.
        [Fact]
        public async Task ARoomWithNoAddressAnywhereRefusesAndSendsNothing()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            var aster = Member("aster");
            quill.HasMore = false;
            aster.HasMore = false;

            var room = Room((quill, "Quill", "#ff0000"), (aster, "Aster", "#00ff00"));

            var outcome = await room.SendAsync("anyone about?");

            Assert.Contains(room.History, t =>
                t.Role == ChatRole.System
                && t.Text == OpenClawSessions.NoAddressInRoom("#general"));

            // Nothing reached either member: no transcript, no send.
            Assert.Empty(quill.History);
            Assert.Empty(aster.History);

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }

        // A member that *can* post, with no gateway to post through. A different
        // sentence from the one above, and both of them end up in the room.
        [Fact]
        public async Task WithNoGatewayTheFailureIsSaidInTheRoom()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            var outcome = await room.SendAsync("anyone about?");

            var note = Assert.Single(room.History, t => t.Role == ChatRole.System);
            Assert.StartsWith("Couldn't post to #general:", note.Text);
            Assert.Contains("Nothing was sent", note.Text);

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }

        // A note has to survive the next rebuild, and until now none did.
        // Rebuild throws the whole transcript away and re-merges from the
        // members, so the "Replying is off" note this class has always written
        // lasted only until any member event landed — which is any moment at all
        // in a busy channel.
        [Fact]
        public async Task ANoteSurvivesTheNextRebuild()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = false;

            var quill = Member("quill");
            quill.HasMore = false;
            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");
            Assert.Contains(room.History, t => t.Text.Contains("Replying is off"));

            room.Rebuild();

            Assert.Contains(room.History, t => t.Text.Contains("Replying is off"));
        }

        // ...and stops surviving the moment the gateway's own copy of it turns
        // up, which is what keeps a sent message from being drawn twice — once
        // optimistically and once for real — for as long as the panel is open.
        [Fact]
        public async Task YourMessageGivesWayToTheGatewaysOwnCopyOfIt()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            // The gateway records it and the member's transcript reloads with it
            // in, which is what happens a second or two after any real send.
            GiveAttributed(quill, (ChatRole.User, "anyone about?", 3, true, null));
            room.Rebuild();

            var mine = Assert.Single(room.History, t => t.Text == "anyone about?");
            Assert.True(mine.Mine);

            // ...and it is the gateway's copy that survived, timestamped by the
            // gateway rather than by this window.
            Assert.Equal(T0.AddMinutes(3), mine.At);
        }

        // One copy back retires one optimistic copy, not both of them — the
        // other half of CB-32, in the layer that holds what you have just typed.
        //
        // Send the same words twice and one of the gateway's copies comes back
        // first, which is the ordinary case: they are written as two messages
        // and acknowledged one at a time. The old predicate asked only whether
        // *any* merged turn matched, so the single returning copy retired both
        // locals and the second message vanished off the screen until its own
        // copy arrived — a message the person can see they sent, gone, which is
        // the same failure the merge had.
        [Fact]
        public async Task OneCopyComingBackRetiresOneOptimisticCopy()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("ok");
            await room.SendAsync("ok");

            Assert.Equal(2, room.History.Count(t => t.Text == "ok"));

            // The gateway records the first one and the member's transcript
            // reloads with it in. The second has not come back yet.
            GiveAttributed(quill, (ChatRole.User, "ok", 3, true, null));
            room.Rebuild();

            Assert.Equal(2, room.History.Count(t => t.Text == "ok"));
            Assert.Contains(room.History, t => t.Text == "ok" && t.At == T0.AddMinutes(3));
        }

        // ...and when the second copy lands too, both optimistic copies are
        // retired and the room is the gateway's record of the conversation
        // rather than this window's. Without the pairing this would be the case
        // that regressed: a budget that never ran out would leave one local copy
        // behind forever and draw three bubbles for two messages.
        [Fact]
        public async Task BothCopiesComingBackRetireBothOptimisticCopies()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("ok");
            await room.SendAsync("ok");

            GiveAttributed(quill,
                (ChatRole.User, "ok", 3, true, null),
                (ChatRole.User, "ok", 4, true, null));
            room.Rebuild();

            Assert.Equal(
                new[] { T0.AddMinutes(3), T0.AddMinutes(4) },
                room.History.Where(t => t.Text == "ok").Select(t => t.At));
        }

        // The dedupe predicate walks past turns that are not yours without
        // matching them. Obvious, and uncovered until now because every case
        // that reached it had nothing else in the room to walk past — so the
        // arm that says "this merged turn is somebody else's, keep looking" had
        // never been taken.
        [Fact]
        public async Task SomebodyElsesIdenticalWordsDoNotRetireYourOwnMessage()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            // An agent that happens to have said the same words, attributed to
            // it in pass one — which is not your message coming back.
            Give(quill, (ChatRole.Assistant, "anyone about?", 1));

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");
            room.Rebuild();

            // Both survive: Quill's, attributed, and yours.
            Assert.Contains(room.History, t => t.Speaker == "Quill" && !t.Mine);
            Assert.Contains(room.History, t => t.Mine);
        }

        // CB-35 removed the dedupe this test used to name
        // ("TheSameNoteTwiceRunningIsSaidOnce"): SendAsync now adds the
        // user's own message before every refusal it produces (see its own
        // comment), so a repeated identical note is never adjacent to its
        // twin any more — there is always a fresh message between them, the
        // same as there always was for the no-address refusal. Three
        // attempts with replying off now read as three attempts, each with
        // its own message and its own explanation, which is the behaviour
        // CB-35 asks this path to match the others in.
        [Fact]
        public async Task EachRefusedAttemptKeepsItsOwnMessageAndNote()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = false;

            var quill = Member("quill");
            quill.HasMore = false;
            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("first try");
            await room.SendAsync("second try");
            await room.SendAsync("third try");

            Assert.Equal(3, room.History.Count(t => t.Role == ChatRole.System));
            Assert.Equal(3, room.History.Count(t => t.Role == ChatRole.User));
            Assert.Equal(
                new[] { "first try", "second try", "third try" },
                room.History.Where(t => t.Role == ChatRole.User).Select(t => t.Text));
        }

        // ...but a note with your own message between it and the last identical
        // one is a note about *that* message, and belongs there. The failure
        // happened again, to something new, and suppressing the second one would
        // leave a message on screen with no explanation under it.
        [Fact]
        public async Task ANoteAboutAFreshMessageIsNotSwallowed()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            // No gateway, so each of these fails the same way — but each has its
            // own message above it.
            await room.SendAsync("first try");
            await room.SendAsync("second try");

            Assert.Equal(2, room.History.Count(t => t.Role == ChatRole.System));
            Assert.Contains(room.History, t => t.Mine && t.Text == "first try");
            Assert.Contains(room.History, t => t.Mine && t.Text == "second try");
        }

        // A different sentence always lands, however recently anything else was
        // said. Only an exact repeat is suppressed.
        [Fact]
        public async Task ADifferentNoteAlwaysLands()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = false;

            var quill = Member("quill");
            quill.HasMore = false;
            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("first try");

            OrbweaverSettings.OpenClawReplyEnabled = true;
            await room.SendAsync("second try");

            var notes = room.History.Where(t => t.Role == ChatRole.System).ToList();
            Assert.Equal(2, notes.Count);
            Assert.Contains(notes, n => n.Text.Contains("Replying is off"));
            Assert.Contains(notes, n => n.Text.Contains("no member of this channel"));
        }

        // The room's own list is bounded, and this asserts what falls off rather
        // than only that something does.
        //
        // It replaces a case that had quietly stopped testing anything. That one
        // sent forty messages with replying switched off, which used to leave
        // forty notes and overflow the cap — and once notes coalesce, forty
        // identical ones are a single note. It kept one entry and passed
        // `1 <= 32` while proving nothing at all. Measured, not guessed: the old
        // assertion was replaced with a failing probe and it reported "kept 1".
        //
        // Two of this branch's own commits between them closed every route to
        // the eviction: the note coalescing killed this one, and the `_local`
        // prune killed the other, since a sent message whose gateway copy has
        // arrived no longer occupies a slot. Neither left the line unreached in
        // the way a read-through would notice — they left it reachable only in a
        // state no test now produces, which is the same failure mode that hid
        // LastSpoke's comparison and is worth recognising twice.
        //
        // **The environment is the opposite of the leak case's, deliberately.**
        // ArrivedMessagesDoNotEvictTheNoteExplainingAnEarlierFailure needs sends
        // that *succeed*, so the gateway's copies come back and prune. This one
        // needs sends that *fail*, so nothing is ever pruned and the entries
        // accumulate. Moving either into the other's file makes it silently
        // stop testing its own subject — which has now happened once in each
        // direction on this branch.
        [Fact]
        public async Task TheOldestEntryIsWhatFallsOffWhenTheListOverflows()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            // No gateway, so every send fails: each leaves your message *and* a
            // note, two entries a time and nothing to prune them. Distinct texts,
            // because identical adjacent notes coalesce — and the notes here are
            // never adjacent, each having its own message above it.
            for (var i = 0; i < 20; i++) await room.SendAsync("sent " + i);

            room.Rebuild();

            // Forty entries into a list that holds thirty-two.
            Assert.Equal(32, room.History.Count);

            // The oldest went, the newest stayed. Asserting both, because a cap
            // that trimmed the wrong end would also leave thirty-two.
            Assert.DoesNotContain(room.History, t => t.Text == "sent 0");
            Assert.Contains(room.History, t => t.Text == "sent 19");
        }

        // ...and so does the message you typed, until the gateway's own copy of
        // it turns up.
        [Fact]
        public async Task YourMessageSurvivesTheNextRebuild()
        {
            OrbweaverSettings.ReloadForTests();
            OrbweaverSettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");
            room.Rebuild();

            Assert.Single(room.History, t => t.Text == "anyone about?");
        }

        // --- state and identity ---

        // Raised directly rather than posted, unlike the per-agent session's: a
        // room's state changes only when something already on the UI thread
        // rebuilt it, so there is no thread to hop off.
        [Fact]
        public void SettingTheStateRaisesStateChanged()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            RemoteChatState? seen = null;
            room.StateChanged += s => seen = s;

            room.SetState(RemoteChatState.Error);

            Assert.Equal(RemoteChatState.Error, seen);
            Assert.Equal(RemoteChatState.Error, room.State);
        }

        // The same state twice raises nothing. Every scan calls this, so a room
        // sitting connected would otherwise raise several times a second for the
        // panel to react to.
        [Fact]
        public void SettingTheSameStateAgainRaisesNothing()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetState(RemoteChatState.Error);

            var raised = 0;
            room.StateChanged += _ => raised++;
            room.SetState(RemoteChatState.Error);

            Assert.Equal(0, raised);
        }

        // Deliberately nothing: a room is a view, and an agent's own run is
        // stopped from its own orb. Asserted so that "does nothing" stays a
        // decision on record rather than an empty method somebody fills in.
        [Fact]
        public void CancelDoesNothing()
        {
            var nova = Member("nova");
            nova.HasMore = false;
            Give(nova, (ChatRole.Assistant, "hello", 1));
            var room = Room((nova, "Nova", "#ff0000"));
            var before = room.History.Count;

            room.Cancel();

            Assert.Equal(before, room.History.Count);
        }

        [Fact]
        public void TheRoomKeepsTheSessionIdItWasGiven()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:7", "#general");

            Assert.Equal("openclaw:room:discord:7", room.SessionId);
        }

        // --- paging the member that constrains the view ---

        // The room can only show the stretch every member has loaded, so paging
        // means paging whichever member starts LATEST — that is the one holding
        // the line up. Picking any other fetches history nobody can see yet.
        //
        // With no gateway the fetch itself fails, which is what makes this safe to
        // run: what is exercised is choosing the member and asking, not the answer.
        [Fact]
        public async Task PagingAsksTheMemberThatIsHoldingTheLineUp()
        {
            var early = Member("early");
            var late = Member("late");
            early.HasMore = true;
            late.HasMore = true;
            Give(early, (ChatRole.Assistant, "from ages ago", 1));
            Give(late, (ChatRole.Assistant, "from just now", 100));

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(early, "Early", "#ff0000"), (late, "Late", "#00ff00")]);
            room.Rebuild();

            // Nothing moves, because there is no gateway to fetch from — the point
            // is that it chose a member and asked without throwing, and reported
            // the window as unmoved.
            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        // A member with more to fetch but nothing loaded yet cannot be the
        // constraint — there is no "earliest" to compare — so it is skipped rather
        // than chosen and endlessly re-asked.
        [Fact]
        public async Task AMemberWithNothingLoadedIsNotChosenAsTheConstraint()
        {
            var empty = Member("empty");
            empty.HasMore = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(empty, "Empty", "#ff0000")]);
            room.Rebuild();

            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        // Deepening runs the same paging in the background when a room is first
        // opened, because the members' first pages rarely cover the same stretch
        // and the room can only show where they overlap. With nothing to fetch it
        // stops on the first round rather than spinning through all eight.
        [Fact]
        public async Task DeepeningARoomWithNothingToFetchStopsImmediately()
        {
            var nova = Member("nova");
            nova.HasMore = false;
            Give(nova, (ChatRole.Assistant, "hello", 1));

            var room = Room((nova, "Nova", "#ff0000"));

            await room.DeepenAsync();
        }
    }
}

using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests
{
    // A gateway conversation as the chat panel sees it: events in, turns out.
    //
    // This was at 0% across 144 instrumented lines and needed nothing to reach —
    // no dispatcher, no gateway, no fake. The constructor takes three strings and
    // every event handler is in-memory. Only SendAsync leaves the process, and it
    // is one line delegating to the class that owns the connection.
    //
    // The rules here are the ones that decide what a user watching an agent work
    // actually sees, and two of them are load-bearing in ways the code does not
    // advertise: the snapshot semantics, and the cap on history.
    [Collection("Settings")]
    public class OpenClawChatSessionTests
    {
        private static OpenClawChatSession Session() =>
            new("openclaw:agent:main:main", "agent:main:main", "main");

        private static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement;

        private static JsonElement AgentText(string text, string? stream = null)
        {
            var streamPart = stream is null ? "" : $"\"stream\":{JsonSerializer.Serialize(stream)},";
            return Event($"{{{streamPart}\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}");
        }

        // --- OnAgentText: the snapshot, not the delta ---

        [Fact]
        public void AnAgentEventStartsATurn()
        {
            var session = Session();
            var added = new List<ChatTurn>();
            session.TurnAdded += added.Add;

            session.OnAgentEvent("agent", AgentText("Working on it"));

            Assert.Single(added);
            Assert.Equal("Working on it", added[0].Text);
            Assert.Equal(ChatRole.Assistant, added[0].Role);
        }

        // data.text is a full snapshot of the turn so far rather than an
        // increment, and using it means a dropped or coalesced event costs
        // nothing. The test that matters is therefore that the second event
        // *replaces* rather than appends — treating it as a delta would produce
        // "OneOne two".
        [Fact]
        public void LaterEventsReplaceTheTurnRatherThanAppendingToIt()
        {
            var session = Session();
            var updates = new List<ChatTurn>();
            session.TurnUpdated += updates.Add;

            session.OnAgentEvent("agent", AgentText("One"));
            session.OnAgentEvent("agent", AgentText("One two"));
            session.OnAgentEvent("agent", AgentText("One two three"));

            Assert.Single(session.History);
            Assert.Equal("One two three", session.History[0].Text);
            Assert.Equal(2, updates.Count);
        }

        // A coalesced event — the gateway skipping straight from "One" to the
        // finished paragraph — loses nothing, which is the property the panel is
        // written against.
        [Fact]
        public void AnEventSkippedEntirelyCostsNothing()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("One"));
            session.OnAgentEvent("agent", AgentText("One two three four five"));

            Assert.Equal("One two three four five", session.History[0].Text);
        }

        // Thinking is shown, because watching an agent think is most of the value
        // of an orb that pulses — but as its own turn rather than mixed into the
        // reply it will eventually give.
        [Fact]
        public void ThinkingIsItsOwnTurnAndItsOwnRole()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("Considering the clamp", "thinking"));
            session.OnAgentEvent("agent", AgentText("The clamp runs first", "assistant"));

            Assert.Equal(2, session.History.Count);
            Assert.Equal(ChatRole.System, session.History[0].Role);
            Assert.Equal(ChatRole.Assistant, session.History[1].Role);
        }

        // ...and back again, so a second thinking pass after a reply is a third
        // turn rather than reopening the first.
        [Fact]
        public void SwitchingBackToThinkingStartsAnotherTurn()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("thought", "thinking"));
            session.OnAgentEvent("agent", AgentText("said", "assistant"));
            session.OnAgentEvent("agent", AgentText("thought again", "thinking"));

            Assert.Equal(3, session.History.Count);
        }

        // With no stream named, it is the assistant talking — the common case.
        [Fact]
        public void AnUnlabelledStreamIsTheAssistant()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("plain"));

            Assert.Equal(ChatRole.Assistant, session.History[0].Role);
        }

        // Nothing to show is no turn, rather than an empty bubble.
        [Theory]
        [InlineData("""{"data":{"text":""}}""")]
        [InlineData("""{"data":{}}""")]
        [InlineData("""{"data":7}""")]
        [InlineData("{}")]
        public void AnEventWithNoTextAddsNothing(string json)
        {
            var session = Session();

            session.OnAgentEvent("agent", Event(json));

            Assert.Empty(session.History);
        }

        // --- Complete: the turn is finished ---

        [Fact]
        public void AFinishedCronRunCompletesTheTurn()
        {
            var session = Session();
            session.OnAgentEvent("agent", AgentText("done thinking"));

            session.OnAgentEvent("cron", Event("""{"action":"finished"}"""));

            Assert.True(session.History[0].IsComplete);
        }

        [Fact]
        public void AnUpsertedTaskCompletesTheTurn()
        {
            var session = Session();
            session.OnAgentEvent("agent", AgentText("working"));

            session.OnAgentEvent("task", Event("""{"action":"upserted"}"""));

            Assert.True(session.History[0].IsComplete);
        }

        // Some other action on the same event name is not a completion, or a
        // cron job merely being scheduled would close a turn mid-sentence.
        [Theory]
        [InlineData("cron", """{"action":"scheduled"}""")]
        [InlineData("task", """{"action":"deleted"}""")]
        [InlineData("cron", "{}")]
        public void AnotherActionDoesNotCompleteTheTurn(string name, string json)
        {
            var session = Session();
            session.OnAgentEvent("agent", AgentText("still going"));

            session.OnAgentEvent(name, Event(json));

            Assert.False(session.History[0].IsComplete);
        }

        // Once complete, the next agent event starts a new turn rather than
        // reopening the finished one.
        [Fact]
        public void ACompletedTurnIsNotReopened()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("first"));
            session.OnAgentEvent("cron", Event("""{"action":"finished"}"""));
            session.OnAgentEvent("agent", AgentText("second"));

            Assert.Equal(2, session.History.Count);
            Assert.Equal("first", session.History[0].Text);
            Assert.Equal("second", session.History[1].Text);
        }

        [Fact]
        public void CompletingWithNothingInFlightIsHarmless()
        {
            var session = Session();

            session.OnAgentEvent("cron", Event("""{"action":"finished"}"""));

            Assert.Empty(session.History);
        }

        // --- OnTool: what the agent reached for ---

        // One line per tool call, in the transcript rather than in a status area,
        // because what an agent reached for is part of what it said.
        [Fact]
        public void AToolCallBecomesItsOwnLine()
        {
            var session = Session();

            session.OnAgentEvent("session.tool",
                Event("""{"data":{"phase":"start","name":"Read"}}"""));

            Assert.Single(session.History);
            Assert.Equal("· Read", session.History[0].Text);
            Assert.Equal(ChatRole.System, session.History[0].Role);
            Assert.True(session.History[0].IsComplete);
        }

        // Only the start. A tool that reported start and finish would otherwise
        // appear twice for one call.
        [Theory]
        [InlineData("""{"data":{"phase":"end","name":"Read"}}""")]
        [InlineData("""{"data":{"name":"Read"}}""")]
        [InlineData("""{"data":{"phase":"start"}}""")]
        [InlineData("""{"data":{"phase":"start","name":""}}""")]
        [InlineData("{}")]
        public void OnlyAToolStartWithANameIsShown(string json)
        {
            var session = Session();

            session.OnAgentEvent("session.tool", Event(json));

            Assert.Empty(session.History);
        }

        // A tool line ends the bubble it follows (CB-270 Stage 3): what the agent
        // said before the tool is one step and what it says after is the next,
        // which is what history draws for the same run. The run itself carries on,
        // so the text after the tool opens a bubble of its own and the stream's
        // blank-line joiner never shows.
        [Fact]
        public void AToolLineSplitsTheReplyIntoTheStepsOnEitherSideOfIt()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("Looking"));
            session.OnAgentEvent("session.tool", Event("""{"data":{"phase":"start","name":"Grep"}}"""));
            session.OnAgentEvent("agent", AgentText("Looking\n\nFound it"));

            Assert.Equal(new[] { "Looking", "· Grep", "Found it" }, session.History.Select(t => t.Text));
            Assert.True(session.History[0].IsComplete);
        }

        // The tool line does not end the *run*: a later delta still lands in the
        // bubble that opened after it.
        [Fact]
        public void AToolLineDoesNotEndTheRun()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("Looking"));
            session.OnAgentEvent("session.tool", Event("""{"data":{"phase":"start","name":"Grep"}}"""));
            session.OnAgentEvent("agent", Event("""{"data":{"delta":"\n\nFound"}}"""));
            session.OnAgentEvent("agent", Event("""{"data":{"delta":" it"}}"""));

            Assert.Equal(new[] { "Looking", "· Grep", "Found it" }, session.History.Select(t => t.Text));
            Assert.False(session.History[2].IsComplete);
        }

        // An event name this app does not know is ignored rather than mishandled.
        [Fact]
        public void AnUnknownEventNameIsIgnored()
        {
            var session = Session();

            session.OnAgentEvent("something.new", AgentText("text"));

            Assert.Empty(session.History);
        }

        // --- CB-95: a delivery lands while the panel is already open ---
        //
        // OnAgentEvent's "chat"/"session.message" cases only trigger a
        // chat.history refetch — a live gateway round trip, excluded from
        // coverage the same way TryResolveLiveImage's identical fetch already
        // is. AppendNewTail is the half of the fix that decides what to do
        // with the page once it comes back, and it needs no gateway at all to
        // exercise.

        [Fact]
        public void ADeliveredMessageIsAppendedPastTheKnownTail()
        {
            var session = Session();
            var added = new List<ChatTurn>();
            session.SetHistory(Turns("one"));
            session.TurnAdded += added.Add;

            session.AppendNewTail(Turns("one", "two"));

            Assert.Equal(new[] { "one", "two" }, session.History.Select(t => t.Text));
            Assert.Equal(new[] { "two" }, added.Select(t => t.Text));
        }

        // Several deliveries landing before the panel gets around to asking
        // are all appended, in order, rather than only the last one.
        [Fact]
        public void SeveralNewDeliveriesAreAllAppendedInOrder()
        {
            var session = Session();
            session.SetHistory(Turns("one"));

            session.AppendNewTail(Turns("one", "two", "three"));

            Assert.Equal(new[] { "one", "two", "three" }, session.History.Select(t => t.Text));
        }

        // The common case: the doorbell fires for a reason other than a
        // delivery (findings.md's own census recorded three "chat" events for
        // one delivered message), and the refetched page is identical to what
        // the transcript already has. Nothing is appended and nothing fires —
        // the property that makes this safe to call on every such event
        // rather than only on ones known to matter.
        [Fact]
        public void ARefetchWithNothingNewAppendsNothing()
        {
            var session = Session();
            var added = new List<ChatTurn>();
            session.SetHistory(Turns("one", "two"));
            session.TurnAdded += added.Add;

            session.AppendNewTail(Turns("one", "two"));

            Assert.Equal(new[] { "one", "two" }, session.History.Select(t => t.Text));
            Assert.Empty(added);
        }

        // An empty transcript — a panel bound before its first history read
        // completes — takes the whole page rather than needing an anchor to
        // extend past.
        [Fact]
        public void AnEmptyTranscriptTakesTheWholePage()
        {
            var session = Session();

            session.AppendNewTail(Turns("a", "b"));

            Assert.Equal(new[] { "a", "b" }, session.History.Select(t => t.Text));
        }

        // The anchor — this transcript's own last turn — is not found on the
        // refetched page at all: more turns arrived than one page holds, or a
        // page boundary moved it off. Appending nothing is the safe answer
        // rather than guessing where the new content starts, which would risk
        // a duplicated or reordered transcript.
        [Fact]
        public void AnUnmatchableAnchorAppendsNothingRatherThanGuessing()
        {
            var session = Session();
            var added = new List<ChatTurn>();
            session.SetHistory(Turns("something not on the page"));
            session.TurnAdded += added.Add;

            session.AppendNewTail(Turns("x", "y"));

            Assert.Equal(new[] { "something not on the page" }, session.History.Select(t => t.Text));
            Assert.Empty(added);
        }

        [Fact]
        public void AnEmptyPageAppendsNothing()
        {
            var session = Session();
            session.SetHistory(Turns("one"));

            session.AppendNewTail(Turns());

            Assert.Equal(new[] { "one" }, session.History.Select(t => t.Text));
        }

        // The role has to match too, not just the text: a user's own turn and
        // an assistant's turn that happen to say the same thing are not the
        // same turn, and matching on text alone could anchor on the wrong one.
        [Fact]
        public void TheAnchorMatchOnBothRoleAndText()
        {
            var session = Session();
            session.SetHistory(new List<HistoryTurn>
            {
                new(ChatRole.User, "same words", null, "", DateTimeOffset.UnixEpoch, null, null)
            });

            // The page's last turn says the identical text but as the
            // assistant, so it is not a match for the user's turn already on
            // screen — appending nothing is correct even though the text is
            // byte-identical.
            session.AppendNewTail(new List<HistoryTurn>
            {
                new(ChatRole.Assistant, "same words", null, "", DateTimeOffset.UnixEpoch, null, null)
            });

            Assert.Equal(ChatRole.User, session.History[0].Role);
            Assert.Single(session.History);
        }

        // A delivery goes out over the wire and comes into effect through
        // OnAgentEvent, not just through AppendNewTail called directly — this
        // pins that the "chat" and "session.message" event names actually
        // reach the switch's new arm rather than falling through to nothing,
        // the same regression AnUnknownEventNameIsIgnored above guards
        // against for names that should be ignored. No gateway is configured
        // in this suite (TestBootstrap points settings at a fresh temp
        // directory), so the chat.history refetch itself resolves to no
        // gateway and returns without changing anything — the same "no
        // gateway" seam ALiveMediaLineFailureStillExplainsWhy already relies
        // on for the picture-refetch twin of this path. What this pins is
        // that the event is accepted rather than ignored, not what a real
        // gateway would hand back.
        [Theory]
        [InlineData("chat")]
        [InlineData("session.message")]
        public async Task ADeliveryDoorbellIsAcceptedAndDoesNotThrow(string eventName)
        {
            var session = Session();
            session.SetHistory(Turns("one"));

            session.OnAgentEvent(eventName, Event("""{"state":"delta","deltaText":"hi"}"""));

            await Task.Delay(50);

            // No gateway to answer means no append happened — the event
            // reached RefreshTailAsync and RefreshTailAsync's own null guard
            // did its job, rather than the process crashing or the switch
            // arm silently not existing.
            Assert.Equal(new[] { "one" }, session.History.Select(t => t.Text));
        }

        // --- the history cap ---

        // Generous on purpose: at 60 a busy conversation dropped its own
        // beginning while you were reading it, which is what "stuff disappears"
        // looked like. The cap is asserted along with *which end* it trims, since
        // trimming the wrong one would delete the newest messages.
        [Fact]
        public void TheHistoryIsCappedAndTrimsTheOldestFirst()
        {
            var session = Session();

            for (var i = 0; i < 520; i++)
            {
                session.OnAgentEvent("session.tool",
                    Event("{\"data\":{\"phase\":\"start\",\"name\":\"tool" + i + "\"}}"));
            }

            Assert.Equal(500, session.History.Count);
            Assert.Equal("· tool20", session.History[0].Text);
            Assert.Equal("· tool519", session.History[^1].Text);
        }

        // --- history replacement and paging ---

        private static IReadOnlyList<HistoryTurn> Turns(params string[] texts) =>
            texts.Select(t => new HistoryTurn(
                ChatRole.Assistant, t, null, "", DateTimeOffset.UnixEpoch,
                "main", "#7f7")).ToList();

        // CB-91: a backlog turn whose picture came inline has to reach the
        // panel as bytes. Both loading paths map HistoryTurn onto ChatTurn
        // field by field, so a field left out of either one is a picture that
        // silently never appears.
        [Fact]
        public void SetHistoryCarriesInlinePictureBytesThrough()
        {
            var session = Session();
            var bytes = new byte[] { 1, 2, 3, 4 };

            session.SetHistory(new[]
            {
                new HistoryTurn(ChatRole.Assistant, "", null, "", DateTimeOffset.UnixEpoch,
                    null, null, false, bytes)
            });

            Assert.Equal(bytes, Assert.Single(session.History).ImageBytes);
        }

        [Fact]
        public void PrependHistoryCarriesInlinePictureBytesThrough()
        {
            var session = Session();
            var bytes = new byte[] { 5, 6, 7 };

            session.SetHistory(Turns("newer"));
            session.PrependHistory(new[]
            {
                new HistoryTurn(ChatRole.Assistant, "", null, "", DateTimeOffset.UnixEpoch,
                    null, null, false, bytes)
            });

            Assert.Equal(bytes, session.History[0].ImageBytes);
        }

        [Fact]
        public void SettingHistoryReplacesEverythingAndSaysSo()
        {
            var session = Session();
            var replaced = 0;
            session.HistoryReplaced += () => replaced++;

            session.OnAgentEvent("agent", AgentText("live"));
            session.SetHistory(Turns("one", "two"));

            Assert.Equal(new[] { "one", "two" }, session.History.Select(t => t.Text));
            Assert.Equal(1, replaced);
        }

        // The turn in flight is dropped with the rest, so a snapshot arriving
        // afterwards starts a fresh bubble rather than updating one that is no
        // longer in the list.
        [Fact]
        public void ReplacingHistoryClearsTheTurnInFlight()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("live"));
            session.SetHistory(Turns("loaded"));
            session.OnAgentEvent("agent", AgentText("after"));

            Assert.Equal(new[] { "loaded", "after" }, session.History.Select(t => t.Text));
        }

        [Fact]
        public void AnEmptyHistoryIsNotWorthReplacingWith()
        {
            var session = Session();
            var replaced = 0;
            session.HistoryReplaced += () => replaced++;

            session.SetHistory(Turns());

            Assert.Equal(0, replaced);
        }

        // Older turns go on the front and report how many, because the panel has
        // to put the scroll position back afterwards — content appearing above
        // where you are reading would throw you down the page.
        [Fact]
        public void OlderTurnsArePrependedAndCounted()
        {
            var session = Session();
            var prepended = 0;
            session.HistoryPrepended += n => prepended = n;

            session.SetHistory(Turns("newer"));
            session.PrependHistory(Turns("older", "middling"));

            Assert.Equal(new[] { "older", "middling", "newer" }, session.History.Select(t => t.Text));
            Assert.Equal(2, prepended);
        }

        [Fact]
        public void PrependingNothingRaisesNothing()
        {
            var session = Session();
            var raised = false;
            session.HistoryPrepended += _ => raised = true;

            session.PrependHistory(Turns());

            Assert.False(raised);
        }

        // Loaded turns are complete: they already happened, so nothing should
        // draw them as still being typed.
        [Fact]
        public void LoadedTurnsAreAlreadyComplete()
        {
            var session = Session();

            session.SetHistory(Turns("one"));
            session.PrependHistory(Turns("older"));

            Assert.All(session.History, turn => Assert.True(turn.IsComplete));
        }

        // The speaker survives the round trip, which is what draws the right
        // initials and colour on a room's bubbles.
        [Fact]
        public void TheSpeakerAndColourSurvive()
        {
            var session = Session();

            session.SetHistory(Turns("one"));

            Assert.Equal("main", session.History[0].Speaker);
            Assert.Equal("#7f7", session.History[0].SpeakerColor);
        }

        // --- state ---

        [Fact]
        public void AStateChangeIsAnnouncedOnce()
        {
            var session = Session();
            var seen = new List<RemoteChatState>();
            session.StateChanged += seen.Add;

            // Starts Connected, so the first change has to be to something else
            // or the "no event when unchanged" rule would hide a real bug.
            session.SetState(RemoteChatState.Error);
            session.SetState(RemoteChatState.Error);
            session.SetState(RemoteChatState.Connecting);

            Assert.Equal(new[] { RemoteChatState.Error, RemoteChatState.Connecting }, seen);
        }

        [Fact]
        public void TheDisplayNameCanImproveAfterTheSessionExists()
        {
            var session = Session();

            // agents.list arrives moments after the connection, so a panel
            // opened in that window would otherwise keep the raw id forever.
            session.DisplayName = "Aurora";

            Assert.Equal("Aurora", session.DisplayName);
        }

        // The composer hint is how the panel says replying is switched off,
        // rather than a send that silently does nothing.
        [Fact]
        public void TheComposerHintSaysWhetherReplyingIsOn()
        {
            var session = Session();

            OrbweaverSettings.OpenClawReplyEnabled = true;
            Assert.Equal("Message…", session.ComposerHint);

            OrbweaverSettings.OpenClawReplyEnabled = false;
            Assert.Equal("Replying is off", session.ComposerHint);
        }

        [Fact]
        public void TheGatewayKeyKeepsItsOwnNamespaceSeparateFromTheSessionId()
        {
            var session = Session();

            Assert.Equal("openclaw:agent:main:main", session.SessionId);
            Assert.Equal("agent:main:main", session.GatewayKey);
        }

        // ---- CB-116: TryResolveLocalMedia's own confidence gate --------------
        //
        // The live path (OnAgentText's rooted-candidate branch) has no message
        // envelope to read an openclawAutomation tag off, unlike the history
        // read — so the only provenance it can go on is whether the agent wrote
        // an explicit "MEDIA:" line. No gateway is configured in this suite
        // (TestBootstrap points settings at a fresh temp directory), so both the
        // byte fetch and the meta call reach their own host/token guard and
        // answer without a socket — the same "no gateway" seam
        // ChatPanelImageNoteTests.ARefusedPicturesTooltipIsThePathFromTheSource
        // already relies on for the history-path twin of this behaviour.

        // The regression pin: an explicit MEDIA: line on the live path kept
        // explaining a failure before this ticket, and still does — High
        // confidence, wording unchanged.
        [Fact]
        public async Task ALiveMediaLineFailureStillExplainsWhy()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("here you go\nMEDIA:/tmp/live_high.png"));

            for (var i = 0; i < 50 && session.History[0].ImageNote is null; i++)
                await Task.Delay(10);

            Assert.Equal("Picture not shown — couldn't ask the gateway why.",
                session.History[0].ImageNote);
            Assert.Equal(MediaConfidence.High, session.History[0].Confidence);
        }

        // The fix itself: a live reply ending in a rooted path with no "MEDIA:"
        // prefix and no automation to confirm it — the same unbounded, ordinary-
        // prose population "I deleted photo.png" belongs to — stays silent on a
        // failure. Confidence is set synchronously before the fetch even starts
        // (see TryResolveLocalMedia), so it is the positive witness that the
        // gate actually ran rather than nothing having happened yet.
        [Fact]
        public async Task ALiveTrailingRootedPathFailureStaysSilent()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("here you go /tmp/live_low.png"));

            Assert.Equal(MediaConfidence.Low, session.History[0].Confidence);

            await Task.Delay(200);

            Assert.Null(session.History[0].ImageNote);
            Assert.Null(session.History[0].ImageNoteDetail);
        }
    }
}

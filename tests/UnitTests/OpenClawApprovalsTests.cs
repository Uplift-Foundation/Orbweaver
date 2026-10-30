using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The pure half of Stage 6: reading the gateway's approval records, events and
// answers, and deciding what is safe to draw.
//
// **Every shape here is declared, not measured.** They were read out of
// openclaw 2026.10.5's own dist on the gateway host: the list row and the
// requested event are the same record (id, request, createdAtMs, expiresAtMs, and
// approvalKind); the resolved event is {id, decision, resolvedBy, ts, request};
// approval.resolve answers {applied, approval}. Nothing has been called against a
// live approval, because that needs this device paired with operator.approvals
// and a person on the gateway host to approve the pairing. When those are
// measured the fixtures are re-checked against them and this comment changes.
//
// Every id, path and command is invented.
public class OpenClawApprovalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string Key = "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555";

    // A declared list row, which is also the declared requested-event payload.
    private static string Row(
        string id = "approval-0001", string command = "ls -la /tmp", string extra = "",
        string sessionKey = Key) => $$"""
        {"id":"{{id}}","approvalKind":"exec",
         "request":{"command":{{JsonSerializer.Serialize(command)}},"cwd":"/tmp","host":"gateway",
                    "security":"allowlist","ask":"on-miss","allowedDecisions":["allow-once","deny"],
                    "agentId":"alpha","sessionKey":"{{sessionKey}}","runId":"run-0001"},
         "createdAtMs":{{Ms(Now)}},"expiresAtMs":{{Ms(Now.AddMinutes(30))}}{{extra}}}
        """;

    // --- a record ---

    [Fact]
    public void ADeclaredListRowIsReadWholeAndTheSameRowIsTheRequestedEvent()
    {
        var fromList = OpenClawApprovals.ParseRecord(Json(Row()), "exec", Now)!;
        var fromEvent = OpenClawApprovals.ParseRequested("exec.approval.requested", Json(Row()), Now)!;

        Assert.Equal(fromList, fromEvent);
        Assert.Equal("approval-0001", fromList.Id);
        Assert.Equal("exec", fromList.Kind);
        Assert.Equal(Key, fromList.SessionKey);
        Assert.Equal("alpha", fromList.AgentId);
        Assert.Equal("ls -la /tmp", fromList.Command);
        Assert.Equal("/tmp", fromList.Cwd);
        Assert.Equal("gateway", fromList.Host);
        Assert.Equal(Now, fromList.CreatedAt);
        Assert.Equal(Now.AddMinutes(30), fromList.ExpiresAt);
        Assert.False(fromList.CommandTruncated);
    }

    // A run-scoped key is trimmed back to the session's own, as a run event's is.
    [Fact]
    public void ARunScopedSessionKeyIsTrimmedToTheSession() =>
        Assert.Equal(Key, OpenClawApprovals.ParseRecord(
            Json(Row(sessionKey: Key + ":run:run-0001")), "exec", Now)!.SessionKey);

    // A top-level sourceSessionKey is used when the request has none, which is
    // how a record projected into a session-scoped surface carries it.
    [Fact]
    public void AProjectedRecordCarriesItsSessionAsSourceSessionKey()
    {
        var json = "{\"id\":\"a1\",\"sourceSessionKey\":\"" + Key + "\",\"request\":{\"command\":\"true\"}}";

        Assert.Equal(Key, OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.SessionKey);
    }

    // The record's own approvalKind outranks the kind the caller assumed, and an
    // unknown one falls back to it.
    [Theory]
    [InlineData("plugin", "plugin")]
    [InlineData("system-agent", "system-agent")]
    [InlineData("bogus", "exec")]
    public void TheRecordsOwnKindWinsOverTheCallers(string rowKind, string expected)
    {
        var json = Row().Replace("\"approvalKind\":\"exec\"", $"\"approvalKind\":\"{rowKind}\"");

        Assert.Equal(expected, OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.Kind);
    }

    [Fact]
    public void ARecordWithNoKindUsesTheCallers()
    {
        var json = Row().Replace("\"approvalKind\":\"exec\",", "");

        Assert.Equal("plugin", OpenClawApprovals.ParseRecord(Json(json), "plugin", Now)!.Kind);
    }

    // What cannot be drawn honestly is null, not a record that says something the
    // gateway did not.
    [Theory]
    [InlineData("[]")]
    [InlineData("\"approval\"")]
    [InlineData("{}")]
    [InlineData("{\"request\":{\"sessionKey\":\"agent:a:b\"}}")]
    [InlineData("{\"id\":\"  \",\"request\":{\"sessionKey\":\"agent:a:b\"}}")]
    [InlineData("{\"id\":\"a1\",\"request\":{\"command\":\"true\"}}")]
    [InlineData("{\"id\":\"a1\",\"request\":{\"command\":\"true\",\"sessionKey\":\"  \"}}")]
    [InlineData("{\"id\":\"a1\"}")]
    public void ARecordWithNoIdOrNoSessionIsNull(string json) =>
        Assert.Null(OpenClawApprovals.ParseRecord(Json(json), "exec", Now));

    [Fact]
    public void AnIdentifierWithAControlCharacterOrTooLongIsRefused()
    {
        Assert.Null(OpenClawApprovals.ParseRecord(Json(Row(id: "a\\nb")), "exec", Now));
        Assert.Null(OpenClawApprovals.ParseRecord(Json(Row(id: new string('x', OpenClawApprovals.MaxIdLength + 1))), "exec", Now));
        Assert.Null(OpenClawApprovals.ParseRecord(Json(Row(sessionKey: "agent:a:b\\u0007")), "exec", Now));
        Assert.Null(OpenClawApprovals.ParseRecord(Json(Row(sessionKey: "agent:" + new string('x', OpenClawApprovals.MaxKeyLength))), "exec", Now));

        // The control: the same fields at their limits are accepted.
        Assert.NotNull(OpenClawApprovals.ParseRecord(Json(Row(id: new string('x', OpenClawApprovals.MaxIdLength))), "exec", Now));
    }

    [Fact]
    public void AnAgentIdWithAControlCharacterOrTooLongIsDroppedNotTheRecord()
    {
        var bad = Row().Replace("\"agentId\":\"alpha\"", "\"agentId\":\"al\\u0007pha\"");
        var tooLong = Row().Replace("\"agentId\":\"alpha\"", $"\"agentId\":\"{new string('x', OpenClawApprovals.MaxIdLength + 1)}\"");

        Assert.Null(OpenClawApprovals.ParseRecord(Json(bad), "exec", Now)!.AgentId);
        Assert.Null(OpenClawApprovals.ParseRecord(Json(tooLong), "exec", Now)!.AgentId);
    }

    // --- times ---

    [Fact]
    public void ARecordThatSaysNothingOfItsTimesGetsTheExecDefaultFromNow()
    {
        var json = "{\"id\":\"a1\",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + Key + "\"}}";

        var approval = OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!;

        Assert.Equal(Now, approval.CreatedAt);
        Assert.Equal(Now + OpenClawApprovals.DefaultLifetime, approval.ExpiresAt);
    }

    [Fact]
    public void ACreationTimeWithNoExpiryCountsTheDefaultFromCreation()
    {
        var created = Now.AddMinutes(-10);
        var json = "{\"id\":\"a1\",\"createdAtMs\":" + Ms(created) + ",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + Key + "\"}}";

        Assert.Equal(created + OpenClawApprovals.DefaultLifetime, OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.ExpiresAt);
    }

    // A time that is not a positive number is no time.
    [Theory]
    [InlineData("\"createdAtMs\":\"soon\"")]
    [InlineData("\"createdAtMs\":0")]
    [InlineData("\"createdAtMs\":-5")]
    [InlineData("\"createdAtMs\":1.5")]
    public void ATimeThatIsNotAPositiveWholeNumberIsIgnored(string field)
    {
        var json = "{\"id\":\"a1\"," + field + ",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + Key + "\"}}";

        Assert.Equal(Now, OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.CreatedAt);
    }

    [Fact]
    public void ExpiryIsInclusiveOfTheInstant()
    {
        var approval = OpenClawApprovals.ParseRecord(Json(Row()), "exec", Now)!;

        Assert.False(OpenClawApprovals.IsExpired(approval, approval.ExpiresAt.AddMilliseconds(-1)));
        Assert.True(OpenClawApprovals.IsExpired(approval, approval.ExpiresAt));
        Assert.True(OpenClawApprovals.IsExpired(approval, approval.ExpiresAt.AddMinutes(1)));
    }

    // --- the command, as drawn ---

    [Fact]
    public void ACommandKeepsItsNewlinesAndTabs() =>
        Assert.Equal("a\n\tb", OpenClawApprovals.ParseRecord(Json(Row(command: "a\n\tb")), "exec", Now)!.Command);

    // Anything else that could hide a tail, or reorder one string into another, is
    // replaced, not refused: a legitimate command must still be answerable.
    [Fact]
    public void ControlAndBidirectionalCharactersAreReplacedNotRefused()
    {
        var command = "rm -rf /tmp/x\u0007\u001b[2K\u007f‮ txt.exe⁦‎‏‪⁩";

        var shown = OpenClawApprovals.ParseRecord(Json(Row(command: command)), "exec", Now)!.Command;

        Assert.Equal("rm -rf /tmp/x��[2K�� txt.exe�����", shown);
    }

    // Too long to show whole is cut and SAYS so, so the panel can refuse "Allow
    // once" for a command nobody saw the end of.
    [Fact]
    public void ACommandPastTheBoundIsCutAndMarkedTruncated()
    {
        var whole = new string('a', OpenClawApprovals.MaxCommand);
        var over = whole + "TAIL";

        var atLimit = OpenClawApprovals.ParseRecord(Json(Row(command: whole)), "exec", Now)!;
        var cut = OpenClawApprovals.ParseRecord(Json(Row(command: over)), "exec", Now)!;

        Assert.False(atLimit.CommandTruncated);
        Assert.Equal(whole, atLimit.Command);
        Assert.True(cut.CommandTruncated);
        Assert.Equal(whole, cut.Command);
        Assert.DoesNotContain("TAIL", cut.Command);
    }

    [Fact]
    public void CwdAndHostAreBoundedAndBlankMeansNone()
    {
        var long400 = new string('c', OpenClawApprovals.MaxShort + 50);
        var json = Row().Replace("\"cwd\":\"/tmp\"", $"\"cwd\":\"{long400}\"").Replace("\"host\":\"gateway\"", "\"host\":\"   \"");

        var approval = OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!;

        Assert.Equal(OpenClawApprovals.MaxShort, approval.Cwd!.Length);
        Assert.Null(approval.Host);
    }

    // A plugin approval has no shell command: it says what it is asking about.
    [Theory]
    [InlineData("\"commandArgv\":[\"git\",\"push\",7,\"origin\"]", "git push origin")]
    [InlineData("\"title\":\"Publish package\"", "Publish package")]
    [InlineData("\"description\":\"Writes outside the workspace\"", "Writes outside the workspace")]
    [InlineData("\"toolName\":\"browser.navigate\"", "browser.navigate")]
    public void WithoutACommandTheRequestIsDescribedByWhatItDoesCarry(string field, string expected)
    {
        var json = "{\"id\":\"a1\",\"request\":{" + field + ",\"sessionKey\":\"" + Key + "\"}}";

        Assert.Equal(expected, OpenClawApprovals.ParseRecord(Json(json), "plugin", Now)!.Command);
    }

    [Theory]
    [InlineData("\"commandArgv\":[]")]
    [InlineData("\"commandArgv\":\"ls\"")]
    [InlineData("\"commandArgv\":[1,2]")]
    [InlineData("\"command\":\"   \"")]
    [InlineData("\"cwd\":\"/tmp\"")]
    public void WithNothingToShowTheRecordSaysSo(string field)
    {
        var json = "{\"id\":\"a1\",\"request\":{" + field + ",\"sessionKey\":\"" + Key + "\"}}";

        Assert.Equal(
            "(the gateway did not say what this runs)",
            OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.Command);
    }

    // --- events, kinds and methods ---

    [Theory]
    [InlineData("exec.approval.requested", "exec", true, false)]
    [InlineData("exec.approval.resolved", "exec", false, true)]
    [InlineData("plugin.approval.requested", "plugin", true, false)]
    [InlineData("plugin.approval.resolved", "plugin", false, true)]
    [InlineData("openclaw.approval.requested", "system-agent", true, false)]
    [InlineData("openclaw.approval.resolved", "system-agent", false, true)]
    public void TheSixApprovalEventsAreRecognised(string name, string kind, bool requested, bool resolved)
    {
        Assert.Equal(kind, OpenClawApprovals.KindOfEvent(name));
        Assert.Equal(requested, OpenClawApprovals.IsRequestedEvent(name));
        Assert.Equal(resolved, OpenClawApprovals.IsResolvedEvent(name));
    }

    [Theory]
    [InlineData("sessions.changed")]
    [InlineData("chat")]
    [InlineData("exec.approval.waitDecision")]
    [InlineData("approval.requested")]
    [InlineData("")]
    public void NothingElseIsAnApprovalEvent(string name)
    {
        Assert.Null(OpenClawApprovals.KindOfEvent(name));
        Assert.False(OpenClawApprovals.IsRequestedEvent(name));
        Assert.False(OpenClawApprovals.IsResolvedEvent(name));
        Assert.Null(OpenClawApprovals.ParseRequested(name, Json(Row()), Now));
        Assert.Null(OpenClawApprovals.ParseResolved(name, Json("{\"id\":\"a1\"}")));
    }

    // A resolved event does not parse as a request, and the reverse.
    [Fact]
    public void ARequestIsNotParsedFromAResolvedNameNorTheReverse()
    {
        Assert.Null(OpenClawApprovals.ParseRequested("exec.approval.resolved", Json(Row()), Now));
        Assert.Null(OpenClawApprovals.ParseResolved("exec.approval.requested", Json("{\"id\":\"a1\"}")));
    }

    [Theory]
    [InlineData("exec", "exec.approval.list")]
    [InlineData("plugin", "plugin.approval.list")]
    [InlineData("system-agent", "openclaw.approval.list")]
    public void EachKindHasItsOwnListMethod(string kind, string method) =>
        Assert.Equal(method, OpenClawApprovals.ListMethodFor(kind));

    [Fact]
    public void ThereAreThreeKindsAndEachHasAListMethod()
    {
        Assert.Equal(new[] { "exec", "plugin", "system-agent" }, OpenClawApprovals.Kinds);
    }

    // --- a resolved event ---

    [Fact]
    public void ADeclaredResolvedEventCarriesWhoDecidedWhat()
    {
        var json = "{\"id\":\"approval-0001\",\"decision\":\"deny\",\"resolvedBy\":\"device-0001\",\"ts\":" + Ms(Now) + ",\"request\":{\"sessionKey\":\"" + Key + "\"}}";

        var resolved = OpenClawApprovals.ParseResolved("exec.approval.resolved", Json(json))!;

        Assert.Equal(new OpenClawApprovalResolved("approval-0001", "exec", "deny", null, "device-0001"), resolved);
    }

    [Fact]
    public void AResolvedEventCarriesAReasonWhenThereIsOne()
    {
        var resolved = OpenClawApprovals.ParseResolved(
            "plugin.approval.resolved", Json("{\"id\":\"a1\",\"decision\":null,\"reason\":\"timeout\"}"))!;

        Assert.Equal("plugin", resolved.Kind);
        Assert.Null(resolved.Decision);
        Assert.Equal("timeout", resolved.Reason);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"  \"}")]
    [InlineData("{\"id\":\"a\\nb\"}")]
    public void AResolvedEventWithNoUsableIdIsNull(string json) =>
        Assert.Null(OpenClawApprovals.ParseResolved("exec.approval.resolved", Json(json)));

    [Fact]
    public void AResolvedEventsOverlongIdIsNull() =>
        Assert.Null(OpenClawApprovals.ParseResolved(
            "exec.approval.resolved", Json($"{{\"id\":\"{new string('x', OpenClawApprovals.MaxIdLength + 1)}\"}}")));

    // --- a list ---

    [Fact]
    public void ABareArrayIsTheDeclaredListShape()
    {
        var list = OpenClawApprovals.ParseList(
            Json($"[{Row("a1")},{Row("a2")}]"), "exec", Now);

        Assert.Equal(new[] { "a1", "a2" }, list.Select(a => a.Id).ToArray());
    }

    [Theory]
    [InlineData("approvals")]
    [InlineData("items")]
    [InlineData("pending")]
    [InlineData("rows")]
    public void AnObjectHoldingTheArrayUnderALikelyNameIsAccepted(string name)
    {
        var list = OpenClawApprovals.ParseList(Json($"{{\"{name}\":[{Row("a1")}]}}"), "system-agent", Now);

        Assert.Equal("a1", Assert.Single(list).Id);
    }

    // One malformed row does not hide the rest, and a reply that is not a list is
    // an empty one.
    [Fact]
    public void OneBadRowIsSkippedAndAnythingThatIsNotAListIsEmpty()
    {
        var list = OpenClawApprovals.ParseList(Json($"[{{\"id\":\"no-session\"}},{Row("a2")},7]"), "exec", Now);

        Assert.Equal("a2", Assert.Single(list).Id);
        Assert.Empty(OpenClawApprovals.ParseList(Json("{\"something\":[]}"), "exec", Now));
        Assert.Empty(OpenClawApprovals.ParseList(Json("{\"approvals\":\"nope\"}"), "exec", Now));
        Assert.Empty(OpenClawApprovals.ParseList(Json("\"approvals\""), "exec", Now));
    }

    // --- the answer, and what may be sent ---

    [Fact]
    public void AnAppliedAnswerCarriesTheTerminalSnapshot()
    {
        var answer = OpenClawApprovals.ParseResolveResult(Json(
            "{\"applied\":true,\"approval\":{\"status\":\"denied\",\"reason\":\"user\",\"decision\":\"deny\"}}"))!;

        Assert.Equal(new OpenClawApprovalAnswer(true, "denied", "user", "deny"), answer);
    }

    // applied:false is another reviewer having decided first; the snapshot is what
    // they decided.
    [Fact]
    public void AnUnappliedAnswerIsAnotherReviewersDecision()
    {
        var answer = OpenClawApprovals.ParseResolveResult(Json(
            "{\"applied\":false,\"approval\":{\"status\":\"allowed\",\"decision\":\"allow-once\"}}"))!;

        Assert.False(answer.Applied);
        Assert.Equal("allowed", answer.Status);
        Assert.Equal("allow-once", answer.Decision);
    }

    [Fact]
    public void AnAnswerWithNoSnapshotStillSaysWhetherItApplied()
    {
        var answer = OpenClawApprovals.ParseResolveResult(Json("{\"applied\":true}"))!;

        Assert.True(answer.Applied);
        Assert.Null(answer.Status);
    }

    // A reply that does not say is not treated as applied: claiming an answer
    // landed that the gateway did not confirm would be a guess presented as a result.
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"applied\":\"yes\"}")]
    [InlineData("{\"ok\":true}")]
    [InlineData("[]")]
    public void AReplyThatDoesNotSayIsNull(string json) =>
        Assert.Null(OpenClawApprovals.ParseResolveResult(Json(json)));

    [Theory]
    [InlineData("deny", true)]
    [InlineData("allow-once", true)]
    [InlineData("allow-always", false)]
    [InlineData("Deny", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyDenyAndAllowOnceCanBeSent(string? decision, bool expected) =>
        Assert.Equal(expected, OpenClawApprovals.IsAnswerableDecision(decision));

    // The gateway deliberately does not say whether an id is unknown, expired or
    // not visible to this device, so its message is the only signal.
    [Theory]
    [InlineData("approval expired or not found", true)]
    [InlineData("Approval NOT FOUND", true)]
    [InlineData("unknown or expired approval id", true)]
    [InlineData("missing scope: operator.approvals", false)]
    [InlineData("invalid decision", false)]
    public void ANotFoundRefusalIsRecognisedByItsMessage(string message, bool expected) =>
        Assert.Equal(expected, OpenClawApprovals.IsNotFound(new OpenClawRequestException(message, null)));
}

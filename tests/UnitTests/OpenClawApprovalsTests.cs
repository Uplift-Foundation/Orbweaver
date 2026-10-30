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

    // --- the reviewer-safe snapshot, session events and the replay ---
    //
    // Declared, not measured: read from the gateway's own TypeBox schemas in
    // openclaw 2026.10.5's dist (approval-result-validators, "since 2026.7"). A
    // pending snapshot is {id, status, presentation, urlPath, createdAtMs,
    // expiresAtMs}; the presentation is what is being asked, with the runtime's cwd
    // and environment deliberately left out.

    private static string ExecPresentation(
        string command = "ls -la /tmp", string extra = "", string decisions = "[\"allow-once\",\"deny\"]") =>
        "{\"kind\":\"exec\",\"commandText\":" + JsonSerializer.Serialize(command) +
        ",\"host\":\"gateway\",\"agentId\":\"alpha\",\"allowedDecisions\":" + decisions + extra + "}";

    private static string Snapshot(string id = "approval-0001", string presentation = "") =>
        "{\"id\":\"" + id + "\",\"status\":\"pending\",\"urlPath\":\"/approve/" + id + "\"," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) + "," +
        "\"presentation\":" + (presentation.Length == 0 ? ExecPresentation() : presentation) + "}";

    [Fact]
    public void AnExecSnapshotIsReadFromItsPresentationWithNoCwd()
    {
        var approval = OpenClawApprovals.ParseSnapshot(Json(Snapshot()), Key, Now)!;

        Assert.Equal("approval-0001", approval.Id);
        Assert.Equal("exec", approval.Kind);
        Assert.Equal(Key, approval.SessionKey);
        Assert.Equal("alpha", approval.AgentId);
        Assert.Equal("ls -la /tmp", approval.Command);
        Assert.Null(approval.Cwd);
        Assert.Equal("gateway", approval.Host);
        Assert.Equal(Now, approval.CreatedAt);
        Assert.Equal(new[] { "allow-once", "deny" }, approval.AllowedDecisions);
        Assert.Null(approval.Warning);
    }

    // The preview is the fallback when the full text is absent, and the warning is
    // carried for the panel to show.
    [Fact]
    public void AnExecSnapshotFallsBackToThePreviewAndCarriesItsWarning()
    {
        var presentation = "{\"kind\":\"exec\",\"commandPreview\":\"rm -rf …\",\"warningText\":\"deletes files\"," +
                           "\"allowedDecisions\":[\"deny\"]}";

        var approval = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation: presentation)), Key, Now)!;

        Assert.Equal("rm -rf …", approval.Command);
        Assert.Equal("deletes files", approval.Warning);
        Assert.Equal(new[] { "deny" }, approval.AllowedDecisions);
    }

    [Fact]
    public void APluginAndASystemAgentSnapshotAreDescribedByTheirTitleAndDescription()
    {
        var plugin = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"plugin\",\"title\":\"Publish\",\"description\":\"Pushes the package\",\"severity\":\"high\"," +
            "\"allowedDecisions\":[\"allow-once\",\"deny\"]}")), Key, Now)!;
        var system = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"system-agent\",\"title\":\"Change config\",\"description\":\"Edits openclaw.json\"," +
            "\"proposalHash\":\"" + new string('a', 64) + "\",\"allowedDecisions\":[\"allow-once\",\"deny\"]}")), Key, Now)!;
        var titleOnly = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"plugin\",\"title\":\"Publish\"}")), Key, Now)!;
        var descriptionOnly = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"plugin\",\"description\":\"Pushes the package\"}")), Key, Now)!;
        var toolOnly = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"plugin\",\"toolName\":\"browser.navigate\"}")), Key, Now)!;

        Assert.Equal("plugin", plugin.Kind);
        Assert.Equal("Publish — Pushes the package", plugin.Command);
        Assert.Equal("system-agent", system.Kind);
        Assert.Equal("Change config — Edits openclaw.json", system.Command);
        Assert.Equal("Publish", titleOnly.Command);
        Assert.Equal("Pushes the package", descriptionOnly.Command);
        Assert.Equal("browser.navigate", toolOnly.Command);
    }

    // The session key is the caller's, trimmed of a run suffix, and a snapshot is
    // refused without a presentation of a known kind, an id, or a usable key.
    [Fact]
    public void ASnapshotUsesTheCallersKeyTrimmedAndRefusesWhatCannotBeDrawn()
    {
        Assert.Equal(Key, OpenClawApprovals.ParseSnapshot(Json(Snapshot()), Key + ":run:r1", Now)!.SessionKey);

        Assert.Null(OpenClawApprovals.ParseSnapshot(Json("[]"), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json("{\"presentation\":{\"kind\":\"exec\"}}"), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot()), "  ", Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot()), "agent:a\u0007", Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot()), "agent:" + new string('x', OpenClawApprovals.MaxKeyLength), Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot(id: "a\\nb")), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot(id: new string('x', OpenClawApprovals.MaxIdLength + 1))), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json("{\"id\":\"a1\"}"), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json("{\"id\":\"a1\",\"presentation\":7}"), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation: "{\"kind\":\"shell\"}")), Key, Now));
    }

    [Fact]
    public void ASnapshotWithNoTimesGetsTheDefaultAndABadAgentIdIsDropped()
    {
        var json = "{\"id\":\"a1\",\"presentation\":{\"kind\":\"exec\",\"commandText\":\"true\",\"agentId\":\"a\\u0007b\"}}";

        var approval = OpenClawApprovals.ParseSnapshot(Json(json), Key, Now)!;

        Assert.Equal(Now, approval.CreatedAt);
        Assert.Equal(Now + OpenClawApprovals.DefaultLifetime, approval.ExpiresAt);
        Assert.Null(approval.AgentId);
        Assert.Null(approval.AllowedDecisions);

        var tooLong = "{\"id\":\"a1\",\"presentation\":{\"kind\":\"exec\",\"commandText\":\"true\",\"agentId\":\"" +
                      new string('x', OpenClawApprovals.MaxIdLength + 1) + "\"}}";
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(tooLong), Key, Now)!.AgentId);
    }

    // The decisions listed: only well-formed strings, de-duplicated, at most three,
    // and nothing readable means "did not say".
    [Theory]
    [InlineData("[\"deny\"]", new[] { "deny" })]
    [InlineData("[\"deny\",\"deny\",\"allow-once\"]", new[] { "deny", "allow-once" })]
    [InlineData("[\"a\",\"b\",\"c\",\"d\"]", new[] { "a", "b", "c" })]
    [InlineData("[\"ok\",7,\"bad\\u0007\",\"\"]", new[] { "ok" })]
    [InlineData("[]", null)]
    [InlineData("[7]", null)]
    [InlineData("\"deny\"", null)]
    public void TheListedDecisionsAreReadForgivingly(string decisions, string[]? expected)
    {
        var json = Snapshot(presentation: ExecPresentation(decisions: decisions));

        Assert.Equal(expected, OpenClawApprovals.ParseSnapshot(Json(json), Key, Now)!.AllowedDecisions);
    }

    // The legacy request carries the same two fields.
    [Fact]
    public void ALegacyRowCarriesItsAllowedDecisionsAndWarningToo()
    {
        var json = Row().Replace("\"security\":\"allowlist\"", "\"security\":\"allowlist\",\"warningText\":\"runs as root\"");

        var approval = OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!;

        Assert.Equal(new[] { "allow-once", "deny" }, approval.AllowedDecisions);
        Assert.Equal("runs as root", approval.Warning);
        Assert.Null(OpenClawApprovals.ParseRecord(Json(Row().Replace("\"allowedDecisions\":[\"allow-once\",\"deny\"],", "")), "exec", Now)!.AllowedDecisions);
    }

    // The new fields take part in equality, and Allows says what is ruled out.
    [Fact]
    public void TheDecisionListIsPartOfAnApprovalsValueAndAllowsReadsIt()
    {
        var a = OpenClawApprovals.ParseSnapshot(Json(Snapshot()), Key, Now)!;
        var same = OpenClawApprovals.ParseSnapshot(Json(Snapshot()), Key, Now)!;
        var denyOnly = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation: ExecPresentation(decisions: "[\"deny\"]"))), Key, Now)!;
        var unsaid = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation: ExecPresentation(decisions: "[]"))), Key, Now)!;

        Assert.Equal(a, same);
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(a, denyOnly);
        Assert.NotEqual(a, unsaid);
        Assert.NotEqual(unsaid, a);
        Assert.False(a.Equals(null));
        Assert.Equal(unsaid, OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation: ExecPresentation(decisions: "[]"))), Key, Now));

        Assert.True(a.Allows("allow-once"));
        Assert.False(denyOnly.Allows("allow-once"));
        Assert.True(denyOnly.Allows("deny"));
        Assert.True(unsaid.Allows("allow-once"));
        Assert.NotEqual(a, a with { Warning = "different" });
    }

    // --- a session.approval event ---

    private static string SessionEvent(string phase, string approval, string key = Key) =>
        "{\"sessionKey\":\"" + key + "\",\"sourceSessionKey\":\"" + key + "\",\"updatedAtMs\":" + Ms(Now) +
        ",\"phase\":\"" + phase + "\",\"approval\":" + approval + "}";

    [Fact]
    public void APendingSessionEventCarriesTheApproval()
    {
        var change = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("pending", Snapshot())), Now)!;

        Assert.Equal("pending", change.Phase);
        Assert.Equal("approval-0001", change.Pending!.Id);
        Assert.Equal(Key, change.Pending.SessionKey);
        Assert.Null(change.Terminal);
    }

    [Fact]
    public void ATerminalSessionEventCarriesHowItEnded()
    {
        var terminal = "{\"id\":\"approval-0001\",\"status\":\"denied\",\"decision\":\"deny\",\"reason\":\"user\"," +
                       "\"resolvedAtMs\":" + Ms(Now) + ",\"resolver\":{\"kind\":\"device\",\"id\":\"device-0001\"}," +
                       "\"presentation\":" + ExecPresentation() + "}";

        var change = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", terminal)), Now)!;

        Assert.Equal("terminal", change.Phase);
        Assert.Equal(new OpenClawApprovalResolved("approval-0001", "exec", "deny", "user", "device-0001"), change.Terminal);
        Assert.Null(change.Pending);
    }

    // An expired snapshot has no decision, and the reason or the status says why.
    [Fact]
    public void ATerminalEventWithNoDecisionOrResolverFallsBackToTheStatus()
    {
        var withReason = "{\"id\":\"a1\",\"status\":\"expired\",\"reason\":\"timeout\"}";
        var statusOnly = "{\"id\":\"a1\",\"status\":\"cancelled\"}";
        var byKind = "{\"id\":\"a1\",\"status\":\"allowed\",\"decision\":\"allow-once\",\"resolver\":{\"kind\":\"channel\"}}";

        var a = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", withReason)), Now)!.Terminal!;
        var b = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", statusOnly)), Now)!.Terminal!;
        var c = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", byKind)), Now)!.Terminal!;

        Assert.Equal(("exec", (string?)null, "timeout", (string?)null), (a.Kind, a.Decision, a.Reason, a.ResolvedBy));
        Assert.Equal("cancelled", b.Reason);
        Assert.Equal("channel", c.ResolvedBy);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"sessionKey\":\"agent:a:b\",\"phase\":\"pending\"}")]
    [InlineData("{\"phase\":\"pending\",\"approval\":{}}")]
    [InlineData("{\"sessionKey\":\"agent:a:b\",\"phase\":\"unknown\",\"approval\":{}}")]
    [InlineData("{\"sessionKey\":\"agent:a:b\",\"phase\":\"pending\",\"approval\":{\"id\":\"a1\"}}")]
    [InlineData("{\"sessionKey\":\"agent:a:b\",\"phase\":\"terminal\",\"approval\":{}}")]
    [InlineData("{\"sessionKey\":\"agent:a:b\",\"phase\":\"terminal\",\"approval\":{\"id\":\"a\\nb\"}}")]
    public void AMalformedSessionEventIsNull(string json) =>
        Assert.Null(OpenClawApprovals.ParseSessionEvent(Json(json), Now));

    [Fact]
    public void ATerminalEventsOverlongIdIsNull() =>
        Assert.Null(OpenClawApprovals.ParseSessionEvent(Json(SessionEvent(
            "terminal", "{\"id\":\"" + new string('x', OpenClawApprovals.MaxIdLength + 1) + "\"}")), Now));

    [Fact]
    public void OnlyTheSessionApprovalNameIsASessionEvent()
    {
        Assert.True(OpenClawApprovals.IsSessionEvent("session.approval"));
        Assert.False(OpenClawApprovals.IsSessionEvent("exec.approval.requested"));
        Assert.False(OpenClawApprovals.IsSessionEvent("session.message"));
    }

    // --- the subscribe's replay ---

    private static string Response(string approvals, bool truncated = false, bool withReplay = true) =>
        "{\"subscribed\":true,\"key\":\"" + Key + "\",\"agentId\":\"alpha\"" +
        (withReplay
            ? ",\"approvalReplay\":{\"sessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) +
              ",\"approvals\":" + approvals + ",\"truncated\":" + (truncated ? "true" : "false") + "}"
            : "") + "}";

    [Fact]
    public void AReplayIsReadUnderTheKeyThatWasSubscribed()
    {
        var replay = OpenClawApprovals.ParseReplay(
            Json(Response("[" + Snapshot("a1") + "," + Snapshot("a2") + "]")), Key, Now)!;

        Assert.Equal(new[] { "a1", "a2" }, replay.Approvals.Select(a => a.Id).ToArray());
        Assert.All(replay.Approvals, a => Assert.Equal(Key, a.SessionKey));
        Assert.False(replay.Truncated);
    }

    [Fact]
    public void ATruncatedReplaySaysSoAndABadRowIsSkipped()
    {
        var replay = OpenClawApprovals.ParseReplay(
            Json(Response("[{\"id\":\"no-presentation\"}," + Snapshot("a2") + "]", truncated: true)), Key, Now)!;

        Assert.True(replay.Truncated);
        Assert.Equal("a2", Assert.Single(replay.Approvals).Id);
    }

    // No replay in the answer, or not in the shape, is no replay: a subscribe made
    // without includeApprovals answers none.
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"subscribed\":true}")]
    [InlineData("{\"approvalReplay\":7}")]
    [InlineData("{\"approvalReplay\":{}}")]
    [InlineData("{\"approvalReplay\":{\"approvals\":\"nope\"}}")]
    public void AnAnswerWithNoReplayIsNull(string json) =>
        Assert.Null(OpenClawApprovals.ParseReplay(Json(json), Key, Now));

    [Fact]
    public void AnEmptyReplayIsAnEmptyAuthoritativeSet()
    {
        var replay = OpenClawApprovals.ParseReplay(Json(Response("[]")), Key, Now)!;

        Assert.Empty(replay.Approvals);
        Assert.False(replay.Truncated);
    }
}

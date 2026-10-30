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

    // A legacy list row: {id, request, createdAtMs, expiresAtMs, approvalKind}, as
    // `exec.approval.list` answers it (read from the handler, which responds a bare
    // array of exactly these). It is ALSO used as the `*.approval.requested`
    // broadcast payload, and that use is ASSEMBLED, not declared: the broadcast has no
    // named schema in the gateway's dist, it is built inline from the record, so this
    // is the record's declared fields standing in for it. P3's live capture is what
    // confirms the broadcast shape; until then nobody should read this as measured.
    // The `request` object is the raw stored request, not a closed schema: `command`,
    // `cwd`, `host`, `agentId` and `sessionKey` match ExecApprovalRequestParamsSchema,
    // while `security`, `ask`, `runId` and `allowedDecisions` are plausible but ASSUMED.
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

        // Counted from now, not from the gateway's creation time: an old creation
        // date must not leave a record already expired the moment it is read.
        Assert.Equal(Now + OpenClawApprovals.DefaultLifetime, OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.ExpiresAt);
        Assert.Equal(created, OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.CreatedAt);
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

    // A multi-line script stays answerable: newline and tab are real line breaks
    // and tabs in the command, \r\n is one line break, and nothing is flagged.
    [Fact]
    public void AMultiLineHeredocIsSafeAndKeepsItsLineBreaks()
    {
        var command = "cat <<'EOF' > /tmp/x\n\tline one\r\n\tline two\nEOF";

        var approval = OpenClawApprovals.ParseRecord(Json(Row(command: command)), "exec", Now)!;

        Assert.False(approval.Unsafe);
        Assert.Equal("cat <<'EOF' > /tmp/x\n\tline one\n\tline two\nEOF", approval.Command);
        Assert.Equal(approval.Command.Length, approval.CommandLength);
    }

    // Every class of character that can make one string read as another marks the
    // request Unsafe: its text is not shown and never rewritten into clean-looking
    // text, the record is kept so the orb is still amber, and the panel offers
    // Deny only. One case per class, in the command and, where it applies, beside it.
    [Theory]
    [InlineData("\u0007", "BEL, a C0 control")]
    [InlineData("\u001b", "ESC, a C0 control")]
    [InlineData("\u007f", "DEL")]
    [InlineData("\u0085", "NEL, a C1 control")]
    [InlineData("\u009b", "CSI, a C1 control")]
    [InlineData("\r", "a lone carriage return")]
    [InlineData("\u202E", "a bidi override")]
    [InlineData("\u2066", "a bidi isolate")]
    [InlineData("\u200E", "a left-to-right mark")]
    [InlineData("\u200B", "a zero-width space")]
    [InlineData("\u200D", "a zero-width joiner")]
    [InlineData("\uFEFF", "the byte order mark")]
    [InlineData("\u2028", "a line separator, Zl")]
    [InlineData("\u2029", "a paragraph separator, Zp")]
    public void AHostileCharacterInTheCommandMarksTheRequestUnsafeAndShowsNoText(string hostile, string why)
    {
        var approval = OpenClawApprovals.ParseRecord(
            Json(Row(command: "rm -rf /tmp/x " + hostile + " txt.exe")), "exec", Now)!;

        Assert.True(approval.Unsafe, why);
        Assert.Equal("", approval.Command);
        Assert.Null(approval.Cwd);
        Assert.Null(approval.Host);
        Assert.Null(approval.Warning);
        Assert.Null(approval.AgentId);

        // Kept, not dropped: the identity and the clamped expiry are all the orb needs.
        Assert.Equal("approval-0001", approval.Id);
        Assert.Equal(Key, approval.SessionKey);
        Assert.Equal("exec", approval.Kind);
        Assert.True(approval.CommandLength > 0);
    }

    // The same classes beside the command, where not even a newline or a tab is
    // allowed: cwd, host, agent and the warning.
    [Theory]
    [InlineData("cwd", "\n")]
    [InlineData("cwd", "\t")]
    [InlineData("cwd", "\u202E")]
    [InlineData("host", "\n")]
    [InlineData("host", "\u200B")]
    [InlineData("agentId", "\u0007")]
    [InlineData("agentId", "\n")]
    [InlineData("warningText", "\t")]
    [InlineData("warningText", "\u2028")]
    public void AHostileCharacterBesideTheCommandAlsoMarksTheRequestUnsafe(string field, string hostile)
    {
        var json = Row().Replace(
            "\"" + field + "\":\"" + (field == "agentId" ? "alpha" : field == "cwd" ? "/tmp" : field == "host" ? "gateway" : "") + "\"",
            "\"" + field + "\":\"x" + JsonSerializer.Serialize(hostile).Trim('"') + "y\"");
        if (field == "warningText")
            json = Row().Replace("\"security\":\"allowlist\"",
                "\"warningText\":\"x" + JsonSerializer.Serialize(hostile).Trim('"') + "y\",\"security\":\"allowlist\"");

        var approval = OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!;

        Assert.True(approval.Unsafe);
        Assert.Equal("", approval.Command);
    }

    // The control: the same fields, clean, are not flagged.
    [Fact]
    public void CleanFieldsAreNotFlagged()
    {
        var approval = OpenClawApprovals.ParseRecord(Json(Row()), "exec", Now)!;

        Assert.False(approval.Unsafe);
        Assert.Equal("ls -la /tmp", approval.Command);
    }

    // An agent id past the id bound is unsafe to show too.
    [Fact]
    public void AnAgentIdPastTheBoundMarksTheRequestUnsafe()
    {
        var json = Row().Replace("\"agentId\":\"alpha\"", "\"agentId\":\"" + new string('x', OpenClawApprovals.MaxIdLength + 1) + "\"");

        Assert.True(OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!.Unsafe);
    }

    // IsDisplaySafe, directly: the allowed shapes and the refused ones.
    [Theory]
    [InlineData("plain text", false, true)]
    [InlineData("emoji \ud83d\ude00 and accents \u00e9", false, true)]
    [InlineData("a\nb", true, true)]
    [InlineData("a\tb", true, true)]
    [InlineData("a\nb", false, false)]
    [InlineData("a\tb", false, false)]
    [InlineData("a\rb", true, false)]
    [InlineData("a\u202Eb", true, false)]
    [InlineData("", false, true)]
    public void DisplaySafetyAcceptsOnlyWhatCannotReorderOrHideText(string text, bool multiLine, bool expected) =>
        Assert.Equal(expected, OpenClawApprovals.IsDisplaySafe(text, multiLine));

    [Fact]
    public void NullIsSafeToDisplayBecauseThereIsNothingToShow() =>
        Assert.True(OpenClawApprovals.IsDisplaySafe(null));

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
    // ASSEMBLED, not declared. The `.resolved` broadcast has no named schema; the only
    // declared vocabulary for a resolved record is the terminal snapshot (status,
    // reason, decision, resolvedAtMs, a resolver object), which the tests further
    // down use. This older shape, with `resolvedBy` and `ts`, is what the first read of
    // the dist suggested and is kept only as an assumed alias. P3's live capture
    // confirms which the broadcast really sends.
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

    // The gateway's own not-found, and nothing looser. Its detail is carried in
    // `details.reason`, which this client does not read, so the code and the exact
    // sentence are the signal. A refusal that merely contains the word "unknown" or
    // "expired" is a failure like any other: treating it as not-found would take a
    // real pending approval out of sight and strand it.
    [Theory]
    [InlineData("INVALID_REQUEST", "unknown or expired approval id", true)]
    [InlineData("INVALID_REQUEST", "approval expired or not found", true)]
    [InlineData("INVALID_REQUEST", "approval already resolved", true)]
    [InlineData("INVALID_REQUEST", "unknown error: gateway busy", false)]
    [InlineData("INVALID_REQUEST", "approval expired or not found today", false)]
    [InlineData("INVALID_REQUEST", "UNKNOWN OR EXPIRED APPROVAL ID", false)]
    [InlineData("INVALID_REQUEST", "Approval NOT FOUND", false)]
    [InlineData("INVALID_REQUEST", "missing scope: operator.approvals", false)]
    [InlineData("INVALID_REQUEST", "invalid decision", false)]
    [InlineData("UNAVAILABLE", "unknown or expired approval id", false)]
    [InlineData(null, "unknown or expired approval id", false)]
    public void ANotFoundRefusalIsTheGatewaysOwnCodeAndSentenceAndNothingLooser(
        string? code, string message, bool expected) =>
        Assert.Equal(expected, OpenClawApprovals.IsNotFound(new OpenClawRequestException(message, null) { Code = code }));

    [Fact]
    public void AnExceptionThatIsNotAGatewayRefusalIsNeverNotFound() =>
        Assert.False(OpenClawApprovals.IsNotFound(new InvalidOperationException("unknown or expired approval id")));

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
    // carried for the panel to show. TOLERANCE, not a declared shape: the declared
    // exec presentation requires commandText (a non-empty string).
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

    // The full plugin and system-agent presentations are declared shapes. The
    // title-only, description-only and toolName-only ones are TOLERANCE: the
    // declaration requires title, description and severity together.
    [Fact]
    public void APluginAndASystemAgentSnapshotAreDescribedByTheirTitleAndDescription()
    {
        var plugin = OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"plugin\",\"title\":\"Publish\",\"description\":\"Pushes the package\",\"severity\":\"warning\"," +
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
        // The declared shape (DeniedApprovalSnapshotSchema): the common record
        // fields (urlPath, createdAtMs, expiresAtMs) plus the terminal ones.
        var terminal = "{\"id\":\"approval-0001\",\"status\":\"denied\",\"urlPath\":\"/approve/approval-0001\"," +
                       "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) + "," +
                       "\"decision\":\"deny\",\"reason\":\"user\"," +
                       "\"resolvedAtMs\":" + Ms(Now) + ",\"resolver\":{\"kind\":\"device\",\"id\":\"device-0001\"}," +
                       "\"presentation\":" + ExecPresentation() + "}";

        var change = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", terminal)), Now)!;

        Assert.Equal("terminal", change.Phase);
        Assert.Equal(new OpenClawApprovalResolved("approval-0001", "exec", "deny", "user", "device-0001", "denied"), change.Terminal);
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

    // --- hostile input, one case per way a row can go wrong ---

    // A time out of range must cost the row nothing worse than the time. These are
    // the values that used to throw out of the parser: long.MaxValue and anything
    // past the last instant a DateTimeOffset holds. Through the poll that stalled
    // every OpenClaw orb, not only approvals.
    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("1000000000000000")]
    [InlineData("253402300800000")]
    [InlineData("253402300799999")]
    [InlineData("-9223372036854775808")]
    [InlineData("1e400")]
    [InlineData("\"soon\"")]
    [InlineData("1.5")]
    public void APoisonedTimeNeverThrowsAndNeverCostsTheRow(string value)
    {
        var json = Row().Replace("\"createdAtMs\":" + Ms(Now), "\"createdAtMs\":" + value)
                        .Replace("\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":" + value);

        var approval = OpenClawApprovals.ParseRecord(Json(json), "exec", Now);

        Assert.NotNull(approval);
        Assert.Equal("approval-0001", approval!.Id);
        Assert.True(approval.ExpiresAt >= Now + OpenClawApprovals.MinLifetime);
        Assert.True(approval.ExpiresAt <= Now + OpenClawApprovals.MaxLifetime);
    }

    // A whole list with one poisoned row beside good ones comes back with the good
    // ones: the poisoned row is the only casualty.
    [Fact]
    public void APoisonedRowBesideGoodOnesDoesNotCostTheGoodOnes()
    {
        var poisoned = Row("poison").Replace("\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":9223372036854775807");

        var list = OpenClawApprovals.ParseList(Json("[" + Row("good1") + "," + poisoned + "," + Row("good2") + "]"), "exec", Now);

        Assert.Equal(new[] { "good1", "poison", "good2" }, list.Select(a => a.Id).ToArray());
    }

    // A list is read to a bound: a hostile gateway cannot make this parse a million
    // rows.
    [Fact]
    public void AListIsReadOnlyToItsBound()
    {
        var rows = string.Join(",", Enumerable.Range(0, OpenClawApprovals.MaxListRows + 50).Select(i => Row("r" + i)));

        var list = OpenClawApprovals.ParseList(Json("[" + rows + "]"), "exec", Now);

        Assert.Equal(OpenClawApprovals.MaxListRows, list.Count);
    }

    // --- expiry is the gateway's date, clamped, never trusted for control ---

    [Fact]
    public void ADateAYearAheadExpiresInADayAndOneInThePastIsShownForASecond()
    {
        var farAhead = OpenClawApprovals.ParseRecord(Json(Row().Replace(
            "\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":" + Ms(Now.AddDays(365)))), "exec", Now)!;
        var past = OpenClawApprovals.ParseRecord(Json(Row().Replace(
            "\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":" + Ms(Now.AddDays(-1)))), "exec", Now)!;

        Assert.Equal(Now + OpenClawApprovals.MaxLifetime, farAhead.ExpiresAt);
        Assert.Equal(Now + OpenClawApprovals.MinLifetime, past.ExpiresAt);

        // Shown for the second it is given, then gone: not dropped unseen.
        Assert.False(OpenClawApprovals.IsExpired(past, Now));
        Assert.True(OpenClawApprovals.IsExpired(past, Now + OpenClawApprovals.MinLifetime));
    }

    // Inside the range the gateway's own value is kept; at the edges it is too.
    [Fact]
    public void ADateInsideTheRangeIsKeptAsItIs()
    {
        var inside = Now.AddMinutes(30);
        var approval = OpenClawApprovals.ParseRecord(Json(Row()), "exec", Now)!;

        Assert.Equal(inside, approval.ExpiresAt);

        var atLowEdge = OpenClawApprovals.ParseRecord(Json(Row().Replace(
            "\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":" + Ms(Now + OpenClawApprovals.MinLifetime))), "exec", Now)!;
        var atHighEdge = OpenClawApprovals.ParseRecord(Json(Row().Replace(
            "\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":" + Ms(Now + OpenClawApprovals.MaxLifetime))), "exec", Now)!;

        Assert.Equal(Now + OpenClawApprovals.MinLifetime, atLowEdge.ExpiresAt);
        Assert.Equal(Now + OpenClawApprovals.MaxLifetime, atHighEdge.ExpiresAt);
    }

    // --- identifiers ---

    // The gateway admits any non-empty id without an unpaired surrogate, up to 512
    // characters, so the pattern is not tightened here or a real approval would be
    // unanswerable. A printable id with quotes and punctuation is kept byte for byte,
    // because it only ever goes into a JSON frame.
    [Fact]
    public void AnIdIsTheGatewaysOwnWellFormednessAndNothingTighter()
    {
        foreach (var id in new[] { new string('x', OpenClawApprovals.MaxIdLength), "a b;'\"\\ <tag> é😀", "  ", "x.y" })
        {
            var approval = OpenClawApprovals.ParseRecord(Json(Row(id: JsonSerializer.Serialize(id).Trim('"'))), "exec", Now);

            Assert.NotNull(approval);
            Assert.Equal(id, approval!.Id);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void AnEmptyOrPathLikeIdIsNotStored(string id) =>
        Assert.False(OpenClawApprovals.IsWellFormedId(id));

    [Fact]
    public void AnIdPastTheBoundIsNotStored() =>
        Assert.False(OpenClawApprovals.IsWellFormedId(new string('x', OpenClawApprovals.MaxIdLength + 1)));

    // An id containing any control, format, line or paragraph separator, or a lone
    // surrogate, is never stored, shown or sent.
    [Theory]
    [InlineData("a\u0007b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\uFEFFb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\n")]
    public void AnIdWithAHostileCharacterIsNeverStored(string id)
    {
        Assert.False(OpenClawApprovals.IsWellFormedId(id));
        Assert.Null(OpenClawApprovals.IsWellFormedId(null) ? "" : null);
    }

    [Fact]
    public void AnIdThatIsNullIsNotWellFormed() => Assert.False(OpenClawApprovals.IsWellFormedId(null));

    // ...whichever feed it arrives by.
    [Fact]
    public void AHostileIdIsRefusedByEveryFeed()
    {
        const string bad = "a\\u202Eb";

        Assert.Null(OpenClawApprovals.ParseRecord(Json(Row(id: bad)), "exec", Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot(id: bad)), Key, Now));
        Assert.Null(OpenClawApprovals.ParseResolved("exec.approval.resolved", Json("{\"id\":\"" + bad + "\"}")));
        Assert.Null(OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", "{\"id\":\"" + bad + "\"}")), Now));
    }

    // --- kind ---

    // A hostile approvalKind is refused: it is sent back on a resolve, so it is not
    // stored. The caller's own kind is used only when the row names none.
    [Theory]
    [InlineData("shell")]
    [InlineData("EXEC")]
    [InlineData("")]
    public void AnApprovalKindOutsideTheThreeIsRefused(string kind) =>
        Assert.Null(OpenClawApprovals.ParseRecord(
            Json(Row().Replace("\"approvalKind\":\"exec\"", "\"approvalKind\":\"" + kind + "\"")), "exec", Now));

    // --- the command's bound ---

    [Fact]
    public void ACommandOfExactlyTheBoundIsWholeAndOneOverIsCutAndKnowsItsLength()
    {
        var at = OpenClawApprovals.ParseRecord(Json(Row(command: new string('a', 4000))), "exec", Now)!;
        var over = OpenClawApprovals.ParseRecord(Json(Row(command: new string('a', 4001))), "exec", Now)!;

        Assert.Equal(4000, OpenClawApprovals.MaxCommand);
        Assert.False(at.CommandTruncated);
        Assert.Equal(4000, at.CommandLength);
        Assert.True(over.CommandTruncated);
        Assert.Equal(4000, over.Command.Length);
        Assert.Equal(4001, over.CommandLength);
    }

    // Never cut through the middle of a surrogate pair.
    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        var command = new string('a', 3999) + "😀" + "tail";

        var approval = OpenClawApprovals.ParseRecord(Json(Row(command: command)), "exec", Now)!;

        Assert.True(approval.CommandTruncated);
        Assert.Equal(3999, approval.Command.Length);
        Assert.True(OpenClawApprovals.IsDisplaySafe(approval.Command));
    }

    // A hostile character past the cut still marks it unsafe: the whole command
    // is judged, not only the part shown.
    [Fact]
    public void AHostileCharacterBeyondTheCutStillMarksTheRequestUnsafe()
    {
        var approval = OpenClawApprovals.ParseRecord(
            Json(Row(command: new string('a', 4100) + "\u202E")), "exec", Now)!;

        Assert.True(approval.Unsafe);
        Assert.Equal(4101, approval.CommandLength);
    }

    // --- the terminal vocabulary ---

    [Theory]
    [InlineData("allowed", "allow-once", "user")]
    [InlineData("denied", "deny", "user")]
    [InlineData("expired", null, "timeout")]
    [InlineData("cancelled", null, "run-aborted")]
    public void AResolvedEventReadsTheDeclaredTerminalVocabulary(string status, string? decision, string reason)
    {
        var json = "{\"id\":\"a1\",\"status\":\"" + status + "\",\"reason\":\"" + reason + "\"" +
                   (decision is null ? "" : ",\"decision\":\"" + decision + "\"") +
                   ",\"resolvedAtMs\":" + Ms(Now) + ",\"resolver\":{\"kind\":\"device\",\"id\":\"device-0001\"}}";

        var resolved = OpenClawApprovals.ParseResolved("exec.approval.resolved", Json(json))!;

        Assert.Equal(new OpenClawApprovalResolved("a1", "exec", decision, reason, "device-0001", status), resolved);
    }

    // The alias the first read of the dist had, accepted as assumed: a string
    // resolvedBy with no resolver object, and the resolver's kind when it has no id.
    [Fact]
    public void AResolvedEventStillAcceptsTheAssumedAliasAndTheResolversKind()
    {
        var alias = OpenClawApprovals.ParseResolved("exec.approval.resolved", Json("{\"id\":\"a1\",\"resolvedBy\":\"device-0009\"}"))!;
        var byKind = OpenClawApprovals.ParseResolved("exec.approval.resolved", Json("{\"id\":\"a1\",\"resolver\":{\"kind\":\"channel\"}}"))!;

        Assert.Equal("device-0009", alias.ResolvedBy);
        Assert.Equal("channel", byKind.ResolvedBy);
        Assert.Null(byKind.Status);
    }

    // Bookkeeping strings are shown only if safe and short; otherwise they are
    // treated as absent rather than cleaned.
    [Fact]
    public void AHostileOrOverlongBookkeepingStringIsTreatedAsAbsent()
    {
        var hostile = OpenClawApprovals.ParseResolved("exec.approval.resolved", Json(
            "{\"id\":\"a1\",\"reason\":\"ti\\u202Emeout\",\"status\":\"" + new string('x', 200) + "\",\"decision\":\"  \"}"))!;

        Assert.Null(hostile.Reason);
        Assert.Null(hostile.Status);
        Assert.Null(hostile.Decision);
    }

    // A terminal session event for a kind it does not name is read as exec, the
    // gateway's default, and one naming a bogus kind the same.
    [Fact]
    public void ATerminalSessionEventsKindFallsBackToExecWhenItNamesNoneOrABogusOne()
    {
        var none = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent("terminal", "{\"id\":\"a1\",\"status\":\"denied\"}")), Now)!.Terminal!;
        var bogus = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent(
            "terminal", "{\"id\":\"a1\",\"status\":\"denied\",\"presentation\":{\"kind\":\"shell\"}}")), Now)!.Terminal!;
        var plugin = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent(
            "terminal", "{\"id\":\"a1\",\"status\":\"denied\",\"presentation\":{\"kind\":\"plugin\"}}")), Now)!.Terminal!;

        Assert.Equal("exec", none.Kind);
        Assert.Equal("exec", bogus.Kind);
        Assert.Equal("plugin", plugin.Kind);
    }

    // Lone surrogates, built here because xunit's theory data cannot carry one: it
    // round-trips the string through UTF-8 and hands back a replacement character.
    [Fact]
    public void ALoneSurrogateIsNeverSafeAnywhere()
    {
        var high = ((char)0xD800).ToString();
        var low = ((char)0xDC00).ToString();

        Assert.False(OpenClawApprovals.IsDisplaySafe("a" + high));
        Assert.False(OpenClawApprovals.IsDisplaySafe(low + "a"));
        Assert.False(OpenClawApprovals.IsDisplaySafe("a" + high + "b"));
        Assert.False(OpenClawApprovals.IsDisplaySafe("a" + ((char)0xD83D).ToString()));
        Assert.False(OpenClawApprovals.IsWellFormedId("a" + high + "b"));
        Assert.False(OpenClawApprovals.IsWellFormedId("a" + low + "b"));

        // The control: a correctly paired one is fine.
        Assert.True(OpenClawApprovals.IsDisplaySafe("a😀b"));
    }

    [Fact]
    public void ALoneSurrogateInTheCommandMarksTheRequestUnsafe()
    {
        // JSON can carry a lone surrogate as an escape; the parser must refuse to show it.
        foreach (var escape in new[] { "\\ud800", "\\udc00" })
        {
            var json = Row().Replace("\"command\":\"ls -la /tmp\"", "\"command\":\"ls " + escape + " /tmp\"");

            var approval = OpenClawApprovals.ParseRecord(Json(json), "exec", Now)!;

            Assert.True(approval.Unsafe);
            Assert.Equal("", approval.Command);
        }
    }

    // JsonElement.GetString throws on a string holding an unpaired surrogate escape,
    // and a row built to make it throw would take a whole poll down. So no string
    // field of any feed may reach GetString unguarded: each is attacked here, and the
    // parse must return, with the record refused if it was the id or the session
    // and flagged unsafe if it was anything shown.
    private const string Lone = "\\ud800";

    [Fact]
    public void ALoneSurrogateEscapeInAnyStringOfARowNeverThrows()
    {
        foreach (var (field, makesNull) in new[]
                 {
                     ("\"id\":\"approval-0001\"", true),
                     ("\"approvalKind\":\"exec\"", true),
                     ("\"command\":\"ls -la /tmp\"", false),
                     ("\"cwd\":\"/tmp\"", false),
                     ("\"host\":\"gateway\"", false),
                     ("\"agentId\":\"alpha\"", false),
                     ("\"sessionKey\":\"" + Key + "\"", true),
                 })
        {
            var name = field[..field.IndexOf(':')];
            var value = field[(field.IndexOf(':') + 1)..].Trim('"');
            var poisoned = Row().Replace(field, name + ":\"x" + Lone + "\"");

            var approval = OpenClawApprovals.ParseRecord(Json(poisoned), "exec", Now);

            if (makesNull) Assert.Null(approval);
            else Assert.True(approval!.Unsafe, name);
            _ = value;
        }
    }

    [Fact]
    public void ALoneSurrogateEscapeInTheOtherFeedsNeverThrowsEither()
    {
        // argv entries, the allowed decisions, and the resolved and terminal fields.
        var argv = "{\"id\":\"a1\",\"request\":{\"commandArgv\":[\"ls\",\"x" + Lone + "\"],\"sessionKey\":\"" + Key + "\"}}";
        Assert.True(OpenClawApprovals.ParseRecord(Json(argv), "exec", Now)!.Unsafe);

        var decisions = Row().Replace("\"allowedDecisions\":[\"allow-once\",\"deny\"]", "\"allowedDecisions\":[\"x" + Lone + "\",\"deny\"]");
        Assert.Equal(new[] { "deny" }, OpenClawApprovals.ParseRecord(Json(decisions), "exec", Now)!.AllowedDecisions);

        var resolved = OpenClawApprovals.ParseResolved("exec.approval.resolved", Json(
            "{\"id\":\"a1\",\"decision\":\"x" + Lone + "\",\"reason\":\"x" + Lone + "\",\"status\":\"x" + Lone + "\"}"))!;
        Assert.Null(resolved.Decision);
        Assert.Null(resolved.Reason);
        Assert.Null(resolved.Status);

        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot(id: "x" + Lone)), Key, Now));
        Assert.Null(OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"x" + Lone + "\"}")), Key, Now));
        Assert.True(OpenClawApprovals.ParseSnapshot(Json(Snapshot(presentation:
            "{\"kind\":\"exec\",\"commandText\":\"x" + Lone + "\",\"allowedDecisions\":[\"deny\"]}")), Key, Now)!.Unsafe);

        var terminal = OpenClawApprovals.ParseSessionEvent(Json(SessionEvent(
            "terminal", "{\"id\":\"a1\",\"status\":\"denied\",\"reason\":\"x" + Lone + "\",\"resolver\":{\"id\":\"x" + Lone + "\"}}")), Now)!.Terminal!;
        Assert.Null(terminal.ResolvedBy);
        Assert.Equal("denied", terminal.Reason);

        Assert.Empty(OpenClawApprovals.ParseList(Json("[" + Row("good1") + "," +
            Row("x" + Lone) + "," + Row("good2") + "]"), "exec", Now).Where(a => a.Id.StartsWith("x")));
    }
}

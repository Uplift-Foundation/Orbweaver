using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // How a user turn left, and what to tell the person about it.
    internal enum CloudSendResult
    {
        // A 2xx. The event almost certainly landed.
        Sent,

        // Refused, or never attempted: nothing was queued anywhere and the only
        // copy of the text is the one still in the box.
        NotSent,

        // Unavailable twice. A request that timed out may well have been written,
        // so this is deliberately not "not sent".
        MaybeNotDelivered,
    }

    // Uuid is the one the event went out with, so a caller can match the echo
    // that comes back (cloud history or a local transcript row) onto its own
    // bubble. Note is the sentence for the panel when the turn did not go
    // cleanly; Outcome is the last answer the API gave, null when no request
    // was made at all.
    internal sealed record CloudSendAttempt(
        CloudSendResult Result, string Uuid, string? Note, CloudOutcome? Outcome);

    // The credential read, the POST, the one retry and the wording, shared by the
    // cloud panel and the Desktop-bridge send (CB-266) so the two cannot drift.
    // Lifted out of ClaudeCloudChatSession.SendAsync with its behaviour unchanged:
    // the session keeps what is specific to it (its read-only gate, its refusal
    // latch, its bubble and its live loop).
    internal static class CloudTurnSender
    {
        // **One retry, on Unavailable only, with the same uuid.** Measured: a
        // repeated uuid comes back `duplicate: true` and is not written twice,
        // so a send whose answer was lost can safely go again. Nothing else is
        // retried. A 401 is not, because the credential was re-read a moment
        // earlier and a second read would return the same token; a 429 is not,
        // because retrying into a rate limit is how a guest loses the invitation.
        //
        // The credential is re-read on every send and never kept, through the
        // source it was handed: a second source would be a second Keychain
        // consent surface, and would not know whose login this session needs.
        internal static async Task<CloudSendAttempt> SendUserTurnAsync(
            ICloudApi api, ICloudCredentialSource credentials, CredentialBudget readBudget,
            string sessionId, string text, Func<TimeSpan, CancellationToken, Task> delay,
            CancellationToken ct)
        {
            if (CloudRequest.CodeEventsPath(sessionId) is not { } path)
            {
                return new CloudSendAttempt(CloudSendResult.NotSent, "",
                    $"Not sent: this session's id is not in a shape {Brand.ShortName} will write to.", null);
            }

            var read = await ClaudeCliCredentials
                .ReadWithinAsync(credentials, readBudget, ct).ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                return new CloudSendAttempt(CloudSendResult.NotSent, "",
                    "Not sent: " + ClaudeCliCredentials.StatusFor(read) + ".", null);
            }

            var uuid = Guid.NewGuid().ToString();
            var context = new CloudRequestContext(token, path, HttpMethod.Post,
                ClaudeCloudSend.UserMessageBody(sessionId, text, uuid));

            var result = await api.SendAsync(context, ct).ConfigureAwait(false);

            if (result.Outcome.Kind == CloudOutcomeKind.Unavailable)
            {
                await delay(Backoff.UnavailableFloor, ct).ConfigureAwait(false);
                result = await api.SendAsync(context, ct).ConfigureAwait(false);

                if (result.Outcome.Kind == CloudOutcomeKind.Unavailable)
                {
                    return new CloudSendAttempt(CloudSendResult.MaybeNotDelivered, uuid,
                        MaybeNotDeliveredNote(result.Outcome), result.Outcome);
                }
            }

            // A 2xx whose receipt this version cannot read is still a 2xx, so the
            // receipt is not read here at all.
            return result.Outcome.Kind == CloudOutcomeKind.Ok
                ? new CloudSendAttempt(CloudSendResult.Sent, uuid, null, result.Outcome)
                : new CloudSendAttempt(CloudSendResult.NotSent, uuid, NotSentNote(result.Outcome), result.Outcome);
        }

        // One control request on the session's events channel: the credential
        // read and the POST that ClaudeCloudChatSession's interrupt used to do
        // inline, lifted out (CB-306) so the Desktop/headless bridge arm posts
        // its control requests the same way and the two cannot drift.
        //
        // Returns null on a 2xx and otherwise the clause a note can finish —
        // "the credential is missing", "the id is malformed", the API's own
        // Detail — with no retry, for the reason the interrupt had none: a
        // control request is a command about *now*, and resending one whose
        // answer was lost is not known to be harmless for every subtype.
        internal static async Task<string?> PostControlAsync(
            ICloudApi api, ICloudCredentialSource credentials, CredentialBudget readBudget,
            string sessionId, JsonObject request, CancellationToken ct) =>
            (await PostControlCoreAsync(api, credentials, readBudget, sessionId, request, ct)
                .ConfigureAwait(false)).Failure;

        private readonly record struct Posted(string? Failure, string Token, string Path, string RequestId);

        private static async Task<Posted> PostControlCoreAsync(
            ICloudApi api, ICloudCredentialSource credentials, CredentialBudget readBudget,
            string sessionId, JsonObject request, CancellationToken ct)
        {
            var read = await ClaudeCliCredentials
                .ReadWithinAsync(credentials, readBudget, ct).ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
                return new Posted(ClaudeCliCredentials.StatusFor(read), "", "", "");

            // Null for a malformed id: the roster can report a session this app
            // never sent to, so the refusal is handled rather than assumed away.
            if (CloudRequest.CodeEventsPath(sessionId) is not { } path)
                return new Posted($"this session's id is not in a shape {Brand.ShortName} will write to", "", "", "");

            var requestId = Guid.NewGuid().ToString();
            var result = await api.SendAsync(
                new CloudRequestContext(token, path, HttpMethod.Post,
                    ClaudeCloudSend.ControlRequestBody(requestId, Guid.NewGuid().ToString(), request)),
                ct).ConfigureAwait(false);

            return new Posted(result.Outcome.Kind == CloudOutcomeKind.Ok ? null : result.Outcome.Detail,
                token, path, requestId);
        }

        // --- the CLI's own answer (CB-306's product-readiness round) ---------
        //
        // A 2xx on the POST says the event was stored, not that the CLI ran
        // it: the CLI answers afterwards with a `control_response` row carrying
        // our request_id, `subtype` "success" or "error" and, for an error, its
        // own sentence. Measured 2026-11-03 against a throwaway `claude --bg
        // --remote-control` job on Claude Code 2.1.318, read back with
        // `GET /v1/code/sessions/<bridge>/events?limit=20&sort_order=desc`:
        // three answers, 160-220 ms after their requests, newest first, each
        // request_id matching. `set_color not-a-colour` answered `Unknown color
        // "not-a-colour". Available: …`; an unknown subtype answered `REPL
        // bridge does not handle control_request subtype: …`.
        //
        // So the bridge path reads the answer back, asynchronously and for up
        // to about five seconds — long enough for a session in the middle of a
        // turn, by the lead's call; the measured idle answer took a fifth of a
        // second. Confirmed is true only when the CLI answered "success". No
        // answer in that time is not a failure (the command was delivered),
        // but it is not a success either, and the caller says so.
        internal sealed record ControlAttempt(string? Failure, string? CliError, bool Confirmed = false);

        internal static readonly TimeSpan AnswerPollInterval = TimeSpan.FromMilliseconds(500);
        internal const int AnswerPolls = 10;

        // Newest first and twenty deep: the answer lands within a second of
        // the request, so it is near the top of a busy session too.
        internal static string AnswerPath(string eventsPath) => eventsPath + "?limit=20&sort_order=desc";

        internal static async Task<ControlAttempt> PostControlAndReadAnswerAsync(
            ICloudApi api, ICloudCredentialSource credentials, CredentialBudget readBudget,
            string sessionId, JsonObject request, Func<TimeSpan, CancellationToken, Task> delay,
            CancellationToken ct)
        {
            var posted = await PostControlCoreAsync(api, credentials, readBudget, sessionId, request, ct)
                .ConfigureAwait(false);
            if (posted.Failure is not null) return new ControlAttempt(posted.Failure, null);

            for (var i = 0; i < AnswerPolls; i++)
            {
                await delay(AnswerPollInterval, ct).ConfigureAwait(false);

                var page = await api.SendAsync(
                    new CloudRequestContext(posted.Token, AnswerPath(posted.Path)), ct).ConfigureAwait(false);
                if (page.Outcome.Kind != CloudOutcomeKind.Ok) break;

                if (ControlAnswerIn(page.Body, posted.RequestId) is { } answer)
                    return new ControlAttempt(null, answer.Error, Confirmed: answer.Error is null);
            }

            return new ControlAttempt(null, null);
        }

        internal readonly record struct ControlAnswer(string? Error);

        // The answer to one request in an events page, or null when the page
        // holds none (or is not a page). Error is null for "success", and the
        // CLI's own sentence for "error" — capped, since it is quoted into a
        // note, and given a stand-in when the CLI sent none.
        internal static ControlAnswer? ControlAnswerIn(string? body, string requestId)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                if (JsonNode.Parse(body) is not JsonObject root || root["data"] is not JsonArray rows) return null;
                foreach (var row in rows)
                {
                    // A row that is not an object (a null, a number) is skipped:
                    // the indexer on a non-object throws rather than answering null.
                    if (row is not JsonObject r || r["payload"] is not JsonObject payload) continue;
                    if (Text(payload["type"]) != "control_response") continue;
                    if (payload["response"] is not JsonObject response) continue;
                    if (Text(response["request_id"]) != requestId) continue;

                    if (Text(response["subtype"]) == "success") return new ControlAnswer(null);
                    var error = Text(response["error"])?.Trim();
                    if (string.IsNullOrEmpty(error)) error = "it gave no reason";
                    return new ControlAnswer(error.Length > MaxErrorLength ? error[..MaxErrorLength] + "…" : error);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
            return null;
        }

        internal const int MaxErrorLength = 300;

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // The clause a rejected command's note carries: the CLI's sentence and,
        // when the registry row says which Claude Code this is, that version
        // beside the one the control requests were measured on. This replaces
        // a startup warning, which would fire on almost every daily CLI update
        // whether or not anything had changed.
        internal static string RejectionClause(string cliError, string? version) =>
            "Claude Code refused it: " + cliError.TrimEnd('.', ' ')
            + (string.IsNullOrWhiteSpace(version)
                ? ""
                : $". This session runs Claude Code {version.Trim()}; {Brand.ShortName}'s commands were measured on "
                  + SlashRouting.MeasuredOnClaudeCode);

        internal static string AdviceFor(CloudOutcomeKind kind) => kind switch
        {
            CloudOutcomeKind.TokenRefused or CloudOutcomeKind.AuthFailed =>
                " Run `claude` in a terminal and sign in, then try again.",
            CloudOutcomeKind.RateLimited => " Wait a minute before trying again.",
            CloudOutcomeKind.TooLarge => " Try a shorter message.",
            _ => "",
        };

        internal static string NotSentNote(CloudOutcome outcome) =>
            "Not sent: " + outcome.Detail + "." + AdviceFor(outcome.Kind);

        // Not "not sent": a request that timed out may well have been written,
        // and saying otherwise invites a resend that — with a fresh uuid from
        // the box — would land twice.
        internal static string MaybeNotDeliveredNote(CloudOutcome outcome) =>
            "Your message may not have arrived (" + outcome.Detail
            + ", twice). Check the session before sending it again.";
    }
}

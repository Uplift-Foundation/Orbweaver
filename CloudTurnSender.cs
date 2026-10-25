using System;
using System.Net.Http;
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

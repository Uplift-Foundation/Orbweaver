using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    // The bodies a write into a cloud session carries, and what its answer means.
    //
    // Pure, and kept apart from the session that sends them for the reason
    // ClaudeCloudRoster is kept apart from the arm: text in, text out, so every
    // rule is testable a case at a time and the probe can build exactly the body
    // the app builds. **Nothing here touches a credential** — a body is what the
    // user typed plus ids this process minted, and the token rides only in the
    // Authorization header CloudRequest.Build sets.
    //
    // Every shape below is the one CB-199's gate sent to
    // `POST /v1/code/sessions/{id}/events` and saw accepted. They were read out
    // of the Claude Code CLI binary first and then measured, and the measurement
    // is the authority: where the two could differ, what is here is what the
    // endpoint answered 200 to.
    internal static class ClaudeCloudSend
    {
        // A user turn: `{events:[{payload:{...}}]}`, the payload a Claude Code
        // user row carrying its own uuid.
        //
        // **The uuid is what makes a retry safe.** Measured: the same uuid sent
        // twice comes back 200 both times, the second with `duplicate: true` and
        // the same `sequence_num`, so a send whose answer was lost can be sent
        // again without typing the message into the session twice. The caller
        // mints it once per message and reuses it on the retry.
        internal static string UserMessageBody(string sessionId, string text, string uuid) =>
            Envelope(new JsonObject
            {
                ["uuid"] = uuid,
                ["session_id"] = sessionId,
                ["type"] = "user",
                ["parent_tool_use_id"] = null,
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
            });

        // An interrupt: a control_request with the `interrupt` subtype.
        // `cancel_queued` is the CLI's own value and is what the gate sent.
        // Measured harmless against an idle session, which is what lets a Stop
        // press that races the end of a turn go out without a check first.
        internal static string InterruptBody(string requestId, string uuid) =>
            ControlRequestBody(requestId, uuid,
                new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true });

        // Any control request: the interrupt's envelope with the `request`
        // object supplied by the caller. CB-306 adds the other subtypes the CLI's
        // bridge handler accepts (`set_color`, `rename_session`, `set_model`,
        // `set_permission_mode`, `set_max_thinking_tokens`, read out of the
        // 2.1.317 binary's `[bridge:repl]` switch); which of those a slash
        // command maps to, and with what fields, is SlashRoute's table, so this
        // stays a plain envelope and knows no subtype by name.
        internal static string ControlRequestBody(string requestId, string uuid, JsonObject request) =>
            Envelope(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = requestId,
                ["request"] = request,
                ["uuid"] = uuid,
            });

        // Built through System.Text.Json rather than by concatenation, so a quote
        // or a newline in a message is escaped by the serialiser rather than by
        // anything here remembering to.
        private static string Envelope(JsonObject payload) =>
            new JsonObject
            {
                ["events"] = new JsonArray(new JsonObject { ["payload"] = payload }),
            }.ToJsonString();

        internal enum SendResultKind
        {
            // Accepted as a new event.
            Sent,

            // Accepted, and the endpoint already had it — the retry case.
            Duplicate,

            // A 2xx whose body this version cannot read.
            Unparseable,
        }

        // SequenceNum is carried as the string the endpoint sent. **It is a JSON
        // string in the measured response**, not a number — `"sequence_num":"20"`
        // — and nothing here does arithmetic on it, so there is nothing to gain
        // from converting it and a precision question to lose.
        internal sealed record SendResult(SendResultKind Kind, string? SequenceNum);

        // What a 2xx from the write endpoint said.
        //
        // **The app does not call this, and does not need to.** A send is Sent on
        // any 2xx — ClaudeCloudChatSession.SendAsync says why an unreadable
        // receipt must not become "not sent" — and nothing the panel shows is in
        // the receipt, so for the app there is nothing here to decide. Its only
        // callers are the unit and integration tests, which pin the measured
        // receipt shape — `duplicate: true` on a repeated uuid, the sequence
        // number as a string — through it. That is the record of what a
        // successful write answers, and it is kept for that reason. The probe
        // does not call it: it prints receipts through its own allow-listed
        // summary. If the app ever needs the sequence number, this is where it is.
        //
        // Measured shape: `{"results":[{"duplicate":false,"sequence_num":"20",
        // "event_id":<uuid>,...}]}`, one result per event sent. The app sends one
        // event per request, so the first result is the answer.
        //
        // **Unparseable is not failure, and the caller must not read it as one.**
        // The status was already 2xx by the time a body gets here, so the event
        // almost certainly landed; what is unknown is only the receipt. Saying
        // "not sent" about it would invite the user to send it again — which the
        // uuid would dedupe, but only if the retry reused it.
        internal static SendResult ParseSendResult(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return Unparseable;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("results", out var results)
                    || results.ValueKind != JsonValueKind.Array
                    || results.GetArrayLength() == 0)
                {
                    return Unparseable;
                }

                var first = results[0];
                if (first.ValueKind != JsonValueKind.Object) return Unparseable;

                var sequence = SequenceFrom(first);

                if (first.TryGetProperty("duplicate", out var duplicate)
                    && duplicate.ValueKind == JsonValueKind.True)
                {
                    return new SendResult(SendResultKind.Duplicate, sequence);
                }

                // A result with no sequence number is not a receipt for anything.
                return sequence is null
                    ? Unparseable
                    : new SendResult(SendResultKind.Sent, sequence);
            }
            catch (JsonException)
            {
                return Unparseable;
            }
        }

        private static readonly SendResult Unparseable = new(SendResultKind.Unparseable, null);

        // A string, as measured — or a number, which is what the same field would
        // most plausibly turn into if the server's serialiser changed. Both are an
        // ordering token and nothing more, so both are read; anything else is not.
        private static string? SequenceFrom(JsonElement result)
        {
            if (!result.TryGetProperty("sequence_num", out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) =>
                    value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
    }
}

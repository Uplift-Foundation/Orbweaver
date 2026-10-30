using System.Collections.Generic;

namespace Orbweaver;

// Where a reply typed into a chat panel goes, and what the two requests that
// carry it look like (CB-270).
//
// A conversation that lives on a chat surface (Discord, WhatsApp) has to be
// answered on that surface, so its reply is sent with deliver:true and the
// typed message is mirrored into the channel first. A native session has no
// surface at all: the agent answers in place, and there is nowhere to deliver
// to. For that one case the reply goes with deliver:false and no mirror.
//
// Every guard below fails toward today's payload, never away from it. The cost
// of a wrong Surface is nothing (that is what ships today); the cost of a wrong
// Native is a reply that never reaches the person it was written to, silently.
// So Native needs all three of:
//
//   - the gateway has listed the session and classified it native. Not listed,
//     or listed as anything else, is Surface;
//   - it has never been seen as a surface conversation in this process, so a
//     session that was a Discord DM a poll ago cannot flip to Native on a
//     transient classification;
//   - it has no delivery address. Any known address means there is somewhere
//     to deliver to.
//
// Deliberately not keyed on "Delivery is null" alone. A Discord DM that
// momentarily lacks a delivery context is still classified as a direct
// conversation, so its route is Surface and its payload is today's.
internal enum OpenClawSendRoute { Surface, Native }

internal static class OpenClawSendPolicy
{
    internal static OpenClawSendRoute RouteFor(
        OpenClawNativeKind? native, bool seenAsSurface, OpenClawSessions.Delivery? delivery) =>
        native is OpenClawNativeKind.Main or OpenClawNativeKind.Dashboard
            && !seenAsSurface
            && delivery is null
            ? OpenClawSendRoute.Native
            : OpenClawSendRoute.Surface;

    // The channel post that puts the typed message above the agent's answer.
    // Insertion order is today's and is pinned by tests. The prefix is the same
    // constant the recognizer reads back, not a second literal saying the same
    // thing, which is how a mirror once silently stopped being recognisable as
    // ours.
    internal static Dictionary<string, object> MirrorRequest(
        OpenClawSessions.Delivery delivery, string text, string idempotencyKey)
    {
        var mirror = new Dictionary<string, object>
        {
            ["to"] = delivery.To,
            ["message"] = OpenClawSender.MirrorPrefix + text,
            ["channel"] = delivery.Channel,
            ["idempotencyKey"] = idempotencyKey
        };

        if (!string.IsNullOrWhiteSpace(delivery.AccountId))
        {
            mirror["accountId"] = delivery.AccountId!;
        }

        return mirror;
    }

    // deliver is the whole difference between the routes.
    //
    // true: without it the gateway routes the reply to its internal channel, so
    // the agent answers and the transcript records it and nothing ever reaches
    // Discord. Its own routing reads
    //
    //   if (!(params.deliver === true)) return { originatingChannel: INTERNAL_MESSAGE_CHANNEL, ... }
    //   const sessionDeliveryContext = deliveryContextFromSession(entry)
    //
    // so true is what makes it look up where the conversation actually lives and
    // deliver there. A reply typed into a Discord conversation is a reply *to*
    // that conversation; anything else would be a message that looked sent and
    // wasn't.
    //
    // false, for a native session: nothing to deliver to. Measured on 2026.10.5
    // to complete normally, with the gateway still stamping its ":user"
    // idempotency suffix on the user's turn; see the CB-270 findings.
    //
    // The key comes in from the call site so production keeps a bare
    // Guid.ToString() and tests can hand in a fixed one. A fresh id per send
    // means a retry after a timeout can't post the message twice.
    //
    // timeoutMs (Stage 4) is the person's own limit on how long the agent may run
    // for this send, and is appended after idempotencyKey only when one was
    // chosen: with none the request is byte-for-byte what it always was. The
    // gateway reads 0 as "no timeout at all" and a negative value as "use the
    // default", so neither is ever sent: this refuses them rather than trust
    // every caller, since "no limit" is never something a time-limit choice here
    // is allowed to mean.
    internal static Dictionary<string, object> ChatSendRequest(
        string sessionKey, string text, OpenClawSendRoute route, string idempotencyKey,
        int? timeoutMs = null, IReadOnlyList<OpenClawOutgoingImage>? attachments = null)
    {
        if (timeoutMs is <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "must be positive");

        var request = new Dictionary<string, object>
        {
            ["sessionKey"] = sessionKey,
            ["message"] = text,
            ["deliver"] = route == OpenClawSendRoute.Surface,
            ["idempotencyKey"] = idempotencyKey
        };

        if (timeoutMs is { } limit) request["timeoutMs"] = limit;

        // Pictures ride last, as the wire shape the probe measured, and only when
        // there are any: an empty list is no attachments, not an empty field.
        if (attachments is { Count: > 0 }) request["attachments"] = attachments.Select(a => a.ToWire()).ToList();

        return request;
    }
}

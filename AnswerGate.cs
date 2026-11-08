using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // A prompt the desktop sent to the phones and has not yet seen settled.
    // It deliberately does not keep the options it was minted with: the gate
    // presses only from a fresh parse (rule 7), and a list that is not here
    // cannot be pressed from by mistake. Answered is set
    // by the caller *before* the keypress (rule 8), so a second answer racing
    // the first finds it settled and is refused not_pending.
    internal sealed class PendingPrompt
    {
        internal PendingPrompt(string requestId, string sessionId, string kind, string screenHash, long expiresAt, bool deskOnly = false)
        {
            DeskOnly = deskOnly;
            RequestId = requestId;
            SessionId = sessionId;
            Kind = kind;
            ScreenHash = screenHash;
            ExpiresAt = expiresAt;
        }

        internal string RequestId { get; }
        internal string SessionId { get; }
        internal string Kind { get; }
        internal string ScreenHash { get; }
        internal long ExpiresAt { get; }
        internal bool Answered { get; set; }

        // A first-run screen (trust, update, hooks review: FirstRunDialogs) sent
        // as "Open on desk" with no options. Nothing about it is pressable from
        // a phone, whatever an answer names.
        internal bool DeskOnly { get; }
    }

    // The fields of a sealed answer frame (frames-v3 sealed_plaintext.answer),
    // exactly as they arrived.
    internal sealed record PhoneAnswer(string RequestId, string SessionId, string Action, string Scope, string Note,
        string Nonce, long ExpiresAt, string ScreenHash, string Sig);

    // Nonces seen per phone in the last hour (rule 4). Time is passed in, never
    // read, so the gate stays a pure function of its inputs and a test can walk
    // the clock across the hour boundary.
    internal sealed class NonceWindow
    {
        internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
        private readonly Dictionary<(string Phone, string Nonce), DateTimeOffset> _seen = new();

        internal bool Seen(string phoneId, string nonce, DateTimeOffset now)
        {
            Prune(now);
            return _seen.ContainsKey((phoneId, nonce));
        }

        internal void Add(string phoneId, string nonce, DateTimeOffset now) => _seen[(phoneId, nonce)] = now;

        internal int Count => _seen.Count;

        private void Prune(DateTimeOffset now)
        {
            // Dictionary allows Remove during enumeration (since .NET Core 3.0).
            foreach (var kv in _seen)
                if (now - kv.Value >= Lifetime) _seen.Remove(kv.Key);
        }
    }

    // CB-315 §4: the eight-rule gate between a phone's answer and a keypress.
    // The rules run in exactly this order and the first that fails decides the
    // refusal, so a QA control that breaks one thing hits one rule:
    //   1. the signature verifies over the canonical body (and a tell's scope is
    //      the hash of its note)                               bad_signature
    //   2. a prompt is pending for that request and session, and not answered
    //                                                          not_pending
    //   3. not expired, and expires_at equals the pending one  expired
    //   4. the nonce is unseen for this phone in the last hour replayed
    //   5. the echoed screen_hash equals the pending one       stale_prompt
    //   6. a FRESH capture hashes to the pending one           stale_prompt
    //   7. the action names exactly one option of the fresh parse, it is not
    //      mode, and an always's scope matches                 forbidden_option
    //   8. press — the caller marks the prompt answered first, presses once,
    //      recaptures, and AfterPress says whether it took
    // A wrong keypress must be impossible, so every path that is not rule 8's is
    // a refusal, and rule 8 only ever names a key read off the fresh screen.
    internal static class AnswerGate
    {
        internal const string StalePrompt = "stale_prompt", BadSignature = "bad_signature", ForbiddenOption = "forbidden_option",
            NotPending = "not_pending", Expired = "expired", Replayed = "replayed";

        // frames-v3 sealed_plaintext.err.codes, in that file's order.
        internal static readonly string[] Codes = [StalePrompt, BadSignature, ForbiddenOption, NotPending, Expired, Replayed];

        internal const int NonceLength = 16;

        // Rule is the number of the rule that decided (1–7 for a refusal, 8 for
        // a press); Key is set only for a press.
        internal sealed record Decision(int Rule, string? Code, string? Key)
        {
            internal bool Press => Key is not null;
        }

        private static Decision Refuse(int rule, string code) => new(rule, code, null);

        // pending: the prompt the answer's request_id names, or null when none
        // is pending. phoneIdPublic: the answering phone's pairing id_pub.
        // freshCapture: the screen read just now, after rules 1–5 passed and
        // before this call returns — it is the caller's job to read it fresh,
        // never from a cache (IScreenReader promises that).
        internal static Decision Decide(PendingPrompt? pending, PhoneAnswer answer, string? freshCapture,
            string phoneId, byte[] phoneIdPublic, DateTimeOffset now, NonceWindow nonces)
        {
            // 1. signature
            var body = AnswerSigning.Body(answer.RequestId, answer.SessionId, answer.Action, answer.Scope, answer.Nonce, answer.ExpiresAt);
            if (!AnswerSigning.Verify(phoneIdPublic, body, answer.Sig)) return Refuse(1, BadSignature);
            if (answer.Action == "tell" && answer.Scope != AnswerSigning.TellScope(answer.Note)) return Refuse(1, BadSignature);
            if (answer.Action != "tell" && answer.Note.Length > 0) return Refuse(1, BadSignature);
            if (RelayCrypto.FromBase64Url(answer.Nonce) is not { Length: NonceLength }) return Refuse(1, BadSignature);

            // 2. pending, for that session, not answered
            if (pending is null || pending.RequestId != answer.RequestId || pending.SessionId != answer.SessionId || pending.Answered)
                return Refuse(2, NotPending);

            // 3. expiry
            if (answer.ExpiresAt != pending.ExpiresAt || now.ToUnixTimeSeconds() >= pending.ExpiresAt) return Refuse(3, Expired);

            // 4. nonce — recorded the moment it passes, so a replay of an answer
            // refused further down is still a replay.
            if (nonces.Seen(phoneId, answer.Nonce, now)) return Refuse(4, Replayed);
            nonces.Add(phoneId, answer.Nonce, now);

            // 5. echoed hash
            if (answer.ScreenHash != pending.ScreenHash) return Refuse(5, StalePrompt);

            // 6. fresh capture
            var fresh = PromptFingerprint.Of(freshCapture);
            if (fresh is null || fresh.Hash != pending.ScreenHash) return Refuse(6, StalePrompt);

            // 7. option, off the fresh parse — never on a first-run screen,
            // whether the prompt was minted desk-only or the screen became one.
            if (pending.DeskOnly || FirstRunDialogs.Is(freshCapture)) return Refuse(7, ForbiddenOption);
            var key = KeyFor(answer, fresh, pending.Kind);
            if (key is null) return Refuse(7, ForbiddenOption);

            // 8. press
            return new Decision(8, null, key);
        }

        // The one key the action names on the fresh screen, or null. Mode is
        // never pressable; never is not an action a phone can send; an action
        // that matches two options is ambiguous and refused rather than guessed.
        internal static string? KeyFor(PhoneAnswer answer, PromptFingerprint fresh, string kind)
        {
            var options = fresh.Prompt.Options.Select(o =>
            {
                var c = OptionKinds.Classify(kind, o.Label, o.Mode);
                return new PromptOption(o.Key, o.Label, c.Kind, c.Scope, o.ScopeTruncated);
            }).ToList()
                // A cut scope is never pressable: the phone could not show what
                // it would grant (lead's rule; the parser's flag, #228).
                .Where(o => !o.ScopeTruncated).ToList();

            IEnumerable<PromptOption> matches;
            if (answer.Action.StartsWith("option:", StringComparison.Ordinal))
            {
                var k = answer.Action["option:".Length..];
                matches = options.Where(o => o.Key == k && o.Kind == OptionKinds.Option);
            }
            else
            {
                matches = answer.Action switch
                {
                    "approve" => options.Where(o => o.Kind == OptionKinds.Approve),
                    "deny" => options.Where(o => o.Kind == OptionKinds.Deny),
                    "tell" => options.Where(o => o.Kind == OptionKinds.Tell),
                    "always" => options.Where(o => o.Kind == OptionKinds.Always && o.Scope == answer.Scope),
                    _ => [],
                };
            }

            var list = matches.ToList();
            return list.Count == 1 ? list[0].Key : null;
        }

        // Rule 8's second half: the recapture after the one keypress. If the
        // same dialog is still there the press did nothing, and the answer is
        // refused stale_prompt with no retry — a second press could land on
        // whatever the first one eventually opens.
        internal static bool PressTookEffect(PendingPrompt pending, string? recapture) =>
            PromptFingerprint.Of(recapture)?.Hash != pending.ScreenHash;
    }
}

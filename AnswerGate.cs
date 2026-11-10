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
        internal PendingPrompt(string requestId, string sessionId, string kind, string screenHash, long expiresAt, bool deskOnly = false,
            IReadOnlyList<string>? keys = null, bool multi = false, bool text = false)
        {
            DeskOnly = deskOnly;
            Keys = keys;
            Multi = multi;
            Text = text;
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

        // The only keys an answer may press, when the prompt was sent with a
        // chosen subset of the screen's options (a question: its own choices,
        // never "Type something." or "Chat about this", whose digit declines
        // the form). Null: any option the fresh parse allows.
        internal IReadOnlyList<string>? Keys { get; }

        // CB-323 frames-v7-questions: the flags the prompt went out with. Multi:
        // a multi-select question, answered only by options:<keys>. Text: a
        // single-select question with a "Type something." row, which also
        // takes other (typed text).
        internal bool Multi { get; }
        internal bool Text { get; }

        // A first-run screen (trust, update, hooks review: FirstRunDialogs) sent
        // as "Open on desk" with no options. Nothing about it is pressable from
        // a phone, whatever an answer names.
        internal bool DeskOnly { get; }
    }

    // The fields of a sealed answer frame (frames-v3 sealed_plaintext.answer),
    // exactly as they arrived.
    //
    // CB-323 watch-key addendum: Key is the optional `key` beside the signed
    // body — null when absent (the approval key signed it, frames-v3 exactly),
    // "watch" when the watch key did. Any other value, a non-string included,
    // arrives as itself or "" and fails rule 1.
    internal sealed record PhoneAnswer(string RequestId, string SessionId, string Action, string Scope, string Note,
        string Nonce, long ExpiresAt, string ScreenHash, string Sig, string? Key = null);

    // Nonces seen per phone in the last hour (rule 4). Time is passed in, never
    // read, so the gate stays a pure function of its inputs and a test can walk
    // the clock across the hour boundary.
    internal sealed class NonceWindow
    {
        internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
        private readonly Dictionary<(string Phone, string Nonce), DateTimeOffset> _seen = new();

        // Locked since CB-316: answers come off the prompt pipeline and
        // launches off the relay's request path, which run concurrently.
        internal bool Seen(string phoneId, string nonce, DateTimeOffset now)
        {
            lock (_seen)
            {
                Prune(now);
                return _seen.ContainsKey((phoneId, nonce));
            }
        }

        internal void Add(string phoneId, string nonce, DateTimeOffset now)
        {
            lock (_seen) _seen[(phoneId, nonce)] = now;
        }

        // Seen-then-add as one step, so two launches carrying one nonce cannot
        // both pass between the check and the record.
        internal bool TryAdd(string phoneId, string nonce, DateTimeOffset now)
        {
            lock (_seen)
            {
                Prune(now);
                return _seen.TryAdd((phoneId, nonce), now);
            }
        }

        internal int Count { get { lock (_seen) return _seen.Count; } }

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
    //      the hash of its note), under the key the answer names: the pair's
    //      id_pub when it names none, the stored watch key for key:"watch"
    //      (CB-323), and nothing at all for any other name     bad_signature
    //  1w. a watch-signed answer only approves or denies — "Always allow",
    //      tell and a question's option stay Face ID only (CB-323)
    //                                                          forbidden_option
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
        // a press). A press names what to press: Key alone (one option); Keys,
        // the boxes a multi-select question is to end up with ticked
        // (options:); or Text with Key the "Type something." row (other).
        internal sealed record Decision(int Rule, string? Code, string? Key, IReadOnlyList<string>? Keys = null, string? Text = null)
        {
            internal bool Press => Code is null;
        }

        internal const string Other = "other";
        internal const string OptionsPrefix = "options:";
        internal const int MaxNoteBytes = 1000;
        private static readonly System.Text.RegularExpressions.Regex OptionsShape =
            new("^options:[1-9][0-9]*(,[1-9][0-9]*)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static Decision Refuse(int rule, string code) => new(rule, code, null);

        // pending: the prompt the answer's request_id names, or null when none
        // is pending. phoneIdPublic: the answering phone's pairing id_pub.
        // freshCapture: the screen read just now, after rules 1–5 passed and
        // before this call returns — it is the caller's job to read it fresh,
        // never from a cache (IScreenReader promises that).
        // watchPublic: that phone's watch key as this desktop last accepted it,
        // or null when it holds none (CB-323).
        internal static Decision Decide(PendingPrompt? pending, PhoneAnswer answer, string? freshCapture,
            string phoneId, byte[] phoneIdPublic, DateTimeOffset now, NonceWindow nonces, byte[]? watchPublic = null)
        {
            // 1. signature, under the key the answer names
            var verifier = answer.Key switch
            {
                null => phoneIdPublic,
                RelayWatchKey.Kind => watchPublic,
                _ => null,
            };
            var body = AnswerSigning.Body(answer.RequestId, answer.SessionId, answer.Action, answer.Scope, answer.Nonce, answer.ExpiresAt);
            if (verifier is null || !AnswerSigning.Verify(verifier, body, answer.Sig)) return Refuse(1, BadSignature);
            // tell and other (CB-323) bind their note through scope; every other
            // action carries none.
            var noted = answer.Action is "tell" or Other;
            if (noted && answer.Scope != AnswerSigning.TellScope(answer.Note)) return Refuse(1, BadSignature);
            if (!noted && answer.Note.Length > 0) return Refuse(1, BadSignature);
            if (RelayCrypto.FromBase64Url(answer.Nonce) is not { Length: NonceLength }) return Refuse(1, BadSignature);

            // 1w. the watch key approves or denies, nothing else. Reported as
            // rule 1: it is decided with the signature, before anything looks
            // at what is pending.
            if (answer.Key is not null && answer.Action is not ("approve" or "deny")) return Refuse(1, ForbiddenOption);

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
            var form = ChatTranscript.ParseAskForm(freshCapture!);
            var typeRow = form is { MultiSelect: false } ? form.FreeTextKey : null;
            return Rule7(pending, answer, Classified(fresh, pending.Kind), typeRow);
        }

        // The fresh screen's options as the gate weighs them: classified for
        // the prompt's kind, and never one whose scope was cut at the screen's
        // edge (the phone could not show what it would grant; #228).
        internal static IReadOnlyList<PromptOption> Classified(PromptFingerprint fresh, string kind) =>
            fresh.Prompt.Options.Select(o =>
            {
                var c = OptionKinds.Classify(kind, o.Label, o.Mode);
                return new PromptOption(o.Key, o.Label, c.Kind, c.Scope, o.ScopeTruncated);
            }).Where(o => !o.ScopeTruncated).ToList();

        // Rule 7, pure: what the answer may press on the fresh screen, given
        // what the prompt offered. typeRow is the "Type something." row's key
        // on a single-select question, or null when the screen has none.
        // frames-v7-questions: options:<keys> only on a multi:true prompt, in
        // its exact grammar (ascending as integers, distinct, no spaces), every
        // key a kind:option choice on the screen and among what was offered;
        // other only on a text:true prompt whose screen still shows the row,
        // with a one-line note of 1–1000 bytes; and a single option:<k> is not
        // how a multi-select question is answered.
        internal static Decision Rule7(PendingPrompt pending, PhoneAnswer answer, IReadOnlyList<PromptOption> fresh, string? typeRow)
        {
            if (answer.Action.StartsWith(OptionsPrefix, StringComparison.Ordinal))
            {
                if (!pending.Multi || OptionKeys(answer.Action) is not { } keys) return Refuse(7, ForbiddenOption);
                foreach (var k in keys)
                    if (!fresh.Any(o => o.Key == k && o.Kind == OptionKinds.Option) || (pending.Keys is { } offered && !offered.Contains(k)))
                        return Refuse(7, ForbiddenOption);
                return new Decision(8, null, null, keys);
            }

            if (answer.Action == Other)
            {
                if (!pending.Text || pending.Multi || typeRow is null || !NoteAllowed(answer.Note)) return Refuse(7, ForbiddenOption);
                return new Decision(8, null, typeRow, Text: answer.Note);
            }

            if (pending.Multi) return Refuse(7, ForbiddenOption);
            var key = KeyFor(answer, fresh);
            if (key is null || (pending.Keys is { } allowed && !allowed.Contains(key))) return Refuse(7, ForbiddenOption);

            // 8. press
            return new Decision(8, null, key);
        }

        // options:<k1>,<k2>,… as its keys, or null when it is not exactly the
        // grammar: digits with no leading zero, joined by "," alone, strictly
        // ascending as integers (so distinct, and 2,10,11 rather than 10,2).
        internal static IReadOnlyList<string>? OptionKeys(string action)
        {
            if (!OptionsShape.IsMatch(action)) return null;
            var keys = action[OptionsPrefix.Length..].Split(',');
            for (var i = 1; i < keys.Length; i++)
                if (!(keys[i].Length > keys[i - 1].Length || (keys[i].Length == keys[i - 1].Length && string.CompareOrdinal(keys[i], keys[i - 1]) > 0)))
                    return null;
            return keys;
        }

        // other's note: one line of 1–1000 UTF-8 bytes, with no C0 control, no
        // DEL and no C1 control — so no newline, carriage return or tab.
        internal static bool NoteAllowed(string note) =>
            note.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(note) <= MaxNoteBytes
            && !note.Any(c => c < 0x20 || c is >= '\u007f' and <= '\u009f');

        // The one key the action names on the fresh screen, or null. Mode is
        // never pressable; never is not an action a phone can send; an action
        // that matches two options is ambiguous and refused rather than guessed.
        internal static string? KeyFor(PhoneAnswer answer, IReadOnlyList<PromptOption> options)
        {
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

namespace Orbweaver
{
    // CB-315 §4: the eight-rule gate between a phone's answer and a keypress.
    internal static class AnswerGate
    {
        internal const string StalePrompt = "stale_prompt", BadSignature = "bad_signature", ForbiddenOption = "forbidden_option",
            NotPending = "not_pending", Expired = "expired", Replayed = "replayed";

        // frames-v3 sealed_plaintext.err.codes, in that file's order.
        internal static readonly string[] Codes = [StalePrompt, BadSignature, ForbiddenOption, NotPending, Expired, Replayed];
    }
}

namespace Orbweaver
{
    // Every string the run-controls chip and flyout show (CB-270 Stage 4), in
    // one place so the wording is a one-line change and a test can pin it.
    internal static class OpenClawRunText
    {
        internal const string ModelLabel = "Model";
        internal const string ThinkingLabel = "Thinking";
        internal const string FastLabel = "Fast mode";
        internal const string TimeLimitLabel = "Time limit";
        internal const string DefaultLabel = "Default";

        internal const string LockedModelTip =
            "The model can't be changed for this conversation while it is running on its current one.";

        // Native mains are included (S1's fork 3), and an agent's main session is
        // where its scheduled reminders and heartbeats run, so the model chosen
        // here is theirs too. A person should not find that out afterwards.
        internal const string ChipTip =
            "Model, thinking and fast mode for this conversation. On an agent's main session this "
            + "also changes the model its scheduled reminders and heartbeats run on.";

        // The minutes a person may pick. Deliberately no "no limit": the gateway
        // reads a timeout of 0 as none at all.
        internal static readonly int[] TimeLimitMinutes = { 2, 10, 30, 60 };

        internal static string TimeLimitText(int? minutes) =>
            minutes is { } m ? $"{m} minutes" : DefaultLabel;

        // "<model name> · <thinking label> · fast", the thinking part only when
        // there is a level to name and the fast part only when fast is on.
        internal static string Chip(string modelName, string? thinkingLabel, bool fast)
        {
            var parts = new List<string> { modelName };
            if (!string.IsNullOrEmpty(thinkingLabel)) parts.Add(thinkingLabel);
            if (fast) parts.Add("fast");

            return string.Join(" · ", parts);
        }
    }
}

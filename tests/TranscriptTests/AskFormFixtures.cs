namespace Orbweaver.Tests;

// Real `tmux capture-pane -p` output of Claude Code 2.1.319 AskUserQuestion
// forms (CB-307 item 3), captured 2026-11-07 from a throwaway session on the
// mini's own qa307 tmux socket: a two-question form, Q1 "Pick a color" (single
// select) and Q2 "Pick toppings" (multiSelect). The content is the session's own
// invented wording, so there is nothing to redact. Verbatim, trailing blank
// lines trimmed.
internal static class AskFormFixtures
{
    // The multiSelect question, nothing checked: [ ] rows, an unnumbered Submit row, and Chat about this.
    internal const string MultiSelectQ2 = """""

 ▐▛███▛█   Claude Code v2.1.319
▝▜██████▀  Opus 5.5 with medium effort · Claude Team
 ▝▝   ▝▝   /private/tmp/qa307m · /rc


❯ Use the AskUserQuestion tool right now in ONE call with exactly two questions. Q1: question "Pick a color", header
  "Color", single select, options Red and Blue (with short descriptions). Q2: question "Pick toppings", header
  "Toppings", multiSelect true, options Cheese, Ham, Olives (with short descriptions). Do nothing else until I answer.

⏺ User declined to answer questions
  ⎿  · Pick a color (Red / Blue)
     · Pick toppings (Cheese / Ham / Olives)

✻ Cooked for 2s · done 11:42 AM

❯ Ask the same two questions again with the AskUserQuestion tool, one call. Do nothing else until I answer.
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
←  ☒ Color  ☐ Toppings  ✔ Submit  →

Pick toppings

❯ 1. [ ] Cheese
         Classic melted cheese
  2. [ ] Ham
         Savory sliced ham
  3. [ ] Olives
         Briny sliced olives
  4. [ ] Type something
     Submit
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  5. Chat about this

Enter to select · Tab/Arrow keys to navigate · Esc to cancel
""""";

    // The same question with Cheese and Ham checked ([✔]).
    internal const string MultiSelectTwoChecked = """""

 ▐▛███▛█   Claude Code v2.1.319
▝▜██████▀  Opus 5.5 with medium effort · Claude Team
 ▝▝   ▝▝   /private/tmp/qa307m · /rc


❯ Use the AskUserQuestion tool right now in ONE call with exactly two questions. Q1: question "Pick a color", header
  "Color", single select, options Red and Blue (with short descriptions). Q2: question "Pick toppings", header
  "Toppings", multiSelect true, options Cheese, Ham, Olives (with short descriptions). Do nothing else until I answer.

⏺ User declined to answer questions
  ⎿  · Pick a color (Red / Blue)
     · Pick toppings (Cheese / Ham / Olives)

✻ Cooked for 2s · done 11:42 AM

❯ Ask the same two questions again with the AskUserQuestion tool, one call. Do nothing else until I answer.
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
←  ☒ Color  ☒ Toppings  ✔ Submit  →

Pick toppings

  1. [✔] Cheese
         Classic melted cheese
❯ 2. [✔] Ham
         Savory sliced ham
  3. [ ] Olives
         Briny sliced olives
  4. [ ] Type something
     Submit
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  5. Chat about this

Enter to select · Tab/Arrow keys to navigate · Esc to cancel
""""";

    // The Submit tab: "Review your answers", 1. Submit answers, 2. Cancel.
    internal const string SubmitTab = """""

 ▐▛███▛█   Claude Code v2.1.319
▝▜██████▀  Opus 5.5 with medium effort · Claude Team
 ▝▝   ▝▝   /private/tmp/qa307m · /rc


❯ Use the AskUserQuestion tool right now in ONE call with exactly two questions. Q1: question "Pick a color", header
  "Color", single select, options Red and Blue (with short descriptions). Q2: question "Pick toppings", header
  "Toppings", multiSelect true, options Cheese, Ham, Olives (with short descriptions). Do nothing else until I answer.

⏺ User declined to answer questions
  ⎿  · Pick a color (Red / Blue)
     · Pick toppings (Cheese / Ham / Olives)

✻ Cooked for 2s · done 11:42 AM

❯ Ask the same two questions again with the AskUserQuestion tool, one call. Do nothing else until I answer.

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
←  ☒ Color  ☒ Toppings  ✔ Submit  →

Review your answers

 ● Pick a color
   → my own answer
 ● Pick toppings
   → Cheese, Ham, Olives

Ready to submit your answers?

❯ 1. Submit answers
  2. Cancel
""""";

    // Q1 with the free-text row filled in: the label is replaced by the typed text.
    internal const string FreeTextTyped = """""

 ▐▛███▛█   Claude Code v2.1.319
▝▜██████▀  Opus 5.5 with medium effort · Claude Team
 ▝▝   ▝▝   /private/tmp/qa307m · /rc


❯ Use the AskUserQuestion tool right now in ONE call with exactly two questions. Q1: question "Pick a color", header
  "Color", single select, options Red and Blue (with short descriptions). Q2: question "Pick toppings", header
  "Toppings", multiSelect true, options Cheese, Ham, Olives (with short descriptions). Do nothing else until I answer.

⏺ User declined to answer questions
  ⎿  · Pick a color (Red / Blue)
     · Pick toppings (Cheese / Ham / Olives)

✻ Cooked for 2s · done 11:42 AM

❯ Ask the same two questions again with the AskUserQuestion tool, one call. Do nothing else until I answer.
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
←  ☐ Color  ☐ Toppings  ✔ Submit  →

Pick a color

  1. Red
     A warm, bold color
  2. Blue
     A cool, calm color
❯ 3. my own answer
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  4. Chat about this

Enter to select · Tab/Arrow keys to navigate · ctrl+g to edit in Vim · Esc to cancel
""""";

    // Q1, single select, as the form first shows it.
    internal const string SingleSelectQ1 = """""

 ▐▛███▛█   Claude Code v2.1.319
▝▜██████▀  Opus 5.5 with medium effort · Claude Team
 ▝▝   ▝▝   /private/tmp/qa307m · /rc


❯ Use the AskUserQuestion tool right now in ONE call with exactly two questions. Q1: question "Pick a color", header
  "Color", single select, options Red and Blue (with short descriptions). Q2: question "Pick toppings", header
  "Toppings", multiSelect true, options Cheese, Ham, Olives (with short descriptions). Do nothing else until I answer.

⏺ User declined to answer questions
  ⎿  · Pick a color (Red / Blue)
     · Pick toppings (Cheese / Ham / Olives)

✻ Cooked for 2s · done 11:42 AM

❯ Ask the same two questions again with the AskUserQuestion tool, one call. Do nothing else until I answer.
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
←  ☐ Color  ☐ Toppings  ✔ Submit  →

Pick a color

❯ 1. Red
     A warm, bold color
  2. Blue
     A cool, calm color
  3. Type something.
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  4. Chat about this

Enter to select · Tab/Arrow keys to navigate · Esc to cancel
""""";
}

// Real `tmux capture-pane -p` output of Claude Code 2.1.326 with the jira-board mod pane docked
// to the right of the chat, captured 2026-11-17 for CB-353. A mod pane is drawn on every row as
// the chat, a gap, a `│` divider and the panel's own text, which is what PaneColumns strips.
//
// **Scrubbed before committing, in the way PaneFixtures is:** everything right of the divider (a
// private backlog's titles, in the originals) has every letter replaced by `x` and every digit by
// `0`, and on the two captures that held a person's name and repository security settings in the
// chat text the prose is masked the same way. Same length throughout, so every row keeps its width
// and wrapping, which is all a screen parser can see; the structure a parser reads (tabs, option
// numbers, ticks, the Submit row, hints, rules and boxes) is untouched.

using System.Linq;

namespace Orbweaver.Tests
{
    internal static class SidePanelFixtures
    {
        private static string Lf(string text) => text.Replace("\r\n", "\n");

        // Cuts each row at its last divider at or beyond column 20 and trims, which is the
        // side-panel strip done by hand: the twin every fixture's parse must equal.
        internal static string HandCut(string screen) =>
            string.Join("\n", screen.Split('\n').Select(row =>
            {
                var bar = row.LastIndexOf('│');
                return bar >= 20 ? row[..bar].TrimEnd() : row;
            }));

        internal static (string Name, string Screen)[] All =>
        [
            ("FormTwoTabSingle", FormTwoTabSingle),
            ("FormTwoTabMulti", FormTwoTabMulti),
            ("FormFramedWrapped", FormFramedWrapped),
            ("PermissionBoard", PermissionBoard),
            ("IdleBoard", IdleBoard),
            ("WideBoard", WideBoard),
            ("TypedFreeText", TypedFreeText),
            ("ReviewTab", ReviewTab),
        ];

        // Two tabs, the first (single-select) open, with the Submit tab to its right. Source r4-form-q1.
        internal static readonly string FormTwoTabSingle = Lf("""""
                                                                                                                       │                                                                                             x
 ▐▛███▛█   Claude Code v2.1.326                                                                                        │xx xxxxx                                                           00 xxxxxx x xxxxxxx xxxx xxx
▝▜██████▀  Haiku 5.5 · Claude Team                                                                                     │[ xxxxxxx ] [ xxxx ] xxxx xx xxxx x
 ▝▝   ▝▝   /…/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/scratchpad/live/work · /rc                                          │
                                                                                                                       │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
                                                                                                                       │
❯ /jira                                                                                                                │xx-000 "xxxx xxxx xxx" xx xxx xxxxx-xxxxx xxxx, xxxx x xxxxxx xxxx xxxx xx xxxxxxxx xx xxxxxxxx
  ⎿  jira-board: CB board open.                                                                                        │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ Reply with exactly this one line and nothing else: 日本語のテスト 🤖 emoji ✅ done                                   │xx-000 xxx x xxxx xxxx xx xxx xxxxxxxxx xxxxxxx xxxxxxxx xxxxxx (xxxxxxx, xxxxxxxxxxxxx)
                                                                                                                       │xxxx x = xxxxxx x x x 0x
⏺ 日本語のテスト 🤖 emoji ✅ done                                                                                      │
                                                                                                                       │xx-000 xxxxxx: xxx xxxxx'x xxxxxxx xxxxxxxxxx xxxxx xxx xxxxxxxx (xxxxxxxxx, xxxxxxxxx, xxxxxxx
✻ Sautéed for 1s · done 12:38 PM                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
❯ Run this bash command now: touch probe-file.txt                                                                      │xx-000 xxx xxxxx'x xxxxxxx xxx xxxxx xxx xxxx xxxxx x xxxxxxx xx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 00x
  Did not run 1 call                                                                                                   │
  ⎿  $ touch probe-file.txt && ls -l probe-file.txt                                                                    │xx-000 x xxxxxx xxxx xxxx xxxxx xx "xxx xx xxx xxxxxx", xx xxx xxxxx xxx'x xxxx xxxx xx xxxxxxx
  ⎿  Interrupted · What should Claude do instead?                                                                      │xxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ Call AskUserQuestion now with two questions: (1) header Venue, single select, question 'Where should the offsite be  │xx-000 xxxx xxxxx xx xxxx xxxxxxxx xxxxxxxxx (xxxxxxxx xxxxx xxx xxxx xxxx), xxxxxxx xxxx xxxxx
  held?', options Lakeside cabin, City hotel, Stay remote; (2) header Catering, multiSelect true, question 'Which      │xxxxxxx x = xxxxxx x x x 0x
  catering options should be booked?', options Breakfast, Lunch, Dinner, None of these.                                │
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xx-000 xxxxxxxxxxxxxxxxx.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxxxx xxxxxxxxx xxxx
←  ☐ Venue  ☐ Catering  ✔ Submit  →                                                                                    │xxx x = xxxxxx x x x 0x
                                                                                                                       │
Where should the offsite be held?                                                                                      │xx-000 xx-000: xxxxx xxxxxxx xx x xxxxx xxxxxxx, xxx xxxx xxxx xxxxx xxxxx xxxxx xxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
❯ 1. Lakeside cabin                                                                                                    │
     A cabin rental by a lake for the group.                                                                           │xx-000 xx-xxxx xx xxxxxxxx xxxxxxxx xxxxxxxxxxxx xxxx xxx xxx xxxx xxxxxx, xxx xxxx xxxxx x xxx
  2. City hotel                                                                                                        │xxxxxxx x = xxxxxx x x x 0x
     A hotel in the city, close to transit and amenities.                                                              │
  3. Stay remote                                                                                                       │xx-000 xxxxx xxxx xxxxxxxxxxxxx xx xxxxxxxxx (xxxxxxxxx, xxx, xxxxxxx, xxxxxxx)
     Hold the offsite remotely with no travel.                                                                         │xxxxxxx x = xxxxxx x x x 0x
  4. Type something.                                                                                                   │
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xx-000 xxxxxx: x xxxxxx xxxxxx xx xxx xxx xxxxxxx xxxxxx xxxx xxxxxxx xxx xxxxxx xx'x xxxxxxx,x
  5. Chat about this                                                                                                   │xxxxxxx x = xxxxxx x x x 00x
                                                                                                                       │
Enter to select · Tab/Arrow keys to navigate · Esc to cancel                                                           │xx-000 x xxxxxxxxx xxxxxxxx (xxxx-0) xxxxx xxx xxxxxxx xxx xxxxx xxx xxxx xxxx'x xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx xx xxx xxxx xxxxx xxxxxx xxxxxxxxxx: xxxx xxxx xxxx xxx xxxxx xxx xxxxxxx, xxx xx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxxxxx.xxxxxxxxxx_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxx xxxxxxxxxxxxxx xxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxx xxxx xxxxxxx xxxxxxxx xx xxxxx xxxxx x xxxxxxxx xxxxxx xx xxx xxx xxxxxxxxxxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x

""""");

        // Two tabs, second a multi-select, with its Submit row. Source r4-form-q2.
        internal static readonly string FormTwoTabMulti = Lf("""""
                                                                                                                       │                                                                                             x
 ▐▛███▛█   Claude Code v2.1.326                                                                                        │xx xxxxx                                                           00 xxxxxx x xxxxxxx xxxx xxx
▝▜██████▀  Haiku 5.5 · Claude Team                                                                                     │[ xxxxxxx ] [ xxxx ] xxxx xx xxxx x
 ▝▝   ▝▝   /…/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/scratchpad/live/work · /rc                                          │
                                                                                                                       │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
                                                                                                                       │
❯ /jira                                                                                                                │xx-000 "xxxx xxxx xxx" xx xxx xxxxx-xxxxx xxxx, xxxx x xxxxxx xxxx xxxx xx xxxxxxxx xx xxxxxxxx
  ⎿  jira-board: CB board open.                                                                                        │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ Reply with exactly this one line and nothing else: 日本語のテスト 🤖 emoji ✅ done                                   │xx-000 xxx x xxxx xxxx xx xxx xxxxxxxxx xxxxxxx xxxxxxxx xxxxxx (xxxxxxx, xxxxxxxxxxxxx)
                                                                                                                       │xxxx x = xxxxxx x x x 0x
⏺ 日本語のテスト 🤖 emoji ✅ done                                                                                      │
                                                                                                                       │xx-000 xxxxxx: xxx xxxxx'x xxxxxxx xxxxxxxxxx xxxxx xxx xxxxxxxx (xxxxxxxxx, xxxxxxxxx, xxxxxxx
✻ Sautéed for 1s · done 12:38 PM                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
❯ Run this bash command now: touch probe-file.txt                                                                      │xx-000 xxx xxxxx'x xxxxxxx xxx xxxxx xxx xxxx xxxxx x xxxxxxx xx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 00x
  Did not run 1 call                                                                                                   │
  ⎿  $ touch probe-file.txt && ls -l probe-file.txt                                                                    │xx-000 x xxxxxx xxxx xxxx xxxxx xx "xxx xx xxx xxxxxx", xx xxx xxxxx xxx'x xxxx xxxx xx xxxxxxx
  ⎿  Interrupted · What should Claude do instead?                                                                      │xxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ Call AskUserQuestion now with two questions: (1) header Venue, single select, question 'Where should the offsite be  │xx-000 xxxx xxxxx xx xxxx xxxxxxxx xxxxxxxxx (xxxxxxxx xxxxx xxx xxxx xxxx), xxxxxxx xxxx xxxxx
  held?', options Lakeside cabin, City hotel, Stay remote; (2) header Catering, multiSelect true, question 'Which      │xxxxxxx x = xxxxxx x x x 0x
  catering options should be booked?', options Breakfast, Lunch, Dinner, None of these.                                │
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xx-000 xxxxxxxxxxxxxxxxx.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxxxx xxxxxxxxx xxxx
←  ☒ Venue  ☐ Catering  ✔ Submit  →                                                                                    │xxx x = xxxxxx x x x 0x
                                                                                                                       │
Which catering options should be booked?                                                                               │xx-000 xx-000: xxxxx xxxxxxx xx x xxxxx xxxxxxx, xxx xxxx xxxx xxxxx xxxxx xxxxx xxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
❯ 1. [ ] Breakfast                                                                                                     │
         Book breakfast for attendees.                                                                                 │xx-000 xx-xxxx xx xxxxxxxx xxxxxxxx xxxxxxxxxxxx xxxx xxx xxx xxxx xxxxxx, xxx xxxx xxxxx x xxx
  2. [ ] Lunch                                                                                                         │xxxxxxx x = xxxxxx x x x 0x
         Book lunch for attendees.                                                                                     │
  3. [ ] Dinner                                                                                                        │xx-000 xxxxx xxxx xxxxxxxxxxxxx xx xxxxxxxxx (xxxxxxxxx, xxx, xxxxxxx, xxxxxxx)
         Book dinner for attendees.                                                                                    │xxxxxxx x = xxxxxx x x x 0x
  4. [ ] None of these                                                                                                 │
         Skip catering entirely.                                                                                       │xx-000 xxxxxx: x xxxxxx xxxxxx xx xxx xxx xxxxxxx xxxxxx xxxx xxxxxxx xxx xxxxxx xx'x xxxxxxx,x
  5. [ ] Type something                                                                                                │xxxxxxx x = xxxxxx x x x 00x
     Submit                                                                                                            │
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xx-000 x xxxxxxxxx xxxxxxxx (xxxx-0) xxxxx xxx xxxxxxx xxx xxxxx xxx xxxx xxxx'x xxxx
  6. Chat about this                                                                                                   │xxx x = xxxxxx x x x 0x
                                                                                                                       │
Enter to select · Tab/Arrow keys to navigate · Esc to cancel                                                           │xx-000 xxxxxx xx xxx xxxx xxxxx xxxxxx xxxxxxxxxx: xxxx xxxx xxxx xxx xxxxx xxx xxxxxxx, xxx xx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxxxxx.xxxxxxxxxx_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxx xxxxxxxxxxxxxx xxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxx xxxx xxxxxxx xxxxxxxx xx xxxxx xxxxx x xxxxxxxx xxxxxx xx xxx xxx xxxxxxxxxxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x

""""");

        // A form whose wrapped question and long descriptions sit in a frame. Source form3-q1.
        internal static readonly string FormFramedWrapped = Lf("""""
                                                                                                                       │                                                                                             x
 ▐▛███▛█   Claude Code v2.1.326                                                                                        │xx xxxxx                                                           00 xxxxxx x xxxxxxx xxxx xxx
▝▜██████▀  Haiku 5.5 · Claude Team                                                                                     │[ xxxxxxx ] [ xxxx ] xxxx xx xxxx x
 ▝▝   ▝▝   /…/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/scratchpad/live/work · /rc                                          │
                                                                                                                       │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
                                                                                                                       │
❯ Call the AskUserQuestion tool now with exactly one question: header Fruits, question 'Which fruits do you want?',    │xx-000 "xxxx xxxx xxx" xx xxx xxxxx-xxxxx xxxx, xxxx x xxxxxx xxxx xxxx xx xxxxxxxx xx xxxxxxxx
  multiSelect true, options Apple, Banana, Cherry (each with a one-line description). Do not do anything else first.   │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
⏺ Declined to answer Claude's questions:                                                                               │xx-000 xxx x xxxx xxxx xx xxx xxxxxxxxx xxxxxxx xxxxxxxx xxxxxx (xxxxxxx, xxxxxxxxxxxxx)
  ⎿  · Which fruits do you want? (Apple / Banana / Cherry)                                                             │xxxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ /jira                                                                                                                │xx-000 xxxxxx: xxx xxxxx'x xxxxxxx xxxxxxxxxx xxxxx xxx xxxxxxxx (xxxxxxxxx, xxxxxxxxx, xxxxxxx
  ⎿  jira-board: CB board open.                                                                                        │xxx x = xxxxxx x x x 00x
                                                                                                                       │
❯ Call AskUserQuestion again with two questions: (1) header Venue, single select, question 'Where should the offsite   │xx-000 xxx xxxxx'x xxxxxxx xxx xxxxx xxx xxxx xxxxx x xxxxxxx xx xxxx
  be held, given that half the team is remote and the budget only stretches to one night away from home?', options     │xxx x = xxxxxx x x x 00x
  Lakeside cabin, City hotel, Stay remote, each with a long two-sentence description; (2) header Catering, multiSelect │
  true, question 'Which catering options should be booked?', options Breakfast, Lunch, Dinner, None of these.          │xx-000 x xxxxxx xxxx xxxx xxxxx xx "xxx xx xxx xxxxxx", xx xxx xxxxx xxx'x xxxx xxxx xx xxxxxxx
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xxx x = xxxxxx x x x 0x
←  ☐ Venue  ☐ Catering  ✔ Submit  →                                                                                    │
                                                                                                                       │xx-000 xxxx xxxxx xx xxxx xxxxxxxx xxxxxxxxx (xxxxxxxx xxxxx xxx xxxx xxxx), xxxxxxx xxxx xxxxx
│ Where should the offsite be held, given that half the team is remote and the budget only stretches to one night away │xxxxxxx x = xxxxxx x x x 0x
│ from home?                                                                                                           │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxx.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxxxx xxxxxxxxx xxxx
❯ 1. Lakeside cabin                                                                                                    │xxx x = xxxxxx x x x 0x
     Book a rented cabin on a lake for one night, so the in-person half of the team gets a shared, rural retreat       │
     setting. It is the most immersive option, but the remote half has to travel the furthest, and the cabin needs a   │xx-000 xx-000: xxxxx xxxxxxx xx x xxxxx xxxxxxx, xxx xxxx xxxx xxxxx xxxxx xxxxx xxx xxxxx
     headcount that fits its beds.                                                                                     │xxx x = xxxxxx x x x 0x
  2. City hotel                                                                                                        │
     Book a hotel room block in the nearest city for one night, so travel is quick and simple for most people. It is   │xx-000 xx-xxxx xx xxxxxxxx xxxxxxxx xxxxxxxxxxxx xxxx xxx xxx xxxx xxxxxx, xxx xxxx xxxxx x xxx
     cheaper per room and easier to reach, but it offers less of a retreat feel and is less conducive to focused,      │xxxxxxx x = xxxxxx x x x 0x
     away-from-work time.                                                                                              │
  3. Stay remote                                                                                                       │xx-000 xxxxx xxxx xxxxxxxxxxxxx xx xxxxxxxxx (xxxxxxxxx, xxx, xxxxxxx, xxxxxxx)
     Skip travel entirely and run the offsite as a video-first session with the whole team working from home. It costs │xxxxxxx x = xxxxxx x x x 0x
     nothing beyond the meeting tools and keeps everyone on equal footing, but it loses the in-person bonding that an  │
     offsite is usually meant to provide.                                                                              │xx-000 xxxxxx: x xxxxxx xxxxxx xx xxx xxx xxxxxxx xxxxxx xxxx xxxxxxx xxx xxxxxx xx'x xxxxxxx,x
  4. Type something.                                                                                                   │xxxxxxx x = xxxxxx x x x 00x
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│
  5. Chat about this                                                                                                   │xx-000 x xxxxxxxxx xxxxxxxx (xxxx-0) xxxxx xxx xxxxxxx xxx xxxxx xxx xxxx xxxx'x xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
Enter to select · Tab/Arrow keys to navigate · Esc to cancel                                                           │
                                                                                                                       │xx-000 xxxxxx xx xxx xxxx xxxxx xxxxxx xxxxxxxxxx: xxxx xxxx xxxx xxx xxxxx xxx xxxxxxx, xxx xx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxxxxx.xxxxxxxxxx_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxx xxxxxxxxxxxxxx xxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxx xxxx xxxxxxx xxxxxxxx xx xxxxx xxxxx x xxxxxxxx xxxxxx xx xxx xxx xxxxxxxxxxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x

""""");

        // A Bash permission dialog beside the board. Source r4-perm-board.
        internal static readonly string PermissionBoard = Lf("""""
                                                                                                                       │                                                                                             x
 ▐▛███▛█   Claude Code v2.1.326                                                                                        │xx xxxxx                                                           00 xxxxxx x xxxxxxx xxxx xxx
▝▜██████▀  Haiku 5.5 · Claude Team                                                                                     │[ xxxxxxx ] [ xxxx ] xxxx xx xxxx x
 ▝▝   ▝▝   /…/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/scratchpad/live/work · /rc                                          │
                                                                                                                       │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
                                                                                                                       │
❯ /jira                                                                                                                │xx-000 "xxxx xxxx xxx" xx xxx xxxxx-xxxxx xxxx, xxxx x xxxxxx xxxx xxxx xx xxxxxxxx xx xxxxxxxx
  ⎿  jira-board: CB board open.                                                                                        │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ Reply with exactly this one line and nothing else: 日本語のテスト 🤖 emoji ✅ done                                   │xx-000 xxx x xxxx xxxx xx xxx xxxxxxxxx xxxxxxx xxxxxxxx xxxxxx (xxxxxxx, xxxxxxxxxxxxx)
                                                                                                                       │xxxx x = xxxxxx x x x 0x
⏺ 日本語のテスト 🤖 emoji ✅ done                                                                                      │
                                                                                                                       │xx-000 xxxxxx: xxx xxxxx'x xxxxxxx xxxxxxxxxx xxxxx xxx xxxxxxxx (xxxxxxxxx, xxxxxxxxx, xxxxxxx
✻ Sautéed for 1s · done 12:38 PM                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
❯ Run this bash command now: touch probe-file.txt                                                                      │xx-000 xxx xxxxx'x xxxxxxx xxx xxxxx xxx xxxx xxxxx x xxxxxxx xx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 00x
  Creating probe-file.txt in the working directory and confirm it exists                                               │
  ⎿  $ touch probe-file.txt && ls -l probe-file.txt                                                                    │xx-000 x xxxxxx xxxx xxxx xxxxx xx "xxx xx xxx xxxxxx", xx xxx xxxxx xxx'x xxxx xxxx xx xxxxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│
 Bash command                                                                                                          │xx-000 xxxx xxxxx xx xxxx xxxxxxxx xxxxxxxxx (xxxxxxxx xxxxx xxx xxxx xxxx), xxxxxxx xxxx xxxxx
 Tip: auto mode handles these prompts for you — choose "switch to auto mode" below                                     │xxxxxxx x = xxxxxx x x x 0x
 Create probe-file.txt in the working directory and confirm it exists                                                  │
╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌│xx-000 xxxxxxxxxxxxxxxxx.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxxxx xxxxxxxxx xxxx
 touch probe-file.txt && ls -l probe-file.txt                                                                          │xxx x = xxxxxx x x x 0x
╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌│
 Do you want to proceed?                                                                                               │xx-000 xx-000: xxxxx xxxxxxx xx x xxxxx xxxxxxx, xxx xxxx xxxx xxxxx xxxxx xxxxx xxx xxxxx
 ❯ 1. Yes                                                                                                              │xxx x = xxxxxx x x x 0x
   2. Yes, and always allow access to /work/project/xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx-xxxx-xxx │
      x-xxxx-xxxxxxxxxxxx/scratchpad/live/work from this project                                                       │xx-000 xx-xxxx xx xxxxxxxx xxxxxxxx xxxxxxxxxxxx xxxx xxx xxx xxxx xxxxxx, xxx xxxx xxxxx x xxx
   3. Yes, and switch to auto mode · auto mode handles these prompts for you                                           │xxxxxxx x = xxxxxx x x x 0x
   4. No                                                                                                               │
                                                                                                                       │xx-000 xxxxx xxxx xxxxxxxxxxxxx xx xxxxxxxxx (xxxxxxxxx, xxx, xxxxxxx, xxxxxxx)
 Esc to cancel · Tab to amend                                                                                          │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx: x xxxxxx xxxxxx xx xxx xxx xxxxxxx xxxxxx xxxx xxxxxxx xxx xxxxxx xx'x xxxxxxx,x
                                                                                                                       │xxxxxxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 x xxxxxxxxx xxxxxxxx (xxxx-0) xxxxx xxx xxxxxxx xxx xxxxx xxx xxxx xxxx'x xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx xx xxx xxxx xxxxx xxxxxx xxxxxxxxxx: xxxx xxxx xxxx xxx xxxxx xxx xxxxxxx, xxx xx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxxxxx.xxxxxxxxxx_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxx xxxxxxxxxxxxxx xxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxx xxxx xxxxxxx xxxxxxxx xx xxxxx xxxxx x xxxxxxxx xxxxxx xx xxx xxx xxxxxxxxxxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x

""""");

        // No dialog, the board open, the input box below it full width. Source r4-idle-board.
        internal static readonly string IdleBoard = Lf("""""
                                                                                                                       │                                                                                             x
 ▐▛███▛█   Claude Code v2.1.326                                                                                        │xx xxxxx                                                           00 xxxxxx x xxxxxxx xxxx xxx
▝▜██████▀  Haiku 5.5 · Claude Team                                                                                     │[ xxxxxxx ] [ xxxx ] xxxx xx xxxx x
 ▝▝   ▝▝   /…/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/scratchpad/live/work · /rc                                          │
                                                                                                                       │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
                                                                                                                       │
❯ /jira                                                                                                                │xx-000 "xxxx xxxx xxx" xx xxx xxxxx-xxxxx xxxx, xxxx x xxxxxx xxxx xxxx xx xxxxxxxx xx xxxxxxxx
  ⎿  jira-board: CB board open.                                                                                        │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxx x xxxx xxxx xx xxx xxxxxxxxx xxxxxxx xxxxxxxx xxxxxx (xxxxxxx, xxxxxxxxxxxxx)
                                                                                                                       │xxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx: xxx xxxxx'x xxxxxxx xxxxxxxxxx xxxxx xxx xxxxxxxx (xxxxxxxxx, xxxxxxxxx, xxxxxxx
                                                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 xxx xxxxx'x xxxxxxx xxx xxxxx xxx xxxx xxxxx x xxxxxxx xx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 x xxxxxx xxxx xxxx xxxxx xx "xxx xx xxx xxxxxx", xx xxx xxxxx xxx'x xxxx xxxx xx xxxxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxx xxxxx xx xxxx xxxxxxxx xxxxxxxxx (xxxxxxxx xxxxx xxx xxxx xxxx), xxxxxxx xxxx xxxxx
                                                                                                                       │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxx.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxxxx xxxxxxxxx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xx-000: xxxxx xxxxxxx xx x xxxxx xxxxxxx, xxx xxxx xxxx xxxxx xxxxx xxxxx xxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xx-xxxx xx xxxxxxxx xxxxxxxx xxxxxxxxxxxx xxxx xxx xxx xxxx xxxxxx, xxx xxxx xxxxx x xxx
                                                                                                                       │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxx xxxx xxxxxxxxxxxxx xx xxxxxxxxx (xxxxxxxxx, xxx, xxxxxxx, xxxxxxx)
                                                                                                                       │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx: x xxxxxx xxxxxx xx xxx xxx xxxxxxx xxxxxx xxxx xxxxxxx xxx xxxxxx xx'x xxxxxxx,x
                                                                                                                       │xxxxxxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 x xxxxxxxxx xxxxxxxx (xxxx-0) xxxxx xxx xxxxxxx xxx xxxxx xxx xxxx xxxx'x xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx xx xxx xxxx xxxxx xxxxxx xxxxxxxxxx: xxxx xxxx xxxx xxx xxxxx xxx xxxxxxx, xxx xx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
❯ 
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  Haiku 5.5 | ~/work | [.claude]
  ⏸ manual mode on · ← 2 agents

""""");

        // CJK and emoji left of the divider, so its string index and display cell differ. Source r4-wide-board.
        internal static readonly string WideBoard = Lf("""""
                                                                                                                       │                                                                                             x
 ▐▛███▛█   Claude Code v2.1.326                                                                                        │xx xxxxx                                                           00 xxxxxx x xxxxxxx xxxx xxx
▝▜██████▀  Haiku 5.5 · Claude Team                                                                                     │[ xxxxxxx ] [ xxxx ] xxxx xx xxxx x
 ▝▝   ▝▝   /…/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/scratchpad/live/work · /rc                                          │
                                                                                                                       │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
                                                                                                                       │
❯ /jira                                                                                                                │xx-000 "xxxx xxxx xxx" xx xxx xxxxx-xxxxx xxxx, xxxx x xxxxxx xxxx xxxx xx xxxxxxxx xx xxxxxxxx
  ⎿  jira-board: CB board open.                                                                                        │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
❯ Reply with exactly this one line and nothing else: 日本語のテスト 🤖 emoji ✅ done                                   │xx-000 xxx x xxxx xxxx xx xxx xxxxxxxxx xxxxxxx xxxxxxxx xxxxxx (xxxxxxx, xxxxxxxxxxxxx)
                                                                                                                       │xxxx x = xxxxxx x x x 0x
⏺ 日本語のテスト 🤖 emoji ✅ done                                                                                      │
                                                                                                                       │xx-000 xxxxxx: xxx xxxxx'x xxxxxxx xxxxxxxxxx xxxxx xxx xxxxxxxx (xxxxxxxxx, xxxxxxxxx, xxxxxxx
✻ Sautéed for 1s · done 12:38 PM                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 xxx xxxxx'x xxxxxxx xxx xxxxx xxx xxxx xxxxx x xxxxxxx xx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 x xxxxxx xxxx xxxx xxxxx xx "xxx xx xxx xxxxxx", xx xxx xxxxx xxx'x xxxx xxxx xx xxxxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxx xxxxx xx xxxx xxxxxxxx xxxxxxxxx (xxxxxxxx xxxxx xxx xxxx xxxx), xxxxxxx xxxx xxxxx
                                                                                                                       │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxxxxxxxxxxxxx.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx xxxxxxx xxxxxxxxx xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xx-000: xxxxx xxxxxxx xx x xxxxx xxxxxxx, xxx xxxx xxxx xxxxx xxxxx xxxxx xxx xxxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xx-xxxx xx xxxxxxxx xxxxxxxx xxxxxxxxxxxx xxxx xxx xxx xxxx xxxxxx, xxx xxxx xxxxx x xxx
                                                                                                                       │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxx xxxx xxxxxxxxxxxxx xx xxxxxxxxx (xxxxxxxxx, xxx, xxxxxxx, xxxxxxx)
                                                                                                                       │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx: x xxxxxx xxxxxx xx xxx xxx xxxxxxx xxxxxx xxxx xxxxxxx xxx xxxxxx xx'x xxxxxxx,x
                                                                                                                       │xxxxxxx x = xxxxxx x x x 00x
                                                                                                                       │
                                                                                                                       │xx-000 x xxxxxxxxx xxxxxxxx (xxxx-0) xxxxx xxx xxxxxxx xxx xxxxx xxx xxxx xxxx'x xxxx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
                                                                                                                       │xx-000 xxxxxx xx xxx xxxx xxxxx xxxxxx xxxxxxxxxx: xxxx xxxx xxxx xxx xxxxx xxx xxxxxxx, xxx xx
                                                                                                                       │xxx x = xxxxxx x x x 0x
                                                                                                                       │
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
❯ 
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  Haiku 5.5 | ~/work | 43% (7d:6%) | [.claude]
  ⏸ manual mode on · ← 2 agents

""""");

        // A multi-select with ticks and free text typed into its fifth row. Source twotab-typing-after-state-flip.
        internal static readonly string TypedFreeText = Lf("""""
                                                                                                                      │                                                                                             x
                                                                                                                      │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
  xxx 1 xxxxx xxxxxxx                                                                                                 │
                                                                                                                      │xx-000 xxxxx xxxxxxxx xx xxxxxx xxxxxxx xxx xxxxxxx xxxxxxxx xxxxxx
⏺ xx xxxxxxx xx x'xx xxxxxxxxx xx xxxx. x xxxxxxxxxx xxxxxx xxxxxxx xxxx xxxxxxx'x xxx xxxxx xxx xxxxxxxx xxx xxxx    │xxxx x = xxxxxx x x x 00x
  xxxxx xxxxxx, xxxx x xxx xxx xxx xxxxxxxxx xxxxxxxx.                                                                │
                                                                                                                      │xx-000 xxx xxxxxxxxx xxxx: x xxxxxxxxx xxxx xxx xxx xx xxx xxx
  xxx 1 xxxxx xxxxxxx                                                                                                 │xxxxxxx x = xxxxxx x x x 00x
                                                                                                                      │
⏺ xxxxxxxx xxxxxx'x xxxxxxxxx:                                                                                        │xx-000 xxxxx xxxxxxx xxx xxxxxxxxxxxx xxxxxxx xxx xxxxxxxxx xx xxxxx xxxxx-xxxxxx xxxx (xx xxxx
  ⎿  · xxxxx xxxxx xxx xxx xxxxxxx xxxx (xxxxxxxxx, xxxx xxxxxxxx). xxxx xxxx xxxx xxxx xx xx xxx xxxxxxxxx xxxxx     │xxx x x xxxx x x x 0x
     xxxxx xxx? → xxx, xxx xxxx xx xxxxx                                                                              │
                                                                                                                      │xx-000 xxxx xxx xxxxxxx xxxx xx xxxxxx xxxxxxxxxx; xxxxxxx xxxxx xxxxxxxx xx xxxxx
  xxx 1 xxxxx xxxxxxx                                                                                                 │xxx x = xxxxxx x x x 0x
                                                                                                                      │
⏺ xxx xxxxxx xxxx xxxxx xxx xxxx xx xxxxxxx xxxxx xxxxx 9 xxxxxxx, xx xxx xxxxxxxx xxx-xxx xxxx xxxx x xxxxx-xxxxxx xx│xx-000 xxxxxx xxxxxxxx, xxxx xxxxxxxxxx xxx xxxxxxxxxx xxx xxxxxxxx xx xxx xxxxxx xxxx
  xxx xxxxxx xxxxxxx. x'x xx-xxxxxx xxxx xxxx xxxx xxx xxxx xxxxx xxx xxxxxx xxxx; xxxx xxxxxxx xxxxxxx xxxx          │xxx x = xxxxxx x x x 0x
  xxxxxxxx, xx xxxx xxxxxxxx. · xxxxxxx                                                                               │
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xx-000 xxx-xxxx xxxxxx xxxx (xxxxxx, xxxxx, xxxxxxxxx, xxxxx) xxxx xxxxxxxx xxxxxx xx xxx xxxxx
←  ☒ Ownership  ☒ Risky steps  ✔ Submit  →                                                                            │xxx x x xxx x x x 0x
                                                                                                                      │
│ xxxxx. xxxxx xxxxx xxx'x xx xxxxx xxxxxx xx xxx xxx xxxxx xxxxxx. xxxxx xxx x xx xxxxxxx xxxxxx xxx xxxxx? (1)      │xx-000 xxxxx: xxxxxx xxxxx x xxxxx xxxxxxx, xxxxxxxx xxxxx xxx xxxxxx-xxxxx xxxxxxx (xxxxxxx xx
│ xxxxxx xxx xxx xxxxx_* xxxxxxx xxxxxxx xxxx x xxxxxxxxx 'xxxxxxx' xxxxxxxxxxx: xxxxxx xxx'x xxxx xxxxxx xxxxxx xxxx,│xxxxxxx x = xxxxxx x x x 0x
│ xx xxx xx xxxxxx xxxx xx xxxxx xxxx xx xxxxx, xxx xxxxxxxx xxxx xxxxx xxx xx. (2) xxxxxxxxx 2xx xxx xxx xxxxx xxx:  │
│ xxxxxx xxxxxxx xxxxx xxxxxx xxx xxxxx'x xxxx xx. (3) xxxxxxxx xxx xxx xxxxxxx xxxxxxxxxx xxxx xxxxx xx xxxx: xxxxx  │xx-000 xxxx xxxx xxx xxxx x xxx xxxx xxxx xxxx xxx xxxxxxx xxx
│ 44 xxxxxx xxxx xxxx xxxxxx xxxxxx xxxx'xx xxxxx xxxx xx x xxxx.                                                     │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                      │
  1. [✔] xxxxxxx → xxxxxxx xxx                                                                                        │xx-000 xx-000: x xxxx xxxxx xxxxxxx x xxxxxx xxx xxx'x xx xxxxxxx xx x xxxxx, xxx xxx xxx xxx'x
         x xxxxxx xxx xxxxxxxxxxx xxxx xxx xxx xxxxxx xx xxxxxxxx xxxxxxxxx xxx x* xxxx xxxx, xxx xxxx xx xxxxxxx.xxx.│xxx x = xxxxxx x x x 0x
         xxx xxxxx xx xxx xxx xxxxxx; x xxxxxx xxx xxxx-xxxxx xxxxxx xxxx xxxx xxxx'xx xx.                            │
  2. [✔] xxxxxxx xxx 2xx                                                                                              │xx-000 xxxx xxxxxxxxxxxxxxxxx xxxxxxxxxx xxxxx xxxx xxxx xxxxxxx 000x, xxxxx xx xxxxxx xxx xxx
         x xxxx xxx xxxxx xx xxxxxxx xxxxx xxx xxxx xxx xxx xxxx xxxxxx x xxxx xx xx.                                 │xxxx x = xxxxxx x x x 0x
  3. [✔] xxxxxxx xxxx → xxxx                                                                                          │
         x xxxxx xxxxxx x xxxx xxx xxx xxxxxxx xxxxxxxxx xxxxxxx xx xxxxxx xxx xxxxxxxx xxxxx xx xx xxxxx xxxxxx.     │xx-000 xxxxxxx xxxxxxxxxxxxxxxx xxxx xxxxx xxxxx xxxxx xxxxx xxxx xxx xxxxxxxx, xx xx-000'x 0xx
  4. [ ] xxxx xx xxxxx                                                                                                │xxx x = xxxxxx x x x 0x
         x xxxxxxx xxx xxxxx xxx xxx xx xxxxxx xxxx xxx xxxxxxxx xxxxxxxxxx.                                          │
❯ 5. [✔] xxxx xxxx xxx xxx xxxx xx xx xx                                                                              │xx-000 xxxx xxxx: xxx xxx-xxxxxx xxxxxx xxxxxxxxx (xxxxxx, xxxxxxxxxxxx) xxxx xxx xxxxxxxx xxxx
     Submit                                                                                                           │xxx x = xxxxxx x x x 0x
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│
  6. Chat about this                                                                                                  │xx-000 xx-000: x xxxxx xxxxxx xxxx xxxx xxxxxxxx xxx-xxxx, xxxxx xxxxxxx xxx xxxx, xxxxxx xxxxx
                                                                                                                      │xxx x = xxxxxx x x x 0x
Enter to select · Tab/Arrow keys to navigate · ctrl+g to edit in nano · Esc to cancel                                 │

""""");

        // The Submit tab, "Review your answers", with its Submit answers / Cancel prompt. Source pane-55.
        internal static readonly string ReviewTab = Lf("""""
                                                                                                                      │                                                                                             x
                                                                                                                      │[ xxxx 00 ] [ xxxxxxxxxxx 0 ] [ xxxxxxx 0 ] [ xxxxxxxxxx 00 ]
  xxx 1 xxxxx xxxxxxx                                                                                                 │
                                                                                                                      │xx-000 xxxxx xxxxxxxx xx xxxxxx xxxxxxx xxx xxxxxxx xxxxxxxx xxxxxx
⏺ xx xxxxxxx xx x'xx xxxxxxxxx xx xxxx. x xxxxxxxxxx xxxxxx xxxxxxx xxxx xxxxxxx'x xxx xxxxx xxx xxxxxxxx xxx xxxx    │xxxx x = xxxxxx x x x 00x
  xxxxx xxxxxx, xxxx x xxx xxx xxx xxxxxxxxx xxxxxxxx.                                                                │
                                                                                                                      │xx-000 xxx xxxxxxxxx xxxx: x xxxxxxxxx xxxx xxx xxx xx xxx xxx
  xxx 1 xxxxx xxxxxxx                                                                                                 │xxxxxxx x = xxxxxx x x x 00x
                                                                                                                      │
⏺ xxxxxxxx xxxxxx'x xxxxxxxxx:                                                                                        │xx-000 xxxxx xxxxxxx xxx xxxxxxxxxxxx xxxxxxx xxx xxxxxxxxx xx xxxxx xxxxx-xxxxxx xxxx (xx xxxx
  ⎿  · xxxxx xxxxx xxx xxx xxxxxxx xxxx (xxxxxxxxx, xxxx xxxxxxxx). xxxx xxxx xxxx xxxx xx xx xxx xxxxxxxxx xxxxx     │xxx x x xxxx x x x 0x
     xxxxx xxx? → xxx, xxx xxxx xx xxxxx                                                                              │
                                                                                                                      │xx-000 xxxx xxx xxxxxxx xxxx xx xxxxxx xxxxxxxxxx; xxxxxxx xxxxx xxxxxxxx xx xxxxx
  xxx 1 xxxxx xxxxxxx                                                                                                 │xxx x = xxxxxx x x x 0x
                                                                                                                      │
⏺ xxx xxxxxx xxxx xxxxx xxx xxxx xx xxxxxxx xxxxx xxxxx 9 xxxxxxx, xx xxx xxxxxxxx xxx-xxx xxxx xxxx x xxxxx-xxxxxx xx│xx-000 xxxxxx xxxxxxxx, xxxx xxxxxxxxxx xxx xxxxxxxxxx xxx xxxxxxxx xx xxx xxxxxx xxxx
  xxx xxxxxx xxxxxxx. x'x xx-xxxxxx xxxx xxxx xxxx xxx xxxx xxxxx xxx xxxxxx xxxx; xxxx xxxxxxx xxxxxxx xxxx          │xxx x = xxxxxx x x x 0x
  xxxxxxxx, xx xxxx xxxxxxxx. · xxxxxxx                                                                               │
                                                                                                                      │xx-000 xxx-xxxx xxxxxx xxxx (xxxxxx, xxxxx, xxxxxxxxx, xxxxx) xxxx xxxxxxxx xxxxxx xx xxx xxxxx
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────│xxx x x xxx x x x 0x
←  ☒ Ownership  ☒ Risky steps  ✔ Submit  →                                                                            │
                                                                                                                      │xx-000 xxxxx: xxxxxx xxxxx x xxxxx xxxxxxx, xxxxxxxx xxxxx xxx xxxxxx-xxxxx xxxxxxx (xxxxxxx xx
Review your answers                                                                                                   │xxxxxxx x = xxxxxx x x x 0x
                                                                                                                      │
 │ ● xxxxx xx xxx xxxxxxxx xxxx (xxxxxxx xxxxxxx, xxxx xxxxxxx xxxxxxx xxxxx). xxxxxx xxxxxx xxxxx xx-317 xx xx-320   │xx-000 xxxx xxxx xxx xxxx x xxx xxxx xxxx xxxx xxx xxxxxxx xxx
 │   (xxxxxx xxxxxxxx xxxxxxxxx). xxxx xxxxxxxx xxxx xxxx x xxxxxx xx xxxxxx'x xxxxxxx xxx xxxxx xxxx, xxxxxxx xx xx  │xxxxxxx x = xxxxxx x x x 0x
 │   xxxxxx xxxx. x xx-xxxxxxx xxxxx xxx xxx xxxx xxx xxxxx xxxxxxx xx xxxxx: xx xxxxxxxxxxxx, xx xxxxxxxx, xx xxxxxx │
 │   xxxxxxxxxx, xxxxxxx xxxxx xx xxxxx, xxxxxxxx xxx xxxxxxxxxx xxx, xxx 2xx xxx xxxxxxxx, xxx xxxx xxxx xx 'xxx     │xx-000 xx-000: x xxxx xxxxx xxxxxxx x xxxxxx xxx xxx'x xx xxxxxxx xx x xxxxx, xxx xxx xxx xxx'x
 │   xxxxxxxxxxxx'. xx xx xxxxx xxx xxxxx, xx x xxx xxxx xxxx xx xxxxx xxxxxxx. xx xxx xxxx xx xx xxxx xxxxx xxxx xxxx│xxx x = xxxxxx x x x 0x
 │   xxxxxx?                                                                                                          │
   → xxx, xxxx xxxx xxxx (xxxxxxxxxxx)                                                                                │xx-000 xxxx xxxxxxxxxxxxxxxxx xxxxxxxxxx xxxxx xxxx xxxx xxxxxxx 000x, xxxxx xx xxxxxx xxx xxx
 │ ● xxxxx. xxxxx xxxxx xxx'x xx xxxxx xxxxxx xx xxx xxx xxxxx xxxxxx. xxxxx xxx x xx xxxxxxx xxxxxx xxx xxxxx? (1)   │xxxx x = xxxxxx x x x 0x
 │   xxxxxx xxx xxx xxxxx_* xxxxxxx xxxxxxx xxxx x xxxxxxxxx 'xxxxxxx' xxxxxxxxxxx: xxxxxx xxx'x xxxx xxxxxx xxxxxx   │
 │   xxxx, xx xxx xx xxxxxx xxxx xx xxxxx xxxx xx xxxxx, xxx xxxxxxxx xxxx xxxxx xxx xx. (2) xxxxxxxxx 2xx xxx xxx    │xx-000 xxxxxxx xxxxxxxxxxxxxxxx xxxx xxxxx xxxxx xxxxx xxxxx xxxx xxx xxxxxxxx, xx xx-000'x 0xx
 │   xxxxx xxx: xxxxxx xxxxxxx xxxxx xxxxxx xxx xxxxx'x xxxx xx. (3) xxxxxxxx xxx xxx xxxxxxx xxxxxxxxxx xxxx xxxxx xx│xxx x = xxxxxx x x x 0x
 │   xxxx: xxxxx 44 xxxxxx xxxx xxxx xxxxxx xxxxxx xxxx'xx xxxxx xxxx xx x xxxx.                                      │
   → xxxxxxx → xxxxxxx xxx, xxxxxxx xxx 2xx, xxxxxxx xxxx → xxxx, xxxx xxxx xxx xxx xxxx xx xx xxxxxxxxx xxx          │xx-000 xxxx xxxx: xxx xxx-xxxxxx xxxxxx xxxxxxxxx (xxxxxx, xxxxxxxxxxxx) xxxx xxx xxxxxxxx xxxx
                                                                                                                      │xxx x = xxxxxx x x x 0x
Ready to submit your answers?                                                                                         │
                                                                                                                      │xx-000 xx-000: x xxxxx xxxxxx xxxx xxxx xxxxxxxx xxx-xxxx, xxxxx xxxxxxx xxx xxxx, xxxxxx xxxxx
❯ 1. Submit answers                                                                                                   │xxx x = xxxxxx x x x 0x
  2. Cancel                                                                                                           │

""""");
    }
}

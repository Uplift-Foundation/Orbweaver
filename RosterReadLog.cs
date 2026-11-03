using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // CB-300: says once per dir that reading its agent roster failed, and again
    // only when the reason changes or the dir has recovered and failed afresh.
    //
    // A peer says hello about every ten seconds, so a dir that stays dead would
    // otherwise write a line per tick forever; MirrorLog.SayOnce has the same
    // aim but cannot see a recovery (an identical detail after a success is
    // suppressed), and the log is how a person finds out a profile that used to
    // work stopped. The sink is injected so the decision is testable without
    // the opt-in log being on.
    internal sealed class RosterReadLog
    {
        private readonly Action<string, string> _say;
        private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        internal RosterReadLog(Action<string, string> say) => _say = say;

        // label is the dir already made safe to print; failure is null for a
        // read that worked, which also forgets an earlier failure.
        internal void Note(string label, string? failure)
        {
            lock (_lock)
            {
                if (failure is null)
                {
                    _last.Remove(label);
                    return;
                }

                if (_last.TryGetValue(label, out var before) && before == failure) return;

                _last[label] = failure;
            }

            _say("roster-read-failed", $"dir={label} reason={failure}");
        }
    }
}

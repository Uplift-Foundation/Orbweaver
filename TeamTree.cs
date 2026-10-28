namespace Orbweaver
{
    // CB-268: who leads whom among the orbs on screen, answered once so the
    // arrangement, the drag, the Group menu and the arrows all see the same
    // tree. Before this each of them walked SessionStatus.Lead on its own, and
    // they disagreed in exactly the cases the team rules care about.
    //
    // Two things a raw Lead walk gets wrong:
    //
    // T4 — a lead that ends. A member's Lead still names it, and its status is
    // gone, so a walk over live statuses stops dead at the member and makes it
    // a root of its own. The rule is that the member re-attaches to its
    // nearest *surviving* ancestor, and the only way to know who that is, is
    // to have remembered the ended lead's own Lead. `knownLead` is that memory:
    // the last Lead seen for every session, kept after it ends for as long as
    // something on screen still descends through it (see Reachable).
    //
    // Roots are not decided here. Once LeadOf has turned the remembered chain
    // into an on-screen leadOf array, OrbGroups.RootOf — the single team-root
    // resolver, built on OrbArrangement.Resolves so it is Classify's own rule —
    // says where each orb's team starts. A cycle, or a chain into one, is
    // therefore its own root, exactly as the arrangement draws it; a second
    // opinion here would let a drag move as one team orbs the arrangement drew
    // apart. This class only adds what that resolver cannot know on its own:
    // who an ended lead's lead was.
    //
    // Pure: no windows, no settings, no statuses — ids in, ids out.
    internal sealed class TeamTree
    {
        private readonly IReadOnlyList<string> _present;
        private readonly HashSet<string> _onScreen;
        private readonly IReadOnlyDictionary<string, string> _knownLead;
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly int[] _leadOf;

        // `present` is every orb on screen, in the order the caller wants teams
        // listed in (SessionManager's display order); duplicates are ignored.
        // `knownLead` maps a session id to the Lead it last reported, ended
        // sessions included; an id with no entry, or an empty one, leads itself.
        internal TeamTree(IReadOnlyList<string> present, IReadOnlyDictionary<string, string> knownLead)
        {
            _present = present.Distinct(StringComparer.Ordinal).ToList();
            _onScreen = new HashSet<string>(_present, StringComparer.Ordinal);
            _knownLead = knownLead;

            for (var i = 0; i < _present.Count; i++) _index[_present[i]] = i;

            _leadOf = new int[_present.Count];
            for (var i = 0; i < _present.Count; i++)
                _leadOf[i] = LeadOf(_present[i]) is { } lead ? _index[lead] : -1;
        }

        internal IReadOnlyList<string> Present => _present;

        // The nearest ancestor that is on screen, or null when there is none:
        // no lead, a self-lead, a chain that runs out among ended sessions, or
        // one that loops back without passing anybody on screen.
        internal string? LeadOf(string id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var at = NextOf(id);

            while (at is not null && seen.Add(at))
            {
                if (_onScreen.Contains(at)) return at;
                at = NextOf(at);
            }

            return null;
        }

        // The top of the orb's team, by OrbGroups.RootOf over LeadIndices. An
        // orb that is not on screen is its own root.
        internal string RootOf(string id) =>
            _index.TryGetValue(id, out var at)
                ? _present[OrbGroups.RootOf(at, _leadOf, _present.Count)]
                : id;

        // The orb's whole team: its root first, then every on-screen orb with
        // that root in `present` order — nested members included. A solo orb
        // is a team of one. This is the list a plain drag moves (T2) and the
        // keys a snap writes (T3).
        internal IReadOnlyList<string> TeamOf(string id)
        {
            var root = RootOf(id);
            var team = new List<string> { root };

            foreach (var other in _present)
            {
                if (other != root && RootOf(other) == root) team.Add(other);
            }

            return team;
        }

        // LeadOf as indices into `present`, -1 for none — the leadOf array
        // OrbArrangement takes. Built from the same walk, so an orb whose lead
        // ended fans off its surviving grand-lead rather than standing alone.
        internal int[] LeadIndices() => (int[])_leadOf.Clone();

        // Which entries of a known-lead memory are still worth keeping: every
        // on-screen orb's own, and every ended session's that an on-screen orb
        // still walks through. Everything else is a session nothing descends
        // from any more, and keeping it would grow the memory for ever.
        internal static HashSet<string> Reachable(
            IEnumerable<string> present, IReadOnlyDictionary<string, string> knownLead)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);

            foreach (var start in present)
            {
                var at = start;
                while (knownLead.ContainsKey(at) && keep.Add(at))
                {
                    var next = knownLead[at];
                    if (string.IsNullOrEmpty(next)) break;
                    at = next;
                }
            }

            return keep;
        }

        private string? NextOf(string id) =>
            _knownLead.TryGetValue(id, out var lead) && !string.IsNullOrEmpty(lead) ? lead : null;
    }
}

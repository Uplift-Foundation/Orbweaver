using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    // CB-268 S1: the pure half of the group model. No settings, no windows, no
    // file — OrbweaverSettings owns one OrbGroupTable and wraps every mutation in
    // its lock and a Save, so everything that decides *which group an orb is in*
    // can be tested with a dictionary and nothing else, the same split OrbClusters
    // and OrbArrangement keep.
    //
    // This is a second file of the partial OrbGroups that OrbGroups.cs opened
    // with the contracts, rather than an edit to that file, so the shared
    // records and the logic built on them can change without colliding.
    internal static partial class OrbGroups
    {
        // The shapes the three predefined groups had before groups existed. They
        // are repeated here rather than read from OrbweaverSettings so that
        // Ordered, which is handed a dictionary and nothing else, can still
        // answer for a predefined group the dictionary does not hold.
        internal static readonly string[] PredefinedIds = { Main, Heartbeats, Crons };

        internal static bool IsPredefined(string? id) =>
            id == Main || id == Heartbeats || id == Crons;

        // The CB-14 classification, as a group id. An explicit membership wins
        // outright: an orb dragged out of the heartbeats cluster into `main` is
        // in main even though OrbClusters.Of still calls it a heartbeat, which is
        // what makes a drag stick. Everything else falls back to what the app
        // did before groups — a cluster the user gave its own shape is that
        // cluster's slot, anything else is main.
        //
        // `membership` is the *resolved* id — OrbGroupTable.MembershipFor has
        // already thrown out one naming a group that was deleted — so a blank is
        // the only "none" this needs to recognise.
        internal static string GroupIdFor(
            string? membership, OrbCluster cluster, ClusterMode heartbeats, ClusterMode crons)
        {
            if (!string.IsNullOrWhiteSpace(membership)) return membership;

            return OrbClusters.GroupOf(cluster, heartbeats, crons) switch
            {
                1 => Heartbeats,
                2 => Crons,
                _ => Main
            };
        }

        // Slot order is the group table's, never the sessions': main,
        // heartbeats, crons, then user groups by ordinal id. A predefined group
        // missing from the dictionary is synthesised rather than skipped, so
        // slots 0-2 can never shift under a caller that indexes by them — the
        // reason OrbClusters.GroupOf gives for fixed slots.
        internal static IReadOnlyList<OrbGroup> Ordered(IReadOnlyDictionary<string, OrbGroup> groups)
        {
            var ordered = new List<OrbGroup>(groups.Count + 3);
            foreach (var id in PredefinedIds)
                ordered.Add(groups.TryGetValue(id, out var group) ? group : DefaultFor(id));

            var userIds = new List<string>();
            foreach (var id in groups.Keys)
                if (!IsPredefined(id)) userIds.Add(id);
            userIds.Sort(StringComparer.Ordinal);
            foreach (var id in userIds) ordered.Add(groups[id]);

            return ordered;
        }

        internal static OrbGroup DefaultFor(string id) => id switch
        {
            Heartbeats => new OrbGroup(
                Heartbeats, "Heartbeats", OrbweaverSettings.DefaultOpenClawHeartbeatShape, null, null),
            Crons => new OrbGroup(
                Crons, "Crons", OrbweaverSettings.DefaultOpenClawCronShape, null, null),
            _ => new OrbGroup(Main, "Main", OrbweaverSettings.DefaultArrangeShape, null, null)
        };

        // "g-" and eight lowercase hex digits. Random rather than a counter: a
        // counter would hand a deleted group's id to the next one made, and a
        // membership left behind by an older build's file would then silently
        // attach to a stranger.
        internal static string NewUserGroupId() =>
            "g-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        // ---- value hygiene --------------------------------------------------

        // Opacity is a 0..1 fraction everywhere. NaN is "not a number" rather
        // than a number to clamp, so it reads as unset.
        internal static double? ClampOpacity(double? value) =>
            value is null || double.IsNaN(value.Value) ? null : Math.Clamp(value.Value, 0.0, 1.0);

        // The settings file's colour form: #RRGGBB, upper case, no alpha — alpha
        // is the opacity field's job. Accepts #RGB and #AARRGGBB on the way in
        // (the alpha is dropped, as OrbColors.Resolve drops it) and answers null
        // for anything else, so a hand-edited value costs that one colour.
        internal static string? NormalizeColor(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var hex = text.Trim();
            if (hex[0] != '#') return null;
            hex = hex[1..];

            foreach (var c in hex)
                if (!Uri.IsHexDigit(c)) return null;

            switch (hex.Length)
            {
                case 3:
                    return $"#{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}".ToUpperInvariant();
                case 6:
                    return "#" + hex.ToUpperInvariant();
                case 8:
                    return "#" + hex[2..].ToUpperInvariant();
                default:
                    return null;
            }
        }

        // A style with every field cleaned, or null when nothing is left to
        // override — an all-null style and no style mean the same thing, so only
        // one of them is ever stored.
        internal static GroupStyle? CleanStyle(GroupStyle? style)
        {
            if (style is null) return null;

            var clean = new GroupStyle(
                NormalizeColor(style.Stroke),
                ClampOpacity(style.StrokeOpacity),
                NormalizeColor(style.Fill),
                ClampOpacity(style.FillOpacity));

            return clean.Stroke is null && clean.StrokeOpacity is null
                && clean.Fill is null && clean.FillOpacity is null
                    ? null
                    : clean;
        }
    }

    // The group table and the membership map. Not thread-safe by itself —
    // OrbweaverSettings holds its Gate around every call, which is how every
    // other mutable thing in that class is protected.
    internal sealed class OrbGroupTable
    {
        private readonly Dictionary<string, OrbGroup> _groups = new(StringComparer.Ordinal);

        // Position key -> group id. OrdinalIgnoreCase for the reason OrbPositions
        // is: Windows paths are, and the same repo must not get two entries over
        // a capitalisation difference.
        private readonly Dictionary<string, string> _members = new(StringComparer.OrdinalIgnoreCase);

        internal OrbGroupTable()
        {
            foreach (var id in OrbGroups.PredefinedIds) _groups[id] = OrbGroups.DefaultFor(id);
        }

        internal IReadOnlyDictionary<string, OrbGroup> Groups => _groups;
        internal IReadOnlyDictionary<string, string> Members => _members;
        internal IReadOnlyList<OrbGroup> Ordered() => OrbGroups.Ordered(_groups);

        internal OrbGroup? Get(string? id) =>
            id is not null && _groups.TryGetValue(id, out var group) ? group : null;

        internal int MemberCount(string id)
        {
            var count = 0;
            foreach (var group in _members.Values)
                if (group == id) count++;
            return count;
        }

        // ---- membership -----------------------------------------------------

        // The explicit group for a key, or null. `fallbackKey` is the same
        // second chance OrbPositionFor gets from SessionManager — the bare
        // directory, tried when nothing is saved under the full key, so a retitle
        // or a membership written before titles were part of the key still
        // finds its group. The caller decides *whether* a fallback applies (it is
        // Claude Code only there) and passes null when it does not.
        //
        // A membership naming a group that no longer exists reads as none, so an
        // orb never lands in a slot that has no shape.
        internal string? MembershipFor(string? key, string? fallbackKey = null)
        {
            if (Resolve(key) is { } direct) return direct;
            return Resolve(fallbackKey);
        }

        private string? Resolve(string? key) =>
            !string.IsNullOrEmpty(key)
            && _members.TryGetValue(key, out var id)
            && _groups.ContainsKey(id)
                ? id
                : null;

        internal bool Move(string? key, string groupId) =>
            AssignTeam(key, Array.Empty<string?>(), groupId);

        // Writes one membership under the lead's key and every member's. A team
        // never ends up split across groups, and teammates that share a key
        // with the lead are harmless — they are in the same group anyway. Empty
        // keys are skipped: a session with no cwd has no stable identity to
        // remember. A group that loses its last member by this is removed.
        internal bool AssignTeam(string? leadKey, IEnumerable<string?> memberKeys, string groupId)
        {
            if (!_groups.ContainsKey(groupId)) return false;

            var changed = false;
            var vacated = new List<string>();

            void Write(string? key)
            {
                if (string.IsNullOrEmpty(key)) return;
                if (_members.TryGetValue(key, out var before))
                {
                    if (before == groupId) return;
                    vacated.Add(before);
                }

                _members[key] = groupId;
                changed = true;
            }

            Write(leadKey);
            foreach (var key in memberKeys) Write(key);

            foreach (var id in vacated) PruneIfEmpty(id);
            return changed;
        }

        // Takes the key out of explicit membership, so the CB-14 classification
        // decides again. Distinct from Move(key, Main), which pins the orb in
        // main even if it is a heartbeat.
        internal bool Unassign(string? key)
        {
            if (string.IsNullOrEmpty(key) || !_members.Remove(key, out var before)) return false;

            PruneIfEmpty(before);
            return true;
        }

        // Copied, never moved and never over an existing membership — the rule
        // OrbWindow.RefreshSoundKey states and for the same two reasons: two
        // orbs can share one key, so clearing the old one would pull the other
        // out of its group, and a membership already under the new key is a
        // choice somebody made. An orphaned entry under a key nothing looks up
        // is harmless; losing a group is not. Cannot empty a group, so never
        // prunes.
        internal bool CarryMembership(string? oldKey, string? newKey)
        {
            if (string.IsNullOrEmpty(newKey)
                || string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return false;
            if (Resolve(oldKey) is not { } id) return false;
            if (Resolve(newKey) is not null) return false;

            _members[newKey] = id;
            return true;
        }

        // A user group with nobody left in it is not kept: there is nowhere in
        // the UI to find an empty one, and its id would sit in the file forever.
        // The predefined three are slots, not collections of members, and stay.
        private void PruneIfEmpty(string id)
        {
            if (OrbGroups.IsPredefined(id) || !_groups.ContainsKey(id) || MemberCount(id) > 0) return;
            _groups.Remove(id);
        }

        // ---- groups ---------------------------------------------------------

        // A new, empty user group named "Group N" for the lowest N not already
        // taken, so deleting Group 2 and making another gives Group 2 back
        // rather than counting up for ever. It stays until someone is moved in
        // (a group made and never used is pruned only once a member leaves it).
        // `idSource` is a test seam for ids and collisions.
        internal OrbGroup Create(string? shape = null, Func<string>? idSource = null)
        {
            idSource ??= OrbGroups.NewUserGroupId;

            var id = idSource();
            while (_groups.ContainsKey(id)) id = idSource();

            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in _groups.Values) taken.Add(group.Name);

            var number = 1;
            while (taken.Contains("Group " + number.ToString(CultureInfo.InvariantCulture))) number++;

            var made = new OrbGroup(
                id,
                "Group " + number.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(shape) ? _groups[OrbGroups.Main].Shape : shape,
                null,
                null);
            _groups[id] = made;
            return made;
        }

        // User groups only; the three predefined ones are slots the arrangement
        // counts on. Members fall back to the classification, which puts a
        // regular orb in main.
        internal bool Delete(string id)
        {
            if (OrbGroups.IsPredefined(id) || !_groups.Remove(id)) return false;

            foreach (var key in _members.Where(pair => pair.Value == id).Select(pair => pair.Key).ToList())
                _members.Remove(key);
            return true;
        }

        internal bool Rename(string id, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Update(id, group => group with { Name = name.Trim() });
        }

        internal bool SetShape(string id, string? shape)
        {
            if (string.IsNullOrWhiteSpace(shape)) return false;
            return Update(id, group => group with { Shape = shape });
        }

        internal bool SetAnchor(string id, OrbweaverSettings.OrbPlacement? anchor) =>
            Update(id, group => group with { Anchor = anchor });

        internal bool SetStyle(string id, GroupStyle? style) =>
            Update(id, group => group with { Style = OrbGroups.CleanStyle(style) });

        private bool Update(string id, Func<OrbGroup, OrbGroup> change)
        {
            if (!_groups.TryGetValue(id, out var before)) return false;

            var after = change(before);
            if (after == before) return false;

            _groups[id] = after;
            return true;
        }

        // ---- settings.json --------------------------------------------------

        // Reads the orbGroups block over a table that already holds the
        // predefined groups built from the legacy keys — which is the
        // migration: with no block there is nothing to overlay and the legacy
        // values stand.
        //
        // With a block, the legacy keys still win for what they say. Every
        // predefined group's shape and the main group's anchor are written to
        // both places by Save, and an older build edits only the legacy ones, so
        // the legacy copy is the one that can be newer. Heartbeats and crons
        // have no legacy anchor, so theirs come from the block, and a name or
        // style has no legacy home at all.
        //
        // Tolerant to the last field: anything of the wrong type costs that one
        // entry, never the model — the reason Text() and Number() exist.
        internal void ReadJson(JsonNode? node)
        {
            if (node is not JsonObject block) return;

            if (block["groups"] is JsonObject groups)
            {
                foreach (var (id, entry) in groups)
                {
                    if (string.IsNullOrWhiteSpace(id) || entry is not JsonObject fields) continue;

                    var predefined = OrbGroups.IsPredefined(id);
                    var existing = _groups.GetValueOrDefault(id) ?? OrbGroups.DefaultFor(OrbGroups.Main) with { Id = id };

                    var name = Text(fields["name"]) ?? existing.Name;
                    var shape = predefined ? existing.Shape : Text(fields["shape"]) ?? existing.Shape;
                    var anchor = id == OrbGroups.Main ? existing.Anchor : Anchor(fields["anchor"]);
                    var style = Style(fields["style"]);

                    _groups[id] = new OrbGroup(id, name, shape, anchor, style);
                }
            }

            if (block["members"] is JsonObject members)
            {
                foreach (var (key, value) in members)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    if (Text(value) is { } id && _groups.ContainsKey(id)) _members[key] = id;
                }
            }
        }

        internal JsonObject ToJson()
        {
            var groups = new JsonObject();
            foreach (var group in Ordered())
            {
                var entry = new JsonObject { ["name"] = group.Name, ["shape"] = group.Shape };
                if (group.Anchor is { } anchor)
                    entry["anchor"] = new JsonObject { ["x"] = anchor.X, ["y"] = anchor.Y };
                if (group.Style is { } style)
                {
                    var styled = new JsonObject();
                    if (style.Stroke is not null) styled["stroke"] = style.Stroke;
                    if (style.StrokeOpacity is { } strokeOpacity) styled["strokeOpacity"] = strokeOpacity;
                    if (style.Fill is not null) styled["fill"] = style.Fill;
                    if (style.FillOpacity is { } fillOpacity) styled["fillOpacity"] = fillOpacity;
                    entry["style"] = styled;
                }

                groups[group.Id] = entry;
            }

            var members = new JsonObject();
            foreach (var (key, id) in _members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                members[key] = id;

            return new JsonObject { ["groups"] = groups, ["members"] = members };
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

        // Tries int as well as double: a node built in memory from an int holds
        // an int and refuses TryGetValue<double>, while one parsed from a file
        // answers either. Matters to a round trip in a test, not to a real file,
        // and costs nothing.
        private static double? Number(JsonNode? node) =>
            node is not JsonValue value ? null
            : value.TryGetValue<double>(out var number) ? number
            : value.TryGetValue<int>(out var small) ? small
            : null;

        private static OrbweaverSettings.OrbPlacement? Anchor(JsonNode? node)
        {
            if (node is not JsonObject anchor) return null;

            var x = Number(anchor["x"]);
            var y = Number(anchor["y"]);
            return x is null || y is null
                ? null
                : new OrbweaverSettings.OrbPlacement((int)Math.Round(x.Value), (int)Math.Round(y.Value));
        }

        private static GroupStyle? Style(JsonNode? node) =>
            node is not JsonObject style
                ? null
                : OrbGroups.CleanStyle(new GroupStyle(
                    Text(style["stroke"]), Number(style["strokeOpacity"]),
                    Text(style["fill"]), Number(style["fillOpacity"])));
    }
}

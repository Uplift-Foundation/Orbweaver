using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // What the app remembers of a Paperclip company between launches, so a key that
    // is refused (or a Paperclip that is unreachable) at the very first poll after a
    // restart still draws the company, dimmed, with the reason, instead of drawing
    // nothing. The product promise is "orbs dim and one line says Reconnect, never a
    // silent disappearance"; without a snapshot there was nothing to dim.
    //
    // Only what is needed to draw sleeping orbs: ids, names, roles, titles, who
    // reports to whom, and the issue prefix the page URLs are made of. Deliberately
    // not the key, a task, spend, an error reason or an adapter setting (which
    // carries local paths). It is a cache of a picture, not of Paperclip's data.
    internal sealed record RosterAgent(string Id, string Name, string Role, string Title, string? ReportsTo);

    internal sealed record RosterFile(string Url, string CompanyId, string IssuePrefix, DateTime SavedUtc,
        List<RosterAgent> Agents);

    // The seam. The app's one is a private file beside the settings
    // (FileRosterStore); the suites hand over an in-memory one or a scratch directory.
    internal interface IRosterStore
    {
        RosterFile? Load();
        void Save(RosterFile roster);
        void Delete();
    }

    internal sealed class NullRosterStore : IRosterStore
    {
        public RosterFile? Load() => null;
        public void Save(RosterFile roster) { }
        public void Delete() { }
    }

    internal static class PaperclipRoster
    {
        internal static string Normalise(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

        internal static RosterFile FromLive(string baseUrl, string companyId, string issuePrefix,
            IReadOnlyList<PaperclipAgent> agents, DateTime now) =>
            new(Normalise(baseUrl), companyId, issuePrefix, now,
                agents.Select(a => new RosterAgent(a.Id, a.Name, a.Role, a.Title, a.ReportsTo)).ToList());

        // What decides whether the file needs rewriting: everything but the time.
        internal static string Signature(RosterFile f) =>
            f.Url + "\n" + f.CompanyId + "\n" + f.IssuePrefix + "\n"
            + string.Join("\n", f.Agents.Select(a => $"{a.Id}|{a.Name}|{a.Role}|{a.Title}|{a.ReportsTo}"));

        // The file's rows as sleeping orbs: idle, no mark, no ring, nothing waiting.
        // The scan parks them and says why. LastActivity is when the picture was
        // saved, never now, so the stamp stays a true claim about the data.
        internal static IReadOnlyList<PaperclipAgent> Rows(RosterFile f, string baseUrl) =>
            f.Agents.Select(a => new PaperclipAgent(
                a.Id, a.Name, a.Role, a.Title, "idle", a.ReportsTo, "",
                "idle", PaperclipMark.None, null, Array.Empty<PaperclipNeed>(), null, null, null, null, null,
                f.SavedUtc, PaperclipApi.AgentUrl(baseUrl, f.IssuePrefix, a.Id), true)).ToList();

        // The rows to draw before the first successful read, if the file is for this
        // address and company. A file for another is stale and is removed: switching
        // company or address forgets the old picture.
        internal static IReadOnlyList<PaperclipAgent> Remembered(
            IRosterStore store, string baseUrl, string companyId, out RosterFile? file)
        {
            file = null;
            RosterFile? loaded;
            try { loaded = store.Load(); }
            catch { return Array.Empty<PaperclipAgent>(); }
            if (loaded is null || loaded.Agents is null) return Array.Empty<PaperclipAgent>();

            if (loaded.Url != Normalise(baseUrl) || loaded.CompanyId != companyId || string.IsNullOrEmpty(companyId))
            {
                try { store.Delete(); } catch { }
                return Array.Empty<PaperclipAgent>();
            }

            // A row with no id cannot be drawn or keyed; the rest are kept.
            loaded = loaded with { Agents = loaded.Agents.Where(a => a is not null && !string.IsNullOrEmpty(a.Id)).ToList() };
            file = loaded;
            return Rows(loaded, baseUrl);
        }
    }
}

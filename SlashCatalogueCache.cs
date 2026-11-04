using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // The slash catalogue for a (CLI, cwd), read through OffThreadCache so no
    // caller waits on SlashCommandCatalog's walk of the cwd and every parent
    // (CB-306's second reopen — OffThreadCache has the account of the freeze).
    //
    // The stand-in is the CLI's built-ins, not nothing: the near side treats a
    // name as a command only if the roster lists it, so an empty list would
    // send "/compact" to the far model as prose.
    internal sealed class SlashCatalogueCache
    {
        internal sealed record Seams(
            // The real scan. May block for ever; may throw.
            Func<SessionSource, string, IReadOnlyList<SlashCommand>> Scan,
            // What to answer before a scan has: no IO, ever.
            Func<SessionSource, IReadOnlyList<SlashCommand>> Builtins,
            Func<DateTime> Now,
            // Runs the scan somewhere that is not the caller.
            Action<Action> StartWorker);

        // How long a scanned catalogue is trusted before a refresh is started
        // behind it. A user who adds a skill sees it within this much of the
        // next ask; the old code rescanned on every hello (ten seconds), which
        // was disk IO for an answer that almost never changes.
        internal static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(1);

        internal const int MaxInFlight = OffThreadCache<(SessionSource, string), IReadOnlyList<SlashCommand>>.MaxInFlight;

        internal readonly record struct Lookup(IReadOnlyList<SlashCommand> Commands, bool Scanned);

        private readonly OffThreadCache<(SessionSource Source, string Cwd), IReadOnlyList<SlashCommand>> _cache;

        public SlashCatalogueCache(Seams seams) =>
            _cache = new(new(
                key => seams.Scan(key.Source, key.Cwd),
                key => seams.Builtins(key.Source),
                seams.Now,
                seams.StartWorker), RefreshAfter);

        internal static readonly SlashCatalogueCache Shared = new(new Seams(
            SlashCommandCatalog.For,
            SlashCommandCatalog.BuiltinsFor,
            () => DateTime.UtcNow,
            OffThread.OnOwnThread));

        internal int InFlight => _cache.InFlight;

        // Never waits. What the roster path uses.
        public IReadOnlyList<SlashCommand> Peek(SessionSource source, string? cwd) =>
            For(source, cwd, TimeSpan.Zero).Commands;

        public Lookup For(SessionSource source, string? cwd, TimeSpan wait)
        {
            // Codex and Grok read only the home directory, so their catalogue
            // does not depend on the cwd and one key serves every session.
            var key = (source, source is SessionSource.Codex or SessionSource.Grok ? "" : cwd ?? "");
            var found = _cache.For(key, wait);
            return new Lookup(found.Value, found.Read);
        }

        // For a caller that keeps its own copy (a local panel, which used to
        // scan once per session): `kept` is filled only with a real scan's
        // answer, so a stand-in is never remembered in place of one.
        internal IReadOnlyList<SlashCommand> Remembered(
            ref IReadOnlyList<SlashCommand>? kept, SessionSource source, string? cwd, TimeSpan wait)
        {
            if (kept is not null) return kept;
            var found = For(source, cwd, wait);
            if (found.Scanned) kept = found.Commands;
            return found.Commands;
        }
    }
}

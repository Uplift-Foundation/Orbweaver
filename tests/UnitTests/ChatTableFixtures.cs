namespace Orbweaver.Tests;

// Real table output, for the CB-289 tests, copied byte for byte.
//
// BoxUpdateStatus is the seven lines of `openclaw update status`, as they sit
// in a Claude Code project transcript for ~/.openclaw (a tool result) on the
// Mac mini: 120 columns wide, with a U+00B7 in a cell. BoxTruncated is rows of
// a real `openclaw status` table that was cut off with no bottom border (rows
// that carry an address, hostname or pid are left out; every row kept is
// verbatim). AsciiSqlite is `sqlite3 3.54 .mode table` output.
//
// There is deliberately no pipe-less GFM fixture here: no real sample of one
// exists on that machine, so the tests use the GFM spec's own example and say
// so. Nor is any of this a model's reply — every real box table found was CLI
// output in a tool result, which is the provenance to claim and no more.
internal static class ChatTableFixtures
{
    internal static readonly string[] BoxUpdateStatus = new[]
    {
        "┌──────────┬───────────────────────────────────────────────────────────────────────────────────────────────────────────┐",
        "│ Item     │ Value                                                                                                     │",
        "├──────────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────┤",
        "│ Install  │ npm                                                                                                       │",
        "│ Channel  │ stable (default)                                                                                          │",
        "│ Update   │ available · npm · npm update 2026.10.5                                                                    │",
        "└──────────┴───────────────────────────────────────────────────────────────────────────────────────────────────────────┘"
    };

    internal static readonly string[] BoxTruncated = new[]
    {
        "┌──────────────────────┬───────────────────────────────────────────────────────────────────────────────────────────────┐",
        "│ Item                 │ Value                                                                                         │",
        "├──────────────────────┼───────────────────────────────────────────────────────────────────────────────────────────────┤",
        "│ OS                   │ macos 27.0.1 (arm64) · node 26.7.0                                                            │",
        "│ Tailscale exposure   │ off                                                                                           │",
        "│ Channel              │ stable (default)                                                                              │",
        "│ Update               │ npm                                                                                           │",
        "│ Node service         │ LaunchAgent not installed                                                                     │",
        "│ Agents               │ 8 · no workspaces bootstrapping · sessions 2029 · default main active 3m ago                  │"
    };

    internal static readonly string[] AsciiSqlite = new[]
    {
        "+----+------------+",
        "| id |    name    |",
        "+----+------------+",
        "|  1 | alpha      |",
        "|  2 | beta gamma |",
        "+----+------------+"
    };

    internal static string Join(string[] lines) => string.Join("\n", lines);
}

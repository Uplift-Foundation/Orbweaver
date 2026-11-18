using Orbweaver;

// Drives the peer link from a terminal: connect to a paired machine, ask what
// it has, and fetch one session's transcript.
//
// **This exists because the panel is the only other way to ask, and a panel
// needs a mouse, an unlocked screen and a person.** The plan that replaced the
// relay called for it in as many words — "what makes the far side diagnosable
// without a mouse" — and it replaces tools/mirror-probe, which drove the relay
// and went with it.
//
// It runs the *shipped* PeerLink and the *shipped* RemoteMirrorClient rather
// than a second copy of either. A probe that spoke the protocol its own way
// could answer a question the app would answer differently, which is the one
// thing a diagnostic must never do.
//
// Two things to know before reading the output.
//
// **It uses this machine's identity**, so it only reaches machines this Buddy
// has already paired with — it cannot pair, and it is not a way around the
// pairing code. Running it beside a live Buddy is fine: both present the same
// certificate, and the far side keeps ONE connection per machine name:
// PeerLink.Adopt replaces whatever it already holds under that name. So a probe
// run *evicts the live app from the link*, and anything the app pushes in that
// window is lost (CB-306: four replacements on the mini, then five
// peer-send-no-link). The probe therefore refuses to run while the app holds
// its single-instance claim, unless given --evict.
//
// It logs to mirror-log-probe.txt, never to the app's mirror-log.txt.
//
// **On macOS it is subject to Local Network consent**, and a terminal usually
// has none where the signed app does. A probe that cannot connect while the app
// plainly can is telling you about the terminal, not the network — see CB-38,
// and the note in CLAUDE.md about ping and ssh being exempt and therefore
// useless as evidence here.
internal static class Program
{
    private static async Task<int> Main(string[] rawArgs)
    {
        MirrorLog.UseFile(MirrorLog.ProbeFileName);

        var (evict, args) = PeerProbeGuard.SplitEvict(rawArgs);

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: peer-probe [--evict] <host[:port]> <machine-name> [session-name]\n" +
                "       peer-probe [--evict] <host[:port]> <machine-name> " + PeerProbeFile.Usage.Replace("\n", "\n       ") + "\n" +
                "\n" +
                "  host          where to dial, e.g. 198.51.100.10 or mini:7677\n" +
                "  machine-name  what that machine calls itself, as paired\n" +
                "  session-name  optional; fetch this session's transcript\n" +
                "  --evict       run even though Orbweaver is running. This DISCONNECTS the\n" +
                "                running app from that peer until it reconnects.\n" +
                "\n" +
                "With no session name it prints the roster and stops.");
            return 2;
        }

        var address = PeerSessions.Address(args[0], PeerLink.DefaultPort);
        if (address is null)
        {
            Console.Error.WriteLine($"not an address: {args[0]}");
            return 2;
        }

        var verdict = PeerProbeGuard.Decide(PeerProbeGuard.AppIsRunning(), evict);
        if (verdict.Message is not null) Console.Error.WriteLine(verdict.Message);
        if (!verdict.Run) return 3;

        var machine = args[1];

        // `read_file` is a command rather than a session name: a session is
        // never called that, and a flag would make the common call longer.
        PeerProbeFile.Request? fileRequest = null;
        if (args.Length > 2 && args[2] == "read_file")
        {
            var parsed = PeerProbeFile.Parse(args[3..]);
            if (parsed.Request is null)
            {
                Console.Error.WriteLine($"{parsed.Error}\n\n{PeerProbeFile.Usage}");
                return 2;
            }

            fileRequest = parsed.Request;
        }

        var session = fileRequest is null && args.Length > 2 ? args[2] : null;

        if (PeerIdentity.PeerFor(machine) is null)
        {
            Console.Error.WriteLine(
                $"not paired with '{machine}'. This probe uses this machine's own identity " +
                "and cannot pair — do that in Settings, or with the pair-open file.");
            return 2;
        }

        using var host = new PeerMirrorHost();
        host.Serve(() => PeerRosterDirs.Configured(), RemoteControlSessions.LocalSessions);

        var client = host.Client!;

        Console.WriteLine($"dialling {address.Value.Host}:{address.Value.Port} as {machine}…");

        var connected = await host.Link.ConnectAsync(
            machine, address.Value.Host, address.Value.Port,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        if (!connected)
        {
            Console.Error.WriteLine(
                "could not connect. On macOS check Local Network access for this terminal " +
                "before suspecting the network — a failing probe beside a working app is " +
                "the terminal's permission, not a fault.");
            return 1;
        }

        Console.WriteLine("connected; asking what it has…");

        await client.AskWhatTheyHaveAsync(new[] { machine });

        var known = client.Known();

        if (fileRequest is { } file) return await ReadFileAsync(client, machine, file);

        if (known.Count == 0)
        {
            Console.WriteLine("roster: nothing offered.");
            return 1;
        }

        Console.WriteLine($"roster: {known.Count} session(s)");
        foreach (var (peer, entry) in known)
        {
            Console.WriteLine(
                $"  {entry.Name}  cli={entry.Cli}  transcript={entry.HasTranscript}  " +
                $"pane={entry.HasPane}  status={entry.Status ?? "(none)"}  via={peer}");
        }

        if (session is null) return 0;

        Console.WriteLine($"\nfetching {session}…");

        // Subscribed before asking, because the turns arrive on this event
        // rather than as a return value — the panel paints from here too, so
        // reading them anywhere else would be reading something the panel does
        // not use.
        // A record struct, so this is Nullable<MirrorRows> — "nothing arrived"
        // and "an empty window arrived" are different answers and worth keeping
        // apart.
        RemoteMirrorClient.MirrorRows? rows = null;
        client.Delivered += delivered => { rows = delivered; };

        var started = DateTime.UtcNow;
        var opened = await client.OpenAsync(session);
        var took = DateTime.UtcNow - started;

        if (!opened)
        {
            Console.Error.WriteLine($"no live view of {session} after {took.TotalMilliseconds:0}ms");
            return 1;
        }

        var state = client.StateFor(session);

        Console.WriteLine(
            $"opened in {took.TotalMilliseconds:0}ms — availability {state.Availability}");

        if (rows is null)
        {
            Console.Error.WriteLine("opened, but no turns were delivered.");
            return 1;
        }

        Console.WriteLine($"{rows.Value.Turns.Count} turn(s) parsed from the far transcript");

        // The ends rather than the middle: enough to see it is a real
        // conversation and in the right order, without printing somebody's
        // whole session into a terminal.
        foreach (var turn in rows.Value.Turns.Take(2).Concat(rows.Value.Turns.TakeLast(2)))
        {
            var text = turn.Text.Replace('\n', ' ');
            if (text.Length > 100) text = text[..100] + "…";

            Console.WriteLine($"  {turn.Role,-9} {text}");
        }

        return 0;
    }

    // One FILE round trip, printed. Excluded from the unit suites by being a
    // tool: its decisions (arguments, wording, exit code) are PeerProbeFile's.
    private static async Task<int> ReadFileAsync(RemoteMirrorClient client, string machine, PeerProbeFile.Request file)
    {
        if (!client.SpeaksFiles(file.Session))
        {
            Console.Error.WriteLine(
                $"'{file.Session}' is not offered by {machine}, or {machine} does not serve files " +
                "(it needs a build with CB-363).");
            return 1;
        }

        Console.WriteLine($"fetching a file from {file.Session}…");

        var started = DateTime.UtcNow;
        var result = await client.FetchFileAsync(file.Session, file.Path, file.Have);
        Console.WriteLine($"took={(DateTime.UtcNow - started).TotalMilliseconds:0}ms");

        foreach (var line in PeerProbeFile.Describe(result)) Console.WriteLine(line);

        if (result is PeerFileResult.Bytes bytes)
        {
            var path = file.OutPath ?? PeerProbeFile.DefaultOutPath(Path.GetTempPath(), bytes.Data);
            await File.WriteAllBytesAsync(path, bytes.Data);

            // Never the far side's execute bit: what arrives is data.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Console.WriteLine($"wrote={path}");
        }

        return PeerProbeFile.ExitCodeOf(result);
    }
}

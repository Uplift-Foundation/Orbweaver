using System.Text;
using Orbweaver;
using Orbweaver.ScreenReadProbe;

// Reads a terminal's visible screen the way the app would, then asks the shipped
// parser what it makes of it. CB-313.
//
//   probe <reader> <target> [--out <file>]
//   probe send <pid> <text> [--paste] [--enter]      (Windows only: type into a console)
//   readers: tmux | terminal | iterm2 | wezterm | kitty | wincon | file
//
// Output is `lines=N`, `screen=<PaneScreen word>/<check>`, then the same
// title / [k] label lines tests/TranscriptTests prints, so a cell can be
// re-derived with either tool. The capture is written UTF-8, no BOM, LF.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: probe <tmux|terminal|iterm2|wezterm|kitty|wincon|file> <target> [--out <file>]");
    return 2;
}

// probe send <pid> <text> [--paste] [--enter]: drive a Windows console's CLI.
if (args[0] == "send")
{
    var n = WindowsConsole.Type(int.Parse(args[1]), args[2].StartsWith("--") ? "" : args[2], args.Contains("--paste"), args.Contains("--enter"));
    Console.WriteLine($"sent {n} records");
    return 0;
}

string? outPath = null;
for (var i = 2; i + 1 < args.Length; i++)
    if (args[i] == "--out") outPath = args[i + 1];

string screen;
try
{
    screen = args[0] switch
    {
        "tmux" => Tmux.Read(args[1]),
        "terminal" => TerminalApp.Read(args[1]),
        "iterm2" => ITerm2.Read(args[1]),
        "wezterm" => WezTerm.Read(args[1]),
        "kitty" => Kitty.Read(args[1]),
        "wincon" => WindowsConsole.Read(int.Parse(args[1])),
        "file" => File.ReadAllText(args[1]),
        _ => throw new ArgumentException("unknown reader " + args[0]),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine($"read failed: {e.GetType().Name}: {e.Message}");
    return 1;
}

// LF only, whatever the reader gave: the fixtures are LF (.gitattributes), and
// the parser is separately pinned to read CRLF the same.
screen = screen.Replace("\r\n", "\n").Replace('\r', '\n');
if (outPath is not null) File.WriteAllText(outPath, screen, new UTF8Encoding(false));

var (reading, check) = PaneScreen.Read(screen);
Console.WriteLine($"lines={ChatTranscript.ScreenLines(screen).Length}");
Console.WriteLine($"screen={reading}/{check}");

var dialog = ChatTranscript.ParseDialog(screen);
if (dialog is null)
{
    Console.WriteLine("no dialog — the panel would offer the terminal instead");
    return 0;
}

Console.WriteLine($"title: {dialog.Title}");
foreach (var o in dialog.Options) Console.WriteLine($"  [{o.Key}] {o.Label}");
return 0;

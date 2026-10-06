using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

internal static class Program
{
    internal static string Version => typeof(Program).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";

    private const string Help = """
        atlascli: USB Atlas on the command line. Reads this PC's USB controllers, hubs, ports and
        devices, and explains problems in plain words. Read-only; no administrator rights needed.

        Usage: atlascli <command> [arguments] [options]

        Commands
          issues                 Every issue, most severe first, with what it means, whether it affects
                                 anything now, the likely cause and what to do. Start here.
                                 --min note|warning|error leaves out less severe ones.
          tree                   The topology as an indented tree: path, name, kind, link rate, polling
                                 rate, power, VID:PID and issues. --ports lists every empty port.
          show <target>          Everything about one node: identity, driver, link, socket, power,
                                 bandwidth, power saving, the chain to the host, its neighbors, its issues
                                 explained, and the evidence behind them.
          find <text>            Search names, VID:PID, types, paths, sockets ("USB-C"), polling rates
                                 ("1000 Hz"), HID collections ("Joystick") and issue names.
          budget [<target>]      Bandwidth and power arithmetic with its inputs. Without a target: every
                                 hub and anything with a budget issue.
          raw <target>           The node's USB descriptors, decoded field by field with their hex.
          displays               Graphics adapters and whether each has a driver, every monitor Windows has
                                 known with the adapter it was last shown through, and USB-C displays whose
                                 USB is connected but whose picture isn't. Start here when a monitor is dark.
          events                 Recent events from the Windows logs: USB devices and drivers, USB-C
                                 controller (UCSI) failures, displays and graphics drivers (installed,
                                 disabled, reset), and restarts a program started. --since 24h (default),
                                 --max 100, --errors for critical, error and warning events only,
                                 --usb-only for USB events only.
          watch                  Report devices connecting, disconnecting and changing as it happens, and
                                 devices that drop and come back. When the computer sleeps and wakes, it
                                 reports what didn't come back or came back slower. --for 60s (default; 0
                                 runs until Ctrl+C), --verbose for every Windows notification, --out FILE
                                 to also append each event to FILE as a line of JSON, --trace to add the
                                 USB hub driver's own events (see trace).
          trace                  Record what Windows' USB hub driver reports, placed on the topology:
                                 connections, port and warm resets, USB 3 link failures, overcurrent,
                                 enumeration retries and failures, rejected descriptors, SuperSpeed
                                 devices on the USB 2 bus, U1/U2 refused, USB-C alternate modes. Then a
                                 count per port. --for 30s (default; 0 runs until Ctrl+C), --verbose for
                                 every step and its fields, --out FILE as in watch. Needs administrator
                                 rights or membership in the Performance Log Users group.
          scan                   The full snapshot as JSON. --out FILE saves it; --raw includes descriptors.
          diff <before> [<after>]  What changed between two saved snapshots, or between one and now.
          map                    This machine's port map as JSON: each host controller's ports as the
                                 firmware describes them to Windows (what each speaks, whether it can be
                                 plugged into, USB-C, debug capable, and the other half of its socket) and
                                 what is wired in. --out FILE saves it; --devices also expects what is
                                 plugged in now, for a test fixture.
          check [<map>]          Check the firmware's port map for contradictions and, given a map saved
                                 from a known-good unit, that this machine matches it. A field deleted
                                 from the map isn't checked.
          mcp                    Serve the commands as Model Context Protocol tools on stdin/stdout.
          self-test              Run the built-in checks.
          version                Print the version.

        Targets
          A path such as H01/04/02 (host 1, port 4, port 2, as tree shows them), a VID:PID such as
          046D:C52B, a Windows instance ID, or words from the name. An ambiguous target lists matches.

        Options
          --json                 JSON instead of text (same content). Also --format json|text.
          --input FILE           Read a saved snapshot (from scan, or Export in the app) instead of scanning.
          --demo                 Use the app's sample topology instead of scanning.
          --redact               Replace serial numbers with stable hashes, for sharing output.

        Exit codes
          0  Success; for issues, nothing worse than notes.
          1  issues found warnings; check found a mismatch or a port map finding.
          2  issues found errors.
          3  Failure: bad arguments, a target that matches nothing or several things, a file that won't
             load, or a scan Windows refused.

        Examples
          atlascli issues
          atlascli show H01/02 --json
          atlascli scan --out before.json   (change something)   atlascli diff before.json
          atlascli watch --for 30s
          atlascli map --out board.json   (on another unit)   atlascli check board.json
          claude mcp add usb-atlas -- "C:\path\to\atlascli.exe" mcp
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        if (args.Length > 0 && args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
        {
            try { Options.Parse(args); }
            catch (CliException ex) { Console.Error.WriteLine("atlascli: " + ex.Message); return 3; }
            return Mcp.Serve(args.Skip(1).ToList(), Console.In, Console.Out, cancel.Token);
        }
        return Execute(args, Console.Out, Console.Error, cancel.Token);
    }

    // Runs one command, writing its report to output and problems to error. The MCP tools call this too.
    internal static int Execute(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancel)
    {
        try
        {
            var o = Options.Parse(args);
            if (o.Has("help") || o.Command is "" or "help") { output.Write(Help.Replace("\n", Environment.NewLine) + Environment.NewLine); return 0; }
            string target = string.Join(' ', o.Positional).Trim();
            void Print(JsonObject report) => output.Write(o.Json ? Json.Write(report) + Environment.NewLine : TextOut.Render(report));
            switch (o.Command)
            {
                case "version" or "--version":
                    output.WriteLine("atlascli " + Version); return 0;
                case "issues":
                {
                    var s = Session.Open(o);
                    Print(Reports.Issues(s, Reports.ParseSeverity(o.Get("min")) ?? Severity.Note));
                    return Reports.HealthCode(s);
                }
                case "tree":
                    Print(Reports.Tree(Session.Open(o), o.Has("ports"))); return 0;
                case "show":
                {
                    var s = Session.Open(o);
                    Print(Reports.Show(s, s.Resolve(Need(target, "show <target>")))); return 0;
                }
                case "find":
                    Print(Reports.Find(Session.Open(o), Need(target, "find <text>"))); return 0;
                case "budget":
                {
                    var s = Session.Open(o);
                    Print(Reports.Budget(s, target.Length > 0 ? s.Resolve(target) : null)); return 0;
                }
                case "raw":
                {
                    var s = Session.Open(o, raw: true);
                    Print(Descriptors.Report(s, s.Resolve(Need(target, "raw <target>")))); return 0;
                }
                case "displays":
                    Print(Reports.Displays(Session.Open(o))); return 0;
                case "events":
                {
                    if (!OperatingSystem.IsWindows()) throw new CliException("events reads the Windows event logs. On Linux, the kernel's USB messages are in journalctl -k.");
                    var s = Session.Open(o);
                    Print(EventLog.Report(s, o.Duration("since", TimeSpan.FromHours(24)), o.Int("max", 100, 1, 10000), o.Has("errors"), o.Has("redact"), o.Has("usb-only"))); return 0;
                }
                case "scan":
                {
                    var s = Session.Open(o, raw: o.Has("raw"));
                    string json = JsonSerializer.Serialize(s.Snapshot, Json.Options);
                    if (o.Get("out") is string file)
                    {
                        Save(file, json);
                        output.WriteLine($"Saved {s.Snapshot.Nodes.Count(n => n.Kind == "Device")} devices and {s.Snapshot.Nodes.Count(n => n.Kind == "Hub")} hubs to {file}.");
                    }
                    else output.WriteLine(json);
                    return 0;
                }
                case "map":
                {
                    var map = PortMapFile.Export(Session.Open(o), o.Has("devices"));
                    if (o.Get("out") is string file)
                    {
                        Save(file, Json.Write(map));
                        output.WriteLine($"Saved the port map of {map["controllers"]!.AsArray().Count} controllers and {PortMapFile.PortCount(map)} ports to {file}. Check a machine against it with atlascli check {file}.");
                    }
                    else output.WriteLine(Json.Write(map));
                    return 0;
                }
                case "check":
                {
                    if (o.Positional.Count > 1) throw new CliException("check takes one port map file, or none to check only that the port map is consistent: check [<map.json>].");
                    var map = o.Positional.Count == 1 ? PortMapFile.Load(o.Positional[0]) : null;
                    var report = PortMapFile.Check(Session.Open(o), map, o.Positional.Count == 1 ? Path.GetFileName(o.Positional[0]) : null);
                    Print(report);
                    return report["result"]!.ToString() == "pass" ? 0 : 1;
                }
                case "diff":
                {
                    if (o.Positional.Count is 0 or > 2) throw new CliException("diff takes one or two snapshot files: diff <before.json> [<after.json>]. With one, it compares with a scan now.");
                    if (o.Has("input") && o.Positional.Count == 2) throw new CliException("diff compares two files, or one file with --input or a scan now; not three.");
                    var before = new Session(Session.LoadFile(o.Positional[0]), Path.GetFileName(o.Positional[0]));
                    var after = o.Positional.Count == 2 ? new Session(Session.LoadFile(o.Positional[1]), Path.GetFileName(o.Positional[1])) : Session.Open(o);
                    if (o.Has("redact")) { before = new(Session.Redact(before.Snapshot), before.Source); if (o.Positional.Count == 2) after = new(Session.Redact(after.Snapshot), after.Source); }
                    var diff = Diff.Compare(before, after);
                    output.Write(o.Json ? Json.Write(diff) + Environment.NewLine : Diff.Text(diff));
                    return 0;
                }
                case "watch" or "trace":
                {
                    // --out keeps every event as a line of JSON, whatever the console shows. Hub driver events
                    // arrive on a thread of their own, so lines are written one at a time.
                    using var log = o.Get("out") is string logFile ? OpenLog(logFile) : null;
                    var gate = new object();
                    void Emit(JsonObject e)
                    {
                        lock (gate)
                        {
                            output.Write(o.Json ? Json.Write(e, false) + Environment.NewLine : UsbTrace.Text(e)); output.Flush();
                            log?.WriteLine(Json.Write(e, false)); log?.Flush();
                        }
                    }
                    var duration = o.Duration("for", TimeSpan.FromSeconds(o.Command == "trace" ? 30 : 60));
                    Emit(o.Command == "trace" ? UsbTrace.Run(o, duration, o.Has("verbose"), Emit, cancel) : Watch.Run(o, duration, o.Has("verbose"), o.Has("redact"), Emit, cancel));
                    return 0;
                }
                case "self-test":
                    try { SelfTests.Run(); CliTests.Run(); }
                    catch (Exception ex) { error.WriteLine("Self-test failed: " + ex); return 3; }
                    output.WriteLine("All checks passed.");
                    return 0;
                case "mcp":
                    throw new CliException("mcp must be the first argument: atlascli mcp [--demo | --input FILE] [--redact].");
                default:
                    throw new CliException($"Unknown command “{o.Command}”. Run atlascli help.");
            }
        }
        catch (CliException ex) { error.WriteLine("atlascli: " + ex.Message); return 3; }
        // Anything else is a bug or hardware Windows describes unexpectedly; it still exits 3 with a message,
        // and the MCP server keeps serving.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error.WriteLine($"atlascli: {ex.GetType().Name}: {ex.Message}"); return 3;
        }
    }

    private static void Save(string file, string text)
    {
        try { File.WriteAllText(file, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new CliException($"Can't write {file}: {ex.Message}"); }
    }
    private static StreamWriter OpenLog(string file)
    {
        try { return new StreamWriter(file, true, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new CliException($"Can't write {file}: {ex.Message}"); }
    }
    private static string Need(string value, string usage) => value.Length > 0 ? value : throw new CliException("Usage: atlascli " + usage);
}

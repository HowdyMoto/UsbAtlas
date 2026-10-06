using System.Text;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// What Windows' USB 3 hub driver (USBHUB3, which also runs USB 2 ports on xHCI controllers) logs through
// ETW, placed on the topology and said in plain words: link states and resets from each port's status,
// enumeration retries and failures, descriptors Windows rejected, SuperSpeed devices that came up on the
// USB 2 bus, low-power link states refused, and USB-C alternate modes. Its rundown, which the provider
// sends when the session starts, says where each hub and device it names sits: the host controller's
// PCI address and the ports on the way.
internal sealed class UsbTrace(Func<Session> session)
{
    internal static readonly Guid Hub3 = new("AC52AD17-CC01-4F85-8DF5-4DCE4333C99B");
    // Default (errors and port status), Enumeration and Rundown; not the bus traces or the state machine.
    internal const ulong Keywords = 0x1 | 0x400 | 0x8000;

    private readonly Dictionary<ulong, (string Pci, List<int> Ports)> places = [];
    // Ports where Windows has started setting up a device and not finished: a device being set up gets a
    // handle of its own before any event says where it is, and a failed attempt may name none.
    private readonly Dictionary<string, (string Pci, List<int> Ports)> enumerating = [];
    private readonly Dictionary<string, (ulong Status, ulong Change)> lastStatus = [];
    private readonly Dictionary<string, string> names = [];
    internal readonly Dictionary<string, Dictionary<string, int>> Counts = [];
    internal int Seen { get; private set; }
    private static string Key((string Pci, List<int> Ports) place) => place.Pci + "/" + string.Join("/", place.Ports);

    // Where the hubs and devices the provider names sit: the controller's PCI address as bus:device.function
    // and the port path from its root hub, from rundown and new-device events, and from any event that names
    // a device with its hub and port.
    internal void Learn(EtwEvent e)
    {
        ulong? port = e.U("fid_PortNumber");
        if (e.U("fid_PciBus") is ulong bus && e.U("fid_PciDevice") is ulong dev && e.U("fid_PciFunction") is ulong fn)
        {
            string pci = $"{bus:X2}:{dev:X2}.{fn:X}";
            if (e.U("fid_UsbDevice") is ulong device && e.U("fid_PortPathDepth") is ulong depth && depth is > 0 and <= 6)
            {
                var ports = e.List("fid_PortPath").Take((int)depth).Select(p => (int)p).ToList();
                if (ports.Count == (int)depth) places[device] = (pci, ports);
                if (e.U("fid_HubDevice") is ulong hub && depth == 1) places.TryAdd(hub, (pci, []));
            }
            // A root hub's port: the root hub's handle and its controller.
            else if (e.Id is 7 or 8 or 25 or 26 && e.U("fid_UsbDevice") is ulong root) places.TryAdd(root, (pci, []));
        }
        else if (e.U("fid_HubDevice") is ulong hub && e.U("fid_UsbDevice") is ulong device && device != 0 && port is > 0 && places.TryGetValue(hub, out var h))
            places[device] = (h.Pci, [.. h.Ports, (int)port]);
        // 60 starts setting up the device on a hub's port; 61 finishes, and 63 and 64 give up.
        if (e.Id == 60 && e.U("fid_UsbDevice") is ulong starting && port is > 0 && places.TryGetValue(starting, out var at))
        {
            var place = (at.Pci, new List<int>([.. at.Ports, (int)port]));
            enumerating[Key(place)] = place;
        }
        else if (e.Id is 61 or 63 or 64 && Place(e) is { } done) enumerating.Remove(Key(done));
        // A port whose connection changed to nothing connected.
        else if (e.Id is 100 or 101 && ((e.U("fid_PortChange") ?? 0) & 1) != 0 && ((e.U("fid_PortStatus") ?? 0) & 1) == 0 && Place(e) is { } gone) Forget(gone);
    }

    // The port an event is about, as the host and the ports on the way: a hub's handle and port number, a
    // device's handle, the one device Windows is setting up, or the one device with the VID:PID it names.
    internal (string Pci, List<int> Ports)? Place(EtwEvent e)
    {
        ulong? port = e.U("fid_PortNumber");
        if (e.U("fid_HubDevice") is ulong hub && port is > 0 && places.TryGetValue(hub, out var h)) return (h.Pci, [.. h.Ports, (int)port]);
        // What goes wrong while a device is set up belongs to the port being set up, whatever handle it names;
        // so does any event naming a handle nothing has placed while one device is being set up.
        bool known = e.U("fid_UsbDevice") is ulong handle && places.ContainsKey(handle);
        if ((SetUpFailure(e.Id) || !known) && e.Id is not (61 or 63 or 64) && enumerating.Count == 1)
        {
            var only = enumerating.Values.First();
            if (e.U("fid_UsbDevice") is ulong fresh && fresh != 0) places[fresh] = only;
            return only;
        }
        if (known && places.TryGetValue(e.U("fid_UsbDevice")!.Value, out var p))
            // A hub's events name it and one of its ports; a device's name only the device.
            return port is > 0 && Kind(e) is "hub" ? (p.Pci, [.. p.Ports, (int)port]) : p;
        return null;
    }
    private static bool SetUpFailure(int id) => id is >= 62 and <= 65 or >= 70 and <= 85 or >= 160 and <= 172 or 176 or 181 or 182 or >= 189 and <= 192 or 197;
    // Handles are the driver's pointers, reused once a device is gone, so a disconnected port forgets
    // everything that was behind it.
    private void Forget((string Pci, List<int> Ports) port)
    {
        foreach (var (handle, place) in places.ToList())
            if (place.Pci == port.Pci && place.Ports.Count >= port.Ports.Count && place.Ports.Take(port.Ports.Count).SequenceEqual(port.Ports)) places.Remove(handle);
        foreach (var key in enumerating.Keys.Where(k => k == Key(port) || k.StartsWith(Key(port) + "/", StringComparison.Ordinal)).ToList()) enumerating.Remove(key);
    }
    // Port status, port errors, hub resets, transfer errors on a port and the start of setting up a device
    // name the hub and its port; the rest name the device.
    private static string Kind(EtwEvent e) => e.Id is 60 or 100 or 101 or 102 or 103 or 122 or 123 or 131 or 7 or 8 or 9 or 10 or 25 or 26 or 27 or 28 ? "hub" : "device";
    // Events with no handle, such as a failed port change, may name the device by VID:PID.
    private static UsbNode? ByVidPid(Session s, EtwEvent e) => e.U("fid_idVendor") is ulong vid && e.U("fid_idProduct") is ulong pid
        && s.Listed.Where(n => n.Kind is "Device" or "Hub" && n.VendorId == vid.ToString("X4") && n.ProductId == pid.ToString("X4")).ToList() is [var one] ? one : null;

    // The path, as tree shows it, for a controller's PCI address and port path, and the node there if it
    // was in the last scan.
    internal (string Path, UsbNode? Node) Locate(Session s, (string Pci, List<int> Ports) place)
    {
        var controller = s.Snapshot.Controllers.FirstOrDefault(c => c.PciAddress.Equals(place.Pci, StringComparison.OrdinalIgnoreCase));
        if (controller == null) return ($"PCI {place.Pci}/" + string.Join("/", place.Ports.Select(p => p.ToString("00"))), null);
        UsbNode? at = Topology.MergedRoot(controller) ?? controller.Children.FirstOrDefault(c => c.Kind == "Root hub");
        string path = s.PathOf(controller);
        foreach (int port in place.Ports)
        {
            at = at?.Children.FirstOrDefault(c => c.Port == port);
            path = at != null ? s.PathOf(at) : path + "/" + port.ToString("00");
        }
        return (path, at);
    }

    // One event in plain words, or null for one that isn't worth a line: rundowns, routine power transitions,
    // and with verbose off, enumeration steps that went fine.
    internal JsonObject? Interpret(EtwEvent e, bool verbose)
    {
        Seen++;
        Learn(e);
        // Rundown and hub start-up describe where things are; they aren't news.
        if (e.Id is >= 1 and <= 11 or >= 20 and <= 28) return null;
        var (severity, what, count) = Describe(e);
        if (what == null || (!verbose && severity == "info" && count == null)) return null;
        var s = session();
        var place = Place(e);
        // The hub driver logs a port's status twice when it reads a change; the second says nothing new.
        if (e.Id is 100 or 101 && place is { } at)
        {
            var status = (e.U("fid_PortStatus") ?? 0, e.U("fid_PortChange") ?? 0);
            if (lastStatus.GetValueOrDefault(Key(at)) == status) return null;
            lastStatus[Key(at)] = status;
        }
        var (path, node) = place is { } p ? Locate(s, p) : ByVidPid(s, e) is UsbNode n ? (s.PathOf(n), n) : ("", null);
        // A device that's unplugged, or not yet rescanned, keeps the name it last had at its port.
        string? name = node is { Kind: "Device" or "Hub" } ? Topology.ShortName(node) : null;
        if (name != null) names[path] = name; else name = names.GetValueOrDefault(path);
        if (count != null && path.Length > 0)
        {
            if (!Counts.TryGetValue(path, out var counts)) Counts[path] = counts = [];
            counts[count] = counts.GetValueOrDefault(count) + 1;
        }
        // Connections set the scene; the resets and set-up steps that go with them are routine.
        if (!verbose && severity == "info" && count is "resets" or "set-ups") return null;
        return J.Obj(("time", e.Time.ToString("HH:mm:ss.fff")), ("event", "usb"), ("severity", severity), ("path", J.S(path)), ("name", name),
            ("what", what), ("source", $"USBHUB3 {e.Id}"), ("windows", J.S(e.Name)),
            ("fields", verbose ? J.Obj([.. e.Fields.Where(f => f.Key.StartsWith("fid_") && f.Value is not List<object>).Select(f => (f.Key[4..], (JsonNode?)(f.Value is ulong u ? $"0x{u:X}" : f.Value.ToString())))]) : null));
    }

    // Severity, what happened, and the summary count it adds to, for each event worth telling.
    internal static (string Severity, string? What, string? Count) Describe(EtwEvent e)
    {
        switch (e.Id)
        {
            case 100 or 101: return PortStatus(e.Id == 101, e.U("fid_PortStatus") ?? 0, e.U("fid_PortChange") ?? 0);
            case 43: return ("info", "Windows set up a new device here.", "set-ups");
            case 62: return ("warning", "Setting up the device here failed, and Windows is retrying.", "enumeration retries");
            case 63: return ("error", "Windows gave up setting up the device here after retrying.", "enumeration failures");
            case 64: return ("error", "Setting up the device here failed.", "enumeration failures");
            case 65: return ("error", "The port reset timed out: the device didn't answer after being reset.", "reset timeouts");
            case 66: return ("warning", "The hub was reset when the computer woke.", "hub resets");
            case 122: return ("error", "The hub was reset because one of its ports reported an error.", "hub resets");
            case 123: return ("error", "A request to change this port's state failed.", "port errors");
            case 130 or 131 or 132 or 133: return ("warning", $"A control transfer failed ({Quoted(e.Name)}).", "transfer errors");
            case 134: return ("warning", "This SuperSpeed hub can't wake the computer or be suspended, so Windows can't put it in a low-power state.", null);
            case 173: return ("warning", "A SuperSpeed device connected on the USB 2 bus: its USB 3 link didn't come up, so it runs at USB 2. Usually the cable or plug.", "SuperSpeed on USB 2");
            case 174: return ("error", "The hub was reset too many times, so Windows stopped using it.", "hub resets");
            case 175: return ("note", "The device's interface supports remote wake, but its configuration doesn't say so.", null);
            case 177: return ("warning", "The device was reset and set up again without Windows reporting it to drivers: it stopped answering and was recovered.", "silent re-enumerations");
            case 178: return ("warning", "The device came back with a different serial number than before.", null);
            case 179: return ("warning", "Its driver asked Windows to reset the port to recover the device.", "driver recoveries");
            case 183: return ("warning", "The device refused the U1 low-power link state.", "U1/U2 refusals");
            case 184: return ("warning", "The device refused the U2 low-power link state.", "U1/U2 refusals");
            case 185: return ("note", $"Windows found a problem in a descriptor ({Quoted(e.Name)}).", null);
            case 186: return ("warning", "A Billboard device appeared: a USB-C alternate mode, such as DisplayPort, may not have started.", "Billboards");
            case 187: return ("warning", "A USB-C alternate mode failed to start.", "alternate mode failures");
            case 188: return ("info", "A USB-C alternate mode started.", "alternate modes entered");
            case 194 or 195 or 196: return ("warning", "The device kept the computer from saving power while idle, and Windows acted on it.", null);
            case 197: return ("error", "The device reported problems that stopped it from being set up.", "enumeration failures");
            case 198: return ("error", "The device shares one endpoint between two interfaces that reserve bandwidth, which Windows doesn't allow.", null);
            case >= 70 and <= 85 or 176 or 191 or 192: return ("error", $"Windows rejected a malformed descriptor ({Quoted(e.Name)}).", "descriptors rejected");
            case >= 160 and <= 172 or 181 or 182 or 189 or 190: return ("warning", $"A request failed while Windows set it up ({Quoted(e.Name)}).", "failed requests");
            case 44: return ("error", "This hub is behind too many hubs for USB to reach it.", null);
            case 102 or 103: return ("note", $"The firmware's description of this port couldn't be read ({Quoted(e.Name)}).", null);
            case 60 or 61: return ("info", e.Id == 60 ? "Setting up the device here." : "Finished setting up the device here.", null);
        }
        // Anything else at warning or worse is told as Windows names it.
        return e.Level is >= 1 and <= 3 && e.Name.Length > 0 ? (e.Level == 3 ? "warning" : "error", e.Name.TrimEnd('.') + ".", "other errors") : ("info", null, null);
    }
    private static string Quoted(string text) => $"“{text.TrimEnd('.')}”";

    // USB 3 link states, as wPortStatus carries them in bits 5 to 8.
    internal static string LinkState(ulong status) => ((status >> 5) & 0xF) switch
    {
        0 => "U0", 1 => "U1", 2 => "U2", 3 => "U3 (suspended)", 4 => "SS.Disabled", 5 => "Rx.Detect", 6 => "SS.Inactive", 7 => "Polling",
        8 => "Recovery", 9 => "Hot Reset", 0xA => "Compliance Mode", 0xB => "Loopback", var x => $"link state {x}"
    };

    // A port's status after a change, as a hub reports it (USB 2.0 11.24.2.7, USB 3.2 10.16.2.6): what
    // changed, and the link state for USB 3. Suspend and resume are routine, and so is a connection's own reset.
    internal static (string Severity, string? What, string? Count) PortStatus(bool usb3, ulong status, ulong change)
    {
        bool connected = (status & 1) != 0;
        if (usb3)
        {
            string state = LinkState(status);
            if ((change & 0x80) != 0) return ("error", "The USB 3 link couldn't be trained (config error): the device stays on USB 2 or doesn't connect. Usually the cable or plug.", "link failures");
            if ((change & 0x08) != 0 && (status & 0x08) != 0) return ("error", "Overcurrent: the port drew too much power and was switched off.", "overcurrent events");
            if ((change & 0x20) != 0) return ("warning", $"Warm reset of the USB 3 link (now {state}): Windows reset a link that stopped working.", "warm resets");
            if ((change & 0x40) != 0 && ((status >> 5) & 0xF) is 6 or 0xA) return ("error", $"The USB 3 link failed and entered {state}.", "link failures");
            if ((change & 0x01) != 0) return ("info", connected ? "Connected." : "Disconnected.", connected ? "connections" : "disconnections");
            if ((change & 0x10) != 0) return ("info", "Port reset finished.", "resets");
            if ((change & 0x40) != 0) return ("info", $"Link state changed to {state}.", null);
            return ("info", change == 0 ? null : $"Port status 0x{status:X4}, change 0x{change:X4}.", null);
        }
        if ((change & 0x08) != 0 && (status & 0x08) != 0) return ("error", "Overcurrent: the port drew too much power and was switched off.", "overcurrent events");
        // The hub turns a port off by itself when the device misbehaves on the bus.
        if ((change & 0x02) != 0 && (status & 0x02) == 0 && connected) return ("error", "The hub disabled this port because the device misbehaved on the bus.", "ports disabled");
        if ((change & 0x01) != 0) return ("info", connected ? "Connected." : "Disconnected.", connected ? "connections" : "disconnections");
        if ((change & 0x10) != 0) return ("info", "Port reset finished.", "resets");
        if ((change & 0x04) != 0) return ("info", "Resumed from suspend.", null);
        return ("info", change == 0 ? null : $"Port status 0x{status:X4}, change 0x{change:X4}.", null);
    }

    // Each port's counts, most eventful first.
    internal JsonObject Summary(DateTime start, Session s) => J.Obj(("time", DateTime.Now.ToString("HH:mm:ss.fff")), ("event", "trace-summary"),
        ("recordedSeconds", Math.Round((DateTime.Now - start).TotalSeconds)), ("eventsRead", Seen),
        ("ports", J.Arr(Counts.OrderByDescending(c => c.Value.Values.Sum()).Select(c => (JsonNode)J.Obj(("path", c.Key),
            ("name", s.Listed.FirstOrDefault(n => n.Kind is "Device" or "Hub" && s.PathOf(n) == c.Key) is UsbNode n ? Topology.ShortName(n) : names.GetValueOrDefault(c.Key)),
            ("counts", J.Obj([.. c.Value.OrderByDescending(x => x.Value).Select(x => (x.Key, (JsonNode?)x.Value))])))))));

    // Counts are named in the plural; one of them reads in the singular.
    internal static string Singular(string count) => count switch
    {
        "enumeration retries" => "enumeration retry", "driver recoveries" => "driver recovery", "alternate modes entered" => "alternate mode entered",
        "descriptors rejected" => "descriptor rejected", "ports disabled" => "port disabled", "SuperSpeed on USB 2" => count,
        _ => count.EndsWith('s') ? count[..^1] : count
    };

    internal static string Text(JsonObject e)
    {
        string time = e["time"]?.ToString() ?? "";
        switch (e["event"]?.ToString())
        {
            case "trace-start":
                return $"{time} recording USB hub driver events {e["for"]} · plug, unplug or wiggle now\n";
            case "usb":
                var line = new StringBuilder($"{time} {e["severity"],-8} {e["path"]}{(e["name"] is JsonNode name ? " " + name : "")}  {e["what"]}  ({e["source"]})");
                if (e["fields"] is JsonObject fields && fields.Count > 0) line.Append("\n    " + string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}")));
                return line.Append('\n').ToString();
            case "trace-summary":
                var sb = new StringBuilder($"{time} done after {e["recordedSeconds"]} s · {e["eventsRead"]} hub driver events read\n");
                foreach (var p in e["ports"]!.AsArray())
                    sb.AppendLine($"  {p!["path"]}{(p["name"] is JsonNode n ? " " + n : "")}: {string.Join(", ", p["counts"]!.AsObject().Select(c => $"{c.Value} {(c.Value!.GetValue<int>() == 1 ? Singular(c.Key) : c.Key)}"))}");
                if (e["ports"]!.AsArray().Count == 0) sb.AppendLine("  Nothing happened on any port.");
                return sb.ToString();
            default:
                return Watch.Text(e);
        }
    }

    // Records for a while, or until stopped, emitting each event worth telling and then a summary.
    internal static JsonObject Run(Options o, TimeSpan duration, bool verbose, Action<JsonObject> emit, CancellationToken cancel)
    {
        if (o.Has("demo") || o.Has("input")) throw new CliException("trace records live hardware; it can't trace --demo or --input.");
        if (!OperatingSystem.IsWindows()) throw new CliException("trace records Windows' USB hub driver. On Linux, the kernel's USB messages are in journalctl -k.");
        var current = Session.Open(o);
        var trace = new UsbTrace(() => current);
        var start = DateTime.Now;
        using (var etw = Start())
        {
            emit(J.Obj(("time", start.ToString("HH:mm:ss.fff")), ("event", "trace-start"), ("for", duration == TimeSpan.Zero ? "until stopped" : $"for {duration.TotalSeconds:0} s")));
            var gate = new object();
            etw.Read(e => { lock (gate) if (trace.Interpret(e, verbose) is JsonObject line) emit(line); });
            if (duration == TimeSpan.Zero) cancel.WaitHandle.WaitOne(); else cancel.WaitHandle.WaitOne(duration);
            // Devices that arrived while recording aren't in the first scan; a fresh one names them in the summary.
            current = Session.Open(o);
        }
        return trace.Summary(start, current);
    }
    internal static EtwSession Start() => EtwSession.Start("UsbAtlas-trace", [(Hub3, 5, Keywords)]);
}

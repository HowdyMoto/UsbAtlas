namespace UsbAtlas;

// Checks the port map for contradictions. A computer's firmware tells Windows which root ports can be
// plugged into, which are USB-C, and which USB 2 and USB 3 ports are the two halves of one socket (ACPI
// _UPC and _PLD). Boards ship with mistakes in it, and Windows then pairs the wrong halves or none. The
// ports still work, so each finding is a note. Only root ports are checked: Windows pairs a plug-in
// hub's ports from the hub's own two sides, and those often can't say what kind of socket they are.
internal static class PortMap
{
    internal const string CompanionMissing = "Other half of socket not found";
    internal const string CompanionOneWay = "Socket halves not paired both ways";
    internal const string SameVersionHalves = "Socket halves are the same USB version";
    internal const string HalvesDisagree = "Socket halves described differently";
    internal const string NoUsb2Half = "No USB 2 half reported";
    internal static bool IsFinding(string issue) => issue is CompanionMissing or CompanionOneWay or SameVersionHalves or HalvesDisagree or NoUsb2Half;

    // Every port Windows names as sharing this port's socket: the scan's node when it has one, and the
    // port number Windows gave. Sample data and older snapshots carry only the node.
    internal static List<(string Id, int Port)> Companions(UsbNode port)
    {
        var list = new List<(string, int)>();
        if (port.CompanionPortNumber > 0 || port.CompanionId.Length > 0) list.Add((port.CompanionId, port.CompanionPortNumber));
        list.AddRange(port.MoreCompanions.Where(m => m.PortNumber > 0).Select(m => (m.Id, m.PortNumber)));
        return list;
    }

    // 2 for a port that speaks only USB 1 and 2, 3 for one that speaks only USB 3, as the two halves of a
    // socket do; null when Windows didn't say.
    internal static int? Generation(UsbNode port)
    {
        bool usb3 = port.Protocols.Contains("USB 3.x"), usb2 = port.Protocols.Contains("USB 2.0") || port.Protocols.Contains("USB 1.x");
        return usb3 == usb2 ? null : usb3 ? 3 : 2;
    }

    internal static void Analyze(Snapshot snapshot)
    {
        var byId = new Dictionary<string, UsbNode>();
        foreach (var n in snapshot.Nodes) { byId.TryAdd(n.Id, n); n.PortMapWarnings.Clear(); }
        var paths = Topology.PathLabels(snapshot);
        string Path(UsbNode n) => paths.GetValueOrDefault(n.Id, n.Id);
        // A controller or hub the scan couldn't read may hold the port another names, so none is called missing.
        bool complete = !snapshot.Nodes.Any(n => n.ScanIncomplete) && !snapshot.Diagnostics.Any(d => d.StartsWith("Controller", StringComparison.Ordinal));
        var rootPorts = snapshot.Nodes.Where(n => n.Kind == "Root hub").SelectMany(root => root.Children).Where(p => p.Port > 0).ToList();
        var order = new Dictionary<string, int>();
        foreach (var port in rootPorts) order.TryAdd(port.Id, order.Count);
        foreach (var port in rootPorts)
        {
            var named = Companions(port);
            foreach (var (id, number) in named)
            {
                if (id.Length == 0 || !byId.TryGetValue(id, out var other))
                {
                    if (complete) Flag(port, CompanionMissing, $"Windows names {(number > 0 ? $"a port numbered {number}" : "another port")} as the other half of this port's socket, and this scan has no such port on the hub it names.");
                    continue;
                }
                if (!Companions(other).Any(c => c.Id == port.Id))
                {
                    var names = Companions(other).Where(c => c.Id.Length > 0 && byId.ContainsKey(c.Id)).Select(c => Path(byId[c.Id])).ToList();
                    Flag(port, CompanionOneWay, $"This port names {Path(other)} as the other half of its socket, but {Path(other)} names {(names.Count > 0 ? string.Join(" and ", names) : "no other half")}.");
                    continue;
                }
                // A pair that names each other is judged once, at whichever half comes first.
                if (order.TryGetValue(other.Id, out var position) && position < order[port.Id]) continue;
                if (Generation(port) is int generation && generation == Generation(other))
                    Flag(port, SameVersionHalves, $"This port and {Path(other)} are paired as one socket, and both are USB {generation} ports. A socket's halves are one USB 2 port and one USB 3 port.");
                var differences = new List<string>();
                if (port.PortConnectorIsTypeC is bool c && other.PortConnectorIsTypeC is bool otherC && c != otherC) differences.Add($"{(c ? "this port" : Path(other))} is described as USB-C and {(c ? Path(other) : "this port")} isn't");
                if (port.PortIsUserConnectable is bool user && other.PortIsUserConnectable is bool otherUser && user != otherUser) differences.Add($"{(user ? "this port" : Path(other))} is described as one you can plug into and {(user ? Path(other) : "this port")} isn't");
                if (differences.Count > 0) Flag(port, HalvesDisagree, $"This port and {Path(other)} are paired as one socket, but {string.Join(", and ", differences)}.");
            }
            if (named.Count == 0 && port.PortIsUserConnectable == true && Generation(port) == 3)
                Flag(port, NoUsb2Half, "Windows describes this USB 3 port as one you can plug into and names no USB 2 port as the other half of its socket. Every USB 3 socket also carries USB 2.");
        }
    }

    private static void Flag(UsbNode port, string finding, string evidence)
    {
        if (!port.PortMapWarnings.Contains(finding)) port.PortMapWarnings.Add(finding);
        if (!port.Notes.Contains("Port map evidence: " + evidence)) port.Notes.Add("Port map evidence: " + evidence);
    }
}

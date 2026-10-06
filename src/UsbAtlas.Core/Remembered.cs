using System.Text.RegularExpressions;

namespace UsbAtlas;

// A USB device Windows remembers but that isn't connected now. Windows keeps an entry for every device it has
// set up. A device with a serial number keeps one entry wherever it's plugged in, but one without gets a new
// entry for each port, with its own settings and, for a serial adapter, its own COM number; the old ones stay,
// hidden, in Device Manager. Read-only: USB Atlas lists them and says how to remove them, but doesn't.
public sealed class RememberedDevice
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    // Windows' own description of where it was ("Port_#0002.Hub_#0003") and its location path, which places
    // it on the hub it was plugged into when that hub is connected now.
    public string LocationInfo { get; set; } = "";
    public string LocationPath { get; set; } = "";
    public string ComPort { get; set; } = "";
    public DateTime? LastConnected { get; set; }
    public DateTime? LastRemoved { get; set; }
}

internal static class Remembered
{
    internal const string ComChanged = "COM number changed";
    internal const string OtherPorts = "Remembered on other ports";

    private static readonly Regex Ids = new(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\", RegexOptions.IgnoreCase);
    internal static (string Vid, string Pid)? VidPid(string instance) => Ids.Match(instance) is { Success: true } m ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()) : null;
    // Windows builds a device's instance ID from its serial number when it has a usable one; otherwise from
    // where it's plugged in, with & in it, such as 7&38104379&0&1.
    internal static bool ByPort(string instance) => instance.Split('\\') is [_, _, var last] && last.Contains('&');
    // A serial port's COM number from its Windows name, such as "USB Serial Device (COM5)".
    internal static string ComOf(string name) => Regex.Match(name, @"\((COM\d+)\)\s*$") is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : "";

    // The other entries Windows keeps for a connected device: the same VID:PID, both named by port, since a
    // device with a serial number has one entry and two serials are two devices.
    internal static List<RememberedDevice> For(Snapshot snapshot, UsbNode n)
    {
        if (snapshot.Remembered is not { Count: > 0 } all || n.Kind is not ("Device" or "Hub") || n.VendorId.Length == 0 || !ByPort(n.InstanceId)) return [];
        return all.Where(r => r.VendorId.Equals(n.VendorId, StringComparison.OrdinalIgnoreCase) && r.ProductId.Equals(n.ProductId, StringComparison.OrdinalIgnoreCase)
                && ByPort(r.InstanceId) && !r.InstanceId.Equals(n.InstanceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.LastConnected ?? DateTime.MinValue).ToList();
    }

    // Places each connected device's other entries on the map and flags what they break: a serial adapter on
    // another COM number, or a game controller games may see twice. Snapshots are analyzed again when loaded.
    internal static void Analyze(Snapshot snapshot)
    {
        var labels = Topology.PathLabels(snapshot);
        foreach (var n in snapshot.Nodes)
        {
            n.OtherEntries = [.. For(snapshot, n).Select(r => (r, Place(snapshot, r, labels)))];
            n.RememberedIssue = n.OtherEntries.Count == 0 ? ""
                : n.ComPort.Length > 0 && n.OtherEntries.Any(o => o.Entry.ComPort.Length > 0 && o.Entry.ComPort != n.ComPort) ? ComChanged
                : n.DeviceType == "Game controller" ? OtherPorts : "";
        }
    }

    // Where an entry was plugged in: the port on the map when its hub is connected now, else Windows' words.
    internal static string Place(Snapshot snapshot, RememberedDevice r, IReadOnlyDictionary<string, string> labels)
    {
        if (Regex.Match(r.LocationPath, @"^(.*)#USB\((\d+)\)$") is { Success: true } m && int.TryParse(m.Groups[2].Value, out int port)
            && snapshot.Nodes.FirstOrDefault(n => n.LocationPath.Length > 0 && n.LocationPath.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is UsbNode hub
            && labels.TryGetValue(hub.Id, out var at))
            return $"{at}/{port:00}";
        return r.LocationInfo.Length > 0 ? r.LocationInfo + " (on a hub not connected now)" : "Port not recorded";
    }

    // One line per entry, for Properties, Detection details and show's text.
    internal static string Line((RememberedDevice Entry, string Place) o) =>
        string.Join(" · ", new[] { o.Place, o.Entry.ComPort, o.Entry.LastConnected is DateTime t ? "last connected " + t.ToString("d MMM yyyy HH:mm") : "" }.Where(x => x.Length > 0));

    internal static Explanations.Explanation Explain(UsbNode n, string issue)
    {
        var others = n.OtherEntries.Select(o => o.Entry).ToList();
        string Where(RememberedDevice r) => n.OtherEntries.First(o => o.Entry == r).Place;
        var remove = "Remove old entries: in Device Manager, choose View › Show hidden devices and uninstall the grayed-out ones, or run pnputil /remove-device followed by the instance ID as administrator. Detection details lists them.";
        if (issue == ComChanged)
        {
            var earlier = others.Where(o => o.ComPort.Length > 0 && o.ComPort != n.ComPort).ToList();
            string before = string.Join(", ", earlier.Take(3).Select(o => $"{o.ComPort} on {Where(o)}")) + (earlier.Count > 3 ? $" and {earlier.Count - 3} more" : "");
            return new($"This device is {n.ComPort} on this port, but it was {before}. It has no serial number, so Windows sets it up again, with a new COM number, on each port it's plugged into.",
                $"Maybe: software set up for {earlier[0].ComPort} won't find it until it's set to {n.ComPort}.", "",
                [$"Set the software that uses it to {n.ComPort}.",
                 $"Or give it its old number: Device Manager › Ports (COM & LPT) › {n.ComPort} › Properties › Port Settings › Advanced › COM Port Number. Windows may say the number is in use by the old entry; remove that entry first.",
                 remove,
                 "Plugging it into the same port each time keeps one number."]);
        }
        return new($"Windows remembers this controller on {others.Count} other port{(others.Count == 1 ? "" : "s")} too: {string.Join(", ", others.Take(3).Select(Where))}{(others.Count > 3 ? " and more" : "")}. It has no serial number, so each port it's been plugged into left a separate entry.",
            "Maybe: games and the Game Controllers panel can list the old entries, or keep button mappings and calibration on one of them.", "",
            [remove, "Plugging it into the same port each time keeps one entry."]);
    }
}

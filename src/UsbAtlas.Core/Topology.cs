using System.Text.RegularExpressions;

namespace UsbAtlas;

// Paths, names and figures shared by the app's cards and the command line's text.
internal static class Topology
{
    // On Windows each xHCI controller has one root hub, and to the user they are one thing: a host whose
    // sockets are the root ports. They share one card and one path; a controller with several root hubs,
    // or none readable, keeps them apart.
    internal static UsbNode? MergedRoot(UsbNode n) => n.Kind == "Controller" && n.Children.Count == 1 && n.Children[0].Kind == "Root hub" ? n.Children[0] : null;

    // The host, then each port number on the way: H01/04/02. A merged root hub shares its controller's
    // path, so root ports read H01/03; a root hub that isn't merged reads H01/root.
    internal static Dictionary<string, string> PathLabels(Snapshot snapshot)
    {
        var labels = new Dictionary<string, string>();
        void Visit(UsbNode n, string path)
        {
            labels[n.Id] = path;
            bool merged = MergedRoot(n) != null;
            foreach (var child in n.Children) Visit(child, merged ? path : path + "/" + (child.Port > 0 ? child.Port.ToString("00") : "root"));
        }
        for (int i = 0; i < snapshot.Controllers.Count; i++) Visit(snapshot.Controllers[i], $"H{i + 1:00}");
        return labels;
    }

    // The chain from the host controller down to the node, or empty when it isn't in the snapshot.
    internal static List<UsbNode> FindPath(Snapshot snapshot, string id)
    {
        List<UsbNode>? SearchPath(UsbNode n) { if (n.Id == id) return [n]; foreach (var c in n.Children) { var path = SearchPath(c); if (path != null) { path.Insert(0, n); return path; } } return null; }
        return snapshot.Controllers.Select(SearchPath).FirstOrDefault(x => x != null) ?? [];
    }

    // Everything search looks at: names, IDs, types, sockets, issues, path, functions and figures.
    internal static string SearchText(UsbNode n, string? path) =>
        $"{n.DisplayName} {n.PortLabel} {n.Name} {n.ReportedProduct} {n.WindowsName} {n.LookupVendor} {n.LookupProduct} {n.VendorId}:{n.ProductId} {(n.DeviceRevision.Length > 0 ? "rev " + n.DeviceRevision : "")} {n.Serial} {n.Manufacturer} {n.DeviceClass} {n.DeviceType} {n.Location} {n.Status} {n.Connector} {n.SocketSpeed} {PciText(n)} {IssueRules.Summary(n)} {path} {string.Join(" ", n.InterfaceFunctions)} {string.Join(" ", n.HidUsages)} {(n.PollIntervalMs is double ms ? UsbBudgets.PollingRate(ms) : "")} {(n.Kind == "Hub" ? TtType(n) : "")} {Billboard.Summary(n)} {n.ContainerName} {(n.WakeSetting == "On" ? "wakes computer" : "")}";

    internal static string TtType(UsbNode n) => n.TransactionTranslators switch { "Single" => "Share one link · single TT", "Per port" => "Link per port · multi-TT", "Not reported" => "Not reported", _ => "None" };
    internal static string ShortSpeed(UsbNode n) => n.LinkMbps switch { > 5000 and var fast => $"{fast / 1000:0.##} Gb/s", 5000 => "5 Gb/s", 480 => "480 Mb/s", 12 => "12 Mb/s", 1.5 => "1.5 Mb/s", _ => n.Speed.StartsWith("SuperSpeedPlus") ? "≥10 Gb/s" : "Rate unknown" };
    // A host controller's PCI identity and place, such as "PCI 1022:1128 at 04:00.3".
    internal static string PciText(UsbNode n) => n.PciId.Length == 0 ? "" : $"PCI {n.PciId}{(n.PciAddress.Length > 0 ? " at " + n.PciAddress : "")}";
    // Who made the controller chip, for the makers of nearly every USB host controller.
    internal static string PciVendor(string pciId) => pciId.Split(':')[0] switch
    {
        "8086" => "Intel", "1022" or "1002" => "AMD", "1B21" => "ASMedia", "1912" => "Renesas", "1033" => "NEC", "1106" => "VIA",
        "104C" => "Texas Instruments", "1B73" => "Fresco Logic", "1B6F" => "Etron", "10DE" => "NVIDIA", _ => ""
    };

    // Input devices and game controllers show how often they are polled, since that is what their
    // owners compare; for other devices it is in Properties.
    internal static bool ShowsPolling(UsbNode n) => n.Kind == "Device" && n.PollIntervalMs != null && n.DeviceType is "Keyboard" or "Mouse" or "HID / controls" or "Game controller";

    // Requested power and its source are one figure. A device or hub that asks the bus for nothing runs on
    // its own supply, so it reads "External power" rather than a misleading 0 mA; one that also draws a
    // little from the bus reads "External + 100 mA".
    internal static bool UsesExternalPower(UsbNode n) => n.Kind is "Device" or "Hub" && (n.PowerSource == "Self powered" || n.MaxPowerMa == 0);
    internal static (string Text, string Words) PowerFigure(UsbNode n)
    {
        if (UsesExternalPower(n))
            return n.MaxPowerMa is > 0 ? ($"External + {n.MaxPowerMa} mA", $"external power plus {n.MaxPowerMa} mA requested from the bus") : ("External power", "external power, nothing requested from the bus");
        return n.MaxPowerMa is int ma ? ($"{ma} mA", $"{ma} mA requested") : ("Unknown", "power request unknown");
    }

    // Device Manager's power-saving setting for this hardware. Turned on, it still does nothing while the
    // power plan's USB selective suspend is off.
    internal static string PowerSavingText(UsbNode n, Snapshot snapshot) => n.PowerSaving == "On" && snapshot.UsbSuspendActive == false ? "On · plan disables it" : n.PowerSaving;

    internal static string Label(UsbNode n) => n.Kind switch
    {
        "Controller" => "Host controller", "Root hub" => "Root ports",
        // Where a hub sits is worked out, not reported; the Location row's tooltip says how.
        "Hub" when n.Location == "Internal" => "Built-in hub",
        "Hub" when n.Location == "External" => "Plug-in hub",
        "Hub" => "Hub", "Empty port" => "Empty port", "Unavailable" => "USB port",
        _ => n.DeviceType
    };

    // Driver suffixes such as "- 1.10 (Microsoft)" and company words are dropped; labels are never shortened.
    internal static string ShortName(UsbNode n)
    {
        if (n.UserLabel.Length > 0) return n.UserLabel;
        var name = n.Name.Replace('_', ' ');
        name = Regex.Replace(name, @"\s+-\s+\d+(\.\d+)*(?=\s*(\(Microsoft\))?\s*$)", "");
        name = Regex.Replace(name, @"\s*\(Microsoft\)\s*$", "");
        name = Regex.Replace(name, @"eXtensible Host Controller", "xHCI", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @",?\s+(Inc|Incorporated|Corp|Corporation|Co|Ltd|Limited|LLC|GmbH)\.?(?=[\s,]|$)", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+(Semiconductor|Technology|Technologies|Tech|Electronics|International|Systems)\.?(?=\s|$)", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', ',');
        return name.Length > 0 ? name : n.Name;
    }
}

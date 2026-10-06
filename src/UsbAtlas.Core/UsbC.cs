namespace UsbAtlas;

// What USB Atlas can and can't say about USB-C beyond the socket's shape. Windows' hub queries don't say
// whether a USB-C socket is USB4 or Thunderbolt, carries DisplayPort, or what Power Delivery contract it has,
// and no API gives apps the contract. What it does report: whether the computer has a USB4 host router, the
// USB4 devices (docks, enclosures) connected through one, whether a USB-C connector manager (UCSI firmware)
// runs its USB-C ports, and which host controllers are reached through a USB4 or Thunderbolt tunnel, such as
// a dock's own controller. A USB-C device whose alternate mode fails shows a Billboard (Billboard.cs).
internal static class UsbC
{
    internal const string TunneledLocation = "In a USB4 or Thunderbolt dock";

    // Windows' USB4 host router driver; its PCI class is 0C0340, a USB4 host interface.
    internal static bool IsHostRouter(string service) => service.Equals("Usb4HostRouter", StringComparison.OrdinalIgnoreCase);
    // USB4 device routers below a host router: docks, enclosures and hubs. The root router is the host's own.
    internal static bool IsUsb4Device(string instance) => instance.StartsWith(@"USB4\", StringComparison.OrdinalIgnoreCase)
        && !instance.Contains("ROOT_DEVICE_ROUTER", StringComparison.OrdinalIgnoreCase) && !instance.Contains("VIRTUAL_POWER", StringComparison.OrdinalIgnoreCase);
    // UCSI and TCPCI connector managers: UcmUcsiAcpiClient, UcmTcpciCx and the like.
    internal static bool IsConnectorManager(string service) => service.StartsWith("Ucm", StringComparison.OrdinalIgnoreCase);

    // A controller reached through a USB4 or Thunderbolt tunnel is in whatever is plugged in, such as a dock.
    internal static void MarkTunneled(UsbNode controller)
    {
        if (controller.PcieTunneled != true) return;
        controller.Location = TunneledLocation;
        controller.LocationEvidence = "Windows reaches this host controller through a PCIe tunnel over USB4 or Thunderbolt, so it's inside something plugged into the computer, such as a dock, monitor or enclosure, not on the motherboard.";
    }

    // The USB-C socket a port is, when it is one; empty ports included. A connector the user set wins over the firmware's flag.
    private static bool IsUsbC(UsbNode n) => n.SocketConnectorSet is string set ? set == "USB-C" : n.Connector == "USB-C" || n.PortConnectorIsTypeC == true;
    private static bool OnHost(Snapshot s, UsbNode n) => Topology.FindPath(s, n.Id) is { Count: >= 2 } path && path[^2].Kind is "Root hub" or "Controller";

    // What Windows says about USB4, Thunderbolt, DisplayPort and Power Delivery for a USB-C socket, each spelled
    // out when it says nothing, as Detection details and atlascli show give it. Empty for anything else.
    internal static List<(string Feature, string Text)> Socket(Snapshot s, UsbNode n)
    {
        if (!IsUsbC(n) || n.Kind is "Controller" or "Root hub") return [];
        string usb4 = !OnHost(s, n) ? "Not reported: Windows doesn't say whether a hub's or dock's USB-C sockets carry USB4 or Thunderbolt."
            : s.Usb4HostRouters is { Count: > 0 } routers ? $"Not reported for this socket. This computer has a USB4 host router ({Names(routers)}), so some of its USB-C sockets are USB4 ports; Windows doesn't say which. Look for a lightning-bolt or USB4 logo beside the socket."
            : s.Usb4HostRouters != null ? "Not reported for this socket. Windows lists no USB4 host router in this computer, so it's most likely not a USB4 port; an older Thunderbolt 3 controller isn't detected."
            : "Not reported.";
        return
        [
            ("USB4 / Thunderbolt", usb4),
            ("DisplayPort", "Not reported: Windows doesn't say whether a socket carries video. A USB-C monitor or adapter whose DisplayPort mode doesn't start shows a Billboard device with the reason."),
            ("Power Delivery", "Not reported: Windows doesn't give apps a port's Power Delivery contract (voltage and current) or how much it can supply. A USB-C power meter between port and device shows it.")
        ];
    }

    // Router names without Windows' trademark and driver-maker suffixes: "USB4(TM) Host Router (Microsoft)" reads
    // "USB4 Host Router". Done when shown, so snapshots saved earlier read the same.
    internal static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => System.Text.RegularExpressions.Regex.Replace(n, @"\((?:TM|R|Microsoft)\)|™|®", "").Replace("  ", " ").Trim()).Distinct());

    // The computer's USB-C hardware as Windows lists it, for a host's Detection details and atlascli show.
    internal static List<string> Computer(Snapshot s)
    {
        var lines = new List<string>();
        if (s.Usb4HostRouters is { Count: > 0 } routers) lines.Add($"USB4 host router: {Names(routers)}. Some of the computer's USB-C sockets are USB4 ports, many also Thunderbolt-compatible; Windows doesn't say which sockets, or which host controllers they share.");
        else if (s.Usb4HostRouters != null) lines.Add("Windows lists no USB4 host router in this computer. Older Thunderbolt 3 controllers aren't detected.");
        if (s.Usb4Devices is { Count: > 0 } docks) lines.Add($"Connected through USB4: {string.Join(", ", docks)}.");
        if (s.UsbCConnectorManager == true) lines.Add("A USB-C connector manager (UCSI) runs this computer's USB-C ports: their power roles, charging and alternate modes. atlascli events lists its failures.");
        return lines;
    }
}

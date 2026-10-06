namespace UsbAtlas;

// Companion links are reported by Windows; names and matching chip IDs alone do not prove a pair.
internal static class HubRelationships
{
    // Hub links arrive with and without a \\?\, \\.\ or \??\ prefix.
    private static string Normalize(string path) => path.Trim().TrimStart('\\', '?', '.').ToUpperInvariant();
    // Each port's companion: the other logical port of its physical socket, named by hub link and port number.
    internal static void ResolveCompanions(Snapshot snapshot)
    {
        var hubs = new Dictionary<string, UsbNode>();
        foreach (var hub in snapshot.Nodes.Where(n => n.HubSymbolicLink.Length > 0)) hubs.TryAdd(Normalize(hub.HubSymbolicLink), hub);
        foreach (var port in snapshot.Nodes.Where(n => n.CompanionPortNumber > 0 && n.CompanionHubSymbolicLink.Length > 0))
            if (hubs.TryGetValue(Normalize(port.CompanionHubSymbolicLink), out var hub) && hub.Children.FirstOrDefault(c => c.Port == port.CompanionPortNumber) is UsbNode companion && companion.Id != port.Id)
                port.CompanionId = companion.Id;
        foreach (var port in snapshot.Nodes)
        foreach (var more in port.MoreCompanions.Where(m => m.PortNumber > 0 && m.HubSymbolicLink.Length > 0))
            more.Id = hubs.TryGetValue(Normalize(more.HubSymbolicLink), out var hub) && hub.Children.FirstOrDefault(c => c.Port == more.PortNumber) is UsbNode companion && companion.Id != port.Id ? companion.Id : "";
    }
    internal static void Analyze(Snapshot snapshot)
    {
        var hubs = snapshot.Nodes.Where(n => n.Kind is "Hub" or "Root hub").ToList();
        foreach (var hub in hubs) { hub.CompanionHubId = ""; hub.IsUsb2Companion = false; hub.Usb3SideMissing = hub.Usb3SideFailed = false; }
        void Pair(UsbNode node, UsbNode? other)
        {
            if (other == null || other.Id == node.Id || other.Kind != "Hub" || node.VendorId != other.VendorId || node.VendorId.Length == 0) return;
            bool nodeUsb2 = node.LinkMbps is > 0 and <= 480;
            bool otherUsb2 = other.LinkMbps is > 0 and <= 480;
            bool nodeUsb3 = node.LinkMbps >= 5000 || node.Speed.StartsWith("SuperSpeed", StringComparison.Ordinal);
            bool otherUsb3 = other.LinkMbps >= 5000 || other.Speed.StartsWith("SuperSpeed", StringComparison.Ordinal);
            if (!(nodeUsb2 && otherUsb3 || otherUsb2 && nodeUsb3)) return;
            node.CompanionHubId = other.Id; other.CompanionHubId = node.Id;
            node.IsUsb2Companion = nodeUsb2; other.IsUsb2Companion = otherUsb2;
        }
        foreach (var parent in hubs)
        foreach (var node in parent.Children.Where(n => n.Kind == "Hub" && n.CompanionPortNumber > 0 && n.CompanionHubSymbolicLink.Length > 0))
        {
            var companionParent = hubs.FirstOrDefault(h => h.HubSymbolicLink.Length > 0 && Normalize(h.HubSymbolicLink) == Normalize(node.CompanionHubSymbolicLink));
            Pair(node, companionParent?.Children.FirstOrDefault(n => n.Port == node.CompanionPortNumber && n.Kind == "Hub"));
        }
        // A hub on a socket Windows doesn't pair, such as a built-in one, still names its other side
        // through its own ports: every port with a companion has it on that one other hub.
        var parentOf = new Dictionary<string, UsbNode>();
        foreach (var hub in hubs) foreach (var port in hub.Children) parentOf.TryAdd(port.Id, hub);
        foreach (var node in hubs.Where(h => h.Kind == "Hub" && h.CompanionHubId.Length == 0))
            if (node.Children.Where(c => c.CompanionId.Length > 0).Select(c => parentOf.GetValueOrDefault(c.CompanionId)).Distinct().ToList() is [UsbNode other] && other.CompanionHubId.Length == 0)
                Pair(node, other);
        // A USB 3 hub's USB 2 side with no USB 3 side runs everything behind it at USB 2 when its socket's
        // USB 3 half is empty (a USB 2-only cable, or a monitor or dock giving its lanes to the display) or
        // shows an error (the USB 3 side tried and failed, often a damaged cable or plug). A USB4 or
        // Thunderbolt dock tunnels its USB 3 side to another controller, where Windows doesn't pair it, so an
        // unpaired USB 3 hub from the same vendor on another controller may be the missing side and keeps
        // this silent. One on the same controller can't be: it would occupy the socket's USB 3 half.
        var ports = new Dictionary<string, UsbNode>();
        foreach (var port in snapshot.Nodes) ports.TryAdd(port.Id, port);
        var controllerOf = new Dictionary<UsbNode, UsbNode>();
        foreach (var controller in snapshot.Controllers) foreach (var node in controller.Walk()) controllerOf[node] = controller;
        foreach (var node in hubs.Where(h => h.SpeedLimited && h.CompanionHubId.Length == 0 && Usb2HubAtNativeSpeed(h)))
        {
            var companion = node.CompanionId.Length > 0 ? ports.GetValueOrDefault(node.CompanionId) : null;
            node.Usb3SideMissing = companion is { Kind: "Empty port" or "Unavailable" } && !hubs.Any(h => h.CompanionHubId.Length == 0 && h.VendorId == node.VendorId
                && UsbBudgets.SuperSpeed(h) && controllerOf.GetValueOrDefault(h) != controllerOf.GetValueOrDefault(node));
            node.Usb3SideFailed = node.Usb3SideMissing && companion!.Kind == "Unavailable";
        }
    }
    // The hub whose USB 3 side belongs on this port: the empty, or failed, USB 3 half of the socket that
    // hub's USB 2 side is plugged into.
    internal static UsbNode? MissingUsb3HubFor(UsbNode port, Snapshot snapshot) => port.Kind is "Empty port" or "Unavailable"
        ? snapshot.Nodes.FirstOrDefault(h => h.Usb3SideMissing && h.CompanionId == port.Id) : null;
    internal static string CardLabel(UsbNode node) => node.CompanionHubId.Length > 0
        ? node.IsUsb2Companion ? "USB 2 side · paired hub" : "USB 3 side · paired hub"
        : node.Kind == "Hub" && node.PortIsUserConnectable == false ? "Built-in hub" : "";
    internal static bool Usb2HubAtNativeSpeed(UsbNode node) => node.Kind == "Hub" && node.LinkMbps == 480 && node.UsbVersion.StartsWith("USB 2.", StringComparison.Ordinal);
    // A USB 3 hub's USB 2 side reports the SuperSpeed capability of its other side, but 480 Mb/s is its
    // native rate, so only pairing, known after Analyze, says whether a slower link is a problem: it is
    // when the USB 3 side is missing.
    internal static bool ReducedSpeed(UsbNode node) => node.SpeedLimited && !node.IsUsb2Companion && (!Usb2HubAtNativeSpeed(node) || node.Usb3SideMissing)
        || FullSpeedOnly(node);
    // A device that supports USB 2's high speed, linked at 12 Mb/s.
    internal static bool FullSpeedOnly(UsbNode node) => node.LinkMbps == 12 && node.HighSpeedCapable == true;
    internal const string FullSpeedNote = "Speed evidence: this device answers the request for its high-speed details (its device qualifier), so it supports USB 2's 480 Mb/s, but it is connected at 12 Mb/s.";
    // Evidence for Detection details; what it means and what to do lead Properties (Explanations.Speed).
    internal const string ReducedSpeedNote = "Speed evidence: Windows reports that this device supports a faster link than its current connection.";
    internal const string Usb3SideMissingNote = "Speed evidence: this hub reports USB 3 support but is connected at 480 Mb/s, no USB 3 side of it appears in this scan, and Windows reports the USB 3 half of its socket empty.";
    internal const string Usb3SideFailedNote = "Speed evidence: this hub reports USB 3 support but is connected at 480 Mb/s, no USB 3 side of it appears in this scan, and Windows reports an error on the USB 3 half of its socket.";
    internal static void NoteReducedSpeed(Snapshot snapshot)
    {
        foreach (var node in snapshot.Nodes.Where(ReducedSpeed))
        {
            var note = node.Usb3SideFailed ? Usb3SideFailedNote : node.Usb3SideMissing ? Usb3SideMissingNote : FullSpeedOnly(node) && !node.SpeedLimited ? FullSpeedNote : ReducedSpeedNote;
            if (!node.Notes.Contains(note)) node.Notes.Add(note);
        }
    }
    internal static string Description(UsbNode node, Snapshot snapshot)
    {
        if (node.Kind != "Hub") return "";
        var text = node.CompanionHubId.Length > 0
            ? "Windows identifies this as the " + (node.IsUsb2Companion ? "USB 2" : "USB 3") + " side of a shared hub connection. Both sides appear separately in the topology; they do not represent two external boxes. " + (node.IsUsb2Companion ? "480 Mb/s is normal on this side even when the USB 3 side runs faster." : "USB 2 devices use the companion side.")
            : node.Usb3SideMissing
            ? "This is the USB 2 side of a USB 3 hub. Its USB 3 side isn't connected, so it runs at USB 2."
            : "This is a logical hub reported by Windows. One physical enclosure can contain several hub chips, and USB 3 hubs can expose separate USB 2 and USB 3 sides. This scan has not confirmed a companion pairing.";
        if (snapshot.Nodes.Any(n => n.Kind == "Hub" && n.Children.Contains(node)))
            text += " This hub is downstream of another hub. It may be another chip inside the same enclosure or a separately connected hub; the enclosure boundary is not reported.";
        if (node.PortIsUserConnectable == false) text += " Windows reports its upstream port as not user-accessible; this does not identify which enclosure contains it.";
        return text;
    }
}

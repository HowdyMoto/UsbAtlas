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
    }
    internal static void Analyze(Snapshot snapshot)
    {
        var hubs = snapshot.Nodes.Where(n => n.Kind is "Hub" or "Root hub").ToList();
        foreach (var hub in hubs) { hub.CompanionHubId = ""; hub.IsUsb2Companion = false; }
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
    }
    internal static string CardLabel(UsbNode node) => node.CompanionHubId.Length > 0
        ? node.IsUsb2Companion ? "USB 2 side · paired hub" : "USB 3 side · paired hub"
        : node.Kind == "Hub" && node.PortIsUserConnectable == false ? "Internal connection · reported" : "";
    internal static bool Usb2HubAtNativeSpeed(UsbNode node) => node.Kind == "Hub" && node.LinkMbps == 480 && node.UsbVersion.StartsWith("USB 2.", StringComparison.Ordinal);
    internal static string Description(UsbNode node, Snapshot snapshot)
    {
        if (node.Kind != "Hub") return "";
        var text = node.CompanionHubId.Length > 0
            ? "Windows identifies this as the " + (node.IsUsb2Companion ? "USB 2" : "USB 3") + " side of a shared hub connection. Both sides appear separately in the topology; they do not represent two external boxes. " + (node.IsUsb2Companion ? "480 Mb/s is normal on this side even when the USB 3 side runs faster." : "USB 2 devices use the companion side.")
            : "This is a logical hub reported by Windows. One physical enclosure can contain several hub chips, and USB 3 hubs can expose separate USB 2 and USB 3 sides. This scan has not confirmed a companion pairing.";
        if (snapshot.Nodes.Any(n => n.Kind == "Hub" && n.Children.Contains(node)))
            text += " This hub is downstream of another hub. It may be another chip inside the same enclosure or a separately connected hub; the enclosure boundary is not reported.";
        if (node.PortIsUserConnectable == false) text += " Windows reports its upstream port as not user-accessible; this does not identify which enclosure contains it.";
        return text;
    }
}

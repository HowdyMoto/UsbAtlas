namespace UsbAtlas;

// Companion links are reported by Windows; names and matching chip IDs alone do not prove a pair.
internal static class HubRelationships
{
    private static string Normalize(string path) => path.Trim().Replace("\\\\?\\", "").Replace("\\\\.\\", "").ToUpperInvariant();
    internal static void Analyze(Snapshot snapshot)
    {
        var hubs = snapshot.Nodes.Where(n => n.Kind is "Hub" or "Root hub").ToList();
        foreach (var hub in hubs) { hub.CompanionHubId = ""; hub.IsUsb2Companion = false; }
        foreach (var parent in hubs)
        foreach (var node in parent.Children.Where(n => n.Kind == "Hub" && n.CompanionPortNumber > 0 && n.CompanionHubSymbolicLink.Length > 0))
        {
            var companionParent = hubs.FirstOrDefault(h => h.HubSymbolicLink.Length > 0 && Normalize(h.HubSymbolicLink) == Normalize(node.CompanionHubSymbolicLink));
            var other = companionParent?.Children.FirstOrDefault(n => n.Port == node.CompanionPortNumber && n.Kind == "Hub");
            if (other == null || other.Id == node.Id || node.VendorId != other.VendorId || node.VendorId.Length == 0) continue;
            bool nodeUsb2 = node.LinkMbps is > 0 and <= 480;
            bool otherUsb2 = other.LinkMbps is > 0 and <= 480;
            bool nodeUsb3 = node.LinkMbps >= 5000 || node.Speed.StartsWith("SuperSpeed", StringComparison.Ordinal);
            bool otherUsb3 = other.LinkMbps >= 5000 || other.Speed.StartsWith("SuperSpeed", StringComparison.Ordinal);
            if (!(nodeUsb2 && otherUsb3 || otherUsb2 && nodeUsb3)) continue;
            node.CompanionHubId = other.Id; other.CompanionHubId = node.Id;
            node.IsUsb2Companion = nodeUsb2; other.IsUsb2Companion = otherUsb2;
        }
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

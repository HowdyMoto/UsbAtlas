namespace UsbAtlas;

// Fast drive enclosures support UAS (USB Attached SCSI). When Windows falls back to the older bulk-only
// protocol, the drive works but moves less and uses more of the processor. Whether a drive offers UAS comes
// from its interface descriptors (protocol 0x62 in any alternate setting); what Windows uses, from the driver
// it bound: UASPStor or USBSTOR. Flash drives and card readers that offer only bulk-only are as they should be.
internal static class Uas
{
    internal const string NotInUse = "UAS not in use";

    // Any mass storage interface, in any alternate setting, that speaks UAS.
    internal static bool Offers(byte[] configuration)
    {
        var limit = configuration.Length >= 4 ? Math.Min(configuration.Length, BitConverter.ToUInt16(configuration, 2)) : 0;
        for (int offset = 0; offset + 2 <= limit;)
        {
            int length = configuration[offset];
            if (length < 2 || offset + length > limit) break;
            if (configuration[offset + 1] == 4 && length >= 9 && configuration[offset + 5] == 8 && configuration[offset + 7] == 0x62) return true;
            offset += length;
        }
        return false;
    }

    // The protocol Windows is using, from the storage driver on the device or one of its functions.
    internal static string Protocol(IEnumerable<string> services)
    {
        var list = services.ToList();
        return list.Any(s => s.Equals("UASPStor", StringComparison.OrdinalIgnoreCase)) ? "UAS"
            : list.Any(s => s.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase)) ? "Bulk-only" : "";
    }

    internal static void Apply(Snapshot snapshot, IReadOnlyDictionary<string, UsbScanner.DevNode> devices)
    {
        var services = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, node) in devices)
            if (UsbScanner.UsbOwner(instance, devices) is string owner)
            {
                if (!services.TryGetValue(owner, out var list)) services[owner] = list = [];
                list.Add(node.Service);
            }
        foreach (var n in snapshot.Nodes.Where(n => n.Kind == "Device" && n.InstanceId.Length > 0))
            n.StorageProtocol = Protocol(services.GetValueOrDefault(n.InstanceId) ?? []);
    }

    internal static bool NotUsed(UsbNode n) => n.Kind == "Device" && n.OffersUas == true && n.StorageProtocol == "Bulk-only";

    // Properties' Storage protocol row; empty for what isn't a drive.
    internal static string Summary(UsbNode n) => n.StorageProtocol switch
    {
        "UAS" => "UAS",
        "Bulk-only" => n.OffersUas == true ? "Bulk-only · supports UAS" : "Bulk-only",
        _ => n.OffersUas == true ? "Supports UAS · not in use" : ""
    };

    // path runs from the host controller down to n. The likeliest cause leads: a USB 2 link, then what's in between.
    internal static Explanations.Explanation Explain(UsbNode n, IReadOnlyList<UsbNode> path)
    {
        var steps = new List<string>();
        if (n.LinkMbps is <= 480)
            steps.Add("It's connected at USB 2 speed, and many enclosures offer UAS only over USB 3. Plug it into a USB 3 port on the computer, with a cable rated 5 Gb/s or faster.");
        if (path.SkipLast(1).LastOrDefault(p => p.Kind == "Hub") is UsbNode hub)
            steps.Add($"Plug it straight into a port on the computer instead of through {Topology.ShortName(hub)}.");
        steps.Add("If it still runs bulk-only, Windows may not use UAS with its enclosure's chip or firmware; check the enclosure maker for a firmware update.");
        return new("This drive supports UAS, the faster protocol for USB drives, but Windows is using the older bulk-only protocol with it.",
            "Yes: transfers are slower and use more of the processor than they could. It works otherwise.", "", steps);
    }
}

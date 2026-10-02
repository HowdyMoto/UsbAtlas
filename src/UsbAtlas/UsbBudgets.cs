namespace UsbAtlas;

// Bandwidth a device reserves and power it requests, from its descriptors. Neither is a measurement:
// periodic (interrupt and isochronous) endpoints reserve bus time when opened, bulk and control share
// what is left, and MaxPower is the most a configuration may draw. The power checks compare declared
// draw with what the USB specification guarantees a port; real hubs and ports may supply more.
internal static class UsbBudgets
{
    internal sealed record Endpoint(int Interface, int Alternate, byte Address, byte Attributes, ushort MaxPacket, byte Interval, int? BytesPerInterval);

    // Speed classes follow USB_DEVICE_SPEED: 0 low, 1 full, 2 high, 3 SuperSpeed or faster.
    internal static double PeriodicMbps(byte attributes, ushort maxPacket, byte interval, int speedClass, int? bytesPerInterval = null)
    {
        int type = attributes & 3;
        if (type is not (1 or 3)) return 0;
        int exponent = Math.Clamp((int)interval, 1, 16) - 1;
        double bytes, seconds;
        if (speedClass >= 3) { bytes = bytesPerInterval ?? (maxPacket & 0x7FF); seconds = 125e-6 * (1 << exponent); }
        else if (speedClass == 2) { bytes = (maxPacket & 0x7FF) * (1 + ((maxPacket >> 11) & 3)); seconds = 125e-6 * (1 << exponent); }
        else { bytes = maxPacket & 0x3FF; seconds = type == 1 ? 1e-3 * (1 << exponent) : 1e-3 * Math.Max(1, (int)interval); }
        return bytes * 8 / seconds / 1e6;
    }

    internal static List<Endpoint> ReadEndpoints(byte[] descriptor)
    {
        var result = new List<Endpoint>();
        int limit = descriptor.Length >= 4 ? Math.Min(descriptor.Length, BitConverter.ToUInt16(descriptor, 2)) : 0;
        int iface = -1, alternate = 0;
        for (int offset = 0; offset + 2 <= limit;)
        {
            int length = descriptor[offset];
            if (length < 2 || offset + length > limit) break;
            byte type = descriptor[offset + 1];
            if (type == 4 && length >= 9) { iface = descriptor[offset + 2]; alternate = descriptor[offset + 3]; }
            else if (type == 5 && length >= 7 && iface >= 0)
                result.Add(new(iface, alternate, descriptor[offset + 2], descriptor[offset + 3], BitConverter.ToUInt16(descriptor, offset + 4), descriptor[offset + 6], null));
            // SuperSpeed companions give the real bytes per service interval for periodic endpoints.
            else if (type == 0x30 && length >= 6 && result.Count > 0)
                result[^1] = result[^1] with { BytesPerInterval = BitConverter.ToUInt16(descriptor, offset + 4) };
            else if (type == 0x31 && length >= 8 && result.Count > 0)
                result[^1] = result[^1] with { BytesPerInterval = BitConverter.ToInt32(descriptor, offset + 4) };
            offset += length;
        }
        return result;
    }

    // The most the active configuration can reserve: each interface's busiest alternate setting.
    internal static double PeakPeriodicMbps(List<Endpoint> endpoints, int speedClass) =>
        endpoints.GroupBy(e => e.Interface).Sum(i => i.GroupBy(e => e.Alternate).Max(alt => alt.Sum(e => PeriodicMbps(e.Attributes, e.MaxPacket, e.Interval, speedClass, e.BytesPerInterval))));

    // The most payload the host will reserve for periodic transfers on a link: 90% of a low- or
    // full-speed frame, 80% of a high-speed microframe, and 90% of SuperSpeed bus time after line
    // encoding (8b/10b at 5 Gb/s, 128b/132b beyond). SuperSpeedPlus lane rates aren't resolved, so 10 Gb/s is assumed.
    internal static double? ReservableMbps(UsbNode n) => n.LinkMbps switch
    {
        1.5 => 1.35, 12 => 10.8, 480 => 384, 5000 => 3600,
        _ => n.Speed.StartsWith("SuperSpeedPlus", StringComparison.Ordinal) ? 10000 * 128.0 / 132 * 0.9 : null
    };

    // Reservations that share a node's upstream link: its own and, for a hub, everything behind it.
    internal static (double Mbps, int Unknown) ReservedThroughLink(UsbNode n)
    {
        double total = n.ReservedMbps ?? 0; int unknown = n.ReservedMbps == null ? 1 : 0;
        if (n.Kind == "Hub")
            foreach (var child in n.Children.Where(c => c.Kind is "Device" or "Hub"))
            {
                var (mbps, missing) = ReservedThroughLink(child); total += mbps; unknown += missing;
            }
        return (total, unknown);
    }

    // How full a device's or hub's link is with reservations, when both sides are known.
    internal static (double Reserved, double Capacity, int Unknown)? LinkUse(UsbNode n)
    {
        if (n.Kind is not ("Device" or "Hub") || n.ReservedMbps == null || ReservableMbps(n) is not double capacity) return null;
        var (reserved, unknown) = ReservedThroughLink(n);
        return (reserved, capacity, unknown);
    }

    // Past this share of what a link can reserve, the next audio, video or input device may be refused.
    internal const double NearlyFullShare = 0.8;
    internal static bool LinkNearlyFull(UsbNode n) => LinkUse(n) is (var reserved, var capacity, _) && reserved / capacity >= NearlyFullShare;

    internal static string Share(double reserved, double capacity)
    {
        double percent = reserved / capacity * 100;
        return (percent is > 0 and < 1 ? "<1" : $"{Math.Round(percent):0}") + "% of " + Rate(capacity);
    }

    internal static string Rate(double mbps) => mbps switch
    {
        0 => "0 Mb/s",
        >= 1000 => $"{mbps / 1000:0.#} Gb/s",
        >= 100 => $"{mbps:0} Mb/s",
        >= 1 => $"{mbps:0.#} Mb/s",
        < 0.0001 => "<0.1 kb/s",
        _ => $"{mbps * 1000:0.#} kb/s"
    };

    internal static string DescribePipe(byte address, byte attributes, ushort maxPacket, byte interval, int speedClass, int? bytesPerInterval)
    {
        string type = (attributes & 3) switch { 0 => "control", 1 => "isochronous", 2 => "bulk", _ => "interrupt" };
        string head = $"Endpoint {address:X2} {((address & 0x80) != 0 ? "IN" : "OUT")} {type}";
        double mbps = PeriodicMbps(attributes, maxPacket, interval, speedClass, bytesPerInterval);
        return (attributes & 3) is 1 or 3 ? $"{head}: reserves {Rate(mbps)}" : $"{head}: shares unreserved bandwidth";
    }

    internal static string FaultNote(string status) => status == "Overcurrent"
        ? "Windows reports that the device on this port drew more current than the port allows, so the port was switched off. Unplug it and reconnect it to a powered hub or a different port; a damaged cable or device can also cause this."
        : "Windows refused to configure the device on this port because it asks for more power than the port can supply. Connect it to a powered hub or directly to the computer.";

    internal static bool IsPowerFault(UsbNode n) => n.Kind == "Unavailable" && n.Status is "Insufficient power" or "Overcurrent";

    // Declared draw a node pulls through its upstream port. A bus-powered hub passes its devices' draw upstream.
    internal static (int Known, int Unknown) Demand(UsbNode n)
    {
        var (known, unknown) = n.MaxPowerMa is int ma ? (ma, 0) : (0, 1);
        if (n.Kind == "Hub" && n.PowerSource == "Bus powered")
            foreach (var child in n.Children.Where(c => c.Kind is "Device" or "Hub"))
            {
                var d = Demand(child); known += d.Known; unknown += d.Unknown;
            }
        return (known, unknown);
    }

    internal static bool SuperSpeed(UsbNode n) => n.LinkMbps >= 5000 || n.Speed.StartsWith("SuperSpeed", StringComparison.Ordinal);

    internal static void AnalyzePower(Snapshot snapshot)
    {
        foreach (var hub in snapshot.Nodes.Where(n => n.Kind == "Hub").ToList())
        {
            bool bus = hub.PowerSource == "Bus powered";
            if (bus && hub.SelfPowerCapable == true)
                Warn(hub, "Hub adapter not detected", "This hub's descriptor says it can run from its own power supply, but Windows reports it running on bus power. If it has a power adapter, check that it is plugged in and switched on. Until then its devices share the power of one upstream port.");
            if (!bus) continue;
            bool super = SuperSpeed(hub);
            int upstream = super ? 900 : 500;
            int children = 0, unknown = 0;
            foreach (var child in hub.Children.Where(c => c.Kind is "Device" or "Hub"))
            {
                var (known, missing) = Demand(child);
                children += known; unknown += missing;
                int port = SuperSpeed(child) ? 150 : 100;
                if (known <= port) continue;
                Warn(child, "Power at risk", child.Kind == "Hub" && child.PowerSource == "Bus powered"
                    ? $"This bus-powered hub and its devices declare {known} mA, but a port on a bus-powered hub only guarantees {port} mA. Bus-powered hubs should not be chained; give one of them a power adapter."
                    : $"Declares up to {known} mA, but a port on a bus-powered hub only guarantees {port} mA. It may disconnect or misbehave under load. Connect it to a powered hub or a computer port.");
            }
            int own = hub.MaxPowerMa ?? 0, total = own + children;
            if (total > upstream)
                Warn(hub, "Over power budget", $"Devices on this bus-powered hub declare {children} mA, plus {own} mA for the hub itself: {total} mA in all. A standard USB {(super ? "3" : "2")} port guarantees {upstream} mA. USB-C and charging ports can supply more, but Windows does not report that." + (unknown > 0 ? $" {unknown} device(s) did not report their draw." : ""));
        }
    }

    private static void Warn(UsbNode node, string label, string explanation)
    {
        if (node.PowerWarnings.Contains(label)) return;
        node.PowerWarnings.Add(label);
        node.Notes.Add(explanation);
    }
}

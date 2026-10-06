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
    // encoding (8b/10b on 5 Gb/s lanes, 128b/132b on faster ones). When Windows doesn't report a
    // SuperSpeedPlus link's rate, 10 Gb/s is assumed.
    internal const double FullSpeedReservableMbps = 10.8;
    internal static double? ReservableMbps(UsbNode n) => n.LinkMbps switch
    {
        1.5 => 1.35, 12 => FullSpeedReservableMbps, 480 => 384, 5000 => 3600,
        > 5000 and var fast => fast * (fast / (n.LinkLanes ?? 1) <= 5000 ? 0.8 : 128.0 / 132) * 0.9,
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

    // The most a node's link could reserve with it and everything behind it at their busiest settings.
    // Cameras and audio reserve little while idle and far more while streaming.
    internal static (double Mbps, int Unknown) PeakThroughLink(UsbNode n)
    {
        double total = Math.Max(n.PeakReservedMbps ?? 0, n.ReservedMbps ?? 0);
        int unknown = n.ReservedMbps == null && n.PeakReservedMbps == null ? 1 : 0;
        if (n.Kind == "Hub")
            foreach (var child in n.Children.Where(c => c.Kind is "Device" or "Hub"))
            {
                var (mbps, missing) = PeakThroughLink(child); total += mbps; unknown += missing;
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
    // Idle cameras and audio reserve almost nothing, so a link can look empty until they stream. This
    // flags a link whose known peaks would not fit at once; a link already nearly full warns as that instead.
    internal static bool CouldExceedWhenStreaming(UsbNode n) =>
        !LinkNearlyFull(n) && LinkUse(n) is (_, var capacity, _) && PeakThroughLink(n).Mbps > capacity;

    // Full- and low-speed devices behind a high-speed hub don't use its 480 Mb/s directly: a transaction
    // translator (TT) runs their periodic transfers on a 12 Mb/s bus, which reserves at most 90% of each
    // frame, as a full-speed link does. A single-TT hub shares that one bus among all its ports, so devices
    // that each fit their own link can overflow it together. A low-speed byte takes eight full-speed byte
    // times. Ports counts the ports whose devices use the TT.
    internal static (double Now, double Peak, int Unknown, int Ports)? SharedTtUse(UsbNode hub)
    {
        if (hub.Kind != "Hub" || hub.TransactionTranslators != "Single") return null;
        double now = 0, peak = 0; int unknown = 0, ports = 0;
        foreach (var user in hub.Children.Where(c => c.Kind is "Device" or "Hub" && c.LinkMbps is 1.5 or 12))
        {
            var t = FullSpeedBusTime(user); now += t.Now; peak += t.Peak; unknown += t.Unknown; ports++;
        }
        return (now, Math.Max(now, peak), unknown, ports);
    }
    private static (double Now, double Peak, int Unknown) FullSpeedBusTime(UsbNode n)
    {
        double scale = n.LinkMbps == 1.5 ? 8 : 1;
        double now = (n.ReservedMbps ?? 0) * scale, peak = Math.Max(n.PeakReservedMbps ?? 0, n.ReservedMbps ?? 0) * scale;
        int unknown = n.ReservedMbps == null ? 1 : 0;
        if (n.Kind == "Hub")
            foreach (var child in n.Children.Where(c => c.Kind is "Device" or "Hub"))
            {
                var t = FullSpeedBusTime(child); now += t.Now; peak += t.Peak; unknown += t.Unknown;
            }
        return (now, peak, unknown);
    }
    // Devices on one port already warn against their own 12 Mb/s link; the shared TT adds a warning only
    // when devices on two or more ports share it.
    internal static bool SharedTtNearlyFull(UsbNode hub) =>
        SharedTtUse(hub) is (var now, _, _, >= 2) && now / FullSpeedReservableMbps >= NearlyFullShare;
    internal static bool SharedTtCouldExceed(UsbNode hub) =>
        !SharedTtNearlyFull(hub) && SharedTtUse(hub) is (_, var peak, _, >= 2) && peak > FullSpeedReservableMbps;

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

    // How often the host polls an interrupt endpoint. At low and full speed bInterval counts 1 ms frames,
    // and hosts poll at the largest power of two that fits (xHCI and EHCI schedules are power-of-two),
    // so 10 ms is polled every 8 ms. At high speed and faster it is 2^(bInterval-1) microframes of 125 µs.
    internal static double PollIntervalMs(byte interval, int speedClass) => speedClass >= 2
        ? 0.125 * (1 << (Math.Clamp((int)interval, 1, 16) - 1))
        : 1 << (int)Math.Log2(Math.Clamp((int)interval, 1, 255));
    internal static string PollingRate(double ms)
    {
        double hz = 1000 / ms;
        return hz >= 100 ? $"{hz:0} Hz" : $"{hz:0.#} Hz";
    }
    internal static string PollingInterval(double ms) => ms >= 1 ? $"every {ms:0.###} ms" : $"every {ms * 1000:0} µs";
    internal static string PollingNote(byte address, byte interval, int speedClass)
    {
        double ms = PollIntervalMs(interval, speedClass);
        string note = $"Polling: the host asks input endpoint {address:X2} for new data {PollingInterval(ms)} ({PollingRate(ms)}).";
        if (speedClass < 2 && Math.Max(1, (int)interval) != ms)
            note += $" Its descriptor asks for every {Math.Max(1, (int)interval)} ms; hosts poll full- and low-speed devices at the next shorter power-of-two interval.";
        return note + " This is how often the host asks, set by the device's descriptor. A device skips a poll when it has nothing new, and its sensors or firmware may update less often, so it is not a measured report rate.";
    }

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

    // A device takes one of its xHCI controller's device slots, and an endpoint for its control pipe plus one for
    // each open pipe. Controllers hold only so many endpoints and don't say how many; some common Intel ones top
    // out at 96, and Windows then refuses the next device with "Not enough USB controller resources".
    internal static (int Devices, int Endpoints, int Unreported) ControllerLoad(UsbNode controller)
    {
        var connected = controller.Walk().Where(n => n.Kind is "Device" or "Hub").ToList();
        return (connected.Count, connected.Sum(n => n.OpenPipes.Count + 1), connected.Count(n => n.ReservedMbps == null));
    }
    // Two thirds of the 96 some controllers allow: early enough to point at the controller before it runs out.
    internal const int ManyEndpoints = 64;
    internal static bool EndpointsRunningHigh(UsbNode n) => n.Kind == "Controller" && ControllerLoad(n).Endpoints >= ManyEndpoints;

    // A PCIe link after line encoding: 8b/10b up to 5 GT/s, 128b/130b up to 32 GT/s, and FLIT at 64 GT/s.
    internal static double? PcieMbps(int? generation, int? lanes) => generation is >= 1 and <= 6 && lanes is > 0
        ? new[] { 2000.0, 4000, 7877, 15754, 31508, 60500 }[generation.Value - 1] * lanes.Value : null;
    internal static string PcieText(int generation, int lanes) => $"PCIe {generation}.0 ×{lanes}";
    // What a USB link carries after line encoding: 8b/10b at 5 Gb/s, 128b/132b beyond.
    internal static double? UsbDataMbps(UsbNode n) => n.LinkMbps switch { 5000 => 4000, > 5000 and double rate => rate * 128 / 132, _ => null };
    // The most a socket can carry, from a device linked there or the socket's own speed.
    private static double? SocketDataMbps(UsbNode port) => UsbDataMbps(port) ?? port.SocketSpeed switch { "≥10 Gb/s" => 10000 * 128.0 / 132, "≥5 Gb/s" or "5 Gb/s" => 4000, _ => null };

    // Everything on a controller's root ports shares its PCIe link to the computer: its usable rate, what its
    // fastest port could carry, what's linked to its ports now, and a device linked faster than it alone.
    internal static (double Uplink, double Fastest, double Linked, UsbNode? Capped)? Uplink(UsbNode controller)
    {
        if (controller.Kind != "Controller" || PcieMbps(controller.PcieGeneration, controller.PcieLanes) is not double uplink) return null;
        var ports = controller.Children.Where(r => r.Kind == "Root hub").SelectMany(r => r.Children).ToList();
        var links = ports.Where(p => p.Kind is "Device" or "Hub" && UsbDataMbps(p) != null).ToList();
        return (uplink, ports.Select(SocketDataMbps).Max() ?? 0, links.Sum(p => UsbDataMbps(p)!.Value), links.FirstOrDefault(p => UsbDataMbps(p) > uplink));
    }
    // A device held back every time it's busy is a warning; ports that could only together, or one day, outrun it, a note.
    internal static Severity? UplinkSeverity(UsbNode controller) => Uplink(controller) switch
    {
        (_, _, _, not null) => Severity.Warning,
        (var uplink, var fastest, var linked, null) when fastest > uplink || linked > uplink => Severity.Note,
        _ => null
    };

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

    internal static void Warn(UsbNode node, string label, string explanation)
    {
        if (node.PowerWarnings.Contains(label)) return;
        node.PowerWarnings.Add(label);
        node.Notes.Add(explanation);
    }
}

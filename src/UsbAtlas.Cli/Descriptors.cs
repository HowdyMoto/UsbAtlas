using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// USB descriptors decoded field by field, with the bytes they came from. Fields follow the USB 2.0 and
// 3.2 specifications' names, so they can be checked against them; anything not decoded keeps its hex.
internal static class Descriptors
{
    internal static JsonObject Report(Session s, UsbNode n)
    {
        var report = Reports.Header(s, "descriptors");
        report["node"] = Reports.Ref(s, n);
        // A merged host's descriptors are its root hub's.
        var raw = (Topology.MergedRoot(n) ?? n).Raw;
        if (raw == null)
        {
            report["note"] = s.Source == "live" ? "Windows returned no descriptors for this node." : "This snapshot has no descriptors. Take one with atlascli scan --raw, or run raw against live hardware.";
            return report;
        }
        // Speed classes follow USB_DEVICE_SPEED: 0 low, 1 full, 2 high, 3 SuperSpeed or faster.
        int speed = raw.ConnectionFlags is int f && (f & 5) != 0 ? 3 : raw.SpeedCode ?? 2;
        var deviceBytes = Bytes(raw.Device);
        ushort bcd = deviceBytes is { Length: >= 4 } ? BitConverter.ToUInt16(deviceBytes, 2) : (ushort)0;
        if (raw.ConnectionFlags is int flags)
            report["connection"] = J.Obj(("flags", $"0x{flags:X}"),
                ("operatingAtSuperSpeed", (flags & 1) != 0), ("superSpeedCapable", (flags & 2) != 0),
                ("operatingAtSuperSpeedPlus", (flags & 4) != 0), ("superSpeedPlusCapable", (flags & 8) != 0),
                ("portProtocols", raw.Protocols is int p ? string.Join(", ", new[] { (p & 1) != 0 ? "USB 1.1" : null, (p & 2) != 0 ? "USB 2.0" : null, (p & 4) != 0 ? "USB 3" : null }.Where(x => x != null)) : null),
                ("speedCode", raw.SpeedCode is int sc ? $"{sc} ({sc switch { 0 => "low", 1 => "full", 2 => "high", 3 => "SuperSpeed or faster", _ => "unknown" }}){((flags & 5) != 0 && sc < 3 ? "; a legacy field, the flags above say SuperSpeed" : "")}" : null));
        if (Bytes(raw.SuperSpeedPlus) is { Length: >= 16 } plus)
            report["superSpeedPlusLink"] = J.Obj(("rx", Sublink(plus, 0)), ("tx", Sublink(plus, 8)), ("hex", raw.SuperSpeedPlus));
        if (raw.ConnectorProperties is uint c)
            report["connector"] = J.Obj(("flags", $"0x{c:X}"), ("userConnectable", (c & 1) != 0), ("debugCapable", (c & 2) != 0), ("multipleCompanions", (c & 4) != 0), ("typeC", (c & 8) != 0));
        if (deviceBytes != null) report["device"] = J.Arr(Walk(deviceBytes, speed, bcd).Select(d => (JsonNode)d));
        if (Bytes(raw.Configuration) is { } config) report["configuration"] = J.Arr(Walk(config, speed, bcd).Select(d => (JsonNode)d));
        if (Bytes(raw.Bos) is { } bos) report["bos"] = J.Arr(Walk(bos, speed, bcd).Select(d => (JsonNode)d));
        if (Bytes(raw.Hub) is { } hub)
        {
            report["hubNote"] = "Reported by Windows (IOCTL_USB_GET_NODE_INFORMATION) in USB 2.0 hub-descriptor format. For root hubs and USB 3 hubs Windows fills it in itself, so fields such as TT think time and controller current may not describe the hardware.";
            report["hub"] = J.Arr(Walk(hub, speed, bcd).Select(d => (JsonNode)d));
        }
        return report;
    }
    // A sublink speed and its lane count, as USB_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION holds them.
    private static string Sublink(byte[] b, int at)
    {
        uint s = BitConverter.ToUInt32(b, at); uint lanes = BitConverter.ToUInt32(b, at + 4) + 1;
        double bps = (s >> 16) * Math.Pow(1000, (s >> 4) & 3);
        return $"{(bps >= 1e9 ? $"{bps / 1e9:0.##} Gb/s" : $"{bps / 1e6:0.##} Mb/s")} per lane × {lanes} lane{(lanes == 1 ? "" : "s")}, {((s >> 14) & 3) switch { 0 => "SuperSpeed", 1 => "SuperSpeedPlus", _ => "reserved" }} protocol";
    }
    private static byte[]? Bytes(string hex) { try { return hex.Length >= 4 ? Convert.FromHexString(hex) : null; } catch (FormatException) { return null; } }

    // Each descriptor in a blob, in order. A bad length stops the walk and keeps the rest as hex.
    internal static List<JsonObject> Walk(byte[] data, int speedClass, ushort bcdUsb)
    {
        var result = new List<JsonObject>();
        int iface = -1;
        for (int at = 0; at + 2 <= data.Length;)
        {
            int length = data[at];
            if (length < 2 || at + length > data.Length)
            {
                result.Add(J.Obj(("offset", at), ("error", "Bad descriptor length; the rest is shown as is."), ("hex", Convert.ToHexString(data, at, data.Length - at))));
                break;
            }
            var d = data.AsSpan(at, length).ToArray();
            if (d[1] == 4 && length >= 3) iface = d[2];
            result.Add(Decode(d, speedClass, bcdUsb, iface));
            at += length;
        }
        return result;
    }

    private static ushort U16(byte[] d, int i) => i + 1 < d.Length ? BitConverter.ToUInt16(d, i) : (ushort)0;
    private static string Bcd(ushort v) => $"{v >> 8:X}.{(v >> 4) & 15:X}{v & 15:X}";

    internal static JsonObject Decode(byte[] d, int speedClass, ushort bcdUsb, int iface = -1)
    {
        byte type = d[1];
        var o = J.Obj(("type", $"0x{type:X2} {TypeName(type)}"));
        void F(string name, JsonNode? value) { if (value != null) o[name] = value; }
        bool has(int n) => d.Length >= n;
        switch (type)
        {
            case 1 when has(18):
                F("bcdUSB", Bcd(U16(d, 2))); F("bDeviceClass", ClassText(d[4])); F("bDeviceSubClass", d[5]); F("bDeviceProtocol", d[6]);
                // From USB 3.0 it's an exponent: 9 means 512 bytes.
                F("bMaxPacketSize0", bcdUsb >= 0x0300 && d[7] < 16 ? $"{d[7]} (2^{d[7]} = {1 << d[7]} bytes)" : d[7].ToString()); F("idVendor", $"{U16(d, 8):X4}"); F("idProduct", $"{U16(d, 10):X4}"); F("bcdDevice", Bcd(U16(d, 12)));
                F("iManufacturer", d[14]); F("iProduct", d[15]); F("iSerialNumber", d[16]); F("bNumConfigurations", d[17]);
                break;
            case 2 when has(9):
                F("wTotalLength", U16(d, 2)); F("bNumInterfaces", d[4]); F("bConfigurationValue", d[5]); F("iConfiguration", d[6]);
                F("bmAttributes", $"0x{d[7]:X2}{((d[7] & 0x40) != 0 ? " self-powered" : "")}{((d[7] & 0x20) != 0 ? " remote-wakeup" : "")}");
                F("bMaxPower", $"{d[8]} ({UsbScanner.DecodePower(d[8], bcdUsb)} mA)");
                break;
            case 4 when has(9):
                F("bInterfaceNumber", d[2]); F("bAlternateSetting", d[3]); F("bNumEndpoints", d[4]);
                F("bInterfaceClass", ClassText(d[5])); F("bInterfaceSubClass", d[6]); F("bInterfaceProtocol", d[7]); F("iInterface", d[8]);
                break;
            case 5 when has(7):
            {
                byte address = d[2], attributes = d[3], interval = d[6];
                ushort packet = U16(d, 4);
                string transfer = (attributes & 3) switch { 0 => "control", 1 => "isochronous", 2 => "bulk", _ => "interrupt" };
                F("bEndpointAddress", $"0x{address:X2} (endpoint {address & 15} {((address & 0x80) != 0 ? "IN" : "OUT")})");
                F("bmAttributes", $"0x{attributes:X2} ({transfer}{((attributes & 3) == 1 ? $", {((attributes >> 2) & 3) switch { 0 => "no sync", 1 => "async", 2 => "adaptive", _ => "sync" }}, {((attributes >> 4) & 3) switch { 0 => "data", 1 => "feedback", 2 => "implicit feedback", _ => "reserved" }}" : "")})");
                F("wMaxPacketSize", $"0x{packet:X4} ({packet & 0x7FF} bytes{(((packet >> 11) & 3) > 0 ? $" × {1 + ((packet >> 11) & 3)} per microframe" : "")})");
                if ((attributes & 3) is 1 or 3) F("bInterval", $"{interval} (serviced every {ServiceMs(interval, speedClass, attributes):0.###} ms)");
                else F("bInterval", interval);
                if (iface >= 0) F("interface", iface);
                break;
            }
            case 11 when has(8):
                F("bFirstInterface", d[2]); F("bInterfaceCount", d[3]); F("bFunctionClass", ClassText(d[4])); F("bFunctionSubClass", d[5]); F("bFunctionProtocol", d[6]); F("iFunction", d[7]);
                break;
            case 0x21 when has(9):
                F("bcdHID", Bcd(U16(d, 2))); F("bCountryCode", d[4]); F("bNumDescriptors", d[5]); F("bDescriptorType", $"0x{d[6]:X2}"); F("wDescriptorLength", U16(d, 7));
                break;
            case 0x30 when has(6):
                F("bMaxBurst", d[2]); F("bmAttributes", $"0x{d[3]:X2}"); F("wBytesPerInterval", U16(d, 4));
                break;
            case 0x31 when has(8):
                F("dwBytesPerInterval", BitConverter.ToUInt32(d, 4));
                break;
            case 15 when has(5):
                F("wTotalLength", U16(d, 2)); F("bNumDeviceCaps", d[4]);
                break;
            case 16 when has(3):
                DecodeCapability(d, F);
                break;
            case 0x29 when has(7):
            {
                ushort ch = U16(d, 3);
                F("bNbrPorts", d[2]);
                F("wHubCharacteristics", $"0x{ch:X4} (power switching {((ch & 3) switch { 0 => "ganged", 1 => "per port", _ => "none" })}, {((ch & 4) != 0 ? "compound device" : "not compound")}, overcurrent {((ch >> 3) & 3) switch { 0 => "global", 1 => "per port", _ => "none" }}, TT think time {8 * (1 + ((ch >> 5) & 3))} FS bit times{((ch & 0x80) != 0 ? ", port indicators" : "")})");
                F("bPwrOn2PwrGood", $"{d[5]} ({d[5] * 2} ms)"); F("bHubContrCurrent", $"{d[6]} mA");
                if (d.Length > 7) F("DeviceRemovable", Convert.ToHexString(d, 7, Math.Min((d[2] + 8) / 8, d.Length - 7)));
                break;
            }
            case 0x2A when has(12):
            {
                ushort ch = U16(d, 3);
                F("bNbrPorts", d[2]);
                F("wHubCharacteristics", $"0x{ch:X4} (power switching {((ch & 3) switch { 0 => "ganged", 1 => "per port", _ => "none" })}, {((ch & 4) != 0 ? "compound device" : "not compound")}, overcurrent {((ch >> 3) & 3) switch { 0 => "global", 1 => "per port", _ => "none" }})");
                F("bPwrOn2PwrGood", $"{d[5]} ({d[5] * 2} ms)"); F("bHubContrCurrent", $"{d[6]} ({d[6] * 4} mA)");
                F("bHubHdrDecLat", d[7]); F("wHubDelay", $"{U16(d, 8)} ns"); F("DeviceRemovable", $"0x{U16(d, 10):X4}");
                break;
            }
        }
        o["hex"] = Convert.ToHexString(d);
        return o;
    }

    private static void DecodeCapability(byte[] d, Action<string, JsonNode?> F)
    {
        byte cap = d[2];
        F("bDevCapabilityType", $"0x{cap:X2} {cap switch { 1 => "Wireless USB", 2 => "USB 2.0 Extension", 3 => "SuperSpeed USB", 4 => "Container ID", 5 => "Platform", 0x0A => "SuperSpeedPlus USB", 0x0B => "Precision Time Measurement", 0x0D => "Billboard", 0x0E => "Authentication", 0x0F => "Billboard Ex", 0x10 => "Configuration Summary", _ => "other" }}");
        switch (cap)
        {
            case 2 when d.Length >= 7:
            {
                uint a = BitConverter.ToUInt32(d, 3);
                F("bmAttributes", $"0x{a:X8}{((a & 2) != 0 ? " LPM" : "")}{((a & 4) != 0 ? " BESL" : "")}");
                break;
            }
            case 3 when d.Length >= 10:
            {
                ushort speeds = BitConverter.ToUInt16(d, 4);
                F("bmAttributes", $"0x{d[3]:X2}{((d[3] & 2) != 0 ? " LTM" : "")}");
                F("wSpeedsSupported", $"0x{speeds:X4} ({string.Join(", ", new[] { (speeds & 1) != 0 ? "low" : null, (speeds & 2) != 0 ? "full" : null, (speeds & 4) != 0 ? "high" : null, (speeds & 8) != 0 ? "5 Gb/s" : null }.Where(x => x != null))})");
                F("bFunctionalitySupport", d[6]); F("bU1DevExitLat", $"{d[7]} µs"); F("wU2DevExitLat", $"{BitConverter.ToUInt16(d, 8)} µs");
                break;
            }
            case 4 when d.Length >= 20:
                F("ContainerID", new Guid(d.AsSpan(4, 16)).ToString());
                break;
            case 5 when d.Length >= 20:
                F("PlatformCapabilityUUID", new Guid(d.AsSpan(4, 16)).ToString() switch
                {
                    "d8dd60df-4589-4cc7-9cd2-659d9e648a9f" => "d8dd60df-4589-4cc7-9cd2-659d9e648a9f (Microsoft OS 2.0 descriptors)",
                    "3408b638-09a9-47a0-8bfd-a0768815b665" => "3408b638-09a9-47a0-8bfd-a0768815b665 (WebUSB)",
                    var g => g
                });
                break;
            case 0x0A when d.Length >= 12:
            {
                uint a = BitConverter.ToUInt32(d, 4);
                int count = (int)(a & 0x1F) + 1;
                F("sublinkSpeedAttributes", count);
                var speeds = new JsonArray();
                for (int i = 0; i < count && 12 + i * 4 + 4 <= d.Length; i++)
                {
                    uint s = BitConverter.ToUInt32(d, 12 + i * 4);
                    int exponent = (int)((s >> 4) & 3); uint mantissa = s >> 16;
                    double bps = mantissa * Math.Pow(1000, exponent);
                    speeds.Add($"ID {s & 15}: {((s >> 6) & 1) switch { 0 => "symmetric", _ => "asymmetric" }} {((s >> 7) & 1) switch { 0 => "RX", _ => "TX" }} {(bps >= 1e9 ? $"{bps / 1e9:0.##} Gb/s" : $"{bps / 1e6:0.##} Mb/s")}, {((s >> 14) & 3) switch { 0 => "SuperSpeed", 1 => "SuperSpeedPlus", _ => "reserved" }} protocol");
                }
                F("speeds", speeds);
                break;
            }
        }
    }

    // How often a periodic endpoint is serviced: interrupt endpoints as the scanner polls them, and
    // full- or low-speed isochronous ones every 2^(bInterval-1) frames.
    internal static double ServiceMs(byte interval, int speedClass, byte attributes) =>
        (attributes & 3) == 1 && speedClass < 2 ? 1 << (Math.Clamp((int)interval, 1, 16) - 1) : UsbBudgets.PollIntervalMs(interval, speedClass);

    private static string TypeName(byte type) => type switch
    {
        1 => "Device", 2 => "Configuration", 3 => "String", 4 => "Interface", 5 => "Endpoint", 6 => "Device Qualifier", 7 => "Other Speed Configuration",
        8 => "Interface Power", 9 => "OTG", 10 => "Debug", 11 => "Interface Association", 15 => "BOS", 16 => "Device Capability",
        0x21 => "HID (or class-specific)", 0x22 => "HID Report", 0x24 => "Class-specific Interface", 0x25 => "Class-specific Endpoint",
        0x29 => "Hub", 0x2A => "SuperSpeed Hub", 0x30 => "SuperSpeed Endpoint Companion", 0x31 => "SuperSpeedPlus Isochronous Endpoint Companion",
        _ => "Unknown"
    };
    private static string ClassText(byte c) => $"0x{c:X2} " + c switch
    {
        0 => "(defined by interface)", 1 => "Audio", 2 => "Communications", 3 => "HID", 5 => "Physical", 6 => "Image", 7 => "Printer", 8 => "Mass storage",
        9 => "Hub", 10 => "CDC data", 11 => "Smart card", 13 => "Content security", 14 => "Video", 15 => "Personal healthcare", 16 => "Audio/video",
        17 => "Billboard", 18 => "Type-C bridge", 0xDC => "Diagnostic", 0xE0 => "Wireless controller", 0xEF => "Miscellaneous", 0xFE => "Application specific", 0xFF => "Vendor specific",
        _ => "(other)"
    };
}

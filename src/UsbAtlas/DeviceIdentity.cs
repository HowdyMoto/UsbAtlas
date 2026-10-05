namespace UsbAtlas;

internal static class DeviceIdentity
{
    internal static bool IsGenericName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var remaining = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(),
            @"\b(?:usb\s*\d*(?:\.\d+)*|generic|standard|unknown|superspeed(?:plus)?|high|speed|root|hub|composite|device|manufacturer|hid|compliant|keyboard|mouse|mass|storage|host|controller|xhci|ehci)\b|[\s\p{P}\d]+", "");
        return remaining.Length == 0;
    }
    internal static void ResolveName(UsbNode node, UsbIdDatabase? database = null)
    {
        (node.LookupVendor, node.LookupProduct) = (database ?? UsbIdDatabase.Default).Lookup(node.VendorId, node.ProductId);
        string WithVendor(string vendor, string product) => IsGenericName(vendor) || product.Contains(vendor, StringComparison.OrdinalIgnoreCase) ? product : vendor + " " + product;
        string fallback = node.ReportedProduct.Length > 0 ? node.ReportedProduct : node.WindowsName.Length > 0 ? node.WindowsName : $"USB {node.Kind.ToLowerInvariant()} {node.VendorId}:{node.ProductId}";
        if (!IsGenericName(node.ReportedProduct))
        {
            node.Name = WithVendor(node.Manufacturer, node.ReportedProduct); node.NameSource = "USB product / manufacturer descriptors";
        }
        else if (!IsGenericName(node.WindowsName))
        {
            node.Name = node.WindowsName; node.NameSource = "Windows device name";
        }
        else if (!IsGenericName(node.LookupProduct))
        {
            node.Name = WithVendor(node.LookupVendor, node.LookupProduct); node.NameSource = "USB ID lookup";
        }
        else if (!IsGenericName(node.Manufacturer))
        {
            node.Name = WithVendor(node.Manufacturer, fallback); node.NameSource = "USB manufacturer descriptor";
        }
        else if (!IsGenericName(node.LookupVendor))
        {
            node.Name = WithVendor(node.LookupVendor, fallback); node.NameSource = "USB vendor ID lookup";
        }
        else { node.Name = fallback; node.NameSource = "Generic reported name"; }
    }
    internal static string MergeProtocols(IEnumerable<string> values)
    {
        var protocols = values.SelectMany(v => v.Split(" / ", StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        var result = new[] { "USB 1.x", "USB 2.0", "USB 3.x" }.Where(protocols.Contains).ToArray();
        return result.Length == 0 ? "Not reported" : string.Join(" / ", result);
    }
    internal static void SummarizeProtocols(UsbNode node)
    {
        node.DownstreamProtocols = MergeProtocols(node.Children.Select(c => node.Kind == "Controller" ? c.DownstreamProtocols : c.Protocols));
        node.ProtocolSummaryPartial = node.ScanIncomplete || node.Children.Any(c => node.Kind == "Controller"
            ? c.ProtocolSummaryPartial || c.DownstreamProtocols == "Not reported" : c.Protocols == "Not reported");
        if (node.Kind is "Controller" or "Root hub") node.Protocols = node.DownstreamProtocols;
    }
    internal static void ApplyPortProperties(UsbNode node, uint flags)
    {
        node.PortIsUserConnectable = (flags & 1) != 0;
        node.PortConnectorIsTypeC = (flags & 8) != 0;
    }

    // A USB 3 socket is two logical ports, a USB 2 half and a USB 3 half, often on different hubs, so
    // each port is drawn as the socket both halves share.
    internal static void ClassifySockets(Snapshot snapshot)
    {
        var ports = snapshot.Nodes.Where(n => n.Kind is "Root hub" or "Hub").SelectMany(hub => hub.Children.Select(port => (Port: port, Hub: hub))).ToList();
        var byId = new Dictionary<string, (UsbNode Port, UsbNode Hub)>();
        foreach (var half in ports) byId.TryAdd(half.Port.Id, half);
        foreach (var half in ports)
            ClassifySocket(half.Port.CompanionId.Length > 0 && byId.TryGetValue(half.Port.CompanionId, out var other) ? [half, other] : [half]);
    }

    // Classifies the first half's socket from the connector Windows reports and the fastest rate either
    // half is known to carry. Ports people plug into are USB-A or USB-C, so a user-accessible port that
    // isn't USB-C is USB-A. Windows doesn't report an empty port's top rate, so 10 Gb/s needs proof: a
    // device linked at SuperSpeedPlus, or a hub that supports it.
    internal static void ClassifySocket(IReadOnlyList<(UsbNode Port, UsbNode Hub)> halves)
    {
        var (port, hub) = halves[0];
        var evidence = new List<string>();
        if (halves.Any(h => h.Port.PortConnectorIsTypeC == true))
        {
            port.Connector = "USB-C"; evidence.Add("Windows reports a USB-C socket.");
        }
        else if (halves.Any(h => h.Port.PortIsUserConnectable == true))
        {
            port.Connector = "USB-A"; evidence.Add("Windows reports a user-accessible socket that isn't USB-C, so it is drawn as USB-A.");
            if (hub.Kind == "Hub") evidence.Add("Plug-in hubs usually can't tell Windows that a socket is USB-C, so a USB-C socket on one may be drawn as USB-A.");
        }
        else if (halves.Any(h => h.Port.PortIsUserConnectable == false))
        {
            port.Connector = "Internal"; evidence.Add("Windows marks this port as not user-accessible: usually a built-in device or internal connection, with no socket to plug into.");
        }
        else
        {
            port.Connector = "Not reported"; evidence.Add("Windows did not report this port's connector.");
        }
        var super = halves.Where(h => h.Port.Protocols.Contains("USB 3.x")).ToList();
        if (halves.Any(h => h.Port.Speed.StartsWith("SuperSpeedPlus")))
        {
            port.SocketSpeed = "≥10 Gb/s"; evidence.Add("A device in this socket is linked at SuperSpeedPlus, 10 Gb/s or faster.");
        }
        else if (super.Any(h => h.Hub.SuperSpeedPlusCapable == true))
        {
            port.SocketSpeed = "≥10 Gb/s"; evidence.Add("Its hub supports SuperSpeedPlus, so this USB 3 socket is taken to carry 10 Gb/s or faster.");
        }
        else if (super.Count > 0)
        {
            // A plug-in hub that doesn't support SuperSpeedPlus caps its sockets at 5 Gb/s; a host's may be faster.
            bool capped = super.All(h => h.Hub.Kind == "Hub" && h.Hub.SuperSpeedPlusCapable == false);
            port.SocketSpeed = capped ? "5 Gb/s" : "≥5 Gb/s";
            evidence.Add(capped ? "This socket supports SuperSpeed USB 3, and its hub tops out at 5 Gb/s."
                : "This socket supports SuperSpeed USB 3, 5 Gb/s or faster. Windows doesn't report whether it also carries 10 Gb/s until a device links that fast.");
        }
        else if (halves.Any(h => h.Port.Protocols != "Not reported"))
        {
            port.SocketSpeed = "USB 2.0"; evidence.Add("This socket supports USB 2.0, up to 480 Mb/s; Windows reports no USB 3 half for it.");
        }
        else
        {
            port.SocketSpeed = "Not reported"; evidence.Add("Windows did not report which USB versions this port supports.");
        }
        port.SocketEvidence = string.Join(" ", evidence);
    }

    internal static void AssignLocation(UsbNode node, UsbNode parent)
    {
        // A captive connection inside an external dock is not a motherboard connection.
        if (parent.Location == "External" || node.PortIsUserConnectable == true)
        {
            node.Location = "External";
            node.LocationEvidence = parent.Location == "External"
                ? "Likely external: downstream of a hub on a user-accessible port. Physical enclosure is not reported."
                : "Likely external: Windows marks the upstream port as user-accessible. Physical enclosure is not reported.";
        }
        else if (node.PortIsUserConnectable == false && parent.Location is "Host" or "Internal")
        {
            node.Location = "Internal";
            node.LocationEvidence = "Likely internal: Windows marks this host-side connection as not user-accessible. This does not prove motherboard mounting.";
        }
    }

    internal static List<string> ReadInterfaceFunctions(byte[] descriptor)
    {
        var result = new List<string>();
        var limit = descriptor.Length >= 4 ? Math.Min(descriptor.Length, BitConverter.ToUInt16(descriptor, 2)) : 0;
        for (int offset = 0; offset + 2 <= limit;)
        {
            int length = descriptor[offset];
            if (length < 2 || offset + length > limit) break;
            if (descriptor[offset + 1] == 4 && length >= 9)
            {
                byte cls = descriptor[offset + 5], sub = descriptor[offset + 6], protocol = descriptor[offset + 7];
                string? function = cls switch
                {
                    1 => "Audio", 2 or 10 => "Serial / communications", 3 when sub == 1 && protocol == 1 => "Keyboard",
                    3 when sub == 1 && protocol == 2 => "Mouse", 3 => "HID / controls", 7 => "Printer",
                    // Mass storage: MMC-5 (ATAPI) is optical, UFI and SFF-8070i are floppy-style, and the
                    // UAS protocol is used by fast disk enclosures. Everything else reports SCSI over bulk-only.
                    8 when sub == 2 => "Optical drive", 8 when sub is 4 or 5 => "Floppy drive", 8 when protocol == 0x62 => "External drive",
                    8 => "Storage", 14 => "Camera / video", 0xE0 => "Wireless", 17 => "Billboard", _ => null
                };
                if (function != null && !result.Contains(function)) result.Add(function);
            }
            offset += length;
        }
        return result;
    }

    internal static void Identify(UsbNode node)
    {
        if (node.Kind != "Device") return;
        var name = node.Name.ToLowerInvariant();
        string? namedType = name switch
        {
            var s when s.Contains("keyboard") => "Keyboard",
            var s when s.Contains("mouse") => "Mouse",
            var s when s.Contains("webcam") || s.Contains("camera") => "Camera / video",
            var s when s.Contains("headset") || s.Contains("microphone") || s.Contains("speaker") => "Audio",
            var s when s.Contains("gamepad") || s.Contains("joystick") || s.Contains("simagic") || s.Contains("racing wheel") => "Game controller",
            var s when s.Contains("quest") || s.Contains("vive") => "VR headset",
            var s when s.Contains("billboard") => "Billboard",
            _ => null
        };
        if (namedType != null) { node.DeviceType = namedType; node.TypeEvidence = "Inferred from the device product name."; return; }
        foreach (var type in new[] { "Camera / video", "Audio", "Optical drive", "Floppy drive", "External drive", "Storage", "Keyboard", "Mouse", "Printer", "Wireless", "Serial / communications", "HID / controls", "Billboard" })
            if (node.InterfaceFunctions.Contains(type))
            {
                node.DeviceType = type; node.TypeEvidence = "Reported by the active USB configuration's interface descriptors.";
                RefineStorage(node, name);
                return;
            }
        node.DeviceType = node.DeviceClass switch
        {
            "Audio" => "Audio", "Video" => "Camera / video", "Mass storage" => "Storage", "Human interface (HID)" => "HID / controls",
            "Printer" => "Printer", "Wireless controller" => "Wireless", "Communications" => "Serial / communications", _ => "USB device"
        };
        node.TypeEvidence = node.DeviceType == "USB device" ? "Specific function not reported; generic USB device shown." : "Reported by the USB device class.";
        RefineStorage(node, name);
    }

    internal static readonly string[] StorageTypes = ["External drive", "Optical drive", "Card reader", "Flash drive", "Floppy drive", "Storage"];
    internal static bool IsStorage(string deviceType) => StorageTypes.Contains(deviceType);

    // USB tells optical and floppy drives apart by subclass and fast enclosures by the UAS protocol, but
    // flash drives, card readers and disk enclosures all report plain SCSI storage; their names decide.
    private static void RefineStorage(UsbNode node, string name)
    {
        if (!IsStorage(node.DeviceType)) return;
        string? named = name switch
        {
            var s when s.Contains("dvd") || s.Contains("cd-rom") || s.Contains("cdrom") || s.Contains("blu-ray") || s.Contains("bd-re") || s.Contains("super multi") || s.Contains("optical") => "Optical drive",
            var s when s.Contains("card reader") || s.Contains("cardreader") || s.Contains("sd reader") || s.Contains("multi-card") || s.Contains("multicard") => "Card reader",
            var s when s.Contains("floppy") => "Floppy drive",
            var s when s.Contains("flash") || s.Contains("thumb") || s.Contains("usb stick") || s.Contains("pen drive") || s.Contains("cruzer") || s.Contains("datatraveler") || s.Contains("jumpdrive") => "Flash drive",
            var s when s.Contains("ssd") || s.Contains("hdd") || s.Contains("hard drive") || s.Contains("hard disk") || s.Contains("nvme") || s.Contains("portable drive") || s.Contains("external drive") => "External drive",
            _ => null
        };
        if (named == null || named == node.DeviceType) return;
        node.DeviceType = named;
        node.TypeEvidence = "Mass storage; the kind of drive is inferred from the product name.";
    }
}

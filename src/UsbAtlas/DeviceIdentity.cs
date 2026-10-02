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

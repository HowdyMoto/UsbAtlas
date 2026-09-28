namespace UsbAtlas;

internal static class DeviceIdentity
{
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
        foreach (var type in new[] { "Camera / video", "Audio", "Storage", "Keyboard", "Mouse", "Printer", "Wireless", "Serial / communications", "HID / controls", "Billboard" })
            if (node.InterfaceFunctions.Contains(type))
            { node.DeviceType = type; node.TypeEvidence = "Reported by the active USB configuration's interface descriptors."; return; }
        node.DeviceType = node.DeviceClass switch
        {
            "Audio" => "Audio", "Video" => "Camera / video", "Mass storage" => "Storage", "Human interface (HID)" => "HID / controls",
            "Printer" => "Printer", "Wireless controller" => "Wireless", "Communications" => "Serial / communications", _ => "USB device"
        };
        node.TypeEvidence = node.DeviceType == "USB device" ? "Specific function not reported; generic USB device shown." : "Reported by the USB device class.";
    }
}

namespace UsbAtlas;

internal static class SelfTests
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Check(UsbScanner.DecodePower(250, 0x0200) == 500, "USB 2 power units must be 2 mA.");
        Check(UsbScanner.DecodePower(112, 0x0300) == 896, "USB 3 power units must be 8 mA.");
        Check(UsbScanner.DecodeSpeed(2, 1).Item2 == 5000, "EX V2 must override legacy high-speed reporting.");
        Check(UsbScanner.DecodeSpeed(2, 4).Item2 == null, "SuperSpeedPlus must not pretend to know exact lane rate.");
        Check(UsbScanner.DecodeSpeed(0, 0).Item2 == 1.5, "Low-speed decoding.");
        Check(UsbScanner.DecodeSpeed(1, 0).Item2 == 12, "Full-speed decoding.");
        Check(UsbScanner.DecodeSpeed(2, 0).Item2 == 480, "High-speed decoding.");
        var demo = DemoData.Create();
        Check(demo.IsDemo, "Sample data must be explicitly identified.");
        Check(demo.Nodes.Count(x => x.Kind == "Device") == 5, "Recursive topology traversal.");
        Check(demo.Nodes.Select(x => x.Id).Distinct().Count() == demo.Nodes.Count(), "Stable unique graph identities.");
        var port = new UsbNode();
        Check(port.PortIsUserConnectable == null && port.PortConnectorIsTypeC == null, "Missing connector query must remain unknown.");
        DeviceIdentity.ApplyPortProperties(port, 9);
        Check(port.PortIsUserConnectable == true && port.PortConnectorIsTypeC == true, "Decode user-accessible USB-C flags.");
        DeviceIdentity.ApplyPortProperties(port, 0);
        Check(port.PortConnectorIsTypeC == false, "Non-Type-C must not be guessed as Type-A.");
        DeviceIdentity.AssignLocation(port, new UsbNode { Location = "External" });
        Check(port.Location == "External", "A captive connection inside an external dock must not be shown as motherboard hardware.");
        var internalHub = new UsbNode { Kind = "Hub", PortIsUserConnectable = false };
        DeviceIdentity.AssignLocation(internalHub, new UsbNode { Location = "Host" });
        Check(internalHub.Location == "Internal", "Non-user-accessible host-side connection should be marked likely internal.");
        var unknownHub = new UsbNode { Kind = "Hub" };
        DeviceIdentity.AssignLocation(unknownHub, new UsbNode { Location = "Host" });
        Check(unknownHub.Location == "Unknown", "Missing port information cannot establish hub location.");
        byte[] keyboardConfig = [9, 2, 18, 0, 1, 1, 0, 128, 50, 9, 4, 0, 0, 1, 3, 1, 1, 0];
        var keyboard = new UsbNode { Name = "Uninformative product", InterfaceFunctions = DeviceIdentity.ReadInterfaceFunctions(keyboardConfig) };
        DeviceIdentity.Identify(keyboard);
        Check(keyboard.DeviceType == "Keyboard", "Identify composite keyboards from boot interface descriptors.");
        Check(DeviceIdentity.ReadInterfaceFunctions([9, 2, 11, 0, 0, 1, 0, 128, 0, 0, 4]).Count == 0, "Zero-length malformed descriptors must terminate safely.");
        Check(DeviceIdentity.ReadInterfaceFunctions(keyboardConfig[..15]).Count == 0, "Truncated interfaces must not be read.");
        Check(demo.Nodes.First(x => x.Name == "Portable SSD").DeviceType == "Storage", "Demo storage device type.");
    }
}

namespace UsbAtlas;

internal static class DemoData
{
    public static Snapshot Create()
    {
        var root = new UsbNode { Id = "demo/root", Name = "Root hub", Kind = "Root hub", PortCount = 4, Protocols = "USB 2.0 / USB 3.x", PowerSource = "System supplied" };
        var hub = new UsbNode { Id = "demo/root/1", Name = "Studio desktop hub", Kind = "Hub", Port = 1, PortCount = 4, UsbVersion = "USB 3.00", Speed = "SuperSpeed · 5 Gb/s", LinkMbps = 5000, Protocols = "USB 3.x", PowerSource = "Self powered", MaxPowerMa = 0, VendorId = "2109", ProductId = "0817" };
        UsbNode Device(string id, string name, int port, double speed, int power, string cls) => new() { Id = id, Name = name, Port = port, LinkMbps = speed, Speed = speed == 5000 ? "SuperSpeed · 5 Gb/s" : speed == 480 ? "High speed · 480 Mb/s" : "Full speed · 12 Mb/s", UsbVersion = speed == 5000 ? "USB 3.00" : "USB 2.00", MaxPowerMa = power, PowerSource = "Bus powered", DeviceClass = cls, Protocols = speed == 5000 ? "USB 3.x" : "USB 2.0", Notes = ["Illustrative demo device. This is not connected hardware."] };
        hub.Children.Add(Device("demo/root/1/1", "Portable SSD", 1, 5000, 896, "Mass storage"));
        hub.Children.Add(Device("demo/root/1/2", "Studio camera", 2, 5000, 400, "Video"));
        hub.Children.Add(new UsbNode { Id = "demo/root/1/3", Name = "Available port 3", Kind = "Empty port", Port = 3, Status = "Empty", Protocols = "USB 3.x" });
        hub.Children.Add(new UsbNode { Id = "demo/root/1/4", Name = "Available port 4", Kind = "Empty port", Port = 4, Status = "Empty", Protocols = "USB 3.x" });
        root.Children.Add(hub);
        root.Children.Add(Device("demo/root/2", "Audio interface", 2, 480, 500, "Audio"));
        root.Children.Add(Device("demo/root/3", "Mechanical keyboard", 3, 12, 100, "Human interface (HID)"));
        root.Children.Add(Device("demo/root/4", "Wireless mouse receiver", 4, 12, 100, "Human interface (HID)"));
        root.Location = "Host";
        root.LocationEvidence = "Demo: logical root ports belong to the host controller.";
        hub.PortIsUserConnectable = true;
        hub.PortConnectorIsTypeC = true;
        hub.Location = "External";
        hub.LocationEvidence = "Illustrative demo: external desktop hub connected to a USB-C port.";
        foreach (var n in root.Walk().Where(n => n.Kind == "Device"))
        {
            n.PortIsUserConnectable = true;
            n.Location = "External";
            n.LocationEvidence = "Illustrative external demo device.";
            DeviceIdentity.Identify(n);
        }
        hub.Children[0].PortConnectorIsTypeC = true;
        return new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "demo", Name = "USB xHCI host controller", Kind = "Controller", Children = [root], PowerSource = "System supplied", Location = "Host", LocationEvidence = "Demo host controller." }] };
    }
}

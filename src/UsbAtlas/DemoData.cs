namespace UsbAtlas;

internal static class DemoData
{
    public static Snapshot Create()
    {
        var root = new UsbNode { Id = "demo/root", Name = "Root hub", Kind = "Root hub", PortCount = 8, Protocols = "USB 2.0 / USB 3.x", PowerSource = "System supplied" };
        var hub = new UsbNode { Id = "demo/root/1", Name = "Studio desktop hub", Kind = "Hub", Port = 1, PortCount = 4, UsbVersion = "USB 3.00", Speed = "SuperSpeed · 5 Gb/s", LinkMbps = 5000, Protocols = "USB 3.x", PowerSource = "Self powered", MaxPowerMa = 0, VendorId = "2109", ProductId = "0817", SuperSpeedPlusCapable = false };
        UsbNode Device(string id, string name, int port, double speed, int power, string cls) => new() { Id = id, Name = name, Port = port, LinkMbps = speed, Speed = speed == 5000 ? "SuperSpeed · 5 Gb/s" : speed == 480 ? "High speed · 480 Mb/s" : "Full speed · 12 Mb/s", UsbVersion = speed == 5000 ? "USB 3.00" : "USB 2.00", MaxPowerMa = power, PowerSource = "Bus powered", DeviceClass = cls, Protocols = speed == 5000 ? "USB 3.x" : "USB 2.0", Notes = ["Illustrative demo device. This is not connected hardware."] };
        hub.Children.Add(Device("demo/root/1/1", "Portable SSD", 1, 5000, 896, "Mass storage"));
        hub.Children.Add(Device("demo/root/1/2", "Studio camera", 2, 5000, 400, "Video"));
        hub.Children.Add(new UsbNode { Id = "demo/root/1/3", Name = "Available port 3", Kind = "Empty port", Port = 3, Status = "Empty", Protocols = "USB 3.x" });
        hub.Children.Add(new UsbNode { Id = "demo/root/1/4", Name = "Available port 4", Kind = "Empty port", Port = 4, Status = "Empty", Protocols = "USB 3.x" });
        root.Children.Add(hub);
        root.Children.Add(Device("demo/root/2", "Audio interface", 2, 480, 500, "Audio"));
        root.Children.Add(Device("demo/root/3", "Mechanical keyboard", 3, 12, 100, "Human interface (HID)"));
        root.Children.Add(Device("demo/root/4", "Wireless mouse receiver", 4, 12, 100, "Human interface (HID)"));
        // A bus-powered travel hub with its adapter unplugged and too much plugged in shows the power checks.
        var travel = new UsbNode { Id = "demo/root/5", Name = "Travel hub", Kind = "Hub", Port = 5, PortCount = 4, UsbVersion = "USB 2.00", Speed = "High speed · 480 Mb/s", LinkMbps = 480, Protocols = "USB 2.0", PowerSource = "Bus powered", SelfPowerCapable = true, MaxPowerMa = 100, VendorId = "05E3", ProductId = "0610" };
        travel.Children.Add(Device("demo/root/5/1", "USB flash drive", 1, 480, 200, "Mass storage"));
        travel.Children.Add(Device("demo/root/5/2", "LED ring light", 2, 12, 500, "Human interface (HID)"));
        travel.Children.Add(new UsbNode { Id = "demo/root/5/3", Name = "Portable hard drive · Insufficient power", Kind = "Unavailable", Port = 3, Status = "Insufficient power", UsbVersion = "USB 3.00", MaxPowerMa = 896, Protocols = "USB 2.0", Notes = [UsbBudgets.FaultNote("Insufficient power"), "Illustrative demo device. This is not connected hardware."] });
        travel.Children.Add(new UsbNode { Id = "demo/root/5/4", Name = "Available port 4", Kind = "Empty port", Port = 4, Status = "Empty", Protocols = "USB 2.0" });
        root.Children.Add(travel);
        // A 10 Gb/s enclosure on a USB-C port shows the socket colors. Its socket's USB 2 half is port 8, and
        // the keyboard's USB 3 socket has its free USB 3 half on port 7, so each pair is drawn as one socket.
        var enclosure = Device("demo/root/6", "NVMe SSD enclosure", 6, 5000, 896, "Mass storage");
        enclosure.Speed = "SuperSpeedPlus · 10 Gb/s or higher"; enclosure.LinkMbps = null; enclosure.UsbVersion = "USB 3.20"; enclosure.SuperSpeedPlusCapable = true;
        root.Children.Add(enclosure);
        root.Children.Add(new UsbNode { Id = "demo/root/7", Name = "Available port 7", Kind = "Empty port", Port = 7, Status = "Empty", Protocols = "USB 3.x", CompanionId = "demo/root/3" });
        root.Children.Add(new UsbNode { Id = "demo/root/8", Name = "Available port 8", Kind = "Empty port", Port = 8, Status = "Empty", Protocols = "USB 2.0", CompanionId = "demo/root/6", PortConnectorIsTypeC = true });
        root.Children[2].CompanionId = "demo/root/7"; enclosure.CompanionId = "demo/root/8";
        // Bandwidth a device reserves for its open periodic pipes now, and the most its configuration can reserve.
        void Reserve(UsbNode n, double now, double peak) { n.ReservedMbps = now; n.PeakReservedMbps = peak; }
        Reserve(hub, 0.0001, 0.0001); Reserve(travel, 0.0001, 0.0001);
        Reserve(hub.Children[0], 0, 0); Reserve(hub.Children[1], 98.3, 196.6);
        Reserve(root.Children[1], 4.6, 4.6); Reserve(root.Children[2], 0.0064, 0.0064); Reserve(root.Children[3], 0.064, 0.064);
        Reserve(travel.Children[0], 0, 0); Reserve(travel.Children[1], 0.0064, 0.0064); Reserve(enclosure, 0, 0);
        root.Location = "Host";
        root.LocationEvidence = "Demo: logical root ports belong to the host controller.";
        hub.PortIsUserConnectable = true;
        hub.PortConnectorIsTypeC = true;
        hub.Location = "External";
        hub.LocationEvidence = "Illustrative demo: external desktop hub connected to a USB-C port.";
        travel.PortIsUserConnectable = true;
        foreach (var n in travel.Walk()) { n.Location = "External"; n.LocationEvidence = "Illustrative external demo hub."; }
        foreach (var n in root.Walk().Where(n => n.Kind == "Device"))
        {
            n.PortIsUserConnectable = true;
            n.Location = "External";
            n.LocationEvidence = "Illustrative external demo device.";
            DeviceIdentity.Identify(n);
        }
        hub.Children[0].PortConnectorIsTypeC = true;
        enclosure.PortConnectorIsTypeC = true;
        foreach (var n in root.Walk().Skip(1)) n.PortIsUserConnectable ??= true;
        var snapshot = new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "demo", Name = "USB xHCI host controller", Kind = "Controller", Children = [root], PowerSource = "System supplied", Location = "Host", LocationEvidence = "Demo host controller." }] };
        foreach (var node in snapshot.Nodes)
        {
            node.NameSource = "Illustrative sample data";
            if (node.Kind is "Device" or "Hub") node.ReportedProduct = node.Name;
        }
        DeviceIdentity.ClassifySockets(snapshot);
        foreach (var node in snapshot.Nodes.Reverse().Where(n => n.Kind is "Controller" or "Root hub" or "Hub")) DeviceIdentity.SummarizeProtocols(node);
        UsbBudgets.AnalyzePower(snapshot);
        return snapshot;
    }
}

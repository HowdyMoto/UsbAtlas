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
        var usb2 = new UsbNode { Id = "two", Kind = "Hub", Port = 1, VendorId = "0BDA", LinkMbps = 480, SpeedLimited = true, CompanionPortNumber = 2, CompanionHubSymbolicLink = "root" };
        var usb3 = new UsbNode { Id = "three", Kind = "Hub", Port = 2, VendorId = "0BDA", LinkMbps = 5000 };
        var root = new UsbNode { Id = "root", Kind = "Root hub", HubSymbolicLink = @"\\?\root", Children = [usb2, usb3] };
        var companionSnapshot = new Snapshot { Controllers = [root] };
        HubRelationships.Analyze(companionSnapshot);
        Check(usb2.IsUsb2Companion && usb2.CompanionHubId == usb3.Id && !usb3.IsUsb2Companion, "Companion hubs must pair from the Windows port mapping.");
        usb2.CompanionPortNumber = 9; HubRelationships.Analyze(companionSnapshot);
        Check(!usb2.IsUsb2Companion && usb2.CompanionHubId.Length == 0, "Matching names or VID alone must not pair hubs.");
        // A built-in hub's two sides, on root ports Windows doesn't pair, name each other through their own ports.
        var side2 = new UsbNode { Id = "r/2", Kind = "Hub", VendorId = "05E3", LinkMbps = 480, Children = [new UsbNode { Id = "r/2/1", Port = 1, CompanionId = "r/7/1" }] };
        var side3 = new UsbNode { Id = "r/7", Kind = "Hub", VendorId = "05E3", LinkMbps = 5000, Children = [new UsbNode { Id = "r/7/1", Port = 1, CompanionId = "r/2/1" }] };
        HubRelationships.Analyze(new Snapshot { Controllers = [new UsbNode { Id = "r", Kind = "Root hub", Children = [side2, side3] }] });
        Check(side2.CompanionHubId == side3.Id && side2.IsUsb2Companion && side3.CompanionHubId == side2.Id && !side3.IsUsb2Companion, "Hub sides must pair through their own ports' companions.");
        var demo = DemoData.Create();
        Check(demo.IsDemo, "Sample data must be explicitly identified.");
        Check(demo.Nodes.Count(x => x.Kind == "Device") == 8, "Recursive topology traversal.");
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
        // Storage kinds: the interface reports optical, floppy and UAS drives; names refine plain SCSI storage.
        static List<string> Interface(byte cls, byte sub, byte protocol) => DeviceIdentity.ReadInterfaceFunctions([9, 2, 18, 0, 1, 1, 0, 128, 50, 9, 4, 0, 0, 2, cls, sub, protocol, 0]);
        static UsbNode Identified(string name, List<string> functions) { var n = new UsbNode { Name = name, InterfaceFunctions = functions }; DeviceIdentity.Identify(n); return n; }
        Check(Identified("Uninformative product", Interface(8, 2, 0x50)).DeviceType == "Optical drive", "ATAPI mass storage is an optical drive.");
        Check(Identified("Uninformative product", Interface(8, 4, 0)).DeviceType == "Floppy drive", "UFI mass storage is a floppy drive.");
        Check(Identified("Uninformative product", Interface(8, 6, 0x62)).DeviceType == "External drive", "UAS mass storage is a drive enclosure.");
        Check(Identified("Uninformative product", Interface(8, 6, 0x50)).DeviceType == "Storage", "Plain SCSI storage stays generic without a telling name.");
        Check(Identified("Hitachi-LG Portable Super Multi Drive", Interface(8, 6, 0x50)).DeviceType == "Optical drive", "Optical drives that report plain SCSI are recognized by name.");
        Check(Identified("Generic USB3.0 Card Reader", Interface(8, 6, 0x50)).DeviceType == "Card reader", "Card readers are recognized by name.");
        Check(Identified("SanDisk Cruzer Blade", Interface(8, 6, 0x50)).DeviceType == "Flash drive", "Flash drives are recognized by name.");
        Check(Identified("LED flash ring light", Interface(3, 0, 0)).DeviceType == "HID / controls", "Storage names must not reclassify devices without storage.");
        Check(demo.Nodes.First(x => x.Name == "Portable SSD").DeviceType == "External drive", "Demo SSD is an external drive.");
        SocketTests(demo);
        BudgetTests(demo);
        IdentityTests.Run();
    }

    private static void SocketTests(Snapshot demo)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static (string, string) Socket(UsbNode n) => (n.Connector, n.SocketSpeed);
        // A USB-C socket on a host: the USB 2 half and the USB 3 half name each other as companions.
        var usb2 = new UsbNode { Id = "r/1", Kind = "Empty port", Protocols = "USB 1.x / USB 2.0", PortIsUserConnectable = true, PortConnectorIsTypeC = true, CompanionId = "r/2" };
        var usb3 = new UsbNode { Id = "r/2", Kind = "Empty port", Protocols = "USB 3.x", PortIsUserConnectable = true, PortConnectorIsTypeC = true, CompanionId = "r/1" };
        var lone = new UsbNode { Id = "r/3", Kind = "Empty port", Protocols = "USB 1.x / USB 2.0", PortIsUserConnectable = true, PortConnectorIsTypeC = false };
        var builtIn = new UsbNode { Id = "r/4", Kind = "Device", Protocols = "USB 1.x / USB 2.0", PortIsUserConnectable = false, PortConnectorIsTypeC = false };
        var unread = new UsbNode { Id = "r/5", Kind = "Unavailable" };
        var root = new UsbNode { Id = "r", Kind = "Root hub", Children = [usb2, usb3, lone, builtIn, unread] };
        var host = new Snapshot { Controllers = [new UsbNode { Kind = "Controller", Children = [root] }] };
        DeviceIdentity.ClassifySockets(host);
        Check(Socket(usb2) == ("USB-C", "≥5 Gb/s") && Socket(usb3) == ("USB-C", "≥5 Gb/s"), "Both halves of a USB 3 socket are drawn as the socket they share.");
        Check(Socket(lone) == ("USB-A", "USB 2.0"), "A user-accessible USB 2 port without a USB 3 half is a black USB-A socket.");
        Check(Socket(builtIn) == ("Internal", "USB 2.0") && Socket(unread) == ("Not reported", "Not reported"), "Built-in and unreported ports must not be drawn as sockets.");
        usb3.Kind = "Device"; usb3.Speed = "SuperSpeedPlus · 10 Gb/s or higher";
        DeviceIdentity.ClassifySockets(host);
        Check(usb2.SocketSpeed == "≥10 Gb/s" && usb3.SocketSpeed == "≥10 Gb/s", "A device linked at SuperSpeedPlus proves the whole socket carries 10 Gb/s.");
        // A plug-in hub's own SuperSpeedPlus support sets its sockets' speed; a host's root ports stay open-ended.
        var port = new UsbNode { Id = "h/1", Kind = "Empty port", Protocols = "USB 3.x", PortIsUserConnectable = true, PortConnectorIsTypeC = false };
        DeviceIdentity.ClassifySocket([(port, new UsbNode { Kind = "Hub", SuperSpeedPlusCapable = true })]);
        Check(Socket(port) == ("USB-A", "≥10 Gb/s"), "A SuperSpeedPlus hub's USB 3 sockets carry 10 Gb/s.");
        DeviceIdentity.ClassifySocket([(port, new UsbNode { Kind = "Hub", SuperSpeedPlusCapable = false })]);
        Check(port.SocketSpeed == "5 Gb/s", "A 5 Gb/s hub caps its sockets.");
        DeviceIdentity.ClassifySocket([(port, new UsbNode { Kind = "Hub" })]);
        Check(port.SocketSpeed == "≥5 Gb/s", "A hub whose SuperSpeedPlus support is unknown must not be assumed to cap its sockets.");
        Check(Socket(demo.Nodes.Single(n => n.Id == "demo/root/6")) == ("USB-C", "≥10 Gb/s") && Socket(demo.Nodes.Single(n => n.Id == "demo/root/1/1")) == ("USB-C", "5 Gb/s")
            && Socket(demo.Nodes.Single(n => n.Id == "demo/root/5/4")) == ("USB-A", "USB 2.0") && Socket(demo.Nodes.Single(n => n.Id == "demo/root/7")) == ("USB-A", "≥5 Gb/s"), "Sample sockets show each connector and speed.");
    }

    private static void BudgetTests(Snapshot demo)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        Check(Near(UsbBudgets.PeriodicMbps(3, 8, 10, 1), 0.0064), "Full-speed interrupt intervals are whole frames.");
        Check(Near(UsbBudgets.PeriodicMbps(1, 192, 1, 1), 1.536), "Full-speed isochronous reserves every frame.");
        Check(Near(UsbBudgets.PeriodicMbps(5, 0x1400, 1, 2), 196.608), "High-bandwidth high-speed endpoints carry up to three packets per microframe.");
        Check(Near(UsbBudgets.PeriodicMbps(5, 1024, 1, 3, 3072), 196.608), "SuperSpeed reservations use the companion's bytes per interval.");
        Check(UsbBudgets.PeriodicMbps(2, 512, 0, 2) == 0 && UsbBudgets.PeriodicMbps(0, 64, 0, 2) == 0, "Bulk and control transfers reserve nothing.");
        Check(UsbBudgets.Rate(0.0064) == "6.4 kb/s" && UsbBudgets.Rate(196.608) == "197 Mb/s" && UsbBudgets.Rate(4.6) == "4.6 Mb/s", "Rate formatting.");
        byte[] camera = [9, 2, 50, 0, 2, 1, 0, 0x80, 50, 9, 4, 0, 0, 0, 0x0E, 2, 0, 0, 9, 4, 0, 1, 1, 0x0E, 2, 0, 0, 7, 5, 0x81, 5, 0x00, 0x14, 1, 9, 4, 1, 0, 1, 3, 0, 0, 0, 7, 5, 0x82, 3, 8, 0, 4];
        var endpoints = UsbBudgets.ReadEndpoints(camera);
        Check(endpoints.Count == 2 && endpoints[0].Alternate == 1 && endpoints[1].Interface == 1, "Endpoints keep their interface and alternate setting.");
        Check(Near(UsbBudgets.PeakPeriodicMbps(endpoints, 2), 196.672), "Peak reservation takes each interface's busiest alternate setting.");
        Check(UsbBudgets.ReadEndpoints(camera[..30]).Count == 0, "Truncated endpoint descriptors must not be read.");
        var pipes = new byte[4096];
        BitConverter.GetBytes(2).CopyTo(pipes, 27);
        byte[] open = [7, 5, 0x81, 5, 0x00, 0x14, 1, 0, 0, 0, 0, 7, 5, 0x82, 3, 8, 0, 4, 0, 0, 0, 0];
        open.CopyTo(pipes, 35);
        var streaming = new UsbNode();
        UsbScanner.ReadOpenPipes(pipes, 35 + open.Length, 2, endpoints, streaming);
        Check(streaming.OpenPipes.Count == 2 && Near(streaming.ReservedMbps!.Value, 196.672) && Near(streaming.PeakReservedMbps!.Value, 196.672), "Open periodic pipes add up to the reserved bandwidth.");
        var truncated = new UsbNode();
        UsbScanner.ReadOpenPipes(pipes, 40, 2, endpoints, truncated);
        Check(truncated.ReservedMbps == null && truncated.OpenPipes.Count == 0, "A pipe list longer than the returned data must stay unknown.");

        Check(UsbBudgets.ReservableMbps(new UsbNode { LinkMbps = 480 }) == 384 && UsbBudgets.ReservableMbps(new UsbNode { LinkMbps = 12 }) == 10.8 && UsbBudgets.ReservableMbps(new UsbNode { LinkMbps = 5000 }) == 3600, "Reservable capacity follows the periodic limits of each speed.");
        Check(Math.Abs(UsbBudgets.ReservableMbps(new UsbNode { Speed = "SuperSpeedPlus · 10 Gb/s or higher" })!.Value - 8727.27) < 0.01 && UsbBudgets.ReservableMbps(new UsbNode()) == null, "Unresolved SuperSpeedPlus assumes 10 Gb/s; unknown links have no capacity.");
        var studio = demo.Nodes.Single(n => n.Id == "demo/root/1");
        Check(UsbBudgets.LinkUse(studio) is (var through, 3600, 0) && Near(through, 98.3001), "A hub's link use adds up everything behind it.");
        Check(UsbBudgets.LinkUse(demo.Nodes.Single(n => n.Id == "demo/root/5/3")) == null && UsbBudgets.LinkUse(demo.Controllers[0]) == null, "Unavailable ports and hosts have no single link to fill.");
        var crowded = new UsbNode { Kind = "Hub", LinkMbps = 480, ReservedMbps = 0.0001, Children = [new UsbNode { Kind = "Device", LinkMbps = 480, ReservedMbps = 300 }, new UsbNode { Kind = "Device", LinkMbps = 12, ReservedMbps = 8 }] };
        Check(UsbBudgets.LinkNearlyFull(crowded) && !UsbBudgets.LinkNearlyFull(crowded.Children[0]) && !UsbBudgets.LinkNearlyFull(studio), "A hub is nearly full when what it carries passes 80% of what its link can reserve.");
        Check(!UsbBudgets.LinkNearlyFull(new UsbNode { Kind = "Hub", LinkMbps = 480, Children = [new UsbNode { Kind = "Device", ReservedMbps = 380 }] }), "Without the hub's own pipe list, link use stays unknown rather than nearly full.");
        UsbNode Kiyo() => new() { Kind = "Device", LinkMbps = 480, ReservedMbps = 0.032, PeakReservedMbps = 197.44 };
        var cameras = new UsbNode { Kind = "Hub", LinkMbps = 480, ReservedMbps = 0.0001, Children = [Kiyo(), Kiyo()] };
        Check(UsbBudgets.CouldExceedWhenStreaming(cameras) && !UsbBudgets.LinkNearlyFull(cameras), "Two idle webcams that need 395 Mb/s at peak overflow a USB 2 hub's 384 Mb/s.");
        Check(!UsbBudgets.CouldExceedWhenStreaming(new UsbNode { Kind = "Hub", LinkMbps = 480, ReservedMbps = 0.0001, Children = [Kiyo()] }), "One webcam's peak fits a USB 2 hub.");
        Check(!UsbBudgets.CouldExceedWhenStreaming(crowded) && !UsbBudgets.CouldExceedWhenStreaming(cameras.Children[0]), "A link already nearly full warns only as nearly full; a device that fits its own link is fine.");
        Check(!UsbBudgets.CouldExceedWhenStreaming(new UsbNode { Kind = "Hub", LinkMbps = 480, ReservedMbps = 0.0001, Children = [new UsbNode { Kind = "Device" }, Kiyo()] }), "Devices without reservation data add nothing to the peak.");
        Check(UsbBudgets.Share(98.3, 384) == "26% of 384 Mb/s" && UsbBudgets.Share(0.0064, 10.8) == "<1% of 10.8 Mb/s" && UsbBudgets.Share(0, 384) == "0% of 384 Mb/s", "Share formatting.");
        var travel = demo.Nodes.Single(n => n.Id == "demo/root/5");
        Check(travel.PowerWarnings.SequenceEqual(["Hub adapter not detected", "Over power budget"]), "A self-power-capable hub on bus power, over its upstream budget, must say so.");
        Check(demo.Nodes.Where(n => n.PowerWarnings.Contains("Power at risk")).Select(n => n.Id).SequenceEqual(["demo/root/5/1", "demo/root/5/2"]), "Devices declaring more than a bus-powered port guarantees are at risk.");
        Check(demo.Nodes.Single(n => n.Id == "demo/root/1").PowerWarnings.Count == 0 && demo.Nodes.Single(n => n.Id == "demo/root/1/1").PowerWarnings.Count == 0, "Self-powered hubs must not be judged against bus-power limits.");
        Check(UsbBudgets.IsPowerFault(demo.Nodes.Single(n => n.Id == "demo/root/5/3")), "Insufficient power is a power fault.");
        var keyboard = new UsbNode { Kind = "Device", LinkMbps = 12, MaxPowerMa = 100 };
        var inner = new UsbNode { Kind = "Hub", LinkMbps = 480, PowerSource = "Bus powered", MaxPowerMa = 100, Children = [keyboard] };
        var unknownDraw = new UsbNode { Kind = "Device", LinkMbps = 480 };
        var outer = new UsbNode { Kind = "Hub", LinkMbps = 480, PowerSource = "Bus powered", MaxPowerMa = 100, Children = [inner, unknownDraw] };
        var chained = new Snapshot { Controllers = [new UsbNode { Kind = "Controller", Children = [new UsbNode { Kind = "Root hub", Children = [outer] }] }] };
        UsbBudgets.AnalyzePower(chained);
        Check(inner.PowerWarnings.SequenceEqual(["Power at risk"]) && keyboard.PowerWarnings.Count == 0, "A bus-powered hub chained behind another passes its devices' draw upstream.");
        Check(outer.PowerWarnings.Count == 0 && unknownDraw.PowerWarnings.Count == 0, "Unknown draw must not be invented into a budget problem.");
        var superHub = new UsbNode { Kind = "Hub", LinkMbps = 5000, PowerSource = "Bus powered", MaxPowerMa = 96, Children = [new UsbNode { Kind = "Device", LinkMbps = 5000, MaxPowerMa = 144 }] };
        UsbBudgets.AnalyzePower(new Snapshot { Controllers = [superHub] });
        Check(superHub.Children[0].PowerWarnings.Count == 0, "SuperSpeed ports on bus-powered hubs guarantee 150 mA.");

        Check(ReconnectTracker.InstanceIdFromPath(@"\\?\USB#VID_046D&PID_C52B#5&2a8c&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}") == @"USB\VID_046D&PID_C52B\5&2a8c&0&3", "Device interface paths name their instance.");
        Check(ReconnectTracker.InstanceIdFromPath(@"\\?\HID#broken") == null, "Unrecognized paths are ignored.");
        var tracker = new ReconnectTracker(); var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        const string id = @"USB\VID_1234&PID_5678\SERIAL";
        for (int i = 0; i < 2; i++) { tracker.Removed(id, t0.AddSeconds(i * 20)); tracker.Arrived(id, t0.AddSeconds(i * 20 + 2)); }
        tracker.Removed(id, t0.AddSeconds(50)); tracker.Arrived(id, t0.AddSeconds(120));
        Check(!tracker.IsUnstable(id), "A slow return is a deliberate replug, not a drop.");
        tracker.Removed(id, t0.AddSeconds(130)); tracker.Arrived(id, t0.AddSeconds(133));
        Check(tracker.IsUnstable(id), "Three quick returns within five minutes mark an unstable connection.");
        var flaky = new UsbNode { Kind = "Device", InstanceId = id.ToLowerInvariant() };
        tracker.Apply(new Snapshot { Controllers = [flaky] });
        Check(flaky.QuickReconnects == 3, "Instance IDs match regardless of case.");
        var sample = new UsbNode { Kind = "Device", InstanceId = id };
        tracker.Apply(new Snapshot { IsDemo = true, Controllers = [sample] });
        Check(sample.QuickReconnects == 0, "Hardware reconnects must not leak into sample data.");
    }
}

namespace UsbAtlas;

internal static class SelfTests
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Check(UsbScanner.DecodePower(250, 0x0200) == 500, "USB 2 power units must be 2 mA.");
        Check(UsbScanner.DecodePower(112, 0x0300) == 896, "USB 3 power units must be 8 mA.");
        Check(UsbScanner.Bcd(0x0104) == "1.04" && UsbScanner.Bcd(0x0210) == "2.10" && UsbScanner.Bcd(0x1A0F) == "1A.0F", "Versions and revisions read as binary-coded decimal.");
        Check(Topology.SearchText(new UsbNode { DeviceRevision = "1.04" }, null).Contains("rev 1.04"), "Search finds a device by its revision.");
        BillboardTests(Check);
        ContainerTests(Check);
        LinuxTests(Check);
        Check(UsbScanner.DecodeSpeed(2, 1).Item2 == 5000, "EX V2 must override legacy high-speed reporting.");
        Check(UsbScanner.DecodeSpeed(2, 4).Item2 == null, "SuperSpeedPlus must not pretend to know exact lane rate.");
        Check(UsbScanner.DecodeSpeed(0, 0).Item2 == 1.5, "Low-speed decoding.");
        Check(UsbScanner.DecodeSpeed(1, 0).Item2 == 12, "Full-speed decoding.");
        Check(UsbScanner.DecodeSpeed(2, 0).Item2 == 480, "High-speed decoding.");
        // A hub's connection information: the port index, then its device descriptor, whose bDeviceProtocol names its TTs.
        var hubInfo = new byte[35]; new byte[] { 18, 1, 0x00, 0x02, 9, 0, 2, 64 }.CopyTo(hubInfo, 4);
        Check(UsbScanner.TransactionTranslators(hubInfo) == "Per port", "bDeviceProtocol 2 is one TT per port.");
        hubInfo[10] = 1; Check(UsbScanner.TransactionTranslators(hubInfo) == "Single", "bDeviceProtocol 1 is one TT for all ports.");
        Check(UsbScanner.DescriptorRead(hubInfo) && !UsbScanner.DescriptorRead(new byte[35]), "A port whose device descriptor Windows hasn't read yet isn't a device.");
        // A device qualifier: 10 bytes, type 6, USB 2.0; a full-speed-only device refuses the request.
        Check(UsbScanner.HighSpeedQualifier([10, 6, 0x00, 0x02, 0, 0, 0, 64, 1, 0]) && !UsbScanner.HighSpeedQualifier([10, 6, 0x10, 0x01, 0, 0, 0, 64, 1, 0])
            && !UsbScanner.HighSpeedQualifier([10, 6, 0x00]) && !UsbScanner.HighSpeedQualifier(null), "Only a well-formed USB 2.0 device qualifier shows high-speed support.");
        // A USB 2 device stuck at 12 Mb/s: behind a USB 1.1 hub, or on a connection that couldn't hold high speed.
        var stuck = new UsbNode { Id = "fs/1/1", Kind = "Device", Name = "Webcam", LinkMbps = 12, HighSpeedCapable = true, SocketSpeed = "USB 2.0" };
        var oldHub = new UsbNode { Id = "fs/1", Kind = "Hub", Name = "Old hub", LinkMbps = 12, Children = [stuck] };
        var stuckSpeed = Explanations.Speed(stuck, [new() { Kind = "Root hub" }, oldHub, stuck]);
        Check(HubRelationships.ReducedSpeed(stuck) && Explanations.SpeedLabel(stuck) == "Running at 12 Mb/s" && Explanations.SpeedSeverity(stuck) == Severity.Warning
            && stuckSpeed.What == "This device is connected at 12 Mb/s (USB 1 speed), though it supports USB 2 (480 Mb/s)." && stuckSpeed.Affects == "Yes: its transfers are limited to 12 Mb/s."
            && stuckSpeed.Cause.StartsWith("The hub it's plugged into runs at 12 Mb/s"), "A USB 2 device behind a USB 1.1 hub runs at 12 Mb/s, and the hub is named as the cause.");
        Check(Explanations.HeldBack(oldHub).SequenceEqual([stuck]), "A USB 1.1 hub holds back a high-speed device behind it.");
        oldHub.HighSpeedCapable = true;
        var slowFirst = Triage.FixFirst(new Snapshot { Controllers = [new() { Id = "fs", Kind = "Controller", Children = [oldHub] }] });
        Check(slowFirst.Count == 1 && slowFirst[0].Node == oldHub && slowFirst[0].Also.SequenceEqual([(Explanations.SpeedLabel(stuck), stuck)]),
            "A device a slow hub holds back is fixed at the hub, so fix first lists them once.");
        oldHub.HighSpeedCapable = null;
        Check(Explanations.Speed(stuck, [new() { Kind = "Root hub" }, new UsbNode { Id = "hs", Kind = "Hub", LinkMbps = 480 }, stuck]).Cause.StartsWith("Its connection couldn't hold USB 2's high speed"),
            "Behind a high-speed hub, a 12 Mb/s link means the connection couldn't hold high speed.");
        stuck.HighSpeedCapable = false;
        Check(!HubRelationships.ReducedSpeed(stuck) && !HubRelationships.ReducedSpeed(new UsbNode { Kind = "Device", LinkMbps = 12 }), "A full-speed-only device at 12 Mb/s is where it belongs.");
        // Remembered devices: a serial adapter without a serial number got a new COM number on each port.
        Check(Remembered.ComOf("USB Serial Device (COM13)") == "COM13" && Remembered.ComOf("USB Composite Device") == "" && Remembered.ByPort(@"USB\VID_16D0&PID_127B\7&1&0&1")
            && !Remembered.ByPort(@"USB\VID_303A&PID_4D01\E8F60AE0C461") && Remembered.VidPid(@"USB\VID_16d0&PID_127b\7&1&0&1") == ("16D0", "127B"), "Remembered devices read their COM number, IDs and whether Windows named them by port.");
        var adapter = new UsbNode { Id = "m/r/3", Kind = "Device", Port = 3, VendorId = "16D0", ProductId = "127B", InstanceId = @"USB\VID_16D0&PID_127B\7&1&0&3", ComPort = "COM13", DeviceType = "Serial / communications" };
        var desk = new Snapshot
        {
            Controllers = [new() { Id = "m", Kind = "Controller", Children = [new() { Id = "m/r", Kind = "Root hub", LocationPath = "PCIROOT(0)#PCI(0000)#USBROOT(0)", Children = [adapter] }] }],
            Remembered =
            [
                new() { InstanceId = @"USB\VID_16D0&PID_127B\7&1&0&1", VendorId = "16D0", ProductId = "127B", LocationPath = "PCIROOT(0)#PCI(0000)#USBROOT(0)#USB(1)", ComPort = "COM9", LastConnected = new DateTime(2026, 9, 22, 8, 17, 0) },
                new() { InstanceId = @"USB\VID_16D0&PID_127B\8&2&0&2", VendorId = "16D0", ProductId = "127B", LocationPath = "PCIROOT(0)#PCI(0000)#USBROOT(0)#USB(4)#USB(2)", LocationInfo = "Port_#0002.Hub_#0005", ComPort = "COM13" },
                new() { InstanceId = @"USB\VID_16D0&PID_127B\ABC123456", VendorId = "16D0", ProductId = "127B", ComPort = "COM4" },
                new() { InstanceId = @"USB\VID_16D0&PID_127C\7&1&0&2", VendorId = "16D0", ProductId = "127C", ComPort = "COM5" }
            ]
        };
        Remembered.Analyze(desk);
        Check(adapter.OtherEntries.Select(o => o.Place).SequenceEqual(["H01/01", "Port_#0002.Hub_#0005 (on a hub not connected now)"]),
            "Other entries are the same VID:PID named by port, never a unit with a serial number or another product, placed on the map when their hub is connected.");
        var comChanged = Explanations.For(adapter, Remembered.ComChanged, Topology.FindPath(desk, adapter.Id));
        Check(adapter.RememberedIssue == Remembered.ComChanged && IssueRules.For(adapter).Contains((Severity.Note, Remembered.ComChanged)) && comChanged.What.StartsWith("This device is COM13 on this port, but it was COM9 on H01/01.")
            && comChanged.Steps!.Any(s => s.Contains("pnputil /remove-device")), "A serial adapter on a new COM number gets a note naming the old one, and how to remove old entries.");
        adapter.ComPort = ""; adapter.DeviceType = "Game controller"; Remembered.Analyze(desk);
        Check(adapter.RememberedIssue == Remembered.OtherPorts && Explanations.For(adapter, Remembered.OtherPorts, []).What.StartsWith("Windows remembers this controller on 2 other ports too: H01/01"), "A game controller remembered on other ports gets a note.");
        adapter.DeviceType = "Keyboard"; Remembered.Analyze(desk);
        Check(adapter.OtherEntries.Count == 2 && adapter.RememberedIssue == "", "Other entries of anything else are listed but not flagged.");
        adapter.InstanceId = @"USB\VID_16D0&PID_127B\ABC123456"; Remembered.Analyze(desk);
        Check(adapter.OtherEntries.Count == 0, "A device with a serial number has one entry wherever it's plugged in.");
        // A controller's PCIe link is shared by everything on its ports.
        Check(UsbBudgets.PcieMbps(2, 1) == 4000 && UsbBudgets.PcieMbps(4, 16) == 15754 * 16 && UsbBudgets.PcieMbps(null, 4) == null && UsbBudgets.PcieMbps(7, 1) == null
            && UsbBudgets.PcieText(3, 4) == "PCIe 3.0 ×4", "PCIe links are read after line encoding.");
        // USB-C: a dock's tunneled controller, and what Windows does and doesn't say about a USB-C socket.
        var docked = new UsbNode { Kind = "Controller", PcieTunneled = true, PcieGeneration = 1, PcieLanes = 1, Location = "Host" };
        UsbC.MarkTunneled(docked);
        Check(docked.Location == UsbC.TunneledLocation && UsbBudgets.Uplink(docked) == null && UsbBudgets.UplinkSeverity(docked) == null,
            "A controller reached over USB4 or Thunderbolt is in a dock, and its tunnel's PCIe link isn't judged.");
        var usbCPort = new UsbNode { Id = "c/root/1", Kind = "Empty port", Port = 1, Connector = "USB-C" };
        var usbCHost = new Snapshot { Controllers = [new() { Id = "c", Kind = "Controller", Children = [new() { Id = "c/root", Kind = "Root hub", Children = [usbCPort] }] }] };
        string Usb4(Snapshot s) => UsbC.Socket(s, usbCPort).Single(f => f.Feature == "USB4 / Thunderbolt").Text;
        Check(UsbC.Socket(usbCHost, usbCPort).Select(f => f.Feature).SequenceEqual(["USB4 / Thunderbolt", "DisplayPort", "Power Delivery"]) && UsbC.Socket(usbCHost, usbCPort).All(f => f.Text.StartsWith("Not reported"))
            && Usb4(usbCHost) == "Not reported.", "Every USB-C socket says USB4, DisplayPort and Power Delivery aren't reported, and claims nothing when routers weren't read.");
        usbCHost.Usb4HostRouters = ["Router"];
        Check(Usb4(usbCHost).Contains("has a USB4 host router (Router)") && Usb4(usbCHost).Contains("doesn't say which"), "A computer with a USB4 router says some sockets are USB4, not which.");
        usbCHost.Usb4HostRouters = [];
        Check(Usb4(usbCHost).Contains("most likely not a USB4 port") && UsbC.Socket(usbCHost, new UsbNode { Kind = "Empty port", Connector = "USB-A" }).Count == 0, "No router means most likely not USB4; USB-A sockets get nothing.");
        UsbNode Card(int generation, int lanes, params UsbNode[] ports) => new() { Kind = "Controller", PcieGeneration = generation, PcieLanes = lanes, PcieMaxGeneration = 3, PcieMaxLanes = 2,
            Children = [new UsbNode { Kind = "Root hub", Children = [.. ports] }] };
        UsbNode Linked(double mbps) => new() { Kind = "Device", Name = "SSD", DeviceType = "External drive", LinkMbps = mbps };
        var capped = Card(2, 1, Linked(10000));
        Check(UsbBudgets.UplinkSeverity(capped) == Severity.Warning && IssueRules.For(capped).Contains((Severity.Warning, "Limited by PCIe link")), "A 10 Gb/s device on a PCIe 2.0 ×1 card is held back.");
        var cappedWhy = Explanations.For(capped, "Limited by PCIe link", [capped]);
        Check(cappedWhy.What.Contains("PCIe 2.0 ×1, about 4 Gb/s") && cappedWhy.What.Contains("It can do PCIe 3.0 ×2") && cappedWhy.Affects.StartsWith("Yes: your external drive is linked at 10 Gb/s")
            && cappedWhy.Steps![0].Contains("slot"), "The explanation names the link, what it could do, and what it holds back.");
        Check(UsbBudgets.UplinkSeverity(Card(2, 1, Linked(5000), Linked(5000))) == Severity.Note && UsbBudgets.UplinkSeverity(Card(2, 1, new UsbNode { Kind = "Empty port", SocketSpeed = "≥10 Gb/s" })) == Severity.Note,
            "Ports that could outrun the link together, or one day, are a note.");
        Check(UsbBudgets.UplinkSeverity(Card(3, 4, Linked(10000))) == null && UsbBudgets.UplinkSeverity(new UsbNode { Kind = "Controller" }) == null, "A link with room to spare, or none reported, says nothing.");
        // Endpoints are what a controller runs out of; each device has a control endpoint plus one per open pipe.
        UsbNode Busy(int pipes) => new() { Kind = "Device", Name = "Interface", ReservedMbps = 0, OpenPipes = [.. Enumerable.Repeat("pipe", pipes)] };
        var crowded = new UsbNode { Kind = "Controller", Children = [new UsbNode { Kind = "Root hub", Children = [Busy(30), Busy(30), new UsbNode { Kind = "Device" }] }] };
        Check(UsbBudgets.ControllerLoad(crowded) == (3, 63, 1) && !UsbBudgets.EndpointsRunningHigh(crowded), "Endpoints are counted per device: control plus open pipes; unread devices are counted as unreported.");
        crowded.Children[0].Children.Add(Busy(0));
        Check(IssueRules.For(crowded).Contains((Severity.Note, "Many endpoints in use")) && Explanations.For(crowded, "Many endpoints in use", [crowded]).Affects.StartsWith("Not right now"),
            "A controller with many endpoints open is a note while everything works.");
        crowded.Children[0].Children[0].DriverProblems.Add(new DeviceProblem { Code = 10 });
        Check(Explanations.For(crowded, "Many endpoints in use", [crowded]).Affects.StartsWith("Maybe: Interface isn't working"), "A failing device on a crowded controller points to the endpoints.");
        var connecting = new UsbNode { Kind = "Unavailable", Status = "Enumerating" };
        Check(IssueRules.For(connecting).SequenceEqual([(Severity.Note, "Still connecting")]) && Explanations.For(connecting, "Still connecting", [connecting]).What.Contains("still setting it up"),
            "A port still being set up is a calm note, not a port error.");
        hubInfo[10] = 0; Check(UsbScanner.TransactionTranslators(hubInfo) == "Not reported", "A high-speed hub without a TT protocol stays unknown.");
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
        // The USB 2 side reports its USB 3 side's SuperSpeed capability; only a device below its own speed gets the note.
        side2.SpeedLimited = true;
        var slow = new UsbNode { Id = "r/3", Kind = "Device", LinkMbps = 480, SpeedLimited = true };
        var slowSnapshot = new Snapshot { Controllers = [new UsbNode { Id = "r", Kind = "Root hub", Children = [side2, side3, slow] }] };
        HubRelationships.Analyze(slowSnapshot); HubRelationships.NoteReducedSpeed(slowSnapshot); HubRelationships.NoteReducedSpeed(slowSnapshot);
        Check(!side2.Notes.Contains(HubRelationships.ReducedSpeedNote) && slow.Notes.Count(n => n == HubRelationships.ReducedSpeedNote) == 1, "A paired hub's USB 2 side must not be told to check its cable; a slower device is told once.");
        // A USB 3 hub's USB 2 side alone, with the USB 3 half of its socket empty, runs everything behind it at USB 2.
        var lone = new UsbNode { Id = "x/1", Kind = "Hub", Port = 1, VendorId = "0451", LinkMbps = 480, UsbVersion = "USB 2.10", SpeedLimited = true, CompanionId = "x/2" };
        var loneRoot = new UsbNode { Id = "x", Kind = "Root hub", Children = [lone, new UsbNode { Id = "x/2", Kind = "Empty port", Port = 2 }] };
        var loneSnapshot = new Snapshot { Controllers = [new UsbNode { Id = "c", Kind = "Controller", Children = [loneRoot] }] };
        HubRelationships.Analyze(loneSnapshot); HubRelationships.NoteReducedSpeed(loneSnapshot);
        Check(lone.Usb3SideMissing && HubRelationships.ReducedSpeed(lone) && lone.Notes.SequenceEqual([HubRelationships.Usb3SideMissingNote]), "A USB 3 hub whose USB 3 side didn't connect runs at reduced speed.");
        lone.CompanionId = "";
        HubRelationships.Analyze(loneSnapshot);
        Check(!lone.Usb3SideMissing && !HubRelationships.ReducedSpeed(lone), "Without the socket's USB 3 half, a USB 2 hub side stays silent.");
        lone.CompanionId = "x/2";
        var tunneled = new UsbNode { Id = "t/1", Kind = "Hub", VendorId = "0451", LinkMbps = 5000, Speed = "SuperSpeed · 5 Gb/s" };
        loneSnapshot.Controllers.Add(new UsbNode { Id = "t", Kind = "Controller", Children = [new UsbNode { Id = "t/root", Kind = "Root hub", Children = [tunneled] }] });
        HubRelationships.Analyze(loneSnapshot);
        Check(!lone.Usb3SideMissing && !HubRelationships.ReducedSpeed(lone), "An unpaired USB 3 hub from the same vendor elsewhere, as behind a USB4 dock, may be the missing side.");
        // One on the same controller can't be: it would occupy the socket's USB 3 half.
        loneSnapshot.Controllers.RemoveAt(1);
        var sibling = new UsbNode { Id = "x/3", Kind = "Hub", Port = 3, VendorId = "0451", LinkMbps = 5000, Speed = "SuperSpeed · 5 Gb/s" };
        loneRoot.Children.Add(sibling); HubRelationships.Analyze(loneSnapshot);
        Check(lone.Usb3SideMissing, "A same-vendor USB 3 hub on the same controller doesn't silence a missing USB 3 side.");
        loneRoot.Children.Remove(sibling);
        // A USB 3 half that shows an error means the USB 3 side tried to connect and failed.
        loneRoot.Children[1].Kind = "Unavailable"; HubRelationships.Analyze(loneSnapshot);
        Check(lone.Usb3SideMissing && lone.Usb3SideFailed && Explanations.Speed(lone, [loneRoot, lone]).Cause.Contains("tried to connect and failed"), "A failed USB 3 half is a failed USB 3 side, with cable advice.");
        // An occupied USB 3 half may hold the hub's USB 3 side under another ID, so it stays silent.
        loneRoot.Children[1].Kind = "Device"; HubRelationships.Analyze(loneSnapshot);
        Check(!lone.Usb3SideMissing, "An occupied USB 3 half isn't a missing USB 3 side.");
        loneRoot.Children[1].Kind = "Empty port";
        // A built-in hub has no cable or plug to change.
        lone.Connector = "Internal"; HubRelationships.Analyze(loneSnapshot);
        Check(lone.Usb3SideMissing && Explanations.SpeedSeverity(lone) == Severity.Note && Explanations.Speed(lone, [loneRoot, lone]).Steps!.Count == 0, "A built-in hub gets no cable advice.");
        lone.Connector = "Not reported";
        // Speed explained in plain words: what is happening, whether it affects anything, and the likely cause first.
        UsbNode Usb2Device(string name) => new() { Kind = "Device", Name = name, DeviceType = name, LinkMbps = 12 };
        var monitorHub = new UsbNode { Id = "m", Kind = "Hub", Name = "Monitor hub", LinkMbps = 480, UsbVersion = "USB 2.10", SpeedLimited = true, Usb3SideMissing = true, Connector = "USB-C", Children = [Usb2Device("Keyboard"), Usb2Device("Mouse")] };
        var monitorPath = new List<UsbNode> { new() { Kind = "Controller" }, new() { Kind = "Root hub" }, monitorHub };
        var hubSpeed = Explanations.Speed(monitorHub, monitorPath);
        Check(Explanations.SpeedLabel(monitorHub) == "Running at USB 2" && Explanations.SpeedSeverity(monitorHub) == Severity.Note
            && hubSpeed.What == "This hub is connected at USB 2 (480 Mb/s), though it supports USB 3 (5 Gb/s)."
            && hubSpeed.Affects.StartsWith("Not right now: your keyboard and mouse are USB 2 devices, so they lose nothing.") && hubSpeed.Cause == "Its USB-C connection isn't carrying USB 3."
            && hubSpeed.Steps![0].Contains("USB-C Prioritization") && hubSpeed.Steps[1].Contains("charging cables"), "A monitor hub that slows nothing is a note naming the USB-C causes, display lanes first.");
        monitorHub.Connector = "USB-A";
        Check(Explanations.Speed(monitorHub, monitorPath).Cause.Contains("didn't come up"), "Over USB-A, the cable or plug is the cause.");
        var ssd = new UsbNode { Id = "m/3", Kind = "Device", Name = "Portable SSD", LinkMbps = 480, SpeedLimited = true, SocketSpeed = "≥5 Gb/s" };
        monitorHub.Children.Add(ssd);
        var ssdSpeed = Explanations.Speed(ssd, [.. monitorPath, ssd]);
        Check(Explanations.SpeedSeverity(monitorHub) == Severity.Warning && Explanations.Speed(monitorHub, monitorPath).Affects.StartsWith("Yes: Portable SSD supports a faster link")
            && ssdSpeed.Cause == "The hub it's plugged into runs at USB 2." && ssdSpeed.Steps![0].StartsWith("Fix that hub's USB 3 connection") && ssdSpeed.Steps[1].StartsWith("Or plug"),
            "A hub that holds a faster device back warns, and the device points to it.");
        var pairedStage = new UsbNode { Id = "m/9", Kind = "Hub", LinkMbps = 480, IsUsb2Companion = true };
        Check(Explanations.Speed(ssd, [.. monitorPath, pairedStage, ssd]).Cause == "It's connected through Monitor hub, which runs at USB 2.", "A slower hub further up is named.");
        var direct = new List<UsbNode> { new() { Kind = "Root hub" }, ssd };
        ssd.SocketSpeed = "USB 2.0";
        Check(Explanations.Speed(ssd, direct).Cause == "This port supports only USB 2.", "A USB 2 port is the cause when nothing upstream is slower.");
        ssd.SocketSpeed = "≥5 Gb/s";
        Check(Explanations.Speed(ssd, direct).Cause.Contains("cable or the plug"), "On a USB 3 port, the cable or plug is the cause.");
        var pairedSide = new UsbNode { Kind = "Hub", LinkMbps = 480, IsUsb2Companion = true };
        Check(Explanations.Speed(ssd, [new() { Kind = "Root hub" }, pairedSide, ssd]).Cause.Contains("cable or the plug"), "A paired hub's USB 2 side isn't what slows a device; it could have used the USB 3 side.");
        var fast = new UsbNode { Kind = "Device", LinkMbps = 5000, SpeedLimited = true, SuperSpeedPlusCapable = true, SocketSpeed = "5 Gb/s" };
        Check(Explanations.SpeedLabel(fast) == "Running at 5 Gb/s" && Explanations.Speed(fast, [fast]).What.EndsWith("though it supports 10 Gb/s or faster.") && Explanations.Speed(fast, [fast]).Cause == "This port supports up to 5 Gb/s.", "A 10 Gb/s device on a 5 Gb/s port runs at 5 Gb/s.");
        var builtIn = new UsbNode { Kind = "Device", LinkMbps = 480, SpeedLimited = true, Connector = "Internal" };
        Check(Explanations.SpeedSeverity(builtIn) == Severity.Note && Explanations.Speed(builtIn, [builtIn]).Cause.Contains("nothing to change") && Explanations.Speed(builtIn, [builtIn]).Steps!.Count == 0, "A built-in connection can't be changed, so it is a note.");
        Check(Explanations.Kinds([Usb2Device("Keyboard"), Usb2Device("Mouse"), Usb2Device("Mouse"), Usb2Device("HID / controls")]) == "your keyboard, 2 mice and 1 other device"
            && Explanations.Kinds([Usb2Device("Mouse"), new() { Kind = "Device", Name = "Wheel", UserLabel = "Sim wheel" }]) == "your mouse and Sim wheel"
            && Explanations.Kinds([Usb2Device("HID / controls"), Usb2Device("HID / controls")]) == "its 2 devices" && Explanations.Kinds([ssd]) == "Portable SSD", "Devices are described by what they are.");
        Check(Explanations.Names([Usb2Device("A"), Usb2Device("B"), Usb2Device("C"), Usb2Device("D")]) == "A, B and 2 more devices" && Explanations.Names([Usb2Device("A"), Usb2Device("B"), Usb2Device("C")]) == "A, B and C", "Device lists read naturally.");
        var demo = DemoData.Create();
        Check(demo.IsDemo, "Sample data must be explicitly identified.");
        Check(demo.Nodes.Count(x => x.Kind == "Device") == 9, "Recursive topology traversal.");
        Check(demo.Nodes.Select(x => x.Id).Distinct().Count() == demo.Nodes.Count(), "Stable unique graph identities.");
        var port = new UsbNode();
        Check(port.PortIsUserConnectable == null && port.PortConnectorIsTypeC == null, "Missing connector query must remain unknown.");
        DeviceIdentity.ApplyPortProperties(port, 9);
        Check(port.PortIsUserConnectable == true && port.PortConnectorIsTypeC == true && port.PortIsDebugCapable == false && port.PortHasMultipleCompanions == false, "Decode user-accessible USB-C flags.");
        DeviceIdentity.ApplyPortProperties(port, 6);
        Check(port.PortIsDebugCapable == true && port.PortHasMultipleCompanions == true && port.PortIsUserConnectable == false, "Decode the debug-capable and multiple-companion flags.");
        DeviceIdentity.ApplyPortProperties(port, 9);
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
        LinkRateTests();
        PortMapTests(demo);
        BudgetTests(demo);
        SimHardwareTests(demo);
        IdentityTests.Run();
    }

    // Polling rates, game controllers by name and HID usage, and whether Windows may suspend them.
    private static void SimHardwareTests(Snapshot demo)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Check(UsbBudgets.PollIntervalMs(1, 1) == 1 && UsbBudgets.PollIntervalMs(10, 1) == 8 && UsbBudgets.PollIntervalMs(255, 0) == 128 && UsbBudgets.PollIntervalMs(0, 1) == 1, "Full- and low-speed polling rounds down to a power of two of 1 ms frames.");
        Check(UsbBudgets.PollIntervalMs(1, 2) == 0.125 && UsbBudgets.PollIntervalMs(4, 2) == 1 && UsbBudgets.PollIntervalMs(4, 3) == 1 && UsbBudgets.PollIntervalMs(0, 2) == 0.125, "High speed and faster poll every 2^(bInterval-1) microframes.");
        Check(UsbBudgets.PollingRate(1) == "1000 Hz" && UsbBudgets.PollingRate(0.125) == "8000 Hz" && UsbBudgets.PollingRate(8) == "125 Hz" && UsbBudgets.PollingRate(16) == "62.5 Hz", "Polling rate formatting.");
        Check(UsbBudgets.PollingInterval(0.125) == "every 125 µs" && UsbBudgets.PollingInterval(1) == "every 1 ms" && UsbBudgets.PollingInterval(8) == "every 8 ms", "Polling interval formatting.");
        Check(UsbBudgets.PollingNote(0x81, 10, 1).Contains("asks for every 10 ms") && !UsbBudgets.PollingNote(0x81, 8, 1).Contains("asks for every"), "Rounded full-speed polling explains the declared interval.");
        // The fastest interrupt IN pipe sets the polling rate; OUT pipes and isochronous streams don't.
        var pipes = new byte[4096];
        BitConverter.GetBytes(4).CopyTo(pipes, 27);
        byte[] open = [7, 5, 0x81, 3, 64, 0, 4, 0, 0, 0, 0, 7, 5, 0x83, 3, 64, 0, 1, 0, 0, 0, 0, 7, 5, 0x02, 3, 64, 0, 1, 0, 0, 0, 0, 7, 5, 0x84, 1, 0, 2, 1, 0, 0, 0, 0];
        open.CopyTo(pipes, 35);
        var wheelPipes = new UsbNode();
        UsbScanner.ReadOpenPipes(pipes, 35 + open.Length, 1, [], wheelPipes);
        Check(wheelPipes.PollIntervalMs == 1 && wheelPipes.Notes.Any(n => n.Contains("endpoint 83")), "The fastest interrupt input pipe sets the polling rate.");
        var highSpeed = new UsbNode();
        BitConverter.GetBytes(1).CopyTo(pipes, 27);
        UsbScanner.ReadOpenPipes(pipes, 35 + 11, 2, [], highSpeed);
        Check(highSpeed.PollIntervalMs == 1, "A high-speed interrupt input pipe with bInterval 4 is polled every 1 ms.");
        var storage = new UsbNode();
        pipes[35 + 3] = 2;
        UsbScanner.ReadOpenPipes(pipes, 35 + 11, 2, [], storage);
        Check(storage.PollIntervalMs == null && storage.ReservedMbps == 0, "Bulk pipes are not polled for input.");

        // HID usages come from the hardware IDs of each collection.
        var usages = DeviceIdentity.ReadHidUsages([@"HID\VID_0EB7&PID_0020&REV_0100", @"HID\VID_0EB7&UP:0001_U:0004", "HID_DEVICE_SYSTEM_GAME", "HID_DEVICE_UP:0001_U:0004", "HID_DEVICE_UP:FF00_U:0001", "HID_DEVICE_UP:0001_U:0004", "HID_DEVICE"]);
        Check(usages.SequenceEqual(["Joystick", "Vendor-defined"]), "HID usages are read from HID_DEVICE_UP hardware IDs, once each.");
        Check(DeviceIdentity.UsageName(2, 0xC8) == "Simulation controls" && DeviceIdentity.UsageName(1, 5) == "Game pad" && DeviceIdentity.UsageName(0x59, 1) == "Lighting" && DeviceIdentity.UsageName(0x40, 1) == "Usage page 0040h, usage 0001h", "HID usage names.");
        static UsbNode Identified(string name, List<string> hid, params string[] functions) { var n = new UsbNode { Name = name, HidUsages = hid, InterfaceFunctions = [.. functions], DeviceClass = "Defined by interfaces" }; DeviceIdentity.Identify(n); return n; }
        Check(Identified("Uninformative product", ["Joystick", "Vendor-defined"], "HID / controls").DeviceType == "Game controller", "A joystick collection marks a game controller.");
        Check(Identified("Sony Interactive Entertainment Wireless Controller", ["Game pad"], "Audio", "HID / controls").DeviceType == "Game controller", "A game pad with audio is still a game controller.");
        Check(Identified("Analog keyboard", ["Keyboard", "Game pad"], "Keyboard").DeviceType == "Keyboard" && Identified("Uninformative product", ["Keyboard", "Game pad"], "Keyboard", "HID / controls").DeviceType == "Keyboard", "A boot keyboard that also offers a game pad stays a keyboard.");
        Check(Identified("Uninformative product", ["Consumer controls"], "HID / controls").DeviceType == "HID / controls", "Other HID collections don't make a game controller.");
        foreach (var name in new[] { "FANATEC CSL DD", "Heusinkveld Sim Pedals Sprint", "MOZA HBP Handbrake", "Simucube 2 Pro", "Thrustmaster T300RS", "Logitech G923 Racing Wheel for PlayStation and PC", "Asetek SimSports Invicta Wheelbase", "VRS DirectForce Pro", "Simagic Alpha Mini", "DIY button box", "Honeycomb Bravo Throttle Quadrant", "Direct-drive wheel base" })
            Check(Identified(name, [], "HID / controls").DeviceType == "Game controller", $"\"{name}\" is sim hardware.");
        Check(Identified("Logitech Wheel Mouse Optical", [], "Mouse").DeviceType == "Mouse" && Identified("Thrustmaster Y-300CPX Headset", [], "Audio").DeviceType == "Audio" && Identified("Wheelock Industries sensor", [], "HID / controls").DeviceType == "HID / controls", "Sim hardware names must not capture mice, headsets or partial words.");

        // A HID collection belongs to the USB device above its interface; across a Bluetooth link, to no USB device.
        var devices = new Dictionary<string, UsbScanner.DevNode>(StringComparer.OrdinalIgnoreCase)
        {
            [@"HID\VID_1234&PID_0001&MI_00\8&1"] = new(@"USB\VID_1234&PID_0001&MI_00\7&1", "", ["Joystick"]),
            [@"USB\VID_1234&PID_0001&MI_00\7&1"] = new(@"USB\VID_1234&PID_0001\SERIAL", "HidUsb", []),
            [@"USB\VID_1234&PID_0001\SERIAL"] = new(@"USB\ROOT_HUB30\5&1", "usbccgp", []),
            [@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&09CC\9&1"] = new(@"BTHENUM\{00001124}\8&2", "", ["Game pad"]),
            [@"BTHENUM\{00001124}\8&2"] = new(@"BTH\MS_BTHBRB\7&3", "HidBth", []),
            [@"BTH\MS_BTHBRB\7&3"] = new(@"USB\VID_0489&PID_E111&MI_00\7&4", "", []),
            [@"USB\VID_0489&PID_E111&MI_00\7&4"] = new(@"USB\VID_0489&PID_E111\5&5", "BTHUSB", []),
        };
        Check(UsbScanner.UsbOwner(@"HID\VID_1234&PID_0001&MI_00\8&1", devices) == @"USB\VID_1234&PID_0001\SERIAL" && UsbScanner.UsbOwner(@"USB\VID_1234&PID_0001\SERIAL", devices) == @"USB\VID_1234&PID_0001\SERIAL", "HID collections and interfaces belong to their USB device.");
        Check(UsbScanner.UsbOwner(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&09CC\9&1", devices) == null && UsbScanner.UsbOwner(@"PCI\VEN_1022&DEV_15E2\4&1", devices) == null, "A Bluetooth controller must not be attributed to the Bluetooth adapter.");
        Check(UsbScanner.IsUsbDevice(@"USB\ROOT_HUB30\5&1") && !UsbScanner.IsUsbDevice(@"USB\VID_1234&PID_0001&MI_00\7&1"), "Composite interfaces are not USB devices.");

        // Device Manager's setting, per function: one off keeps a composite device awake; a HID driver must use it.
        static PowerSaving.Setting Setting(bool allowed, string service = "USBHUB3", bool? hid = null) => new("x", service, allowed, hid);
        Check(PowerSaving.Classify([]) == "Not offered" && PowerSaving.Classify([Setting(true)]) == "On" && PowerSaving.Classify([Setting(false)]) == "Off", "Power saving follows Device Manager's setting.");
        Check(PowerSaving.Classify([Setting(true, "HidUsb", true), Setting(false, "HidUsb", true)]) == "Off", "One function with power saving off keeps a composite device awake.");
        Check(PowerSaving.Classify([Setting(true, "HidUsb", false)]) == "Unused by driver" && PowerSaving.Classify([Setting(true, "HidUsb", true)]) == "On" && PowerSaving.Classify([Setting(true, "HidUsb")]) == "On", "A HID driver suspends only when SelectiveSuspendEnabled is on; unknown is not ruled out.");
        UsbNode Controller(string type, string saving) => new() { Kind = "Device", DeviceType = type, PowerSaving = saving };
        List<UsbNode> Flagged(bool? plan, params UsbNode[] nodes)
        {
            var s = new Snapshot { UsbSuspendPluggedIn = plan, UsbSuspendOnBattery = false, OnBattery = false, Controllers = [.. nodes] };
            PowerSaving.Analyze(s);
            return nodes.Where(n => n.PowerWarnings.Contains(PowerSaving.Warning)).ToList();
        }
        var wheel = Controller("Game controller", "On");
        Check(Flagged(true, wheel, Controller("Keyboard", "On"), Controller("Game controller", "Off"), Controller("Game controller", "Unused by driver")).SequenceEqual([wheel]), "Only game controllers that Windows may suspend are flagged.");
        Check(Flagged(false, Controller("Game controller", "On")).Count == 0, "Nothing is flagged while the power plan's USB selective suspend is off.");
        Check(Flagged(null, Controller("Game controller", "On")).Count == 1, "An unreported plan setting is taken as Windows' default, on.");
        var onBattery = new Snapshot { UsbSuspendPluggedIn = true, UsbSuspendOnBattery = false, OnBattery = true };
        Check(onBattery.UsbSuspendActive == false && PowerSaving.PlanSummary(onBattery) == "Selective suspend off" && PowerSaving.PlanNote(onBattery).Contains("on when plugged in and off on battery"), "The plan setting in force follows the power source.");
        var demoWheel = demo.Nodes.Single(n => n.Id == "demo/root/9");
        Check(demoWheel.DeviceType == "Game controller" && demoWheel.PowerWarnings.SequenceEqual([PowerSaving.Warning]) && demo.Nodes.Count(n => n.PowerWarnings.Contains(PowerSaving.Warning)) == 1, "The sample wheel base is a game controller Windows may suspend.");
    }

    // A SuperSpeedPlus link's rate and lanes, as Windows reports them, and what they do to the figures.
    private static void LinkRateTests()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        // Port index and length, then RX and TX: a sublink speed (mantissa in the high word, exponent 3 for
        // Gb/s in bits 4-5, SuperSpeedPlus protocol in bit 14) and a lane count that is one less than the lanes.
        static byte[] Info(uint speed, uint lanes) => [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(24), .. BitConverter.GetBytes(speed), .. BitConverter.GetBytes(lanes), .. BitConverter.GetBytes(speed), .. BitConverter.GetBytes(lanes)];
        const uint Gen2 = 0x000A4030, Gen1 = 0x00050030;
        Check(UsbScanner.DecodeSuperSpeedPlus(Info(Gen2, 0)) is ("SuperSpeedPlus · 10 Gb/s", 10000, 1), "One 10 Gb/s lane is 10 Gb/s.");
        Check(UsbScanner.DecodeSuperSpeedPlus(Info(Gen2, 1)) is ("SuperSpeedPlus · 20 Gb/s · 2 lanes", 20000, 2), "Two 10 Gb/s lanes are 20 Gb/s.");
        Check(UsbScanner.DecodeSuperSpeedPlus(Info(Gen1, 1)) is ("SuperSpeedPlus · 10 Gb/s · 2 lanes", 10000, 2), "Two 5 Gb/s lanes are 10 Gb/s.");
        Check(UsbScanner.DecodeSuperSpeedPlus(Info(0, 0)) == null && UsbScanner.DecodeSuperSpeedPlus(new byte[8]) == null, "A rate Windows left empty stays unresolved.");
        byte[] uneven = Info(Gen2, 1); BitConverter.GetBytes(Gen1).CopyTo(uneven, 16); BitConverter.GetBytes(0).CopyTo(uneven, 20);
        Check(UsbScanner.DecodeSuperSpeedPlus(uneven) is (_, 5000, 1), "A link is as fast as its slower direction.");
        var twenty = new UsbNode { Kind = "Device", LinkMbps = 20000, LinkLanes = 2, Speed = "SuperSpeedPlus · 20 Gb/s · 2 lanes" };
        var gen1x2 = new UsbNode { Kind = "Device", LinkMbps = 10000, LinkLanes = 2 };
        var unresolved = new UsbNode { Kind = "Device", Speed = "SuperSpeedPlus · 10 Gb/s or higher" };
        Check(Topology.ShortSpeed(twenty) == "20 Gb/s" && Topology.ShortSpeed(new UsbNode { LinkMbps = 10000 }) == "10 Gb/s" && Topology.ShortSpeed(unresolved) == "≥10 Gb/s" && Topology.ShortSpeed(new UsbNode { LinkMbps = 5000 }) == "5 Gb/s", "A resolved rate is shown exactly, and an unresolved one as a floor.");
        Check(Math.Abs(UsbBudgets.ReservableMbps(twenty)!.Value - 20000 * 128.0 / 132 * 0.9) < 0.01 && UsbBudgets.ReservableMbps(gen1x2) == 7200
            && Math.Abs(UsbBudgets.ReservableMbps(unresolved)!.Value - 10000 * 128.0 / 132 * 0.9) < 0.01, "Reservable time follows the resolved rate and its lanes' encoding; unresolved assumes 10 Gb/s.");
        Check(UsbBudgets.SuperSpeed(twenty) && !HubRelationships.ReducedSpeed(twenty), "A resolved SuperSpeedPlus link is a USB 3 link at full speed.");
        Check(UsbScanner.PciIdentity(@"PCI\VEN_1022&DEV_1128&SUBSYS_380C17AA&REV_00\4&4A6783B&0&0441") == ("1022:1128", "17AA:380C", "00"), "A PCI instance ID names vendor:device, the subsystem's vendor:device and the revision.");
        Check(UsbScanner.PciIdentity(@"pci\ven_8086&dev_a36d") == ("8086:A36D", "", "") && UsbScanner.PciIdentity(@"ACPI\PNP0D10\0") == ("", "", ""), "A short PCI ID still names the chip, and a controller that isn't on PCI has none.");
        var host = new UsbNode { Kind = "Controller", PciId = "8086:A36D", PciAddress = "00:14.0" };
        Check(Topology.PciText(host) == "PCI 8086:A36D at 00:14.0" && Topology.PciVendor(host.PciId) == "Intel" && Topology.PciText(new UsbNode()) == "" && Topology.SearchText(host, "H01").Contains("8086:A36D"), "A host controller is named and found by its PCI identity.");
    }

    // The firmware's port map: root ports whose description contradicts itself are noted, and nothing else is.
    private static void PortMapTests(Snapshot demo)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static UsbNode Port(int number, string protocols, bool user = true, bool? usbC = false) => new() { Id = "r/" + number, Port = number, Kind = "Empty port", Protocols = protocols, PortIsUserConnectable = user, PortConnectorIsTypeC = usbC };
        static void Link(UsbNode a, UsbNode b) { a.CompanionId = b.Id; a.CompanionPortNumber = b.Port; }
        static Snapshot Board(params UsbNode[] ports) { var s = new Snapshot { Controllers = [new UsbNode { Id = "c", Kind = "Controller", Children = [new UsbNode { Id = "r", Kind = "Root hub", Children = [.. ports] }] }] }; PortMap.Analyze(s); return s; }
        const string Usb2 = "USB 1.x / USB 2.0", Usb3 = "USB 3.x";

        // A well-described board: a paired socket, a USB 2-only socket, and built-in ports that pair with nothing.
        UsbNode a2 = Port(1, Usb2), a3 = Port(2, Usb3), only2 = Port(3, Usb2), camera = Port(4, Usb2, false), internal3 = Port(5, Usb3, false);
        Link(a2, a3); Link(a3, a2);
        var good = Board(a2, a3, only2, camera, internal3);
        Check(good.Nodes.All(n => n.PortMapWarnings.Count == 0 && IssueRules.For(n).Count == 0), "A consistent port map has no findings: USB 2-only sockets and built-in USB 3 ports are fine.");

        var lone3 = Port(1, Usb3);
        Board(lone3);
        Check(lone3.PortMapWarnings.SequenceEqual([PortMap.NoUsb2Half]) && lone3.Notes.Count(n => n.StartsWith("Port map evidence: ")) == 1, "A USB 3 port you can plug into needs a USB 2 half.");
        var finding = IssueRules.For(lone3).Single();
        var explained = Explanations.For(lone3, finding.Text, [lone3]);
        Check(finding.Severity == Severity.Note && explained.What.Contains("without a USB 2 half") && explained.Affects.StartsWith("No:") && explained.Cause.Contains("firmware") && explained.Steps!.Count == 2, "A port map finding is a note that says nothing plugged in is affected and that the firmware is the cause.");
        PortMap.Analyze(new Snapshot { Controllers = [new UsbNode { Id = "c", Kind = "Controller", Children = [new UsbNode { Id = "r", Kind = "Root hub", Children = [lone3] }] }] });
        Check(lone3.PortMapWarnings.Count == 1 && lone3.Notes.Count == 1, "Checking a snapshot again repeats nothing.");

        UsbNode one2 = Port(1, Usb2), one3 = Port(2, Usb3), other2 = Port(3, Usb2);
        Link(one2, one3); Link(one3, other2); Link(other2, one3);
        Board(one2, one3, other2);
        Check(one2.PortMapWarnings.SequenceEqual([PortMap.CompanionOneWay]) && one3.PortMapWarnings.Count == 0 && other2.PortMapWarnings.Count == 0 && one2.Notes.Any(n => n.Contains("H01/02 names H01/03")),
            "A port whose other half names a different port is paired one way, and the evidence names the ports.");

        UsbNode same1 = Port(1, Usb2), same2 = Port(2, Usb2);
        Link(same1, same2); Link(same2, same1);
        Board(same1, same2);
        Check(same1.PortMapWarnings.SequenceEqual([PortMap.SameVersionHalves]) && same2.PortMapWarnings.Count == 0, "Two USB 2 ports can't be the halves of one socket, and a pair is noted once.");

        UsbNode c2 = Port(1, Usb2, true, true), c3 = Port(2, Usb3, false, false);
        Link(c2, c3); Link(c3, c2);
        Board(c2, c3);
        Check(c2.PortMapWarnings.SequenceEqual([PortMap.HalvesDisagree]) && c2.Notes.Any(n => n.Contains("USB-C") && n.Contains("plug into")), "Halves that differ on USB-C or on being a port you can plug into are noted with both differences.");
        c3.PortConnectorIsTypeC = null; c3.PortIsUserConnectable = null; c2.Notes.Clear();
        PortMap.Analyze(new Snapshot { Controllers = [new UsbNode { Id = "c", Kind = "Controller", Children = [new UsbNode { Id = "r", Kind = "Root hub", Children = [c2, c3] }] }] });
        Check(c2.PortMapWarnings.Count == 0, "What Windows didn't report about a half isn't a disagreement, and a finding that no longer holds is cleared.");

        var missing = Port(1, Usb3); missing.CompanionPortNumber = 9; missing.CompanionHubSymbolicLink = "elsewhere";
        var partial = Board(missing);
        Check(missing.PortMapWarnings.SequenceEqual([PortMap.CompanionMissing]), "A port whose named other half isn't in the scan is noted.");
        partial.Controllers[0].ScanIncomplete = true; PortMap.Analyze(partial);
        Check(missing.PortMapWarnings.Count == 0, "When part of the scan couldn't be read, a missing half isn't called missing.");

        // A socket with several companions is paired when any of them names it back.
        UsbNode multi3 = Port(1, Usb3), first2 = Port(2, Usb2), second2 = Port(3, Usb2);
        Link(multi3, first2); Link(first2, multi3); Link(second2, multi3);
        multi3.PortHasMultipleCompanions = true; multi3.MoreCompanions.Add(new() { PortNumber = 3, HubSymbolicLink = "ROOT" });
        var several = new Snapshot { Controllers = [new UsbNode { Id = "c", Kind = "Controller", Children = [new UsbNode { Id = "r", Kind = "Root hub", HubSymbolicLink = @"\\?\root", Children = [multi3, first2, second2] }] }] };
        HubRelationships.ResolveCompanions(several); PortMap.Analyze(several);
        Check(multi3.MoreCompanions[0].Id == second2.Id && several.Nodes.All(n => n.PortMapWarnings.Count == 0), "Further companions resolve to their ports and count as halves.");

        // A plug-in hub's ports aren't described by firmware, so they aren't judged.
        var hubPort = Port(1, Usb3); hubPort.Id = "r/1/1";
        var plugIn = new UsbNode { Id = "r/1", Port = 1, Kind = "Hub", Protocols = Usb2, PortIsUserConnectable = true, Children = [hubPort] };
        Board(plugIn);
        Check(hubPort.PortMapWarnings.Count == 0, "Only root ports are checked.");

        var studio = demo.Nodes.First(n => n.Name == "Studio desktop hub");
        Check(demo.Nodes.Where(n => n.PortMapWarnings.Count > 0).ToList() is [var noted] && noted == studio && studio.PortMapWarnings.SequenceEqual([PortMap.NoUsb2Half]), "The sample host's USB 3 port with no USB 2 half shows the port map note, and its paired sockets don't.");
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
        // Full- and low-speed devices behind a single-TT hub share one 12 Mb/s bus, whatever the hub's own link.
        UsbNode FullSpeed(double now, double peak) => new() { Kind = "Device", LinkMbps = 12, ReservedMbps = now, PeakReservedMbps = peak };
        UsbNode SingleTt(params UsbNode[] children) => new() { Kind = "Hub", LinkMbps = 480, ReservedMbps = 0.0001, TransactionTranslators = "Single", Children = [.. children] };
        var interfaces = SingleTt(FullSpeed(6, 6), FullSpeed(6, 6));
        Check(UsbBudgets.SharedTtNearlyFull(interfaces) && !UsbBudgets.LinkNearlyFull(interfaces) && !UsbBudgets.LinkNearlyFull(interfaces.Children[0]), "Two full-speed audio interfaces that each fit their own link fill a single TT together.");
        interfaces.TransactionTranslators = "Per port";
        Check(UsbBudgets.SharedTtUse(interfaces) == null && !UsbBudgets.SharedTtNearlyFull(interfaces), "A hub with one TT per port gives each port its own bus.");
        var idleInterfaces = SingleTt(FullSpeed(0, 6), FullSpeed(0.5, 6));
        Check(UsbBudgets.SharedTtCouldExceed(idleInterfaces) && !UsbBudgets.SharedTtNearlyFull(idleInterfaces), "Idle full-speed peaks that overflow a single TT warn before they stream.");
        Check(!UsbBudgets.SharedTtCouldExceed(SingleTt(FullSpeed(0, 6), new UsbNode { Kind = "Device", LinkMbps = 480, ReservedMbps = 0, PeakReservedMbps = 200 })), "High-speed devices don't use the TT.");
        var behindOnePort = SingleTt(new UsbNode { Kind = "Hub", LinkMbps = 12, ReservedMbps = 0.0001, Children = [FullSpeed(6, 6), FullSpeed(6, 6)] });
        Check(UsbBudgets.SharedTtUse(behindOnePort) is (var onePort, _, 0, 1) && Near(onePort, 12.0001) && !UsbBudgets.SharedTtNearlyFull(behindOnePort) && UsbBudgets.LinkNearlyFull(behindOnePort.Children[0]), "Devices behind one port warn against that port's own link, not again as a shared TT.");
        Check(UsbBudgets.SharedTtUse(SingleTt(new UsbNode { Kind = "Device", LinkMbps = 1.5, ReservedMbps = 0.064 }, new UsbNode { Kind = "Device", LinkMbps = 12 })) is (var lowSpeed, _, 1, 2) && Near(lowSpeed, 0.512), "Low-speed bytes take eight full-speed byte times; unreported devices count as unknown.");
        var demoTravel = demo.Nodes.Single(n => n.Id == "demo/root/5");
        Check(UsbBudgets.SharedTtUse(demoTravel) is (_, _, 0, 1) && !UsbBudgets.SharedTtNearlyFull(demoTravel) && !UsbBudgets.SharedTtCouldExceed(demoTravel), "The travel hub's one full-speed device uses its single TT without a warning.");
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
        static int Flagged(ReconnectTracker t, string instance) { var n = new UsbNode { Kind = "Device", InstanceId = instance }; t.Apply(new Snapshot { Controllers = [n] }); return n.QuickReconnects; }
        const string id = @"USB\VID_1234&PID_5678\SERIAL";
        for (int i = 0; i < 2; i++) { tracker.Removed(id, t0.AddSeconds(i * 20)); tracker.Arrived(id, t0.AddSeconds(i * 20 + 2)); }
        tracker.Removed(id, t0.AddSeconds(50)); tracker.Arrived(id, t0.AddSeconds(120));
        Check(Flagged(tracker, id) == 0, "A slow return is a deliberate replug, not a drop.");
        tracker.Removed(id, t0.AddSeconds(130)); tracker.Arrived(id, t0.AddSeconds(133));
        Check(Flagged(tracker, id) == 3, "Three quick returns within five minutes mark an unstable connection.");
        var flaky = new UsbNode { Kind = "Device", InstanceId = id.ToLowerInvariant() };
        tracker.Apply(new Snapshot { Controllers = [flaky] });
        Check(flaky.QuickReconnects == 3, "Instance IDs match regardless of case.");
        var sample = new UsbNode { Kind = "Device", InstanceId = id };
        tracker.Apply(new Snapshot { IsDemo = true, Controllers = [sample] });
        Check(sample.QuickReconnects == 0, "Hardware reconnects must not leak into sample data.");
        // Switching a KVM drops the monitor's hub and everything behind it together: only the hub is flagged.
        var kvm = new ReconnectTracker();
        const string hubId = @"USB\VID_0451&PID_8442\HUB", keyboardId = @"USB\VID_320F&PID_5044\KEYBOARD";
        for (int i = 0; i < 3; i++)
        {
            kvm.Removed(hubId, t0.AddSeconds(i * 40)); kvm.Removed(keyboardId, t0.AddSeconds(i * 40));
            kvm.Arrived(hubId, t0.AddSeconds(i * 40 + 2)); kvm.Arrived(keyboardId, t0.AddSeconds(i * 40 + 4));
        }
        var kvmKeyboard = new UsbNode { Kind = "Device", InstanceId = keyboardId };
        var monitorKvm = new UsbNode { Kind = "Hub", InstanceId = hubId, Children = [kvmKeyboard] };
        kvm.Apply(new Snapshot { Controllers = [new UsbNode { Kind = "Controller", Children = [monitorKvm] }] });
        Check(monitorKvm.QuickReconnects == 3 && kvmKeyboard.QuickReconnects == 0, "Devices that drop with their hub are explained by the hub.");
        Check(IssueRules.For(monitorKvm).Contains((Severity.Note, "Unstable connection")) && IssueRules.For(new UsbNode { Kind = "Device", QuickReconnects = 3 }).Contains((Severity.Warning, "Unstable connection")),
            "A whole hub reconnecting, as a KVM switch does, is a note; a device dropping on its own is a warning.");
        string lastReturn = monitorKvm.QuickReconnectTimes[^1].ToString("T");
        Check(monitorKvm.Notes.Any(n => n.Contains(lastReturn)) && Explanations.For(monitorKvm, "Unstable connection", [monitorKvm]).What.Contains(lastReturn),
            "Reconnect times read the same in the explanation and in Detection details.");
        for (int i = 0; i < 3; i++) { kvm.Removed(keyboardId, t0.AddSeconds(600 + i * 20)); kvm.Arrived(keyboardId, t0.AddSeconds(602 + i * 20)); }
        kvmKeyboard.QuickReconnects = 0; monitorKvm.QuickReconnects = 0;
        kvm.Apply(new Snapshot { Controllers = [new UsbNode { Kind = "Controller", Children = [monitorKvm] }] });
        Check(kvmKeyboard.QuickReconnects == 3 && kvmKeyboard.QuickReconnectTimes[0] == t0.AddSeconds(602), "A device that also drops on its own is flagged for those drops.");
        // A hub with no instance can't explain its devices' drops, so they count as their own.
        var anonymous = new UsbNode { Kind = "Hub", Children = [new UsbNode { Kind = "Device", InstanceId = hubId }] };
        kvm.Apply(new Snapshot { Controllers = [anonymous] });
        Check(anonymous.QuickReconnects == 0 && anonymous.Children[0].QuickReconnects == 3, "Without a hub instance, a device's drops are its own.");
        // Waking powers devices back up: returns just after a wake aren't drops, and later ones still are.
        var sleeper = new ReconnectTracker();
        for (int i = 0; i < 3; i++)
        {
            sleeper.Removed(id, t0.AddSeconds(i * 60)); sleeper.Woke(t0.AddSeconds(i * 60 + 1));
            sleeper.Removed(id, t0.AddSeconds(i * 60 + 2)); sleeper.Arrived(id, t0.AddSeconds(i * 60 + 5));
        }
        Check(Flagged(sleeper, id) == 0, "Devices that return as the computer wakes aren't counted as drops.");
        for (int i = 0; i < 3; i++) { sleeper.Removed(id, t0.AddSeconds(300 + i * 10)); sleeper.Arrived(id, t0.AddSeconds(302 + i * 10)); }
        Check(Flagged(sleeper, id) == 3, "Drops well after a wake still count.");
        Check(ReconnectTracker.InstanceIdFromPath(@"\\?\USB#VID_0451&PID_8442#MSFT20E30108613F47#{f18a0e88-c30c-11d0-8815-00a0c906bed8}") == @"USB\VID_0451&PID_8442\MSFT20E30108613F47", "Hub interface paths name their instance too.");
    }

    // A Billboard capability: each mode's state in bmConfigured, its SVID and index, and the failure info
    // that Billboard 1.0 doesn't have.
    private static byte[] BillboardCapability(int[] states, ushort[] svids, byte failure = 0, bool version10 = false, ushort vconn = 0)
    {
        int start = version10 ? 42 : 44, n = svids.Length;
        var d = new byte[start + 4 * n];
        d[0] = (byte)d.Length; d[1] = 0x10; d[2] = 0x0D; d[3] = 5; d[4] = (byte)n;
        BitConverter.GetBytes(vconn).CopyTo(d, 6);
        for (int i = 0; i < n; i++) d[8 + i / 4] |= (byte)(states[i] << (2 * (i % 4)));
        BitConverter.GetBytes((ushort)(version10 ? 0x0100 : 0x0121)).CopyTo(d, 40);
        if (!version10) d[42] = failure;
        for (int i = 0; i < n; i++) { BitConverter.GetBytes(svids[i]).CopyTo(d, start + 4 * i); d[start + 4 * i + 2] = (byte)i; d[start + 4 * i + 3] = (byte)(6 + i); }
        return d;
    }
    private static byte[] Bos(params byte[][] caps) { var body = caps.SelectMany(c => c).ToArray(); return [5, 15, .. BitConverter.GetBytes((ushort)(5 + body.Length)), (byte)caps.Length, .. body]; }

    // A laptop's sysfs as Linux lays it out: an Intel xHCI controller's USB 2 bus (usb1) and USB 3 bus (usb2),
    // a USB 3 hub on a USB-C socket seen on both buses, a keyboard on its USB 2 side, a 10 Gb/s enclosure on
    // its USB 3 side, a USB 3 flash drive stuck at USB 2, a built-in webcam, and an HID device with no driver.
    internal static FakeSysfs LinuxLaptop()
    {
        var fs = new FakeSysfs();
        const string D = "bus/usb/devices", Pci = "bus/pci/devices/0000:00:14.0";
        foreach (var (file, value) in new[] { ("vendor", "0x8086"), ("device", "0xa36d"), ("subsystem_vendor", "0x17aa"), ("subsystem_device", "0x2292"), ("revision", "0x10") }) fs.Files[$"{Pci}/{file}"] = value;
        fs.Links[$"{Pci}/driver"] = "../../../bus/pci/drivers/xhci_hcd";
        void Device(string name, Dictionary<string, string> attributes, byte[]? descriptors = null)
        {
            foreach (var (k, v) in attributes) fs.Files[$"{D}/{name}/{k}"] = v + "\n";
            if (descriptors != null) fs.Binary[$"{D}/{name}/descriptors"] = descriptors;
        }
        void Interface(string name, int cls, string? driver, params (string Ep, string Attributes, string Packet, string Interval)[] endpoints)
        {
            fs.Files[$"{D}/{name}/bInterfaceClass"] = cls.ToString("x2");
            if (driver != null) fs.Links[$"{D}/{name}/driver"] = "../../../../bus/usb/drivers/" + driver;
            foreach (var (ep, attributes, packet, interval) in endpoints)
                foreach (var (k, v) in new[] { ("bEndpointAddress", ep), ("bmAttributes", attributes), ("wMaxPacketSize", packet), ("bInterval", interval) }) fs.Files[$"{D}/{name}/ep_{ep}/{k}"] = v;
        }
        void Port(string hubInterface, string port, string connect, string? peer = null, bool typeC = false)
        {
            fs.Files[$"{D}/{hubInterface}/{port}/connect_type"] = connect;
            if (peer != null) fs.Links[$"{D}/{hubInterface}/{port}/peer"] = peer;
            if (typeC) fs.Links[$"{D}/{hubInterface}/{port}/connector"] = "../../../../../../../port1-connector";
        }
        // A HID keyboard's configuration: one interface, a HID descriptor and an interrupt IN endpoint every 10 ms.
        byte[] keyboard = [18, 1, 0x00, 0x02, 0, 0, 0, 8, 0x6D, 0x04, 0x1C, 0xC3, 0x04, 0x01, 1, 2, 0, 1,
            9, 2, 34, 0, 1, 1, 0, 0xA0, 50, 9, 4, 0, 0, 1, 3, 1, 1, 0, 9, 0x21, 0x11, 1, 0, 1, 0x22, 65, 0, 7, 5, 0x81, 3, 8, 0, 10];
        foreach (var (bus, version, ports) in new[] { (1, "2.00", 4), (2, "3.10", 2) })
        {
            fs.Links[$"{D}/usb{bus}"] = $"../../../devices/pci0000:00/0000:00:14.0/usb{bus}";
            Device($"usb{bus}", new() { ["version"] = " " + version, ["maxchild"] = ports.ToString(), ["product"] = "xHCI Host Controller", ["bDeviceClass"] = "09", ["speed"] = bus == 1 ? "480" : "5000" });
            fs.Files[$"{D}/{bus}-0:1.0/bInterfaceClass"] = "09";
        }
        Port("1-0:1.0", "usb1-port1", "hotplug", "../../usb2/2-0:1.0/usb2-port1");
        Port("1-0:1.0", "usb1-port2", "hotplug", "../../usb2/2-0:1.0/usb2-port2", typeC: true);
        Port("1-0:1.0", "usb1-port3", "hardwired");
        Port("1-0:1.0", "usb1-port4", "not used");
        Port("2-0:1.0", "usb2-port1", "hotplug", "../../usb1/1-0:1.0/usb1-port1");
        Port("2-0:1.0", "usb2-port2", "hotplug", "../../usb1/1-0:1.0/usb1-port2", typeC: true);
        // A USB 3 flash drive linked at 480 Mb/s.
        Device("1-1", new() { ["idVendor"] = "0781", ["idProduct"] = "5583", ["bcdDevice"] = "0100", ["version"] = " 3.20", ["speed"] = "480", ["bDeviceClass"] = "00", ["product"] = "Ultra Fit", ["manufacturer"] = "SanDisk", ["bMaxPower"] = "224mA", ["bmAttributes"] = "80", ["serial"] = "4C530001" });
        Interface("1-1:1.0", 8, "usb-storage");
        // The USB 3 hub's USB 2 side, with one TT, and its USB 3 side; their ports name each other.
        Device("1-2", new() { ["idVendor"] = "0bda", ["idProduct"] = "5411", ["bcdDevice"] = "0002", ["version"] = " 2.10", ["speed"] = "480", ["bDeviceClass"] = "09", ["bDeviceProtocol"] = "01", ["maxchild"] = "2", ["bmAttributes"] = "e0", ["bMaxPower"] = "0mA" });
        Interface("1-2:1.0", 9, "hub");
        Device("2-2", new() { ["idVendor"] = "0bda", ["idProduct"] = "0411", ["bcdDevice"] = "0002", ["version"] = " 3.20", ["speed"] = "5000", ["bDeviceClass"] = "09", ["bDeviceProtocol"] = "03", ["maxchild"] = "2", ["bmAttributes"] = "e0", ["bMaxPower"] = "0mA" });
        Interface("2-2:1.0", 9, "hub");
        for (int p = 1; p <= 2; p++) { Port("1-2:1.0", $"1-2-port{p}", "hotplug", $"../../2-2/2-2:1.0/2-2-port{p}"); Port("2-2:1.0", $"2-2-port{p}", "hotplug", $"../../1-2/1-2:1.0/1-2-port{p}"); }
        Device("1-2.1", new() { ["idVendor"] = "046d", ["idProduct"] = "c31c", ["bcdDevice"] = "6401", ["version"] = " 2.00", ["speed"] = "12", ["bDeviceClass"] = "00", ["product"] = "USB Keyboard", ["manufacturer"] = "Logitech", ["bMaxPower"] = "100mA", ["bmAttributes"] = "a0", ["bConfigurationValue"] = "1" }, keyboard);
        Interface("1-2.1:1.0", 3, "usbhid", ("81", "03", "0008", "0a"));
        // An HID device no driver claimed.
        Device("1-2.2", new() { ["idVendor"] = "1234", ["idProduct"] = "5678", ["version"] = " 2.00", ["speed"] = "12", ["bDeviceClass"] = "00", ["product"] = "Macro Pad", ["bMaxPower"] = "100mA", ["bmAttributes"] = "80" });
        Interface("1-2.2:1.0", 3, null);
        Device("2-2.2", new() { ["idVendor"] = "0bda", ["idProduct"] = "9210", ["bcdDevice"] = "2001", ["version"] = " 3.20", ["speed"] = "10000", ["rx_lanes"] = "1", ["bDeviceClass"] = "00", ["product"] = "RTL9210 NVMe", ["manufacturer"] = "Realtek", ["bMaxPower"] = "896mA", ["bmAttributes"] = "80" });
        Interface("2-2.2:1.0", 8, "uas");
        // A built-in webcam on a hardwired port.
        Device("1-3", new() { ["idVendor"] = "5986", ["idProduct"] = "2113", ["version"] = " 2.01", ["speed"] = "480", ["bDeviceClass"] = "ef", ["product"] = "Integrated Camera", ["bMaxPower"] = "500mA", ["bmAttributes"] = "80" });
        Interface("1-3:1.0", 14, "uvcvideo");
        return fs;
    }

    private static void LinuxTests(Action<bool, string> Check)
    {
        var s = new LinuxUsbScanner(LinuxLaptop()) { CaptureRaw = true }.Scan();
        var paths = Topology.PathLabels(s);
        UsbNode At(string path) => s.Nodes.First(n => paths[n.Id] == path);
        Check(s.Controllers.Count == 2 && s.Controllers[0].Name == "Intel xHCI Host Controller · bus 1" && s.Controllers[1].PciId == "8086:A36D" && s.Controllers[0].PciAddress == "00:14.0"
            && s.Controllers[0].PciSubsystem == "17AA:2292" && s.Controllers[0].DriverService == "xhci_hcd", "Each bus is a host named after its controller's PCI device.");
        var drive = At("H01/01");
        Check(drive.Name == "SanDisk Ultra Fit" && drive.LinkMbps == 480 && drive.SpeedLimited && IssueRules.For(drive).Any(i => i.Text == "Running at USB 2" && i.Severity == Severity.Warning)
            && drive.DriverService == "usb-storage" && drive.DeviceRevision == "1.00" && drive.MaxPowerMa == 224, "A USB 3 drive at 480 Mb/s runs at USB 2, as on Windows.");
        var root1 = s.Controllers[0].Children[0];
        Check(root1.Children[0].CompanionId == "usb2/1" && s.Controllers[1].Children[0].Children[0].CompanionId == "usb1/1" && root1.Children[1].Connector == "USB-C"
            && root1.Children[2].Connector == "Internal" && root1.Children[3].Kind == "Empty port", "Root ports pair through peer links, and USB-C and built-in ports are told apart.");
        var hub2 = At("H01/02"); var hub3 = At("H02/02");
        Check(hub2.CompanionHubId == hub3.Id && hub2.IsUsb2Companion && hub2.TransactionTranslators == "Single" && hub3.Children.Count == 2, "A USB 3 hub's two sides pair across the buses.");
        var keyboard = At("H01/02/01");
        Check(keyboard.DeviceType == "Keyboard" && keyboard.PollIntervalMs == 8 && keyboard.OpenPipes.Count == 1 && keyboard.DriverService == "usbhid" && keyboard.Raw?.Configuration.Length == 68,
            "A keyboard's interrupt endpoint gives its polling rate, as on Windows.");
        var pad = At("H01/02/02");
        Check(pad.KernelProblem == LinuxProblems.NoDriver && IssueRules.For(pad).Contains((Severity.Warning, LinuxProblems.NoDriver)) && Explanations.For(pad, LinuxProblems.NoDriver, [pad]).Steps!.Any(x => x.Contains("lsmod")),
            "An HID device no driver claimed is a warning explained in Linux terms.");
        var ssd = At("H02/02/02");
        Check(ssd.LinkMbps == 10000 && ssd.Speed == "SuperSpeedPlus · 10 Gb/s" && ssd.SuperSpeedPlusCapable == true && ssd.DeviceType == "External drive" && ssd.Children.Count == 0, "A 10 Gb/s enclosure reads its rate.");
        Check(At("H01/03").Location == "Internal" && At("H01/03").DeviceType == "Camera / video" && s.Nodes.All(n => n.PortMapWarnings.Count == 0), "A hardwired webcam is internal, and the port map is consistent.");
        Check(LinuxUsbScanner.Generation("8.0 GT/s PCIe") == 3 && LinuxUsbScanner.Speed("20000", 2).Speed == "SuperSpeedPlus · 20 Gb/s · 2 lanes" && LinuxUsbScanner.Speed("bogus", 1).Mbps == null, "PCIe and USB rates parse.");
        Check(new LinuxUsbScanner(new FakeSysfs()).Scan().Diagnostics.Count == 1, "No buses is a diagnostic, not a crash.");
    }

    private static void ContainerTests(Action<bool, string> Check)
    {
        // A monitor: its hub is named from the USB ID database, and its HID and Billboard sit behind it.
        const string monitorId = "{11111111-0000-0000-0000-000000000001}", realtek = "{20b9cde5-7039-e011-a935-0002a5d5c51b}";
        var hid = new UsbNode { Id = "r/1/1", Kind = "Device", Name = "USB Input Device", NameSource = "Generic reported name", ContainerId = monitorId };
        var bb = new UsbNode { Id = "r/1/2", Kind = "Device", Name = "Billboard", NameSource = "USB product / manufacturer descriptors", ContainerId = monitorId };
        var monitorHub = new UsbNode { Id = "r/1", Kind = "Hub", Name = "Realtek RTS5411 Hub", LookupProduct = "RTS5411 Hub", NameSource = "USB ID lookup", ContainerId = monitorId, Children = [hid, bb] };
        // Two physical USB 3 hubs, each seen as a USB 2 side and a USB 3 side, whose firmware gives both the same Container ID.
        UsbNode Side(string id, bool usb2, string companion) => new() { Id = id, Kind = "Hub", Name = "Realtek USB Hub", NameSource = "USB ID lookup", ContainerId = realtek, IsUsb2Companion = usb2, CompanionHubId = companion };
        var a2 = Side("r/2", true, "r/3"); var a3 = Side("r/3", false, "r/2"); var b2 = Side("r/4", true, "r/5"); var b3 = Side("r/5", false, "r/4");
        var builtIn = new UsbNode { Id = "r/6", Kind = "Device", Name = "Webcam", ContainerId = "{00000000-0000-0000-ffff-ffffffffffff}" };
        var builtIn2 = new UsbNode { Id = "r/7", Kind = "Device", Name = "Fingerprint reader", ContainerId = "{00000000-0000-0000-ffff-ffffffffffff}" };
        var snapshot = new Snapshot
        {
            Controllers = [new UsbNode { Id = "c", Kind = "Controller", Children = [new UsbNode { Id = "r", Kind = "Root hub", Children = [monitorHub, a2, a3, b2, b3, builtIn, builtIn2] }] }],
            Containers = [new DeviceContainer { Id = monitorId, Name = "DELL U2723QE", Model = "DELL U2723QE" }, new DeviceContainer { Id = realtek, Name = "USB3.2 Hub", Model = "USB3.2 Hub" }]
        };
        Containers.Analyze(snapshot); Containers.Analyze(snapshot);
        Check(monitorHub.Name == "Realtek RTS5411 Hub in DELL U2723QE" && monitorHub.NameSource == "USB ID lookup and device container" && hid.Name == "USB Input Device in DELL U2723QE" && bb.Name == "Billboard",
            "Chip-named and generic members are named after their product once; a device that names itself keeps its name.");
        Check(Containers.PartOf(snapshot, hid) == "DELL U2723QE" && Containers.Of(snapshot, monitorHub)!.Value.Others.Count == 2 && IssueRules.For(monitorHub).Count == 0,
            "Members of one product show what they're part of.");
        Check(!a2.ContainerIdShared && a3.ContainerIdShared && b3.ContainerIdShared && IssueRules.For(a3).Contains((Severity.Note, Containers.SharedId)) && a2.Name == "Realtek USB Hub",
            "Two physical hubs with one Container ID are flagged once each, on their USB 3 side, and aren't renamed.");
        Check(Containers.SharingWith(snapshot, a3).SequenceEqual([b3]) && Containers.SharingWith(snapshot, a2).SequenceEqual([b3]) && Containers.PartOf(snapshot, a2) == "ID shared with 1 other",
            "A hub's own other side isn't unrelated hardware.");
        Check(Containers.Explain(a3).Cause.Contains("same Container ID") && Containers.Explain(a3).Affects.StartsWith("No:"), "The explanation says it's harmless and comes from firmware.");
        Check(!builtIn.ContainerIdShared && Containers.Of(snapshot, builtIn) == null && Containers.PartOf(snapshot, builtIn) == "", "The computer's own container groups nothing.");
        // One hub's two sides alone are one unit, and a generic container name names nothing.
        snapshot.Controllers[0].Children[0].Children.RemoveAll(n => n == b2 || n == b3);
        Containers.Analyze(snapshot);
        Check(!a3.ContainerIdShared && Containers.Of(snapshot, a2)!.Value.Product == "" && a2.Name == "Realtek USB Hub", "A USB 3 hub's two sides are one product.");
        Check(Topology.SearchText(monitorHub, null).Contains("DELL U2723QE"), "Search finds devices by their container's name.");
    }

    private static void BillboardTests(Action<bool, string> Check)
    {
        // Captured from a monitor-style hub's Billboard on real hardware: DisplayPort (SVID FF01) entered.
        var real = Billboard.Decode(Convert.FromHexString("050F490002" + "30100D050100000003" + new string('0', 62) + "0102000001FF0006" + "1410040000000000000000000000000000000000"))!;
        Check(real.Info.Modes is [{ Svid: "FF01", Name: "DisplayPort", Index: 0, State: "Entered" }] && real.UrlString == 5 && real.ModeStrings is [6] && real.Info.VconnPower == "1 W" && !real.Info.InsufficientPower,
            "A captured Billboard decodes DisplayPort as entered.");
        // DisplayPort failed for lack of power, and a vendor mode that wasn't asked for, with Billboard Ex VDOs.
        var failed = Billboard.Decode(Bos(BillboardCapability([2, 1], [0xFF01, 0x0BDA], failure: 1, vconn: 0x8000), [8, 0x10, 0x0F, 0, 0x45, 0x0C, 0x00, 0x00]))!.Info;
        Check(failed.Modes[0].State == "Failed" && failed.Modes[1].State == "Not entered" && failed.Modes[1].Name == "Realtek mode"
            && failed.InsufficientPower && !failed.PowerDeliveryFailed && failed.VconnPower == "Not required" && failed.Version == "1.21" && failed.Modes[0].Vdo == "00000C45",
            "Modes decode their state, SVID name and VDO, and the failure info its bits.");
        var device = new UsbNode { Kind = "Device", Name = "Monitor Billboard", Billboard = failed };
        var why = Explanations.For(device, Billboard.Failed, [device]);
        Check(IssueRules.For(device).SequenceEqual([(Severity.Warning, Billboard.Failed)]) && why.What.Contains("offers DisplayPort") && why.Affects.StartsWith("Yes: the picture doesn't come through")
            && why.Cause.Contains("enough power") && why.Steps![0].Contains("power adapter") && why.Steps.Any(s => s.Contains("DisplayPort (a DP or D logo)")), "A failed DisplayPort mode is a warning that names the mode, the cause and what to do.");
        Check(Billboard.Summary(device) == "DisplayPort · failed, Realtek mode · not entered" && Topology.SearchText(device, null).Contains("DisplayPort · failed"), "Properties and search show each mode's state.");
        // Nothing failed and nothing entered: the computer never asked, which may be what's wanted.
        var idle = new UsbNode { Kind = "Device", Billboard = Billboard.Decode(BillboardCapability([1], [0xFF01], failure: 2))!.Info };
        Check(IssueRules.For(idle).SequenceEqual([(Severity.Note, Billboard.NotEntered)]) && Explanations.For(idle, Billboard.NotEntered, [idle]).What.Contains("didn't ask for it"), "A mode never asked for is a note.");
        Check(idle.Billboard!.PowerDeliveryFailed && !idle.Billboard.InsufficientPower, "The second failure-info bit is a USB PD failure.");
        // One mode entered and the others idle is how alternate modes work, so it's not an issue.
        Check(IssueRules.For(new UsbNode { Kind = "Device", Billboard = Billboard.Decode(BillboardCapability([3, 1], [0xFF01, 0x8087]))!.Info }).Count == 0, "One mode entered and another idle is fine.");
        // An unspecified error counts as a failure.
        Check(IssueRules.For(new UsbNode { Kind = "Device", Billboard = Billboard.Decode(BillboardCapability([0], [0x8087]))!.Info }).Contains((Severity.Warning, Billboard.Failed)), "An unspecified error is a failure.");
        // Billboard 1.0 has no failure info, so its modes start two bytes earlier.
        var old = Billboard.Decode(BillboardCapability([3], [0xFF01], version10: true))!.Info;
        Check(old.Modes is [{ Svid: "FF01", State: "Entered" }] && old.Version == "1.00" && !old.InsufficientPower, "A Billboard 1.0 capability decodes from its shorter layout.");
        // Five modes span two bytes of bmConfigured.
        var five = Billboard.Decode(BillboardCapability([3, 1, 1, 1, 2], [0xFF01, 1, 2, 3, 4]))!.Info;
        Check(five.Modes[4].State == "Failed" && five.Modes[3].State == "Not entered", "bmConfigured gives each mode two bits, four modes to a byte.");
        Check(Billboard.Decode(BillboardCapability([3], [0xFF01])[..30]) == null && Billboard.Decode(Bos()) == null && Billboard.Decode([]) == null, "A truncated capability or a BOS without one decodes to nothing.");
        Check(Billboard.SvidName(0x8087) == "Thunderbolt" && Billboard.SvidName(0xFFFE) == "Vendor mode FFFE", "Thunderbolt is named, and unknown SVIDs say so.");
        // The captured device puts its maker's name where a web address belongs.
        Check(!Billboard.LooksLikeUrl("GsCooLink") && Billboard.LooksLikeUrl("www.dell.com/support") && Billboard.LooksLikeUrl("https://example.com"), "Only a web address is offered as a help page.");
    }
}

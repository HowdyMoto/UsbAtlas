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
        // A hub's connection information: the port index, then its device descriptor, whose bDeviceProtocol names its TTs.
        var hubInfo = new byte[35]; new byte[] { 18, 1, 0x00, 0x02, 9, 0, 2, 64 }.CopyTo(hubInfo, 4);
        Check(UsbScanner.TransactionTranslators(hubInfo) == "Per port", "bDeviceProtocol 2 is one TT per port.");
        hubInfo[10] = 1; Check(UsbScanner.TransactionTranslators(hubInfo) == "Single", "bDeviceProtocol 1 is one TT for all ports.");
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
        for (int i = 0; i < 3; i++) { kvm.Removed(keyboardId, t0.AddSeconds(600 + i * 20)); kvm.Arrived(keyboardId, t0.AddSeconds(602 + i * 20)); }
        kvmKeyboard.QuickReconnects = 0; monitorKvm.QuickReconnects = 0;
        kvm.Apply(new Snapshot { Controllers = [new UsbNode { Kind = "Controller", Children = [monitorKvm] }] });
        Check(kvmKeyboard.QuickReconnects == 3 && kvmKeyboard.QuickReconnectTimes[0] == t0.AddSeconds(602), "A device that also drops on its own is flagged for those drops.");
        // A hub with no instance can't explain its devices' drops, so they count as their own.
        var anonymous = new UsbNode { Kind = "Hub", Children = [new UsbNode { Kind = "Device", InstanceId = hubId }] };
        kvm.Apply(new Snapshot { Controllers = [anonymous] });
        Check(anonymous.QuickReconnects == 0 && anonymous.Children[0].QuickReconnects == 3, "Without a hub instance, a device's drops are its own.");
        Check(ReconnectTracker.InstanceIdFromPath(@"\\?\USB#VID_0451&PID_8442#MSFT20E30108613F47#{f18a0e88-c30c-11d0-8815-00a0c906bed8}") == @"USB\VID_0451&PID_8442\MSFT20E30108613F47", "Hub interface paths name their instance too.");
    }
}

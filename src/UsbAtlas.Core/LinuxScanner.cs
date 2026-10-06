using System.Globalization;
using System.Text.RegularExpressions;

namespace UsbAtlas;

// The files the Linux scanner reads, so self-tests can hand it a fixture tree on any machine. Paths are
// relative to /sys.
internal interface ISysfs
{
    string? Read(string path);
    byte[]? Bytes(string path);
    // A symbolic link's target as written, such as ../../usb2/2-0:1.0/usb2-port1.
    string? Link(string path);
    IEnumerable<string> List(string path);
}

internal sealed class Sysfs(string root = "/sys") : ISysfs
{
    private string Full(string path) => Path.Combine(root, path);
    public string? Read(string path) { try { return File.Exists(Full(path)) ? File.ReadAllText(Full(path)).Trim() : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
    public byte[]? Bytes(string path) { try { return File.Exists(Full(path)) ? File.ReadAllBytes(Full(path)) : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
    public string? Link(string path) { try { return new FileInfo(Full(path)).LinkTarget; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
    public IEnumerable<string> List(string path) { try { return Directory.Exists(Full(path)) ? Directory.EnumerateFileSystemEntries(Full(path)).Select(Path.GetFileName).OfType<string>().ToList() : []; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; } }
}

// A sysfs tree in memory: files, binary files and links by path.
internal sealed class FakeSysfs : ISysfs
{
    internal readonly Dictionary<string, string> Files = [];
    internal readonly Dictionary<string, byte[]> Binary = [];
    internal readonly Dictionary<string, string> Links = [];
    public string? Read(string path) => Files.TryGetValue(path, out var text) ? text.Trim() : null;
    public byte[]? Bytes(string path) => Binary.GetValueOrDefault(path);
    public string? Link(string path) => Links.GetValueOrDefault(path);
    public IEnumerable<string> List(string path) => Files.Keys.Concat(Binary.Keys).Concat(Links.Keys)
        .Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).Select(k => k[(path.Length + 1)..].Split('/')[0]).Distinct().ToList();
}

// Scans USB on Linux through sysfs into the same snapshot the Windows scanner fills, so the same checks
// and explanations apply. Each USB bus is a host, as lsusb numbers them: an xHCI controller's USB 2 and
// USB 3 root hubs are two buses, sharing the controller's PCI device. Ports come from usbN-portM: whether
// they can be plugged into (connect_type), their other half (peer) and USB-C (connector). Devices come
// with their descriptors, interfaces and the endpoints of each interface's current setting.
internal sealed class LinuxUsbScanner(ISysfs sys)
{
    private const string Devices = "bus/usb/devices";
    public bool CaptureRaw { get; init; }
    public LinuxUsbScanner() : this(new Sysfs()) { }

    public Snapshot Scan()
    {
        var snapshot = new Snapshot();
        var names = sys.List(Devices).ToList();
        var buses = names.Select(n => Regex.Match(n, @"^usb(\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Order().ToList();
        foreach (int bus in buses)
        {
            string rootName = $"usb{bus}";
            var controller = new UsbNode { Id = $"linux/{rootName}", Kind = "Controller", InstanceId = rootName, PowerSource = "System supplied", Location = "Host",
                LocationEvidence = "Host controller: motherboard or expansion hardware. Physical mounting is not reported." };
            ReadPci(controller, sys.Link($"{Devices}/{rootName}") ?? "");
            string product = sys.Read($"{Devices}/{rootName}/product") ?? "USB host controller";
            string vendor = Topology.PciVendor(controller.PciId);
            controller.Name = $"{(vendor.Length > 0 ? vendor + " " : "")}{product} · bus {bus}";
            controller.Notes.Add($"Linux shows this controller's USB 2 and USB 3 ports as separate buses; this is bus {bus}{(controller.PciAddress.Length > 0 ? $", on PCI device {controller.PciAddress}" : "")}. Buses on one controller share its PCIe link and its endpoints.");
            var root = new UsbNode { Id = rootName, Name = "Root hub", Kind = "Root hub", HubSymbolicLink = rootName, InstanceId = rootName, PowerSource = "System supplied", Location = "Host",
                LocationEvidence = "Logical root ports belong to the host controller, not a separate external hub.", UsbVersion = Version(rootName) };
            controller.Children.Add(root);
            snapshot.Controllers.Add(controller);
            ReadPorts(rootName, root, 0);
        }
        if (buses.Count == 0) snapshot.Diagnostics.Add("No USB buses were found in /sys/bus/usb/devices.");
        HubRelationships.ResolveCompanions(snapshot);
        DeviceIdentity.ClassifySockets(snapshot);
        foreach (var node in snapshot.Nodes.Reverse().Where(n => n.Kind is "Controller" or "Root hub" or "Hub")) DeviceIdentity.SummarizeProtocols(node);
        HubRelationships.Analyze(snapshot);
        HubRelationships.NoteReducedSpeed(snapshot);
        PortMap.Analyze(snapshot);
        UsbBudgets.AnalyzePower(snapshot);
        return snapshot;
    }

    // The PCI device a root hub hangs off, from where its sysfs entry links: …/0000:00:14.0/usb1.
    private void ReadPci(UsbNode controller, string link)
    {
        var address = link.Split('/').LastOrDefault(s => Regex.IsMatch(s, @"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$", RegexOptions.IgnoreCase));
        if (address == null) return;
        string pci = $"bus/pci/devices/{address}";
        string Hex(string file) => (sys.Read($"{pci}/{file}") ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        controller.PciId = Hex("vendor") is { Length: 4 } v && Hex("device") is { Length: 4 } d ? $"{v}:{d}" : "";
        controller.PciSubsystem = Hex("subsystem_vendor") is { Length: 4 } sv && Hex("subsystem_device") is { Length: 4 } sd ? $"{sv}:{sd}" : "";
        controller.PciRevision = Hex("revision") is { Length: > 0 } r ? r.PadLeft(2, '0')[^2..] : "";
        // Windows writes bus:device.function; the PCI domain is almost always 0000.
        controller.PciAddress = address.StartsWith("0000:", StringComparison.Ordinal) ? address[5..] : address;
        controller.DriverService = Driver(pci) ?? "";
        controller.PcieGeneration = Generation(sys.Read($"{pci}/current_link_speed")); controller.PcieLanes = Lanes(sys.Read($"{pci}/current_link_width"));
        controller.PcieMaxGeneration = Generation(sys.Read($"{pci}/max_link_speed")); controller.PcieMaxLanes = Lanes(sys.Read($"{pci}/max_link_width"));
    }
    // "8.0 GT/s PCIe" is generation 3.
    internal static int? Generation(string? speed) => speed?.Split(' ')[0] switch { "2.5" => 1, "5.0" or "5" => 2, "8.0" or "8" => 3, "16.0" => 4, "32.0" => 5, "64.0" => 6, _ => null };
    private static int? Lanes(string? width) => int.TryParse(width, out var lanes) && lanes > 0 ? lanes : null;

    private string Version(string device) => sys.Read($"{Devices}/{device}/version") is string v && v.Length > 0 ? "USB " + v.Trim() : "Not reported";
    private string? Driver(string path) => sys.Link($"{path}/driver") is string target ? target.Split('/')[^1] : null;

    // A hub's ports, numbered 1 to maxchild: usbN-portM under a root hub's interface, or 1-2-portM under hub 1-2's.
    private void ReadPorts(string hubName, UsbNode hub, int depth)
    {
        bool isRoot = hubName.StartsWith("usb", StringComparison.Ordinal);
        int bus = isRoot ? int.Parse(hubName[3..], CultureInfo.InvariantCulture) : int.Parse(hubName.Split('-')[0], CultureInfo.InvariantCulture);
        string device = isRoot ? $"{bus}-0" : hubName;
        string? hubInterface = sys.List(Devices).Where(n => n.StartsWith(device + ":", StringComparison.Ordinal)).Order().FirstOrDefault();
        hub.PortCount = int.TryParse(sys.Read($"{Devices}/{hubName}/maxchild"), out var count) ? count : 0;
        if (depth > 12) { hub.ScanIncomplete = true; hub.Notes.Add("Repeated or excessively deep hub path; enumeration stopped."); return; }
        bool superSpeed = isRoot ? hub.UsbVersion.StartsWith("USB 3", StringComparison.Ordinal) : hub.LinkMbps >= 5000;
        bool usb1 = isRoot ? hub.UsbVersion.StartsWith("USB 1", StringComparison.Ordinal) : hub.LinkMbps <= 12;
        for (int port = 1; port <= hub.PortCount; port++)
        {
            string portDir = $"{Devices}/{hubInterface}/{hubName}-port{port}";
            string child = isRoot ? $"{bus}-{port}" : $"{hubName}.{port}";
            var node = new UsbNode { Id = $"{hub.Id}/{port}", Port = port, Protocols = superSpeed ? "USB 3.x" : usb1 ? "USB 1.x" : "USB 1.x / USB 2.0" };
            hub.Children.Add(node);
            node.PortIsUserConnectable = sys.Read($"{portDir}/connect_type") switch { "hotplug" => true, "hardwired" or "not used" => false, _ => null };
            if (sys.Link($"{portDir}/connector") != null) node.PortConnectorIsTypeC = true;
            // The other half of a USB 3 socket: ../../usb2/2-0:1.0/usb2-port1, or …/2-1:1.0/2-1-port3 on a hub.
            if (sys.Link($"{portDir}/peer")?.Split('/')[^1] is string peer && Regex.Match(peer, @"^(.+)-port(\d+)$") is { Success: true } m)
                (node.CompanionHubSymbolicLink, node.CompanionPortNumber) = (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            if (int.TryParse(sys.Read($"{portDir}/over_current_count"), out var overcurrent) && overcurrent > 0)
                node.Notes.Add($"Linux has counted {overcurrent} overcurrent event{(overcurrent == 1 ? "" : "s")} on this port since it started: something plugged in here drew too much power.");
            DeviceIdentity.AssignLocation(node, hub);
            if (sys.Read($"{Devices}/{child}/idVendor") == null) { node.Kind = "Empty port"; node.Name = "Available port " + port; node.Status = "Empty"; continue; }
            ReadDevice(child, node, depth);
        }
    }

    private void ReadDevice(string name, UsbNode node, int depth)
    {
        string d = $"{Devices}/{name}";
        string? Text(string file) => sys.Read($"{d}/{file}");
        int Hex(string file) => int.TryParse(Text(file), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
        node.Status = "Connected";
        node.InstanceId = name;
        node.Kind = Hex("bDeviceClass") == 9 ? "Hub" : "Device";
        node.VendorId = (Text("idVendor") ?? "").ToUpperInvariant();
        node.ProductId = (Text("idProduct") ?? "").ToUpperInvariant();
        node.DeviceRevision = UsbScanner.Bcd((ushort)Hex("bcdDevice"));
        node.UsbVersion = Version(name);
        ushort bcd = ushort.TryParse(Text("version")?.Replace(".", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) ? b : (ushort)0;
        (node.Speed, node.LinkMbps, node.LinkLanes) = Speed(Text("speed"), int.TryParse(Text("rx_lanes"), out var lanes) ? lanes : 1);
        // A USB 3 device on a USB 2 link supports more than it gets. Linux doesn't say which can do 10 Gb/s.
        node.SpeedLimited = bcd >= 0x0300 && node.LinkMbps <= 480;
        if (node.LinkMbps >= 10000) node.SuperSpeedPlusCapable = true;
        node.Manufacturer = Text("manufacturer") ?? ""; node.ReportedProduct = Text("product") ?? ""; node.Serial = Text("serial") ?? "";
        node.DeviceClass = UsbScanner.ClassName((byte)Hex("bDeviceClass"));
        node.MaxPowerMa = int.TryParse(Regex.Match(Text("bMaxPower") ?? "", @"\d+").Value, out var ma) ? ma : null;
        node.SelfPowerCapable = (Hex("bmAttributes") & 0x40) != 0;
        node.PowerSource = node.Kind == "Hub" ? (node.SelfPowerCapable == true ? "Self powered" : "Bus powered") : node.SelfPowerCapable == true ? "Self-powered capable" : "Bus powered";
        if (node.Kind == "Hub" && node.LinkMbps == 480) node.TransactionTranslators = Hex("bDeviceProtocol") switch { 1 => "Single", 2 => "Per port", _ => "Not reported" };
        int speedClass = node.LinkMbps switch { 1.5 => 0, 12 => 1, 480 => 2, _ => 3 };

        // descriptors holds the device descriptor and then every configuration; the active one is read.
        List<UsbBudgets.Endpoint> endpoints = [];
        if (sys.Bytes($"{d}/descriptors") is { Length: >= 18 } raw)
        {
            int active = int.TryParse(Text("bConfigurationValue"), out var value) ? value : 1;
            for (int at = 18; at + 9 <= raw.Length;)
            {
                int total = BitConverter.ToUInt16(raw, at + 2);
                if (raw[at + 1] != 2 || total < 9 || at + total > raw.Length) break;
                if (raw[at + 5] == active)
                {
                    var config = raw[at..(at + total)];
                    node.InterfaceFunctions = DeviceIdentity.ReadInterfaceFunctions(config);
                    endpoints = UsbBudgets.ReadEndpoints(config);
                    if (CaptureRaw) node.Raw = new RawDescriptors { Device = Convert.ToHexString(raw, 0, 18), Configuration = Convert.ToHexString(config), SpeedCode = speedClass };
                }
                at += total;
            }
        }
        // Each interface's driver, and the endpoints of the setting it's in; a bound interface's periodic
        // endpoints are scheduled, which is what Windows calls open pipes.
        var drivers = new List<string>();
        var pipes = new List<(byte, byte, ushort, byte)>();
        // Interface descriptors as each interface's directory gives them, for when descriptors can't be read.
        var interfaces = new List<byte>();
        bool anyBound = false, standard = false;
        foreach (var iface in sys.List(Devices).Where(n => n.StartsWith(name + ":", StringComparison.Ordinal)).Order())
        {
            string i = $"{Devices}/{iface}";
            byte Field(string file, byte fallback) => byte.TryParse(sys.Read($"{i}/{file}"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            byte cls = Field("bInterfaceClass", 0xFF);
            interfaces.AddRange([9, 4, 0, 0, 0, cls, Field("bInterfaceSubClass", 0), Field("bInterfaceProtocol", 0), 0]);
            standard |= cls is 1 or 2 or 3 or 7 or 8 or 9 or 10 or 14 or 0xE0;
            if (Driver(i) is not string driver) continue;
            anyBound = true;
            if (!drivers.Contains(driver)) drivers.Add(driver);
            foreach (var ep in sys.List(i).Where(e => Regex.IsMatch(e, "^ep_[0-9a-f]{2}$") && e != "ep_00"))
            {
                byte H(string file) => byte.TryParse(sys.Read($"{i}/{ep}/{file}"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? x : (byte)0;
                ushort packet = ushort.TryParse(sys.Read($"{i}/{ep}/wMaxPacketSize"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) ? p : (ushort)0;
                pipes.Add((H("bEndpointAddress"), H("bmAttributes"), packet, H("bInterval")));
            }
        }
        if (anyBound) UsbScanner.ApplyOpenPipes(pipes, speedClass, endpoints, node);
        if (node.InterfaceFunctions.Count == 0 && interfaces.Count > 0)
            node.InterfaceFunctions = DeviceIdentity.ReadInterfaceFunctions([9, 2, .. BitConverter.GetBytes((ushort)(9 + interfaces.Count)), 0, 1, 0, 0x80, 0, .. interfaces]);
        node.DriverService = string.Join(", ", drivers);
        // A device Linux was told not to use, or one with standard functions and no driver for any of them.
        if (Text("authorized") == "0") node.KernelProblem = LinuxProblems.NotAuthorized;
        else if (!anyBound && standard && node.Kind == "Device") node.KernelProblem = LinuxProblems.NoDriver;

        DeviceIdentity.ResolveName(node);
        DeviceIdentity.Identify(node);
        if (node.Kind == "Hub")
        {
            node.HubSymbolicLink = name;
            node.Notes.Add("Downstream devices share this hub's upstream link. Link speed is a signaling ceiling, not available payload throughput.");
            ReadPorts(name, node, depth + 1);
        }
    }

    // speed is in Mb/s: 1.5, 12, 480, 5000, 10000 or 20000; a 20 Gb/s link may be two lanes of 10.
    internal static (string Speed, double? Mbps, int? Lanes) Speed(string? text, int lanes) => text switch
    {
        "1.5" => ("Low speed · 1.5 Mb/s", 1.5, null), "12" => ("Full speed · 12 Mb/s", 12, null), "480" => ("High speed · 480 Mb/s", 480, null),
        "5000" => ("SuperSpeed · 5 Gb/s", 5000, null),
        _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) && mbps >= 10000 =>
            ($"SuperSpeedPlus · {mbps / 1000:0.##} Gb/s{(lanes > 1 ? $" · {lanes} lanes" : "")}", mbps, lanes),
        _ => ("Not reported", null, null)
    };
}

// What keeps Linux from using a device, as issues with their explanations.
internal static class LinuxProblems
{
    internal const string NoDriver = "No driver bound";
    internal const string NotAuthorized = "Not authorized";
    // Not authorized is a choice someone made, as a disabled device is in Device Manager.
    internal static Severity? SeverityOf(UsbNode n) => n.KernelProblem switch { NoDriver => Severity.Warning, NotAuthorized => Severity.Note, _ => null };
    internal static Explanations.Explanation Explain(UsbNode n) => n.KernelProblem == NotAuthorized
        ? new("Linux has been told not to use this device (its authorized setting is 0), so no driver uses it.", "Yes, on purpose: it does nothing until it's authorized.",
            "USBGuard or a similar policy usually does this to devices it doesn't know.",
            [$"If you trust it, allow it in your USB policy, such as usbguard allow-device, or as root: echo 1 > /sys/bus/usb/devices/{n.InstanceId}/authorized"])
        : new("This device has standard functions, such as input, storage or audio, but no Linux driver has claimed any of them.", "Yes: Linux isn't using it.", "",
            ["Unplug it and plug it back in; journalctl -k shows what the kernel said when it connected.",
             "Check that the kernel module for it is loaded (lsmod), or install the package that provides its driver.",
             "If an application talks to it directly, as some tools do through libusb, nothing is wrong."]);
}

// This computer's USB, from whichever scanner its operating system has.
internal static class Scanner
{
    internal static Snapshot ThisComputer(bool raw = false) =>
        OperatingSystem.IsWindows() ? new UsbScanner { CaptureRaw = raw }.Scan()
        : OperatingSystem.IsLinux() ? new LinuxUsbScanner { CaptureRaw = raw }.Scan()
        : throw new PlatformNotSupportedException("USB Atlas scans USB on Windows and Linux.");
}

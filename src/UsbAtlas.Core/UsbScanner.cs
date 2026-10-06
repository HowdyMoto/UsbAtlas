using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbAtlas;

// Read-only Windows USB queries. Layouts follow the Windows SDK's packed usbioctl.h.
public sealed class UsbScanner
{
    private static readonly Guid ControllerGuid = new("3ABF6F2D-71C4-462A-8A92-1E6861E6AF27");
    private readonly Dictionary<string, (string Name, string Manufacturer, string InstanceId)> names = new(StringComparer.OrdinalIgnoreCase);
    // Every present devnode by instance ID: its parent, its driver service and, for a HID collection, its
    // usages; its name, driver key and any Device Manager problem code.
    internal sealed record DevNode(string Parent, string Service, List<string> Usages, string Name = "", string DriverKey = "", int Problem = 0, string ContainerId = "");
    private readonly Dictionary<string, DevNode> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> hidUsages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
    private Snapshot snapshot = new();
    // Keep descriptor bytes on each node for deep diagnostics. Off for the app, which doesn't show them.
    public bool CaptureRaw { get; init; }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public Snapshot Scan()
    {
        snapshot = new(); visited.Clear(); names.Clear(); devices.Clear(); hidUsages.Clear();
        ReadDevices();
        snapshot.Usb4HostRouters = [.. devices.Where(d => UsbC.IsHostRouter(d.Value.Service)).Select(d => d.Value.Name.Length > 0 ? d.Value.Name : "USB4 host router")];
        snapshot.Usb4Devices = [.. devices.Where(d => UsbC.IsUsb4Device(d.Key)).Select(d => d.Value.Name.Length > 0 ? d.Value.Name : "USB4 device")];
        snapshot.UsbCConnectorManager = devices.Values.Any(d => UsbC.IsConnectorManager(d.Service));
        var guid = ControllerGuid;
        var set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface))
                {
                    if (Marshal.GetLastWin32Error() != 259) snapshot.Diagnostics.Add("Controller enumeration: " + Error());
                    break;
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size < 8) { snapshot.Diagnostics.Add("Controller interface details unavailable: " + Error()); continue; }
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    var dev = new Native.DeviceData { Size = Marshal.SizeOf<Native.DeviceData>() };
                    if (!Native.SetupDiGetDeviceInterfaceDetailData(set, ref iface, detail, size, out _, ref dev)) { snapshot.Diagnostics.Add("Controller interface details unavailable: " + Error()); continue; }
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    var controller = new UsbNode { Id = path, Kind = "Controller", Name = Property(set, ref dev, 12) ?? Property(set, ref dev, 0) ?? "USB host controller", PowerSource = "System supplied" };
                    var instance = new StringBuilder(512);
                    if (Native.SetupDiGetDeviceInstanceId(set, ref dev, instance, instance.Capacity, out _)) controller.InstanceId = instance.ToString();
                    (controller.PciId, controller.PciSubsystem, controller.PciRevision) = PciIdentity(controller.InstanceId);
                    // SPDRP_BUSNUMBER and SPDRP_ADDRESS; a PCI address is its device number over its function number.
                    if (controller.PciId.Length > 0 && DwordProperty(set, ref dev, 21) is uint bus && DwordProperty(set, ref dev, 28) is uint address)
                        controller.PciAddress = $"{bus:X2}:{address >> 16:X2}.{address & 0xFFFF:X}";
                    // DEVPKEY_PciDevice_CurrentLinkSpeed and Width, MaxLinkSpeed and Width (pciprop.h): the PCIe link
                    // everything on this controller shares to reach the computer.
                    (controller.PcieGeneration, controller.PcieLanes) = (PciProperty(set, ref dev, 9), PciProperty(set, ref dev, 10));
                    (controller.PcieMaxGeneration, controller.PcieMaxLanes) = (PciProperty(set, ref dev, 11), PciProperty(set, ref dev, 12));
                    controller.Location = "Host";
                    controller.LocationEvidence = "Host controller: motherboard or expansion hardware. Physical mounting is not reported.";
                    // DEVPKEY_PciDevice_IsTunneledDevice: reached over USB4 or Thunderbolt, as a dock's controller is.
                    controller.PcieTunneled = PciBool(set, ref dev, 47);
                    UsbC.MarkTunneled(controller);
                    controller.Notes.Add("Controller ports may use separate USB 2 and USB 3 buses. Their link rates are not a single controller-wide bandwidth budget.");
                    snapshot.Controllers.Add(controller);
                    using var handle = Open(path);
                    if (handle.IsInvalid) { controller.ScanIncomplete = true; controller.Notes.Add("Cannot open controller: " + Error()); continue; }
                    var rootName = QueryName(handle, 258, 0, 4);
                    if (rootName.Length == 0) { controller.ScanIncomplete = true; controller.Notes.Add("Root hub unavailable: " + Error()); continue; }
                    var root = new UsbNode { Id = path + "/root", Name = "Root hub", Kind = "Root hub", PowerSource = "System supplied", InstanceId = ReconnectTracker.InstanceIdFromPath(rootName) ?? "" };
                    controller.Children.Add(root);
                    root.Location = "Host";
                    root.LocationEvidence = "Logical root ports belong to the host controller, not a separate external hub.";
                    ReadHub(rootName, root, 0);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        HubRelationships.ResolveCompanions(snapshot);
        DeviceIdentity.ClassifySockets(snapshot);
        foreach (var node in snapshot.Nodes.Reverse().Where(n => n.Kind is "Controller" or "Root hub" or "Hub")) DeviceIdentity.SummarizeProtocols(node);
        HubRelationships.Analyze(snapshot);
        HubRelationships.NoteReducedSpeed(snapshot);
        foreach (var node in snapshot.Nodes.Where(n => n.Kind is "Device" or "Hub" && n.InstanceId.Length > 0))
            node.ContainerId = devices.GetValueOrDefault(node.InstanceId)?.ContainerId ?? "";
        snapshot.Containers = [.. Containers.Read(snapshot.Nodes.Select(n => n.ContainerId)).Values];
        Containers.Analyze(snapshot);
        Interference.Analyze(snapshot);
        PortMap.Analyze(snapshot);
        HubDepth.Analyze(snapshot);
        UsbBudgets.AnalyzePower(snapshot);
        Drivers.Apply(snapshot, devices);
        Uas.Apply(snapshot, devices);
        try
        {
            var (gpus, displays, billboards) = UsbAtlas.Displays.Read();
            snapshot.Gpus = gpus; snapshot.Displays = displays;
            UsbAtlas.Displays.Analyze(snapshot, billboards);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException) { snapshot.Diagnostics.Add("Displays and graphics adapters unavailable: " + ex.Message); }
        PowerSaving.Read(snapshot, devices);
        PowerSaving.Analyze(snapshot);
        if (snapshot.Controllers.Count == 0) snapshot.Diagnostics.Add("No USB host controllers were returned by Windows.");
        return snapshot;
    }

    private void ReadHub(string path, UsbNode hub, int depth)
    {
        if (depth > 12 || !visited.Add(path)) { hub.ScanIncomplete = true; hub.Notes.Add("Repeated or excessively deep hub path; enumeration stopped."); return; }
        hub.HubSymbolicLink = path;
        using var handle = Open(path);
        if (handle.IsInvalid) { hub.ScanIncomplete = true; hub.Notes.Add("Hub details unavailable: " + Error()); return; }
        var info = new byte[76];
        if (!Query(handle, 258, info, out var returned) || returned < 7) { hub.ScanIncomplete = true; hub.Notes.Add("Cannot read hub ports: " + Error()); return; }
        hub.PortCount = info[6];
        if (CaptureRaw) ReadHubDescriptor(handle, hub, info, returned);
        // USB_HUB_DESCRIPTOR is 71 bytes; HubIsBusPowered follows it.
        if (hub.Kind != "Root hub" && returned >= 76) hub.PowerSource = info[75] != 0 ? "Bus powered" : "Self powered";
        for (int port = 1; port <= hub.PortCount; port++)
        {
            var data = new byte[4096]; Put(data, 0, port);
            if (!Query(handle, 274, data, out returned) || returned < 35)
            {
                hub.Children.Add(new UsbNode { Id = hub.Id + "/" + port, Name = "Port " + port, Port = port, Kind = "Unavailable", Status = "Query failed", Notes = [Error()] });
                continue;
            }
            int returnedInfo = returned;
            var status = BitConverter.ToInt32(data, 31);
            var node = new UsbNode { Id = hub.Id + "/" + port, Port = port, Status = ConnectionStatus(status) };
            hub.Children.Add(node);
            var connector = new byte[4096]; Put(connector, 0, port);
            bool hasConnector = Query(handle, 278, connector, out var connectorReturned) && connectorReturned >= 16;
            if (hasConnector)
            {
                DeviceIdentity.ApplyPortProperties(node, BitConverter.ToUInt32(connector, 8));
                // A USB 3 socket is two logical ports, one on each bus; its companion is the other half.
                node.CompanionPortNumber = BitConverter.ToUInt16(connector, 14);
                if (connectorReturned > 16)
                    node.CompanionHubSymbolicLink = Encoding.Unicode.GetString(connector, 16, (Math.Min(connectorReturned, connector.Length) - 16) & ~1).Split('\0')[0];
                // A socket with several companions lists them by CompanionIndex, until one has no port number.
                for (ushort index = 1; node.PortHasMultipleCompanions == true && index < 8; index++)
                {
                    var more = new byte[4096]; Put(more, 0, port); BitConverter.GetBytes(index).CopyTo(more, 12);
                    if (!Query(handle, 278, more, out var moreReturned) || moreReturned <= 16 || BitConverter.ToUInt16(more, 14) == 0) break;
                    node.MoreCompanions.Add(new() { PortNumber = BitConverter.ToUInt16(more, 14), HubSymbolicLink = Encoding.Unicode.GetString(more, 16, (Math.Min(moreReturned, more.Length) - 16) & ~1).Split('\0')[0] });
                }
            }
            DeviceIdentity.AssignLocation(node, hub);
            var v2 = new byte[16]; Put(v2, 0, port); Put(v2, 4, 16); Put(v2, 8, 7);
            var hasV2 = Query(handle, 279, v2, out returned) && returned >= 16;
            var flags = hasV2 ? BitConverter.ToInt32(v2, 12) : 0;
            if (CaptureRaw)
            {
                var raw = node.Raw = new();
                if (hasV2) { raw.ConnectionFlags = flags; raw.Protocols = BitConverter.ToInt32(v2, 8); }
                if (hasConnector) raw.ConnectorProperties = BitConverter.ToUInt32(connector, 8);
                if (status is 1 or 4 or 5 && data[4] == 18 && data[5] == 1) { raw.Device = Convert.ToHexString(data, 4, 18); raw.SpeedCode = data[23]; }
            }
            if (hasV2)
            {
                var protocols = BitConverter.ToInt32(v2, 8);
                node.Protocols = string.Join(" / ", new[] { (protocols & 1) != 0 ? "USB 1.x" : null, (protocols & 2) != 0 ? "USB 2.0" : null, (protocols & 4) != 0 ? "USB 3.x" : null }.Where(x => x != null));
                if (node.Protocols.Length == 0) node.Protocols = "Not reported";
            }
            if (status == 0) { node.Kind = "Empty port"; node.Name = "Available port " + port; continue; }
            if (status is 4 or 5) { ReadPowerFault(handle, port, data, node); continue; }
            if (status != 1) { node.Kind = "Unavailable"; node.Name = "Port " + port + " · " + node.Status; continue; }
            // Windows reports a port connected as soon as something is there, before it has read its device
            // descriptor. Until then the fields are zeros, and a zero speed would read as low speed.
            if (!DescriptorRead(data)) { node.Kind = "Unavailable"; node.Status = "Enumerating"; node.Name = "Port " + port + " · Still connecting"; continue; }
            node.Kind = data[24] != 0 ? "Hub" : "Device";
            node.VendorId = BitConverter.ToUInt16(data, 12).ToString("X4");
            node.ProductId = BitConverter.ToUInt16(data, 14).ToString("X4");
            node.DeviceRevision = Bcd(BitConverter.ToUInt16(data, 16));
            var bcd = BitConverter.ToUInt16(data, 6);
            node.UsbVersion = "USB " + Bcd(bcd);
            (node.Speed, node.LinkMbps) = DecodeSpeed(data[23], flags);
            // A SuperSpeedPlus link's lane speed and lane count come from a query of their own, which
            // Windows refuses for slower links.
            if ((flags & 4) != 0)
            {
                var plus = new byte[24]; Put(plus, 0, port); Put(plus, 4, plus.Length);
                if (Query(handle, 289, plus, out var plusReturned) && plusReturned >= 24)
                {
                    if (CaptureRaw) node.Raw!.SuperSpeedPlus = Convert.ToHexString(plus, 8, 16);
                    if (DecodeSuperSpeedPlus(plus) is (var speed, var mbps, var lanes)) (node.Speed, node.LinkMbps, node.LinkLanes) = (speed, mbps, lanes);
                }
            }
            if (hasV2) node.SuperSpeedPlusCapable = (flags & 8) != 0;
            node.SpeedLimited = ((flags & 2) != 0 && (flags & 1) == 0) || ((flags & 8) != 0 && (flags & 4) == 0);
            // A device that supports high speed but linked at full speed answers a request for its device qualifier,
            // its high-speed details; a full-speed-only one must refuse it (USB 2.0, 9.6.2).
            if (node.LinkMbps == 12 && bcd >= 0x0200) node.HighSpeedCapable = HighSpeedQualifier(Descriptor(handle, port, 6, 0, 0, 10));
            if (node.Kind == "Hub" && node.LinkMbps == 480) node.TransactionTranslators = TransactionTranslators(data);
            node.DriverKey = QueryName(handle, 264, port, 8);
            node.DeviceClass = ClassName(data[8]);
            ushort language = 0x0409;
            var langs = Descriptor(handle, port, 3, 0, 0, 255);
            if (langs is { Length: >= 4 }) language = BitConverter.ToUInt16(langs, 2);
            node.Manufacturer = StringDescriptor(handle, port, data[18], language);
            node.Serial = StringDescriptor(handle, port, data[20], language);
            node.ReportedProduct = StringDescriptor(handle, port, data[19], language);
            if (names.TryGetValue(node.DriverKey, out var identity))
            {
                node.WindowsName = identity.Name; node.WindowsManufacturer = identity.Manufacturer; node.InstanceId = identity.InstanceId;
                node.HidUsages = hidUsages.GetValueOrDefault(node.InstanceId) ?? [];
            }
            DeviceIdentity.ResolveName(node);
            // Match the active configuration value; descriptor index is not configuration value.
            List<UsbBudgets.Endpoint> endpoints = [];
            if (data[22] != 0)
            {
                for (byte index = 0; index < data[21]; index++)
                {
                    var config = Descriptor(handle, port, 2, index, 0, 9);
                    if (config is not { Length: >= 9 } || config[1] != 2 || config[5] != data[22]) continue;
                    node.MaxPowerMa = DecodePower(config[8], bcd);
                    node.SelfPowerCapable = (config[7] & 0x40) != 0;
                    node.PowerSource = node.SelfPowerCapable == true ? "Self-powered capable" : "Bus powered";
                    var length = BitConverter.ToUInt16(config, 2);
                    if (length >= 9)
                    {
                        var fullConfig = Descriptor(handle, port, 2, index, 0, length);
                        if (fullConfig != null)
                        {
                            if (CaptureRaw) node.Raw!.Configuration = Convert.ToHexString(fullConfig);
                            node.InterfaceFunctions = DeviceIdentity.ReadInterfaceFunctions(fullConfig);
                            node.OffersUas = Uas.Offers(fullConfig);
                            endpoints = UsbBudgets.ReadEndpoints(fullConfig);
                        }
                    }
                    break;
                }
            }
            // The BOS descriptor lists USB 2.1+ and USB 3 capabilities, such as SuperSpeedPlus and LPM. A Billboard's
            // says how its USB-C alternate modes went, so it's read on every scan.
            bool billboard = data[8] == Billboard.Class || node.InterfaceFunctions.Contains("Billboard");
            if ((CaptureRaw || billboard) && bcd >= 0x0201 && Descriptor(handle, port, 15, 0, 0, 5) is { Length: >= 5 } bosHead && bosHead[1] == 15
                && Descriptor(handle, port, 15, 0, 0, Math.Max((ushort)5, BitConverter.ToUInt16(bosHead, 2))) is { } bos)
            {
                if (CaptureRaw) node.Raw!.Bos = Convert.ToHexString(bos);
                if (billboard && Billboard.Decode(bos) is { } decoded)
                {
                    node.Billboard = decoded.Info;
                    node.Billboard.AdditionalInfoUrl = StringDescriptor(handle, port, decoded.UrlString, language);
                    for (int i = 0; i < decoded.ModeStrings.Count; i++) node.Billboard.Modes[i].Description = StringDescriptor(handle, port, decoded.ModeStrings[i], language);
                }
            }
            ReadOpenPipes(data, returnedInfo, SpeedClass(data[23], flags), endpoints, node);
            DeviceIdentity.Identify(node);
            if (node.Kind == "Hub")
            {
                var name = QueryName(handle, 261, port, 8);
                if (name.Length > 0) ReadHub(name, node, depth + 1);
                else { node.ScanIncomplete = true; node.Notes.Add("Downstream hub path unavailable."); }
                node.Notes.Add("Downstream devices share this hub's upstream link. Link speed is a signaling ceiling, not available payload throughput.");
            }
        }
    }

    // The hub's own descriptor, in USB 3 format (0x2A) for a USB 3 hub, from IOCTL_USB_GET_HUB_INFORMATION_EX:
    // a packed USB_HUB_TYPE and HighestPortNumber, then the descriptor. When Windows doesn't answer, the
    // USB 2-format descriptor in USB_NODE_INFORMATION stands in, which Windows fills in itself for USB 3 hubs.
    private static void ReadHubDescriptor(SafeFileHandle handle, UsbNode hub, byte[] nodeInfo, int nodeReturned)
    {
        var raw = hub.Raw ??= new();
        var ex = new byte[128];
        if (Query(handle, 277, ex, out var returned) && HubDescriptorEx(ex, returned) is var (type, descriptor))
        {
            raw.Hub = Convert.ToHexString(descriptor); raw.HubSource = "hub"; raw.HubType = type;
        }
        else if (nodeInfo[4] >= 7 && nodeReturned >= 4 + nodeInfo[4])
        {
            raw.Hub = Convert.ToHexString(nodeInfo, 4, nodeInfo[4]); raw.HubSource = "windows";
        }
    }
    internal static (string Type, byte[] Descriptor)? HubDescriptorEx(byte[] ex, int returned)
    {
        if (returned < 8) return null;
        int length = ex[6];
        if (length < 7 || 6 + length > Math.Min(returned, ex.Length) || ex[7] is not (0x29 or 0x2A)) return null;
        string type = BitConverter.ToInt32(ex, 0) switch { 1 => "Root hub", 2 => "USB 2 hub", 3 => "USB 3 hub", var t => $"Type {t}" };
        return (type, ex[6..(6 + length)]);
    }

    internal static (string, double?) DecodeSpeed(byte speed, int flags)
    {
        if ((flags & 4) != 0) return ("SuperSpeedPlus · 10 Gb/s or higher", null);
        if ((flags & 1) != 0) return ("SuperSpeed · 5 Gb/s", 5000);
        return speed switch { 0 => ("Low speed · 1.5 Mb/s", 1.5), 1 => ("Full speed · 12 Mb/s", 12), 2 => ("High speed · 480 Mb/s", 480), 3 => ("SuperSpeed · 5 Gb/s", 5000), _ => ("Not reported", null) };
    }
    // USB_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION: the port index and length, then the RX sublink speed
    // and lane count and the TX pair. A sublink speed is a mantissa (bits 16-31) scaled by an exponent
    // (bits 4-5: bits, kilobits, megabits or gigabits per second) for one lane, and lane counts are one
    // less than the lanes. Null when Windows fills in nothing usable, which keeps "10 Gb/s or higher".
    internal static (string Speed, double Mbps, int Lanes)? DecodeSuperSpeedPlus(byte[] info)
    {
        if (info.Length < 24) return null;
        static (double Mbps, int Lanes) Sublink(byte[] b, int at)
        {
            uint speed = BitConverter.ToUInt32(b, at);
            int lanes = (int)Math.Min(BitConverter.ToUInt32(b, at + 4), 7) + 1;
            return ((speed >> 16) * Math.Pow(1000, (speed >> 4) & 3) / 1e6 * lanes, lanes);
        }
        var (rx, rxLanes) = Sublink(info, 8); var (tx, txLanes) = Sublink(info, 16);
        // A link is as fast as its slower direction; the two match on every host port made so far.
        double mbps = Math.Min(rx, tx); int lanes = Math.Min(rxLanes, txLanes);
        if (mbps < 5000 || mbps > 80000 || lanes > 4) return null;
        return ($"SuperSpeedPlus · {mbps / 1000:0.##} Gb/s{(lanes > 1 ? $" · {lanes} lanes" : "")}", mbps, lanes);
    }
    // PCI\VEN_1022&DEV_1128&SUBSYS_380C17AA&REV_00\… names the vendor and device, the subsystem's device
    // then vendor, and the revision. Each reads vendor:device, as lspci prints them.
    internal static (string Id, string Subsystem, string Revision) PciIdentity(string instanceId)
    {
        var m = System.Text.RegularExpressions.Regex.Match(instanceId, @"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{4})([0-9A-F]{4}))?(?:&REV_([0-9A-F]{2}))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return ("", "", "");
        return ($"{m.Groups[1].Value}:{m.Groups[2].Value}".ToUpperInvariant(), m.Groups[3].Success ? $"{m.Groups[4].Value}:{m.Groups[3].Value}".ToUpperInvariant() : "", m.Groups[5].Value.ToUpperInvariant());
    }
    // A high-speed hub's transaction translators carry its full- and low-speed devices. bDeviceProtocol follows
    // the 4-byte port index and the device descriptor's first six bytes: 1 is one TT shared by every port, 2 is
    // one per port. Windows' hub driver runs a hub that offers one per port that way.
    internal static string TransactionTranslators(byte[] connectionInfo) => connectionInfo[10] switch { 1 => "Single", 2 => "Per port", _ => "Not reported" };
    // The device descriptor follows the 4-byte port index: bLength 18, bDescriptorType 1 once Windows has read it.
    internal static bool DescriptorRead(byte[] connectionInfo) => connectionInfo[4] == 18 && connectionInfo[5] == 1;
    // A device qualifier is 10 bytes, type 6, naming a USB version of 2.0 or later.
    internal static bool HighSpeedQualifier(byte[]? qualifier) => qualifier is { Length: >= 10 } q && q[0] == 10 && q[1] == 6 && BitConverter.ToUInt16(q, 2) >= 0x0200;
    // DEVPKEY_Device_ContainerId, a DEVPROP_TYPE_GUID.
    private static string ContainerProperty(IntPtr set, ref Native.DeviceData d)
    {
        var key = new Native.PropertyKey { Category = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
        var bytes = new byte[16];
        return Native.SetupDiGetDeviceProperty(set, ref d, ref key, out uint type, bytes, (uint)bytes.Length, out _, 0) && type == 0x0D ? Containers.Normalize(new Guid(bytes)) : "";
    }
    private static readonly Guid PciDeviceProperties = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");
    private static int? PciProperty(IntPtr set, ref Native.DeviceData d, uint id)
    {
        var key = new Native.PropertyKey { Category = PciDeviceProperties, Id = id };
        var bytes = new byte[4];
        // DEVPROP_TYPE_UINT32.
        return Native.SetupDiGetDeviceProperty(set, ref d, ref key, out uint type, bytes, (uint)bytes.Length, out _, 0) && type == 7 && BitConverter.ToInt32(bytes) is > 0 and var value ? value : null;
    }
    private static bool? PciBool(IntPtr set, ref Native.DeviceData d, uint id)
    {
        var key = new Native.PropertyKey { Category = PciDeviceProperties, Id = id };
        var bytes = new byte[1];
        // DEVPROP_TYPE_BOOLEAN: one byte, 0 or 0xFF.
        return Native.SetupDiGetDeviceProperty(set, ref d, ref key, out uint type, bytes, (uint)bytes.Length, out _, 0) && type == 0x11 ? bytes[0] != 0 : null;
    }
    internal static int DecodePower(byte maxPower, ushort bcdUsb) => maxPower * (bcdUsb >= 0x0300 ? 8 : 2);
    // Binary-coded decimal as USB versions and revisions are written: 0x0210 is 2.10.
    internal static string Bcd(ushort value) => $"{value >> 8:X}.{(value >> 4) & 15:X}{value & 15:X}";
    private static int SpeedClass(byte speed, int flags) => (flags & 5) != 0 ? 3 : speed;

    // Open pipes follow NumberOfOpenPipes (offset 27) as packed USB_PIPE_INFO entries from offset 35:
    // a 7-byte endpoint descriptor and a 4-byte schedule offset. Only periodic pipes reserve bandwidth.
    internal static void ReadOpenPipes(byte[] data, int returned, int speedClass, List<UsbBudgets.Endpoint> endpoints, UsbNode node)
    {
        if (returned < 35 || speedClass > 3) return;
        int count = BitConverter.ToInt32(data, 27);
        if (count < 0 || 35 + count * 11 > Math.Min(returned, data.Length)) return;
        ApplyOpenPipes(Enumerable.Range(0, count).Select(i => 35 + i * 11).Select(at => (data[at + 2], data[at + 3], BitConverter.ToUInt16(data, at + 4), data[at + 6])).ToList(), speedClass, endpoints, node);
    }
    // What a device's open pipes reserve, how often it's polled, and each pipe described, from their endpoint
    // descriptors' address, attributes, packet size and interval.
    internal static void ApplyOpenPipes(List<(byte Address, byte Attributes, ushort MaxPacket, byte Interval)> pipes, int speedClass, List<UsbBudgets.Endpoint> endpoints, UsbNode node)
    {
        if (speedClass > 3) return;
        double reserved = 0;
        (byte Address, byte Interval)? fastest = null;
        foreach (var (address, attributes, maxPacket, interval) in pipes)
        {
            // SuperSpeed bytes per interval live in the companion descriptor of the matching alternate setting.
            int? perInterval = endpoints.FirstOrDefault(e => e.Address == address && e.Attributes == attributes && e.MaxPacket == maxPacket && e.Interval == interval)?.BytesPerInterval;
            reserved += UsbBudgets.PeriodicMbps(attributes, maxPacket, interval, speedClass, perInterval);
            node.OpenPipes.Add(UsbBudgets.DescribePipe(address, attributes, maxPacket, interval, speedClass, perInterval));
            // Input reaches the host through interrupt IN pipes; the fastest one sets how often it is polled.
            if ((attributes & 3) == 3 && (address & 0x80) != 0 && (fastest == null || UsbBudgets.PollIntervalMs(interval, speedClass) < UsbBudgets.PollIntervalMs(fastest.Value.Interval, speedClass)))
                fastest = (address, interval);
        }
        node.ReservedMbps = reserved;
        // A hub's interrupt pipe only reports port changes, so hubs have no polling rate to show.
        if (fastest is { } input && node.Kind != "Hub")
        {
            node.PollIntervalMs = UsbBudgets.PollIntervalMs(input.Interval, speedClass);
            node.Notes.Add(UsbBudgets.PollingNote(input.Address, input.Interval, speedClass));
        }
        if (endpoints.Count > 0) node.PeakReservedMbps = Math.Max(reserved, UsbBudgets.PeakPeriodicMbps(endpoints, speedClass));
    }

    // A port refused for power still holds the device's descriptor, so name the device and what it asked for.
    private void ReadPowerFault(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int port, byte[] data, UsbNode node)
    {
        node.Kind = "Unavailable"; node.Name = "Port " + port + " · " + node.Status;
        node.Notes.Add(UsbBudgets.FaultNote(node.Status));
        if (!DescriptorRead(data)) return;
        node.VendorId = BitConverter.ToUInt16(data, 12).ToString("X4");
        node.ProductId = BitConverter.ToUInt16(data, 14).ToString("X4");
        node.DeviceRevision = Bcd(BitConverter.ToUInt16(data, 16));
        var bcd = BitConverter.ToUInt16(data, 6);
        node.UsbVersion = "USB " + Bcd(bcd);
        node.DeviceClass = ClassName(data[8]);
        ushort language = 0x0409;
        var langs = Descriptor(handle, port, 3, 0, 0, 255);
        if (langs is { Length: >= 4 }) language = BitConverter.ToUInt16(langs, 2);
        node.Manufacturer = StringDescriptor(handle, port, data[18], language);
        node.ReportedProduct = StringDescriptor(handle, port, data[19], language);
        node.Kind = "Device"; DeviceIdentity.ResolveName(node); node.Kind = "Unavailable";
        node.Name += " · " + node.Status;
        var config = Descriptor(handle, port, 2, 0, 0, 9);
        if (config is { Length: >= 9 } && config[1] == 2)
        {
            node.MaxPowerMa = DecodePower(config[8], bcd);
            node.SelfPowerCapable = (config[7] & 0x40) != 0;
            node.Notes.Add($"The device's first configuration requests up to {node.MaxPowerMa} mA.");
        }
    }
    private static string ConnectionStatus(int status) => status switch { 0 => "Empty", 1 => "Connected", 2 => "Enumeration failed", 3 => "General failure", 4 => "Overcurrent", 5 => "Insufficient power", 6 => "Insufficient bandwidth", 7 => "Hub nested too deeply", 8 => "Legacy hub", 9 => "Enumerating", 10 => "Resetting", _ => "Status " + status };
    internal static string ClassName(byte value) => value switch { 0 => "Defined by interfaces", 1 => "Audio", 2 => "Communications", 3 => "Human interface (HID)", 7 => "Printer", 8 => "Mass storage", 9 => "Hub", 14 => "Video", 0x11 => "Billboard", 0xE0 => "Wireless controller", 0xEF => "Composite / miscellaneous", 0xFF => "Vendor specific", _ => $"Class 0x{value:X2}" };

    // Names every present device by driver key, and records each devnode's parent and service so HID
    // collections and power settings can be traced back to the USB device they belong to.
    private void ReadDevices()
    {
        var set = Native.SetupDiGetClassDevsNoGuid(IntPtr.Zero, null, IntPtr.Zero, 6);
        if (set == new IntPtr(-1)) return;
        try
        {
            for (uint i = 0; ; i++)
            {
                var d = new Native.DeviceData { Size = Marshal.SizeOf<Native.DeviceData>() };
                if (!Native.SetupDiEnumDeviceInfo(set, i, ref d)) break;
                var key = Property(set, ref d, 9);
                var name = Property(set, ref d, 12) ?? Property(set, ref d, 0);
                var buffer = new StringBuilder(512);
                var instance = Native.SetupDiGetDeviceInstanceId(set, ref d, buffer, buffer.Capacity, out _) ? buffer.ToString() : "";
                if (key != null && name != null) names[key] = (name, Property(set, ref d, 11) ?? "", instance);
                if (instance.Length == 0) continue;
                buffer.Clear();
                var parent = Native.CM_Get_Parent(out var up, d.DevInst, 0) == 0 && Native.CM_Get_Device_ID(up, buffer, buffer.Capacity, 0) == 0 ? buffer.ToString() : "";
                var usages = instance.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) ? DeviceIdentity.ReadHidUsages(MultiProperty(set, ref d, 1)) : [];
                // DN_HAS_PROBLEM: Device Manager shows the problem code on the device's General tab.
                int problem = Native.CM_Get_DevNode_Status(out var devStatus, out var code, d.DevInst, 0) == 0 && (devStatus & 0x400) != 0 ? (int)code : 0;
                devices[instance] = new(parent, Property(set, ref d, 4) ?? "", usages, name ?? "", key ?? "", problem, ContainerProperty(set, ref d));
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        foreach (var (instance, node) in devices.Where(d => d.Value.Usages.Count > 0))
            if (UsbOwner(instance, devices) is string owner)
            {
                if (!hidUsages.TryGetValue(owner, out var list)) hidUsages[owner] = list = [];
                list.AddRange(node.Usages.Except(list).ToList());
            }
    }
    // The USB device a devnode belongs to: itself, or the USB device above it through a composite device's
    // interfaces (…&MI_00) and HID collections. Anything else in between, such as a Bluetooth link, means
    // the USB device above is an adapter, not this device.
    internal static string? UsbOwner(string instance, IReadOnlyDictionary<string, DevNode> devices)
    {
        string? at = instance;
        for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(at); depth++, at = devices.GetValueOrDefault(at)?.Parent)
        {
            if (IsUsbDevice(at)) return at;
            if (!at.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && !at.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return null;
    }
    internal static bool IsUsbDevice(string instance) => instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
        && instance.Split('\\') is [_, var hardware, ..] && !hardware.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
    private static string[] MultiProperty(IntPtr set, ref Native.DeviceData d, uint property)
    {
        var bytes = new byte[8192];
        return Native.SetupDiGetDeviceRegistryProperty(set, ref d, property, out _, bytes, (uint)bytes.Length, out var needed)
            ? Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(needed, (uint)bytes.Length) & ~1).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }
    private static uint? DwordProperty(IntPtr set, ref Native.DeviceData d, uint property)
    {
        var bytes = new byte[4];
        return Native.SetupDiGetDeviceRegistryProperty(set, ref d, property, out _, bytes, 4, out var needed) && needed == 4 ? BitConverter.ToUInt32(bytes) : null;
    }
    private static string? Property(IntPtr set, ref Native.DeviceData d, uint property)
    {
        var bytes = new byte[8192];
        return Native.SetupDiGetDeviceRegistryProperty(set, ref d, property, out _, bytes, (uint)bytes.Length, out _) ? Encoding.Unicode.GetString(bytes).Split('\0')[0] : null;
    }
    private static SafeFileHandle Open(string path) => Native.CreateFile(path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") ? path : @"\\.\" + path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
    private static bool Query(SafeFileHandle handle, int function, byte[] bytes, out int returned) => Native.DeviceIoControl(handle, (uint)((0x22 << 16) | (function << 2)), bytes, bytes.Length, bytes, bytes.Length, out returned, IntPtr.Zero);
    private static void Put(byte[] bytes, int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private static string Error() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
    private static string QueryName(SafeFileHandle handle, int function, int port, int offset)
    {
        var bytes = new byte[8192]; Put(bytes, 0, port);
        return Query(handle, function, bytes, out var returned) && returned > offset ? Encoding.Unicode.GetString(bytes, offset, (Math.Min(returned, bytes.Length) - offset) & ~1).TrimEnd('\0') : "";
    }
    private static byte[]? Descriptor(SafeFileHandle handle, int port, byte type, byte index, ushort language, ushort length)
    {
        var bytes = new byte[12 + length]; Put(bytes, 0, port);
        bytes[4] = 0x80; bytes[5] = 6; bytes[6] = index; bytes[7] = type;
        BitConverter.GetBytes(language).CopyTo(bytes, 8); BitConverter.GetBytes(length).CopyTo(bytes, 10);
        return Query(handle, 260, bytes, out var returned) && returned > 12 ? bytes[12..Math.Min(returned, bytes.Length)] : null;
    }
    private static string StringDescriptor(SafeFileHandle handle, int port, byte index, ushort language)
    {
        if (index == 0) return "";
        var bytes = Descriptor(handle, port, 3, index, language, 255);
        if (bytes is not { Length: >= 2 } || bytes[1] != 3 || bytes[0] < 2) return "";
        return Encoding.Unicode.GetString(bytes, 2, (Math.Min(bytes[0], bytes.Length) - 2) & ~1).TrimEnd('\0');
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid Guid; public int Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceData { public int Size; public Guid Guid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Category; public uint Id; }
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] internal static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DeviceData dev, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint needed, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevsNoGuid(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr dev);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetailData(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint needed, ref DeviceData dev);
    [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceData dev);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceData dev, uint property, out uint type, byte[] buffer, uint size, out uint needed);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceData dev, StringBuilder id, int size, out int needed);
    [DllImport("cfgmgr32.dll")] internal static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);
    [DllImport("cfgmgr32.dll")] internal static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)] internal static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer, int length, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

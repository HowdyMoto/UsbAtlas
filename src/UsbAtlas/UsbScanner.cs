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
    // Every present devnode by instance ID: its parent, its driver service and, for a HID collection, its usages.
    internal sealed record DevNode(string Parent, string Service, List<string> Usages);
    private readonly Dictionary<string, DevNode> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> hidUsages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
    private Snapshot snapshot = new();

    public Snapshot Scan()
    {
        snapshot = new(); visited.Clear(); names.Clear(); devices.Clear(); hidUsages.Clear();
        ReadDevices();
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
                    controller.Location = "Host";
                    controller.LocationEvidence = "Host controller: motherboard or expansion hardware. Physical mounting is not reported.";
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
        UsbBudgets.AnalyzePower(snapshot);
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
            if (Query(handle, 278, connector, out var connectorReturned) && connectorReturned >= 16)
            {
                DeviceIdentity.ApplyPortProperties(node, BitConverter.ToUInt32(connector, 8));
                // A USB 3 socket is two logical ports, one on each bus; its companion is the other half.
                node.CompanionPortNumber = BitConverter.ToUInt16(connector, 14);
                if (connectorReturned > 16)
                    node.CompanionHubSymbolicLink = Encoding.Unicode.GetString(connector, 16, (Math.Min(connectorReturned, connector.Length) - 16) & ~1).Split('\0')[0];
            }
            DeviceIdentity.AssignLocation(node, hub);
            var v2 = new byte[16]; Put(v2, 0, port); Put(v2, 4, 16); Put(v2, 8, 7);
            var hasV2 = Query(handle, 279, v2, out returned) && returned >= 16;
            var flags = hasV2 ? BitConverter.ToInt32(v2, 12) : 0;
            if (hasV2)
            {
                var protocols = BitConverter.ToInt32(v2, 8);
                node.Protocols = string.Join(" / ", new[] { (protocols & 1) != 0 ? "USB 1.x" : null, (protocols & 2) != 0 ? "USB 2.0" : null, (protocols & 4) != 0 ? "USB 3.x" : null }.Where(x => x != null));
                if (node.Protocols.Length == 0) node.Protocols = "Not reported";
            }
            if (status == 0) { node.Kind = "Empty port"; node.Name = "Available port " + port; continue; }
            if (status is 4 or 5) { ReadPowerFault(handle, port, data, node); continue; }
            if (status != 1) { node.Kind = "Unavailable"; node.Name = "Port " + port + " · " + node.Status; continue; }
            node.Kind = data[24] != 0 ? "Hub" : "Device";
            node.VendorId = BitConverter.ToUInt16(data, 12).ToString("X4");
            node.ProductId = BitConverter.ToUInt16(data, 14).ToString("X4");
            var bcd = BitConverter.ToUInt16(data, 6);
            node.UsbVersion = $"USB {bcd >> 8:X}.{(bcd >> 4) & 15:X}{bcd & 15:X}";
            (node.Speed, node.LinkMbps) = DecodeSpeed(data[23], flags);
            if (hasV2) node.SuperSpeedPlusCapable = (flags & 8) != 0;
            node.SpeedLimited = ((flags & 2) != 0 && (flags & 1) == 0) || ((flags & 8) != 0 && (flags & 4) == 0);
            if (node.SpeedLimited) node.Notes.Add("This device reports support for a faster USB link than its current connection. Check the upstream port, hub and cable.");
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
                            node.InterfaceFunctions = DeviceIdentity.ReadInterfaceFunctions(fullConfig);
                            endpoints = UsbBudgets.ReadEndpoints(fullConfig);
                        }
                    }
                    break;
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

    internal static (string, double?) DecodeSpeed(byte speed, int flags)
    {
        if ((flags & 4) != 0) return ("SuperSpeedPlus · 10 Gb/s or higher", null);
        if ((flags & 1) != 0) return ("SuperSpeed · 5 Gb/s", 5000);
        return speed switch { 0 => ("Low speed · 1.5 Mb/s", 1.5), 1 => ("Full speed · 12 Mb/s", 12), 2 => ("High speed · 480 Mb/s", 480), 3 => ("SuperSpeed · 5 Gb/s", 5000), _ => ("Not reported", null) };
    }
    internal static int DecodePower(byte maxPower, ushort bcdUsb) => maxPower * (bcdUsb >= 0x0300 ? 8 : 2);
    private static int SpeedClass(byte speed, int flags) => (flags & 5) != 0 ? 3 : speed;

    // Open pipes follow NumberOfOpenPipes (offset 27) as packed USB_PIPE_INFO entries from offset 35:
    // a 7-byte endpoint descriptor and a 4-byte schedule offset. Only periodic pipes reserve bandwidth.
    internal static void ReadOpenPipes(byte[] data, int returned, int speedClass, List<UsbBudgets.Endpoint> endpoints, UsbNode node)
    {
        if (returned < 35 || speedClass > 3) return;
        int count = BitConverter.ToInt32(data, 27);
        if (count < 0 || 35 + count * 11 > Math.Min(returned, data.Length)) return;
        double reserved = 0;
        (byte Address, byte Interval)? fastest = null;
        for (int i = 0; i < count; i++)
        {
            int at = 35 + i * 11;
            byte address = data[at + 2], attributes = data[at + 3], interval = data[at + 6];
            ushort maxPacket = BitConverter.ToUInt16(data, at + 4);
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
        if (data[4] != 18 || data[5] != 1) return;
        node.VendorId = BitConverter.ToUInt16(data, 12).ToString("X4");
        node.ProductId = BitConverter.ToUInt16(data, 14).ToString("X4");
        var bcd = BitConverter.ToUInt16(data, 6);
        node.UsbVersion = $"USB {bcd >> 8:X}.{(bcd >> 4) & 15:X}{bcd & 15:X}";
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
    private static string ClassName(byte value) => value switch { 0 => "Defined by interfaces", 1 => "Audio", 2 => "Communications", 3 => "Human interface (HID)", 7 => "Printer", 8 => "Mass storage", 9 => "Hub", 14 => "Video", 0xE0 => "Wireless controller", 0xEF => "Composite / miscellaneous", 0xFF => "Vendor specific", _ => $"Class 0x{value:X2}" };

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
                devices[instance] = new(parent, Property(set, ref d, 4) ?? "", usages);
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
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)] internal static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer, int length, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

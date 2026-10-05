namespace UsbAtlas;

public sealed class UsbNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReportedProduct { get; set; } = "";
    public string WindowsName { get; set; } = "";
    public string WindowsManufacturer { get; set; } = "";
    public string LookupVendor { get; set; } = "";
    public string LookupProduct { get; set; } = "";
    public string NameSource { get; set; } = "Reported name";
    public bool SnapToParentHub { get; set; }
    public string PortLabel { get; set; } = "";
    public string UserLabel { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => UserLabel.Length > 0 ? UserLabel : Name;
    public string Kind { get; set; } = "Device";
    public int Port { get; set; }
    public int PortCount { get; set; }
    public string Status { get; set; } = "Connected";
    public string UsbVersion { get; set; } = "Not reported";
    public string Speed { get; set; } = "Not reported";
    public double? LinkMbps { get; set; }
    // A SuperSpeedPlus link's lanes, when Windows reports them: 2 for USB 3.2's two-lane modes.
    public int? LinkLanes { get; set; }
    public string Protocols { get; set; } = "Not reported";
    public string DownstreamProtocols { get; set; } = "Not reported";
    public bool ProtocolSummaryPartial { get; set; }
    public string PowerSource { get; set; } = "Not reported";
    public int? MaxPowerMa { get; set; }
    // The active configuration's self-powered attribute; a hub's actual power source is PowerSource.
    public bool? SelfPowerCapable { get; set; }
    public List<string> PowerWarnings { get; set; } = [];
    // Periodic bandwidth reserved by open pipes now, and the most the active configuration can reserve.
    public double? ReservedMbps { get; set; }
    public double? PeakReservedMbps { get; set; }
    public List<string> OpenPipes { get; set; } = [];
    // How often the host polls the fastest open interrupt input pipe, from its endpoint descriptor.
    public double? PollIntervalMs { get; set; }
    // Top-level HID collections Windows lists for the device, such as Joystick or Keyboard.
    public List<string> HidUsages { get; set; } = [];
    // Device Manager's "Allow the computer to turn off this device to save power": On, Off, Unused by
    // driver (on, but its HID driver doesn't use selective suspend), Not offered or Not reported.
    public string PowerSaving { get; set; } = "Not reported";
    public string InstanceId { get; set; } = "";
    public string HubSymbolicLink { get; set; } = "";
    public string CompanionHubSymbolicLink { get; set; } = "";
    public int CompanionPortNumber { get; set; }
    // A port Windows says has several companions lists the rest here; the first is the one above.
    public List<PortCompanion> MoreCompanions { get; set; } = [];
    public string CompanionHubId { get; set; } = "";
    public bool IsUsb2Companion { get; set; }
    // A USB 3 hub's USB 2 side whose USB 3 side didn't connect: the USB 3 half of its socket is empty, or
    // shows an error when the USB 3 side tried to connect and failed (Usb3SideFailed).
    public bool Usb3SideMissing { get; set; }
    public bool Usb3SideFailed { get; set; }
    public int QuickReconnects { get; set; }
    public List<DateTime> QuickReconnectTimes { get; set; } = [];
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Serial { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string DriverKey { get; set; } = "";
    // A host controller's PCI identity: vendor:device and subsystem vendor:device from its instance ID,
    // its revision, and where it sits as bus:device.function. Empty for a controller that isn't on PCI.
    public string PciId { get; set; } = "";
    public string PciSubsystem { get; set; } = "";
    public string PciRevision { get; set; } = "";
    public string PciAddress { get; set; } = "";
    // The driver Windows loaded for the device itself, from its driver key: service, package version and
    // date, provider and INF. Empty when Windows has none recorded.
    public string DriverService { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string DriverDate { get; set; } = "";
    public string DriverProvider { get; set; } = "";
    public string DriverInf { get; set; } = "";
    // Device Manager problem codes on the device or one of its functions (interfaces, HID collections).
    public List<DeviceProblem> DriverProblems { get; set; } = [];
    // Descriptors as read, only when a scan is asked to keep them.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RawDescriptors? Raw { get; set; }
    public bool SpeedLimited { get; set; }
    // A high-speed hub's transaction translators, from its bDeviceProtocol: Single (one for all ports),
    // Per port, Not reported, or None for devices and hubs that aren't running at high speed.
    public string TransactionTranslators { get; set; } = "None";
    public bool ScanIncomplete { get; set; }
    public bool? PortIsUserConnectable { get; set; }
    public bool? PortConnectorIsTypeC { get; set; }
    // The port can serve as the host's debug port, as most xHCI USB 3 ports can.
    public bool? PortIsDebugCapable { get; set; }
    public bool? PortHasMultipleCompanions { get; set; }
    // Where the firmware's description of this port contradicts itself (PortMap). The port still works.
    public List<string> PortMapWarnings { get; set; } = [];
    // The other logical port of the same physical socket (USB 2 and USB 3 halves), when Windows names it.
    public string CompanionId { get; set; } = "";
    // The upstream socket as drawn: USB-A, USB-C, Internal or Not reported, and the fastest rate it is
    // known to carry: USB 2.0, 5 Gb/s, ≥5 Gb/s, ≥10 Gb/s or Not reported.
    public string Connector { get; set; } = "Not reported";
    public string SocketSpeed { get; set; } = "Not reported";
    public string SocketEvidence { get; set; } = "";
    public bool? SuperSpeedPlusCapable { get; set; }
    public string Location { get; set; } = "Unknown";
    public string LocationEvidence { get; set; } = "Physical placement is not reported.";
    public string DeviceType { get; set; } = "USB device";
    public string TypeEvidence { get; set; } = "No specific device function identified.";
    public List<string> InterfaceFunctions { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<UsbNode> Children { get; set; } = [];
    public IEnumerable<UsbNode> Walk() { yield return this; foreach (var c in Children) foreach (var n in c.Walk()) yield return n; }
}
// Another logical port of the same physical socket, named by its hub's link and port number; Id is the
// port when it is in the scan.
public sealed class PortCompanion
{
    public string HubSymbolicLink { get; set; } = "";
    public int PortNumber { get; set; }
    public string Id { get; set; } = "";
}
// A devnode Windows reports a problem on: Device Manager's "Code N" on its General tab.
public sealed class DeviceProblem
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Code { get; set; }
    public string Meaning { get; set; } = "";
}
// Descriptor bytes as hex, for deep diagnostics, read through the port a device is plugged into. The hub
// descriptor is the one Windows reports for the hub, in USB 2.0 format even for root and USB 3 hubs. ConnectionFlags and Protocols are USB_NODE_CONNECTION_INFORMATION_EX_V2's.
public sealed class RawDescriptors
{
    public string Device { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string Bos { get; set; } = "";
    public string Hub { get; set; } = "";
    // USB_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION after its port index and length: the RX speed and
    // lane count, then the TX speed and lane count, for a port linked at SuperSpeed or faster.
    public string SuperSpeedPlus { get; set; } = "";
    public int? ConnectionFlags { get; set; }
    public int? Protocols { get; set; }
    public uint? ConnectorProperties { get; set; }
    public int? SpeedCode { get; set; }
}
public sealed class Snapshot
{
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public bool IsDemo { get; set; }
    public List<UsbNode> Controllers { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    // The active power plan's USB selective suspend setting, plugged in and on battery, and which applies now.
    public bool? UsbSuspendPluggedIn { get; set; }
    public bool? UsbSuspendOnBattery { get; set; }
    public bool? OnBattery { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? UsbSuspendActive => OnBattery == true ? UsbSuspendOnBattery : UsbSuspendPluggedIn;
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<UsbNode> Nodes => Controllers.SelectMany(x => x.Walk());
}

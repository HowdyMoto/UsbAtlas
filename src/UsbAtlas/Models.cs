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
    public string CompanionHubId { get; set; } = "";
    public bool IsUsb2Companion { get; set; }
    public int QuickReconnects { get; set; }
    public List<DateTime> QuickReconnectTimes { get; set; } = [];
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Serial { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string DriverKey { get; set; } = "";
    public bool SpeedLimited { get; set; }
    public bool ScanIncomplete { get; set; }
    public bool? PortIsUserConnectable { get; set; }
    public bool? PortConnectorIsTypeC { get; set; }
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

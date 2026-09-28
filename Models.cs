namespace UsbAtlas;

public sealed class UsbNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Device";
    public int Port { get; set; }
    public int PortCount { get; set; }
    public string Status { get; set; } = "Connected";
    public string UsbVersion { get; set; } = "Not reported";
    public string Speed { get; set; } = "Not reported";
    public double? LinkMbps { get; set; }
    public string Protocols { get; set; } = "Not reported";
    public string PowerSource { get; set; } = "Not reported";
    public int? MaxPowerMa { get; set; }
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
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<UsbNode> Nodes => Controllers.SelectMany(x => x.Walk());
}

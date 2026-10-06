using System.Runtime.InteropServices;
using System.Text;

namespace UsbAtlas;

// A graphics adapter: a PCI display controller, and whether Windows has a driver running it.
public sealed class GpuInfo
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    // NVIDIA, AMD, Intel, or empty when the vendor isn't one of them.
    public string Vendor { get; set; } = "";
    public string Service { get; set; } = "";
    public int ProblemCode { get; set; }
    public bool HasDriver { get; set; }
}
// A monitor Windows has known, connected now or not, with the adapter it was last shown through.
public sealed class DisplayInfo
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    // Built-in panels belong to the computer's own container; external monitors have their own.
    public bool External { get; set; }
    public bool Present { get; set; }
    public string GpuInstanceId { get; set; } = "";
    public DateTime? LastArrival { get; set; }
    public DateTime? LastRemoval { get; set; }
}
// Why a USB-C display's USB is connected but it isn't showing as a display, on its hub.
public sealed class DisplayFinding
{
    // The adapter it was last shown through has no driver now.
    public bool GpuWithoutDriver { get; set; }
    public string DisplayName { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string GpuVendor { get; set; } = "";
    public DateTime? LastShown { get; set; }
}

// USB-C monitors carry their picture in a USB-C alternate mode, beside the USB that Windows lists, so a
// monitor's hub, keyboard and mouse can work while it shows nothing. Windows reports the two separately:
// the monitor under a graphics adapter, the hub under a USB controller. Their only shared trace is the
// Billboard device a USB-C display creates under its hub. These checks tie them back together.
internal static class Displays
{
    internal const string NotShowing = "Display not showing";
    private const string ComputerContainer = "{00000000-0000-0000-ffff-ffffffffffff}";

    // Stand-ins Windows and drivers create when no real monitor answers, Basic Display's default monitor and
    // NVIDIA's failsafe, aren't displays anyone plugged in.
    internal static bool Placeholder(string instance, string parent) => instance.StartsWith(@"DISPLAY\DEFAULT_MONITOR", StringComparison.OrdinalIgnoreCase)
        || instance.StartsWith(@"DISPLAY\NVD0000", StringComparison.OrdinalIgnoreCase) || parent.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase);
    // A display adapter with no driver, or one whose driver failed, can't drive any monitor wired to it.
    internal static string GpuLabel(GpuInfo g) => g.Name.Length > 0 && g.Name is not ("Display" or "Video Controller" or "Microsoft Basic Display Adapter") ? g.Name
        : g.Vendor.Length > 0 ? $"{g.Vendor} GPU" : "graphics adapter";
    internal static string Vendor(string instanceId) => instanceId.ToUpperInvariant() switch
    {
        var i when i.Contains("VEN_10DE") => "NVIDIA", var i when i.Contains("VEN_1002") => "AMD", var i when i.Contains("VEN_8086") => "Intel", _ => ""
    };
    // "Generic Monitor (DELL U3818DW)" is a DELL U3818DW.
    internal static string MonitorName(string name) => name.StartsWith("Generic Monitor (", StringComparison.Ordinal) && name.EndsWith(')') ? name[17..^1]
        : name.StartsWith("Generic PnP Monitor", StringComparison.Ordinal) || name.Length == 0 ? "an external display" : name;

    // billboardParents: the USB devices a Billboard device was ever reported under, present or not.
    internal static void Analyze(Snapshot snapshot, IReadOnlyCollection<string> billboardParents)
    {
        foreach (var gpu in snapshot.Gpus.Where(g => !g.HasDriver))
        {
            var last = snapshot.Displays.Where(d => d.External && !d.Present && d.GpuInstanceId.Equals(gpu.InstanceId, StringComparison.OrdinalIgnoreCase) && d.LastRemoval != null).MaxBy(d => d.LastRemoval);
            snapshot.Diagnostics.Add($"Windows has no driver running the {GpuLabel(gpu)} ({gpu.InstanceId}){(gpu.ProblemCode != 0 ? $", Code {gpu.ProblemCode}" : "")}, so displays wired to it, often a laptop's USB-C or HDMI ports, show nothing."
                + (last != null ? $" {MonitorName(last.Name)} was last shown through it until {Moment(last.LastRemoval!.Value)}." : "") + " Install its graphics driver, then restart.");
        }
        // With an external display showing, a USB-C monitor's missing picture can't be told from one shown
        // through another port, so nothing is flagged.
        if (snapshot.Displays.Any(d => d.External && d.Present)) return;
        var hubs = snapshot.Nodes.Where(n => n.Kind is "Hub" or "Device" && (n.InstanceId.Length > 0 && billboardParents.Contains(n.InstanceId, StringComparer.OrdinalIgnoreCase)
            || n.Children.Any(c => c.Kind == "Device" && c.Billboard != null))).ToList();
        // A USB 3 hub's two sides both lead to the same monitor; the side with the Billboard carries it.
        foreach (var hub in hubs)
        {
            var last = snapshot.Displays.Where(d => d.External && !d.Present && d.LastRemoval != null).MaxBy(d => d.LastRemoval);
            var gpu = last == null ? null : snapshot.Gpus.FirstOrDefault(g => g.InstanceId.Equals(last.GpuInstanceId, StringComparison.OrdinalIgnoreCase));
            hub.Display = new()
            {
                GpuWithoutDriver = gpu is { HasDriver: false },
                DisplayName = last != null ? MonitorName(last.Name) : "",
                GpuName = gpu != null ? GpuLabel(gpu) : "",
                GpuVendor = gpu?.Vendor ?? "",
                LastShown = last?.LastRemoval
            };
            hub.Notes.Add("Display evidence: a Billboard device, which only USB-C displays create, was reported under this " + (hub.Kind == "Hub" ? "hub" : "device")
                + ", and Windows shows no external display now." + (last != null ? $" The last external display it showed, {MonitorName(last.Name)}, was removed at {Moment(last.LastRemoval!.Value)}{(gpu != null ? $", last shown through the {GpuLabel(gpu)}" : "")}." : ""));
        }
    }
    internal static string Moment(DateTime t) => t.Date == DateTime.Today ? t.ToString("HH:mm") + " today" : t.ToString("yyyy-MM-dd HH:mm");

    internal static Severity SeverityOf(UsbNode n) => n.Display is { GpuWithoutDriver: true } ? Severity.Warning : Severity.Note;

    // What a missing picture means. Without a driverless adapter to blame, the monitor may simply be showing
    // another source, so it says how to check rather than what's wrong.
    internal static Explanations.Explanation Explain(UsbNode n)
    {
        const string what = "This monitor's USB is connected, but Windows isn't showing it as a display.";
        if (n.Display is not DisplayFinding f) return new(what);
        string shown = f.DisplayName.Length > 0 ? f.DisplayName : "The external display";
        if (f.GpuWithoutDriver)
            return new(what + " The graphics adapter that last drove it has no driver.", "Yes: it gets no picture from this computer. Its keyboard, mouse and other USB devices still work.",
                $"{shown} was last shown through the {f.GpuName}{(f.LastShown is DateTime t ? $" until {Moment(t)}" : "")}, and Windows has no driver running that adapter now, so it can't send a picture. This usually follows a graphics driver update that didn't finish.",
                [$"Reinstall the {(f.GpuVendor.Length > 0 ? f.GpuVendor + " " : "")}graphics driver: {Source(f.GpuVendor)}.",
                 "Restart the computer. If the picture doesn't come back, unplug the monitor's USB-C cable and plug it back in.",
                 "Laptops that can switch which graphics adapter drives their external ports (a MUX switch or Advanced Optimus) may need that adapter's driver even when the screen runs on the other one."]);
        return new(what, "Only if you expect a picture from this computer: its keyboard, mouse and other USB devices still work.",
            f.DisplayName.Length > 0 && f.LastShown is DateTime last ? $"The last external display Windows showed, {f.DisplayName}, was removed at {Moment(last)}." : "",
            ["Check the monitor's input. A monitor with a KVM switch can keep its USB on this computer while it shows another source.",
             "Press Windows+P and choose Extend or Duplicate.",
             "Unplug the USB-C cable and plug it back in, at both ends.",
             "If a graphics driver was just updated, restart the computer."]);
    }
    private static string Source(string vendor) => vendor switch
    {
        "NVIDIA" => "the NVIDIA App's Drivers tab, nvidia.com, or the computer maker's support app",
        "AMD" => "AMD Software, amd.com, or the computer maker's support app",
        "Intel" => "Intel Driver & Support Assistant, intel.com, or the computer maker's support app",
        _ => "the computer maker's support site or app"
    };

    // Monitors in every state, the PCI display controllers that are present, and every devnode a USB Billboard
    // device sits under, present or not. Monitors and Billboards are few, so this stays quick.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static (List<GpuInfo> Gpus, List<DisplayInfo> Displays, List<string> BillboardParents) Read()
    {
        var gpus = new List<GpuInfo>(); var displays = new List<DisplayInfo>(); var billboards = new List<string>();
        Each(MonitorClass, null, 0, (set, d, instance) =>
        {
            string parent = PropertyString(set, d, Parent) ?? "";
            if (Placeholder(instance, parent)) return;
            displays.Add(new()
            {
            InstanceId = instance, Name = Text(set, d, 12) ?? Text(set, d, 0) ?? "",
            External = !string.Equals(Container(set, d), ComputerContainer, StringComparison.OrdinalIgnoreCase),
            Present = Bool(set, d, IsPresent) ?? false, GpuInstanceId = parent,
            LastArrival = Time(set, d, LastArrival), LastRemoval = Time(set, d, LastRemoval)
            });
        });
        // DIGCF_ALLCLASSES | DIGCF_PRESENT over the PCI enumerator; display controllers are class 03.
        Each(null, "PCI", 6, (set, d, instance) =>
        {
            if (!Multi(set, d, 2).Any(id => id.Contains("CC_03", StringComparison.OrdinalIgnoreCase))) return;
            string service = Text(set, d, 4) ?? "";
            int problem = Native.CM_Get_DevNode_Status(out var status, out var code, d.DevInst, 0) == 0 && (status & 0x400) != 0 ? (int)code : 0;
            gpus.Add(new() { InstanceId = instance, Name = Text(set, d, 12) ?? Text(set, d, 0) ?? "", Vendor = Vendor(instance), Service = service, ProblemCode = problem, HasDriver = service.Length > 0 && problem == 0 });
        });
        // Billboard devices are class 0x11; DIGCF_ALLCLASSES alone includes the ones not present.
        Each(null, "USB", 4, (set, d, instance) =>
        {
            if (Multi(set, d, 2).Any(id => id.Contains("Class_11", StringComparison.OrdinalIgnoreCase)) && PropertyString(set, d, Parent) is { Length: > 0 } parent) billboards.Add(parent);
        });
        return (gpus, displays, billboards);
    }

    private static readonly Guid MonitorClass = new("4D36E96E-E325-11CE-BFC1-08002BE10318");
    private static readonly Native.PropertyKey Parent = new() { Category = new("4340A6C5-93FA-4706-972C-7B648008A5A7"), Id = 8 };
    private static readonly Native.PropertyKey LastArrival = new() { Category = new("83DA6326-97A6-4088-9453-A1923F573B29"), Id = 102 };
    private static readonly Native.PropertyKey LastRemoval = new() { Category = new("83DA6326-97A6-4088-9453-A1923F573B29"), Id = 103 };
    private static readonly Native.PropertyKey IsPresent = new() { Category = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), Id = 5 };
    private static readonly Native.PropertyKey ContainerId = new() { Category = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Each(Guid? klass, string? enumerator, uint flags, Action<IntPtr, Native.DeviceData, string> visit)
    {
        IntPtr set;
        if (klass is Guid g) set = Native.SetupDiGetClassDevs(ref g, enumerator, IntPtr.Zero, flags);
        else set = Native.SetupDiGetClassDevsNoGuid(IntPtr.Zero, enumerator, IntPtr.Zero, flags);
        if (set == new IntPtr(-1)) return;
        try
        {
            for (uint i = 0; ; i++)
            {
                var d = new Native.DeviceData { Size = Marshal.SizeOf<Native.DeviceData>() };
                if (!Native.SetupDiEnumDeviceInfo(set, i, ref d)) break;
                var buffer = new StringBuilder(512);
                if (Native.SetupDiGetDeviceInstanceId(set, ref d, buffer, buffer.Capacity, out _)) visit(set, d, buffer.ToString());
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }
    private static byte[]? Raw(IntPtr set, Native.DeviceData d, Native.PropertyKey key, uint type)
    {
        var bytes = new byte[1024];
        return Native.SetupDiGetDeviceProperty(set, ref d, ref key, out uint actual, bytes, (uint)bytes.Length, out uint needed, 0) && actual == type ? bytes[..(int)Math.Min(needed, (uint)bytes.Length)] : null;
    }
    private static string? PropertyString(IntPtr set, Native.DeviceData d, Native.PropertyKey key) =>
        Raw(set, d, key, 0x12) is { } b ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;
    private static DateTime? Time(IntPtr set, Native.DeviceData d, Native.PropertyKey key) =>
        Raw(set, d, key, 0x10) is { Length: >= 8 } b ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(b)).ToLocalTime() : null;
    private static bool? Bool(IntPtr set, Native.DeviceData d, Native.PropertyKey key) =>
        Raw(set, d, key, 0x11) is { Length: >= 1 } b ? b[0] != 0 : null;
    private static string Container(IntPtr set, Native.DeviceData d) =>
        Raw(set, d, ContainerId, 0x0D) is { Length: >= 16 } b ? new Guid(b[..16]).ToString("B") : "";
    private static string? Text(IntPtr set, Native.DeviceData d, uint property)
    {
        var bytes = new byte[2048];
        return Native.SetupDiGetDeviceRegistryProperty(set, ref d, property, out _, bytes, (uint)bytes.Length, out _) ? Encoding.Unicode.GetString(bytes).Split('\0')[0] : null;
    }
    private static string[] Multi(IntPtr set, Native.DeviceData d, uint property)
    {
        var bytes = new byte[8192];
        return Native.SetupDiGetDeviceRegistryProperty(set, ref d, property, out _, bytes, (uint)bytes.Length, out var needed)
            ? Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(needed, (uint)bytes.Length) & ~1).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UsbAtlas;

// Whether Windows may suspend a device to save power while the PC is in use (USB selective suspend).
// Three things must allow it: the power plan's USB selective suspend setting, the device's own "Allow the
// computer to turn off this device to save power" (Device Manager's Power Management tab, read through
// WMI), and a driver that idles the device. All of it is read-only; USB Atlas never changes these settings.
internal static class PowerSaving
{
    internal sealed record Setting(string Instance, string Service, bool Allowed, bool? HidSuspend);
    internal const string Warning = "Selective suspend on";
    private const string DeviceSetting = "“Allow the computer to turn off this device to save power”";
    private static readonly Guid UsbSettings = new("2a737441-1930-4402-8d77-b2bebba308a3"), SelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void Read(Snapshot snapshot, IReadOnlyDictionary<string, UsbScanner.DevNode> devices)
    {
        (snapshot.UsbSuspendPluggedIn, snapshot.UsbSuspendOnBattery) = PlanSetting();
        snapshot.OnBattery = GetSystemPowerStatus(out var status) ? status.ACLineStatus switch { 0 => true, 1 => false, _ => null } : null;
        List<(string Instance, bool Allowed)> entries;
        try { entries = DeviceSettings(); }
        catch (Exception ex) { snapshot.Diagnostics.Add("Power-saving settings unavailable: " + ex.Message); return; }
        var byOwner = new Dictionary<string, List<Setting>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, allowed) in entries)
        {
            // A host controller has no USB device above it, so its setting is its own.
            string owner = UsbScanner.UsbOwner(instance, devices) ?? instance, service = devices.GetValueOrDefault(instance)?.Service ?? "";
            if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = [];
            list.Add(new(instance, service, allowed, IsHid(service) ? HidSelectiveSuspend(instance) : null));
        }
        foreach (var node in snapshot.Nodes.Where(n => n.InstanceId.Length > 0))
            Apply(node, byOwner.GetValueOrDefault(node.InstanceId) ?? []);
    }

    // A device with several functions is suspended only when every one of them is idle, so one with the
    // setting off keeps it awake. The HID driver idles a device only when its INF turns on SelectiveSuspendEnabled.
    internal static string Classify(IReadOnlyList<Setting> settings) =>
        settings.Count == 0 ? "Not offered"
        : settings.Any(s => !s.Allowed) ? "Off"
        : settings.All(s => !IsHid(s.Service) || s.HidSuspend != false) ? "On" : "Unused by driver";

    internal static void Apply(UsbNode node, IReadOnlyList<Setting> settings)
    {
        node.PowerSaving = Classify(settings);
        int off = settings.Count(s => !s.Allowed);
        node.Notes.Add("Power saving: " + node.PowerSaving switch
        {
            "Not offered" => $"no driver for this device offers Windows' {DeviceSetting} setting.",
            "Off" when settings.Count > 1 => $"{DeviceSetting} is off for {off} of its {settings.Count} functions. A device with several functions is suspended only when all of them are idle, so Windows doesn't suspend it.",
            "Off" => $"{DeviceSetting} is off on its Power Management tab in Device Manager, so Windows doesn't suspend it.",
            "Unused by driver" => $"{DeviceSetting} is on, but its HID driver isn't set to use selective suspend (SelectiveSuspendEnabled is off), so Windows doesn't suspend it.",
            _ => $"{DeviceSetting} is on" + (settings.Count > 1 ? $" for all {settings.Count} of its functions" : "") + ", so Windows may suspend it when it looks idle while the power plan's USB selective suspend setting is on."
        });
        if (node.Kind is "Hub" or "Root hub" && node.PowerSaving == "On")
            node.Notes.Add("A hub is suspended only after everything plugged into it is, so this setting matters most on the devices themselves.");
    }

    // Sim racing hardware makers advise against suspending wheels, pedals and button boxes, so a game
    // controller that Windows may suspend is flagged. Other devices sleep and wake without trouble.
    internal static void Analyze(Snapshot snapshot)
    {
        if (snapshot.UsbSuspendActive == false) return;
        string plan = snapshot.UsbSuspendActive == true
            ? "USB selective suspend is on in the power plan" + (snapshot.OnBattery == true ? " while on battery" : "")
            : "the power plan didn't report its USB selective suspend setting, which Windows turns on by default";
        foreach (var node in snapshot.Nodes.Where(n => n.Kind == "Device" && n.DeviceType == "Game controller" && n.PowerSaving == "On"))
            UsbBudgets.Warn(node, Warning, $"Windows may suspend this game controller when it looks idle: {plan}, and its own {DeviceSetting} setting is on. A wheel base, pedals or button box suspended mid-session can be slow to wake or drop out. To prevent it, turn off USB selective suspend in Power Options (Change advanced power settings › USB settings), or clear that setting on this device's Power Management tab in Device Manager. USB Atlas only reads these settings.");
    }

    internal static string PlanSummary(Snapshot s) => s.UsbSuspendActive switch { true => "Selective suspend on", false => "Selective suspend off", null => "Not reported" };
    internal static string PlanNote(Snapshot s)
    {
        static string State(bool? on) => on switch { true => "on", false => "off", null => "not reported" };
        return $"Power plan: USB selective suspend is {State(s.UsbSuspendPluggedIn)} when plugged in and {State(s.UsbSuspendOnBattery)} on battery"
            + s.OnBattery switch { true => "; this PC is on battery now.", false => "; this PC is plugged in now.", null => "." }
            + " While it is off, Windows suspends no USB device, whatever each device's own setting says.";
    }

    private static bool IsHid(string service) => service.Equals("HidUsb", StringComparison.OrdinalIgnoreCase);

    // The power plan's USB selective suspend setting, plugged in and on battery.
    private static (bool?, bool?) PlanSetting()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var active) != 0) return (null, null);
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(active); var group = UsbSettings; var setting = SelectiveSuspend;
            bool? plugged = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out var ac) == 0 ? ac != 0 : null;
            bool? battery = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out var dc) == 0 ? dc != 0 : null;
            return (plugged, battery);
        }
        finally { LocalFree(active); }
    }

    // Device Manager's Power Management checkbox for each devnode that offers it. WMI names an instance
    // by its device instance ID plus "_0". Late-bound WMI scripting needs no extra package; it must
    // impersonate and read through Properties_ to return every instance.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<(string Instance, bool Allowed)> DeviceSettings()
    {
        var result = new List<(string, bool)>();
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
        locator.Security_.ImpersonationLevel = 3;
        dynamic service = locator.ConnectServer(".", @"root\wmi");
        dynamic set = service.ExecQuery("SELECT InstanceName, Enable FROM MSPower_DeviceEnable", "WQL", 0);
        int count = set.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic properties = set.ItemIndex(i).Properties_;
            string name = properties.Item("InstanceName").Value;
            bool allowed = properties.Item("Enable").Value;
            result.Add((System.Text.RegularExpressions.Regex.Replace(name, @"_\d+$", ""), allowed));
        }
        return result;
    }

    // SelectiveSuspendEnabled, REG_BINARY or REG_DWORD in the device's hardware key, turns on HID idling.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool? HidSelectiveSuspend(string instance)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters");
            if (key == null) return null;
            return key.GetValue("SelectiveSuspendEnabled") switch { byte[] { Length: > 0 } bytes => bytes[0] != 0, int value => value != 0, _ => false };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicy);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
}

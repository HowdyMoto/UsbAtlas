using Microsoft.Win32;

namespace UsbAtlas;

// The drivers Windows loaded and the problems Device Manager shows, read from the devnode tree and the
// driver's class key. Read-only, and readable without administrator rights.
internal static class Drivers
{
    internal static void Apply(Snapshot snapshot, IReadOnlyDictionary<string, UsbScanner.DevNode> devices)
    {
        // A problem on a composite device's interface or a HID collection belongs to the USB device above it.
        var problems = new Dictionary<string, List<DeviceProblem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, node) in devices.Where(d => d.Value.Problem != 0))
        {
            string owner = UsbScanner.UsbOwner(instance, devices) ?? instance;
            if (!problems.TryGetValue(owner, out var list)) problems[owner] = list = [];
            list.Add(new() { InstanceId = instance, Name = node.Name, Code = node.Problem, Meaning = Meaning(node.Problem) });
        }
        foreach (var n in snapshot.Nodes.Where(n => n.InstanceId.Length > 0))
        {
            var devnode = devices.GetValueOrDefault(n.InstanceId);
            n.DriverService = devnode?.Service ?? "";
            string key = n.DriverKey.Length > 0 ? n.DriverKey : devnode?.DriverKey ?? "";
            if (key.Length > 0) ReadPackage(n, key);
            if (n.DriverService.Length > 0 || n.DriverVersion.Length > 0)
                n.Notes.Add("Driver evidence: " + string.Join(", ", new[] { n.DriverService.Length > 0 ? "service " + n.DriverService : "", n.DriverProvider, n.DriverVersion.Length > 0 ? "version " + n.DriverVersion : "", n.DriverDate, n.DriverInf }.Where(x => x.Length > 0)) + ".");
            n.DriverProblems = problems.GetValueOrDefault(n.InstanceId) ?? [];
            problems.Remove(n.InstanceId);
            foreach (var p in n.DriverProblems)
                n.Notes.Add($"Driver evidence: Windows reports Code {p.Code} on {(p.InstanceId.Equals(n.InstanceId, StringComparison.OrdinalIgnoreCase) ? "this device" : $"its function {p.Name} ({p.InstanceId})")}: {p.Meaning}");
        }
        // A device that failed to enumerate (often Code 43, "Device Descriptor Request Failed") has no driver
        // key at its port, and a host controller that didn't start isn't scanned at all, so their problems
        // can't be placed on a node. They're reported for the whole scan instead.
        foreach (var p in problems.SelectMany(x => x.Value).Where(p => UsbScanner.UsbOwner(p.InstanceId, devices) != null || UsbControllerServices.Contains(devices.GetValueOrDefault(p.InstanceId)?.Service ?? "")))
            snapshot.Diagnostics.Add($"Windows reports Code {p.Code} on {(p.Name.Length > 0 ? p.Name : "a USB device")} ({p.InstanceId}), which this scan couldn't place in the topology: {p.Meaning}");
    }
    private static readonly HashSet<string> UsbControllerServices = new(StringComparer.OrdinalIgnoreCase) { "USBXHCI", "usbehci", "usbohci", "usbuhci", "UsbHub3", "USBHUB", "Ucx01000" };

    // DriverVersion, DriverDate, ProviderName and InfPath live under the device's driver key.
    private static void ReadPackage(UsbNode n, string driverKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
            if (key == null) return;
            n.DriverVersion = key.GetValue("DriverVersion") as string ?? "";
            n.DriverDate = key.GetValue("DriverDate") as string ?? "";
            n.DriverProvider = key.GetValue("ProviderName") as string ?? "";
            n.DriverInf = key.GetValue("InfPath") as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    // What Device Manager says for each problem code (CM_PROB_*), in its own words where they help.
    internal static string Meaning(int code) => code switch
    {
        1 => "This device is not configured correctly.",
        3 => "The driver for this device might be corrupted, or the system may be low on memory or other resources.",
        10 => "This device cannot start.",
        12 => "This device cannot find enough free resources that it can use.",
        14 => "This device cannot work properly until you restart your computer.",
        18 => "Reinstall the drivers for this device.",
        19 => "Windows cannot start this hardware device because its configuration information in the registry is incomplete or damaged.",
        21 => "Windows is removing this device.",
        22 => "This device is disabled.",
        24 => "This device is not present, is not working properly, or does not have all its drivers installed.",
        28 => "The drivers for this device are not installed.",
        29 => "This device is disabled because the firmware of the device did not give it the required resources.",
        31 => "This device is not working properly because Windows cannot load the drivers required for this device.",
        32 => "A driver (service) for this device has been disabled.",
        33 => "Windows cannot determine which resources are required for this device.",
        34 => "Windows cannot determine the settings for this device.",
        35 => "The computer's system firmware does not include enough information to properly configure and use this device.",
        37 => "Windows cannot initialize the device driver for this hardware.",
        38 => "Windows cannot load the device driver for this hardware because a previous instance of the device driver is still in memory.",
        39 => "Windows cannot load the device driver for this hardware. The driver may be corrupted or missing.",
        40 => "Windows cannot access this hardware because its service key information in the registry is missing or recorded incorrectly.",
        41 => "Windows successfully loaded the device driver for this hardware but cannot find the hardware device.",
        42 => "Windows cannot load the device driver for this hardware because there is a duplicate device already running in the system.",
        43 => "Windows has stopped this device because it has reported problems.",
        44 => "An application or service has shut down this hardware device.",
        45 => "Currently, this hardware device is not connected to the computer.",
        46 => "Windows cannot gain access to this hardware device because the operating system is in the process of shutting down.",
        47 => "Windows cannot use this hardware device because it has been prepared for safe removal, but it has not been removed from the computer.",
        48 => "The software for this device has been blocked from starting because it is known to have problems with Windows.",
        49 => "Windows cannot start new hardware devices because the system hive is too large.",
        50 => "Windows cannot apply all of the properties for this device.",
        51 => "This device is currently waiting on another device or set of devices to start.",
        52 => "Windows cannot verify the digital signature for the drivers required for this device.",
        53 => "This device has been reserved for use by the Windows kernel debugger.",
        54 => "This device has failed and is undergoing a reset.",
        _ => $"Device Manager reports problem code {code}."
    };
}

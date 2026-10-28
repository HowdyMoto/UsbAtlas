using Microsoft.Win32;

namespace UsbAtlas;

// The drivers Windows loaded and the problems Device Manager shows, read from the devnode tree and the
// driver's class key. Read-only, and readable without administrator rights.
internal static class Drivers
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
        // Each USB device's functions, and the storage disk under a drive, carry the drivers that do its work.
        var owned = devices.Select(d => (Instance: d.Key, Node: d.Value, Owner: StackOwner(d.Key, devices)))
            .Where(x => x.Owner != null).ToLookup(x => x.Owner!, StringComparer.OrdinalIgnoreCase);
        var classFilters = new Dictionary<string, (string[] Upper, string[] Lower)>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, (string Company, string Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in snapshot.Nodes.Where(n => n.InstanceId.Length > 0))
        {
            var devnode = devices.GetValueOrDefault(n.InstanceId);
            n.DriverService = devnode?.Service ?? "";
            string key = n.DriverKey.Length > 0 ? n.DriverKey : devnode?.DriverKey ?? "";
            if (key.Length > 0) (n.DriverVersion, n.DriverDate, n.DriverProvider, n.DriverInf) = ReadPackage(key);
            if (n.DriverService.Length > 0 || n.DriverVersion.Length > 0)
                n.Notes.Add("Driver evidence: " + string.Join(", ", new[] { n.DriverService.Length > 0 ? "service " + n.DriverService : "", n.DriverProvider, n.DriverVersion.Length > 0 ? "version " + n.DriverVersion : "", n.DriverDate, n.DriverInf }.Where(x => x.Length > 0)) + ".");
            if (devnode != null)
            {
                n.DriverStackRead = true;
                var stack = owned[n.InstanceId].Where(x => !x.Instance.Equals(n.InstanceId, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Instance, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var (instance, f, _) in stack.Where(x => x.Node.Service.Length > 0))
                {
                    var (version, date, provider, inf) = f.DriverKey.Length > 0 ? ReadPackage(f.DriverKey) : ("", "", "", "");
                    n.StackDrivers.Add(new() { Role = "function", InstanceId = instance, Function = f.Name, Service = f.Service, Provider = provider, Version = version, Date = date, Inf = inf });
                }
                foreach (var (instance, f) in stack.Select(x => (x.Instance, x.Node)).Prepend((n.InstanceId, devnode!)))
                {
                    var (upper, lower) = f.ClassGuid.Length > 0 ? classFilters.TryGetValue(f.ClassGuid, out var c) ? c : classFilters[f.ClassGuid] = ReadClassFilters(f.ClassGuid) : ([], []);
                    foreach (var (role, services) in new[] { ("upper filter", f.UpperFilters ?? []), ("lower filter", f.LowerFilters ?? []), ("class upper filter", upper), ("class lower filter", lower) })
                        foreach (var service in services.Where(s => !n.StackDrivers.Any(d => d.Role == role && d.Service.Equals(s, StringComparison.OrdinalIgnoreCase))))
                        {
                            var (company, version) = files.TryGetValue(service, out var file) ? file : files[service] = ReadServiceFile(service);
                            n.StackDrivers.Add(new() { Role = role, InstanceId = instance, Function = f.Name, Service = service, Provider = company, Version = version });
                        }
                }
                foreach (var d in n.StackDrivers)
                    n.Notes.Add($"Driver evidence: {d.Role} {d.Service} on {(d.InstanceId.Equals(n.InstanceId, StringComparison.OrdinalIgnoreCase) ? "this device" : $"{d.Function} ({d.InstanceId})")}: "
                        + string.Join(", ", new[] { d.Provider.Length > 0 ? d.Provider : "maker unknown", d.Version.Length > 0 ? "version " + d.Version : "", d.Date, d.Inf }.Where(x => x.Length > 0)) + ".");
            }
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

    // The USB device whose stack a devnode is part of: its own, a function's, or a drive's disk (USBSTOR\ or
    // SCSI\ under the device). Other children, such as a Bluetooth radio's devices, are devices of their own.
    internal static string? StackOwner(string instance, IReadOnlyDictionary<string, UsbScanner.DevNode> devices) =>
        UsbScanner.UsbOwner(instance, devices) ?? (instance.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) || instance.StartsWith(@"SCSI\", StringComparison.OrdinalIgnoreCase)
            ? devices.GetValueOrDefault(instance)?.Parent is { Length: > 0 } parent ? UsbScanner.UsbOwner(parent, devices) : null : null);

    // DriverVersion, DriverDate, ProviderName and InfPath live under the device's driver key.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Version, string Date, string Provider, string Inf) ReadPackage(string driverKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
            if (key == null) return ("", "", "", "");
            return (key.GetValue("DriverVersion") as string ?? "", key.GetValue("DriverDate") as string ?? "", key.GetValue("ProviderName") as string ?? "", key.GetValue("InfPath") as string ?? "");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ("", "", "", ""); }
    }
    // Filters installed for every device of a class, such as all cameras or all disks.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string[] Upper, string[] Lower) ReadClassFilters(string classGuid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + classGuid);
            return (key?.GetValue("UpperFilters") as string[] ?? [], key?.GetValue("LowerFilters") as string[] ?? []);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ([], []); }
    }
    // A filter has no driver package of its own on the device, so its maker and version come from its .sys file.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Company, string Version) ReadServiceFile(string service)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string path = key?.GetValue("ImagePath") as string ?? $@"System32\drivers\{service}.sys";
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            else if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[12..]);
            else if (!Path.IsPathRooted(path)) path = Path.Combine(windows, path);
            if (!File.Exists(path)) return ("", "");
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return (info.CompanyName?.Trim() ?? "", (info.FileVersion ?? "").Split(' ')[0]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { return ("", ""); }
    }

    // Windows' own drivers are what every computer has; a maker's driver, or a filter another program put in
    // the stack, is what makes this device behave differently from the same device elsewhere.
    internal static bool IsWindows(string provider) => provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
    // Usbccgp only splits a composite device into its functions, whose drivers do the work.
    private static bool GenericParent(string service) => service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase);
    // WinUSB and libusb hand the device to an app instead of a Windows class driver.
    internal static bool AppDriver(string service) => service is { } s && (s.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) || s.StartsWith("libusb", StringComparison.OrdinalIgnoreCase));
    // The drivers that do the device's work: its own unless it is only a composite parent, and its functions'.
    internal static List<DriverInfo> Working(UsbNode n) =>
        (n.DriverService.Length > 0 && !GenericParent(n.DriverService)
            ? new[] { new DriverInfo { Role = "device", InstanceId = n.InstanceId, Service = n.DriverService, Provider = n.DriverProvider, Version = n.DriverVersion, Date = n.DriverDate, Inf = n.DriverInf } } : [])
        .Concat(n.StackDrivers.Where(d => d.Role == "function")).DistinctBy(d => d.Service.ToUpperInvariant()).ToList();
    // What the Driver row says: a maker's drivers with version and age, or that Windows' own run it.
    internal static string Summary(UsbNode n)
    {
        var working = Working(n);
        if (working.Count == 0) return "";
        // Linux names the kernel drivers bound to it but not who made them.
        if (!n.DriverStackRead) return string.Join(", ", working.Select(d => d.Service));
        var makers = working.Where(d => !IsWindows(d.Provider)).ToList();
        string apps = working.Any(d => AppDriver(d.Service)) ? " · an app drives it directly" : "";
        if (makers.Count == 0) return "Windows' own" + apps;
        // One package often brings several functions' drivers, so a maker and date are named once.
        return string.Join("; ", makers.GroupBy(d => (Maker: d.Provider.Length > 0 ? d.Provider : "Unknown maker", Age: Age(d.Date)))
                .Select(g => string.Join(" · ", new[] { g.Key.Maker, g.Key.Age, string.Join(", ", g.Select(d => (d.Service + " " + d.Version).Trim())) }.Where(x => x.Length > 0))))
            + (working.Count > makers.Count ? " + Windows' own" : "") + apps;
    }
    // What the Filters row says: filters another program added. Windows' own filters are in Detection details.
    internal static string FilterSummary(UsbNode n)
    {
        if (!n.DriverStackRead) return "";
        var added = n.StackDrivers.Where(d => d.Role.Contains("filter") && !IsWindows(d.Provider)).DistinctBy(d => d.Service.ToUpperInvariant()).ToList();
        return added.Count == 0 ? NoFilters
            : string.Join(", ", added.Select(d => $"{d.Service} ({(d.Provider.Length > 0 ? d.Provider : "maker unknown")}{(d.Role.StartsWith("class") ? ", on every device of its kind" : "")})"));
    }
    // What search finds a device by: every driver it uses, such as "WinUSB" or "usbaudio2", and the makers of
    // drivers that aren't Windows' own, such as "Razer". Windows' own maker would match nearly everything.
    internal static string SearchText(UsbNode n) =>
        string.Join(" ", new[] { n.DriverService }.Concat(n.StackDrivers.Select(d => d.Service))
            .Concat(new[] { n.DriverProvider }.Concat(n.StackDrivers.Select(d => d.Provider)).Where(p => !IsWindows(p)))
            .Concat(Working(n).Any(d => AppDriver(d.Service)) ? ["an app drives it directly"] : [])
            .Concat(FilterSummary(n) is { Length: > 0 } f && f != NoFilters ? ["filter driver"] : [])
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    internal const string NoFilters = "None from other programs";
    // A driver package's date as month and year: an old one is the first thing to update.
    internal static string Age(string date) =>
        DateTime.TryParseExact(date, "M-d-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : date;

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

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UsbAtlas.Cli;

// Recent history from the Windows event logs. USB: devices set up, started, failing to start or removed
// (Kernel-PnP), drivers that failed to load, and what the USB host and hub drivers logged. Display: monitors
// and graphics adapters coming and going, graphics drivers installed, disabled, updated or reset, since a
// USB-C monitor's picture depends on them. Restart: restarts a program started, such as a driver updater,
// and unexpected ones. Read through wevtapi, so no package is needed; logs a standard user can't read are
// reported, not fatal.
internal static class EventLog
{
    private const string PnpConfiguration = "Microsoft-Windows-Kernel-PnP/Configuration";
    // The USB-C connector manager's failures: the firmware's USB-C controller (UCSI) didn't answer a command.
    // Only computers with UCSI have this log.
    private const string Ucsi = "Microsoft-Windows-USB-UCMUCSICX/Operational";
    private static readonly string[] UsbProviders = ["Microsoft-Windows-Kernel-PnP", "Microsoft-Windows-USB-USBHUB3", "Microsoft-Windows-USB-USBXHCI", "Microsoft-Windows-USB-UCX", "Microsoft-Windows-USB-USBPORT", "Microsoft-Windows-USB-USBHUB", "USBHUB3", "usbhub", "USBXHCI", "Microsoft-Windows-USB-USB4DeviceRouter-EventLogs"];
    // Graphics drivers' own providers, and Display, which logs a driver that stopped responding and recovered (4101).
    private static readonly string[] DisplayProviders = ["Display", "nvlddmkm", "amdkmdag", "amdwddmg", "igfx", "igfxn", "Microsoft-Windows-DxgKrnl"];
    private static readonly Regex Graphics = new(@"nvlddmkm|NVIDIA|amdkmdag|amdwddmg|\bAMD\b|Radeon|igfx|Intel\(R\)[^;]*Graphics|Display", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal sealed record Entry(DateTime Time, string Channel, string Provider, int Id, int Level, string Category, string InstanceId, string Message);
    internal const string Usb = "usb", Display = "display", Restart = "restart", Wake = "wake";

    internal static JsonObject Report(Session s, TimeSpan since, int max, bool errorsOnly, bool redact = false, bool usbOnly = false)
    {
        var problems = new List<string>();
        var entries = new List<Entry>();
        var gpus = s.Snapshot.Gpus.Select(g => g.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        (string, string)? Keep(string provider, int id, List<(string Name, string Value)> data) => Classify(provider, id, data, gpus);
        // timediff takes 64-bit milliseconds; a century is more than any log keeps.
        long ms = (long)Math.Min(since.TotalMilliseconds, TimeSpan.FromDays(36500).TotalMilliseconds);
        string recent = $"TimeCreated[timediff(@SystemTime) <= {ms}]";
        static string Names(IEnumerable<string> names) => string.Join(" or ", names.Select(p => $"@Name='{p}'"));
        // Each query has its own cap, so a busy log can't push the others' events out of the window.
        entries.AddRange(Read(PnpConfiguration, $"*[System[{recent}]]", problems, Keep));
        entries.AddRange(Read("System", $"*[System[Provider[{Names(UsbProviders)}] and {recent}]]", problems, Keep));
        entries.AddRange(Read(Ucsi, $"*[System[{recent}]]", problems, Keep, optional: true));
        if (!usbOnly)
        {
            entries.AddRange(Read("System", $"*[System[Provider[{Names(DisplayProviders)}] and {recent}]]", problems, Keep));
            // Service changes (7040 start type, 7045 installed) and Windows Update installs, kept when they're about graphics.
            entries.AddRange(Read("System", $"*[System[((Provider[@Name='Service Control Manager'] and (EventID=7040 or EventID=7045)) or Provider[@Name='Microsoft-Windows-WindowsUpdateClient']) and {recent}]]", problems, Keep));
            // Waking from sleep, with what Windows names as the cause (Power-Troubleshooter 1).
            entries.AddRange(Read("System", $"*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1 and {recent}]]", problems, Keep));
            // A program that restarted the computer (1074), and a restart nothing asked for (41).
            entries.AddRange(Read("System", $"*[System[((Provider[@Name='User32'] and EventID=1074) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)) and {recent}]]", problems, Keep));
        }
        var shown = entries.Where(e => (!errorsOnly || e.Level is >= 1 and <= 3) && (!usbOnly || e.Category == Usb))
            .DistinctBy(e => (e.Time, e.Provider, e.Id, e.Message)).OrderByDescending(e => e.Time).Take(max).ToList();
        // Devices since unplugged aren't in the snapshot, so their serials are found in the text itself.
        string Clean(string text) => redact ? Session.RedactText(text) : text;

        var byInstance = s.Snapshot.Nodes.Where(n => n.InstanceId.Length > 0).GroupBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var report = Reports.Header(s, "events");
        report["window"] = $"last {Window(since)}";
        report["note"] = "Kernel-PnP 400 and 410 are normal setup and start events; 411 means a device failed to start, 219 a driver failed to load, 420/430 removal or a device needing more setup. UCMUCSICX events mean the computer's USB-C controller firmware (UCSI) failed a command, which can stop charging, video or role swaps over USB-C. Display events cover monitors and graphics adapters coming and going and graphics drivers being installed, disabled or reset (Display 4101: a driver stopped responding and recovered). Restart events name the program that restarted the computer (User32 1074), or a restart nothing asked for (Kernel-Power 41). The path is where a USB device is now, when it's connected." + (usbOnly ? " Showing USB events only." : "");
        if (problems.Count > 0) report["logDiagnostics"] = J.Arr(problems.Select(p => (JsonNode)p));
        report["events"] = J.Arr(shown.Select(e =>
        {
            var node = e.Category == Usb ? Locate(e.InstanceId, byInstance, s) : null;
            return (JsonNode)J.Obj(("time", e.Time.ToString("yyyy-MM-dd HH:mm:ss")), ("level", LevelName(e.Level)), ("category", e.Category), ("source", $"{Short(e.Provider)} {e.Id}"),
                ("instanceId", J.S(Clean(e.InstanceId))), ("path", node != null ? s.PathOf(node) : null), ("name", node != null ? Topology.ShortName(node) : J.S(DisplayName(e.InstanceId, s))), ("message", J.S(Clean(e.Message))));
        }));
        return report;
    }
    private static string Window(TimeSpan t) => t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} day(s)" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hour(s)" : $"{t.TotalMinutes:0.#} minute(s)";
    private static string Short(string provider) => provider.Replace("Microsoft-Windows-", "");
    private static string LevelName(int level) => level switch { 1 => "critical", 2 => "error", 3 => "warning", 5 => "verbose", _ => "info" };

    // The node an event names: its instance ID exactly, or, for an interface such as …&MI_00, the one
    // connected device with that VID and PID.
    private static UsbNode? Locate(string instance, Dictionary<string, UsbNode> byInstance, Session s)
    {
        if (instance.Length == 0) return null;
        if (byInstance.TryGetValue(instance, out var n)) return s.IsMergedRoot(n) ? s.Parent(n) : n;
        var m = Regex.Match(instance, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var candidates = s.Snapshot.Nodes.Where(x => x.Kind is "Device" or "Hub" && x.VendorId.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) && x.ProductId.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    // A monitor or graphics adapter an event names, as displays shows it.
    private static string? DisplayName(string instance, Session s)
    {
        if (instance.Length == 0) return null;
        if (s.Snapshot.Gpus.FirstOrDefault(g => g.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase)) is GpuInfo gpu) return Displays.GpuLabel(gpu);
        if (s.Snapshot.Displays.FirstOrDefault(d => d.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase)) is DisplayInfo d) return Displays.MonitorName(d.Name);
        return null;
    }

    // Which kind of event this is, and the device it's about; null for one that's none of them.
    internal static (string Category, string Instance)? Classify(string provider, int id, List<(string Name, string Value)> data, IReadOnlySet<string> gpus)
    {
        // Kernel-PnP names the device as DeviceInstanceId; other events carry it in some other field.
        string named = data.FirstOrDefault(d => d.Name.Equals("DeviceInstanceId", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        string usb = UsbRelated(named) ? named : data.Select(d => d.Value.Trim()).FirstOrDefault(value => UsbRelated(value) && !value.Contains(' ')) ?? "";
        if (usb.Length > 0) return (Usb, usb);
        string display = new[] { named }.Concat(data.Select(d => d.Value.Trim())).FirstOrDefault(v => v.StartsWith(@"DISPLAY\", StringComparison.OrdinalIgnoreCase) || gpus.Contains(v)) ?? "";
        if (display.Length > 0) return (Display, display);
        if (provider.Contains("USB", StringComparison.OrdinalIgnoreCase)) return (Usb, "");
        if (provider == "User32" && id == 1074 || provider == "Microsoft-Windows-Kernel-Power" && id == 41) return (Restart, "");
        if (provider == "Microsoft-Windows-Power-Troubleshooter" && id == 1) return (Wake, "");
        if (DisplayProviders.Contains(provider)) return (Display, "");
        if (provider is "Service Control Manager" or "Microsoft-Windows-WindowsUpdateClient" && data.Any(d => Graphics.IsMatch(d.Value))) return (Display, "");
        return null;
    }

    private static bool UsbRelated(string text) => text.Contains(@"USB\", StringComparison.OrdinalIgnoreCase) || text.Contains("VID_", StringComparison.OrdinalIgnoreCase)
        || text.Contains(@"HID\", StringComparison.OrdinalIgnoreCase) || text.Contains(@"USBSTOR\", StringComparison.OrdinalIgnoreCase);

    private delegate (string Category, string Instance)? Classifier(string provider, int id, List<(string Name, string Value)> data);
    // An optional log that isn't on this computer, such as UCSI's on a desktop without USB-C, isn't a problem.
    private static List<Entry> Read(string channel, string query, List<string> problems, Classifier keep, bool optional = false)
    {
        var result = new List<Entry>();
        var handle = EvtQuery(IntPtr.Zero, channel, query, 0x1 | 0x200);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (optional && error is 15007 or 2) return result;
            if (!problems.Any(p => p.StartsWith(channel + ":", StringComparison.Ordinal))) problems.Add($"{channel}: " + (error == 5 ? "access denied; this log needs administrator rights here." : error is 15007 or 2 ? "log not found." : new System.ComponentModel.Win32Exception(error).Message));
            return result;
        }
        var metadata = new Dictionary<string, IntPtr>();
        try
        {
            var events = new IntPtr[64];
            // The Configuration log records every device set up, so the scan is capped.
            for (int scanned = 0; scanned < 20000 && EvtNext(handle, events.Length, events, 2000, 0, out int returned);)
            {
                for (int i = 0; i < returned; i++)
                {
                    try { if (Parse(events[i], channel, metadata, keep) is Entry e) result.Add(e); }
                    finally { EvtClose(events[i]); }
                }
                scanned += returned;
            }
        }
        finally
        {
            EvtClose(handle);
            foreach (var m in metadata.Values) if (m != IntPtr.Zero) EvtClose(m);
        }
        return result;
    }

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static Entry? Parse(IntPtr evt, string channel, Dictionary<string, IntPtr> metadata, Classifier keep)
    {
        if (Render(evt) is not string xml) return null;
        var root = XDocument.Parse(xml).Root!;
        var system = root.Element(Ns + "System")!;
        string provider = system.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "";
        int id = int.TryParse(system.Element(Ns + "EventID")?.Value, out var v) ? v : 0;
        int level = int.TryParse(system.Element(Ns + "Level")?.Value, out var l) ? l : 4;
        var time = DateTime.TryParse(system.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : DateTime.MinValue;
        var data = root.Descendants(Ns + "Data").Select(d => (Name: d.Attribute("Name")?.Value ?? "", Value: d.Value)).ToList();
        if (keep(provider, id, data) is not var (category, instance)) return null;
        string message = Format(evt, provider, metadata) ?? string.Join("; ", data.Where(d => d.Value.Length > 0).Select(d => d.Name.Length > 0 ? $"{d.Name}={d.Value}" : d.Value));
        return new(time, channel, provider, id, level, category, instance, Regex.Replace(message, @"[\s\x00-\x1F]+", " ").Trim());
    }

    private static string? Render(IntPtr evt)
    {
        EvtRender(IntPtr.Zero, evt, 1, 0, null, out int used, out _);
        if (used <= 0) return null;
        var buffer = new char[used / 2 + 1];
        return EvtRender(IntPtr.Zero, evt, 1, buffer.Length * 2, buffer, out used, out _) ? new string(buffer, 0, Math.Max(0, used / 2 - 1)) : null;
    }
    private static string? Format(IntPtr evt, string provider, Dictionary<string, IntPtr> metadata)
    {
        if (!metadata.TryGetValue(provider, out var meta)) metadata[provider] = meta = EvtOpenPublisherMetadata(IntPtr.Zero, provider, null, 0, 0);
        if (meta == IntPtr.Zero) return null;
        EvtFormatMessage(meta, evt, 0, 0, IntPtr.Zero, 1, 0, null, out int used);
        if (used <= 0) return null;
        var buffer = new char[used];
        return EvtFormatMessage(meta, evt, 0, 0, IntPtr.Zero, 1, buffer.Length, buffer, out used) ? new string(buffer, 0, Math.Max(0, used - 1)) : null;
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] private static extern bool EvtNext(IntPtr resultSet, int size, [Out] IntPtr[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, [Out] char[]? buffer, out int bufferUsed, out int propertyCount);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtOpenPublisherMetadata(IntPtr session, string publisher, string? logFile, int locale, int flags);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EvtFormatMessage(IntPtr metadata, IntPtr evt, uint messageId, int valueCount, IntPtr values, int flags, int bufferSize, [Out] char[]? buffer, out int bufferUsed);
    [DllImport("wevtapi.dll")] private static extern bool EvtClose(IntPtr handle);
}

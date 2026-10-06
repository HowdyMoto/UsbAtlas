using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace UsbAtlas;

// Whether a device may wake the computer from sleep, and what woke it last. "My PC wakes up by itself" is
// usually a mouse, keyboard or wireless receiver that's allowed to wake it and sends a stray event. Device
// Manager's "Allow this device to wake the computer" is read through WMI (MSPower_DeviceWakeEnable), and the
// last wake from the System log (Power-Troubleshooter 1), as powercfg /lastwake shows it. Read-only.
public sealed class WakeInfo
{
    public DateTime Time { get; set; }
    // What Windows named as the wake source, such as "USB Input Device", and the USB device it is, when
    // that name belongs to one.
    public string Source { get; set; } = "";
    public string InstanceId { get; set; } = "";
}

internal static class Wake
{
    internal const string WokeComputer = "Woke the computer";
    // A wake older than this is history, not something to act on.
    internal static readonly TimeSpan Recent = TimeSpan.FromDays(7);

    // A device can wake the computer when any of its functions may: On, Off, or Not supported when none offers it.
    internal static string Classify(IReadOnlyList<bool> settings) => settings.Count == 0 ? "Not supported" : settings.Any(s => s) ? "On" : "Off";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void Read(Snapshot snapshot, IReadOnlyDictionary<string, UsbScanner.DevNode> devices)
    {
        try
        {
            var byOwner = new Dictionary<string, List<bool>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instance, enabled) in WakeSettings())
                if (UsbScanner.UsbOwner(instance, devices) is string owner)
                {
                    if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = [];
                    list.Add(enabled);
                }
            foreach (var n in snapshot.Nodes.Where(n => n.Kind is "Device" or "Hub" && n.InstanceId.Length > 0))
                n.WakeSetting = Classify(byOwner.GetValueOrDefault(n.InstanceId) ?? []);
        }
        catch (Exception ex) { snapshot.Diagnostics.Add("Wake settings unavailable: " + ex.Message); }
        snapshot.LastWake = LastWake();
        if (snapshot.LastWake is { Source.Length: > 0 } wake) wake.InstanceId = SourceDevice(wake.Source, devices) ?? "";
    }

    // The USB device a wake source names: the one devnode with that name, or several that are all one device's.
    internal static string? SourceDevice(string source, IReadOnlyDictionary<string, UsbScanner.DevNode> devices)
    {
        string name = Name(source);
        var owners = devices.Where(d => d.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(d => UsbScanner.UsbOwner(d.Key, devices)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return owners is [string owner] ? owner : null;
    }
    // Windows writes a device source as "Device -USB Input Device".
    internal static string Name(string source) => source.StartsWith("Device -", StringComparison.OrdinalIgnoreCase) ? source[8..].Trim() : source.Trim();

    // A note on the device that woke the computer in the last week. Nothing is flagged for being able to,
    // since keyboards and mice usually should.
    internal static void Analyze(Snapshot snapshot)
    {
        foreach (var n in snapshot.Nodes) n.WokeComputerAt = null;
        if (snapshot.LastWake is not { InstanceId.Length: > 0 } wake || snapshot.CapturedAt - wake.Time > Recent) return;
        foreach (var n in snapshot.Nodes.Where(n => n.InstanceId.Equals(wake.InstanceId, StringComparison.OrdinalIgnoreCase))) n.WokeComputerAt = wake.Time;
    }

    internal static Explanations.Explanation Explain(UsbNode n)
    {
        return new($"This device woke the computer from sleep{(n.WokeComputerAt is DateTime at ? $" at {at:g}" : "")}.",
            "Only if you didn't mean it to: a mouse nudged, a keyboard key, or a stray signal from a wireless receiver's mouse or keyboard all wake it.", "",
            ["To stop it: Device Manager › this device › Properties › Power Management › clear “Allow this device to wake the computer”. A keyboard or mouse with several entries may need it cleared on each.",
             "To keep it able to wake the computer but less often, turn a wireless mouse off or over before sleep, or move its receiver away from the mouse."]);
    }

    // Each devnode's "Allow this device to wake the computer". WMI names an instance by its device instance ID
    // plus "_0"; read as PowerSaving reads MSPower_DeviceEnable.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<(string Instance, bool Enabled)> WakeSettings()
    {
        var result = new List<(string, bool)>();
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
        locator.Security_.ImpersonationLevel = 3;
        dynamic service = locator.ConnectServer(".", @"root\wmi");
        dynamic set = service.ExecQuery("SELECT InstanceName, Enable FROM MSPower_DeviceWakeEnable", "WQL", 0);
        int count = set.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic properties = set.ItemIndex(i).Properties_;
            string name = properties.Item("InstanceName").Value;
            bool enabled = properties.Item("Enable").Value;
            result.Add((System.Text.RegularExpressions.Regex.Replace(name, @"_\d+$", ""), enabled));
        }
        return result;
    }

    // The newest Power-Troubleshooter 1 in the System log, which a standard user can read.
    private static WakeInfo? LastWake()
    {
        try
        {
            // EvtQueryChannelPath | EvtQueryReverseDirection: newest first.
            var query = EvtQuery(IntPtr.Zero, "System", "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]", 0x1 | 0x200);
            if (query == IntPtr.Zero) return null;
            try
            {
                var events = new IntPtr[1];
                if (!EvtNext(query, 1, events, 2000, 0, out int returned) || returned == 0) return null;
                try { return Parse(Render(events[0])); }
                finally { EvtClose(events[0]); }
            }
            finally { EvtClose(query); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
    // WakeTime and WakeSourceText from the event's XML; null without a wake time.
    internal static WakeInfo? Parse(string? xml)
    {
        if (xml == null) return null;
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var data = XDocument.Parse(xml).Descendants(ns + "Data").ToDictionary(d => d.Attribute("Name")?.Value ?? "", d => d.Value);
        if (!DateTime.TryParse(data.GetValueOrDefault("WakeTime"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) return null;
        return new WakeInfo { Time = time.ToLocalTime(), Source = (data.GetValueOrDefault("WakeSourceText") ?? "").Trim() };
    }
    private static string? Render(IntPtr evt)
    {
        EvtRender(IntPtr.Zero, evt, 1, 0, null, out int used, out _);
        if (used <= 0) return null;
        var buffer = new char[used / 2 + 1];
        return EvtRender(IntPtr.Zero, evt, 1, buffer.Length * 2, buffer, out used, out _) ? new string(buffer, 0, Math.Max(0, used / 2 - 1)) : null;
    }
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] private static extern bool EvtNext(IntPtr resultSet, int size, [Out] IntPtr[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, [Out] char[]? buffer, out int bufferUsed, out int propertyCount);
    [DllImport("wevtapi.dll")] private static extern bool EvtClose(IntPtr handle);
}

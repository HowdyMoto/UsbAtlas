using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace UsbAtlas;

// Rescans when Windows reports that devices were added or removed. Plugging in one
// device raises a burst of notifications while its drivers load, so the scan waits
// for a short quiet period; a change that arrives during a scan queues one more.
public partial class MainWindow
{
    private const int WmDeviceChange = 0x0219, DbtDevNodesChanged = 0x0007, DbtDeviceArrival = 0x8000, DbtDeviceRemoveComplete = 0x8004, DbtDevTypDeviceInterface = 5;
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private readonly DispatcherTimer deviceSettle = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool rescanQueued;
    private readonly ReconnectTracker reconnects = new();
    private IntPtr deviceNotification;

    private void WatchDevices()
    {
        deviceSettle.Tick += async (_, _) => { deviceSettle.Stop(); await RescanAfterDeviceChange(); };
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(DeviceChangeHook);
        // Registering for USB device interfaces adds arrival and removal messages that name the device,
        // so a quick drop and return is counted even when it settles into a single rescan.
        if (source != null)
        {
            var filter = new DevBroadcastInterface { Size = Marshal.SizeOf<DevBroadcastInterface>(), DeviceType = DbtDevTypDeviceInterface, ClassGuid = UsbDeviceInterface };
            deviceNotification = RegisterDeviceNotification(source.Handle, ref filter, 0);
        }
        Closed += (_, _) => { deviceSettle.Stop(); if (deviceNotification != IntPtr.Zero) UnregisterDeviceNotification(deviceNotification); };
    }

    // DBT_DEVNODES_CHANGED is broadcast to every top-level window without registration.
    private IntPtr DeviceChangeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int change = unchecked((int)wParam.ToInt64());
        if (message == WmDeviceChange && change is DbtDeviceArrival or DbtDeviceRemoveComplete && lParam != IntPtr.Zero
            && Marshal.ReadInt32(lParam, 4) == DbtDevTypDeviceInterface && ReconnectTracker.InstanceIdFromPath(Marshal.PtrToStringUni(lParam + 28) ?? "") is string id)
        {
            if (change == DbtDeviceArrival) reconnects.Arrived(id, DateTime.Now); else reconnects.Removed(id, DateTime.Now);
        }
        if (message == WmDeviceChange && change is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemoveComplete && !demo) QueueDeviceRescan();
        return IntPtr.Zero;
    }

    private void QueueDeviceRescan() { deviceSettle.Stop(); deviceSettle.Start(); }

    private async Task RescanAfterDeviceChange()
    {
        if (busy) { rescanQueued = true; return; }
        await Refresh();
    }

    // Identifies what occupies each port, so a swap at the same port reads as one out, one in.
    private static Dictionary<string, UsbNode> Occupants(Snapshot s) =>
        s.Nodes.Where(n => n.Kind is "Device" or "Hub").GroupBy(n => $"{n.Id}|{n.VendorId}:{n.ProductId}|{n.Serial}").ToDictionary(g => g.Key, g => g.First());

    private void ReportConnections(Dictionary<string, UsbNode> before, Dictionary<string, UsbNode> after)
    {
        var added = after.Where(p => !before.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var removed = before.Where(p => !after.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        static string? Describe(List<UsbNode> nodes, string verb) => nodes.Count switch { 0 => null, 1 => $"{verb} {nodes[0].DisplayName}", _ => $"{nodes.Count} devices {verb.ToLowerInvariant()}" };
        var summary = new[] { Describe(added, "Connected"), Describe(removed, "Disconnected") }.Where(s => s != null).ToList();
        if (summary.Count == 0) return;
        StatusText.Text += " · " + string.Join(" · ", summary);
        StatusText.ToolTip = StatusText.Text;
        foreach (var node in added) Pulse(node, 2.4);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevBroadcastInterface { public int Size, DeviceType, Reserved; public Guid ClassGuid; public short Name; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DevBroadcastInterface filter, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterDeviceNotification(IntPtr handle);
}

// Counts quick disconnect-and-return cycles per device. A device that repeatedly drops and comes
// back within seconds is usually short of power, or on a faulty cable or connector.
internal sealed class ReconnectTracker
{
    internal static readonly TimeSpan QuickReturn = TimeSpan.FromSeconds(30), Window = TimeSpan.FromMinutes(5);
    internal const int Threshold = 3;
    private readonly Dictionary<string, DateTime> removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> returns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unstable = new(StringComparer.OrdinalIgnoreCase);

    internal void Removed(string id, DateTime at) => removed[id] = at;
    internal void Arrived(string id, DateTime at)
    {
        if (!removed.Remove(id, out var gone) || at - gone > QuickReturn) return;
        if (!returns.TryGetValue(id, out var times)) returns[id] = times = [];
        times.Add(at);
        // Once a burst crosses the threshold the device stays flagged for the session, so earlier drops still show.
        if (times.Count(t => at - t <= Window) >= Threshold) unstable.Add(id);
    }
    internal bool IsUnstable(string id) => unstable.Contains(id);

    internal void Apply(Snapshot snapshot)
    {
        if (snapshot.IsDemo) return;
        foreach (var node in snapshot.Nodes.Where(n => n.InstanceId.Length > 0 && unstable.Contains(n.InstanceId)))
        {
            var times = returns[node.InstanceId];
            node.QuickReconnects = times.Count;
            node.Notes.Add($"Dropped and came back within seconds {times.Count} times this session, most recently at {times[^1]:T}. Repeated quick reconnects usually mean the device is short of power, or a cable or connector is faulty.");
        }
    }

    // A path like \\?\USB#VID_046D&PID_C52B#5&2a8c&0&3#{a5dcbf10-…} names the instance USB\VID_046D&PID_C52B\5&2a8c&0&3.
    internal static string? InstanceIdFromPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) path = path[4..];
        int guid = path.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid <= 0) return null;
        var parts = path[..guid].Split('#');
        return parts.Length == 3 && parts.All(p => p.Length > 0) ? string.Join('\\', parts) : null;
    }
}

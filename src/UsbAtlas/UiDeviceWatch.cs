using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace UsbAtlas;

// Rescans when Windows reports that devices were added or removed. Plugging in one
// device raises a burst of notifications while its drivers load, so the scan waits
// for a short quiet period; a change that arrives during a scan queues one more.
public partial class MainWindow
{
    private const int WmDeviceChange = 0x0219, DbtDevNodesChanged = 0x0007, DbtDeviceArrival = 0x8000, DbtDeviceRemoveComplete = 0x8004;
    private readonly DispatcherTimer deviceSettle = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool rescanQueued;

    private void WatchDevices()
    {
        deviceSettle.Tick += async (_, _) => { deviceSettle.Stop(); await RescanAfterDeviceChange(); };
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(DeviceChangeHook);
        Closed += (_, _) => deviceSettle.Stop();
    }

    // DBT_DEVNODES_CHANGED is broadcast to every top-level window without registration.
    private IntPtr DeviceChangeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int change = unchecked((int)wParam.ToInt64());
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
}

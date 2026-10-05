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
    // Hubs register the USB hub interface class, not the USB device one, so a hub's drop, such as a KVM
    // switch taking a monitor's hub away, is only seen by registering for both.
    private static readonly Guid[] UsbInterfaces = [new("A5DCBF10-6530-11D2-901F-00C04FB951ED"), new("F18A0E88-C30C-11D0-8815-00A0C906BED8")];
    private readonly DispatcherTimer deviceSettle = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool rescanQueued;
    private readonly ReconnectTracker reconnects = new();
    private readonly List<IntPtr> deviceNotifications = [];

    private void WatchDevices()
    {
        deviceSettle.Tick += async (_, _) => { deviceSettle.Stop(); await RescanAfterDeviceChange(); };
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(DeviceChangeHook);
        // Registering for USB device and hub interfaces adds arrival and removal messages that name the
        // device, so a quick drop and return is counted even when it settles into a single rescan.
        if (source != null)
            foreach (var guid in UsbInterfaces)
            {
                var filter = new DevBroadcastInterface { Size = Marshal.SizeOf<DevBroadcastInterface>(), DeviceType = DbtDevTypDeviceInterface, ClassGuid = guid };
                if (RegisterDeviceNotification(source.Handle, ref filter, 0) is var handle && handle != IntPtr.Zero) deviceNotifications.Add(handle);
            }
        Closed += (_, _) => { deviceSettle.Stop(); foreach (var handle in deviceNotifications) UnregisterDeviceNotification(handle); };
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

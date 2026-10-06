using System.Text.RegularExpressions;

namespace UsbAtlas;

// USB 3 ports, cables and especially busy drives give off radio noise around 2.4 GHz (Intel's "USB 3.0 Radio
// Frequency Interference Impact on 2.4 GHz Wireless Devices"), so a wireless mouse or keyboard receiver, a
// controller or headset dongle, or a Bluetooth adapter right next to one can lose range or drop out. Nothing
// is measured, so it's a note, and only where it's likeliest: a receiver on the same plug-in hub as a drive or
// video device linked at 5 Gb/s or faster. Windows' root port numbers don't follow the computer's physical
// layout, so sockets on the computer aren't taken to be neighbors.
internal static class Interference
{
    internal const string Nearby = "USB 3 nearby";

    private static readonly Regex ReceiverName = new(@"\b(receiver|dongle|unifying|lightspeed|nano|2\.4 ?ghz|wireless|bolt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A 2.4 GHz receiver: a Bluetooth or other wireless controller, or an input or audio device whose names say
    // it's a receiver or dongle.
    internal static bool IsReceiver(UsbNode n) =>
        n.Kind == "Device" && (n.DeviceType == "Wireless" || n.DeviceType is "Keyboard" or "Mouse" or "HID / controls" or "Game controller" or "Audio"
            && new[] { n.DisplayName, n.ReportedProduct, n.WindowsName, n.LookupProduct }.Any(s => s.Length > 0 && ReceiverName.IsMatch(s)));

    // The busy, fast devices whose noise matters most: drives and video, linked at 5 Gb/s or faster.
    private static bool Noisy(UsbNode n) => n.Kind == "Device" && n.LinkMbps >= 5000
        && n.DeviceType is "Storage" or "External drive" or "Flash drive" or "Card reader" or "Optical drive" or "Camera / video";

    // Names, for each receiver, the device next to it on its hub. Worked out when analyzed.
    internal static void Analyze(Snapshot snapshot)
    {
        foreach (var n in snapshot.Nodes) n.NoisyNeighbor = null;
        foreach (var hub in snapshot.Nodes.Where(n => n.Kind == "Hub"))
        {
            // A USB 3 hub's two sides are one box: a receiver on its USB 2 side sits beside its USB 3 side's drives.
            var sides = snapshot.Nodes.Where(o => o == hub || hub.CompanionHubId.Length > 0 && o.Id == hub.CompanionHubId).SelectMany(h => h.Children).ToList();
            var noisy = sides.Where(Noisy).OrderByDescending(d => d.LinkMbps).FirstOrDefault();
            if (noisy == null) continue;
            foreach (var receiver in hub.Children.Where(IsReceiver)) receiver.NoisyNeighbor = noisy;
        }
    }

    internal static Explanations.Explanation Explain(UsbNode n, IReadOnlyList<UsbNode> path)
    {
        var neighbor = n.NoisyNeighbor;
        string near = neighbor == null ? "a USB 3 device next to it" : $"{Topology.ShortName(neighbor)} next to it, linked at {Topology.ShortSpeed(neighbor)}";
        string hub = path.Count >= 2 && path[^2].Kind == "Hub" ? $" on {Topology.ShortName(path[^2])}" : "";
        return new($"This looks like a 2.4 GHz wireless receiver, with {near}{hub}. USB 3 ports, cables and busy drives give off radio noise around 2.4 GHz.",
            "Only if it drops out or lags: nothing here is measured, and many receivers work fine beside USB 3.", "",
            ["Move the receiver to a USB 2 port, or onto a short USB extension cable that puts it away from USB 3 devices and cables.",
             neighbor == null ? "Or move the USB 3 device to another port." : $"Or move {Topology.ShortName(neighbor)} to a port away from the receiver."]);
    }
}

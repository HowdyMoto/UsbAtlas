namespace UsbAtlas;

// Phones, headsets and other devices that share files over USB (MTP) report their battery to Windows Portable
// Devices (PortableDevices.cs). A battery that falls while its device stays plugged in is using power faster than
// the port supplies. That is seen, not worked out from specifications, so it's a warning. Devices round their level
// to whole percents and some wobble by one, so a fall of one point isn't counted.
internal static class Battery
{
    internal const string Draining = "Battery draining while plugged in";
    internal const int Fall = 2;

    internal static bool IsDraining(UsbNode n) => n.BatteryPercent is int now && n.BatteryHighPercent is int high && high - now >= Fall;

    // Properties' Battery row; empty when the device doesn't report one.
    internal static string Summary(UsbNode n) =>
        n.BatteryPercent is not int now ? ""
        : IsDraining(n) ? $"{now}% · down from {n.BatteryHighPercent}% since {n.BatteryHighAt:t}"
        : n.BatterySource == "External" ? $"{now}% · on external power" : $"{now}%";

    internal static string Evidence(UsbNode n) =>
        n.BatteryPercent is not int now ? ""
        : $"Battery: {now}%{(n.BatterySource.Length > 0 ? $", running on {n.BatterySource.ToLowerInvariant()} power" : "")}, as the device reports it through Windows Portable Devices."
            + (n.BatteryHighPercent is int high && n.BatteryHighAt is DateTime at ? $" Highest this session while connected: {high}% at {at:T}." : "");

    internal static Explanations.Explanation Explain(UsbNode n)
    {
        string cause = PowerNeeds.Ceiling(n) is (_, var why) ? (PowerNeeds.For(n) is PowerNeeds.Need need ? $"{need.Basis}. " : "") + why
            : "How much power this port gives isn't reported, but it's less than this device is using now.";
        return new($"Its battery has fallen from {n.BatteryHighPercent}% to {n.BatteryPercent}% since {n.BatteryHighAt:t} while plugged in here, so it's using power faster than this port supplies.",
            "Yes: while it's in use here it runs down instead of charging, and it will stop when it's empty.", cause,
            ["Plug it into its own charger, or a USB-C port on the computer that supports Power Delivery; the computer's manual says which ports do.",
             "Or use a cable or splitter that adds a charger while it stays connected."]);
    }
}

// Keeps each connected device's highest battery level this session, so one that falls shows. A device that
// disconnects starts afresh when it's back; a scan that couldn't read its battery keeps what's known.
internal sealed class BatteryTracker
{
    private readonly Dictionary<string, (int Percent, DateTime At)> high = new(StringComparer.OrdinalIgnoreCase);

    internal void Apply(Snapshot snapshot, DateTime now)
    {
        if (snapshot.IsDemo) return;
        var present = snapshot.Nodes.Where(n => n.InstanceId.Length > 0).Select(n => n.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in high.Keys.Where(k => !present.Contains(k)).ToList()) high.Remove(gone);
        foreach (var n in snapshot.Nodes.Where(n => n.BatteryPercent != null && n.InstanceId.Length > 0))
        {
            int percent = n.BatteryPercent!.Value;
            // The latest time it was at its highest, so "since" says how long it has been falling.
            if (!high.TryGetValue(n.InstanceId, out var top) || percent >= top.Percent) high[n.InstanceId] = top = (percent, now);
            (n.BatteryHighPercent, n.BatteryHighAt) = (top.Percent, top.At);
        }
    }
}

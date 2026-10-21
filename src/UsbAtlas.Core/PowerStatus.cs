using System.Runtime.InteropServices;

namespace UsbAtlas;

// Where the computer's power comes from now. A USB-C monitor that powers the laptop can drop its contract when
// the laptop draws more than it supplies, and everything on that cable goes with it: the picture, the hub and
// the battery's charging. Read-only.
internal static class PowerStatus
{
    // The battery as Windows reports it. Watts is the rate in or out, or null when the firmware doesn't say;
    // some report 0 while discharging, so it is a hint, not a measurement.
    internal sealed record Battery(bool OnAc, bool Charging, bool Discharging, int? Percent, double? Watts);

    // GUID_ACDC_POWER_SOURCE: the setting whose changes say the computer switched between AC and battery.
    internal static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    // The battery now, or null on a computer without one.
    internal static Battery? Read()
    {
        if (!OperatingSystem.IsWindows() || CallNtPowerInformation(5 /* SystemBatteryState */, IntPtr.Zero, 0, out var s, (uint)Marshal.SizeOf<BatteryState>()) != 0 || s.BatteryPresent == 0) return null;
        // Rate is in milliwatts; Windows reports an unknown rate as the lowest value.
        return new(s.AcOnLine != 0, s.Charging != 0, s.Discharging != 0,
            s.MaxCapacity == 0 ? null : (int)Math.Round(100.0 * s.RemainingCapacity / s.MaxCapacity),
            s.Rate == int.MinValue ? null : Math.Round(s.Rate / 1000.0, 1));
    }

    // "on AC, 22%, charging at 15 W", for a line of text.
    internal static string Describe(Battery b)
    {
        string flow = b.Watts is double w && (b.Charging || b.Discharging) ?$", {(b.Discharging ? "draining" : "charging")} at {Math.Abs(w):0.#} W" : "";
        return $"{(b.OnAc ? "on AC" : "on battery")}{(b.Percent is int p ? $", {p}%" : "")}{flow}";
    }

    // SYSTEM_BATTERY_STATE: four flags, four more bytes (the last a tag), then capacities and the rate in milliwatts.
    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryState
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate;
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }
    [DllImport("powrprof.dll")] private static extern int CallNtPowerInformation(int level, IntPtr input, uint inputLength, out BatteryState output, uint outputLength);
}

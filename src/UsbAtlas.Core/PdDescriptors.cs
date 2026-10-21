namespace UsbAtlas;

// The USB Power Delivery device capabilities a BOS descriptor can carry (USB PD 3.2 V1.1 §9.2, now the PDUSB Status
// Reporting spec §3): Power Delivery (0x06), Battery Info (0x07), PD Consumer Port (0x08) and PD Provider Port (0x09),
// and the power data objects (PDOs) a provider lists (PD 3.2 V1.2 §6.4.1.3). Offsets follow the spec. The Linux and
// Windows headers give 0x06 two more port bitmaps (18 bytes, not 14), so it's read by its length; and Linux's headers
// make bmCapabilities one byte where the spec and Windows make it two, so the spec's offsets are used.
internal static class PdDescriptors
{
    internal const byte PowerDelivery = 0x06, BatteryInfo = 0x07, ConsumerPort = 0x08, ProviderPort = 0x09;

    internal static string Name(byte cap) => cap switch
    {
        PowerDelivery => "Power Delivery", BatteryInfo => "Battery Info", ConsumerPort => "PD Consumer Port", ProviderPort => "PD Provider Port", _ => ""
    };

    // Each device capability in a BOS descriptor, in order. A bad length stops the walk.
    internal static IEnumerable<byte[]> Capabilities(byte[] bos)
    {
        if (bos.Length < 5 || bos[1] != 15) yield break;
        int limit = Math.Min(bos.Length, BitConverter.ToUInt16(bos, 2));
        for (int at = bos[0]; at + 3 <= limit;)
        {
            int length = bos[at];
            if (length < 3 || at + length > limit) yield break;
            if (bos[at + 1] == 16) yield return bos[at..(at + length)];
            at += length;
        }
    }

    // The most power a device says it runs on steadily, from its PD Consumer Port capability: dwMaxOperatingPower,
    // in 10 mW units at offset 12. Null when it carries none.
    internal static double? ConsumerWatts(byte[] bos) =>
        Capabilities(bos).FirstOrDefault(c => c[2] == ConsumerPort && c.Length >= 24) is byte[] c && BitConverter.ToUInt32(c, 12) is > 0 and < 100_000 and var power
            ? power / 100.0 : null;

    // How a port operates, from a consumer's or provider's bmCapabilities.
    internal static string PortModes(ushort bits) => string.Join(", ", new[] { (bits & 1) != 0 ? "Battery Charging" : null, (bits & 2) != 0 ? "USB PD" : null, (bits & 4) != 0 ? "USB Type-C Current" : null }.Where(x => x != null)) is { Length: > 0 } modes ? modes : "none";

    // A source PDO in words, and the most power it gives in watts.
    internal static (string Text, double Watts) SourcePdo(uint pdo)
    {
        static string V(double volts) => $"{volts:0.##} V";
        static string A(double amps) => $"{amps:0.##} A";
        switch (pdo >> 30)
        {
            case 0:
            {
                double volts = ((pdo >> 10) & 0x3FF) * 0.05, amps = (pdo & 0x3FF) * 0.01;
                return ($"Fixed {V(volts)} {A(amps)}", volts * amps);
            }
            case 1:
            {
                double max = ((pdo >> 20) & 0x3FF) * 0.05, min = ((pdo >> 10) & 0x3FF) * 0.05, watts = (pdo & 0x3FF) * 0.25;
                return ($"Battery {V(min)}–{V(max)} {watts:0.##} W", watts);
            }
            case 2:
            {
                double max = ((pdo >> 20) & 0x3FF) * 0.05, min = ((pdo >> 10) & 0x3FF) * 0.05, amps = (pdo & 0x3FF) * 0.01;
                return ($"Variable {V(min)}–{V(max)} {A(amps)}", max * amps);
            }
            default:
                switch ((pdo >> 28) & 3)
                {
                    case 0:
                    {
                        double max = ((pdo >> 17) & 0xFF) * 0.1, min = ((pdo >> 8) & 0xFF) * 0.1, amps = (pdo & 0x7F) * 0.05;
                        return ($"PPS {V(min)}–{V(max)} {A(amps)}", max * amps);
                    }
                    case 1:
                    {
                        double max = ((pdo >> 17) & 0x1FF) * 0.1, min = ((pdo >> 8) & 0xFF) * 0.1, watts = pdo & 0xFF;
                        return ($"EPR AVS {V(min)}–{V(max)} {watts:0} W", watts);
                    }
                    case 2:
                    {
                        double low = ((pdo >> 10) & 0x3FF) * 0.01, high = (pdo & 0x3FF) * 0.01;
                        return ($"SPR AVS 9–15 V {A(low)}{(high > 0 ? $", 15–20 V {A(high)}" : "")}", Math.Max(15 * low, 20 * high));
                    }
                    default:
                        return ($"Reserved APDO 0x{pdo:X8}", 0);
                }
        }
    }
}

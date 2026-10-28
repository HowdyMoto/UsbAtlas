using System.Globalization;
using System.IO;
using System.Reflection;

namespace UsbAtlas;

// How much power a device charges at, and whether the port it's in can give that. What a device needs comes from a
// short bundled list of makers' specifications (Assets/PowerNeeds/power-needs.tsv, whose header says what's taken
// and why), from the sink capabilities Linux reads from it under Power Delivery, or from its own Power Delivery
// descriptor (BOS PD Consumer Port capability), which few devices carry. What a port gives, Windows doesn't report,
// but its socket bounds it: a USB-A port gives 5 V only, which no charging scheme takes past 15 W, while a USB-C port
// can give far more under Power Delivery. On Linux a USB-C port's advertised current and Power Delivery offers are
// read, so there its bound is what it offers. A port that can't give what a device charges at is a note: nothing
// fails, but it charges slowly and can run down in use. A battery seen falling is a warning (Battery.cs).
internal static class PowerNeeds
{
    internal const string SlowCharging = "Charges slowly here";
    // 5 V at 3 A: the most USB-C's own current advertisement gives, and more than any 5 V charging scheme on USB-A.
    internal const double FiveVoltWatts = 15;

    // Product is the product string the device reports, or * for any; ProductId is * when the maker changes it with
    // what the device is doing, as a headset's changes between file sharing, Link and developer modes. Basis says
    // where the figure comes from, such as "Meta's figure, from a Power Delivery charger or port".
    internal sealed record Entry(string VendorId, string ProductId, string Product, double Watts, string Name, string Basis, string Source);

    internal static IReadOnlyList<Entry> Entries { get; } = Load();

    private static List<Entry> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbAtlas.power-needs.tsv")
            ?? throw new InvalidOperationException("Bundled power needs are missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    internal static List<Entry> Parse(TextReader reader)
    {
        var entries = new List<Entry>();
        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length != 7 || !Hex4(f[0]) || f[1] != "*" && !Hex4(f[1]) || f[2].Length == 0
                || !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var watts) || watts is <= 0 or > 240 || f[4].Length == 0 || f[5].Length == 0 || !f[6].StartsWith("https://", StringComparison.Ordinal))
                throw new FormatException("Power needs: can't read " + line);
            entries.Add(new(f[0].ToUpperInvariant(), f[1].ToUpperInvariant(), f[2], watts, f[4], f[5], f[6]));
        }
        return entries;
    }
    private static bool Hex4(string text) => text.Length == 4 && ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    internal static Entry? Listed(UsbNode n) =>
        n.Kind != "Device" || n.VendorId.Length == 0 ? null
        : Entries.FirstOrDefault(e => e.VendorId.Equals(n.VendorId, StringComparison.OrdinalIgnoreCase)
            && (e.ProductId == "*" || e.ProductId.Equals(n.ProductId, StringComparison.OrdinalIgnoreCase))
            && (e.Product == "*" || e.Product.Equals(n.ReportedProduct.Trim(), StringComparison.OrdinalIgnoreCase)));

    // What it needs, in a sentence that ends without a period, and where that comes from.
    internal sealed record Need(double Watts, string Basis, string Source);
    internal static Need? For(UsbNode n) =>
        n.Kind != "Device" ? null
        : Listed(n) is Entry e ? new(e.Watts, $"{e.Name} needs {W(e.Watts)} to charge at full speed ({e.Basis})", e.Source)
        : n.PdPartnerWatts is double asks ? new(asks, $"Under Power Delivery it asks the port for up to {W(asks)}", "its Power Delivery sink capabilities, as Linux reports them")
        : n.PdConsumerWatts is double runs ? new(runs, $"Its own descriptor says it runs on up to {W(runs)}", "its USB Power Delivery descriptor (BOS PD Consumer Port capability)")
        : null;

    // The socket as drawn: a connector the user set wins over what the firmware says.
    private static string Socket(UsbNode n) => n.SocketConnectorSet ?? n.Connector;

    // The most the port could give, and why: what Linux says it offers or advertises, or 5 V's limit on a USB-A port.
    // Null when it isn't known, as for a USB-C port on Windows.
    internal static (double Watts, string Why)? Ceiling(UsbNode n)
    {
        if (n.PdOfferWatts is double offer)
            return (offer, $"This USB-C port offers up to {W(offer)} under Power Delivery ({string.Join(", ", n.PdOffers)}), as Linux reports it.");
        if (n.TypeCPowerMode is "3.0A" or "1.5A" or "default")
        {
            double most = n.TypeCPowerMode switch { "3.0A" => 15, "1.5A" => 7.5, _ => 4.5 };
            return (most, $"This USB-C port isn't using Power Delivery; it advertises {(n.TypeCPowerMode == "default" ? "USB's default current" : n.TypeCPowerMode)} at 5 V, at most {W(most)}, as Linux reports it.");
        }
        if (Socket(n) == "USB-A")
            return (FiveVoltWatts, $"This is a USB-A port, which gives 5 V only: 4.5 W is all a USB 3 port is sure to give, a charging port gives up to 7.5 W, and none goes past about {W(FiveVoltWatts)}.");
        return null;
    }

    internal static bool SlowHere(UsbNode n) => For(n) is Need need && Ceiling(n) is (double most, _) && most < need.Watts;

    // Properties' Charging row: what it needs and what this port gives; empty when nothing says what it needs.
    internal static string Summary(UsbNode n) =>
        For(n) is not Need need ? ""
        : $"Needs up to {W(need.Watts)} · " + (Ceiling(n) is (double most, _) ? most < need.Watts ? $"port ≤{W(most)}" : $"port up to {W(most)}" : "port not reported");

    // Properties' Power available row, for any port: what Linux says it offers or advertises, or what its socket allows.
    internal static string Available(UsbNode n) =>
        n.PdOfferWatts is double offer ? $"Up to {W(offer)} · Power Delivery"
        : n.TypeCPowerMode switch
        {
            "3.0A" => "Up to 15 W · 5 V 3 A",
            "1.5A" => "Up to 7.5 W · 5 V 1.5 A",
            "default" => "USB default · 5 V",
            _ => Socket(n) switch { "USB-A" => $"5 V only · ≤{W(FiveVoltWatts)}", "USB-C" => "Not reported · USB-C", _ => "Unknown · not measured" }
        };

    // Detection details: what it needs and where that comes from, and what bounds the port.
    internal static List<string> Evidence(UsbNode n)
    {
        var lines = new List<string>();
        if (For(n) is Need need) lines.Add($"Charging: {need.Basis}. Source: {need.Source}.");
        if (n.PdOffers.Count > 0) lines.Add($"Power Delivery offers: {string.Join(", ", n.PdOffers)}, as Linux reports them. Linux shows what a port offers, not the contract it agreed with the device.");
        else if (n.TypeCPowerMode.Length > 0) lines.Add($"USB-C power mode: {n.TypeCPowerMode}, as Linux reports it.");
        if (n.PdConsumerWatts is double runs && Listed(n) != null) lines.Add($"Its own Power Delivery descriptor says it runs on up to {W(runs)}.");
        return lines;
    }

    internal static Explanations.Explanation Explain(UsbNode n)
    {
        var need = For(n)!;
        var (_, why) = Ceiling(n)!.Value;
        return new($"{need.Basis}. {why}",
            "Only its battery: it charges more slowly here than it can, and may run down while it's in use.", "",
            ["If it runs down while in use, plug it into its own charger, or a USB-C port on the computer that supports Power Delivery; the computer's manual says which ports do.",
             "Or use a cable or splitter that adds a charger while it stays connected.",
             "If it shares files over USB (MTP), USB Atlas reads its battery and warns when it falls while plugged in."]);
    }

    internal static string W(double watts) => $"{watts.ToString("0.#", CultureInfo.InvariantCulture)} W";
}

using System.Globalization;
using System.IO;
using System.Reflection;

namespace UsbAtlas;

// Offline community identifiers describe USB components, not necessarily retail enclosures.
internal sealed class UsbIdDatabase
{
    private readonly Dictionary<ushort, string> vendors = [];
    private readonly Dictionary<(ushort Vendor, ushort Product), string> products = [];
    internal static UsbIdDatabase Default { get; } = Load();
    private static UsbIdDatabase Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbAtlas.usb.ids")
            ?? throw new InvalidOperationException("Bundled USB ID database is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }
    internal static UsbIdDatabase Parse(TextReader reader)
    {
        var database = new UsbIdDatabase();
        ushort? vendor = null;
        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            if (line.StartsWith("\t\t")) continue; // Interface records are not product IDs.
            bool product = line.StartsWith('\t');
            string entry = product ? line[1..] : line;
            if (entry.Length < 6 || !char.IsWhiteSpace(entry[4]) || !ushort.TryParse(entry.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            {
                if (!product) vendor = null; // Class and other non-vendor sections.
                continue;
            }
            var name = entry[4..].Trim();
            if (name.Length == 0) continue;
            if (!product) { vendor = id; database.vendors[id] = name; }
            else if (vendor is ushort v) database.products[(v, id)] = name;
        }
        return database;
    }
    internal (string Vendor, string Product) Lookup(string vid, string pid)
    {
        if (!ushort.TryParse(vid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor)) return ("", "");
        var name = vendors.GetValueOrDefault(vendor, "");
        return ushort.TryParse(pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var product)
            ? (name, products.GetValueOrDefault((vendor, product), "")) : (name, "");
    }
}

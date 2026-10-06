using System.Globalization;
using System.IO;
using System.Reflection;

namespace UsbAtlas;

// Hub, dock and drive-enclosure chips with known problems, from the workarounds the Linux kernel applies to
// them (Assets/KnownProblems/known-problems.tsv, whose header says what's taken and why). Only problems that
// reach Windows too are listed: links that fail in low-power states, chips that don't come back from suspend,
// hubs whose ports need slower resets, and enclosures unreliable under UAS. An entry says what Linux does, not
// what Windows does, and plenty of these chips work fine, so it's raised as a note only where this node shows
// the problem's symptom; otherwise Properties mentions it. Linux already applies the workaround, so there it
// isn't raised at all.
internal static class KnownProblems
{
    internal const string Label = "Known chip problem";
    internal const string Lpm = "lpm", Resume = "resume", SlowReset = "slow-reset", NoUas = "no-uas";

    // Revisions are bcdDevice values; an entry for every revision runs from 0 to FFFF.
    internal sealed record Entry(string VendorId, string ProductId, ushort FirstRevision, ushort LastRevision, string Problem, string Name, string Source)
    {
        internal bool AllRevisions => FirstRevision == 0 && LastRevision == 0xFFFF;
    }

    internal static IReadOnlyList<Entry> Entries { get; } = Load();

    private static List<Entry> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbAtlas.known-problems.tsv")
            ?? throw new InvalidOperationException("Bundled known chip problems are missing.");
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
            if (f.Length != 6 || f[3] is not (Lpm or Resume or SlowReset or NoUas) || Revisions(f[2]) is not var (first, last))
                throw new FormatException("Known chip problems: can't read " + line);
            entries.Add(new(f[0].ToUpperInvariant(), f[1].ToUpperInvariant(), first, last, f[3], f[4], f[5]));
        }
        return entries;
    }

    // * for every revision, one such as 1.28, or a range such as 1.00-3.09.
    private static (ushort, ushort)? Revisions(string text)
    {
        if (text == "*") return (0, 0xFFFF);
        var ends = text.Split('-');
        return ends.Length is 1 or 2 && Revision(ends[0]) is ushort first && Revision(ends[^1]) is ushort last && first <= last ? (first, last) : null;
    }

    // A revision as Device revision shows it: bcdDevice 0x0128 reads 1.28.
    internal static ushort? Revision(string text) =>
        text.Split('.') is [var major, var minor] && minor.Length == 2
        && ushort.TryParse(major, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hi) && hi <= 0xFF
        && byte.TryParse(minor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lo) ? (ushort)(hi << 8 | lo) : null;

    // The entry for this hub or device, if its chip is listed. An entry for some revisions needs the revision read.
    internal static Entry? For(UsbNode n) =>
        n.Kind is not ("Device" or "Hub") || n.VendorId.Length == 0 ? null
        : Entries.FirstOrDefault(e => e.VendorId.Equals(n.VendorId, StringComparison.OrdinalIgnoreCase) && e.ProductId.Equals(n.ProductId, StringComparison.OrdinalIgnoreCase)
            && (e.AllRevisions || Revision(n.DeviceRevision) is ushort r && r >= e.FirstRevision && r <= e.LastRevision));

    // Linux avoids UAS with this drive, so bulk-only isn't a fault on it.
    internal static bool AvoidsUas(UsbNode n) => For(n)?.Problem == NoUas;

    // What here looks like the problem: drops, a slow link, or devices behind a hub that didn't connect.
    internal static List<string> Signs(UsbNode n, Entry e)
    {
        var signs = new List<string>();
        string dropped = n.QuickReconnects > 0 ? $"It dropped and came back {Times(n.QuickReconnects)} this session." : "";
        var droppedBehind = n.Children.Where(c => c.QuickReconnects > 0).ToList();
        switch (e.Problem)
        {
            case Lpm:
                if (dropped.Length > 0) signs.Add(dropped);
                if (n.Usb3SideMissing) signs.Add("Its USB 3 side didn't connect.");
                else if (HubRelationships.ReducedSpeed(n)) signs.Add("Its link is slower than it supports.");
                break;
            case Resume:
                if (dropped.Length > 0) signs.Add(dropped);
                if (droppedBehind.Count > 0) signs.Add($"{Names(droppedBehind)} behind it dropped and came back.");
                break;
            case SlowReset:
                foreach (var c in n.Children.Where(c => c.Kind == "Unavailable" && c.Status is "Enumeration failed" or "General failure"))
                    signs.Add($"The device on its port {c.Port} didn't connect.");
                if (droppedBehind.Count > 0) signs.Add($"{Names(droppedBehind)} behind it dropped and came back.");
                break;
            case NoUas when n.StorageProtocol == "UAS":
                if (dropped.Length > 0) signs.Add(dropped);
                if (n.DriverProblems.Count > 0) signs.Add("Windows reports a problem with it.");
                break;
        }
        return signs;
    }

    internal static bool Raised(UsbNode n) => !OperatingSystem.IsLinux() && For(n) is Entry e && Signs(n, e).Count > 0;

    internal static string Short(string problem) => problem switch
    {
        Lpm => "Link power management", Resume => "Resume from suspend", SlowReset => "Slow port reset", _ => "Unreliable with UAS"
    };

    // What the chip does, finishing "… has a known problem: ".
    private static string Problem(string problem) => problem switch
    {
        Lpm => "links to it can drop or fail when they enter USB's low-power link states, so Linux keeps them off for it.",
        Resume => "it doesn't always come back properly after being suspended to save power, so Linux resets it instead of resuming it.",
        SlowReset => "its ports need longer to reset than USB allows, so devices plugged into it can fail to connect; Linux waits longer for them.",
        _ => "it can return errors or stall under UAS, so Linux uses the older bulk-only protocol with it."
    };

    // Properties' Known problems row: none listed, or whether anything here shows the problem, then which it is.
    // Whether it shows leads, so a narrow panel cuts the problem's name rather than that.
    internal static string Summary(UsbNode n) =>
        For(n) is not Entry e ? "None listed"
        : $"{(OperatingSystem.IsLinux() ? "Linux works around it" : Signs(n, e).Count > 0 ? "Signs here" : "No signs here")} · {Short(e.Problem).ToLowerInvariant().Replace("uas", "UAS")}";

    // Detection details: what is listed and where it's recorded.
    internal static string Evidence(UsbNode n) =>
        For(n) is not Entry e ? ""
        : $"Known chip problem: {e.Name} ({e.VendorId}:{e.ProductId}{(e.AllRevisions ? "" : ", revision " + n.DeviceRevision)}): {Problem(e.Problem)} Recorded as a Linux kernel workaround: {e.Source}";

    internal static Explanations.Explanation Explain(UsbNode n)
    {
        var e = For(n)!;
        var steps = new List<string>();
        switch (e.Problem)
        {
            case Lpm:
                steps.Add("If your power plan lists USB 3 Link Power Management under USB settings (Power Options › Change advanced power settings), setting it to Off keeps every USB 3 link out of these states.");
                steps.Add(n.Kind == "Hub" ? "Or plug what's behind it into another hub or a port on the computer." : "Or plug it into another port.");
                break;
            case Resume:
                steps.Add("Turn off “Allow the computer to turn off this device to save power” on its Power Management tab in Device Manager, so Windows doesn't suspend it.");
                break;
            case SlowReset:
                steps.Add("Unplug a device that didn't connect and plug it in again; it often connects on a second try.");
                steps.Add("If it keeps failing, plug it into another hub or a port on the computer.");
                break;
            default:
                steps.Add("To make Windows use bulk-only with it: in Device Manager, open the drive's USB Attached SCSI (UAS) Mass Storage Device under Storage controllers, choose Update driver › Browse my computer › Let me pick, and pick USB Mass Storage Device.");
                break;
        }
        steps.Add($"Check its maker for a firmware update{(n.DeviceRevision.Length > 0 ? $"; its revision here is {n.DeviceRevision}" : "")}.");
        return new($"{e.Name} has a known problem: {Problem(e.Problem)} {string.Join(" ", Signs(n, e))}",
            "Maybe: a cable, the power supply or the device itself can do the same, so this is a lead, not a diagnosis.",
            "The problem is recorded as a workaround the Linux kernel applies to this chip, not by its maker, and Windows may handle it differently.", steps);
    }

    private static string Times(int count) => count == 1 ? "once" : count == 2 ? "twice" : $"{count} times";
    private static string Names(List<UsbNode> nodes) => nodes.Count == 1 ? Topology.ShortName(nodes[0]) : $"{nodes.Count} devices";
}

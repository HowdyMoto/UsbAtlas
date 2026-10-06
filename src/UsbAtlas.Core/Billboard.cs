namespace UsbAtlas;

// What a USB-C device's Billboard says about its alternate modes, such as DisplayPort over USB-C: which it
// offers and, for each, whether it was entered, failed or never asked for, and why one failed. A device
// shows a Billboard when an alternate mode doesn't start, and some show one all the time.
public sealed class BillboardInfo
{
    public string Version { get; set; } = "";
    public string AdditionalInfoUrl { get; set; } = "";
    // Index of the mode the device would rather use.
    public int PreferredMode { get; set; }
    // VCONN power the device needs, such as "1 W", or "Not required".
    public string VconnPower { get; set; } = "";
    // bAdditionalFailureInfo: not enough power from the USB-C port, or USB Power Delivery failed. Billboard 1.0 has neither.
    public bool InsufficientPower { get; set; }
    public bool PowerDeliveryFailed { get; set; }
    public List<AlternateMode> Modes { get; set; } = [];
}
public sealed class AlternateMode
{
    // The standard or vendor ID (SVID) the mode belongs to, as four hex digits, such as FF01 for DisplayPort.
    public string Svid { get; set; } = "";
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public string Description { get; set; } = "";
    // Entered, Not entered (not attempted, or exited), Failed (attempted, not entered), or Error (unspecified).
    public string State { get; set; } = "";
    // Billboard Ex: the mode's VDO, its capabilities as Power Delivery reports them, as eight hex digits.
    public string Vdo { get; set; } = "";
}

internal static class Billboard
{
    internal const string Failed = "Alternate mode failed";
    internal const string NotEntered = "Alternate mode not entered";
    internal const byte Class = 0x11;

    // The Billboard capability (0x0D) and Billboard Ex (0x0F) in a BOS descriptor, with string indexes in
    // place of strings: iAdditionalInfoURL and each mode's iAlternateModeString, for the scanner to read.
    internal sealed record Decoded(BillboardInfo Info, byte UrlString, List<byte> ModeStrings);

    internal static Decoded? Decode(byte[] bos)
    {
        Decoded? result = null;
        var vdos = new List<(int Index, uint Vdo)>();
        for (int at = bos.Length >= 5 && bos[1] == 15 ? bos[0] : 0; at + 3 <= bos.Length;)
        {
            int length = bos[at];
            if (length < 3 || at + length > bos.Length) break;
            if (bos[at + 1] == 16)
            {
                var cap = bos.AsSpan(at, length);
                if (cap[2] == 0x0D && result == null) result = Capability(cap);
                else if (cap[2] == 0x0F && length >= 8) vdos.Add((cap[3], BitConverter.ToUInt32(cap[4..8])));
            }
            at += length;
        }
        if (result != null)
            foreach (var (index, vdo) in vdos)
                if (result.Info.Modes.FirstOrDefault(m => m.Index == index) is AlternateMode mode) mode.Vdo = vdo.ToString("X8");
        return result;
    }

    // Offsets follow the Billboard Device Class specification. Billboard 1.0 has no failure info, so its
    // modes start at 42 instead of 44; the descriptor's length says which it is.
    private static Decoded? Capability(ReadOnlySpan<byte> d)
    {
        if (d.Length < 42) return null;
        int count = d[4];
        int start = d.Length == 42 + 4 * count ? 42 : 44;
        if (d.Length < start + 4 * count) return null;
        ushort vconn = BitConverter.ToUInt16(d[6..8]);
        var info = new BillboardInfo
        {
            PreferredMode = d[5],
            VconnPower = (vconn & 0x8000) != 0 ? "Not required" : (vconn & 7) switch { 0 => "1 W", 1 => "1.5 W", 7 => "Reserved", var w => $"{w} W" },
            Version = UsbScanner.Bcd(BitConverter.ToUInt16(d[40..42])),
            InsufficientPower = start == 44 && (d[42] & 1) != 0,
            PowerDeliveryFailed = start == 44 && (d[42] & 2) != 0
        };
        var strings = new List<byte>();
        for (int i = 0; i < count; i++)
        {
            int at = start + 4 * i;
            ushort svid = BitConverter.ToUInt16(d[at..(at + 2)]);
            int bits = (d[8 + i / 4] >> (2 * (i % 4))) & 3;
            info.Modes.Add(new AlternateMode
            {
                Svid = svid.ToString("X4"), Name = SvidName(svid), Index = d[at + 2],
                State = bits switch { 3 => "Entered", 2 => "Failed", 1 => "Not entered", _ => "Error" }
            });
            strings.Add(d[at + 3]);
        }
        return new(info, d[3], strings);
    }

    // DisplayPort and Thunderbolt have SVIDs of their own; any other SVID is a USB vendor ID.
    internal static string SvidName(ushort svid) => svid switch
    {
        0xFF01 => "DisplayPort", 0x8087 => "Thunderbolt",
        _ => UsbIdDatabase.Default.Lookup(svid.ToString("X4"), "").Vendor is { Length: > 0 } vendor ? $"{Topology.ShortName(new UsbNode { Name = vendor })} mode" : $"Vendor mode {svid:X4}"
    };

    internal static bool IsBillboard(UsbNode n) => n.Billboard != null;
    internal static List<AlternateMode> FailedModes(UsbNode n) => n.Billboard?.Modes.Where(m => m.State is "Failed" or "Error").ToList() ?? [];
    // One mode at a time can be entered, so the others not being entered is normal; none entered and none
    // failed means the computer never asked.
    internal static bool NoneEntered(UsbNode n) => n.Billboard is { Modes.Count: > 0 } b && FailedModes(n).Count == 0 && b.Modes.All(m => m.State == "Not entered");

    // "DisplayPort · entered", one per mode, for Properties and search.
    internal static string Summary(UsbNode n) => n.Billboard == null ? "" : string.Join(", ", n.Billboard.Modes.Select(m => $"{m.Name} · {m.State.ToLowerInvariant()}"));

    internal static Explanations.Explanation Explain(UsbNode n, string issue)
    {
        var b = n.Billboard!;
        var modes = issue == Failed ? FailedModes(n) : b.Modes;
        string names = string.Join(" or ", modes.Select(m => m.Name).Distinct());
        bool video = modes.Any(m => m.Name is "DisplayPort" or "Thunderbolt");
        string carries = modes.All(m => m.Name == "DisplayPort") ? "the picture" : modes.All(m => m.Name == "Thunderbolt") ? "Thunderbolt" : "that feature";
        var steps = new List<string>();
        if (b.InsufficientPower) steps.Add("Plug in the device's own power adapter, or connect it to a USB-C port that supplies more power, such as one marked with a charging symbol.");
        if (b.PowerDeliveryFailed) steps.Add("Unplug both ends of the cable, wait a few seconds and plug them back in.");
        steps.Add("Use a cable that carries video: a full-featured USB-C cable, or the one that came with it. Many USB-C charging cables carry only USB 2.");
        if (video) steps.Add($"Plug it straight into a USB-C port on the computer marked for {(modes.Any(m => m.Name == "Thunderbolt") ? "Thunderbolt (a lightning bolt)" : "DisplayPort (a DP or D logo) or Thunderbolt (a lightning bolt)")}. Not every USB-C port carries video, and hubs and adapters in between may not pass it on.");
        if (LooksLikeUrl(b.AdditionalInfoUrl)) steps.Add($"The maker's help page: {b.AdditionalInfoUrl}");
        if (issue == Failed)
        {
            string cause = b.InsufficientPower ? "It reports that it didn't get enough power over USB-C to start it."
                : b.PowerDeliveryFailed ? "It reports that the USB Power Delivery exchange that starts it failed."
                : modes.All(m => m.State == "Error") ? "It reports an error without saying what failed." : "";
            return new($"This USB-C device offers {names} over its USB-C connection, and starting it failed.",
                $"Yes: {carries} doesn't come through this connection; only its USB functions work.", cause, steps);
        }
        steps.Add($"If you use it only for USB, there's nothing to do.");
        return new($"This USB-C device offers {names} over its USB-C connection, but the computer didn't ask for it.",
            $"Maybe: if you expect {carries} through this cable, it won't come.", "The port it's connected through may not carry it, or the cable may not.", steps);
    }

    // Each mode with its SVID, string, state and VDO, then what the device says about power, for Detection details.
    internal static List<string> Evidence(UsbNode n)
    {
        if (n.Billboard is not BillboardInfo b) return [];
        var lines = b.Modes.Select(m => $"Alternate mode {m.Index}: {m.Name} (SVID {m.Svid}){(m.Description.Length > 0 ? $", “{m.Description}”" : "")}: {m.State.ToLowerInvariant()}{(m.Vdo.Length > 0 ? $"; VDO {m.Vdo}" : "")}{(b.Modes.Count > 1 && m.Index == b.PreferredMode ? "; preferred" : "")}.").ToList();
        var power = new List<string>();
        if (b.InsufficientPower) power.Add("an alternate mode failed for lack of power");
        if (b.PowerDeliveryFailed) power.Add("USB Power Delivery communication failed");
        lines.Add($"Billboard {b.Version}: VCONN power {b.VconnPower}{(power.Count > 0 ? "; it reports that " + string.Join(" and ", power) : "")}.");
        if (b.AdditionalInfoUrl.Length > 0) lines.Add((LooksLikeUrl(b.AdditionalInfoUrl) ? "More information from its maker: " : "Its additional-information string: ") + b.AdditionalInfoUrl);
        return lines;
    }
    // Devices are meant to put a web address here, and some put their maker's name instead.
    internal static bool LooksLikeUrl(string text) => text.Contains("://") || System.Text.RegularExpressions.Regex.IsMatch(text, @"^[\w-]+(\.[\w-]+)+(/\S*)?$");
}

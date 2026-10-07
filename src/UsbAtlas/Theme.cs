using System.IO;
using System.Windows;
using System.Windows.Media;

namespace UsbAtlas;

internal static class Theme
{
    public static bool IsDark { get; private set; }
    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbAtlas", "appearance.txt");
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("TextPrimary", "#26313A", "#E4E8EE"), ("TextSecondary", "#596673", "#BAC3CE"),
        ("TextMuted", "#727982", "#9BA6B3"), ("Surface", "#FFFFFF", "#1C2026"),
        ("WindowSurface", "#FAFAF8", "#16191E"), ("CanvasSurface", "#E9EBE9", "#0D0F12"),
        ("HostSurface", "#EAEEEF", "#202731"), ("HostBorder", "#DCE1E3", "#303A47"),
        ("Border", "#CED3D7", "#3A414B"), ("Divider", "#E1E4E7", "#2A3038"),
        // The search field is recessed below the title bar it sits on, and its edge keeps 3:1 against the bar.
        ("SearchField", "#EEF1F4", "#111317"), ("SearchEdge", "#7F8B97", "#6B7A8D"),
        ("Hover", "#E9EDF2", "#303B49"), ("Pressed", "#DCE4EE", "#3A485B"),
        // Selection, search matches, the selected path and focus are teal, a hue nothing else on the canvas uses:
        // USB's socket colors keep blue and red, device categories hold the rest of the cool hues, and warm hues
        // are for warnings and errors. --verify-ui keeps it at least 25° (OKLab) from every one of them.
        ("Accent", "#0F6E74", "#1FC2CF"), ("Selection", "#EEF5F5", "#1E3840"), ("SelectionStrong", "#D6E6E7", "#1E4B54"), ("OnAccent", "#FFFFFF", "#14181E"),
        ("Primary", "#253448", "#3E597B"), ("PrimaryHover", "#3D516B", "#4D6B92"),
        ("Focus", "#2E8A90", "#7FDDE4"), ("BorderHover", "#93A5B7", "#7B91AD"),
        ("Subtle", "#FAFBFC", "#242B34"), ("Wire", "#B8C0C5", "#596575"),
        ("WireDot", "#929FA8", "#8B9AAF"), ("Success", "#4E7B65", "#92C5A7"),
        // Device categories are icon and label inks spread across the cool half of the wheel, so they never
        // resemble Warning or Error. Cards are all NeutralFill, so every ink keeps at least 4.5:1 contrast on
        // it. Hubs, hosts, ports and devices of unknown function are Neutral.
        ("Neutral", "#5F6874", "#A7B0BC"), ("NeutralFill", "#FFFFFF", "#2A3038"), ("NeutralEdge", "#BCC3CA", "#4A5460"),
        ("Input", "#3F4FB0", "#9FA5F5"), ("Gaming", "#7A3FA8", "#C9A0EE"), ("Audio", "#9E2F80", "#E79AD2"),
        ("Video", "#1D6A91", "#84C3EA"), ("Storage", "#367026", "#9BD088"), ("Connectivity", "#177257", "#7DD3AE"),
        // USB's own socket color code, on the tongue inside each port: black for USB 2, blue for SuperSpeed
        // USB 3, red for 10 Gb/s and faster, gray when unreported. Port numbers sit on it in OnSocket, at
        // least 4.5:1. A tongue never carries a glyph or words, and Error is a deeper red that always sits on
        // its tinted surface beside a glyph, so the tongue's red can't be mistaken for an error.
        ("SocketUsb2", "#202428", "#0C0E11"), ("SocketSuperSpeed", "#1A5FC7", "#2D6FD6"),
        ("SocketSuperSpeedPlus", "#C8283A", "#D23A4B"), ("SocketUnknown", "#6A737D", "#5D6773"), ("OnSocket", "#FFFFFF", "#FFFFFF"),
        // A note is worth knowing but affects nothing now: calm gray, always with the info glyph.
        ("Note", "#596673", "#BAC3CE"), ("NoteSurface", "#ECEFF2", "#29313B"),
        // Reserved for warnings and errors: text and badges always carry a status glyph, and the only other
        // amber is a slow link's dashed connection. No role color comes near them.
        ("Warning", "#9A5B00", "#F5B544"), ("WarningSurface", "#FDF0D5", "#3B2D12"),
        ("Error", "#8E1A1A", "#FF7A6E"), ("ErrorSurface", "#FBE3E1", "#43201F")
    ];
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    // A palette color in either theme, without applying it.
    internal static Color Of(string key, bool dark) => Palette.Single(p => p.Key == key) is var (_, light, night) ? (Color)ColorConverter.ConvertFromString(dark ? night : light) : default;
    public static void Initialize(string[] args)
    {
        bool dark = false;
        try { dark = File.Exists(PreferencePath) && File.ReadAllText(PreferencePath).Trim() == "dark"; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (args.Contains("--dark")) dark = true;
        if (args.Contains("--light")) dark = false;
        Apply(dark);
    }
    public static void Apply(bool dark)
    {
        IsDark = dark;
        foreach (var (key, light, night) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze(); Application.Current.Resources[key] = brush;
        }
    }
    public static bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, IsDark ? "dark" : "light"); return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

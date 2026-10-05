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
        ("TextMuted", "#727982", "#9BA6B3"), ("Surface", "#FFFFFF", "#1E232B"),
        ("WindowSurface", "#FAFAF8", "#181C22"), ("CanvasSurface", "#F3F4F2", "#14181E"),
        ("HostSurface", "#EAEEEF", "#202731"), ("HostBorder", "#DCE1E3", "#303A47"),
        ("Border", "#D5D9DC", "#3A4451"), ("Divider", "#E5E8EB", "#303843"),
        ("Hover", "#E9EDF2", "#303B49"), ("Pressed", "#DCE4EE", "#3A485B"),
        ("Accent", "#4772B2", "#86ADE5"), ("Selection", "#F2F6FC", "#27364C"), ("SelectionStrong", "#DCE6F4", "#304A6E"), ("OnAccent", "#FFFFFF", "#14181E"),
        ("Primary", "#253448", "#3E597B"), ("PrimaryHover", "#3D516B", "#4D6B92"),
        ("Focus", "#7097CF", "#ABC8EF"), ("BorderHover", "#93A5B7", "#7B91AD"),
        ("Subtle", "#FAFBFC", "#242B34"), ("Wire", "#B8C0C5", "#596575"),
        ("WireDot", "#929FA8", "#8B9AAF"), ("Success", "#4E7B65", "#92C5A7"),
        // Device categories are icon and label inks spread across the cool half of the wheel, so they never
        // resemble Warning or Error. Cards are all NeutralFill, so every ink keeps at least 4.5:1 contrast on
        // it. Hubs, hosts, ports and devices of unknown function are Neutral.
        ("Neutral", "#5F6874", "#A7B0BC"), ("NeutralFill", "#FFFFFF", "#1E232B"), ("NeutralEdge", "#D5D9DC", "#3A4451"),
        ("Input", "#3F4FB0", "#9FA5F5"), ("Gaming", "#7A3FA8", "#C9A0EE"), ("Audio", "#9E2F80", "#E79AD2"),
        ("Video", "#1D6A91", "#84C3EA"), ("Storage", "#367026", "#9BD088"), ("Connectivity", "#177257", "#7DD3AE"),
        // USB's own socket color code, on the tongue inside each port: black for USB 2, blue for SuperSpeed
        // USB 3, red for 10 Gb/s and faster, gray when unreported. Port numbers sit on it in OnSocket, at
        // least 4.5:1. A tongue never carries a glyph or words, so its red can't be mistaken for an error.
        ("SocketUsb2", "#202428", "#0C0E11"), ("SocketSuperSpeed", "#1A5FC7", "#2D6FD6"),
        ("SocketSuperSpeedPlus", "#C8283A", "#D23A4B"), ("SocketUnknown", "#6A737D", "#5D6773"), ("OnSocket", "#FFFFFF", "#FFFFFF"),
        // A note is worth knowing but affects nothing now: calm gray, always with the info glyph.
        ("Note", "#596673", "#BAC3CE"), ("NoteSurface", "#ECEFF2", "#29313B"),
        // Reserved for warnings and errors: text and badges always carry a status glyph, and the only other
        // amber is a slow link's dashed connection. No role color comes near them.
        ("Warning", "#9A5B00", "#F5B544"), ("WarningSurface", "#FDF0D5", "#3B2D12"),
        ("Error", "#B42318", "#FF7A6E"), ("ErrorSurface", "#FDE7E5", "#43201F")
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

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
        ("Accent", "#4772B2", "#86ADE5"), ("Selection", "#F2F6FC", "#27364C"), ("SelectionStrong", "#DCE6F4", "#304A6E"),
        ("Primary", "#253448", "#3E597B"), ("PrimaryHover", "#3D516B", "#4D6B92"),
        ("Focus", "#7097CF", "#ABC8EF"), ("BorderHover", "#93A5B7", "#7B91AD"),
        ("Subtle", "#FAFBFC", "#242B34"), ("Wire", "#B8C0C5", "#596575"),
        ("WireDot", "#929FA8", "#8B9AAF"), ("HostRole", "#586A80", "#A6B8CE"),
        ("HubRole", "#6B58A0", "#B5A6E3"), ("DeviceRole", "#466F69", "#8ABCB0"),
        ("UnknownRole", "#656D75", "#A2ADB9"), ("Success", "#4E7B65", "#92C5A7"),
        // Card fills and outlines in each role's hue, so a card's kind reads before its label does.
        // Role labels keep at least 4.5:1 contrast on their own fill; unknown stays neutral.
        ("HostRoleFill", "#E6ECF7", "#26303E"), ("HostRoleEdge", "#C3CEDF", "#3D4B60"),
        ("HubRoleFill", "#EEE8FA", "#302A45"), ("HubRoleEdge", "#CFC3EA", "#4B4168"),
        ("DeviceRoleFill", "#E2F3EE", "#1F3733"), ("DeviceRoleEdge", "#B5D9CF", "#2F5550"),
        ("UnknownRoleFill", "#FFFFFF", "#1E232B"), ("UnknownRoleEdge", "#D5D9DC", "#3A4451"),
        // Reserved for warnings and errors, always with a status glyph; no role color comes near them.
        ("Warning", "#9A5B00", "#F5B544"), ("WarningSurface", "#FDF0D5", "#3B2D12"),
        ("Error", "#B42318", "#FF7A6E"), ("ErrorSurface", "#FDE7E5", "#43201F"),
        ("StatusInk", "#FFFFFF", "#15181D")
    ];
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
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

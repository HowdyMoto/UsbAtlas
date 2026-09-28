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
        ("Accent", "#4772B2", "#86ADE5"), ("Selection", "#F2F6FC", "#27364C"),
        ("Primary", "#253448", "#3E597B"), ("PrimaryHover", "#3D516B", "#4D6B92"),
        ("Focus", "#7097CF", "#ABC8EF"), ("BorderHover", "#93A5B7", "#7B91AD"),
        ("Subtle", "#FAFBFC", "#242B34"), ("Wire", "#B8C0C5", "#596575"),
        ("WireDot", "#929FA8", "#8B9AAF"), ("HostRole", "#586A80", "#A6B8CE"),
        ("HubRole", "#98713E", "#D9B17A"), ("DeviceRole", "#4E7B74", "#8ABCB0"),
        ("UnknownRole", "#737B83", "#A2ADB9"), ("Error", "#B6453E", "#F29C95"),
        ("Warning", "#946526", "#DCB37C"), ("Success", "#4E7B65", "#92C5A7")
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

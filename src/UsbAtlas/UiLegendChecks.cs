using System.Windows.Controls;

namespace UsbAtlas;
public partial class MainWindow
{
    // Blue means 5 Gb/s or faster, since a motherboard's 10 Gb/s socket stays blue until something links that
    // fast or its speed is set, and each socket entry says what its color or shape means (#24).
    private void VerifyLegendWording()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        List<StackPanel> Entries(string group) => SocketLegend.Children.OfType<StackPanel>().First(g => g.Children.OfType<TextBlock>().FirstOrDefault()?.Text == group).Children.OfType<StackPanel>().ToList();
        var speed = Entries("Socket speed");
        Check(speed.Select(e => e.Children.OfType<TextBlock>().Last().Text).SequenceEqual(["USB 2", "5 Gb/s+", "10 Gb/s+"]), "The legend calls blue sockets 5 Gb/s+.");
        Check(speed[1].ToolTip is string blue && blue.Contains("stays blue until a device links to it that fast") && blue.Contains("set its speed")
            && speed[2].ToolTip is string red && red.Contains("A device has linked here that fast") && red.Contains("set the socket's speed"),
            "The blue and red entries must say what turns a socket red, including setting its speed.");
        Check(speed.Concat(Entries("Socket")).All(e => e.ToolTip is string { Length: > 0 }), "Every socket legend entry explains itself.");
    }
}

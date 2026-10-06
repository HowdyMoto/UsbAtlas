using System.Windows.Controls;

namespace UsbAtlas;
public partial class MainWindow
{
    // Blue means 5 Gb/s or faster, since a motherboard's 10 Gb/s socket stays blue until something links that
    // fast, and each speed entry says what turns a socket red (#24).
    private void VerifyLegendWording()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var speed = SocketLegend.Children.OfType<StackPanel>().First(g => g.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "Speed");
        var entries = speed.Children.OfType<StackPanel>().ToList();
        var labels = entries.Select(e => e.Children.OfType<TextBlock>().Last().Text).ToList();
        Check(labels.SequenceEqual(["USB 2", "5 Gb/s+", "10 Gb/s+"]), "The legend calls blue sockets 5 Gb/s+.");
        Check(entries.All(e => e.ToolTip is string { Length: > 0 }) && ((string)entries[1].ToolTip).Contains("stays blue until a device links to it at 10 Gb/s") && ((string)entries[2].ToolTip).Contains("once a device here links that fast"),
            "Each speed entry says what its color means and what turns a socket red.");
    }
}

using System.Windows.Controls;

namespace UsbAtlas;
public partial class MainWindow
{
    // A game controller Windows remembers on other ports lists them in Properties, carries the note, and the
    // note goes when the entries do.
    private void VerifyRememberedDevices()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var wheel = snapshot.Nodes.First(n => n.DeviceType == "Game controller");
        var (vendor, product, instance) = (wheel.VendorId, wheel.ProductId, wheel.InstanceId);
        try
        {
            (wheel.VendorId, wheel.ProductId, wheel.InstanceId) = ("346E", "0004", @"USB\VID_346E&PID_0004\6&1&0&9");
            snapshot.Remembered = [new() { InstanceId = @"USB\VID_346E&PID_0004\6&1&0&2", Name = "Wheel base", VendorId = "346E", ProductId = "0004", LocationInfo = "Port_#0002.Hub_#0001", LastConnected = new DateTime(2026, 9, 1, 12, 0, 0) }];
            Remembered.Analyze(snapshot); UpdateIssues(); SelectNode(wheel); ShowDetails(); UpdateLayout();
            var texts = Details.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(Issue(wheel).Contains(Remembered.OtherPorts) && texts.Contains("Other entries for this device") && texts.Any(t => t.StartsWith("Port_#0002.Hub_#0001 (on a hub not connected now) · last connected")),
                "A game controller remembered on other ports lists them in Properties and carries the note.");
            CaptureUi("remembered-preview.png");
        }
        finally
        {
            (wheel.VendorId, wheel.ProductId, wheel.InstanceId) = (vendor, product, instance);
            snapshot.Remembered = null; Remembered.Analyze(snapshot); UpdateIssues(); ShowDetails();
        }
        Check(!Issue(wheel).Contains(Remembered.OtherPorts) && wheel.OtherEntries.Count == 0, "The note goes when the entries do.");
    }
}

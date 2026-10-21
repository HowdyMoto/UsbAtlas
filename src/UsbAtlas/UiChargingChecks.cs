using System.Windows.Controls;

namespace UsbAtlas;
public partial class MainWindow
{
    // A headset that needs 18 W on a USB-A port, whose battery is falling: Properties says what it needs, what the
    // port gives and the battery's fall, Detection details names the source, and the warning replaces the note.
    private void VerifyCharging()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string FieldText(string label) => Details.Children.OfType<Grid>().Where(g => Equals(g.Tag, "field"))
            .Select(g => g.Children.OfType<TextBlock>().ToList()).FirstOrDefault(t => t.Count == 2 && t[0].Text == label)?[1].Text ?? "";
        List<string> Evidence() => Details.Children.OfType<Expander>().FirstOrDefault(x => Equals(x.Header, "Detection details"))?.Content is StackPanel p
            ? p.Children.OfType<TextBlock>().Select(t => t.Text).ToList() : [];
        var device = snapshot.Nodes.First(n => n.Kind == "Device" && n.Connector == "USB-A" && n.SocketConnectorSet == null);
        var saved = (device.VendorId, device.ProductId, device.ReportedProduct, device.BatteryPercent, device.BatteryHighPercent, device.BatteryHighAt);
        var at = new DateTime(2026, 10, 19, 20, 0, 0);
        try
        {
            (device.VendorId, device.ProductId, device.ReportedProduct) = ("2833", "5011", "Quest 3");
            UpdateIssues(); SelectNode(device); ShowDetails(); UpdateLayout();
            Check(FieldText("Charging") == "Needs up to 18 W · port ≤15 W" && FieldText("Power available") == "5 V only · ≤15 W" && FieldText("Battery") == "Not reported"
                && Issue(device).Contains(PowerNeeds.SlowCharging) && Evidence().Any(t => t.StartsWith("Charging: Meta Quest 3 needs 18 W") && t.Contains("meta.com")),
                "A headset that needs more than a USB-A port gives says so in Properties and carries the note.");
            (device.BatteryPercent, device.BatteryHighPercent, device.BatteryHighAt) = (78, 80, at);
            UpdateIssues(); ShowDetails(); UpdateLayout();
            Check(FieldText("Battery") == $"78% · down from 80% since {at:t}" && Issue(device).Contains(Battery.Draining) && !Issue(device).Contains(PowerNeeds.SlowCharging)
                && Evidence().Any(t => t.StartsWith("Battery: 78%")), "A falling battery is a warning that replaces the note, and Properties shows the fall.");
            CaptureUi("charging-preview.png");
        }
        finally
        {
            (device.VendorId, device.ProductId, device.ReportedProduct, device.BatteryPercent, device.BatteryHighPercent, device.BatteryHighAt) = saved;
            UpdateIssues(); ShowDetails(); UpdateLayout();
        }
        Check(FieldText("Charging") == "Not listed" && !Issue(device).Contains(Battery.Draining), "A device that isn't listed shows none and carries no warning.");
    }
}

using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    // Ports and devices are renamed on the canvas, port names ride on card borders, and a hub that runs
    // on its own supply says so instead of showing 0 mA.
    private void VerifyCanvasNaming()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected; var savedLabels = deviceLabels;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsbAtlas-naming-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            snapshot = DemoData.Create(); deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json"));
            Draw(); UpdateLayout();
            UsbNode Node(string id) => snapshot.Nodes.Single(n => n.Id == id);
            static string Text(DependencyObject root) => string.Concat(VisualDescendants(root).OfType<TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text + " "));
            var hub = Node("demo/root/1"); var camera = Node("demo/root/1/2");
            Check(PowerFigure(hub).Text == "External power" && !Text(cards[hub.Id].Card).Contains("0 mA"), "A self-powered hub must read External power, not 0 mA.");
            Check(PowerFigure(camera).Text == "400 mA" && Text(cards[camera.Id].Card).Contains("400 mA"), "A bus-powered device keeps its requested current.");
            Check(PowerFigure(new UsbNode { Kind = "Hub", PowerSource = "Self powered", MaxPowerMa = 100 }).Text == "External + 100 mA", "External power plus a small bus draw must show both.");
            Check(PowerFigure(new UsbNode { Kind = "Device", MaxPowerMa = null }).Text == "Unknown", "An unreported request stays Unknown.");
            void Rename(Action open, string label, string value)
            {
                open(); var panel = (StackPanel)((Border)openNameEditor!.Child).Child;
                panel.Children.OfType<TextBox>().Single().Text = value;
                panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UpdateLayout();
            }
            List<Border> Tags() => Graph.Children.OfType<Border>().Where(b => Equals(b.Tag, PortTagMarker)).ToList();
            Check(Tags().Count == 0, "Unnamed ports must not draw tags.");
            Rename(() => EditPortName(Node("demo/root/1/2"), connectedPorts["demo/root/1/2"]), "Save", "Front left");
            Rename(() => EditPortName(Node("demo/root/1/3"), portSlots["demo/root/1/3"]), "Save", "Spare");
            var tags = Tags();
            var front = tags.FirstOrDefault(t => Text(t).Contains("Front left"));
            Check(tags.Count == 2 && front != null && tags.Any(t => Text(t).Contains("Spare")), "Named occupied and empty ports must each carry a tag.");
            Check(tags.All(t => t.ActualHeight <= 16), "Port tags must stay tiny.");
            var card = cards[camera.Id].Card;
            Check(Math.Abs(Canvas.GetTop(front!) - (Canvas.GetTop(card) - 8)) < 0.5 && Canvas.GetLeft(front!) >= Canvas.GetLeft(card), "An occupied port's tag must sit on its card's top border.");
            CaptureUi("port-name-tags-preview.png");
            Rename(() => EditDeviceName(camera, cards[camera.Id].Card), "Save", "Camera A");
            Check(Node(camera.Id).UserLabel == "Camera A" && Text(cards[camera.Id].Card).Contains("Camera A"), "Renaming a device on the canvas must reach its card.");
            Rename(() => EditPortName(Node("demo/root/1/2"), connectedPorts["demo/root/1/2"]), "Reset", "");
            Check(Tags().Count == 1, "Resetting a port name must remove its tag.");
        }
        finally
        {
            snapshot = savedSnapshot; selected = savedSelection; deviceLabels = savedLabels;
            Draw(); ShowDetails(); UpdateIssues();
            System.IO.Directory.Delete(directory, true);
        }
    }
}

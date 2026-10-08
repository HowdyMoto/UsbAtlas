using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    // Each half of a socket can carry its own name, and every name must be readable. A hub drawn as one card for both
    // of its sides shows both names, each over the half where its connection comes in. An empty socket whose halves
    // are named differently shows each name in full, growing away from the seam, and a lone socket's tag is not cut
    // short by the sockets in use beside it.
    private void VerifyPortTagSides()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected; var savedLabels = deviceLabels; bool savedHorizontal = horizontalTree;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsbAtlas-tag-sides-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json")); selected = null;
            Border TagFor(string name) => Graph.Children.OfType<Border>().Single(b => Equals(b.Tag, PortTagMarker) && ((TextBlock)b.Child).Text == name);
            static Rect Area(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);
            static double Needed(Border tag)
            {
                var label = (TextBlock)tag.Child;
                var probe = new TextBlock { Text = label.Text, FontSize = label.FontSize, FontWeight = label.FontWeight, FontFamily = label.FontFamily };
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return Math.Ceiling(probe.DesiredSize.Width) + 10;
            }
            void Reads(Border tag, string what) => Check(((TextBlock)tag.Child).TextTrimming == TextTrimming.CharacterEllipsis && tag.ActualWidth >= Needed(tag) - 1, $"{what} must be readable in full at rest: {tag.ActualWidth:0.#} px for {Needed(tag):0.#} px of text.");

            // A merged hub: the USB 2 side on host port 1 and its USB 3 side on port 4, the other half of the same
            // socket, each named on its own.
            snapshot = MergedHubFixture();
            UsbNode Node(string id) => snapshot.Nodes.Single(n => n.Id == id);
            foreach (var (id, name) in new[] { ("tag/root/1", "RedL"), ("tag/root/4", "RED R") })
                Check(deviceLabels.TrySetPort(Node(id), snapshot, name, out var error), $"Naming {id} must save: {error}");
            foreach (bool horizontal in new[] { false, true })
            {
                string layout = horizontal ? "horizontal" : "vertical";
                horizontalTree = horizontal; selected = null; Draw(); UpdateLayout();
                Check(cards.ContainsKey("tag/root/4") && !cards.ContainsKey("tag/root/1"), $"The fixture's hub sides must be drawn as one card ({layout}).");
                var card = Area(cards["tag/root/4"].Card); Border near = TagFor("RedL"), far = TagFor("RED R");
                Rect nearArea = Area(near), farArea = Area(far);
                Check(Math.Abs(nearArea.Top - (card.Top - 8)) < 0.5 && Math.Abs(farArea.Top - (card.Top - 8)) < 0.5, $"Both of a merged hub's names must sit on its card's top border ({layout}).");
                if (!horizontal)
                    Check(nearArea.Left >= card.Left && nearArea.Right <= card.Left + card.Width / 2 && farArea.Left >= card.Left + card.Width / 2 && farArea.Right <= card.Right,
                        $"Each side's name must sit over the half of the card where its connection comes in (the USB 2 side, on the lower port, first): {nearArea}, {farArea}, card {card}.");
                else
                    Check(nearArea.Left >= card.Left + 7.5 && farArea.Left >= nearArea.Right + 3 && farArea.Right <= card.Right, $"A merged hub's names must sit side by side on the card ({layout}): {nearArea}, {farArea}, card {card}.");
                Reads(near, $"The USB 2 side's name ({layout})"); Reads(far, $"The USB 3 side's name ({layout})");
                // Selecting one side reveals its name alone.
                SelectNode(Node("tag/root/1")); UpdateLayout();
                Check(((TextBlock)TagFor("RedL").Child).TextTrimming == TextTrimming.None && ((TextBlock)TagFor("RED R").Child).TextTrimming == TextTrimming.CharacterEllipsis, $"Selecting the USB 2 side must reveal its name alone ({layout}).");
                SelectNode(Node("tag/root/4")); UpdateLayout();
                Check(((TextBlock)TagFor("RED R").Child).TextTrimming == TextTrimming.None && ((TextBlock)TagFor("RedL").Child).TextTrimming == TextTrimming.CharacterEllipsis, $"Selecting the USB 3 side must reveal its name alone ({layout}).");
                if (!horizontal) CaptureUi("port-tag-merged-preview.png");
            }

            // The same two names on an unplugged socket, and a lone socket among sockets in use. In the sample the
            // keyboard shares a socket with the empty port 7, and the wheel base is the last port.
            deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels-demo.json")); selected = null;
            snapshot = DemoData.Create();
            snapshot.Nodes.Single(n => n.Id == "demo/root/3").Kind = "Empty port"; snapshot.Nodes.Single(n => n.Id == "demo/root/9").Kind = "Empty port";
            foreach (var (id, name) in new[] { ("demo/root/3", "RedL"), ("demo/root/7", "RED R"), ("demo/root/9", "Direct-drive base cable") })
                Check(deviceLabels.TrySetPort(Node(id), snapshot, name, out var error), $"Naming {id} must save: {error}");
            foreach (bool horizontal in new[] { false, true })
            {
                string layout = horizontal ? "horizontal" : "vertical";
                horizontalTree = horizontal; selected = null; Draw(); UpdateLayout();
                Border left = TagFor("RedL"), right = TagFor("RED R"), lone = TagFor("Direct-drive base cable");
                Rect leftArea = Area(left), rightArea = Area(right), loneArea = Area(lone), s3 = Area(portSlots["demo/root/3"]), s7 = Area(portSlots["demo/root/7"]), s9 = Area(portSlots["demo/root/9"]);
                var host = Area(cards[CardNode(nodeParents["demo/root/7"]).Id].Card);
                if (!horizontal)
                {
                    double seam = Math.Max(Math.Min(s3.Right, s7.Right), Math.Min(s3.Left, s7.Left));
                    Check(leftArea.Right <= seam + 0.5 && rightArea.Left >= seam - 0.5 && leftArea.Right <= rightArea.Left && Math.Max(leftArea.Bottom, rightArea.Bottom) <= Math.Min(s3.Top, s7.Top) - 2,
                        $"Two names on one socket must meet at its seam, above it, without overlapping ({layout}): {leftArea}, {rightArea}, sockets {s3}, {s7}.");
                    Check(loneArea.Right <= host.Right - 2.5 && loneArea.Right >= s9.Left && loneArea.Bottom <= s9.Top - 2, $"A lone socket's tag must stay inside its card and over its socket ({layout}): {loneArea}, socket {s9}, card {host}.");
                    selected = snapshot.Nodes.Single(n => n.Id == "demo/root/7"); RevealSelection(); selected = null; UpdateTags(); UpdateLayout();
                    CaptureUi("port-tag-halves-preview.png");
                }
                else
                {
                    Check(leftArea.Right <= Math.Min(s3.Left, s7.Left) - 2 && rightArea.Right <= Math.Min(s3.Left, s7.Left) - 2
                        && Math.Abs(leftArea.Top + leftArea.Height / 2 - (s3.Top + s3.Height / 2)) < 1 && Math.Abs(rightArea.Top + rightArea.Height / 2 - (s7.Top + s7.Height / 2)) < 1,
                        $"Two names on one socket must each sit beside their own half ({layout}): {leftArea}, {rightArea}, sockets {s3}, {s7}.");
                }
                Reads(left, $"The first half's name ({layout})"); Reads(right, $"The second half's name ({layout})");
                // Beside a horizontal card's sockets a tag is capped at the room the strip makes, so a long name there trims and reveals.
                if (!horizontal) Reads(lone, $"A lone socket's name ({layout})");
            }
        }
        finally
        {
            snapshot = savedSnapshot; selected = savedSelection; deviceLabels = savedLabels; horizontalTree = savedHorizontal;
            Draw(); ShowDetails(); UpdateIssues();
            System.IO.Directory.Delete(directory, true);
        }
    }

    // A USB 3 hub's two sides, which Windows reports as two hubs, paired as one card: the USB 2 side on host port 1 and
    // its USB 3 side on port 4, the other half of the same socket, with all their own ports empty.
    private static Snapshot MergedHubFixture()
    {
        var root = new UsbNode { Id = "tag/root", Kind = "Root hub", Name = "Root hub", PortCount = 6, HubSymbolicLink = @"\\?\tag-root" };
        UsbNode Hub(int port, bool usb3) => new()
        {
            Id = $"tag/root/{port}", Kind = "Hub", Name = "Desk hub", Port = port, PortCount = 2, VendorId = "05E3", LinkMbps = usb3 ? 5000 : 480,
            Speed = usb3 ? "SuperSpeed · 5 Gb/s" : "High speed · 480 Mb/s", UsbVersion = usb3 ? "USB 3.20" : "USB 2.10", HubSymbolicLink = $@"\\?\tag-hub-{port}", PortConnectorIsTypeC = false
        };
        var usb2 = Hub(1, false); var usb3 = Hub(4, true);
        usb2.CompanionPortNumber = 4; usb2.CompanionHubSymbolicLink = root.HubSymbolicLink;
        usb2.CompanionId = usb3.Id; usb3.CompanionId = usb2.Id;
        for (int p = 1; p <= 2; p++)
        {
            var low = new UsbNode { Id = $"{usb2.Id}/{p}", Kind = "Empty port", Name = $"Available port {p}", Port = p, Status = "Empty" };
            var high = new UsbNode { Id = $"{usb3.Id}/{p}", Kind = "Empty port", Name = $"Available port {p}", Port = p, Status = "Empty" };
            low.CompanionId = high.Id; high.CompanionId = low.Id;
            usb2.Children.Add(low); usb3.Children.Add(high);
        }
        root.Children.Add(usb2); root.Children.Add(usb3);
        var built = new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "tag", Kind = "Controller", Name = "Host", Children = [root] }] };
        HubRelationships.Analyze(built);
        return built;
    }
}

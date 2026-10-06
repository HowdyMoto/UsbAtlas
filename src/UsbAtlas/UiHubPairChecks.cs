using System.Windows;
using System.Windows.Input;

namespace UsbAtlas;

public partial class MainWindow
{
    // A USB 3 hub's two sides, which Windows reports as two hubs, are drawn as one card when nothing is wired
    // between their ports: two connections in, its sockets split into their halves, and either side still
    // selecting as itself. With a device between them they keep two cards that mark each other.
    private void VerifyPairedHubs()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected; bool savedHorizontal = horizontalTree;
        // The hub's USB 2 side on host port 1 and its USB 3 side on port 4, the other half of the same socket,
        // or on port 6 with a device on port 3 between them. Pairing comes from Windows' companion links.
        Snapshot Build(bool between)
        {
            var root = new UsbNode { Id = "pair/root", Kind = "Root hub", Name = "Root hub", PortCount = 6, HubSymbolicLink = @"\\?\pair-root" };
            int usb3Port = between ? 6 : 4;
            UsbNode Hub(int port, bool usb3) => new()
            {
                Id = $"pair/root/{port}", Kind = "Hub", Name = "Desk hub", Port = port, PortCount = 2, VendorId = "05E3", LinkMbps = usb3 ? 5000 : 480,
                Speed = usb3 ? "SuperSpeed · 5 Gb/s" : "High speed · 480 Mb/s", UsbVersion = usb3 ? "USB 3.20" : "USB 2.10", HubSymbolicLink = $@"\\?\pair-hub-{port}", PortConnectorIsTypeC = false
            };
            var usb2 = Hub(1, false); var usb3 = Hub(usb3Port, true);
            usb2.CompanionPortNumber = usb3Port; usb2.CompanionHubSymbolicLink = root.HubSymbolicLink;
            if (!between) { usb2.CompanionId = usb3.Id; usb3.CompanionId = usb2.Id; }
            // Each USB 2 port's other half is the USB 3 port with the same number: a keyboard on the USB 2
            // half of socket 1, a drive on the USB 3 half of socket 2.
            for (int p = 1; p <= 2; p++)
            {
                var low = p == 1 ? new UsbNode { Id = $"{usb2.Id}/{p}", Kind = "Device", Name = "Keyboard", DeviceType = "Keyboard", Port = p, LinkMbps = 12 } : new UsbNode { Id = $"{usb2.Id}/{p}", Kind = "Empty port", Name = $"Available port {p}", Port = p, Status = "Empty" };
                var high = p == 2 ? new UsbNode { Id = $"{usb3.Id}/{p}", Kind = "Device", Name = "Portable SSD", DeviceType = "Storage", Port = p, LinkMbps = 5000 } : new UsbNode { Id = $"{usb3.Id}/{p}", Kind = "Empty port", Name = $"Available port {p}", Port = p, Status = "Empty" };
                low.CompanionId = high.Id; high.CompanionId = low.Id;
                usb2.Children.Add(low); usb3.Children.Add(high);
            }
            root.Children.Add(usb2);
            if (between) root.Children.Add(new UsbNode { Id = "pair/root/3", Kind = "Device", Name = "Webcam", DeviceType = "Camera / video", Port = 3, LinkMbps = 480 });
            root.Children.Add(usb3);
            var built = new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "pair", Kind = "Controller", Name = "Host", Children = [root] }] };
            HubRelationships.Analyze(built);
            Check(usb2.CompanionHubId == usb3.Id && usb2.IsUsb2Companion && !usb3.IsUsb2Companion, "The fixture's hub sides must be paired by Windows' companion links.");
            return built;
        }
        try
        {
            snapshot = Build(false);
            UsbNode Node(string id) => snapshot.Nodes.Single(n => n.Id == id);
            foreach (bool horizontal in new[] { true, false })
            foreach (var level in Enum.GetValues<CardDetail>())
            {
                horizontalTree = horizontal; detail = level; selected = Node("pair/root/4"); Draw(); UpdateLayout();
                string where = $"{level} cards, {(horizontal ? "horizontal" : "vertical")}";
                Check(cards.ContainsKey("pair/root/4") && !cards.ContainsKey("pair/root/1") && cards.ContainsKey("pair/root/1/1") && cards.ContainsKey("pair/root/4/2"), $"A paired hub on one socket must be one card holding both sides' devices ({where}).");
                Check(wires.ContainsKey("pair/root/1") && wires.ContainsKey("pair/root/4"), $"A merged hub must have a connection from each side's port ({where}).");
                VerifyWireRouting();
                if (DrawsSockets)
                {
                    Check(socketParts["pair/root/1/1"] == NodeVisuals.SocketPart.First && socketParts["pair/root/4/1"] == NodeVisuals.SocketPart.Second, $"A merged hub's sockets must be split into their USB 2 and USB 3 halves ({where}).");
                    Check(connectedPorts.Count + portSlots.Count == 2 + 4, $"A merged hub must draw both sides' ports, beside the host's two ({where}).");
                }
            }
            horizontalTree = true; detail = CardDetail.Full; Draw();
            var label = VisualDescendants(cards["pair/root/4"].Card).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text);
            Check(label.Contains("USB 3 hub · USB 2 and USB 3 sides"), "A merged hub's card must say it holds both sides.");
            // Selecting the USB 2 side, from the tree or Properties, keeps it as the selection and marks the card.
            SelectNode(Node("pair/root/1")); UpdateLayout();
            Check(selected?.Id == "pair/root/1" && cards["pair/root/4"].Card.Effect != null && GraphBounds(selected) is Rect, "Selecting a merged hub's USB 2 side must select it and mark the merged card.");
            CaptureUi("merged-hub-preview.png");
            Check(((System.Windows.Controls.TextBlock)connectedPorts["pair/root/1/1"].Content).Text.Length > 0 && ((System.Windows.Controls.TextBlock)portSlots["pair/root/4/1"].Content).Text.Length == 0,
                "A merged hub's socket must show its shared number once.");
            // Focusing on the merged hub keeps both sides, and a device behind a folded merged card is revealed.
            SelectNode(Node("pair/root/4")); FocusBranchClick(this, new RoutedEventArgs());
            Check(cards.ContainsKey("pair/root/4") && cards.ContainsKey("pair/root/1/1") && cards.ContainsKey("pair/root/4/2") && wires.ContainsKey("pair/root/1"), "Focusing on a merged hub must keep both of its sides.");
            FocusBranchClick(this, new RoutedEventArgs());
            folded.Add("pair/root/4"); Draw();
            Check(!cards.ContainsKey("pair/root/1/1"), "Folding a merged hub must hide both sides' devices.");
            ShowOnCanvas(Node("pair/root/1/1"));
            Check(cards.ContainsKey("pair/root/1/1") && !folded.Contains("pair/root/4") && selected?.Id == "pair/root/1/1", "Revealing a device behind a folded merged hub must unfold it.");

            snapshot = Build(true); Draw(); UpdateLayout();
            Check(cards.ContainsKey("pair/root/1") && cards.ContainsKey("pair/root/6"), "A paired hub with a device wired between its ports must keep two cards.");
            VerifyWireRouting();
            SelectNode(Node("pair/root/1")); UpdateLayout();
            var other = cards["pair/root/6"].Card;
            Check(other.BorderBrush == Brush("Accent") && other.Effect == null, "Selecting one side of a hub drawn as two cards must outline the other.");
            SelectNode(Node("pair/root/6")); UpdateLayout();
            Check(cards["pair/root/1"].Card.BorderBrush == Brush("Accent") && cards["pair/root/1"].Card.Effect == null, "Selecting the USB 3 side must outline the USB 2 side's card too.");
            SelectNode(Node("pair/root/1")); UpdateLayout();
            cards["pair/root/1"].Card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            var ring = Graph.Children.OfType<System.Windows.Controls.Border>().SingleOrDefault(b => Equals(b.Tag, PartRingTag));
            Check(ring != null && Math.Abs(System.Windows.Controls.Canvas.GetLeft(ring) + 4 - cards["pair/root/6"].Point.X) < 0.01, "Hovering one side of a hub drawn as two cards must ring the other.");
            cards["pair/root/1"].Card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            CaptureUi("paired-hubs-preview.png");
            VerifyNestedPairedHubs();
        }
        finally { snapshot = savedSnapshot; selected = savedSelection; horizontalTree = savedHorizontal; detail = CardDetail.Full; Draw(); ShowDetails(); }
    }
    // One enclosure of two USB 3 hub chips, the second wired to the first's port 4: Windows sees two USB 2 hubs
    // in a chain and two USB 3 hubs in a chain. The inner port has no companion of its own; the second chip's
    // sides pair through their own ports. Each chip is one card, its port 4 one socket of two halves, and linking
    // either side snaps the whole chip beside the first.
    private void VerifyNestedPairedHubs()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var root = new UsbNode { Id = "nest/root", Kind = "Root hub", Name = "Root hub", PortCount = 2, HubSymbolicLink = @"\\?\nest-root" };
        UsbNode Hub(string id, int port, bool usb3) => new()
        {
            Id = id, Kind = "Hub", Name = usb3 ? "USB3.2 Hub" : "RTS5411 Hub", Port = port, PortCount = 4, VendorId = "0BDA", LinkMbps = usb3 ? 5000 : 480,
            Speed = usb3 ? "SuperSpeed · 5 Gb/s" : "High speed · 480 Mb/s", UsbVersion = usb3 ? "USB 3.20" : "USB 2.10", HubSymbolicLink = $@"\\?\{id.Replace('/', '-')}", PortConnectorIsTypeC = false
        };
        var outer2 = Hub("nest/root/1", 1, false); var outer3 = Hub("nest/root/2", 2, true);
        var inner2 = Hub("nest/root/1/4", 4, false); var inner3 = Hub("nest/root/2/4", 4, true);
        outer2.CompanionPortNumber = 2; outer2.CompanionHubSymbolicLink = root.HubSymbolicLink; outer2.CompanionId = outer3.Id; outer3.CompanionId = outer2.Id;
        foreach (var (usb2, usb3, sockets) in new[] { (outer2, outer3, 3), (inner2, inner3, 4) })
            for (int p = 1; p <= sockets; p++)
            {
                var low = p == 1 ? new UsbNode { Id = $"{usb2.Id}/{p}", Kind = "Device", Name = usb2 == outer2 ? "Microphone" : "Stream deck", DeviceType = "Audio", Port = p, LinkMbps = 12 } : new UsbNode { Id = $"{usb2.Id}/{p}", Kind = "Empty port", Name = $"Available port {p}", Port = p, Status = "Empty" };
                var high = new UsbNode { Id = $"{usb3.Id}/{p}", Kind = "Empty port", Name = $"Available port {p}", Port = p, Status = "Empty" };
                low.CompanionId = high.Id; high.CompanionId = low.Id;
                usb2.Children.Add(low); usb3.Children.Add(high);
            }
        outer2.Children.Add(inner2); outer3.Children.Add(inner3);
        root.Children.AddRange([outer2, outer3]);
        snapshot = new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "nest", Kind = "Controller", Name = "Host", Children = [root] }] };
        HubRelationships.Analyze(snapshot);
        Check(inner2.CompanionHubId == inner3.Id && inner2.IsUsb2Companion && inner2.CompanionId.Length == 0, "The fixture's inner chip must pair through its own ports, with no companion on the port it hangs from.");
        foreach (bool horizontal in new[] { true, false })
        {
            horizontalTree = horizontal; detail = CardDetail.Full; Draw(); UpdateLayout();
            string where = horizontal ? "horizontal" : "vertical";
            Check(cards.ContainsKey(outer3.Id) && cards.ContainsKey(inner3.Id) && !cards.ContainsKey(outer2.Id) && !cards.ContainsKey(inner2.Id), $"A paired hub plugged into a merged one must be one card too ({where}).");
            Check(wires.ContainsKey(inner2.Id) && wires.ContainsKey(inner3.Id), $"A merged inner hub must have a connection from each side's port ({where}).");
            Check(socketParts[inner2.Id] == NodeVisuals.SocketPart.First && socketParts[inner3.Id] == NodeVisuals.SocketPart.Second
                && ((System.Windows.Controls.TextBlock)connectedPorts[inner3.Id].Content).Text.Length == 0, $"The port between two chips must be one socket of two halves, numbered once ({where}).");
            VerifyWireRouting();
        }
        // Linking only the USB 2 side, as an older version allowed, still snaps the whole chip beside the first.
        inner2.SnapToParentHub = true; horizontalTree = false; Draw(); UpdateLayout();
        Check(cards.ContainsKey(inner3.Id) && !cards.ContainsKey(inner2.Id) && Math.Abs(cards[inner3.Id].Point.Y - cards[outer3.Id].Point.Y) < .01 && cards[inner3.Id].Point.X > cards[outer3.Id].Point.X,
            "A merged hub linked on either side must sit beside its upstream stage as one card.");
        Check(snappedWires.Contains(inner2.Id) && snappedWires.Contains(inner3.Id), "A merged stage must be linked from both halves of its socket.");
        VerifyWireRouting();
        SelectNode(inner3); UpdateLayout();
        var snap = Details.Children.OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Tag, "snap-upstream-hub"));
        Check((string)snap.Content == "Unlink from upstream hub", "A merged stage linked on its USB 2 side must offer to unlink.");
        CaptureUi("nested-paired-hubs-preview.png");
    }
}

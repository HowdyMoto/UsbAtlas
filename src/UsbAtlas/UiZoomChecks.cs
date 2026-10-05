using System.Windows;

namespace UsbAtlas;

public partial class MainWindow
{
    // Opening and Fit all show the whole topology, never a branch nobody chose, at the most detailed level
    // that fits at a readable scale; zooming in on a region brings back full cards there. The scale and the
    // on-screen name size each topology opens at are written to semantic-zoom.txt.
    private void VerifySemanticZoom()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected; bool savedHorizontal = horizontalTree;
        var report = new List<string>();
        // A name is readable when drawn at 11 px or more on screen.
        double NameSize()
        {
            var (id, item) = cards.First(c => ((UsbNode)c.Value.Card.Tag).Kind is "Device");
            return VisualDescendants(item.Card).OfType<System.Windows.Controls.TextBlock>().First(t => t.Text == NodeVisuals.ShortName((UsbNode)item.Card.Tag)).FontSize * GraphScale.ScaleX;
        }
        static bool Hardware(UsbNode n) => n.Kind is "Hub" or "Device" or "Unavailable";
        bool fullSize = ActualWidth >= 1400 && ActualHeight >= 900;
        void Open(string name)
        {
            FitClick(this, new RoutedEventArgs()); OpenInitialView(); UpdateLayout();
            Check(focusedBranch == null && cards.Values.Count(c => Hardware((UsbNode)c.Card.Tag)) == snapshot.Nodes.Count(Hardware), $"{name} must open with every hub and device on the canvas.");
            Check(Graph.Width * GraphScale.ScaleX <= GraphScroll.ViewportWidth + 1 && Graph.Height * GraphScale.ScaleY <= GraphScroll.ViewportHeight + 1, $"{name} must open fitted whole.");
            // The level is the most detailed whose whole graph fits at a readable scale: the next richer one doesn't.
            var chosen = detail;
            if (chosen != CardDetail.Full)
            {
                detail = chosen - 1; Draw(); double richer = FitScale(); detail = chosen; Draw(); UpdateLayout();
                Check(richer < ReadableScale, $"{name} opened at {chosen} cards though {chosen - 1} cards fit at {richer:P0}.");
            }
            report.Add($"{name} · {(horizontalTree ? "horizontal" : "vertical")} · {ActualWidth:0}×{ActualHeight:0} window: {detail} cards at {GraphScale.ScaleX:P0}, names {NameSize():0.#} px");
        }
        try
        {
            foreach (bool horizontal in new[] { true, false })
            {
                horizontalTree = horizontal;
                snapshot = DemoData.Create(); selected = snapshot.Nodes.First(n => n.Id == "demo/root/1");
                Open("The sample");
                if (horizontal && fullSize) Check(NameSize() >= 11, $"The sample must open with readable names; they are {NameSize():0.#} px.");
                if (horizontal) CaptureUi("overview-preview.png");
            }
            horizontalTree = true;
            // About 40 devices behind five seven-port hubs.
            var root = new UsbNode { Id = "big/root", Kind = "Root hub", Name = "Root hub", PortCount = 10 };
            int count = 0;
            UsbNode Device(string id, int port) => new() { Id = id, Kind = "Device", Name = $"Device {++count}", DeviceType = count % 3 == 0 ? "Mouse" : "Keyboard", Port = port, LinkMbps = 12 };
            for (int h = 1; h <= 5; h++)
            {
                var hub = new UsbNode { Id = $"big/root/{h}", Kind = "Hub", Name = $"Hub {h}", Port = h, PortCount = 7, LinkMbps = 480 };
                for (int p = 1; p <= 7; p++) hub.Children.Add(Device($"{hub.Id}/{p}", p));
                root.Children.Add(hub);
            }
            for (int p = 6; p <= 10; p++) root.Children.Add(Device($"big/root/{p}", p));
            var empty = new UsbNode { Id = "big/root/5/8", Kind = "Empty port", Name = "Available port 8", Port = 8, Status = "Empty" };
            root.Children[4].Children.Add(empty); root.Children[4].PortCount = 8;
            snapshot = new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "big", Kind = "Controller", Name = "Host", Children = [root] }] };
            selected = snapshot.Controllers[0];
            Open("40 devices");
            CaptureUi("overview-40-preview.png");
            Check(root.Children.Take(5).All(h => packedHubs.Contains(h.Id)), "Far cards in the horizontal layout must pack each hub's end devices.");
            if (fullSize) Check(NameSize() >= 11, $"40 devices must open with readable names; they are {NameSize():0.#} px.");
            Check(detail == CardDetail.Far, "40 devices must open at far cards in the default window.");
            // Within a level, zooming keeps the point under the pointer.
            var middle = new Point(GraphScroll.ViewportWidth / 2, GraphScroll.ViewportHeight / 2);
            var held = GraphScroll.TranslatePoint(middle, Graph);
            ZoomAt(GraphScale.ScaleX / 1.2, middle);
            Check(detail == CardDetail.Far && (Graph.TranslatePoint(held, GraphScroll) - middle).Length < 1, "Zooming out at far cards moved the point under the pointer.");
            // The zoom readout is the one 100% control: full cards, laid out for the window.
            ActualSizeClick(this, new RoutedEventArgs());
            Check(detail == CardDetail.Full && GraphScale.ScaleX == 1 && readableView, "Resetting to 100% must show full cards laid out for the window.");
            // Shift+1 fits everything, Shift+2 centers the selection at full size, Shift+0 is the readout; other keys pass through.
            Check(FramingShortcut(System.Windows.Input.Key.D1) && overviewView && detail == CardDetail.Far, "Shift+1 must fit the whole topology.");
            SelectNode(snapshot.Nodes.First(n => n.Id == "big/root/4/6"));
            Check(FramingShortcut(System.Windows.Input.Key.D2) && detail == CardDetail.Full && GraphScale.ScaleX == 1 && cards.ContainsKey("big/root/4/6"), "Shift+2 must center the selection at full size.");
            var found = new Rect(cards["big/root/4/6"].Point, new Size(cards["big/root/4/6"].Card.Width, cards["big/root/4/6"].Card.Height));
            Check(new Rect(0, 0, GraphScroll.ViewportWidth, GraphScroll.ViewportHeight).Contains(Graph.TranslatePoint(new Point(found.X + found.Width / 2, found.Y + found.Height / 2), GraphScroll)), "Shift+2 must bring the selection into view.");
            Check(FramingShortcut(System.Windows.Input.Key.D0) && readableView && !FramingShortcut(System.Windows.Input.Key.A), "Shift+0 must show 100%, and other keys must pass through.");
            // Keyboard navigation from far cards reads the next card at full size.
            OverviewClick(this, new RoutedEventArgs());
            var first = cards["big/root/2/3"].Card; first.Focus();
            first.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, System.Windows.Input.Key.Down) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Check(selected?.Id == "big/root/2/4" && detail == CardDetail.Full && cards.ContainsKey(selected.Id), "Arrow keys from far cards must move to the next card and show it at full size.");
            // A port is only drawn as a socket, so selecting one from far cards brings sockets back at a readable scale.
            OverviewClick(this, new RoutedEventArgs());
            SelectNode(empty);
            Check(detail == CardDetail.Compact && portSlots.ContainsKey(empty.Id) && GraphScale.ScaleX >= ReadableScale && !overviewView, "Selecting a port from far cards must draw its socket at a readable scale.");
            OverviewClick(this, new RoutedEventArgs());
            // Zooming in on a hub brings back its full card, sockets and all, still under the pointer.
            var hubId = "big/root/3";
            var box = new Rect(cards[hubId].Point, new Size(cards[hubId].Card.Width, cards[hubId].Card.Height));
            var pointer = Graph.TranslatePoint(new Point(box.X + box.Width / 2, box.Y + box.Height / 2), GraphScroll);
            for (int step = 0; step < 40 && detail != CardDetail.Full; step++) ZoomAt(GraphScale.ScaleX * 1.12, pointer);
            box = new Rect(cards[hubId].Point, new Size(cards[hubId].Card.Width, cards[hubId].Card.Height));
            Check(detail == CardDetail.Full && connectedPorts.ContainsKey(hubId + "/1"), "Zooming in must bring back full cards with their sockets.");
            Check(box.Contains(GraphScroll.TranslatePoint(pointer, Graph)), "Zooming in must keep the card under the pointer.");
            // Focus is chosen, and it says how much it hides.
            snapshot = DemoData.Create(); horizontalTree = true; FitClick(this, new RoutedEventArgs());
            var focus = snapshot.Nodes.First(n => n.Id == "demo/root/1"); SelectNode(focus); FocusBranchClick(this, new RoutedEventArgs());
            int shown = FindPath(focus.Id).Concat(focus.Walk()).DistinctBy(n => n.Id).Count(Hardware);
            Check(TopologyTitle.Text == $"Showing {shown} of {snapshot.Nodes.Count(Hardware)} hubs and devices", $"A focused branch must say how much it shows, not \"{TopologyTitle.Text}\".");
            FocusBranchClick(this, new RoutedEventArgs());
            Check(TopologyTitle.Text == "TOPOLOGY", "Showing all branches must restore the title.");
        }
        finally
        {
            System.IO.File.WriteAllLines("semantic-zoom.txt", report);
            snapshot = savedSnapshot; selected = savedSelection; horizontalTree = savedHorizontal; focusedBranch = null; FocusBranchButton.Content = "Focus branch";
            FitClick(this, new RoutedEventArgs()); ShowDetails();
        }
    }
}

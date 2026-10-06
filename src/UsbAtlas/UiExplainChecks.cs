using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsbAtlas;
public partial class MainWindow
{
    // Everything drawn on the graph explains itself: pointing at it says what it means, and a card says
    // everything in Properties when clicked. Thin marks explain through a wider invisible twin. Only
    // rings and fading cards, which point at cards that explain themselves, are exempt.
    private void CheckCanvasExplains(string where)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static bool Explains(FrameworkElement e) => e.IsHitTestVisible && e.ToolTip is string { Length: > 0 } or FrameworkElement;
        var cardBorders = cards.Values.Select(c => c.Card).ToHashSet();
        foreach (var e in Graph.Children.OfType<FrameworkElement>())
        {
            if (e.Tag is PartRingTag or PulseTag or RemovedCardTag) continue;
            if (cardBorders.Contains(e)) { Check(e.IsHitTestVisible && e.Tag is UsbNode, $"A card must be selectable, so Properties explains it ({where})."); continue; }
            if (e is Path wire && wire.Tag is UsbNode node && wires.GetValueOrDefault(node.Id) == wire)
            {
                Check(wireHits.TryGetValue(node.Id, out var hit) && Explains(hit) && ReferenceEquals(hit.Data, wire.Data) && hit.Tag == node,
                    $"The connection to {node.Name} must explain itself on hover, along its whole route ({where}).");
                continue;
            }
            if (e is Line stub && Equals(stub.Tag, MissingUsb3Tag))
            {
                Check(Graph.Children.OfType<Line>().Any(h => h.Uid == MissingUsb3HitUid && h.X1 == stub.X1 && h.Y1 == stub.Y1 && h.X2 == stub.X2 && h.Y2 == stub.Y2 && Explains(h)),
                    $"A missing USB 3 side's stub must explain itself on hover ({where}).");
                continue;
            }
            Check(Explains(e), $"{e.GetType().Name} on the canvas (tag {e.Tag ?? "none"}, uid {(e.Uid.Length > 0 ? e.Uid : "none")}) explains nothing ({where}): give it a tooltip, or a wider hit area that has one.");
        }
        // Pointing at a mark must reach its own explanation, not a neighbor's whose hit area overlaps it:
        // the middle of every connection's segments, and of every stub.
        UIElement? Top(Point p)
        {
            UIElement? top = null;
            VisualTreeHelper.HitTest(Graph, null, r =>
            {
                for (var v = r.VisualHit; v != null && v != Graph; v = VisualTreeHelper.GetParent(v))
                    if (VisualTreeHelper.GetParent(v) == Graph) { top = v as UIElement; return HitTestResultBehavior.Stop; }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(p));
            return top;
        }
        foreach (var (id, route) in wireRoutes)
            for (int i = 1; i < route.Count; i++)
            {
                if ((route[i] - route[i - 1]).Length < 8) continue;
                var mid = route[i - 1] + (route[i] - route[i - 1]) / 2;
                var top = Top(mid);
                Check(top == wireHits[id], $"Pointing at the connection to {((UsbNode)wires[id].Tag).Name} must explain that connection, not {(top as FrameworkElement)?.Tag ?? "nothing"} ({where}).");
            }
        foreach (var hit in Graph.Children.OfType<Line>().Where(h => h.Uid == MissingUsb3HitUid))
            Check(Top(new Point((hit.X1 + hit.X2) / 2, (hit.Y1 + hit.Y2) / 2)) == hit, $"Pointing at a missing USB 3 side's stub must explain it ({where}).");
    }

    // The sample, with a monitor hub whose USB 3 side didn't connect, at every level of detail in both
    // directions, so every kind of mark is drawn.
    private void VerifyEverythingExplains()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var saved = (snapshot, selected, detail, horizontalTree, focusedBranch);
        try
        {
            snapshot = DemoData.Create(); focusedBranch = null;
            var monitor = snapshot.Nodes.First(n => n.Id == "demo/root/5");
            monitor.SpeedLimited = true; monitor.Connector = "USB-C"; monitor.CompanionId = "demo/root/7";
            HubRelationships.Analyze(snapshot);
            Check(monitor.Usb3SideMissing, "The sample monitor hub must be missing its USB 3 side.");
            foreach (var horizontal in new[] { true, false })
                foreach (var level in new[] { CardDetail.Full, CardDetail.Compact, CardDetail.Far })
                {
                    horizontalTree = horizontal; detail = level; Draw(); UpdateLayout();
                    CheckCanvasExplains($"{level} cards, {(horizontal ? "horizontal" : "vertical")}");
                }

            // The stub and the empty half it marks both name the hub, and lead to its explanation.
            horizontalTree = true; detail = CardDetail.Full; Draw(); UpdateLayout();
            var hit = Graph.Children.OfType<Line>().Single(h => h.Uid == MissingUsb3HitUid);
            var tip = (string)hit.ToolTip;
            Check(hit.Tag == monitor && tip.Contains("USB 3 side isn't connected") && tip.Contains("USB 3 half of the socket") && tip.Contains("Click for what to do"),
                "The stub must say whose USB 3 side is missing, where it should connect, and how to learn more.");
            Check((string)portSlots["demo/root/7"].ToolTip is var socketTip && socketTip.Contains("USB 3 half of the socket") && socketTip.Contains(NodeVisuals.ShortName(monitor)),
                "The empty half's socket must say which hub's USB 3 side belongs there.");
            hit.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
            UpdateLayout();
            Check(selected == monitor && openExplanations.Contains(Explanations.SpeedLabel(monitor)) && Details.Children.OfType<FrameworkElement>().Any(x => Equals(x.Tag, "warning:Running at USB 2")),
                "Clicking the stub must select the hub and open what its slow link means.");
            var wire = wireHits[monitor.Children[0].Id];
            Check(((string)wire.ToolTip).Contains("480 Mb/s") && ((string)wire.ToolTip).Contains("Width is the negotiated link rate"), "A connection's tooltip must give its rate and what its width means.");
            Check(((string)wireHits[monitor.Id].ToolTip).Contains("Dashed"), "A slow connection's tooltip must say why it's dashed.");

            // Properties for the empty half leads with what it's for and offers the hub.
            SelectNode(snapshot.Nodes.First(n => n.Id == "demo/root/7")); UpdateLayout();
            var panel = Details.Children.OfType<FrameworkElement>().FirstOrDefault(x => x.Tag is string t && t.StartsWith("warning:USB 3 side of", StringComparison.Ordinal));
            var show = Details.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, ShowLostHubTag));
            Check(panel != null && show != null && Details.Children.IndexOf(panel) < Details.Children.IndexOf(Details.Children.OfType<FrameworkElement>().First(x => Equals(x.Tag, "field"))),
                "An empty socket half where a hub's USB 3 side belongs must explain that before its rows, with a way to the hub.");
            show!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(selected == monitor, "Show hub must select the hub.");

            // The legend names the stub and every card color.
            UpdateLayout();
            var legendText = string.Join(" ", VisualDescendants(SocketLegend).OfType<TextBlock>().Select(t => t.Text));
            Check(legendText.Contains("USB 3 side not connected") && new[] { "Input", "Gaming", "Audio", "Video", "Storage", "Connectivity" }.All(legendText.Contains),
                "The legend must name the missing USB 3 side's stub and what each card color means.");
            Check(VisualDescendants(SocketLegend).OfType<StackPanel>().Where(p => p.ToolTip != null).Count() >= 10, "Each link and color entry in the legend must explain itself.");
        }
        finally
        {
            (snapshot, selected, detail, horizontalTree, focusedBranch) = saved;
            openExplanations.Clear();
            Draw(); ShowDetails();
        }
    }
}

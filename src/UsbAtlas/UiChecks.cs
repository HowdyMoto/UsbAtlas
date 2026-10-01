using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UsbAtlas;

public partial class MainWindow
{
    private void VerifyDeviceTree()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var originalSelection = selected;
        var target = snapshot.Nodes.First(n => n.Kind == "Device");
        var parent = FindPath(target.Id).SkipLast(1).Last();
        Check(treeItems.Count == snapshot.Nodes.Count(n => n.Kind != "Empty port"), "Tree must include every connected device.");
        treeItems[parent.Id].IsExpanded = false;
        Draw();
        Check(!treeItems[parent.Id].IsExpanded, "Graph redraw lost tree expansion state.");
        folded.Add(parent.Id); Draw();
        treeItems[target.Id].IsSelected = true; UpdateLayout();
        Check(selected?.Id == target.Id && cards.ContainsKey(target.Id) && !folded.Contains(parent.Id), "Tree selection must reveal the graph card.");
        SelectNode(parent);
        Check(DeviceTree.SelectedItem == treeItems[parent.Id], "Graph selection must select the corresponding tree item.");
        TreePanelClick(this, new RoutedEventArgs()); UpdateLayout();
        Check(TreePanel.Visibility == Visibility.Collapsed && TreeColumn.ActualWidth == 0 && TreeButton.Content as string == "Show tree", "Tree panel did not collapse.");
        TreePanelClick(this, new RoutedEventArgs()); UpdateLayout();
        Check(TreePanel.Visibility == Visibility.Visible && TreeColumn.ActualWidth >= 180 && selected?.Id == parent.Id, "Restoring the tree lost its size or selection.");
        Search.Text = target.Name; ApplySearch();
        Check(treeItems.ContainsKey(target.Id) && FindPath(target.Id).All(n => treeItems.ContainsKey(n.Id)), "Filtered tree lost matching device ancestry.");
        Search.Clear(); ApplySearch();
        EmptyPorts.IsChecked = true; Draw();
        Check(treeItems.Count == snapshot.Nodes.Count(), "Tree must include empty ports when enabled.");
        EmptyPorts.IsChecked = false; collapsedTreeBranches.Clear(); treeSignature = ""; Draw();
        if (originalSelection != null) SelectNode(originalSelection);
    }

    // Tree and canvas selections follow each other: a tree pick is revealed and pulsed on the canvas
    // at the current zoom, and a canvas pick is highlighted and scrolled into view in the tree.
    private async Task VerifyTreeCanvasSync()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        try
        {
            snapshot = DemoData.Create();
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub");
            // Enough rows to overflow the tree even in a 3840×1560 window.
            for (int i = 5; i <= 100; i++) hub.Children.Add(new UsbNode { Id = hub.Id + "/sync/" + i, Kind = "Device", Name = "Sync device " + i, Port = i });
            var first = hub.Children[0]; var last = hub.Children[^1];
            selected = null; FitClick(this, new RoutedEventArgs()); UpdateLayout();
            bool RowShown(UsbNode node)
            {
                var row = (FrameworkElement)treeItems[node.Id].Template.FindName("Row", treeItems[node.Id]);
                var scroller = (ScrollViewer)DeviceTree.Template.FindName("_tv_scrollviewer_", DeviceTree);
                // Measure against the content host: the tree's padding sits between it and the scroller's edge.
                var host = (FrameworkElement)scroller.Template.FindName("PART_ScrollContentPresenter", scroller);
                var top = row.TranslatePoint(new Point(0, 0), host).Y;
                return top >= -0.5 && top + row.ActualHeight <= host.ActualHeight + 0.5;
            }
            bool CardShown(UsbNode node)
            {
                var area = GraphBounds(node)!.Value;
                var shown = new Rect(Graph.TranslatePoint(area.TopLeft, GraphScroll), Graph.TranslatePoint(area.BottomRight, GraphScroll));
                return new Rect(0, 0, GraphScroll.ViewportWidth, GraphScroll.ViewportHeight).Contains(shown);
            }
            int Rings() => Graph.Children.OfType<Border>().Count(b => Panel.GetZIndex(b) == 3);

            // Canvas to tree: the row is selected, clearly highlighted, and scrolled into view.
            Check(!RowShown(last), "Test setup expected the last tree row to start out of view.");
            SelectNode(last);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var lastRow = (Border)treeItems[last.Id].Template.FindName("Row", treeItems[last.Id]);
            Check(DeviceTree.SelectedItem == treeItems[last.Id], "Canvas selection did not select the tree row.");
            Check(lastRow.Background == Brush("SelectionStrong") && lastRow.BorderBrush == Brush("Accent"), "Selected tree row is not highlighted.");
            Check(RowShown(last), "Canvas selection did not scroll its tree row into view.");

            // Tree to canvas: keeps the zoom, brings the card into view, and pulses it.
            SetZoom(0.6); ResetPan(); GraphScroll.ScrollToTop(); GraphScroll.ScrollToLeftEnd(); UpdateLayout();
            treeItems[first.Id].IsSelected = true; UpdateLayout();
            Check(selected?.Id == first.Id && Math.Abs(GraphScale.ScaleX - 0.6) < 0.001, "Tree selection must select on the canvas without changing zoom.");
            Check(CardShown(first) && Rings() > 0, "Tree selection must reveal and pulse the canvas card.");

            // Clicking the already-selected row finds its card again after the canvas moved away.
            PanTransform.X = -20000; PanTransform.Y = -20000; UpdateLayout();
            Check(!CardShown(first), "Test setup failed to move the card out of view.");
            var row = (FrameworkElement)treeItems[first.Id].Template.FindName("Row", treeItems[first.Id]);
            // PreviewMouseDown tunnels through the tree, which re-raises it as PreviewMouseLeftButtonDown.
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(CardShown(first), "Clicking the selected tree row must bring its card back into view.");

            // A selection the tree cannot show leaves no stale highlight.
            Search.Text = last.Name; ApplySearch(); selected = first; UpdateSelection();
            Check(DeviceTree.SelectedItem == null, "Tree kept a stale highlight for a filtered selection.");
            Search.Clear(); ApplySearch();
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); snapshot = savedSnapshot; selected = savedSelection;
            FitClick(this, new RoutedEventArgs()); ShowDetails();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    // Device notifications reach the window, settle into one rescan, queue behind a running
    // scan, and report what was connected or disconnected.
    private async Task VerifyDeviceWatch()
    {
        if (!demo) return;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SendMessage(hwnd, WmDeviceChange, DbtDevNodesChanged, 0);
        Check(!deviceSettle.IsEnabled, "Device changes must not rescan the sample topology.");
        demo = false;
        try
        {
            foreach (int change in new[] { DbtDevNodesChanged, DbtDeviceArrival, DbtDeviceRemoveComplete }) SendMessage(hwnd, WmDeviceChange, change, 0);
            Check(deviceSettle.IsEnabled, "Device change notifications did not reach the window.");
        }
        finally { demo = true; deviceSettle.Stop(); }

        int version = refreshIndicatorVersion;
        for (int i = 0; i < 5; i++) { QueueDeviceRescan(); await Task.Delay(60); }
        Check(refreshIndicatorVersion == version, "A device change must wait for the burst to settle.");
        await Task.Delay(800);
        while (busy) await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Check(refreshIndicatorVersion == version + 1, "A burst of device changes must rescan exactly once.");

        busy = true; await RescanAfterDeviceChange(); busy = false;
        Check(rescanQueued, "A change during a scan must queue another scan.");
        await Refresh();
        Check(!rescanQueued && deviceSettle.IsEnabled, "The queued scan did not start after the running one.");
        deviceSettle.Stop();

        const string keyboardId = "demo/root/3";
        var present = Occupants(snapshot);
        string name = snapshot.Nodes.First(n => n.Id == keyboardId).DisplayName;
        var unplugged = DemoData.Create();
        unplugged.Nodes.First(n => n.Id == keyboardId).Kind = "Empty port";
        StatusText.Text = ""; ReportConnections(present, Occupants(unplugged));
        Check(StatusText.Text.Contains("Disconnected " + name), "Unplugging was not reported.");
        int rings = Graph.Children.OfType<Border>().Count(b => Panel.GetZIndex(b) == 3);
        StatusText.Text = ""; ReportConnections(Occupants(unplugged), present);
        Check(StatusText.Text.Contains("Connected " + name), "Plugging in was not reported.");
        Check(Graph.Children.OfType<Border>().Count(b => Panel.GetZIndex(b) == 3) == rings + 1, "A newly connected device was not highlighted.");
        UpdateIssues();
    }

    // Connections are orthogonal, run from their port to their own card with at most two bends,
    // never pass behind a card, and never cross or touch one another. Rows keep port order unwrapped.
    private void VerifyWireRouting()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;
        static bool Within(double v, double a, double b) => v >= Math.Min(a, b) - 0.01 && v <= Math.Max(a, b) + 0.01;
        static bool OnEdge(Rect r, Point p) => (Near(p.X, r.Left) || Near(p.X, r.Right)) && Within(p.Y, r.Top, r.Bottom) || (Near(p.Y, r.Top) || Near(p.Y, r.Bottom)) && Within(p.X, r.Left, r.Right);
        // Open intervals: touching a card's edge is how a connection meets it.
        static bool Inside(Rect r, Point a, Point b) => Math.Max(a.X, b.X) > r.Left + 0.01 && Math.Min(a.X, b.X) < r.Right - 0.01 && Math.Max(a.Y, b.Y) > r.Top + 0.01 && Math.Min(a.Y, b.Y) < r.Bottom - 0.01;
        static bool Touch((Point A, Point B) s, (Point A, Point B) t)
        {
            bool sv = Near(s.A.X, s.B.X), tv = Near(t.A.X, t.B.X);
            if (sv && tv) return Near(s.A.X, t.A.X) && Within(s.A.Y, t.A.Y, t.B.Y) | Within(s.B.Y, t.A.Y, t.B.Y) | Within(t.A.Y, s.A.Y, s.B.Y);
            if (!sv && !tv) return Near(s.A.Y, t.A.Y) && Within(s.A.X, t.A.X, t.B.X) | Within(s.B.X, t.A.X, t.B.X) | Within(t.A.X, s.A.X, s.B.X);
            var (v, h) = sv ? (s, t) : (t, s);
            return Within(v.A.X, h.A.X, h.B.X) && Within(h.A.Y, v.A.Y, v.B.Y);
        }
        var boxes = cards.ToDictionary(c => c.Key, c => new Rect(c.Value.Point, new Size(c.Value.Card.Width, c.Value.Card.Height)));
        var segments = new List<(string Id, Point A, Point B)>();
        foreach (var (id, route) in wireRoutes)
        {
            var parent = FindPath(id).SkipLast(1).Last();
            Check(boxes.ContainsKey(id) && boxes.ContainsKey(parent.Id), $"Connection {id} is missing a card at one end.");
            Check(route.Count <= 4, $"Connection {id} bends more than twice.");
            Check(portAnchors.TryGetValue(id, out var port) ? (route[0] - port).Length < 0.01 : OnEdge(boxes[parent.Id], route[0]), $"Connection {id} does not start at its port.");
            Check(OnEdge(boxes[id], route[^1]), $"Connection {id} does not end on its card.");
            for (int i = 1; i < route.Count; i++)
            {
                var (a, b) = (route[i - 1], route[i]);
                Check(Near(a.X, b.X) || Near(a.Y, b.Y), $"Connection {id} has a diagonal segment.");
                foreach (var (other, box) in boxes) Check(!Inside(box, a, b), $"Connection {id} passes behind the {other} card.");
                segments.Add((id, a, b));
            }
        }
        for (int i = 0; i < segments.Count; i++)
            for (int j = i + 1; j < segments.Count; j++)
                if (segments[i].Id != segments[j].Id)
                    Check(!Touch((segments[i].A, segments[i].B), (segments[j].A, segments[j].B)), $"Connections {segments[i].Id} and {segments[j].Id} cross or touch.");
        foreach (var node in snapshot.Nodes.Where(n => cards.ContainsKey(n.Id)))
        {
            var kids = Children(node).Select(c => boxes[c.Id]).ToList();
            if (kids.Count < 2) continue;
            bool stacked = stackedHubs.Contains(node.Id);
            // Staircases read top to bottom; rows read along the cross axis and share one flow position.
            var order = kids.Select(r => stacked ? r.Y : horizontalTree ? r.Y : r.X).ToList();
            Check(order.Zip(order.Skip(1)).All(p => p.First < p.Second), $"Children of {node.Id} are out of port order.");
            if (!stacked) Check(kids.All(r => Near(horizontalTree ? r.X : r.Y, horizontalTree ? kids[0].X : kids[0].Y)), $"Children of {node.Id} wrapped onto another row.");
        }
    }

    private void VerifyCrowdedRouting()
    {
        var savedSnapshot = snapshot;
        bool savedHorizontal = horizontalTree;
        double savedWidth = layoutWidth;
        try
        {
            snapshot = DemoData.Create();
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub");
            for (int i = 5; i <= 16; i++)
                hub.Children.Add(new UsbNode { Id = hub.Id + "/routing/" + i, Kind = "Device", Name = "Routing device " + i, Port = i });
            hub.PortCount = 16;
            // The root mixes end devices with a hub mid-row, so wires fan out in both directions.
            var root = snapshot.Nodes.First(n => n.Kind == "Root hub");
            var nested = new UsbNode { Id = root.Id + "/7", Kind = "Hub", Name = "Nested hub", Port = 7, PortCount = 3 };
            for (int i = 1; i <= 3; i++) nested.Children.Add(new UsbNode { Id = nested.Id + "/" + i, Kind = "Device", Name = "Nested device " + i, Port = i });
            root.Children.Add(nested);
            foreach (int port in new[] { 5, 6, 8, 9, 10 }) root.Children.Add(new UsbNode { Id = root.Id + "/" + port, Kind = "Device", Name = "Root device " + port, Port = port });
            root.PortCount = 10;
            // A second controller with a hub chain, so controllers arrange side by side or wrap.
            var chainRoot = new UsbNode { Id = "second/root", Kind = "Root hub", Name = "Root hub", PortCount = 4 };
            var outer = new UsbNode { Id = "second/root/1", Kind = "Hub", Name = "Chain hub", Port = 1, PortCount = 4 };
            outer.Children.Add(new UsbNode { Id = "second/root/1/4", Kind = "Hub", Name = "Inner hub", Port = 4, PortCount = 4, Children = [new UsbNode { Id = "second/root/1/4/2", Kind = "Device", Name = "Chain device", Port = 2 }] });
            chainRoot.Children.AddRange([outer, new UsbNode { Id = "second/root/3", Kind = "Device", Name = "Second device", Port = 3 }]);
            snapshot.Controllers.Add(new UsbNode { Id = "second", Kind = "Controller", Name = "Second controller", Children = [chainRoot] });
            foreach (bool horizontal in new[] { false, true })
            foreach (bool empty in new[] { false, true })
            foreach (double width in new[] { 650.0, 1200.0, 2400.0, 4000.0 })
            {
                horizontalTree = horizontal; EmptyPorts.IsChecked = empty; layoutWidth = width; Draw(); UpdateLayout();
                VerifyWireRouting();
            }
        }
        finally
        {
            snapshot = savedSnapshot; horizontalTree = savedHorizontal; layoutWidth = savedWidth; EmptyPorts.IsChecked = false; Draw();
        }
    }

    private void VerifyIdentityUi()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        var savedSnapshot = snapshot; var savedSelection = selected; var savedLabels = deviceLabels;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsbAtlas-ui-label-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            snapshot = DemoData.Create(); deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json"));
            selected = snapshot.Controllers[0]; Draw(); ShowDetails(); UpdateLayout();
            var text = Descendants(Details).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(text.Contains("Port support") && text.Any(t => t.Contains("USB 3.x")), "Host inspector lost reported port protocols.");
            Check(text.Contains("Supply capacity") && text.Contains("Unknown · not measured") && !text.Contains("Negotiated link"), "Host inspector must distinguish unknown supply from peripheral metrics.");
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub"); SelectNode(hub); UpdateLayout();
            var editor = Details.Children.OfType<Expander>().First(); editor.IsExpanded = true; UpdateLayout();
            var input = Descendants(editor).OfType<TextBox>().Single(); input.Text = "Dell monitor KVM";
            Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Save label").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
            Check(hub.UserLabel == "Dell monitor KVM" && hub.Name == "Studio desktop hub", "Label editor overwrote reported identity or failed to save.");
            Check(Descendants(cards[hub.Id].Card).OfType<TextBlock>().Any(t => t.Text == "Dell monitor KVM"), "Saved label did not reach graph card.");
            Search.Text = "Dell monitor KVM"; ApplySearch(); Check(matches.Count == 1 && selected?.Id == hub.Id, "Saved labels must be searchable.");
            Search.Clear(); ApplySearch(); SelectNode(hub); UpdateLayout();
            deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json"));
            var refreshed = DemoData.Create(); deviceLabels.Apply(refreshed);
            Check(refreshed.Nodes.Single(n => n.Id == hub.Id).UserLabel == "Dell monitor KVM", "Labels did not survive a fresh snapshot and store reload.");
            editor = Details.Children.OfType<Expander>().First(); editor.IsExpanded = true; UpdateLayout();
            Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(hub.UserLabel == "" && hub.DisplayName == "Studio desktop hub", "Label Reset failed to restore the detected name.");
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); snapshot = savedSnapshot; selected = savedSelection; deviceLabels = savedLabels;
            Draw(); ShowDetails(); UpdateIssues();
            System.IO.Directory.Delete(directory, true);
        }
    }
    private async Task VerifyRefreshUi()
    {
        // Demo refresh completes immediately, exercising the shortest possible scan.
        if (!demo) return;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.F5) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Search.RaiseEvent(key);
        Check(key.Handled && busy && RefreshProgress.Visibility == Visibility.Visible && RefreshProgress.Opacity == 1, "F5 must show progress immediately, including from search.");
        int version = refreshIndicatorVersion;
        await Refresh();
        Check(refreshIndicatorVersion == version, "Repeated refresh must not start an overlapping scan.");
        while (busy) await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Check(RefreshButton.IsEnabled && RefreshProgress.Visibility == Visibility.Visible, "Fast refresh must enable controls while its feedback fades.");
        // Restart while the previous scan is fading; its completion must not hide the new bar.
        await Task.Delay(160);
        var restarted = Refresh();
        Check(RefreshProgress.Visibility == Visibility.Visible && RefreshProgress.Opacity == 1, "Refresh during fade must restore full visibility.");
        await restarted;
        await Task.Delay(170);
        Check(RefreshProgress.Visibility == Visibility.Visible, "An earlier fade hid a newer refresh indicator.");
        await Task.Delay(250);
        Check(RefreshProgress.Visibility == Visibility.Collapsed && !RefreshProgress.IsIndeterminate, "Progress animation must stop after fading out.");
    }
    private void VerifyCompactUi()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        bool savedHorizontal = horizontalTree, savedCompact = Compact;
        try
        {
            snapshot = DemoData.Create();
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub");
            hub.ScanIncomplete = true; hub.SpeedLimited = true;
            for (int i = 5; i <= 33; i++) hub.Children.Add(new UsbNode { Id = hub.Id + "/" + i, Kind = "Empty port", Name = "Available port " + i, Port = i, Status = "Empty" });
            hub.PortCount = 33;
            hub.Name = "Long hub identity with several words and USB generation information";
            hub.NameSource = "USB ID lookup";
            hub.UserLabel = "Dell monitor KVM with a longer personal label";
            selected = hub; EmptyPorts.IsChecked = true;
            foreach (bool compact in new[] { true, false })
            foreach (bool horizontal in new[] { false, true })
            {
                CompactDensity.IsChecked = compact; horizontalTree = horizontal; FitClick(this, new RoutedEventArgs()); UpdateLayout();
                VerifyWireRouting();
                var items = cards.Values.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var a = items[i]; var bounds = new Rect(a.Point, new Size(a.Card.Width, a.Card.Height));
                    Check(bounds.Right <= Graph.Width + 1 && bounds.Bottom <= Graph.Height + 1, "Variable-height card exceeds graph bounds.");
                    Check(a.Card.Child.DesiredSize.Height <= a.Card.Height - a.Card.Padding.Top - a.Card.Padding.Bottom - a.Card.BorderThickness.Top - a.Card.BorderThickness.Bottom + 1, "Card content exceeds its allocated height.");
                    for (int j = i + 1; j < items.Count; j++)
                        Check(!bounds.IntersectsWith(new Rect(items[j].Point, new Size(items[j].Card.Width, items[j].Card.Height))), "Variable-height cards overlap.");
                    foreach (var child in Children((UsbNode)a.Card.Tag))
                        Check(horizontal ? cards[child.Id].Point.X >= bounds.Right + TopologyLayout.LevelGap : cards[child.Id].Point.Y >= bounds.Bottom + TopologyLayout.LevelGap, "Variable-height parent overlaps its children.");
                }
                Check(portSlots.Count == 31 && cards.Values.All(c => ((UsbNode)c.Card.Tag).Kind != "Empty port"), "Empty ports must render as slots, not full cards.");
                foreach (var (id, slot) in portSlots.Concat(connectedPorts))
                {
                    var edge = horizontal ? new Point(Canvas.GetLeft(slot) + slot.Width, Canvas.GetTop(slot) + slot.Height / 2)
                        : new Point(Canvas.GetLeft(slot) + slot.Width / 2, Canvas.GetTop(slot) + slot.Height);
                    Check((edge - portAnchors[id]).Length < 0.01, "Port graphic must meet its connection anchor.");
                    if (wires.TryGetValue(id, out var wire))
                        Check((((PathGeometry)wire.Data).Figures[0].StartPoint - edge).Length < 0.01, "Connection must start at its own port graphic.");
                }
            }
            horizontalTree = false; CompactDensity.IsChecked = true; EmptyPorts.IsChecked = false; FitClick(this, new RoutedEventArgs());
            var target = snapshot.Nodes.First(n => n.Kind == "Device");
            var originalCard = cards[target.Id].Card;
            SelectNode(target);
            Check(ReferenceEquals(originalCard, cards[target.Id].Card), "Selecting a node rebuilt the graph.");
            originalCard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(ReferenceEquals(originalCard, cards[target.Id].Card) && selected?.Id == target.Id, "Keyboard activation replaced the focused card.");
            folded.Add(snapshot.Controllers[0].Id);
            PanTransform.X = 250; PanTransform.Y = -800;
            Search.Text = "Available port"; ApplySearch();
            Check(matches.Count == 31 && portSlots.Count == 31, "Searching hidden empty ports must reveal all matching slots.");
            Check(PanTransform.X == 0 && PanTransform.Y == 0 && selected?.Kind == "Empty port", "Search must reset panning and select a match.");
            var first = selected!.Id; NextMatch(1); Check(selected!.Id != first, "Next result failed."); NextMatch(-1); Check(selected!.Id == first, "Previous result failed.");
            Search.Text = "Reduced speed"; ApplySearch();
            Check(matches.Count == 1 && selected?.Id == hub.Id, "Issue search did not select affected hardware.");
            UpdateIssues(); Check(IssuesButton.IsEnabled && Issue(hub).Contains("Scan incomplete"), "Incomplete scans must be visible as issues.");
            InspectorClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(InspectorPanel.Visibility == Visibility.Collapsed && InspectorColumn.ActualWidth == 0, "Inspector collapse failed.");
            InspectorClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(InspectorPanel.Visibility == Visibility.Visible && InspectorColumn.ActualWidth >= 260, "Inspector restore failed.");
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); folded.Clear(); expandedPorts.Clear(); EmptyPorts.IsChecked = false;
            snapshot = savedSnapshot; selected = savedSelection; horizontalTree = savedHorizontal; CompactDensity.IsChecked = savedCompact;
            OrientationButton.Content = horizontalTree ? "Horizontal" : "Vertical";
            FitClick(this, new RoutedEventArgs()); ShowDetails(); UpdateIssues();
        }
    }
}

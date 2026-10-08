using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UsbAtlas;

public partial class MainWindow
{
    private void VerifyWarningExplanation()
    {
        var node = snapshot.Nodes.First(n => n.Kind == "Device");
        var oldCount = node.QuickReconnects;
        var oldTimes = node.QuickReconnectTimes;
        var oldSelection = selected;
        var inspectorWasVisible = InspectorPanel.Visibility == Visibility.Visible;
        bool wasOpen = openExplanations.Contains("Unstable connection");
        try
        {
            node.QuickReconnects = 3;
            node.QuickReconnectTimes = [DateTime.Today.AddHours(12), DateTime.Today.AddHours(12).AddMinutes(1), DateTime.Today.AddHours(12).AddMinutes(2)];
            if (inspectorWasVisible) InspectorClick(this, new RoutedEventArgs());
            var badge = WarningBadge(node, Severity.Warning, "Unstable connection");
            badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var explanationText = ExplanationText("Unstable connection");
            if (selected != node || InspectorPanel.Visibility != Visibility.Visible || !explanationText.Contains("3 times this session") || !explanationText.Contains("12:02:00") || !explanationText.Contains("restarted"))
                throw new Exception("Clicking a warning must reveal its device and explain the trigger, observed reconnects and session lifetime.");
            var panel = (Border)Details.Children.OfType<FrameworkElement>().Single(e => Equals(e.Tag, "warning:Unstable connection"));
            if (((StackPanel)panel.Child).Children.OfType<StackPanel>().Single(p => Equals(p.Tag, ExplanationMoreTag)).Visibility != Visibility.Visible)
                throw new Exception("Clicking a warning must open its whole explanation.");
            CaptureUi("warning-preview.png");
        }
        finally
        {
            node.QuickReconnects = oldCount; node.QuickReconnectTimes = oldTimes;
            if (!wasOpen) openExplanations.Remove("Unstable connection");
            selected = oldSelection; ShowDetails();
            if (!inspectorWasVisible && InspectorPanel.Visibility == Visibility.Visible) InspectorClick(this, new RoutedEventArgs());
        }
    }
    // The text of an issue's explanation panel in Properties, or "" when it has none.
    private string ExplanationText(string issue)
    {
        static IEnumerable<string> Texts(object? element) => element switch
        {
            TextBlock text => [text.Text],
            Panel panel => panel.Children.Cast<object>().SelectMany(Texts),
            Decorator decorator => Texts(decorator.Child),
            _ => []
        };
        return string.Join(" ", Details.Children.OfType<FrameworkElement>().Where(e => Equals(e.Tag, "warning:" + issue)).SelectMany(Texts));
    }
    // A USB 3 hub in a monitor whose USB 3 side didn't connect: a calm note while it slows nothing, explained
    // above the rows in plain words with the likely causes for USB-C; a warning once it holds a device back.
    private void VerifySpeedExplanation()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        try
        {
            snapshot = DemoData.Create();
            var monitor = snapshot.Nodes.First(n => n.Id == "demo/root/5");
            // Its socket's USB 3 half, port 7, is empty, so detection finds the USB 3 side missing.
            monitor.SpeedLimited = true; monitor.Connector = "USB-C"; monitor.CompanionId = "demo/root/7";
            HubRelationships.Analyze(snapshot);
            Check(monitor.Usb3SideMissing && !monitor.Usb3SideFailed, "A USB 2 hub side whose socket's USB 3 half is empty must be found missing its USB 3 side.");
            Draw(); SelectNode(monitor); UpdateLayout();
            Check(Issues(monitor).Contains((Severity.Note, "Running at USB 2")), "A hub that slows nothing plugged into it must be a calm note.");
            Check(VisualDescendants(cards[monitor.Id].Card).OfType<Border>().Any(b => b.Background == Brush("NoteSurface")), "The note must use the calm badge on the card.");
            // The missing USB 3 side shows on the canvas: a dashed stub on the empty half of its socket, in its connection's ink.
            var stub = Graph.Children.OfType<System.Windows.Shapes.Line>().SingleOrDefault(l => Equals(l.Tag, MissingUsb3Tag));
            Check(stub != null && (new Point(stub.X1, stub.Y1) - portAnchors["demo/root/7"]).Length < 0.01 && stub.Stroke == Brush(NodeVisuals.WireInk(monitor)) && stub.StrokeDashArray.Count > 0
                && new Vector(stub.X2 - stub.X1, stub.Y2 - stub.Y1).Length < TopologyLayout.Stub, "A hub whose USB 3 side didn't connect must show a short dashed stub on the empty USB 3 half of its socket, in its connection's ink.");
            var text = ExplanationText("Running at USB 2");
            Check(text.Contains("connected at USB 2 (480 Mb/s)") && text.Contains("Does it affect you?") && text.Contains("Not right now") && text.Contains("USB-C Prioritization") && text.Contains("charging cables"),
                "The explanation must say what is happening, that nothing is affected, and the likely USB-C causes.");
            var panel = Details.Children.OfType<FrameworkElement>().First(e => Equals(e.Tag, "warning:Running at USB 2"));
            var firstRow = Details.Children.OfType<FrameworkElement>().First(e => Equals(e.Tag, "field"));
            Check(Details.Children.IndexOf(panel) < Details.Children.IndexOf(firstRow), "The explanation must come before the data rows.");
            var drive = monitor.Children[0]; drive.SpeedLimited = true;
            Draw(); SelectNode(drive); UpdateLayout();
            Check(Issues(monitor).Contains((Severity.Warning, "Running at USB 2")), "A hub that holds a faster device back must warn.");
            text = ExplanationText("Running at USB 2");
            Check(text.Contains("Yes: its transfers are limited to USB 2 speed") && text.Contains("The hub it's plugged into runs at USB 2") && text.Contains("Fix that hub's USB 3 connection"),
                "A held-back device must point to the hub that slows it.");
            CaptureUi("speed-explanation-preview.png");
            // The slow links show on the canvas without reading a card: dashed and amber once they hold
            // something back, and as wide as their rate, beside a faster hub's wider link.
            SelectNode(snapshot.Controllers[0]); UpdateLayout();
            foreach (var slow in new[] { monitor, drive })
                Check(wires[slow.Id].StrokeDashArray is { Count: > 0 } && wires[slow.Id].Stroke == Brush("Warning"), $"The slow link to {slow.Name} must be dashed amber.");
            Check(wires[monitor.Id].StrokeThickness == 2 && wires["demo/root/1"].StrokeThickness == 3 && wires["demo/root/1"].StrokeDashArray == null, "Links must be as wide as their rates.");
            VerifyWireRouting();
            // With only notes, the issues button and status bar count them calmly, apart from issues.
            var calmHub = new UsbNode { Id = "calm/root/1", Kind = "Hub", Name = "Monitor hub", Port = 1, LinkMbps = 480, UsbVersion = "USB 2.10", SpeedLimited = true, Usb3SideMissing = true, Connector = "USB-C",
                Children = [new UsbNode { Id = "calm/root/1/1", Kind = "Device", Name = "Keyboard", DeviceType = "Keyboard", Port = 1, LinkMbps = 12 }] };
            snapshot = new Snapshot { Controllers = [new UsbNode { Id = "calm", Kind = "Controller", Name = "Host", Children = [new UsbNode { Id = "calm/root", Kind = "Root hub", PortCount = 1, Children = [calmHub] }] }] };
            Draw(); UpdateIssues();
            Check(((DockPanel)IssuesButton.Content).Children.OfType<TextBlock>().Single().Text == "1 note" && StatusText.Text.Contains("No issues detected · 1 note"),
                "With only notes, the issues button and status bar must count them as notes.");
        }
        finally { snapshot = savedSnapshot; selected = savedSelection; Draw(); ShowDetails(); UpdateIssues(); }
    }
    private void VerifySearchInput()
    {
        // The field stands out from the title bar: a recessed fill, an edge at 3:1 or more against the bar
        // (WCAG's minimum for an input's boundary), and a placeholder at 4.5:1 on the fill, in both themes.
        static double Luminance(Color c)
        {
            static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        static double Contrast(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        if (Search.Background != Brush("SearchField") || Search.BorderBrush != Brush("SearchEdge")) throw new Exception("The search box must use the search field's fill and edge.");
        foreach (bool dark in new[] { false, true })
        {
            string theme = dark ? "dark" : "light";
            Color bar = Theme.Of("Surface", dark), field = Theme.Of("SearchField", dark);
            if (field == bar) throw new Exception($"The search field must differ from the title bar in {theme} mode.");
            if (Contrast(Theme.Of("SearchEdge", dark), bar) < 3) throw new Exception($"The search field's edge has {Contrast(Theme.Of("SearchEdge", dark), bar):0.0}:1 against the title bar in {theme} mode; it needs 3:1.");
            if (Contrast(Theme.Of("TextSecondary", dark), field) < 4.5) throw new Exception($"The search placeholder has {Contrast(Theme.Of("TextSecondary", dark), field):0.0}:1 on the field in {theme} mode; it needs 4.5:1.");
        }
        var original = Search.Text;
        try
        {
            Search.Text = "USB webcam gyjp 123";
            Search.UpdateLayout(); UpdateLayout();
            var start = Search.GetRectFromCharacterIndex(0);
            var end = Search.GetRectFromCharacterIndex(Search.Text.Length - 1, true);
            if (start.IsEmpty || end.IsEmpty || start.Top < 0 || start.Bottom > Search.ActualHeight - 1 || end.Bottom > Search.ActualHeight - 1)
                throw new Exception("Search text is vertically clipped.");
            if (Math.Abs((start.Top + start.Bottom) / 2 - Search.ActualHeight / 2) > 2)
                throw new Exception("Search text is not vertically centered.");
            if (start.Left < SearchIcon.Margin.Left + SearchIcon.ActualWidth + 4)
                throw new Exception("Search text overlaps the magnifying glass.");
            // One bar: search centered, clear of the title on the left and the buttons on the right.
            double left = SearchBox.TranslatePoint(new Point(), TitleBar).X, right = left + SearchBox.ActualWidth;
            if (Math.Abs(left + right - TitleBar.ActualWidth) > 1)
                throw new Exception("Search is not centered in the title bar.");
            if (left < TitleLeft.ActualWidth + 8 || right > TitleBar.ActualWidth - TitleRight.ActualWidth - 8)
                throw new Exception("Search runs under the title bar's buttons.");
            var box = (FrameworkElement)Search.Parent;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)box.ActualWidth, (int)box.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(box);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create("search-preview.png");
            png.Save(stream);
        }
        finally { Search.Text = original; searchTimer.Stop(); }
    }
    // Narrowing the window folds the device tree away before the canvas gets less than half the width, and
    // widening brings it back, unless it was shown or hidden by hand in between. The legend wraps by group.
    private void VerifySmallWindow()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        double width = Width, treeWidth = TreePanel.Visibility == Visibility.Visible ? TreeColumn.ActualWidth : treePanelWidth, inspector = InspectorColumn.ActualWidth;
        bool Folded() => TreePanel.Visibility == Visibility.Collapsed && TreeButton.Visibility == Visibility.Visible;
        double Canvas() => ((Grid)TreePanel.Parent).ColumnDefinitions[2].ActualWidth;
        void Resize(double to) { Width = to; UpdateLayout(); }
        try
        {
            Resize(1500);
            Check(!Folded(), "The device tree must show at the default window width.");
            Resize(1050);
            Check(Folded() && treeFoldedForRoom && Canvas() >= ActualWidth / 2 - 20 && Canvas() > InspectorColumn.ActualWidth, $"At the minimum width the tree must fold so the canvas has the most room ({Canvas():0} px).");
            foreach (var group in SocketLegend.Children.OfType<FrameworkElement>())
            {
                var right = group.TranslatePoint(new Point(group.ActualWidth, 0), SocketLegend).X;
                Check(Equals(group.Tag, NodeVisuals.LegendGroupTag) && right <= SocketLegend.ActualWidth + 0.5, "Every legend group must show whole, wrapping when the legend is narrow.");
            }
            Resize(1500);
            Check(!Folded() && !treeFoldedForRoom, "Widening the window must bring the folded tree back.");
            // Dragging the tree wide leaves it shown, however little room that leaves the canvas.
            TreeSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
            TreeSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(600, 0));
            TreeSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(600, 0, false));
            UpdateLayout();
            Check(!Folded() && TreeColumn.ActualWidth > 500, "Dragging the tree's splitter must never fold it.");
            TreeColumn.Width = new GridLength(280); UpdateLayout();
            Resize(1050); TreePanelClick(this, new RoutedEventArgs()); Resize(1060);
            Check(!Folded(), "A tree shown by hand must stay shown while the window stays narrow.");
            Resize(1500); Resize(1050);
        }
        finally
        {
            Resize(width); if (Folded()) TreePanelClick(this, new RoutedEventArgs());
            TreeColumn.Width = new GridLength(treeWidth); InspectorColumn.Width = new GridLength(inspector); UpdateLayout(); FitSidePanels();
        }
    }
    private void VerifyDeviceTree()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var originalSelection = selected;
        var target = snapshot.Nodes.First(n => n.Kind == "Device");
        var parent = CardNode(FindPath(target.Id).SkipLast(1).Last());
        // A merged root hub is part of its host's row, as on the canvas.
        Check(treeItems.Count == snapshot.Nodes.Count(n => n.Kind != "Empty port" && !mergedHosts.ContainsKey(n.Id)), "Tree must include every connected device.");
        treeItems[parent.Id].IsExpanded = false;
        Draw();
        Check(!treeItems[parent.Id].IsExpanded, "Graph redraw lost tree expansion state.");
        folded.Add(parent.Id); Draw();
        treeItems[target.Id].IsSelected = true; UpdateLayout();
        Check(selected?.Id == target.Id && cards.ContainsKey(target.Id) && !folded.Contains(parent.Id), "Tree selection must reveal the graph card.");
        SelectNode(parent);
        Check(DeviceTree.SelectedItem == treeItems[parent.Id], "Graph selection must select the corresponding tree item.");
        TreePanelClick(this, new RoutedEventArgs()); UpdateLayout();
        Check(TreePanel.Visibility == Visibility.Collapsed && TreeColumn.ActualWidth == 0 && TreeButton.Visibility == Visibility.Visible && TreeButton.Content as string == "Show devices", "Tree panel did not collapse.");
        TreePanelClick(this, new RoutedEventArgs()); UpdateLayout();
        Check(TreePanel.Visibility == Visibility.Visible && TreeColumn.ActualWidth >= 180 && selected?.Id == parent.Id, "Restoring the tree lost its size or selection.");
        Search.Text = target.Name; ApplySearch();
        Check(treeItems.ContainsKey(target.Id) && FindPath(target.Id).All(n => treeItems.ContainsKey(CardNode(n).Id)), "Filtered tree lost matching device ancestry.");
        Search.Clear(); ApplySearch();
        var empties = snapshot.Nodes.Where(n => n.Kind == "Empty port").ToList();
        Check(empties.All(n => !treeItems.ContainsKey(n.Id)), "Tree must list empty ports only for a search.");
        if (empties.Count > 0)
        {
            Search.Text = empties[0].Name; ApplySearch();
            Check(treeItems.ContainsKey(empties[0].Id), "Tree must list empty ports that match a search.");
            Search.Clear(); ApplySearch();
        }
        collapsedTreeBranches.Clear(); treeSignature = ""; Draw();
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
            last.Name = "Sync device 100 with an unusually long reported product name that must trim instead of widening the tree";
            selected = null; FitClick(this, new RoutedEventArgs()); UpdateLayout();
            var scroller = (ScrollViewer)DeviceTree.Template.FindName("_tv_scrollviewer_", DeviceTree);
            // Measure against the content host: the tree's padding sits between it and the scroller's edge.
            var host = (FrameworkElement)scroller.Template.FindName("PART_ScrollContentPresenter", scroller);
            bool RowShown(UsbNode node)
            {
                var row = (FrameworkElement)treeItems[node.Id].Template.FindName("Row", treeItems[node.Id]);
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
            // Long names trim to the panel, so the tree never scrolls sideways and rows stay left-aligned.
            var name = ((DockPanel)treeItems[last.Id].Header).Children.OfType<TextBlock>().Single();
            Check(scroller.HorizontalOffset == 0 && scroller.ComputedHorizontalScrollBarVisibility != Visibility.Visible
                && name.TranslatePoint(new Point(name.ActualWidth, 0), host).X <= host.ActualWidth + 0.5, "Long tree names must trim instead of scrolling the tree sideways.");

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

            // Dragging the splitter widens the tree as far as the graph's minimum width allows.
            var graphColumn = ((Grid)TreePanel.Parent).ColumnDefinitions[2];
            double start = TreeColumn.ActualWidth, room = graphColumn.ActualWidth - graphColumn.MinWidth;
            TreeSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
            TreeSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(700, 0));
            TreeSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(700, 0, false));
            UpdateLayout();
            Check(Math.Abs(TreeColumn.ActualWidth - (start + Math.Min(700, room))) < 1, $"Tree panel widened to {TreeColumn.ActualWidth:0}px; expected {start + Math.Min(700, room):0}px.");
            TreeColumn.Width = new GridLength(start); UpdateLayout();
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
        // Every connection is drawn as its link, whatever is selected: width from its rate, dashes when slower
        // than its device supports, and its link's ink unless it is on the selected path.
        var chain = FindPath(selected?.Id ?? "").Select(n => n.Id).ToHashSet();
        foreach (var (id, wire) in wires)
        {
            var node = (UsbNode)wire.Tag;
            Check(node.Id == id && wire.StrokeThickness == NodeVisuals.WireWidth(node), $"Connection {id} must be as wide as its link rate.");
            Check((wire.StrokeDashArray is { Count: > 0 }) == NodeVisuals.SlowLink(node), $"Connection {id} must be dashed exactly when its link is slower than its device supports.");
            Check(wire.Stroke == Brush(chain.Contains(id) ? "Accent" : NodeVisuals.WireInk(node)), $"Connection {id} has the wrong ink.");
        }
        var boxes = cards.ToDictionary(c => c.Key, c => new Rect(c.Value.Point, new Size(c.Value.Card.Width, c.Value.Card.Height)));
        var all = boxes.ToList();
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                Check(!all[i].Value.IntersectsWith(all[j].Value), $"Cards {all[i].Key} and {all[j].Key} overlap.");
        var segments = new List<(string Id, Point A, Point B)>();
        foreach (var (id, route) in wireRoutes)
        {
            var parent = DrawnAs(FindPath(id).SkipLast(1).Last()); var card = DrawnAs(FindPath(id).Last()).Id;
            Check(boxes.ContainsKey(card) && boxes.ContainsKey(parent.Id), $"Connection {id} is missing a card at one end.");
            Check(route.Count <= (snappedWires.Contains(id) ? 5 : 4), $"Connection {id} has too many bends.");
            Check(portAnchors.TryGetValue(id, out var port) ? (route[0] - port).Length < 0.01 : OnEdge(boxes[parent.Id], route[0]), $"Connection {id} does not start at its port.");
            Check(OnEdge(boxes[card], route[^1]), $"Connection {id} does not end on its card.");
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
            if (DrawsSockets && SnappedStages(node).Count > 1 || snappedWires.Contains(node.Id)) continue;
            var kids = Children(node).Select(c => boxes[c.Id]).ToList();
            if (kids.Count < 2) continue;
            // Rows read along the cross axis in socket order and share one flow position. A packed group
            // alternates between two flow positions, each card of the second centered on the gap after
            // its neighbor in the first, so its connection passes between two cards.
            double Along(Rect r) => horizontalTree ? r.Y : r.X;
            double Flow(Rect r) => horizontalTree ? r.X : r.Y;
            double Span(Rect r) => horizontalTree ? r.Height : r.Width;
            var order = kids.Select(Along).ToList();
            Check(order.Zip(order.Skip(1)).All(p => p.First < p.Second), $"Children of {node.Id} are out of socket order.");
            if (!packedHubs.Contains(node.Id)) { Check(kids.All(r => Near(Flow(r), Flow(kids[0]))), $"Children of {node.Id} wrapped onto another row."); continue; }
            Check(horizontalTree && detail == CardDetail.Far, $"Children of {node.Id} are packed outside far cards in the horizontal layout.");
            Check(kids.Select((r, i) => Near(Flow(r), Flow(kids[i % 2]))).All(same => same) && Flow(kids[1]) > Flow(kids[0]) + kids[0].Width,
                $"Packed children of {node.Id} must alternate between exactly two rows.");
            for (int i = 1; i < kids.Count; i += 2)
            {
                double center = Along(kids[i]) + Span(kids[i]) / 2, before = Along(kids[i - 1]) + Span(kids[i - 1]);
                Check(center > before + 0.01 && (i + 1 >= kids.Count || center < Along(kids[i + 1]) - 0.01), $"Packed child {Children(node)[i].Id} must sit on the gap between two cards of the first row.");
            }
        }
    }

    private void VerifyCrowdedRouting()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
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
            // The root mixes end devices with a hub mid-row, so wires fan out in both directions. It builds on
            // the sample's first eight root ports, so the wheel base on port 9 makes way.
            var root = snapshot.Nodes.First(n => n.Kind == "Root hub");
            root.Children.RemoveAll(c => c.Port == 9);
            var nested = new UsbNode { Id = root.Id + "/10", Kind = "Hub", Name = "Nested hub", Port = 10, PortCount = 3 };
            for (int i = 1; i <= 3; i++) nested.Children.Add(new UsbNode { Id = nested.Id + "/" + i, Kind = "Device", Name = "Nested device " + i, Port = i });
            root.Children.Add(nested);
            foreach (int port in new[] { 11, 12, 13, 14 }) root.Children.Add(new UsbNode { Id = root.Id + "/" + port, Kind = "Device", Name = "Root device " + port, Port = port });
            // Port 14's device is on the USB 3 half of port 9's socket, so it moves ahead of the nested hub.
            root.Children.Add(new UsbNode { Id = root.Id + "/9", Kind = "Empty port", Name = "Available port 9", Port = 9, CompanionId = root.Id + "/14" });
            root.Children.First(c => c.Port == 14).CompanionId = root.Id + "/9";
            root.PortCount = 14;
            // A second controller with a hub chain, so controllers arrange side by side or wrap.
            var chainRoot = new UsbNode { Id = "second/root", Kind = "Root hub", Name = "Root hub", PortCount = 4 };
            var outer = new UsbNode { Id = "second/root/1", Kind = "Hub", Name = "Chain hub", Port = 1, PortCount = 4 };
            outer.Children.Add(new UsbNode { Id = "second/root/1/4", Kind = "Hub", Name = "Inner hub", Port = 4, PortCount = 4, Children = [new UsbNode { Id = "second/root/1/4/2", Kind = "Device", Name = "Chain device", Port = 2 }] });
            chainRoot.Children.AddRange([outer, new UsbNode { Id = "second/root/3", Kind = "Device", Name = "Second device", Port = 3 }]);
            snapshot.Controllers.Add(new UsbNode { Id = "second", Kind = "Controller", Name = "Second controller", Children = [chainRoot] });
            // Every level of semantic zoom keeps the same routing rules.
            foreach (var level in Enum.GetValues<CardDetail>())
            foreach (bool horizontal in new[] { false, true })
            foreach (double width in new[] { 650.0, 1200.0, 2400.0, 4000.0 })
            {
                detail = level; horizontalTree = horizontal; layoutWidth = width; Draw(); UpdateLayout();
                VerifyWireRouting();
                var rootOrder = Children(root).Select(c => c.Port).ToList();
                Check(rootOrder.IndexOf(14) == rootOrder.IndexOf(6) + 1 && rootOrder.IndexOf(10) == rootOrder.IndexOf(14) + 1, "A device on a socket's higher-numbered half must sit with its socket.");
                var hosts = snapshot.Controllers.Select(c => cards[c.Id].Point).ToList();
                Check(horizontal ? hosts.All(p => Math.Abs(p.X - hosts[0].X) < 0.01) : hosts[1].Y > hosts[0].Y || hosts[1].X > hosts[0].X, "Independent controller branches must advance to the right or onto a lower row without overlapping.");
            }
        }
        finally
        {
            snapshot = savedSnapshot; horizontalTree = savedHorizontal; layoutWidth = savedWidth; detail = CardDetail.Full; Draw();
        }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    // Every card wears its category's fill, selected or not; hubs, hosts and ports are neutral; only the
    // selected card glows; and category fills and inks stay distinct from one another.
    private void VerifyRoleFills()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        // Cards are neutral; the icon's ink says what a device does.
        foreach (var (id, item) in cards)
        {
            var node = (UsbNode)item.Card.Tag;
            bool marked = id == selected?.Id || item.Card.IsKeyboardFocusWithin || appliedQuery.Length > 0 && Matches(node, appliedQuery);
            Check(item.Card.Background == Brush("NeutralFill") && (marked || item.Card.BorderBrush == Brush("NeutralEdge")), $"Card {id} must be neutral.");
            Check(node.Kind == "Device" || NodeVisuals.Color(node) == "Neutral", $"Hub, host or port card {id} must have a neutral icon.");
            Check(VisualDescendants(item.Card).OfType<System.Windows.Shapes.Path>().First().Fill == Brush(NodeVisuals.Color(node)), $"Card {id}'s icon must wear its category ink.");
            Check(item.Card.Effect != null == (selected != null && id == DrawnAs(selected).Id), $"Only the selected card may glow ({id}).");
        }
        static double Distance(Color x, Color y) => Math.Sqrt(Math.Pow(x.R - y.R, 2) + Math.Pow(x.G - y.G, 2) + Math.Pow(x.B - y.B, 2));
        static double Luminance(Color c)
        {
            static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        static double Contrast(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        var categories = NodeVisuals.Categories;
        // No hue on the canvas means two things (#26): selection is at least 25° in OKLab hue from every category
        // ink, socket color and status color; error red stays clearly apart from the 10 Gb/s tongue's red; and
        // port numbers keep 4.5:1 on every tongue.
        foreach (bool dark in new[] { false, true })
        {
            string theme = dark ? "dark" : "light";
            var accent = Theme.Of("Accent", dark);
            foreach (var other in categories.Concat(["SocketSuperSpeed", "SocketSuperSpeedPlus", "Warning", "Error"]))
                Check(HueGap(accent, Theme.Of(other, dark)) >= 25, $"Selection's teal is only {HueGap(accent, Theme.Of(other, dark)):0}° from {other} in {theme} mode; it needs 25°.");
            Check(OkDistance(Theme.Of("Error", dark), Theme.Of("SocketSuperSpeedPlus", dark)) >= 12, $"Error red is too close to the 10 Gb/s tongue's red in {theme} mode.");
            foreach (var tongue in new[] { "SocketUsb2", "SocketSuperSpeed", "SocketSuperSpeedPlus", "SocketUnknown" })
                Check(Contrast(Theme.Of("OnSocket", dark), Theme.Of(tongue, dark)) >= 4.5, $"Port numbers on {tongue} have under 4.5:1 in {theme} mode.");
        }
        foreach (bool dark in new[] { false, true })
            for (int i = 0; i < categories.Length; i++)
            {
                double contrast = Contrast(Theme.Of(categories[i], dark), Theme.Of("NeutralFill", dark));
                Check(contrast >= 4.5, $"{categories[i]} ink has {contrast:0.0}:1 contrast on cards in {(dark ? "dark" : "light")} mode; it needs 4.5:1.");
                for (int j = i + 1; j < categories.Length; j++)
                    Check(Distance(Theme.Of(categories[i], dark), Theme.Of(categories[j], dark)) > 40, $"{categories[i]} and {categories[j]} inks are too alike to tell apart.");
            }
    }

    // Warning and error colors appear only as semibold text beside a status glyph, error text also on its
    // tinted surface, every issue on a card has its badge, and no role color can be mistaken for a status color.
    private void VerifyStatusStyling()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static bool IsGlyph(DependencyObject d) => d is FrameworkElement { Tag: NodeVisuals.StatusGlyphTag };
        var status = new[] { Brush("Warning"), Brush("Error") };
        var roots = cards.Values.Select(c => (DependencyObject)c.Card).Append(Details).Append(DeviceTree).Append(IssuesButton).Append(FixFirstList);
        foreach (var text in roots.SelectMany(VisualDescendants).OfType<TextBlock>().Where(t => status.Append(Brush("Note")).Contains(t.Foreground)))
            Check(text.FontWeight == FontWeights.SemiBold && text.Parent is Panel row && row.Children.Cast<DependencyObject>().Any(IsGlyph),
                $"\"{text.Text}\" uses a note, warning or error color without the status glyph and weight.");
        // Red alone never says error: error text sits on the error tint, in a badge or a button that wears it.
        bool OnErrorTint(DependencyObject d)
        {
            for (var at = VisualTreeHelper.GetParent(d); at != null; at = VisualTreeHelper.GetParent(at))
                if (at is Border { Background: var b } && b == Brush("ErrorSurface") || at is Control { Background: var c } && c == Brush("ErrorSurface")) return true;
            return false;
        }
        foreach (var text in roots.SelectMany(VisualDescendants).OfType<TextBlock>().Where(t => t.Foreground == Brush("Error")))
            Check(OnErrorTint(text), $"\"{text.Text}\" is in the error color without the error tint behind it.");
        foreach (var (id, item) in cards)
        {
            var node = (UsbNode)item.Card.Tag;
            // A host card also carries its merged root hub's issues.
            int expected = node.Kind is "Controller" or "Root hub" ? OtherIssues(node).Count : Issues(node).Count;
            Check(VisualDescendants(item.Card).Count(IsGlyph) == expected, $"Card {id} must show one status badge per issue.");
        }
        static double Distance(Brush a, Brush b) { var (x, y) = (((SolidColorBrush)a).Color, ((SolidColorBrush)b).Color); return Math.Sqrt(Math.Pow(x.R - y.R, 2) + Math.Pow(x.G - y.G, 2) + Math.Pow(x.B - y.B, 2)); }
        foreach (var category in NodeVisuals.Categories)
            foreach (var severity in status) Check(Distance(Brush(category), severity) > 100, $"{category} is too close to a warning or error color.");
    }

    // Perceptual color: OKLab lightness and the two opponent axes, so hue and distance match what people see.
    private static (double L, double A, double B) OkLab(Color c)
    {
        static double Linear(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b), m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b), s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }
    // How far apart two colors are, on a 0-100 scale, and how far apart their hues are in degrees.
    private static double OkDistance(Color x, Color y) { var (a, b) = (OkLab(x), OkLab(y)); return 100 * Math.Sqrt(Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2)); }
    private static double HueGap(Color x, Color y)
    {
        static double Hue(Color c) { var (_, a, b) = OkLab(c); return (Math.Atan2(b, a) * 180 / Math.PI + 360) % 360; }
        double gap = Math.Abs(Hue(x) - Hue(y)) % 360;
        return Math.Min(gap, 360 - gap);
    }

    // Cards mark link rate, reserved bandwidth and requested power with their glyphs, power problems
    // name themselves, and the inspector carries the same three metrics for every port-level kind.
    private void VerifyPowerUi()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        // Card glyphs sit inline in a trimming TextBlock; inspector glyphs are ordinary children.
        static List<NodeVisuals.Metric> Glyphs(DependencyObject root) => VisualDescendants(root).OfType<TextBlock>()
            .SelectMany(t => t.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Select(c => (DependencyObject)c.Child))
            .Concat(VisualDescendants(root)).OfType<Viewbox>().Where(v => v.Tag is NodeVisuals.MetricGlyphTag).Distinct()
            .Select(v => Enum.Parse<NodeVisuals.Metric>(v.Uid)).ToList();
        var savedSnapshot = snapshot; var savedSelection = selected;
        try
        {
            snapshot = DemoData.Create();
            var flaky = snapshot.Nodes.First(n => n.Id == "demo/root/3"); flaky.QuickReconnects = 3;
            snapshot.Nodes.First(n => n.Id == "demo/root/1/2").SpeedLimited = true;
            Draw(); UpdateLayout();
            foreach (var node in snapshot.Nodes.Where(n => n.Kind is "Device" or "Hub" && n.MaxPowerMa != null && cards.ContainsKey(n.Id)))
                Check(Glyphs(cards[node.Id].Card).SequenceEqual(ShowsPolling(node) ? [NodeVisuals.Metric.Link, NodeVisuals.Metric.Polling, NodeVisuals.Metric.Power] : [NodeVisuals.Metric.Link, NodeVisuals.Metric.Power]),
                    $"Card {node.Id} must mark its link rate, polling rate when it is an input device, and requested power with glyphs.");
            Check(snapshot.Nodes.Count(ShowsPolling) == 4 && !ShowsPolling(snapshot.Nodes.First(n => n.Id == "demo/root/1/2")), "Input devices and game controllers show their polling rate; other devices don't.");
            // One line of figures, link then power; reserved bandwidth is the meter's job, and sockets, not
            // a count, show which ports are used.
            foreach (var (id, item) in cards)
            {
                Check(VisualDescendants(item.Card).OfType<TextBlock>().Count(t => Glyphs(t).Count > 0) <= 1, $"Card {id} must keep its figures on one line.");
                Check(VisualDescendants(item.Card).OfType<TextBlock>().All(t => !new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text.Contains("occupied")), $"Card {id} must not repeat port occupancy as text.");
            }
            Check(Issue(snapshot.Nodes.First(n => n.Id == "demo/root/5")) == "Hub adapter not detected · Over power budget", "Bus-power problems must be listed on the hub.");
            Check(Issue(snapshot.Nodes.First(n => n.Id == "demo/root/5/3")) == "Insufficient power", "A port refused for power must name the fault, not a generic port error.");
            Check(Issue(flaky) == "Unstable connection", "Quick reconnects must be listed as an unstable connection.");
            // Speed and power warnings follow the figures they qualify, speed first; others gather below.
            List<string> Beside(string id, NodeVisuals.Metric metric) => VisualDescendants(cards[id].Card).OfType<WrapPanel>()
                .Where(w => w.Children.OfType<TextBlock>().Any(t => Glyphs(t).Contains(metric)))
                .SelectMany(w => VisualDescendants(w).OfType<TextBlock>().Where(t => t.FontWeight == FontWeights.SemiBold).Select(t => t.Text)).ToList();
            Check(Beside("demo/root/1/2", NodeVisuals.Metric.Link).SequenceEqual(["Running at 5 Gb/s"]), "A slower link than the device supports must sit beside the link rate.");
            // Hubs and streaming devices with a known link and reservation get one meter, directly under
            // their figures: a label in shares of what the link can reserve, above a bar that is solid to
            // what is reserved now and lighter out to the peak. A hub's bar is split into its own part and
            // one per device or hub on its ports, in socket order, which add up to what its link holds.
            int peaked = 0, hovered = 0;
            foreach (var (id, item) in cards)
            {
                var node = (UsbNode)item.Card.Tag;
                var panel = (StackPanel)item.Card.Child;
                var meters = panel.Children.OfType<Grid>().Where(b => b.Tag is NodeVisuals.MeterTag).ToList();
                Check(meters.Count == (ShowsMeter(node) ? 1 : 0), $"Card {id} must show a meter exactly when its reservations matter and are known.");
                // The figures' tooltip describes the meter, or says why the card has none: a keyboard beside a
                // metered microphone must answer the question itself, and say where what it adds does count.
                if (node.Kind is "Device" or "Hub")
                {
                    string help = MetricHelp(node);
                    Check(help.Contains("\nMeter: ") == (meters.Count == 1) && help.Contains("\nNo meter: ") == (meters.Count == 0), $"Card {id}'s figures must explain its meter, or why it has none: \"{help}\".");
                    Check(id switch
                    {
                        "demo/root/3" => help.Contains("No meter: It holds 6.4 kb/s, <1% of the 10.8 Mb/s this link can set aside, the same whether it's idle or in use.") && help.Contains("can't grow") && help.Contains("port of the computer itself"),
                        "demo/root/1/1" => help.Contains("No meter: It reserves no bus time") && help.Contains("Studio desktop hub's link") && help.Contains("It's a part of that hub's meter; pointing at the part rings this card."),
                        "demo/root/5/2" => help.Contains("No meter: It holds 6.4 kb/s") && help.Contains("12 Mb/s link the slower devices on that hub have between them") && help.Contains("part of that hub's meter"),
                        _ => true
                    }, $"Card {id}'s figures must say what it reserves, why that can't fill its link, and whose link it shares: \"{help}\".");
                }
                if (meters.Count == 0) continue;
                var (now, peak, capacity, label) = MeterFor(node)!.Value;
                var text = meters[0].Children.OfType<TextBlock>().Single(t => Equals(t.Tag, NodeVisuals.MeterLabelTag));
                var bars = meters[0].Children.OfType<Grid>().Single(g => Equals(g.Tag, NodeVisuals.MeterBarTag));
                Check(text.Text == label && !label.Contains("b/s") && label.Contains('%'), $"Card {id}'s meter label must give shares, not rates: \"{text.Text}\".");
                Rect Bounds(FrameworkElement e) => e.TransformToAncestor(meters[0]).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                Check(!Bounds(text).IntersectsWith(Bounds(bars)) && Bounds(text).Bottom <= Bounds(bars).Top, $"Card {id}'s meter label must sit above its bar, never on it.");
                var parts = MeterParts(node);
                Check(Math.Abs(parts.Sum(p => p.Now) - now) < 1e-6 && Math.Abs(Math.Max(now, parts.Sum(p => p.Peak)) - peak) < 1e-6, $"Card {id}'s meter parts must add up to what its link holds.");
                if (node.Kind == "Hub") Check(parts.Skip(1).Select(p => p.Node.Port).SequenceEqual(node.Children.Where(c => c.Kind is "Device" or "Hub").Select(c => c.Port).Order()), $"Hub {id}'s meter must have a part for each device or hub on its ports, in socket order.");
                // Drawn widths, not just the requested shares: solid parts first, then each part's peak.
                var columns = bars.ColumnDefinitions;
                double solidWidth = columns.Take(parts.Count).Sum(c => c.ActualWidth), reachWidth = columns.Take(2 * parts.Count).Sum(c => c.ActualWidth);
                double solid = Math.Min(1, parts.Sum(p => p.Now <= 0 ? 0 : Math.Max(p.Now / capacity, 0.01))), reach = Math.Max(solid, Math.Min(1, solid + parts.Sum(p => p.Peak - p.Now) / capacity));
                Check(Math.Abs(solidWidth - solid * bars.ActualWidth) < 0.5 && Math.Abs(reachWidth - reach * bars.ActualWidth) < 0.5, $"Card {id}'s meter must fill to what is reserved and extend to the peak.");
                if (reach > solid + 1e-6) peaked++;
                var segments = bars.Children.OfType<Border>().Skip(1).ToList();
                Check(segments.All(s => s.Background == Brush("Neutral") && (s.Opacity == 1 || s.Opacity == NodeVisuals.MeterPeakOpacity)), $"Card {id}'s meter must be neutral: solid now, lighter to the peak.");
                // Hovering a device's part rings its card; leaving clears it.
                if (node.Kind == "Hub" && parts.Skip(1).FirstOrDefault(p => p.Peak > 0 && cards.ContainsKey(p.Node.Id)) is { } shown && meterSegments.TryGetValue(shown.Node.Id, out var lit))
                {
                    hovered++; lit[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                    var ring = Graph.Children.OfType<Border>().SingleOrDefault(b => Equals(b.Tag, PartRingTag));
                    Check(ring != null && Math.Abs(Canvas.GetLeft(ring) + 4 - cards[shown.Node.Id].Point.X) < 0.01 && lit.All(s => s.Background == Brush("Accent")), $"Hovering a part of hub {id}'s meter must ring {shown.Node.Id}'s card and light its part.");
                    lit[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Check(!Graph.Children.OfType<Border>().Any(b => Equals(b.Tag, PartRingTag)) && lit.All(s => s.Background == Brush("Neutral")), $"Leaving a part of hub {id}'s meter must clear its ring.");
                }
                int at = panel.Children.IndexOf(meters[0]);
                Check(at > 0 && Glyphs(panel.Children[at - 1]).Contains(NodeVisuals.Metric.Link), $"Card {id}'s meter must sit directly under its figures.");
            }
            Check(peaked > 0, "The sample must show a meter that extends to a higher peak.");
            Check(hovered > 0, "The sample must exercise hovering a part of a hub's meter.");
            Check(Beside("demo/root/5", NodeVisuals.Metric.Power).SequenceEqual(["Hub adapter not detected", "Over power budget"]), "Hub power warnings must sit beside its power request.");
            Check(Beside("demo/root/5/1", NodeVisuals.Metric.Power).SequenceEqual(["Power at risk"]), "Power at risk must sit beside the power request.");
            Check(Beside("demo/root/5/3", NodeVisuals.Metric.Power).SequenceEqual(["Insufficient power"]), "A power fault must sit beside the power request.");
            Check(!Beside("demo/root/3", NodeVisuals.Metric.Power).Contains("Unstable connection") && !Beside("demo/root/3", NodeVisuals.Metric.Link).Contains("Unstable connection"), "Other issues stay below the metrics.");
            // A game controller that Windows may suspend says so below its figures, in the game controller hue.
            var wheel = snapshot.Nodes.First(n => n.Id == "demo/root/9");
            Check(Issue(wheel) == PowerSaving.Warning && !Beside(wheel.Id, NodeVisuals.Metric.Link).Contains(PowerSaving.Warning) && OtherIssues(wheel).Any(i => i.Text == PowerSaving.Warning), "Selective suspend on a game controller must be listed below its figures.");
            Check(VisualDescendants(cards[wheel.Id].Card).OfType<System.Windows.Shapes.Path>().First().Fill == Brush("Gaming") && VisualDescendants(cards[wheel.Id].Card).OfType<TextBlock>().Any(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text.Contains("1000 Hz")), "The wheel base card must wear the game controller ink and show its 1000 Hz polling.");
            foreach (var (query, expected) in new[] { ("1000 Hz", new[] { "demo/root/4", "demo/root/9" }), ("Joystick", ["demo/root/9"]), (PowerSaving.Warning, ["demo/root/9"]), ("Single TT", ["demo/root/5"]) })
            {
                Search.Text = query; ApplySearch();
                Check(matches.Select(n => n.Id).SequenceEqual(expected), $"Searching \"{query}\" must find {string.Join(", ", expected)}.");
            }
            Search.Clear(); ApplySearch();
            SelectNode(wheel); LocateClick(this, new RoutedEventArgs()); UpdateLayout();
            CaptureUi("sim-hardware-preview.png");
            string Row(string label) => Details.Children.OfType<Grid>().Where(g => Equals(g.Tag, "field") && ((TextBlock)g.Children[0]).Text == label).Select(g => g.Children[1]).OfType<TextBlock>().Single().Text;
            Check(Row("Polling rate") == "1000 Hz · every 1 ms" && Row("Power saving") == "On", "Properties must show the wheel base's polling rate and power-saving setting.");
            Check(Row("Slower devices") == NotApplicable && Row("Shared link") == NotApplicable, "A device has no transaction translator.");
            // Link use in Properties says why a device's card has no meter: its slice can't grow, or it reserves nothing.
            string LinkUse() => VisualDescendants(Details.Children.OfType<Grid>().Single(g => Equals(g.Tag, "field") && ((TextBlock)g.Children[0]).Text == "Link use")).OfType<TextBlock>().Last().Text;
            Check(LinkUse() == "5% of 10.8 Mb/s · can't grow", $"A game controller's link use must say its slice can't grow: \"{LinkUse()}\".");
            SelectNode(snapshot.Nodes.First(n => n.Id == "demo/root/1/1")); UpdateLayout();
            Check(LinkUse() == "0% of 3.6 Gb/s · reserves nothing", $"A drive's link use must say it reserves nothing: \"{LinkUse()}\".");
            SelectNode(snapshot.Nodes.First(n => n.Id == "demo/root/1/2")); UpdateLayout();
            Check(LinkUse() == "3% of 3.6 Gb/s", $"A camera's link use needs no qualifier, since its card has the meter: \"{LinkUse()}\".");
            SelectNode(wheel); UpdateLayout();
            SelectNode(snapshot.Nodes.First(n => n.Id == "demo/root/5")); UpdateLayout();
            Check(Row("Slower devices") == "Share one link · single TT", "Properties must show a hub's single transaction translator.");
            SelectNode(wheel); UpdateLayout();
            SelectNode(snapshot.Controllers[0]); UpdateLayout();
            Check(Row("Power saving") == "On" && Row("Power plan") == "Selective suspend on", "Host properties must show the root hub's power-saving setting and the power plan.");
            Check(Row("PCIe link") == "PCIe 3.0 ×4 · about 31.5 Gb/s" && Row("Endpoints").EndsWith(" in use"), "Host properties must show the controller's PCIe link and endpoints in use.");
            snapshot.UsbSuspendPluggedIn = false; SelectNode(wheel); UpdateLayout();
            Check(Row("Power saving") == "On · plan disables it", "A device's power saving must say when the power plan disables it.");
            snapshot.UsbSuspendPluggedIn = true;
            foreach (var (id, item) in cards)
                Check(item.Card.Child.DesiredSize.Height <= item.Card.Height - item.Card.Padding.Top - item.Card.Padding.Bottom - 1, $"Card {id} content, including issue badges, exceeds its height.");
            VerifyStatusStyling();
            Search.Text = "Power at risk"; ApplySearch();
            Check(matches.Select(n => n.Id).SequenceEqual(["demo/root/5/1", "demo/root/5/2"]), "Power issues must be searchable.");
            Search.Clear(); ApplySearch();
            foreach (var id in new[] { "demo/root/5/2", "demo/root/5", "demo/root/5/3", "demo/root/5/4" })
            {
                SelectNode(snapshot.Nodes.First(n => n.Id == id)); UpdateLayout();
                var metrics = Details.Children.OfType<Grid>().First(g => g.ColumnDefinitions.Count == 3);
                Check(Glyphs(metrics).SequenceEqual([NodeVisuals.Metric.Link, NodeVisuals.Metric.Reserved, NodeVisuals.Metric.Power]), $"Inspector metrics for {id} must carry their glyphs.");
                // Values fit their column at the default inspector width, including the longest forms.
                foreach (var value in VisualDescendants(metrics).OfType<TextBlock>().Where(t => t.FontWeight == FontWeights.SemiBold))
                    foreach (var text in new[] { value.Text, "≥10 Gb/s", "<0.1 kb/s", "1500 mA" })
                    {
                        var probe = new TextBlock { Text = text, FontSize = value.FontSize, FontWeight = value.FontWeight, FontFamily = value.FontFamily };
                        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Check(probe.DesiredSize.Width <= value.ActualWidth + 0.5, $"Inspector metric \"{text}\" trims at the default inspector width.");
                    }
                if (id == "demo/root/5/2")
                {
                    var values = VisualDescendants(metrics).OfType<TextBlock>().Select(t => t.Text).ToList();
                    Check(values.Contains("12 Mb/s") && values.Contains("6.4 kb/s") && values.Contains("500 mA"), "Inspector metrics must show link, reserved bandwidth and requested power.");
                }
            }
            // A hub whose devices reserve most of its link warns beside its link rate; the devices themselves don't.
            var studio = snapshot.Nodes.First(n => n.Id == "demo/root/1");
            studio.Children[0].ReservedMbps = 1500; studio.Children[1].ReservedMbps = 1500;
            Draw(); UpdateLayout();
            Check(Beside("demo/root/1", NodeVisuals.Metric.Link).SequenceEqual(["Link nearly full"]) && !Issue(studio.Children[0]).Contains("Link nearly full"), "A nearly full hub link must warn beside the hub's link rate.");
            Check(cards["demo/root/1"].Card.Child.DesiredSize.Height <= cards["demo/root/1"].Card.Height - cards["demo/root/1"].Card.Padding.Top - cards["demo/root/1"].Card.Padding.Bottom - 1, "The nearly-full warning must fit on the hub card.");
            // Idle devices whose peaks overflow the link warn before they stream.
            studio.Children[0].ReservedMbps = 0; studio.Children[0].PeakReservedMbps = 2000;
            studio.Children[1].ReservedMbps = 100; studio.Children[1].PeakReservedMbps = 2000;
            Draw(); UpdateLayout();
            Check(Beside("demo/root/1", NodeVisuals.Metric.Link).SequenceEqual(["Could exceed when streaming"]), "Peaks that overflow a hub's link must warn beside its link rate while the devices are idle.");
            Check(cards["demo/root/1"].Card.Child.DesiredSize.Height <= cards["demo/root/1"].Card.Height - cards["demo/root/1"].Card.Padding.Top - cards["demo/root/1"].Card.Padding.Bottom - 1, "The streaming warning must fit on the hub card.");
            // Full-speed devices on two ports of a single-TT hub share one 12 Mb/s bus; the hub warns when they fill it.
            var travel = snapshot.Nodes.First(n => n.Id == "demo/root/5");
            travel.Children[0].LinkMbps = 12; travel.Children[0].ReservedMbps = 5; travel.Children[1].ReservedMbps = 5;
            Draw(); UpdateLayout();
            Check(Beside("demo/root/5", NodeVisuals.Metric.Link).SequenceEqual(["Shared TT nearly full", "Hub adapter not detected", "Over power budget"]), "A nearly full single TT must warn beside the hub's link rate, before its power warnings.");
            Check(cards["demo/root/5"].Card.Child.DesiredSize.Height <= cards["demo/root/5"].Card.Height - cards["demo/root/5"].Card.Padding.Top - cards["demo/root/5"].Card.Padding.Bottom - 1, "The shared TT warning must fit on the hub card.");
            travel.Children[0].ReservedMbps = 0; travel.Children[0].PeakReservedMbps = 6; travel.Children[1].PeakReservedMbps = 6;
            Draw(); UpdateLayout();
            Check(Beside("demo/root/5", NodeVisuals.Metric.Link).First() == "Shared TT could exceed", "Idle full-speed peaks that overflow a single TT must warn beside the hub's link rate.");
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); snapshot = savedSnapshot; selected = savedSelection;
            Draw(); ShowDetails(); UpdateIssues();
        }
    }

    // Flipping between ports compares like with like: a hub, a device, an empty port and an unreadable
    // port show the same rows at the same heights, and the two kinds of host share their own layout.
    private void VerifyInspectorConsistency()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        try
        {
            snapshot = DemoData.Create();
            snapshot.Nodes.First(n => n.Id == "demo/root/4").Kind = "Unavailable";
            Draw();
            // Explanations lead Properties and push the rows down by exactly their own height; nothing else may.
            double rowsStart = 0;
            List<string> Layout(string id)
            {
                SelectNode(snapshot.Nodes.First(n => n.Id == id)); UpdateLayout();
                var rows = Details.Children.OfType<FrameworkElement>().Where(e => e.Tag is "field" or "section").ToList();
                double origin = rows[0].TranslatePoint(new Point(), Details).Y;
                rowsStart = origin - Details.Children.OfType<FrameworkElement>().Where(e => e.Tag is string tag && tag.StartsWith("warning:", StringComparison.Ordinal)).Sum(e => e.ActualHeight + e.Margin.Top + e.Margin.Bottom);
                return rows.Select(e => $"{(e is Grid row ? ((TextBlock)row.Children[0]).Text : ((TextBlock)e).Text)}@{e.TranslatePoint(new Point(), Details).Y - origin:0}").ToList();
            }
            var expected = Layout("demo/root/1");
            double expectedStart = rowsStart;
            foreach (var id in new[] { "demo/root/1/1", "demo/root/1/3", "demo/root/4" })
            {
                var actual = Layout(id);
                Check(actual.SequenceEqual(expected), $"Inspector rows for {id} differ from a hub's: {string.Join(", ", actual.Except(expected).Concat(expected.Except(actual)).Take(4))}.");
                Check(Math.Abs(rowsStart - expectedStart) < 1, $"Inspector rows for {id} start {rowsStart - expectedStart:0.#} px away from a hub's, beyond what its explanations take.");
            }
            Check(Layout("demo").SequenceEqual(Layout("demo/root")), "Controller and root hub inspector rows differ.");
        }
        finally { snapshot = savedSnapshot; selected = savedSelection; Draw(); ShowDetails(); }
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
            Check(text.Contains("Power available") && text.Contains("Unknown · not measured") && !text.Contains("Link speed"), "Host inspector must distinguish unknown supply from peripheral metrics.");
            Check(text.Contains("PCI device") && text.Contains("Not reported"), "A host without a PCI identity must say so.");
            // Detection details stays folded until opened, so its lines are read from its content.
            List<string> Evidence() => Details.Children.OfType<Expander>().Single().Content is StackPanel lines ? lines.Children.OfType<TextBlock>().Select(t => t.Text).ToList() : [];
            snapshot.Controllers[0].PciId = "8086:A36D"; snapshot.Controllers[0].PciAddress = "00:14.0"; ShowDetails(); UpdateLayout();
            text = Descendants(Details).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(text.Contains("Intel · 8086:A36D · 00:14.0") && Evidence().Any(t => t.Contains("PCI identity: 8086:A36D")), "Host inspector must show the controller's PCI identity.");
            // A port map note explains itself like any other issue, and names the firmware as the cause.
            var noted = snapshot.Nodes.First(n => n.PortMapWarnings.Count > 0); SelectNode(noted); UpdateLayout();
            text = Descendants(Details).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(text.Any(t => t.Contains("without a USB 2 half")) && text.Any(t => t.Contains("firmware")) && Evidence().Any(t => t.StartsWith("Port map evidence: ")), "A port map note must be explained in Properties with its evidence.");
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub"); SelectNode(hub); UpdateLayout();
            Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, "edit-device-name")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = inlineLabelHost!; UpdateLayout();
            Check(editor.Visibility == Visibility.Visible && deviceHeading!.Visibility == Visibility.Collapsed, "Clicking the name must replace the heading with its inline editor.");
            CaptureUi("inline-label-preview.png");
            var input = Descendants(editor).OfType<TextBox>().Single(); input.Text = "Dell monitor KVM";
            Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Save label").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
            Check(hub.UserLabel == "Dell monitor KVM" && hub.Name == "Studio desktop hub", "Label editor overwrote reported identity or failed to save.");
            Check(Descendants(cards[hub.Id].Card).OfType<TextBlock>().Any(t => t.Text == "Dell monitor KVM"), "Saved label did not reach graph card.");
            Search.Text = "Dell monitor KVM"; ApplySearch(); Check(matches.Count == 1 && selected?.Id == hub.Id, "Saved labels must be searchable.");
            Search.Clear(); ApplySearch(); SelectNode(hub); UpdateLayout();
            deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json"));
            var refreshed = DemoData.Create(); deviceLabels.Apply(refreshed);
            Check(refreshed.Nodes.Single(n => n.Id == hub.Id).UserLabel == "Dell monitor KVM", "Labels did not survive a fresh snapshot and store reload.");
            editSelectedLabel!(); editor = inlineLabelHost!; UpdateLayout();
            Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(hub.UserLabel == "" && hub.DisplayName == "Studio desktop hub", "Label Reset failed to restore the detected name.");
            // A socket's speed set in Properties recolors its tongue on the graph, on both halves, and clears again.
            var socket = snapshot.Nodes.Single(n => n.Id == "demo/root/7"); var otherHalf = snapshot.Nodes.Single(n => n.Id == socket.CompanionId);
            Border Tongue(UsbNode port) { var slot = portSlots.GetValueOrDefault(port.Id) ?? connectedPorts[port.Id]; return (Border)slot.Template.FindName("Tongue", slot); }
            SelectNode(socket); UpdateLayout();
            var speedButton = Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, SocketSpeedTag));
            Check(Tongue(socket).Background == Brush("SocketSuperSpeed") && speedButton.Content as string == "As detected · Set…", "An unproven USB 3 socket starts blue, with its speed to be set.");
            Check(speedButton.ContextMenu?.Items.OfType<MenuItem>().Select(i => i.Header as string).SequenceEqual(["As detected", "5 Gb/s", "10 Gb/s or faster"]) == true && speedButton.ContextMenu.Items.OfType<MenuItem>().Count(i => i.IsChecked) == 1, "The speed menu must offer the three choices with the current one checked.");
            SetSocketSpeed(socket, 10000); UpdateLayout();
            Check(Tongue(socket).Background == Brush("SocketSuperSpeedPlus") && Tongue(otherHalf).Background == Brush("SocketSuperSpeedPlus"), "Setting a socket to 10 Gb/s must turn both halves' tongues red.");
            Check(Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, SocketSpeedTag)).Content as string == "10 Gb/s or faster · Edit" && Evidence().Any(t => t.Contains("You set this socket's speed")), "Properties must show the speed set and why the socket is drawn so.");
            SetSocketSpeed(socket, null); UpdateLayout();
            Check(Tongue(socket).Background == Brush("SocketSuperSpeed") && socket.SocketRatedMbps == null, "Removing the setting must turn the socket blue again.");
            SelectNode(snapshot.Nodes.Single(n => n.Id == "demo/root/8")); UpdateLayout();
            Check(Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, SocketSpeedTag)).IsEnabled, "A USB 2 half of a USB 3 socket must offer the socket's speed too.");
            SelectNode(snapshot.Nodes.First(n => n.Kind == "Empty port" && n.Protocols == "USB 2.0" && n.CompanionId.Length == 0)); UpdateLayout();
            Check(Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, SocketSpeedTag)) is { IsEnabled: false, Content: "Not applicable" }, "A USB 2 socket has no speed to set.");
            // A socket's type set in Properties redraws both halves on the graph, from a USB-C pill to USB-A to a plain
            // built-in slot, says so in the evidence, and clears again; the firmware's flag stays as reported.
            var typeC = snapshot.Nodes.Single(n => n.Id == "demo/root/8"); var typeCPartner = snapshot.Nodes.Single(n => n.Id == typeC.CompanionId);
            bool Pill(UsbNode port) { var slot = portSlots.GetValueOrDefault(port.Id) ?? connectedPorts[port.Id]; var c = ((Border)slot.Template.FindName("Chrome", slot)).CornerRadius; return Math.Max(Math.Max(c.TopLeft, c.TopRight), Math.Max(c.BottomLeft, c.BottomRight)) >= slot.Height / 2; }
            SelectNode(typeC); UpdateLayout();
            var typeButton = Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, SocketConnectorTag));
            Check(Pill(typeC) && Pill(typeCPartner) && typeButton.Content as string == "As detected · Set…"
                && typeButton.ContextMenu?.Items.OfType<MenuItem>().Select(i => i.Header as string).SequenceEqual(["As detected", "USB-A", "USB-C", "Internal: no socket"]) == true, "A USB-C socket starts as a pill, with its type to be set from four choices.");
            SetSocketConnector(typeC, "USB-A"); UpdateLayout();
            Check(!Pill(typeC) && !Pill(typeCPartner) && Tongue(typeC) != null && typeC.PortConnectorIsTypeC == true, "Setting a USB-C socket to USB-A must redraw both halves as USB-A and keep the firmware's flag.");
            SetSocketConnector(typeC, "Internal"); UpdateLayout();
            Check(Tongue(typeC) == null && Tongue(typeCPartner) == null && Descendants(Details).OfType<Button>().Single(b => Equals(b.Tag, SocketConnectorTag)).Content as string == "Internal: no socket · Edit"
                && Evidence().Any(t => t.Contains("You set this socket's connector")), "An internal socket is drawn without a tongue, and Properties says why.");
            SetSocketConnector(typeC, null); UpdateLayout();
            Check(Pill(typeC) && Pill(typeCPartner) && typeC.SocketConnectorSet == null, "Removing the setting must draw the USB-C socket again.");
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
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        // Auto-refresh sits with Refresh: the chevron's menu turns it on and off, and a dot on Refresh says it's on.
        bool wasOn = autoRefresh;
        try
        {
            SetAutoRefresh(false);
            var item = RefreshMenu().Items.OfType<MenuItem>().Single();
            Check(item.Header as string == AutoRefreshItem && item.IsCheckable && !item.IsChecked && AutoRefreshDot.Visibility == Visibility.Collapsed, "The Refresh menu must offer auto-refresh, off by default, with no dot.");
            item.IsChecked = true; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(autoRefresh && AutoRefreshDot.Visibility == Visibility.Visible && RefreshMenu().Items.OfType<MenuItem>().Single().IsChecked, "Turning auto-refresh on must mark Refresh with a dot and check the menu item.");
            Check(RefreshButton.ToolTip is StackPanel tip && tip.Children.OfType<TextBlock>().First().Text.Contains("auto"), "Refresh's tooltip must say auto-refresh is on.");
            item = RefreshMenu().Items.OfType<MenuItem>().Single(); item.IsChecked = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(!autoRefresh && AutoRefreshDot.Visibility == Visibility.Collapsed, "Turning auto-refresh off must clear the dot.");
            // The item is a themed toggle switch, not WPF's light check box: outlined and left when off, accent and right when on.
            foreach (bool on in new[] { false, true })
            {
                SetAutoRefresh(on);
                var menu = RefreshMenu(); menu.IsOpen = true; menu.UpdateLayout();
                var toggle = menu.Items.OfType<MenuItem>().Single(); toggle.ApplyTemplate();
                var track = (Border)toggle.Template.FindName("Track", toggle); var thumb = (System.Windows.Shapes.Ellipse)toggle.Template.FindName("Thumb", toggle);
                bool drawn = track.Visibility == Visibility.Visible && thumb.HorizontalAlignment == (on ? HorizontalAlignment.Right : HorizontalAlignment.Left)
                    && track.Background == (on ? Brush("Accent") : Brushes.Transparent) && thumb.Fill == Brush(on ? "OnAccent" : "TextMuted") && menu.Background == Brush("Surface");
                { var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var stream = System.IO.File.Create($"refresh-menu-{(on ? "on" : "off")}-preview.png"); png.Save(stream); }
                menu.IsOpen = false;
                Check(drawn, $"Auto-refresh must be a themed toggle switch, {(on ? "on: accent track, thumb right" : "off: outlined track, thumb left")}.");
            }
            SetAutoRefresh(false);
        }
        finally { SetAutoRefresh(wasOn); }
        // Demo refresh completes immediately, exercising the shortest possible scan.
        if (!demo) return;
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
        // Invoke the stale callback directly; elapsed-time assertions can fail when the UI thread is busy rendering.
        CompleteRefreshProgress(version);
        Check(RefreshProgress.Visibility == Visibility.Visible, "An earlier fade hid a newer refresh indicator.");
        await restarted;
        await Task.Delay(420);
        Check(RefreshProgress.Visibility == Visibility.Collapsed && !RefreshProgress.IsIndeterminate, "Progress animation must stop after fading out.");
    }
    private void VerifyCompactUi()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        bool savedHorizontal = horizontalTree;
        try
        {
            snapshot = DemoData.Create();
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub");
            hub.ScanIncomplete = true; hub.SpeedLimited = true;
            snapshot.Nodes.First(n => n.Id == "demo/root/4").Kind = "Unavailable";
            for (int i = 5; i <= 33; i++) hub.Children.Add(new UsbNode { Id = hub.Id + "/" + i, Kind = "Empty port", Name = "Available port " + i, Port = i, Status = "Empty" });
            hub.PortCount = 33;
            hub.Name = "Long hub identity with several words and USB generation information";
            hub.NameSource = "USB ID lookup";
            hub.UserLabel = "Dell monitor KVM with a longer personal label";
            // Port 4 on each sample hub is named as the other's half, as a USB 3 hub's USB 2 and USB 3 hubs name
            // theirs, so both cards must say they share sockets.
            var travelPort = snapshot.Nodes.First(n => n.Id == "demo/root/5/4"); var deskPort = snapshot.Nodes.First(n => n.Id == "demo/root/1/4");
            travelPort.CompanionId = deskPort.Id; deskPort.CompanionId = travelPort.Id;
            selected = hub;
            foreach (bool horizontal in new[] { false, true })
            {
                horizontalTree = horizontal; FitClick(this, new RoutedEventArgs()); UpdateLayout();
                VerifyWireRouting();
                string Shared(string id) => ((Panel)cards[id].Card.Child).Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, SharedSocketsTag))?.Text ?? "";
                Check(Shared("demo/root/5") == "Shares its sockets with H01/01" && Shared("demo/root/1") == "Shares its sockets with H01/05" && Shared("demo") == "", "Cards holding the two halves of the same sockets must name each other.");
                Check(socketParts.Values.Count(p => p == NodeVisuals.SocketPart.First) == 2, "The sample's paired root ports must each be drawn as one socket.");
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
                Check(portSlots.Count == snapshot.Nodes.Count(n => n.Kind == "Empty port") && cards.Values.All(c => ((UsbNode)c.Card.Tag).Kind != "Empty port"), "Empty ports must render as slots, not full cards.");
                int logicalPorts = snapshot.Nodes.Where(n => n.Kind is "Hub" or "Root hub" && cards.ContainsKey(DrawnAs(n).Id)).Sum(n => n.Children.Count);
                Check(portSlots.Count + connectedPorts.Count == logicalPorts, "Every logical port must be drawn on its hub.");
                Check(portSlots.Values.All(b => b.Background == Brush("Surface") || b.Background == Brush("Selection")) && connectedPorts.All(p => p.Value.Background == Brush(FindPath(selected?.Id ?? "").Any(n => n.Id == p.Key) ? "Accent" : NodeVisuals.WireInk((UsbNode)p.Value.Tag))), "Empty sockets must be hollow and occupied ones filled in their connection's ink.");
                ShowDetails(); UpdateIssues(); UpdateLayout();
                VerifyStatusStyling();
                VerifyRoleFills();
                foreach (var (id, slot) in portSlots.Concat(connectedPorts))
                {
                    // The drawn socket matches its connector and speed: a tongue in the speed's color for
                    // USB-A and USB-C, a pill only for USB-C, and a plain slot otherwise.
                    var port = (UsbNode)slot.Tag;
                    var tongue = slot.Template.FindName("Tongue", slot) as Border;
                    Check((tongue != null) == NodeVisuals.HasTongue(port) && (tongue == null || tongue.Background == Brush(NodeVisuals.SocketInk(port))), $"Socket {id} must show its speed on a tongue only when it has one.");
                    var corners = ((Border)slot.Template.FindName("Chrome", slot)).CornerRadius;
                    Check((Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight)) >= slot.Height / 2) == (port.Connector == "USB-C"), $"Socket {id} must be a pill exactly when it is USB-C.");
                    // A USB 3 socket's halves on one hub touch as one socket: side by side, or stacked when horizontal.
                    if (SocketPartner(port) is UsbNode partner && socketParts[id] == NodeVisuals.SocketPart.First)
                    {
                        var second = portSlots.GetValueOrDefault(partner.Id) ?? connectedPorts[partner.Id];
                        var gap = new Vector(Canvas.GetLeft(second) - Canvas.GetLeft(slot), Canvas.GetTop(second) - Canvas.GetTop(slot));
                        Check((gap - (horizontal ? new Vector(0, slot.Height) : new Vector(slot.Width, 0))).Length < 0.01 && socketParts[partner.Id] == NodeVisuals.SocketPart.Second, $"Socket {id} and its other half {partner.Id} must touch as one socket.");
                    }
                    var edge = horizontal ? new Point(Canvas.GetLeft(slot) + slot.Width, Canvas.GetTop(slot) + slot.Height / 2)
                        : new Point(Canvas.GetLeft(slot) + slot.Width / 2, Canvas.GetTop(slot) + slot.Height);
                    Check((edge - portAnchors[id]).Length < 0.01, "Port graphic must meet its connection anchor.");
                    if (wires.TryGetValue(id, out var wire))
                        Check((((PathGeometry)wire.Data).Figures[0].StartPoint - edge).Length < 0.01, "Connection must start at its own port graphic.");
                }
                // Simpler cards still hold their content, long labels and many sockets included.
                foreach (var level in new[] { CardDetail.Compact, CardDetail.Far })
                {
                    detail = level; Draw(); UpdateLayout();
                    foreach (var (id, item) in cards)
                        Check(item.Card.Child.DesiredSize.Height <= item.Card.Height - item.Card.Padding.Top - item.Card.Padding.Bottom - item.Card.BorderThickness.Top - item.Card.BorderThickness.Bottom + 1, $"{level} card {id}'s content exceeds its allocated height.");
                }
                detail = CardDetail.Full;
            }
            horizontalTree = false; FitClick(this, new RoutedEventArgs());
            var target = snapshot.Nodes.First(n => n.Kind == "Device");
            var originalCard = cards[target.Id].Card;
            SelectNode(target);
            Check(ReferenceEquals(originalCard, cards[target.Id].Card), "Selecting a node rebuilt the graph.");
            originalCard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(ReferenceEquals(originalCard, cards[target.Id].Card) && selected?.Id == target.Id, "Keyboard activation replaced the focused card.");
            folded.Add(snapshot.Controllers[0].Id);
            PanTransform.X = 250; PanTransform.Y = -800;
            Search.Text = "Available port"; ApplySearch();
            Check(matches.Count == snapshot.Nodes.Count(n => n.Kind == "Empty port") && portSlots.Count == matches.Count, "Searching hidden empty ports must reveal all matching slots.");
            Check(PanTransform.X == 0 && PanTransform.Y == 0 && selected?.Kind == "Empty port", "Search must reset panning and select a match.");
            var first = selected!.Id; NextMatch(1); Check(selected!.Id != first, "Next result failed."); NextMatch(-1); Check(selected!.Id == first, "Previous result failed.");
            Search.Text = "Running at 5 Gb/s"; ApplySearch();
            Check(matches.Count == 1 && selected?.Id == hub.Id, "Issue search did not select affected hardware.");
            UpdateIssues(); Check(IssuesButton.IsEnabled && Issue(hub).Contains("Scan incomplete"), "Incomplete scans must be visible as issues.");
            InspectorClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(InspectorPanel.Visibility == Visibility.Collapsed && InspectorColumn.ActualWidth == 0, "Inspector collapse failed.");
            InspectorClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(InspectorPanel.Visibility == Visibility.Visible && InspectorColumn.ActualWidth >= 260, "Inspector restore failed.");
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); folded.Clear();
            snapshot = savedSnapshot; selected = savedSelection; horizontalTree = savedHorizontal;
            ShowLayoutChoice();
            FitClick(this, new RoutedEventArgs()); ShowDetails(); UpdateIssues();
        }
    }
}

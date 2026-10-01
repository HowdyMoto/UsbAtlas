using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsbAtlas;

public partial class MainWindow
{
    private const double CardWidth = TopologyLayout.CardWidth, CardHeight = TopologyLayout.CardHeight;
    private bool readableView = true, arranging, horizontalTree;
    private double layoutWidth = 1100, inspectorWidth = 310;
    private double ReadingScale => 1;
    private readonly HashSet<string> expandedPorts = [];
    private readonly HashSet<string> visibleIds = [];
    private readonly Dictionary<string, string> pathLabels = [];
    private readonly Dictionary<string, System.Windows.Shapes.Path> wires = [];
    private readonly Dictionary<string, List<Point>> wireRoutes = [];
    private readonly Dictionary<string, Button> portSlots = [];
    private readonly Dictionary<string, Button> connectedPorts = [];
    private readonly Dictionary<string, Point> portAnchors = [];
    private readonly Dictionary<string, List<UsbNode>> edgePortCache = [];
    private readonly HashSet<string> stackedHubs = [], stackableHubs = [];
    private List<UsbNode> matches = [];
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string appliedQuery = "";
    private bool Compact => CompactDensity.IsChecked == true;
    private static string Issue(UsbNode n) => string.Join(" · ", new[] { n.ScanIncomplete ? "Scan incomplete" : null, n.Kind == "Unavailable" ? "Port error" : null, n.SpeedLimited ? "Reduced speed" : null }.Where(x => x != null));
    private bool Matches(UsbNode n, string q) => $"{n.DisplayName} {n.Name} {n.ReportedProduct} {n.WindowsName} {n.LookupVendor} {n.LookupProduct} {n.VendorId}:{n.ProductId} {n.Serial} {n.Manufacturer} {n.DeviceClass} {n.DeviceType} {n.Location} {n.Status} {Issue(n)} {pathLabels.GetValueOrDefault(n.Id)} {string.Join(" ", n.InterfaceFunctions)}".Contains(q, StringComparison.OrdinalIgnoreCase);
    private bool Visible(UsbNode n) => visibleIds.Contains(n.Id);
    private List<UsbNode> Children(UsbNode n) => folded.Contains(n.Id) && appliedQuery.Length == 0 ? [] : n.Children.Where(c => c.Kind != "Empty port" && Visible(c)).OrderBy(c => c.Port).ToList();
    private bool ShowPorts(UsbNode n) => EmptyPorts.IsChecked == true || expandedPorts.Contains(n.Id) || appliedQuery.Length > 0 && n.Children.Any(c => c.Kind == "Empty port" && Matches(c, appliedQuery));
    // Cached per drawing pass; layout queries each hub's ports many times while choosing staircases.
    private List<UsbNode> EdgePorts(UsbNode n)
    {
        if (edgePortCache.TryGetValue(n.Id, out var ports)) return ports;
        return edgePortCache[n.Id] = n.Kind is "Hub" or "Root hub"
            ? n.Children.Where(c => c.Kind != "Empty port" || ShowPorts(n)).OrderBy(c => c.Port).ToList() : [];
    }
    // Ports spread evenly along the edge; a staircase gathers them at the card's right end
    // so its column of devices can tuck under the card.
    private double? PortOffset(UsbNode parent, UsbNode child)
    {
        var ports = EdgePorts(parent);
        int i = ports.FindIndex(p => p.Id == child.Id);
        if (i < 0) return null;
        double edge = horizontalTree ? HeightFor(parent) : WidthFor(parent);
        return stackedHubs.Contains(parent.Id) ? edge - 30 - (ports.Count - 1 - i) * 38 : edge * (i + 0.5) / ports.Count;
    }
    private double WidthFor(UsbNode n) => horizontalTree ? CardWidth + (EdgePorts(n).Count > 0 ? 44 : 0) : Math.Max(CardWidth, EdgePorts(n).Count * 38 + 24);
    private double HeightFor(UsbNode n)
    {
        double height = n.Kind is "Controller" or "Root hub" ? (Compact ? 84 : 100) : Compact ? CardHeight : 138;
        if (n.UserLabel.Length > 0 || n.NameSource.Contains("lookup", StringComparison.OrdinalIgnoreCase)) height += 18;
        if (Issue(n).Length > 0) height += 22;
        int empty = n.Children.Count(c => c.Kind == "Empty port");
        if (empty > 0) height += 24;
        int ports = EdgePorts(n).Count;
        if (ports > 0) height = horizontalTree ? Math.Max(height, ports * 30 + 16) : height + 44;
        return height;
    }
    private void PrepareGraph()
    {
        appliedQuery = Search.Text.Trim();
        pathLabels.Clear(); visibleIds.Clear(); matches.Clear(); edgePortCache.Clear();
        void Visit(UsbNode n, string path)
        {
            pathLabels[n.Id] = path;
            foreach (var child in n.Children) Visit(child, path + "/" + (child.Port > 0 ? child.Port.ToString("00") : "root"));
            bool match = appliedQuery.Length > 0 && Matches(n, appliedQuery);
            if (match) matches.Add(n);
            if (appliedQuery.Length == 0 || match || n.Children.Any(c => visibleIds.Contains(c.Id))) visibleIds.Add(n.Id);
        }
        for (int i = 0; i < snapshot.Controllers.Count; i++) Visit(snapshot.Controllers[i], $"H{i + 1:00}");
        var matchIds = matches.Select(n => n.Id).ToHashSet();
        matches = snapshot.Nodes.Where(n => matchIds.Contains(n.Id)).ToList();
    }
    private void Draw()
    {
        if (Graph == null) return;
        string? focusId = null;
        for (var hit = Keyboard.FocusedElement as DependencyObject; hit != null; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
            if (hit is FrameworkElement { Tag: UsbNode n }) { focusId = n.Id; break; }
        PrepareGraph();
        UpdateDeviceTree();
        Graph.Children.Clear(); cards.Clear(); wires.Clear(); wireRoutes.Clear(); portSlots.Clear(); connectedPorts.Clear(); portAnchors.Clear();
        var roots = snapshot.Controllers.Where(Visible).ToList();
        const double margin = 16, controllerGap = 24;
        var layouts = ArrangeLayouts(roots, layoutWidth - margin * 2, controllerGap);
        // Controllers flow left to right and wrap only as whole trees; horizontal trees stack.
        double top = 12, left = margin, rowHeight = 0, maxRight = 0;
        foreach (var tree in layouts)
        {
            if (left > margin && (horizontalTree || left + tree.Width > layoutWidth - margin)) { top += rowHeight + 36; left = margin; rowHeight = 0; }
            Place(tree, left, top);
            left += tree.Width + controllerGap;
            rowHeight = Math.Max(rowHeight, tree.Height);
            maxRight = Math.Max(maxRight, left - controllerGap + margin);
        }
        top += rowHeight + 36;
        Graph.Width = Math.Max(CardWidth + margin * 2, maxRight);
        Graph.Height = Math.Max(200, top);
        EmptyMessage.Text = snapshot.Controllers.Count == 0 ? "No USB controllers found. Try Refresh or Sample." : "No matching devices. Press Escape to clear search.";
        EmptyMessage.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
        if (focusId != null)
        {
            if (portSlots.TryGetValue(focusId, out var slot)) slot.Focus();
            else if (cards.TryGetValue(focusId, out var item)) item.Card.Focus();
            else if (selected != null && cards.TryGetValue(selected.Id, out item)) item.Card.Focus();
        }
    }
    // Readable layouts never wrap a hub's children. While the graph is wider than the view,
    // groups of end devices become staircases, each time picking the one that saves the most.
    private List<TopologyLayout.Item> ArrangeLayouts(List<UsbNode> roots, double available, double gap)
    {
        stackedHubs.Clear(); stackableHubs.Clear();
        List<TopologyLayout.Item> Measure() => roots.Select(r => TopologyLayout.Measure(r, Children, horizontalTree, WidthFor, HeightFor, PortOffset, stackedHubs)).ToList();
        var layouts = Measure();
        if (horizontalTree) return layouts;
        static IEnumerable<TopologyLayout.Item> Flatten(TopologyLayout.Item item) => item.Children.SelectMany(Flatten).Prepend(item);
        stackableHubs.UnionWith(layouts.SelectMany(Flatten).Where(i => TopologyLayout.CanStack(i.Node, Children, PortOffset)).Select(i => i.Node.Id));
        void Shrink(Func<List<TopologyLayout.Item>, double> extent)
        {
            while (extent(layouts) > available)
            {
                var options = stackableHubs.Where(id => !stackedHubs.Contains(id)).ToList();
                if (options.Count == 0) return;
                var best = options.Select(id =>
                {
                    stackedHubs.Add(id); var trial = Measure(); stackedHubs.Remove(id);
                    return (Id: id, Layouts: trial, Extent: extent(trial));
                }).ToList().MinBy(t => t.Extent);
                if (best.Extent >= extent(layouts) - 0.5) return;
                stackedHubs.Add(best.Id); layouts = best.Layouts;
            }
        }
        Shrink(all => all.Sum(t => t.Width) + gap * (all.Count - 1));
        if (layouts.Count > 1 && layouts.Sum(t => t.Width) + gap * (layouts.Count - 1) > available)
        {
            // The controllers will wrap anyway, so stack only inside trees too wide on their own.
            stackedHubs.Clear(); layouts = Measure();
            Shrink(all => all.Max(t => t.Width));
        }
        return layouts;
    }
    private void Place(TopologyLayout.Item layout, double left, double top)
    {
        var node = layout.Node;
        double height = HeightFor(node), width = WidthFor(node);
        double x = left + layout.CardX, y = top + layout.CardY;
        var bounds = new Rect(x, y, width, height);
        bool host = node.Kind is "Controller" or "Root hub";
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        if (node.Children.Any(c => c.Kind != "Empty port"))
        {
            var fold = new Button { Content = folded.Contains(node.Id) && appliedQuery.Length == 0 ? "+" : "−", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(5, 0, 0, 0), ToolTip = "Expand / collapse branch", IsEnabled = appliedQuery.Length == 0 };
            fold.Click += (_, e) => { if (!folded.Add(node.Id)) folded.Remove(node.Id); Draw(); ShowDetails(); e.Handled = true; };
            DockPanel.SetDock(fold, Dock.Right); header.Children.Add(fold);
        }
        var path = new TextBlock { Text = pathLabels[node.Id], MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 10, Foreground = Brush("TextMuted"), ToolTip = "Logical path: host / root / ports", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(path, Dock.Right); header.Children.Add(path);
        header.Children.Add(new TextBlock { Text = NodeVisuals.Label(node) + (node.Kind == "Root hub" ? $" · {node.Children.Count(c => c.Kind != "Empty port")}/{node.PortCount} occupied" : ""), FontSize = 11, Foreground = Brush(NodeVisuals.Color(node)), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(header);
        var identity = new DockPanel { Height = host ? 24 : Compact ? 46 : 54 };
        var icon = NodeVisuals.Icon(node, host ? 18 : 23); icon.Margin = new Thickness(0, 0, 7, 0); DockPanel.SetDock(icon, Dock.Left); identity.Children.Add(icon);
        identity.Children.Add(new TextBlock { Text = node.DisplayName, FontSize = host ? 13 : 16, FontWeight = FontWeights.SemiBold, TextWrapping = host ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = host ? 24 : Compact ? 46 : 52, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(identity);
        if (node.UserLabel.Length > 0 || node.NameSource.Contains("lookup", StringComparison.OrdinalIgnoreCase))
            panel.Children.Add(new TextBlock { Text = node.UserLabel.Length > 0 ? "Detected: " + node.Name : "USB ID lookup · component identity", FontSize = 10, Foreground = Brush("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = node.Name + " · " + node.NameSource });
        int occupied = node.Children.Count(c => c.Kind != "Empty port");
        string occupancy = $"{occupied}/{node.PortCount} occupied";
        string metric = node.Kind switch { "Controller" => $"{node.Children.Count} root buses", "Root hub" => occupancy, "Hub" => $"{occupancy} · {ShortSpeed(node)}", "Unavailable" => node.Status, _ => ShortSpeed(node) + (node.MaxPowerMa is int ma ? $" · {ma} mA declared" : "") };
        if (!host) panel.Children.Add(new TextBlock { Text = metric + (node.PortConnectorIsTypeC == true ? " · USB-C" : ""), FontSize = 11, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = metric });
        else panel.Children.Add(new TextBlock { Text = "Ports: " + ProtocolSummary(node), FontSize = 11, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = ProtocolSummary(node) + "\nSupply capacity: unknown; charging limits are not queried.\n" + metric });
        if (Issue(node).Length > 0) panel.Children.Add(new TextBlock { Text = "⚠ " + Issue(node), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("Warning"), Margin = new Thickness(0, 4, 0, 0) });
        var empties = node.Children.Where(c => c.Kind == "Empty port").ToList();
        if (empties.Count > 0)
        {
            var toggle = new Button { Content = $"{(ShowPorts(node) ? "−" : "+")} {empties.Count} empty logical ports · {occupancy}", Padding = new Thickness(3, 1, 3, 1), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Expand numbered empty-port slots" };
            toggle.Click += (_, e) => { if (EmptyPorts.IsChecked == true) { EmptyPorts.IsChecked = false; foreach (var hub in snapshot.Nodes.Where(n => n.Children.Any(c => c.Kind == "Empty port"))) expandedPorts.Add(hub.Id); } if (!expandedPorts.Add(node.Id)) expandedPorts.Remove(node.Id); Draw(); ShowDetails(); e.Handled = true; };
            panel.Children.Add(toggle);
        }
        var edgePorts = EdgePorts(node);
        var padding = edgePorts.Count == 0 ? new Thickness(11, 7, 11, 7) : horizontalTree ? new Thickness(11, 7, 55, 7) : new Thickness(11, 7, 11, 51);
        var card = new Border { Width = width, Height = height, Padding = padding, CornerRadius = new CornerRadius(host ? 3 : 6), Background = Brush("Surface"), BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), Child = panel, Cursor = Cursors.Hand, Focusable = true, Tag = node, ToolTip = node.Name + "\n" + metric + "\n" + pathLabels[node.Id] + "\n" + node.LocationEvidence };
        System.Windows.Automation.AutomationProperties.SetName(card, node.DisplayName + ", " + NodeVisuals.Label(node) + ", " + metric + ", " + Issue(node));
        card.MouseLeftButtonDown += (_, e) => { card.Focus(); SelectNode(node); if (e.ClickCount == 2 && node.Children.Count > 0 && appliedQuery.Length == 0) { if (!folded.Add(node.Id)) folded.Remove(node.Id); Draw(); ShowDetails(); } e.Handled = true; };
        card.KeyDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, card)) return;
            if (e.Key is Key.Enter or Key.Space) { SelectNode(node); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            {
                var items = cards.Keys.ToList(); int i = items.IndexOf(node.Id);
                var next = items[Math.Clamp(i + (e.Key is Key.Up or Key.Left ? -1 : 1), 0, items.Count - 1)];
                SelectNode((UsbNode)cards[next].Card.Tag); cards[next].Card.Focus(); LocateClick(this, new RoutedEventArgs()); e.Handled = true;
            }
        };
        card.GotKeyboardFocus += (_, _) => card.BorderBrush = Brush("Accent");
        card.LostKeyboardFocus += (_, _) => UpdateSelection();
        Canvas.SetLeft(card, x); Canvas.SetTop(card, y); Panel.SetZIndex(card, 1); Graph.Children.Add(card); cards[node.Id] = (card, bounds.TopLeft);
        for (int i = 0; i < edgePorts.Count; i++)
        {
            var port = edgePorts[i];
            double cross = (horizontalTree ? y : x) + PortOffset(node, port)!.Value;
            var graphic = NodeVisuals.PortGraphic(port);
            var content = new StackPanel { Orientation = horizontalTree ? Orientation.Horizontal : Orientation.Vertical };
            content.Children.Add(new TextBlock { Text = port.Port.ToString("00"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (horizontalTree) graphic.LayoutTransform = new RotateTransform(90);
            content.Children.Add(graphic);
            var button = new Button { Content = content, Tag = port, Width = horizontalTree ? 40 : 34, Height = horizontalTree ? 26 : 38, Padding = new Thickness(0), ToolTip = $"Logical port {port.Port} · {(port.Kind == "Empty port" ? "Empty" : port.DisplayName)}\n{(port.PortConnectorIsTypeC == true ? "USB-C receptacle reported by Windows" : "Connector shape unknown")}" };
            System.Windows.Automation.AutomationProperties.SetName(button, $"Port {port.Port}, {port.DisplayName}");
            button.Click += (_, e) => { SelectNode(port); e.Handled = true; };
            Canvas.SetLeft(button, horizontalTree ? bounds.Right - button.Width : cross - button.Width / 2);
            Canvas.SetTop(button, horizontalTree ? cross - button.Height / 2 : bounds.Bottom - button.Height);
            Panel.SetZIndex(button, 2); Graph.Children.Add(button);
            (port.Kind == "Empty port" ? portSlots : connectedPorts)[port.Id] = button;
            portAnchors[port.Id] = horizontalTree ? new Point(bounds.Right, cross) : new Point(cross, bounds.Bottom);
        }
        var children = layout.Children;
        var childCards = children.Select(c => new Rect(left + c.X + c.CardX, top + c.Y + c.CardY, WidthFor(c.Node), HeightFor(c.Node))).ToList();
        var anchors = children.Select(c => portAnchors.TryGetValue(c.Node.Id, out var anchor) ? anchor : (Point?)null).ToList();
        var routes = TopologyLayout.Route(bounds, anchors, childCards, layout.Stacked, horizontalTree);
        for (int i = 0; i < children.Count; i++)
        {
            AddWire(children[i].Node.Id, routes[i]);
            Place(children[i], left + children[i].X, top + children[i].Y);
        }
    }
    private void AddWire(string id, List<Point> route)
    {
        var wire = new System.Windows.Shapes.Path { Data = RoundedRoute(route, 6), Stroke = Brush("Wire"), StrokeThickness = 1.5, IsHitTestVisible = false };
        Graph.Children.Add(wire); wires[id] = wire; wireRoutes[id] = route;
    }
    // Softened corners make orthogonal routes read as cables.
    private static PathGeometry RoundedRoute(List<Point> route, double radius)
    {
        var figure = new PathFigure { StartPoint = route[0] };
        for (int i = 1; i < route.Count - 1; i++)
        {
            Vector incoming = route[i] - route[i - 1], outgoing = route[i + 1] - route[i];
            double r = Math.Min(radius, Math.Min(incoming.Length, outgoing.Length) / 2);
            if (r < 0.5) { figure.Segments.Add(new LineSegment(route[i], true)); continue; }
            incoming.Normalize(); outgoing.Normalize();
            figure.Segments.Add(new LineSegment(route[i] - incoming * r, true));
            figure.Segments.Add(new QuadraticBezierSegment(route[i], route[i] + outgoing * r, true));
        }
        figure.Segments.Add(new LineSegment(route[^1], true));
        var geometry = new PathGeometry(); geometry.Figures.Add(figure);
        return geometry;
    }
    private void SelectNode(UsbNode node)
    {
        bool changed = selected?.Id != node.Id;
        selected = snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id) ?? node;
        UpdateSelection(revealInTree: true); ShowDetails();
        if (changed) DetailsScroll.ScrollToTop();
    }
    private Rect? GraphBounds(UsbNode node)
    {
        if (portSlots.TryGetValue(node.Id, out var slot)) return new Rect(Canvas.GetLeft(slot), Canvas.GetTop(slot), slot.Width, slot.Height);
        return cards.TryGetValue(node.Id, out var item) ? new Rect(item.Point, new Size(item.Card.Width, item.Card.Height)) : null;
    }
    // Brings the selection into view at the current zoom, moving only when it is not fully visible.
    private void RevealSelection()
    {
        if (selected == null || GraphBounds(selected) is not Rect area) return;
        GraphScroll.UpdateLayout();
        var shown = new Rect(Graph.TranslatePoint(area.TopLeft, GraphScroll), Graph.TranslatePoint(area.BottomRight, GraphScroll));
        var view = new Rect(0, 0, GraphScroll.ViewportWidth, GraphScroll.ViewportHeight);
        if (view.Contains(shown)) return;
        var delta = new Vector(shown.X + shown.Width / 2 - view.Width / 2, shown.Y + shown.Height / 2 - view.Height / 2);
        double h = Math.Clamp(GraphScroll.HorizontalOffset + delta.X, 0, GraphScroll.ScrollableWidth);
        double v = Math.Clamp(GraphScroll.VerticalOffset + delta.Y, 0, GraphScroll.ScrollableHeight);
        // Scroll as far as the content allows, then pan for the remainder.
        delta -= new Vector(h - GraphScroll.HorizontalOffset, v - GraphScroll.VerticalOffset);
        GraphScroll.ScrollToHorizontalOffset(h); GraphScroll.ScrollToVerticalOffset(v);
        PanTransform.X -= delta.X; PanTransform.Y -= delta.Y;
        GraphScroll.UpdateLayout();
    }
    // A fading ring draws the eye to a card revealed from the tree or a newly connected device.
    private void Pulse(UsbNode node, double seconds = 0.9)
    {
        if (GraphBounds(node) is not Rect area) return;
        area.Inflate(6, 6);
        var ring = new Border { Width = area.Width, Height = area.Height, CornerRadius = new CornerRadius(10), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(3), IsHitTestVisible = false };
        Canvas.SetLeft(ring, area.X); Canvas.SetTop(ring, area.Y); Panel.SetZIndex(ring, 3);
        Graph.Children.Add(ring);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds)) { BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
        fade.Completed += (_, _) => Graph.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade);
    }
    private void UpdateSelection(bool revealInTree = false)
    {
        SyncTreeSelection(revealInTree);
        var chain = FindPath(selected?.Id ?? "").Select(n => n.Id).ToHashSet();
        foreach (var (id, item) in cards)
        {
            bool match = appliedQuery.Length > 0 && Matches((UsbNode)item.Card.Tag, appliedQuery);
            item.Card.Background = Brush(id == selected?.Id ? "Selection" : "Surface");
            item.Card.BorderBrush = Brush(id == selected?.Id || item.Card.IsKeyboardFocusWithin || match ? "Accent" : "Border");
            item.Card.BorderThickness = new Thickness(id == selected?.Id || match ? 2 : 1);
        }
        foreach (var (id, wire) in wires)
        {
            bool upstream = chain.Contains(id);
            wire.Stroke = Brush(upstream ? "Accent" : "Wire"); wire.StrokeThickness = upstream ? 2.25 : 1.5;
        }
        foreach (var (id, slot) in portSlots.Concat(connectedPorts))
        {
            slot.Background = Brush(id == selected?.Id ? "Selection" : "Surface");
            slot.BorderBrush = Brush(id == selected?.Id || appliedQuery.Length > 0 && Matches((UsbNode)slot.Tag, appliedQuery) ? "Accent" : "Border");
        }
        LocateButton.IsEnabled = selected != null && (cards.ContainsKey(selected.Id) || portSlots.ContainsKey(selected.Id));
        int index = matches.FindIndex(n => n.Id == selected?.Id);
        MatchCount.Text = appliedQuery.Length == 0 ? "" : index >= 0 ? $"{index + 1} / {matches.Count} matches" : $"{matches.Count} matches";
        NextMatchButton.Visibility = appliedQuery.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextMatchButton.IsEnabled = matches.Count > 0;
    }
    private void ApplySearch()
    {
        searchTimer.Stop(); ResetPan(); Draw();
        if (matches.Count > 0) { SelectNode(matches[0]); LocateClick(this, new RoutedEventArgs()); }
        else { GraphScroll.ScrollToTop(); GraphScroll.ScrollToLeftEnd(); ShowDetails(); }
    }
    private void NextMatchClick(object sender, RoutedEventArgs e) => NextMatch(1);
    private void NextMatch(int direction)
    {
        if (searchTimer.IsEnabled) { ApplySearch(); return; }
        if (matches.Count == 0) return;
        int i = matches.FindIndex(n => n.Id == selected?.Id);
        SelectNode(matches[(i + direction + matches.Count) % matches.Count]); LocateClick(this, new RoutedEventArgs());
    }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NextMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        if (e.Key == Key.Escape) { Search.Clear(); ApplySearch(); e.Handled = true; }
    }
    private void DensityClick(object sender, RoutedEventArgs e) => FitClick(sender, e);
    private void InspectorClick(object sender, RoutedEventArgs e)
    {
        bool hide = InspectorPanel.Visibility == Visibility.Visible;
        if (hide) inspectorWidth = InspectorColumn.ActualWidth;
        InspectorColumn.MinWidth = hide ? 0 : 260;
        InspectorColumn.Width = new GridLength(hide ? 0 : inspectorWidth);
        SplitterColumn.Width = new GridLength(hide ? 0 : 5);
        InspectorPanel.Visibility = InspectorSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        InspectorButton.Content = hide ? "Show inspector" : "Hide inspector";
    }
    private void UpdateIssues()
    {
        int count = snapshot.Nodes.Count(n => Issue(n).Length > 0) + snapshot.Diagnostics.Count;
        IssuesButton.Content = count == 0 ? "No issues" : $"⚠ {count} issues";
        IssuesButton.Foreground = Brush(count == 0 ? "TextMuted" : "Warning");
        IssuesButton.IsEnabled = count > 0;
        StatusText.Text = (snapshot.IsDemo ? "Sample topology" : "Local snapshot") + $" · Updated {snapshot.CapturedAt:T} · {snapshot.Nodes.Count(n => n.Kind == "Unavailable")} port errors · {snapshot.Nodes.Count(n => n.ScanIncomplete)} incomplete · {snapshot.Nodes.Count(n => n.SpeedLimited)} reduced speed";
        if (snapshot.Diagnostics.Count > 0) StatusText.Text += " · " + string.Join(" · ", snapshot.Diagnostics);
        StatusText.ToolTip = StatusText.Text;
    }
    private void IssuesClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = Brush("Surface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("Border") };
        foreach (var node in snapshot.Nodes.Where(n => Issue(n).Length > 0))
        {
            var item = new MenuItem { Header = $"{Issue(node)} — {node.DisplayName} ({pathLabels.GetValueOrDefault(node.Id)})" };
            item.Click += (_, _) => { Search.Clear(); searchTimer.Stop(); foreach (var ancestor in FindPath(node.Id)) folded.Remove(ancestor.Id); Draw(); SelectNode(node); LocateClick(this, new RoutedEventArgs()); };
            menu.Items.Add(item);
        }
        foreach (var diagnostic in snapshot.Diagnostics) menu.Items.Add(new MenuItem { Header = diagnostic, IsEnabled = false });
        menu.PlacementTarget = IssuesButton; menu.IsOpen = true;
    }
}

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
    private readonly Dictionary<string, Button> portSlots = [];
    private List<UsbNode> matches = [];
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string appliedQuery = "";
    private bool Compact => CompactDensity.IsChecked == true;
    private static string Issue(UsbNode n) => string.Join(" · ", new[] { n.ScanIncomplete ? "Scan incomplete" : null, n.Kind == "Unavailable" ? "Port error" : null, n.SpeedLimited ? "Reduced speed" : null }.Where(x => x != null));
    private bool Matches(UsbNode n, string q) => $"{n.DisplayName} {n.Name} {n.ReportedProduct} {n.WindowsName} {n.LookupVendor} {n.LookupProduct} {n.VendorId}:{n.ProductId} {n.Serial} {n.Manufacturer} {n.DeviceClass} {n.DeviceType} {n.Location} {n.Status} {Issue(n)} {pathLabels.GetValueOrDefault(n.Id)} {string.Join(" ", n.InterfaceFunctions)}".Contains(q, StringComparison.OrdinalIgnoreCase);
    private bool Visible(UsbNode n) => visibleIds.Contains(n.Id);
    private List<UsbNode> Children(UsbNode n) => folded.Contains(n.Id) && appliedQuery.Length == 0 ? [] : n.Children.Where(c => c.Kind != "Empty port" && Visible(c)).ToList();
    private bool ShowPorts(UsbNode n) => EmptyPorts.IsChecked == true || expandedPorts.Contains(n.Id) || appliedQuery.Length > 0 && n.Children.Any(c => c.Kind == "Empty port" && Matches(c, appliedQuery));
    private double HeightFor(UsbNode n)
    {
        double height = n.Kind is "Controller" or "Root hub" ? (Compact ? 84 : 100) : Compact ? CardHeight : 138;
        if (n.UserLabel.Length > 0 || n.NameSource.Contains("lookup", StringComparison.OrdinalIgnoreCase)) height += 18;
        if (Issue(n).Length > 0) height += 22;
        int empty = n.Children.Count(c => c.Kind == "Empty port");
        if (empty > 0) height += 24 + (ShowPorts(n) ? Math.Ceiling(empty / 8.0) * 27 : 0);
        return height;
    }
    private void PrepareGraph()
    {
        appliedQuery = Search.Text.Trim();
        pathLabels.Clear(); visibleIds.Clear(); matches.Clear();
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
        Graph.Children.Clear(); cards.Clear(); wires.Clear(); portSlots.Clear();
        var roots = snapshot.Controllers.Where(Visible).ToList();
        const double margin = 16, controllerGap = 24;
        int columns = horizontalTree ? 1 : Math.Min(Math.Max(1, roots.Count), Math.Max(1, (int)(layoutWidth / 650)));
        double budget = Math.Max(CardWidth, (layoutWidth - margin * 2 - controllerGap * (columns - 1)) / columns);
        if (horizontalTree) budget = Math.Max(CardHeight + 40, (GraphScroll.ActualHeight - 48 * Math.Max(1, roots.Count)) / Math.Max(1, roots.Count));
        var layouts = roots.Select(r => TopologyLayout.Measure(r, budget, Children, horizontalTree, HeightFor)).ToList();
        double top = 12, maxRight = 0;
        for (int start = 0; start < layouts.Count; start += columns)
        {
            var row = layouts.Skip(start).Take(columns).ToList();
            double left = margin;
            foreach (var tree in row)
            {
                Place(tree, left, top, null, horizontalTree ? top + 5 : left + 5);
                left += tree.Width + controllerGap;
            }
            maxRight = Math.Max(maxRight, left - controllerGap + margin);
            top += row.Max(n => n.Height) + 36;
        }
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
    private void Place(TopologyLayout.Item layout, double left, double y, Rect? parent, double trunk)
    {
        var node = layout.Node;
        var baseY = y;
        double height = HeightFor(node);
        double x = horizontalTree ? left : left + (layout.Width - CardWidth) / 2;
        if (horizontalTree) y += (layout.Height - height) / 2;
        var bounds = new Rect(x, y, CardWidth, height);
        if (parent is Rect p)
        {
            Point Orient(double cross, double along) => horizontalTree ? new Point(along, cross) : new Point(cross, along);
            double pc = horizontalTree ? p.Y + p.Height / 2 : p.X + p.Width / 2;
            double pe = horizontalTree ? p.Right : p.Bottom;
            double cc = horizontalTree ? y + height / 2 : x + CardWidth / 2;
            double cs = horizontalTree ? x : y;
            double bus = cs - 14;
            var fig = new PathFigure { StartPoint = Orient(pc, pe) };
            fig.Segments.Add(new LineSegment(Orient(pc, pe + 10), true));
            if (cs > pe + TopologyLayout.LevelGap + 1)
            {
                fig.Segments.Add(new LineSegment(Orient(trunk, pe + 10), true));
                fig.Segments.Add(new LineSegment(Orient(trunk, bus), true));
            }
            else fig.Segments.Add(new LineSegment(Orient(pc, bus), true));
            fig.Segments.Add(new LineSegment(Orient(cc, bus), true));
            fig.Segments.Add(new LineSegment(Orient(cc, cs), true));
            var geometry = new PathGeometry(); geometry.Figures.Add(fig);
            var wire = new System.Windows.Shapes.Path { Data = geometry, Stroke = Brush("Wire"), StrokeThickness = 1.5, IsHitTestVisible = false };
            Graph.Children.Add(wire); wires[node.Id] = wire;
        }
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
            if (ShowPorts(node))
            {
                var slots = new WrapPanel { Width = 264, Margin = new Thickness(0, 3, 0, 0) };
                foreach (var empty in empties)
                {
                    var button = new Button { Content = empty.Port.ToString("00"), Tag = empty, Width = 30, Height = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3), FontSize = 11, ToolTip = $"Empty logical port {empty.Port} · select for protocols and connector" };
                    button.Click += (_, e) => { SelectNode(empty); e.Handled = true; };
                    slots.Children.Add(button); portSlots[empty.Id] = button;
                }
                panel.Children.Add(slots);
            }
        }
        var card = new Border { Width = CardWidth, Height = height, Padding = new Thickness(11, 7, 11, 7), CornerRadius = new CornerRadius(host ? 3 : 6), Background = Brush("Surface"), BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), Child = panel, Cursor = Cursors.Hand, Focusable = true, Tag = node, ToolTip = node.Name + "\n" + metric + "\n" + pathLabels[node.Id] + "\n" + node.LocationEvidence };
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
        foreach (var child in layout.Children) Place(child, left + child.X, baseY + child.Y, bounds, horizontalTree ? baseY + 5 : left + 5);
    }
    private void SelectNode(UsbNode node)
    {
        bool changed = selected?.Id != node.Id;
        selected = snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id) ?? node;
        UpdateSelection(); ShowDetails();
        if (changed) DetailsScroll.ScrollToTop();
    }
    private void UpdateSelection()
    {
        var chain = FindPath(selected?.Id ?? "").Select(n => n.Id).ToHashSet();
        foreach (var (id, item) in cards)
        {
            bool match = appliedQuery.Length > 0 && Matches((UsbNode)item.Card.Tag, appliedQuery);
            item.Card.Background = Brush(id == selected?.Id ? "Selection" : "Surface");
            item.Card.BorderBrush = Brush(id == selected?.Id || item.Card.IsKeyboardFocusWithin || match ? "Accent" : "Border");
            item.Card.BorderThickness = new Thickness(id == selected?.Id || match ? 2 : 1);
        }
        foreach (var (id, wire) in wires) wire.Stroke = Brush(chain.Contains(id) ? "Accent" : "Wire");
        foreach (var (id, slot) in portSlots)
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

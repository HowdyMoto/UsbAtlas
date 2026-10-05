using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsbAtlas;

public partial class MainWindow
{
    private const double CardWidth = TopologyLayout.CardWidth, CardHeight = TopologyLayout.CardHeight;
    private bool readableView = true, overviewView, arranging, horizontalTree;
    // Semantic zoom: how much a card shows depends on how far out the view is, and each level is laid out
    // on its own rather than scaled. Full is the whole card. Compact keeps the name, the figures and the
    // sockets. Far is one row with the name and the worst status; it draws no sockets, so its connections
    // leave the card's edge evenly spaced.
    internal enum CardDetail { Full, Compact, Far }
    private CardDetail detail = CardDetail.Full;
    private bool DrawsSockets => detail != CardDetail.Far;
    private const double CompactWidth = 230, FarWidth = 210, FarHeight = 22;
    private double SiblingGap => detail switch { CardDetail.Far => 4, CardDetail.Compact => 10, _ => TopologyLayout.Gap };
    private double layoutWidth = 1100, inspectorWidth = 330;
    private double ReadingScale => 1;
    private readonly HashSet<string> visibleIds = [];
    private readonly Dictionary<string, string> pathLabels = [];
    private readonly Dictionary<string, System.Windows.Shapes.Path> wires = [];
    private readonly Dictionary<string, List<Point>> wireRoutes = [];
    private readonly HashSet<string> snappedWires = [];
    private readonly Dictionary<string, Button> portSlots = [];
    private readonly Dictionary<string, Button> connectedPorts = [];
    private readonly Dictionary<string, Point> portAnchors = [];
    private readonly Dictionary<string, List<UsbNode>> edgePortCache = [];
    private readonly Dictionary<string, UsbNode> nodeParents = [];
    private readonly Dictionary<string, NodeVisuals.SocketPart> socketParts = [];
    private readonly HashSet<string> stackedHubs = [], stackableHubs = [];
    private List<UsbNode> matches = [];
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string appliedQuery = "";
    private static List<(Severity Severity, string Text)> Issues(UsbNode n) => IssueRules.For(n);
    private enum IssueRow { Other, Link, Power }
    // Speed and power problems sit beside the number they qualify; the rest gather below the metrics.
    private static IssueRow RowOf(string issue) => issue switch
    {
        string speed when speed.StartsWith("Running at", StringComparison.Ordinal) => IssueRow.Link,
        "Insufficient bandwidth" or "Link nearly full" or "Could exceed when streaming" or "Shared TT nearly full" or "Shared TT could exceed" => IssueRow.Link,
        "Insufficient power" or "Overcurrent" or "Power at risk" or "Over power budget" or "Hub adapter not detected" => IssueRow.Power,
        _ => IssueRow.Other
    };
    private sealed record MetricRow(List<(NodeVisuals.Metric? Glyph, string Text, string Words)> Parts, List<(Severity Severity, string Text)> Issues);
    internal static bool ShowsPolling(UsbNode n) => Topology.ShowsPolling(n);
    // A card's one line of figures, link rate, polling rate for input devices, then requested power,
    // followed by the warnings that qualify them. Reserved bandwidth lives in the meter where it can
    // matter, the parent's socket shows USB-C, and sockets, not a count, show occupancy.
    private static MetricRow CardFigures(UsbNode n)
    {
        var issues = Issues(n).Where(i => RowOf(i.Text) != IssueRow.Other).OrderBy(i => RowOf(i.Text)).ToList();
        var parts = new List<(NodeVisuals.Metric?, string, string)>();
        string link = ShortSpeed(n);
        // A named fault's badge already says what the status would.
        if (n.Kind == "Unavailable" && issues.Count == 0) parts.Add((null, n.Status, n.Status));
        else if (n.Kind is "Device" or "Hub") parts.Add((NodeVisuals.Metric.Link, link, link + " link"));
        if (ShowsPolling(n))
        {
            string rate = UsbBudgets.PollingRate(n.PollIntervalMs!.Value);
            parts.Add((NodeVisuals.Metric.Polling, rate, "polled at " + rate));
        }
        if (n.Kind is "Device" or "Hub" or "Unavailable" && (n.MaxPowerMa.HasValue || issues.Any(i => RowOf(i.Text) == IssueRow.Power)))
        {
            var (text, words) = PowerFigure(n);
            parts.Add((NodeVisuals.Metric.Power, text, words));
        }
        return new(parts, issues);
    }
    internal static bool UsesExternalPower(UsbNode n) => Topology.UsesExternalPower(n);
    internal static (string Text, string Words) PowerFigure(UsbNode n) => Topology.PowerFigure(n);
    private static List<(Severity Severity, string Text)> OtherIssues(UsbNode n) =>
        n.Kind is "Controller" or "Root hub" ? Issues(n).Concat(MergedRoot(n) is UsbNode root ? Issues(root) : []).Distinct().ToList() : Issues(n).Where(i => RowOf(i.Text) == IssueRow.Other).ToList();
    // Reserved bandwidth earns a place on a card where it can decide anything: hubs, whose upstream link
    // everything behind them shares, and devices that stream, which reserve far more while active.
    private static bool ShowsMeter(UsbNode n) => UsbBudgets.LinkUse(n) != null
        && (n.Kind == "Hub" || NodeVisuals.Color(n) is "Audio" or "Video" || (n.PeakReservedMbps ?? 0) - (n.ReservedMbps ?? 0) >= 0.5);
    private static (double Now, double Peak, double Capacity, string Label)? MeterFor(UsbNode n)
    {
        if (!ShowsMeter(n) || UsbBudgets.LinkUse(n) is not (var now, var capacity, _)) return null;
        double peak = Math.Max(now, UsbBudgets.PeakThroughLink(n).Mbps);
        bool more = peak > now + 0.01;
        string of = " of " + UsbBudgets.Rate(capacity);
        string label = n.Kind == "Hub"
            ? UsbBudgets.Rate(more ? peak : now) + of + (more ? " at peak" : " reserved")
            : more ? "up to " + UsbBudgets.Rate(peak) + of : UsbBudgets.Rate(now) + of + " reserved";
        return (now, peak, capacity, label);
    }
    private static string MetricHelp(UsbNode n)
    {
        var lines = new List<string>();
        if (n.Kind is "Device" or "Hub") lines.Add($"Link: {n.Speed}. The signaling rate negotiated when the device connected, shared with everything upstream on the same path. Not a measured speed.");
        if (n.Kind == "Device" && n.PollIntervalMs is double ms)
            lines.Add($"Polling: the host asks it for input {UsbBudgets.PollingInterval(ms)} ({UsbBudgets.PollingRate(ms)}), as its endpoint descriptor requests. A device skips a poll when it has nothing new, so this is not a measured report rate.");
        if (n.Kind == "Hub" && UsbBudgets.LinkUse(n) is (var through, _, var missing))
            lines.Add($"Reserved: {UsbBudgets.Rate(through)} of bus time held by the hub and the devices behind it, which share its upstream link." + (missing > 0 ? $" {missing} device(s) behind it did not report." : "") + " Bulk transfers, such as storage, reserve nothing and share what is left.");
        else if (n.ReservedMbps is double reserved)
            lines.Add($"Reserved: {UsbBudgets.Rate(reserved)} of bus time held by open interrupt and isochronous pipes" + (n.PeakReservedMbps > reserved ? $", up to {UsbBudgets.Rate(n.PeakReservedMbps.Value)} when fully active" : "") + ". Bulk transfers, such as storage, reserve nothing and share what is left.");
        if (UsbBudgets.LinkUse(n) is (var used, var capacity, _))
            lines.Add($"Meter: reservations fill {UsbBudgets.Share(used, capacity)}, the most this link reserves for timed transfers; the lighter part runs to {UsbBudgets.Rate(Math.Max(used, UsbBudgets.PeakThroughLink(n).Mbps))} if everything on it streams at once. It shows bus time set aside, not traffic measured.");
        if (UsbBudgets.SharedTtUse(n) is (var ttNow, var ttPeak, _, > 0 and var ports))
            lines.Add($"Shared TT: full- and low-speed devices on {ports} port(s) share one 12 Mb/s bus behind this hub and hold {UsbBudgets.Share(ttNow, UsbBudgets.FullSpeedReservableMbps)} of what it can reserve" + (ttPeak > ttNow + 0.01 ? $", up to {UsbBudgets.Rate(ttPeak)} at peak." : "."));
        if (UsesExternalPower(n))
            lines.Add(n.MaxPowerMa is > 0 ? $"Power: has its own supply and also requests up to {n.MaxPowerMa} mA from the bus. A declared maximum, not a measurement." : "Power: runs on its own supply and requests no current from the bus, so there is no bus draw to show.");
        else if (n.MaxPowerMa is int ma) lines.Add($"Power: requests up to {ma} mA ({ma * 0.005:0.##} W at 5 V) in its descriptor. A declared maximum, not a measurement.");
        return string.Join("\n", lines);
    }
    private static string Issue(UsbNode n) => IssueRules.Summary(n);
    private bool Matches(UsbNode n, string q) => Topology.SearchText(n, pathLabels.GetValueOrDefault(n.Id)).Contains(q, StringComparison.OrdinalIgnoreCase);
    private static string TtType(UsbNode n) => Topology.TtType(n);
    private bool Visible(UsbNode n) => visibleIds.Contains(n.Id);
    // On Windows each xHCI controller has one root hub, and to the user they are one thing: a host whose
    // sockets are the root ports. They share one card; a controller with several root hubs, or none
    // readable, keeps them apart.
    private readonly Dictionary<string, UsbNode> mergedHosts = [];
    private static UsbNode? MergedRoot(UsbNode n) => Topology.MergedRoot(n);
    // The node whose card shows this one: a merged root hub is drawn by its controller.
    private UsbNode CardNode(UsbNode n) => mergedHosts.GetValueOrDefault(n.Id) ?? n;
    // Devices follow their sockets along the edge, so connections never cross.
    private List<UsbNode> Children(UsbNode n)
    {
        if (folded.Contains(n.Id) && appliedQuery.Length == 0) return [];
        var order = EdgePorts(n);
        return (MergedRoot(n) ?? n).Children.Where(c => c.Kind != "Empty port" && Visible(c) && (focusedIds == null || focusedIds.Contains(c.Id))).OrderBy(c => order.Count > 0 ? order.IndexOf(c) : c.Port).ToList();
    }
    // Every logical port is drawn on its hub, occupied or not, so the sockets themselves show occupancy.
    // A USB 3 socket's two halves on the same hub sit together as one socket, at the place of its
    // lower-numbered half. Cached per drawing pass; layout queries each hub's ports many times while
    // choosing staircases.
    private List<UsbNode> EdgePorts(UsbNode n)
    {
        if (edgePortCache.TryGetValue(n.Id, out var ports)) return ports;
        if (n.Kind is not ("Hub" or "Root hub")) return edgePortCache[n.Id] = MergedRoot(n) is UsbNode root ? EdgePorts(root) : [];
        var shown = n.Children.Where(c => focusedIds == null || focusedIds.Contains(c.Id)).ToList();
        UsbNode? Partner(UsbNode port) => SocketPartner(port) is UsbNode other && shown.Contains(other) ? other : null;
        ports = shown.OrderBy(c => Math.Min(c.Port, Partner(c)?.Port ?? c.Port)).ThenBy(c => c.Port).ToList();
        foreach (var port in ports)
            socketParts[port.Id] = Partner(port) is not UsbNode other ? NodeVisuals.SocketPart.Whole : port.Port < other.Port ? NodeVisuals.SocketPart.First : NodeVisuals.SocketPart.Second;
        return edgePortCache[n.Id] = ports;
    }
    // The other half of a port's socket, when Windows pairs them on the same hub and each names the other.
    private UsbNode? SocketPartner(UsbNode port) =>
        port.CompanionId.Length > 0 && nodeParents.TryGetValue(port.Id, out var hub) && nodeParents.GetValueOrDefault(port.CompanionId) == hub
            && hub.Children.FirstOrDefault(c => c.Id == port.CompanionId) is UsbNode other && other.CompanionId == port.Id ? other : null;
    // Paths of the cards holding the other halves of this card's sockets. Windows sees a USB 3 hub as a
    // USB 2 hub and a USB 3 hub with the same sockets, and each gets its own card. A hub already labeled
    // as one side of a paired hub doesn't repeat its partner here.
    private List<string> SharedSockets(UsbNode n) => EdgePorts(n)
        .Where(p => p.CompanionId.Length > 0 && SocketPartner(p) == null && nodeParents.TryGetValue(p.CompanionId, out var hub) && CardNode(hub).Id != n.CompanionHubId)
        .Select(p => pathLabels.GetValueOrDefault(CardNode(nodeParents[p.CompanionId]).Id, "")).Where(path => path.Length > 0).Distinct().ToList();
    // Socket geometry: 30×22 sockets, 34 apart along a vertical card's bottom edge and 28 apart down a
    // horizontal card's right edge. A socket's two halves touch.
    private const double SocketWidth = NodeVisuals.SocketWidth, SocketHeight = NodeVisuals.SocketHeight, SocketPitch = 34, SocketStep = 28;
    // Each logical port gets an equal slice of the edge, and a socket's two halves meet on the line
    // between their slices. A staircase gathers the ports at the card's right end so its column of
    // devices can tuck under the card.
    private double? PortOffset(UsbNode parent, UsbNode child)
    {
        if (!DrawsSockets) return null;
        var ports = EdgePorts(parent);
        int i = ports.FindIndex(p => p.Id == child.Id);
        if (i < 0) return null;
        double edge = horizontalTree ? HeightFor(parent) : WidthFor(parent), half = (horizontalTree ? SocketHeight : SocketWidth) / 2;
        if (stackedHubs.Contains(parent.Id))
        {
            double offset = edge - 22;
            for (int k = ports.Count - 1; k > i; k--) offset -= socketParts.GetValueOrDefault(ports[k].Id) == NodeVisuals.SocketPart.Second ? 2 * half : SocketPitch;
            return offset;
        }
        return socketParts.GetValueOrDefault(child.Id) switch
        {
            NodeVisuals.SocketPart.First => edge * (i + 1) / ports.Count - half,
            NodeVisuals.SocketPart.Second => edge * i / ports.Count + half,
            _ => edge * (i + 0.5) / ports.Count
        };
    }
    private double WidthFor(UsbNode n)
    {
        // A far row's connections leave its edge 10 apart, so a vertical one widens for many children.
        if (detail == CardDetail.Far) return horizontalTree ? FarWidth : Math.Max(FarWidth, Children(n).Count * 10 + 20);
        double card = detail == CardDetail.Compact ? CompactWidth : CardWidth;
        return horizontalTree ? card + (EdgePorts(n).Count > 0 ? SocketWidth + 6 : 0) : Math.Max(card, EdgePorts(n).Count * SocketPitch + 20);
    }
    // Card height from its rows: the name, a custom label's detected name, the figures line (taller when
    // its warnings wrap) or a host's summary, the bandwidth meter, other warnings, the card sharing its
    // sockets, and the socket strip.
    private double HeightFor(UsbNode n)
    {
        if (detail == CardDetail.Far) return horizontalTree ? Math.Max(FarHeight, Children(n).Count * 10 + 2) : FarHeight;
        if (detail == CardDetail.Compact)
        {
            double compact = 2 + 8 + 22 + 8 + (n.Kind is "Controller" or "Root hub" ? 20 : CardFigures(n).Parts.Count > 0 ? 17 : 0);
            int sockets = EdgePorts(n).Count;
            return sockets == 0 ? compact : horizontalTree ? Math.Max(compact, sockets * SocketStep + 12) : compact + 6 + SocketHeight;
        }
        double height = 2 + 8 + 22 + 8;
        if (n.UserLabel.Length > 0) height += 16;
        if (HubRelationships.CardLabel(n).Length > 0) height += 19;
        height += n.Kind is "Controller" or "Root hub" ? 20 : RowHeight(n, CardFigures(n));
        if (ShowsMeter(n)) height += 4 + 18;
        height += 24 * BadgeRows(n, OtherIssues(n));
        if (SharedSockets(n).Count > 0) height += 16;
        int ports = EdgePorts(n).Count;
        if (ports > 0) height = horizontalTree ? Math.Max(height, ports * SocketStep + 12) : height + 6 + SocketHeight;
        return height;
    }
    // Lines that badges, after any leading text, wrap onto, estimated from label lengths at the badge font size.
    private int BadgeRows(UsbNode n, IEnumerable<(Severity Severity, string Text)> issues, double lead = 0)
    {
        double available = WidthFor(n) - 22, x = lead; int rows = lead > 0 ? 1 : 0;
        foreach (var (_, text) in issues)
        {
            double width = 34 + text.Length * 6.4;
            if (rows == 0 || x + width > available) { rows++; x = 0; }
            x += width;
        }
        return rows;
    }
    private static double TextWidth(MetricRow row) => row.Parts.Count == 0 ? 0 : row.Parts.Sum(p => p.Text.Length * 6.2 + (p.Glyph != null ? 15 : 0)) + (row.Parts.Count - 1) * 18 + 8;
    private double RowHeight(UsbNode n, MetricRow row) => row.Issues.Count == 0 ? 17 : 24 * BadgeRows(n, row.Issues, TextWidth(row));
    private void PrepareGraph()
    {
        appliedQuery = Search.Text.Trim();
        pathLabels.Clear(); visibleIds.Clear(); matches.Clear(); edgePortCache.Clear(); mergedHosts.Clear(); nodeParents.Clear(); socketParts.Clear();
        foreach (var controller in snapshot.Controllers) if (MergedRoot(controller) is UsbNode root) mergedHosts[root.Id] = controller;
        // A merged root hub shares its controller's path, so root ports read H01/03.
        void Visit(UsbNode n, string path)
        {
            pathLabels[n.Id] = path;
            foreach (var child in n.Children) nodeParents[child.Id] = n;
            foreach (var child in n.Children) Visit(child, mergedHosts.ContainsKey(child.Id) ? path : path + "/" + (child.Port > 0 ? child.Port.ToString("00") : "root"));
            bool match = appliedQuery.Length > 0 && !mergedHosts.ContainsKey(n.Id) && Matches(n, appliedQuery);
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
        PrepareGraph(); PrepareFocus();
        UpdateDeviceTree();
        Graph.Children.Clear(); cards.Clear(); wires.Clear(); wireRoutes.Clear(); snappedWires.Clear(); portSlots.Clear(); connectedPorts.Clear(); portAnchors.Clear();
        var roots = snapshot.Controllers.Where(n => Visible(n) && (focusedIds == null || focusedIds.Contains(n.Id))).ToList();
        const double margin = 16, controllerGap = 24;
        var layouts = ArrangeLayouts(roots, layoutWidth - margin * 2, controllerGap);
        // Controllers are siblings like any other row, so they sit side by side and never wrap;
        // horizontal trees stack them instead.
        double top = 12, left = margin, rowHeight = 0, maxRight = 0;
        foreach (var tree in layouts)
        {
            if (left > margin && (horizontalTree || left + tree.Width > layoutWidth)) { top += rowHeight + 36; left = margin; rowHeight = 0; }
            Place(tree, left, top);
            left += tree.Width + controllerGap;
            rowHeight = Math.Max(rowHeight, tree.Height);
            maxRight = Math.Max(maxRight, left - controllerGap + margin);
        }
        top += rowHeight + 36;
        Graph.Width = Math.Max(CardWidth + margin * 2, maxRight);
        Graph.Height = Math.Max(200, top);
        EmptyMessage.Text = snapshot.Controllers.Count == 0 ? "No USB controllers found. Try Refresh." : "No matching devices. Press Escape to clear search.";
        EmptyMessage.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection(); UpdateGraphHint();
        // The socket legend is redrawn with the graph so it follows the theme.
        SocketLegend.Children.Clear(); foreach (var entry in NodeVisuals.SocketLegend()) SocketLegend.Children.Add(entry);
        SocketLegend.ToolTip = NodeVisuals.SocketLegendHelp;
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
        // Linked hub stages are arranged around their sockets, so a far view, which draws none, shows the real
        // hierarchy instead, as the horizontal layout does.
        List<TopologyLayout.Item> Measure() => roots.Select(r => TopologyLayout.Measure(r, Children, horizontalTree, WidthFor, HeightFor, PortOffset, stackedHubs, DrawsSockets ? SnappedStages : null, SiblingGap)).ToList();
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
        return layouts;
    }
    private void Place(TopologyLayout.Item layout, double left, double top)
    {
        if (layout.SnappedStages != null) { PlaceSnapped(layout, left, top); return; }
        var node = layout.Node;
        double height = HeightFor(node), width = WidthFor(node);
        double x = left + layout.CardX, y = top + layout.CardY;
        var bounds = new Rect(x, y, width, height);
        bool host = node.Kind is "Controller" or "Root hub";
        var panel = new StackPanel();
        // The name leads: icon, shortened name and fold button on one line. Type and location are in
        // the tooltip, tree and inspector; the color already says what a device does.
        bool far = detail == CardDetail.Far;
        var title = new DockPanel { Height = far ? 20 : 22 };
        var icon = NodeVisuals.Icon(node, far ? 16 : 20); icon.Margin = new Thickness(0, 0, far ? 6 : 7, 0);
        DockPanel.SetDock(icon, Dock.Left); title.Children.Add(icon);
        // A far row has no room for a fold button; double-clicking it still folds the branch.
        if (!far && (MergedRoot(node) ?? node).Children.Any(c => c.Kind != "Empty port"))
        {
            var fold = new Button { Content = folded.Contains(node.Id) && appliedQuery.Length == 0 ? "+" : "−", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Expand / collapse branch", IsEnabled = appliedQuery.Length == 0 };
            fold.Click += (_, e) => { if (!folded.Add(node.Id)) folded.Remove(node.Id); Draw(); ShowDetails(); e.Handled = true; };
            DockPanel.SetDock(fold, Dock.Right); title.Children.Add(fold);
        }
        // Below full detail, the worst issue's glyph stands in for the badges.
        if (detail != CardDetail.Full && (host ? OtherIssues(node) : Issues(node)) is { Count: > 0 } worst)
        {
            var glyph = NodeVisuals.StatusGlyph(worst.Max(i => i.Severity)); glyph.Margin = new Thickness(6, 0, 0, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.ToolTip = string.Join(" · ", worst.Select(i => i.Text)); DockPanel.SetDock(glyph, Dock.Right); title.Children.Add(glyph);
        }
        var name = new TextBlock { Text = NodeVisuals.ShortName(node), FontSize = far ? 13 : 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = node.DisplayName + (CanNameDevice(node) ? "\nDouble-click to rename" : "") };
        // Double-clicking the name renames; double-clicking elsewhere on the card still folds its branch.
        name.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && CanNameDevice(node)) { EditDeviceName(node, name); e.Handled = true; } };
        title.Children.Add(name);
        panel.Children.Add(title);
        var figures = CardFigures(node);
        string metric = string.Join(" · ", figures.Parts.Select(p => p.Words));
        if (detail == CardDetail.Full) AddCardRows(node, panel, figures, host);
        else if (detail == CardDetail.Compact)
        {
            if (host) panel.Children.Add(new TextBlock { Text = pathLabels[node.Id] + " · " + ProtocolSummary(node), FontSize = 12, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            else if (figures.Parts.Count > 0)
            {
                var line = NodeVisuals.MetricLine(figures.Parts.Select(p => (p.Glyph, p.Text))); line.Margin = new Thickness(0, 3, 0, 0); line.ToolTip = MetricHelp(node);
                panel.Children.Add(line);
            }
        }
        var edgePorts = DrawsSockets ? EdgePorts(node) : [];
        var padding = far ? new Thickness(7, 0, 7, 0) : edgePorts.Count == 0 ? new Thickness(10, 8, 10, 8) : horizontalTree ? new Thickness(10, 8, 10 + SocketWidth + 6, 8) : new Thickness(10, 8, 10, 8 + 6 + SocketHeight);
        if (far) panel.VerticalAlignment = VerticalAlignment.Center;
        var card = new Border { Width = width, Height = height, Padding = padding, CornerRadius = new CornerRadius(host ? 3 : 6), Background = Brush(NodeVisuals.Fill(node)), BorderBrush = Brush(NodeVisuals.Edge(node)), BorderThickness = new Thickness(1), Child = panel, Cursor = Cursors.Hand, Focusable = true, Tag = node, ToolTip = node.DisplayName + "\n" + NodeVisuals.Label(node) + (metric.Length > 0 ? " · " + metric : "") + "\n" + pathLabels[node.Id] + "\n" + node.LocationEvidence };
        System.Windows.Automation.AutomationProperties.SetName(card, node.DisplayName + ", " + NodeVisuals.Label(node) + ", " + metric + ", " + Issue(node));
        card.MouseLeftButtonDown += (_, e) => { card.Focus(); SelectNode(node); if (e.ClickCount == 2 && node.Children.Count > 0 && appliedQuery.Length == 0) { if (!folded.Add(node.Id)) folded.Remove(node.Id); Draw(); ShowDetails(); } e.Handled = true; };
        card.KeyDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, card)) return;
            if (e.Key is Key.Enter or Key.Space) { SelectNode(node); e.Handled = true; }
            else if (e.Key == Key.F2 && CanNameDevice(node)) { EditDeviceName(node, card); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            {
                var items = cards.Keys.ToList(); int i = items.IndexOf(node.Id);
                var next = items[Math.Clamp(i + (e.Key is Key.Up or Key.Left ? -1 : 1), 0, items.Count - 1)];
                SelectNode((UsbNode)cards[next].Card.Tag); cards[next].Card.Focus(); LocateClick(this, new RoutedEventArgs()); e.Handled = true;
            }
        };
        card.ContextMenuOpening += (_, e) => { card.ContextMenu = RenameMenu(node, CanNamePort(node) ? node : null, card); if (card.ContextMenu.Items.Count == 0) e.Handled = true; };
        card.GotKeyboardFocus += (_, _) => card.BorderBrush = Brush("Accent");
        card.LostKeyboardFocus += (_, _) => UpdateSelection();
        Canvas.SetLeft(card, x); Canvas.SetTop(card, y); Panel.SetZIndex(card, 1); Graph.Children.Add(card); cards[node.Id] = (card, bounds.TopLeft);
        // Far rows sit 4 apart, so a port name tag above one would cover the row before it.
        if (node.PortLabel.Length > 0 && !host && !far)
        {
            var tag = PortTag(node, Math.Max(36, width / 2 - 14));
            Canvas.SetLeft(tag, x + 8); Canvas.SetTop(tag, y - 8); Graph.Children.Add(tag);
        }
        for (int i = 0; i < edgePorts.Count; i++)
        {
            var port = edgePorts[i];
            double cross = (horizontalTree ? y : x) + PortOffset(node, port)!.Value;
            // The port's socket with its number on the tongue: cavity filled when in use, hollow when empty.
            string companion = port.CompanionId.Length > 0 && pathLabels.TryGetValue(port.CompanionId, out var other) ? $"\nShares this socket with port {other}." : "";
            var button = new Button { Content = NodeVisuals.SocketNumber(port), Tag = port, Width = SocketWidth, Height = SocketHeight, Padding = new Thickness(0), Template = NodeVisuals.SocketTemplate(port, socketParts.GetValueOrDefault(port.Id), horizontalTree), Cursor = Cursors.Hand, ToolTip = $"Logical port {port.Port}{(port.PortLabel.Length > 0 ? " · " + port.PortLabel : "")} · {(port.Kind == "Empty port" ? "Empty" : port.DisplayName)}\n{NodeVisuals.SocketLabel(port)} socket\n{port.SocketEvidence}{companion}" };
            System.Windows.Automation.AutomationProperties.SetName(button, $"Port {port.Port}, {NodeVisuals.SocketLabel(port)}, {port.DisplayName}");
            button.Click += (_, e) => { SelectNode(port); e.Handled = true; };
            button.MouseDoubleClick += (_, e) => { EditPortName(port, button); e.Handled = true; };
            button.KeyDown += (_, e) => { if (e.Key == Key.F2) { EditPortName(port, button); e.Handled = true; } };
            button.ContextMenu = RenameMenu(null, port, button);
            Canvas.SetLeft(button, horizontalTree ? bounds.Right - button.Width : cross - button.Width / 2);
            Canvas.SetTop(button, horizontalTree ? cross - button.Height / 2 : bounds.Bottom - button.Height);
            Panel.SetZIndex(button, 2); Graph.Children.Add(button);
            (port.Kind == "Empty port" ? portSlots : connectedPorts)[port.Id] = button;
            portAnchors[port.Id] = horizontalTree ? new Point(bounds.Right, cross) : new Point(cross, bounds.Bottom);
            // A named empty socket has no card to carry its name, so the tag sits just beyond it.
            if (port.Kind == "Empty port" && port.PortLabel.Length > 0)
            {
                // Sockets are narrow, so the tag runs right until the next socket with a wire or its own tag.
                double start = cross - SocketWidth / 2, end = bounds.Right + 40;
                if (edgePorts.Skip(i + 1).FirstOrDefault(p => p.Kind != "Empty port" || p.PortLabel.Length > 0) is UsbNode next)
                    end = (horizontalTree ? y : x) + PortOffset(node, next)!.Value - (next.Kind == "Empty port" ? SocketWidth / 2 : 0) - 3;
                var tag = PortTag(port, horizontalTree ? 90 : Math.Max(SocketWidth, end - start));
                tag.HorizontalAlignment = HorizontalAlignment.Left;
                Canvas.SetLeft(tag, horizontalTree ? bounds.Right + 4 : cross - SocketWidth / 2); Canvas.SetTop(tag, horizontalTree ? cross - 7 : bounds.Bottom + 3);
                Graph.Children.Add(tag);
            }
        }
        var children = layout.Children;
        var childCards = children.Select(c => new Rect(left + c.X + c.CardX, top + c.Y + c.CardY, WidthFor(c.Node), HeightFor(c.Node))).ToList();
        var anchors = children.Select(c => portAnchors.TryGetValue(c.Node.Id, out var anchor) ? anchor : (Point?)null).ToList();
        var routes = layout.SnappedColumn ? childCards.Select((child, i) =>
        {
            var start = anchors[i]!.Value;
            double laneY = y + HeightFor(node) + 32 + (children.Count - i) * 8;
            double laneX = left + layout.Width - 8 - (children.Count - 1 - i) * 8;
            double entryY = child.Y + child.Height / 2;
            return new List<Point> { start, new(start.X, laneY), new(laneX, laneY), new(laneX, entryY), new(child.Right, entryY) };
        }).ToList() : TopologyLayout.Route(bounds, anchors, childCards, layout.Stacked, horizontalTree);
        for (int i = 0; i < children.Count; i++)
        {
            if (layout.SnappedColumn) snappedWires.Add(children[i].Node.Id);
            AddWire(children[i].Node, routes[i]);
            Place(children[i], left + children[i].X, top + children[i].Y);
        }
    }
    // A full card's rows under its name: the paired-hub label, the detected name under a custom label, the
    // figures with the warnings that qualify them (or a host's summary), the meter, other warnings and the
    // card that holds the other halves of its sockets.
    private void AddCardRows(UsbNode node, StackPanel panel, MetricRow figures, bool host)
    {
        var relationship = HubRelationships.CardLabel(node);
        if (relationship.Length > 0)
            panel.Children.Add(new TextBlock { Text = relationship, FontSize = 12, Foreground = Brush("TextSecondary"), Height = 19, ToolTip = HubRelationships.Description(node, snapshot) });
        // Under a custom label, keep the detected name visible; where a name came from is in Detection details.
        if (node.UserLabel.Length > 0)
            panel.Children.Add(new TextBlock { Text = "Detected: " + node.Name, FontSize = 11, Foreground = Brush("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), ToolTip = node.Name + " · " + node.NameSource });
        if (host)
            panel.Children.Add(new TextBlock { Text = pathLabels[node.Id] + " · " + ProtocolSummary(node), FontSize = 12, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = "Ports: " + ProtocolSummary(node) + "\nSupply capacity: unknown; charging limits are not queried." });
        else if (figures.Parts.Count > 0 || figures.Issues.Count > 0)
        {
            // One line of figures; the warnings that qualify them follow on the same row and wrap below.
            var row = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            if (figures.Parts.Count > 0)
            {
                var line = NodeVisuals.MetricLine(figures.Parts.Select(p => (p.Glyph, p.Text)));
                line.ToolTip = MetricHelp(node); line.VerticalAlignment = VerticalAlignment.Center; line.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(line);
            }
            foreach (var (severity, text) in figures.Issues) { var badge = WarningBadge(node, severity, text); badge.Margin = new Thickness(0, 1, 4, 1);  row.Children.Add(badge); }
            panel.Children.Add(row);
        }
        if (MeterFor(node) is var (now, peak, capacity, label))
        {
            var meter = NodeVisuals.Meter(now, peak, capacity, label, NodeVisuals.Edge(node));
            meter.Margin = new Thickness(0, 4, 0, 0); meter.ToolTip = MetricHelp(node);
            panel.Children.Add(meter);
        }
        var issues = OtherIssues(node);
        if (issues.Count > 0)
        {
            var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var (severity, text) in issues) { var badge = WarningBadge(node, severity, text); badge.Margin = new Thickness(0, 0, 4, 0);  badges.Children.Add(badge); }
            panel.Children.Add(badges);
        }
        // The other half of a USB 3 hub, drawn as its own card, holds the other halves of these sockets.
        if (SharedSockets(node) is { Count: > 0 } shared)
            panel.Children.Add(new TextBlock { Text = "Shares its sockets with " + string.Join(", ", shared), FontSize = 11, Foreground = Brush("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Tag = SharedSocketsTag, ToolTip = SharedSocketsHelp(shared) });
    }
    private const string SharedSocketsTag = "shared-sockets";
    private static string SharedSocketsHelp(List<string> shared) => $"Windows sees each USB 3 socket as two logical ports, one USB 2 and one USB 3, and sees a USB 3 hub as two hubs, one for each. This card's ports and those on {string.Join(", ", shared)} are the two halves of the same sockets; each socket's tooltip names its other half.";
    private void AddWire(UsbNode node, List<Point> route)
    {
        var wire = new System.Windows.Shapes.Path { Data = RoundedRoute(route, 6), Stroke = Brush(NodeVisuals.WireInk(node)), StrokeThickness = NodeVisuals.WireWidth(node), StrokeDashArray = NodeVisuals.WireDashes(node), Tag = node, IsHitTestVisible = false };
        Graph.Children.Add(wire); wires[node.Id] = wire; wireRoutes[node.Id] = route;
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
        // A merged root hub is selected as its host card. A port is drawn only as a socket, so selecting one
        // brings back a level that draws sockets.
        node = CardNode(node);
        if (node.Kind == "Empty port" && !DrawsSockets) { detail = CardDetail.Compact; Draw(); SetZoom(Math.Max(GraphScale.ScaleX, ReadableScale)); }
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
            var node = (UsbNode)item.Card.Tag;
            bool match = appliedQuery.Length > 0 && Matches(node, appliedQuery);
            // Fill always says what a device does; selection is an accent outline with a soft glow, so it
            // never reads as another category.
            bool chosen = id == selected?.Id;
            item.Card.Background = Brush(NodeVisuals.Fill(node));
            item.Card.BorderBrush = Brush(chosen || item.Card.IsKeyboardFocusWithin || match ? "Accent" : NodeVisuals.Edge(node));
            item.Card.BorderThickness = new Thickness(chosen || match ? 2 : 1);
            item.Card.Effect = chosen ? new System.Windows.Media.Effects.DropShadowEffect { Color = ((SolidColorBrush)Brush("Accent")).Color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.75 } : null;
        }
        // The selected path recolors its connections; their widths and dashes keep saying what each link is.
        foreach (var (id, wire) in wires) wire.Stroke = Brush(chain.Contains(id) ? "Accent" : NodeVisuals.WireInk((UsbNode)wire.Tag));
        // An occupied socket's cavity fills in the color of its wire, as a plug would, accent on the selected
        // path; an empty one stays hollow, so occupancy reads even on folded hubs. Tongues keep their color.
        foreach (var (id, slot) in connectedPorts)
        {
            string ink = chain.Contains(id) ? "Accent" : NodeVisuals.WireInk((UsbNode)slot.Tag);
            slot.Background = slot.BorderBrush = Brush(ink);
            if (!NodeVisuals.HasTongue((UsbNode)slot.Tag)) ((TextBlock)slot.Content).Foreground = Brush(ink is "Accent" or "Warning" ? "OnAccent" : "TextPrimary");
            slot.BorderThickness = NodeVisuals.SocketBorder(socketParts.GetValueOrDefault(id), horizontalTree, 1);
        }
        foreach (var (id, slot) in portSlots)
        {
            bool chosen = id == selected?.Id, match = appliedQuery.Length > 0 && Matches((UsbNode)slot.Tag, appliedQuery);
            slot.Background = Brush(chosen ? "Selection" : "Surface");
            slot.BorderBrush = Brush(chosen || match ? "Accent" : "Wire");
            slot.BorderThickness = NodeVisuals.SocketBorder(socketParts.GetValueOrDefault(id), horizontalTree, chosen || match ? 2 : 1);
        }
        LocateButton.IsEnabled = selected != null && (cards.ContainsKey(selected.Id) || portSlots.ContainsKey(selected.Id));
        int index = matches.FindIndex(n => n.Id == selected?.Id);
        MatchCount.Text = appliedQuery.Length == 0 ? "" : index >= 0 ? $"{index + 1} / {matches.Count} matches" : $"{matches.Count} matches";
        NextMatchButton.Visibility = appliedQuery.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextMatchButton.IsEnabled = matches.Count > 0;
    }
    private void ApplySearch()
    {
        focusedBranch = null; FocusBranchButton.Content = "Focus branch";
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
    private void InspectorClick(object sender, RoutedEventArgs e)
    {
        bool hide = InspectorPanel.Visibility == Visibility.Visible;
        if (hide) inspectorWidth = InspectorColumn.ActualWidth;
        InspectorColumn.MinWidth = hide ? 0 : 260;
        InspectorColumn.Width = new GridLength(hide ? 0 : inspectorWidth);
        SplitterColumn.Width = new GridLength(hide ? 0 : 5);
        InspectorPanel.Visibility = InspectorSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        InspectorButton.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout(); FitSidePanels();
    }
    private void UpdateIssues()
    {
        // Notes are worth knowing but affect nothing now, so they are counted apart from what needs attention.
        var worstOf = snapshot.Nodes.Select(n => Issues(n).Select(i => i.Severity).DefaultIfEmpty((Severity)(-1)).Max()).ToList();
        int notes = worstOf.Count(s => s == Severity.Note), attention = worstOf.Count(s => s > Severity.Note) + snapshot.Diagnostics.Count;
        var worst = attention > 0 ? worstOf.Append(Severity.Warning).Max() : Severity.Note;
        static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
        string summary = string.Join(" · ", new[] { attention > 0 ? Count(attention, "issue") : "", notes > 0 ? Count(notes, "note") : "" }.Where(s => s.Length > 0));
        if (summary.Length == 0) { IssuesButton.Content = new TextBlock { Text = "No issues", Foreground = Brush("TextMuted") }; IssuesButton.ClearValue(BackgroundProperty); }
        else { IssuesButton.Content = NodeVisuals.StatusContent(worst, summary); IssuesButton.Background = Brush(NodeVisuals.StatusColor(worst) + "Surface"); }
        IssuesButton.IsEnabled = summary.Length > 0;
        StatusText.Text = (snapshot.IsDemo ? "Demo hardware" : "Local scan") + $" · Updated {snapshot.CapturedAt:T}"
            + (attention > 0 ? $" · {Count(attention, "item")} {(attention == 1 ? "needs" : "need")} attention" : " · No issues detected") + (notes > 0 ? $" · {Count(notes, "note")}" : "");
        if (snapshot.Diagnostics.Count > 0) StatusText.Text += " · " + string.Join(" · ", snapshot.Diagnostics);
        StatusText.ToolTip = StatusText.Text;
    }
    private void IssuesClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = Brush("Surface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("Border") };
        foreach (var node in snapshot.Nodes.Where(n => Issue(n).Length > 0))
        {
            var item = new MenuItem { Header = $"{Issue(node)} — {node.DisplayName} ({pathLabels.GetValueOrDefault(node.Id)})", Icon = NodeVisuals.StatusGlyph(Issues(node).Max(i => i.Severity)) };
            item.Click += (_, _) => { Search.Clear(); searchTimer.Stop(); foreach (var ancestor in FindPath(node.Id)) folded.Remove(ancestor.Id); Draw(); ShowOnCanvas(node); };
            menu.Items.Add(item);
        }
        foreach (var diagnostic in snapshot.Diagnostics) menu.Items.Add(new MenuItem { Header = diagnostic, IsEnabled = false, Icon = NodeVisuals.StatusGlyph(Severity.Warning) });
        menu.PlacementTarget = IssuesButton; menu.IsOpen = true;
    }
}

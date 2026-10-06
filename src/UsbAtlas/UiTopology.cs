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
    private const double CompactWidth = 230, FarWidth = 190, FarHeight = 22;
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
    private readonly HashSet<string> packedHubs = [];
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
    private MetricRow CardFigures(UsbNode n)
    {
        var issues = CardIssues(n).Where(i => RowOf(i.Text) != IssueRow.Other).OrderBy(i => RowOf(i.Text)).ToList();
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
    // A merged card carries both sides' issues, so nothing about the USB 2 side leaves the canvas.
    private List<(Severity Severity, string Text)> CardIssues(UsbNode n) => Sides(n).SelectMany(Issues).Distinct().ToList();
    private List<(Severity Severity, string Text)> OtherIssues(UsbNode n) =>
        n.Kind is "Controller" or "Root hub" ? Issues(n).Concat(MergedRoot(n) is UsbNode root ? Issues(root) : []).Distinct().ToList() : CardIssues(n).Where(i => RowOf(i.Text) == IssueRow.Other).ToList();
    // Reserved bandwidth earns a place on a card where it can decide anything: hubs, whose upstream link
    // everything behind them shares, and devices that stream, which reserve far more while active.
    private static bool ShowsMeter(UsbNode n) => UsbBudgets.LinkUse(n) != null
        && (n.Kind == "Hub" || NodeVisuals.Color(n) is "Audio" or "Video" || (n.PeakReservedMbps ?? 0) - (n.ReservedMbps ?? 0) >= 0.5);
    private static (double Now, double Peak, double Capacity, string Label)? MeterFor(UsbNode n)
    {
        if (!ShowsMeter(n) || UsbBudgets.LinkUse(n) is not (var now, var capacity, _)) return null;
        double peak = Math.Max(now, UsbBudgets.PeakThroughLink(n).Mbps);
        // Shares of what the link can reserve, so every meter reads on the same scale; the rates are in the tooltip.
        static string Percent(double share) => share is > 0 and < 0.005 ? "<1%" : $"{share * 100:0}%";
        string label = peak > now + 0.01
            ? $"{Percent(now / capacity)} reserved · up to {Percent(peak / capacity)} {(n.Kind == "Hub" ? "at peak" : "streaming")}"
            : $"{Percent(now / capacity)} of link reserved";
        return (now, peak, capacity, label);
    }
    // A hub's meter has a part for the hub and one for each device or hub on its ports, in socket order;
    // together they are what its link holds. A device's meter is all its own.
    private static List<NodeVisuals.MeterPart> MeterParts(UsbNode n)
    {
        static NodeVisuals.MeterPart Through(UsbNode node)
        {
            double now = UsbBudgets.ReservedThroughLink(node).Mbps;
            return new(node, now, Math.Max(now, UsbBudgets.PeakThroughLink(node).Mbps));
        }
        var own = new NodeVisuals.MeterPart(n, n.ReservedMbps ?? 0, Math.Max(n.ReservedMbps ?? 0, n.PeakReservedMbps ?? 0));
        return n.Kind != "Hub" ? [own] : [own, .. n.Children.Where(c => c.Kind is "Device" or "Hub").OrderBy(c => c.Port).Select(Through)];
    }
    // Hovering a part of a hub's meter rings the card it stands for, and hovering a card lights its parts.
    // The ring is its own overlay, so selection redraws never reset it.
    private readonly Dictionary<string, List<Border>> meterSegments = [];
    private Border? partRing;
    private const string PartRingTag = "part-ring";
    private void ShowPart(string id, bool on, bool ring)
    {
        foreach (var segment in meterSegments.GetValueOrDefault(id) ?? []) segment.Background = Brush(on ? "Accent" : "Neutral");
        if (partRing != null) { Graph.Children.Remove(partRing); partRing = null; }
        if (!on || !ring || !cards.TryGetValue(id, out var item)) return;
        var area = new Rect(item.Point, new Size(item.Card.Width, item.Card.Height)); area.Inflate(4, 4);
        partRing = new Border { Width = area.Width, Height = area.Height, CornerRadius = new CornerRadius(9), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(2), IsHitTestVisible = false, Tag = PartRingTag };
        Canvas.SetLeft(partRing, area.X); Canvas.SetTop(partRing, area.Y); Panel.SetZIndex(partRing, 3); Graph.Children.Add(partRing);
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
    // A USB 3 hub appears to Windows as a USB 2 hub and a USB 3 hub with the same sockets. When Windows
    // pairs them, both hang off the same card, and no other device's connection runs between their two
    // ports, they are drawn as one card: the USB 3 side's, with each socket split into its two halves and
    // two connections in, one per side. Otherwise their wires would have to cross, so they keep two cards
    // that mark each other. Merging only draws: either side still selects as itself.
    private readonly Dictionary<string, UsbNode> mergedHubs = [], mergedSides = [];
    private UsbNode DrawnAs(UsbNode n) => mergedHubs.GetValueOrDefault(n.Id) ?? CardNode(n);
    private IEnumerable<UsbNode> Sides(UsbNode n) => mergedSides.TryGetValue(n.Id, out var side) ? [n, side] : [n];
    // Both sides of the hub on this one's card: a merged card links and unlinks as one stage.
    private List<UsbNode> CardSides(UsbNode n) => Sides(DrawnAs(n)).ToList();
    private bool SnappedToParent(UsbNode n) => CardSides(n).Any(s => s.SnapToParentHub);
    private void PrepareMerges()
    {
        mergedHubs.Clear(); mergedSides.Clear(); edgePortCache.Clear(); socketParts.Clear();
        bool Shown(UsbNode n) => Visible(n) && (focusedIds == null || focusedIds.Contains(n.Id));
        // Outer hubs come first, so the two sides of a hub plugged into a merged one, each on its own side's
        // port, find themselves on one card.
        foreach (var usb2 in snapshot.Nodes.Where(n => n.Kind == "Hub" && n.IsUsb2Companion && n.CompanionHubId.Length > 0 && Shown(n)))
        {
            if (snapshot.Nodes.FirstOrDefault(n => n.Id == usb2.CompanionHubId) is not UsbNode usb3 || !Shown(usb3)
                || !nodeParents.TryGetValue(usb2.Id, out var parent) || !nodeParents.TryGetValue(usb3.Id, out var otherParent) || DrawnAs(parent).Id != DrawnAs(otherParent).Id) continue;
            var ports = EdgePorts(DrawnAs(parent));
            int a = ports.IndexOf(usb2), b = ports.IndexOf(usb3);
            if (a < 0 || b < 0 || ports.Skip(Math.Min(a, b) + 1).Take(Math.Abs(a - b) - 1).Any(p => p.Kind != "Empty port" && Shown(p))) continue;
            mergedHubs[usb2.Id] = usb3; mergedSides[usb3.Id] = usb2;
            edgePortCache.Clear(); socketParts.Clear();
        }
    }
    // Devices follow their sockets along the edge, so connections never cross.
    private List<UsbNode> Children(UsbNode n)
    {
        if (folded.Contains(n.Id) && appliedQuery.Length == 0) return [];
        var order = EdgePorts(n);
        return Sides(MergedRoot(n) ?? n).SelectMany(s => s.Children).Where(c => c.Kind != "Empty port" && !mergedHubs.ContainsKey(c.Id) && Visible(c) && (focusedIds == null || focusedIds.Contains(c.Id)))
            .OrderBy(c => order.Count > 0 ? order.IndexOf(c) : c.Port).ToList();
    }
    // Every logical port is drawn on its hub, occupied or not, so the sockets themselves show occupancy.
    // A USB 3 socket's two halves on the same card sit together as one socket, at the place of its
    // lower-numbered half, or the USB 2 side's on a merged hub, where both halves share a number. Cached
    // per drawing pass; layout queries each hub's ports many times.
    private List<UsbNode> EdgePorts(UsbNode n)
    {
        if (edgePortCache.TryGetValue(n.Id, out var ports)) return ports;
        if (n.Kind is not ("Hub" or "Root hub")) return edgePortCache[n.Id] = MergedRoot(n) is UsbNode root ? EdgePorts(root) : [];
        var shown = Sides(n).SelectMany(s => s.Children).Where(c => focusedIds == null || focusedIds.Contains(c.Id)).ToList();
        UsbNode? Partner(UsbNode port) => SocketPartner(port) is UsbNode other && shown.Contains(other) ? other : null;
        bool Usb2Side(UsbNode port) => nodeParents.TryGetValue(port.Id, out var hub) && mergedHubs.ContainsKey(hub.Id);
        bool First(UsbNode port, UsbNode other) => port.Port < other.Port || port.Port == other.Port && Usb2Side(port);
        ports = shown.OrderBy(c => Math.Min(c.Port, Partner(c)?.Port ?? c.Port)).ThenBy(c => Partner(c) is UsbNode other && First(other, c) ? 1 : 0).ThenBy(c => c.Port).ToList();
        foreach (var port in ports)
            socketParts[port.Id] = Partner(port) is not UsbNode other ? NodeVisuals.SocketPart.Whole : First(port, other) ? NodeVisuals.SocketPart.First : NodeVisuals.SocketPart.Second;
        return edgePortCache[n.Id] = ports;
    }
    // The other half of a port's socket, when Windows pairs them on the same card and each names the other.
    // A port wired inside an enclosure, from one hub chip to the next, has no companion reported at all, but
    // the two sides of the hub on it are the two halves of that one socket.
    private static string SocketCompanion(UsbNode port) => port.CompanionId.Length > 0 ? port.CompanionId : port.Kind == "Hub" && port.CompanionPortNumber == 0 ? port.CompanionHubId : "";
    private UsbNode? SocketPartner(UsbNode port) =>
        SocketCompanion(port) is { Length: > 0 } id && nodeParents.TryGetValue(port.Id, out var hub) && nodeParents.TryGetValue(id, out var otherHub) && DrawnAs(otherHub).Id == DrawnAs(hub).Id
            && otherHub.Children.FirstOrDefault(c => c.Id == id) is UsbNode other && SocketCompanion(other) == port.Id ? other : null;
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
    // between their slices.
    private double? PortOffset(UsbNode parent, UsbNode child)
    {
        if (!DrawsSockets) return null;
        var ports = EdgePorts(parent);
        int i = ports.FindIndex(p => p.Id == child.Id);
        if (i < 0) return null;
        double edge = horizontalTree ? HeightFor(parent) : WidthFor(parent), half = (horizontalTree ? SocketHeight : SocketWidth) / 2;
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
        // Laid out from the left, a tree is shallow and tall, so width is the spare dimension: a card widens
        // for its name, by up to 100 px, before the name has to wrap or be shortened.
        if (horizontalTree) card = Math.Clamp(NameWidth(n) + TitleChrome(n) + 2, card, card + 100);
        return horizontalTree ? card + (EdgePorts(n).Count > 0 ? SocketWidth + 6 : 0) : Math.Max(card, EdgePorts(n).Count * SocketPitch + 20);
    }
    // A card's name at its title size, measured once per drawing pass.
    private readonly Dictionary<string, double> nameWidths = [];
    private double NameWidth(UsbNode n)
    {
        if (nameWidths.TryGetValue(n.Id, out var width)) return width;
        var text = new FormattedText(NodeVisuals.ShortName(n), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("UiFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 14, Brushes.Black, 1);
        return nameWidths[n.Id] = text.WidthIncludingTrailingWhitespace;
    }
    // Everything on a title row besides the name: border, padding, icon, fold button and the worst issue's glyph.
    private double TitleChrome(UsbNode n) => 2 + 20 + 27 + (HasFold(n) ? 28 : 0)
        + (detail != CardDetail.Full && (n.Kind is "Controller" or "Root hub" ? OtherIssues(n) : CardIssues(n)).Count > 0 ? 19 : 0);
    // A full card's name wraps onto a second line, once its card can't widen enough, before it's shortened: the title row is
    // as wide as the card's content, less the icon, the fold button and the worst issue's glyph. Compact
    // and far cards keep to one line, so wrapping never makes Fit all fall back to a simpler level.
    private const double TitleHeight = 22, TitleLine = 18;
    private bool HasFold(UsbNode n) => Sides(MergedRoot(n) ?? n).SelectMany(s => s.Children).Any(c => c.Kind != "Empty port");
    private int TitleLines(UsbNode n)
    {
        if (detail != CardDetail.Full) return 1;
        double room = WidthFor(n) - (horizontalTree && EdgePorts(n).Count > 0 ? SocketWidth + 6 : 0) - TitleChrome(n);
        return NameWidth(n) > room ? 2 : 1;
    }
    private double TitleRowHeight(UsbNode n) => TitleHeight + (TitleLines(n) - 1) * TitleLine;
    // Card height from its rows: the name, a custom label's detected name, the figures line (taller when
    // its warnings wrap) or a host's summary, the bandwidth meter, other warnings, the card sharing its
    // sockets, and the socket strip.
    private double HeightFor(UsbNode n)
    {
        if (detail == CardDetail.Far) return horizontalTree ? Math.Max(FarHeight, Children(n).Count * 10 + 2) : FarHeight;
        if (detail == CardDetail.Compact)
        {
            double compact = 2 + 8 + TitleRowHeight(n) + 8 + (n.Kind is "Controller" or "Root hub" ? 20 : CardFigures(n).Parts.Count > 0 ? 17 : 0);
            int sockets = EdgePorts(n).Count;
            return sockets == 0 ? compact : horizontalTree ? Math.Max(compact, sockets * SocketStep + 12) : compact + 6 + SocketHeight;
        }
        double height = 2 + 8 + TitleRowHeight(n) + 8;
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
        pathLabels.Clear(); visibleIds.Clear(); matches.Clear(); edgePortCache.Clear(); nameWidths.Clear(); mergedHosts.Clear(); nodeParents.Clear(); socketParts.Clear();
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
        UpdateFixFirst();
        string? focusId = null;
        for (var hit = Keyboard.FocusedElement as DependencyObject; hit != null; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
            if (hit is FrameworkElement { Tag: UsbNode n }) { focusId = n.Id; break; }
        PrepareGraph(); PrepareFocus(); PrepareMerges();
        UpdateDeviceTree();
        Graph.Children.Clear(); meterSegments.Clear(); partRing = null; cards.Clear(); wires.Clear(); wireHits.Clear(); wireRoutes.Clear(); snappedWires.Clear(); portSlots.Clear(); connectedPorts.Clear(); portAnchors.Clear();
        var roots = snapshot.Controllers.Where(n => Visible(n) && (focusedIds == null || focusedIds.Contains(n.Id))).ToList();
        const double margin = 16, controllerGap = 24;
        var layouts = ArrangeLayouts(roots);
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
        UpdateSelection();
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
    // A hub's children never wrap: a graph wider than the view scrolls, or Fit all picks simpler cards.
    private List<TopologyLayout.Item> ArrangeLayouts(List<UsbNode> roots)
    {
        packedHubs.Clear();
        // Linked hub stages are arranged around their sockets, so a far view, which draws none, shows the real
        // hierarchy instead, as the horizontal layout does.
        var layouts = roots.Select(r => TopologyLayout.Measure(r, Children, horizontalTree, WidthFor, HeightFor, PortOffset, DrawsSockets ? SnappedStages : null, SiblingGap, horizontalTree && detail == CardDetail.Far, n => mergedSides.GetValueOrDefault(n.Id))).ToList();
        static IEnumerable<TopologyLayout.Item> Flatten(TopologyLayout.Item item) => item.Children.SelectMany(Flatten).Prepend(item);
        packedHubs.UnionWith(layouts.SelectMany(Flatten).Where(i => i.Packed).Select(i => i.Node.Id));
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
        int lines = TitleLines(node); var align = lines > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;
        var title = new DockPanel { Height = far ? 20 : TitleRowHeight(node) };
        var icon = NodeVisuals.Icon(node, far ? 16 : 20); icon.Margin = new Thickness(0, 0, far ? 6 : 7, 0); icon.VerticalAlignment = align;
        DockPanel.SetDock(icon, Dock.Left); title.Children.Add(icon);
        // A far row has no room for a fold button; double-clicking it still folds the branch.
        if (!far && HasFold(node))
        {
            var fold = new Button { Content = folded.Contains(node.Id) && appliedQuery.Length == 0 ? "+" : "−", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = align, ToolTip = "Expand / collapse branch", IsEnabled = appliedQuery.Length == 0 };
            fold.Click += (_, e) => { if (!folded.Add(node.Id)) folded.Remove(node.Id); Draw(); ShowDetails(); e.Handled = true; };
            DockPanel.SetDock(fold, Dock.Right); title.Children.Add(fold);
        }
        // Below full detail, the worst issue's glyph stands in for the badges.
        if (detail != CardDetail.Full && (host ? OtherIssues(node) : CardIssues(node)) is { Count: > 0 } worst)
        {
            var glyph = NodeVisuals.StatusGlyph(worst.Max(i => i.Severity)); glyph.Margin = new Thickness(6, lines > 1 ? 2 : 0, 0, 0); glyph.VerticalAlignment = align;
            glyph.ToolTip = string.Join(" · ", worst.Select(i => i.Text)); DockPanel.SetDock(glyph, Dock.Right); title.Children.Add(glyph);
        }
        var name = new TextBlock { Text = NodeVisuals.ShortName(node), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, LineHeight = TitleLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = lines * TitleLine, VerticalAlignment = align, ToolTip = node.DisplayName + (CanNameDevice(node) ? "\nDouble-click to rename" : "") };
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
        var card = new Border { Width = width, Height = height, Padding = padding, CornerRadius = new CornerRadius(host ? 4 : 8), Background = Brush("NeutralFill"), BorderBrush = Brush("NeutralEdge"), BorderThickness = new Thickness(1), Child = panel, Cursor = Cursors.Hand, Focusable = true, Tag = node, ToolTip = node.DisplayName + "\n" + NodeVisuals.Label(node) + (metric.Length > 0 ? " · " + metric : "") + "\n" + pathLabels[node.Id] + "\n" + node.LocationEvidence };
        System.Windows.Automation.AutomationProperties.SetName(card, node.DisplayName + ", " + NodeVisuals.Label(node) + ", " + metric + ", " + string.Join(" · ", CardIssues(node).Select(i => i.Text)));
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
        // Hovering one side of a hub drawn as two cards rings the other.
        string? pair = node.CompanionHubId.Length > 0 && !mergedSides.ContainsKey(node.Id) && !mergedHubs.ContainsKey(node.Id) ? node.CompanionHubId : null;
        card.MouseEnter += (_, _) => { ShowPart(node.Id, true, false); if (pair != null) ShowPart(pair, true, true); };
        card.MouseLeave += (_, _) => { ShowPart(node.Id, false, false); if (pair != null) ShowPart(pair, false, true); };
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
            string companion = (port.CompanionId.Length > 0 ? port.CompanionId : SocketPartner(port)?.Id) is string otherId && pathLabels.TryGetValue(otherId, out var other) ? $"\nShares this socket with port {other}." : "";
            if (HubRelationships.MissingUsb3HubFor(port, snapshot) is UsbNode lostHub)
                companion += "\n\n" + Explanations.MissingUsb3Half(lostHub, FindPath(lostHub.Id)).What + " Click for what to do.";
            var button = new Button { Content = NodeVisuals.SocketNumber(port), Tag = port, Width = SocketWidth, Height = SocketHeight, Padding = new Thickness(0), Template = NodeVisuals.SocketTemplate(port, socketParts.GetValueOrDefault(port.Id), horizontalTree), Cursor = Cursors.Hand, ToolTip = $"Logical port {port.Port}{(port.PortLabel.Length > 0 ? " · " + port.PortLabel : "")} · {(port.Kind == "Empty port" ? "Empty" : port.DisplayName)}\n{NodeVisuals.SocketLabel(port)} socket\n{port.SocketEvidence}{companion}" };
            // A merged hub's socket halves share a number, so it shows once, on the first half.
            if (socketParts.GetValueOrDefault(port.Id) == NodeVisuals.SocketPart.Second && SocketPartner(port)?.Port == port.Port) ((TextBlock)button.Content).Text = "";
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
        var entries = Entries(children, left, top);
        var routes = TopologyLayout.Route(bounds, entries.Select(e => e.Anchor).ToList(), entries.Select(e => e.Card).ToList(), horizontalTree);
        for (int i = 0; i < entries.Count; i++) AddWire(entries[i].Node, routes[i]);
        foreach (var child in children) Place(child, left + child.X, top + child.Y);
        AddMissingUsb3Stubs(children);
    }
    // A hub whose USB 3 side didn't connect shows the missing connection: a short dashed stub on the empty
    // USB 3 half of its socket, shorter than the drop where connections turn. Its ink follows the hub's own
    // connection: amber when the slow link holds something back, gray when nothing is slowed yet.
    private void AddMissingUsb3Stubs(List<TopologyLayout.Item> children)
    {
        foreach (var child in children.Where(c => c.Node.Usb3SideMissing && portAnchors.ContainsKey(c.Node.CompanionId)))
        {
            var at = portAnchors[child.Node.CompanionId];
            var stub = new System.Windows.Shapes.Line { X1 = at.X, Y1 = at.Y, X2 = at.X + (horizontalTree ? TopologyLayout.Stub - 3 : 0), Y2 = at.Y + (horizontalTree ? 0 : TopologyLayout.Stub - 3),
                Stroke = Brush(NodeVisuals.WireInk(child.Node)), StrokeThickness = NodeVisuals.WireWidth(new UsbNode { LinkMbps = 5000 }), StrokeDashArray = NodeVisuals.StubDashes(), IsHitTestVisible = false, Tag = MissingUsb3Tag };
            System.Windows.Automation.AutomationProperties.SetName(stub, $"{child.Node.DisplayName}'s USB 3 side isn't connected");
            Graph.Children.Add(stub);
            var hub = child.Node;
            var hit = new System.Windows.Shapes.Line { X1 = stub.X1, Y1 = stub.Y1, X2 = stub.X2, Y2 = stub.Y2, Stroke = Brushes.Transparent, StrokeThickness = HitWidth, Tag = hub, Uid = MissingUsb3HitUid, Cursor = Cursors.Hand,
                ToolTip = $"{NodeVisuals.ShortName(hub)}'s USB 3 side isn't connected.\n\n{Explanations.MissingUsb3Half(hub, FindPath(hub.Id)).What}\n\nThe short dashed line marks the missing connection: {(NodeVisuals.WireInk(hub) == "Warning" ? "amber because it slows something plugged in now" : "gray because nothing plugged in is slowed by it yet")}. Click for what to do." };
            System.Windows.Automation.AutomationProperties.SetName(hit, $"{hub.DisplayName}'s USB 3 side isn't connected");
            ExplainOnHover(hit, hub.Id);
            hit.MouseLeftButtonDown += (_, e) => { OpenExplanation(hub, Explanations.SpeedLabel(hub)); e.Handled = true; };
            Graph.Children.Add(hit);
        }
    }
    private const string MissingUsb3Tag = "missing-usb3";
    // Thin marks are hard to point at, so each has an invisible, wider twin that carries its explanation.
    // The twin is tagged with its node, so pressing on it selects instead of panning.
    private const double HitWidth = 10;
    private const string MissingUsb3HitUid = "missing-usb3-hit", WireHitUid = "wire-hit";
    private readonly Dictionary<string, System.Windows.Shapes.Path> wireHits = [];
    private void ExplainOnHover(FrameworkElement hit, string ringId)
    {
        hit.MouseEnter += (_, _) => ShowPart(ringId, true, true);
        hit.MouseLeave += (_, _) => ShowPart(ringId, false, true);
    }
    // One connection per child, or two for a merged hub, which meet the two halves of its card's entry
    // edge in the order of their ports; without sockets, the USB 2 side's comes first.
    private List<(UsbNode Node, Rect Card, Point? Anchor)> Entries(List<TopologyLayout.Item> children, double left, double top)
    {
        Point? Anchor(UsbNode n) => portAnchors.TryGetValue(n.Id, out var anchor) ? anchor : null;
        var entries = new List<(UsbNode Node, Rect Card, Point? Anchor)>();
        foreach (var child in children)
        {
            var rect = new Rect(left + child.X + child.CardX, top + child.Y + child.CardY, WidthFor(child.Node), HeightFor(child.Node));
            if (!mergedSides.TryGetValue(child.Node.Id, out var side)) { entries.Add((child.Node, rect, Anchor(child.Node))); continue; }
            var (nearHalf, farHalf) = horizontalTree
                ? (new Rect(rect.X, rect.Y, rect.Width, rect.Height / 2), new Rect(rect.X, rect.Y + rect.Height / 2, rect.Width, rect.Height / 2))
                : (new Rect(rect.X, rect.Y, rect.Width / 2, rect.Height), new Rect(rect.X + rect.Width / 2, rect.Y, rect.Width / 2, rect.Height));
            bool sideFirst = Anchor(side) is not Point a || Anchor(child.Node) is not Point b || (horizontalTree ? a.Y < b.Y : a.X < b.X);
            entries.Add(sideFirst ? (side, nearHalf, Anchor(side)) : (child.Node, nearHalf, Anchor(child.Node)));
            entries.Add(sideFirst ? (child.Node, farHalf, Anchor(child.Node)) : (side, farHalf, Anchor(side)));
        }
        return entries;
    }
    // A full card's rows under its name: the paired-hub label, the detected name under a custom label, the
    // figures with the warnings that qualify them (or a host's summary), the meter, other warnings and the
    // card that holds the other halves of its sockets.
    private void AddCardRows(UsbNode node, StackPanel panel, MetricRow figures, bool host)
    {
        var relationship = mergedSides.ContainsKey(node.Id) ? "USB 3 hub · USB 2 and USB 3 sides" : HubRelationships.CardLabel(node);
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
        if (MeterFor(node) is var (_, _, capacity, label))
        {
            var meter = NodeVisuals.Meter(MeterParts(node), capacity, label, out var segments);
            meter.Margin = new Thickness(0, 4, 0, 0); meter.ToolTip = MetricHelp(node);
            foreach (var (part, segment) in segments)
            {
                if (!meterSegments.TryGetValue(part.Id, out var list)) meterSegments[part.Id] = list = [];
                list.Add(segment);
                var (now, _) = UsbBudgets.ReservedThroughLink(part);
                segment.ToolTip = $"{part.DisplayName}: {UsbBudgets.Rate(part == node ? node.ReservedMbps ?? 0 : now)} reserved now" + (part == node ? ", the hub's own" : $", up to {UsbBudgets.Rate(Math.Max(now, UsbBudgets.PeakThroughLink(part).Mbps))} at its busiest");
                if (part != node) { segment.MouseEnter += (_, _) => ShowPart(part.Id, true, true); segment.MouseLeave += (_, _) => ShowPart(part.Id, false, true); }
            }
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
        var parent = nodeParents.GetValueOrDefault(node.Id);
        string from = parent == null ? "its host" : $"{NodeVisuals.ShortName(DrawnAs(parent))} port {node.Port:00}";
        var hit = new System.Windows.Shapes.Path { Data = wire.Data, Stroke = Brushes.Transparent, StrokeThickness = HitWidth, Tag = node, Uid = WireHitUid, Cursor = Cursors.Hand, ToolTip = Explanations.LinkHelp(node, from) };
        System.Windows.Automation.AutomationProperties.SetName(hit, $"Connection to {node.DisplayName}");
        ExplainOnHover(hit, node.Id);
        hit.MouseLeftButtonDown += (_, e) => { SelectNode(node); e.Handled = true; };
        Graph.Children.Add(hit); wireHits[node.Id] = hit;
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
        return cards.TryGetValue(DrawnAs(node).Id, out var item) ? new Rect(item.Point, new Size(item.Card.Width, item.Card.Height)) : null;
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
    private const string PulseTag = "pulse";
    // A fading ring draws the eye to a card revealed from the tree or a newly connected device.
    private void Pulse(UsbNode node, double seconds = 0.9)
    {
        if (GraphBounds(node) is not Rect area) return;
        area.Inflate(6, 6);
        var ring = new Border { Width = area.Width, Height = area.Height, CornerRadius = new CornerRadius(10), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(3), IsHitTestVisible = false, Tag = PulseTag };
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
            // Cards are neutral and the icon says what a device does; selection is an accent outline with a
            // soft glow, the only blue on a card. Either side of a merged hub selects its card; the other side
            // of a hub drawn as two cards is outlined without the glow.
            bool chosen = selected != null && id == DrawnAs(selected).Id;
            bool paired = selected is { Kind: "Hub" } && selected.CompanionHubId == id && !mergedHubs.ContainsKey(selected.Id) && !mergedSides.ContainsKey(selected.Id);
            item.Card.Background = Brush("NeutralFill");
            item.Card.BorderBrush = Brush(chosen || paired || item.Card.IsKeyboardFocusWithin || match ? "Accent" : "NeutralEdge");
            item.Card.BorderThickness = new Thickness(chosen || paired || match ? 2 : 1);
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
        if (e.Key == Key.Enter) { NextMatch(Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        if (e.Key == Key.Escape) { Search.Clear(); ApplySearch(); e.Handled = true; }
    }
    private void InspectorClick(object sender, RoutedEventArgs e)
    {
        bool hide = InspectorPanel.Visibility == Visibility.Visible;
        if (hide) inspectorWidth = InspectorColumn.ActualWidth;
        InspectorColumn.MinWidth = hide ? 0 : 260;
        InspectorColumn.Width = new GridLength(hide ? 0 : inspectorWidth);
        SplitterColumn.Width = new GridLength(hide ? 0 : 1);
        InspectorPanel.Visibility = InspectorSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        InspectorButton.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout(); FitSidePanels();
    }
    // Notes are worth knowing but affect nothing now, so they are counted apart from what needs attention.
    private (int Attention, int Notes, Severity Worst, string Summary) IssueCounts()
    {
        var worstOf = snapshot.Nodes.Select(n => Issues(n).Select(i => i.Severity).DefaultIfEmpty((Severity)(-1)).Max()).ToList();
        int notes = worstOf.Count(s => s == Severity.Note), attention = worstOf.Count(s => s > Severity.Note) + snapshot.Diagnostics.Count;
        var worst = attention > 0 ? worstOf.Append(Severity.Warning).Max() : Severity.Note;
        return (attention, notes, worst, string.Join(" · ", new[] { attention > 0 ? Count(attention, "issue") : "", notes > 0 ? Count(notes, "note") : "" }.Where(s => s.Length > 0)));
    }
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    private void UpdateIssues()
    {
        var (attention, notes, worst, summary) = IssueCounts();
        if (summary.Length == 0) { IssuesButton.Content = new TextBlock { Text = "No issues", Foreground = Brush("TextMuted") }; IssuesButton.ClearValue(BackgroundProperty); }
        else { IssuesButton.Content = NodeVisuals.StatusContent(worst, summary); IssuesButton.Background = Brush(NodeVisuals.StatusColor(worst) + "Surface"); }
        IssuesButton.IsEnabled = summary.Length > 0;
        UpdateFixFirst(force: true);
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
            item.Click += (_, _) => RevealIssue(node);
            menu.Items.Add(item);
        }
        foreach (var diagnostic in snapshot.Diagnostics) menu.Items.Add(new MenuItem { Header = diagnostic, IsEnabled = false, Icon = NodeVisuals.StatusGlyph(Severity.Warning) });
        menu.PlacementTarget = IssuesButton; menu.IsOpen = true;
    }
}

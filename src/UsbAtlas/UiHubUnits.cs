using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UsbAtlas;

public partial class MainWindow
{
    // These are drawing aliases only. The snapshot, logical paths, and Properties keep every chip.
    private readonly Dictionary<string, UsbNode> unitOwners = [];
    private readonly Dictionary<string, List<UsbNode>> unitGroups = [];
    private UsbNode HubSide(UsbNode n) => mergedHubs.GetValueOrDefault(n.Id) ?? n;
    private void PrepareUnitCards()
    {
        var hubs = snapshot.Nodes.Where(n => n.Kind == "Hub" && !mergedHubs.ContainsKey(n.Id)
            && (focusedIds == null || focusedIds.Contains(n.Id))).ToList();
        UsbNode Root(UsbNode n)
        {
            var seen = new HashSet<string>();
            while (BuiltIn(n) && nodeParents.TryGetValue(n.Id, out var parent) && parent.Kind == "Hub" && seen.Add(n.Id))
            {
                var above = HubSide(parent);
                if (!hubs.Contains(above)) break;
                // Do not combine a paired chip whose sides attach to different physical parents.
                if (mergedSides.TryGetValue(n.Id, out var side)
                    && (!nodeParents.TryGetValue(side.Id, out var other) || HubSide(other) != above)) break;
                n = above;
            }
            return n;
        }
        foreach (var group in hubs.GroupBy(Root).Where(g => g.Count() > 1))
        {
            var root = group.Key;
            var chips = group.OrderBy(c => FindPath(c.Id).Count).ThenBy(c => pathLabels[c.Id], StringComparer.Ordinal).ToList();
            unitGroups[root.Id] = chips;
            foreach (var chip in chips.Where(c => c != root))
                foreach (var side in Sides(chip)) unitOwners[side.Id] = root;
        }
        edgePortCache.Clear(); socketParts.Clear(); tagRooms.Clear(); nameWidths.Clear();
    }
    private IEnumerable<UsbNode> UnitSides(UsbNode n) =>
        unitGroups.TryGetValue(n.Id, out var chips) ? chips.SelectMany(Sides) : Sides(n);
    private int UnitPortGroup(UsbNode unit, UsbNode port) =>
        unitGroups.TryGetValue(unit.Id, out var chips) && nodeParents.TryGetValue(port.Id, out var parent)
            ? Math.Max(0, chips.IndexOf(HubSide(parent))) : 0;
    private static bool EmptyInternalPort(UsbNode n) => n.Kind == "Empty port" && BuiltIn(n);
    private int UnitSocketCount(UsbNode n) => UnitSides(n).SelectMany(s => s.Children)
        .Where(p => !unitOwners.ContainsKey(p.Id) && !EmptyInternalPort(p))
        .GroupBy(p => SocketPartner(p) is UsbNode other ? string.CompareOrdinal(p.Id, other.Id) < 0 ? p.Id : other.Id : p.Id).Count();
    private string UnitName(UsbNode n)
    {
        if (!unitGroups.TryGetValue(n.Id, out var chips)) return NodeVisuals.ShortName(n);
        return chips.FirstOrDefault(c => c.UserLabel.Length > 0)?.UserLabel
            ?? chips.FirstOrDefault(c => c.ContainerName.Length > 0 && !c.ContainerIdShared)?.ContainerName
            ?? NodeVisuals.ShortName(n);
    }
    private List<(UsbNode Node, int Chip, Severity Severity, string Text)> UnitWarnings(UsbNode n) =>
        !unitGroups.TryGetValue(n.Id, out var chips) ? [] : chips.Skip(1).SelectMany((chip, i) =>
            Sides(chip).SelectMany(side => Issues(side).Select(issue => (side, i + 2, issue.Severity, issue.Text)))).ToList();
    private double UnitWarningHeight(UsbNode n, string text) => 24 * BadgeRows(n, [(Severity.Note, text)], 50);
    private void AddUnitWarnings(UsbNode n, StackPanel panel)
    {
        foreach (var warning in UnitWarnings(n))
        {
            var row = new WrapPanel { Height = UnitWarningHeight(n, warning.Text) };
            row.Children.Add(new TextBlock { Text = $"Chip {warning.Chip}: ", VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12, Foreground = Brush("TextSecondary") });
            row.Children.Add(WarningBadge(warning.Node, warning.Severity, warning.Text));
            panel.Children.Add(row);
        }
    }
    private void AddUnitDividers(UsbNode n, List<UsbNode> ports, Rect bounds)
    {
        if (!unitGroups.ContainsKey(n.Id)) return;
        for (int i = 1; i < ports.Count; i++)
        {
            if (UnitPortGroup(n, ports[i - 1]) == UnitPortGroup(n, ports[i])) continue;
            double cross = ((PortOffset(n, ports[i - 1]) ?? 0) + (PortOffset(n, ports[i]) ?? 0)) / 2;
            var divider = new System.Windows.Shapes.Line
            {
                X1 = horizontalTree ? bounds.Right - SocketWidth : bounds.X + cross,
                X2 = horizontalTree ? bounds.Right : bounds.X + cross,
                Y1 = horizontalTree ? bounds.Y + cross : bounds.Bottom - SocketHeight,
                Y2 = horizontalTree ? bounds.Y + cross : bounds.Bottom,
                Stroke = Brush("TextMuted"), StrokeThickness = 1, Tag = "unit-divider",
                ToolTip = $"Ports above/before this divider belong to chip {UnitPortGroup(n, ports[i - 1]) + 1}; ports below/after it belong to chip {UnitPortGroup(n, ports[i]) + 1} inside this hub."
            };
            Panel.SetZIndex(divider, 2); Graph.Children.Add(divider);
        }
    }
    private const string UnitChainTag = "unit-chain", UnitChipTag = "unit-chip";
    private UsbNode? UnitParent(UsbNode chip) =>
        nodeParents.TryGetValue(chip.Id, out var parent) && parent.Kind == "Hub" ? HubSide(parent) : null;
    private int ChipPortCount(UsbNode chip) => Sides(chip).SelectMany(s => s.Children)
        .Where(p => !unitOwners.ContainsKey(p.Id) && !EmptyInternalPort(p))
        .GroupBy(p => SocketPartner(p) is UsbNode other && string.CompareOrdinal(other.Id, p.Id) < 0 ? other.Id : p.Id).Count();
    // Chips sit beside their own sockets rather than in a disconnected diagram/key.
    private readonly Dictionary<string, List<Action<bool>>> unitEmphasis = [];
    private string? hoveredUnitChip;
    private UsbNode? UnitChipFor(UsbNode? node)
    {
        if (node == null) return null;
        var chip = node.Kind == "Hub" ? HubSide(node) : nodeParents.TryGetValue(node.Id, out var parent) ? HubSide(parent) : null;
        return chip != null && unitGroups.Values.Any(chips => chips.Contains(chip)) ? chip : null;
    }
    private void UpdateUnitEmphasis()
    {
        string? chosen = hoveredUnitChip ?? UnitChipFor(selected)?.Id;
        foreach (var (id, actions) in unitEmphasis) foreach (var apply in actions) apply(id == chosen);
    }
    private void HoverUnitChip(UsbNode chip, bool on)
    {
        if (on) hoveredUnitChip = chip.Id;
        else if (hoveredUnitChip == chip.Id) hoveredUnitChip = null;
        UpdateUnitEmphasis();
    }
    private void AddUnitPortLabels(UsbNode n, List<UsbNode> ports, Rect bounds)
    {
        if (!unitGroups.TryGetValue(n.Id, out var chips) || !DrawsSockets) return;
        bool horizontal = horizontalTree;
        double boxWidth = detail == CardDetail.Full ? 58 : 46, boxHeight = detail == CardDetail.Full ? 32 : 23;
        int Depth(UsbNode chip) => chip == n || UnitParent(chip) is not UsbNode parent ? 0 : Depth(parent) + 1;
        int maxDepth = chips.Max(Depth);
        var canvas = new Canvas { Width = bounds.Width, Height = bounds.Height, Tag = UnitChainTag,
            ToolTip = "Inside one hub: each chip is aligned with the bracket around its own sockets. The arrows between chips show their internal wiring. Point at a chip or port to highlight its whole group." };
        var boxes = new Dictionary<string, Rect>();
        var groups = ports.GroupBy(p => UnitPortGroup(n, p)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (chip, index) in chips.Select((c, i) => (c, i)))
        {
            var own = groups.GetValueOrDefault(index) ?? [];
            double mid = own.Count > 0 ? own.Average(p => PortOffset(n, p) ?? 0)
                : (horizontal ? bounds.Height : bounds.Width) * (index + .5) / chips.Count;
            double low = own.Count > 0 ? own.Min(p => PortOffset(n, p) ?? 0) - (horizontal ? SocketHeight : SocketWidth) / 2 - 3 : mid - 18;
            double high = own.Count > 0 ? own.Max(p => PortOffset(n, p) ?? 0) + (horizontal ? SocketHeight : SocketWidth) / 2 + 3 : mid + 18;
            double inset = horizontal ? bounds.Width - SocketWidth - TagRoom(n) - 106 : bounds.Height - SocketHeight - TagRoom(n) - 49;
            var rect = horizontal
                ? new Rect(inset + Depth(chip) * 5, mid - boxHeight / 2, boxWidth, boxHeight)
                : new Rect(mid - boxWidth / 2, inset - Depth(chip) * 5, boxWidth, boxHeight);
            boxes[chip.Id] = rect;
            var area = horizontal
                ? new Rect(inset - 5, Math.Max(3, low), bounds.Width - inset + 2, Math.Min(bounds.Height - 3, high) - Math.Max(3, low))
                : new Rect(Math.Max(3, low), inset - maxDepth * 5 - 3, Math.Min(bounds.Width - 3, high) - Math.Max(3, low), bounds.Height - inset + maxDepth * 5);
            string help = $"Chip {index + 1} · {ChipPortCount(chip)} ports inside {UnitName(n)}. " +
                (chip == n ? "Receives the upstream cable." : $"Wired to chip {chips.IndexOf(UnitParent(chip)!) + 1}, port {chip.Port:00}.") + " Click to inspect this chip.";
            var band = new Border { Width = area.Width, Height = area.Height, CornerRadius = new CornerRadius(6),
                Background = Brush("Surface"), Opacity = .28, Tag = "unit-chip-group", Uid = chip.Id, ToolTip = help };
            band.MouseEnter += (_, _) => HoverUnitChip(chip, true);
            band.MouseLeave += (_, _) => HoverUnitChip(chip, false);
            band.MouseLeftButtonDown += (_, e) => { SelectNode(chip); e.Handled = true; };
            Canvas.SetLeft(band, area.X); Canvas.SetTop(band, area.Y); canvas.Children.Add(band);
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = $"Chip {index + 1}", FontSize = 11, Foreground = Brush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center });
            if (detail == CardDetail.Full) label.Children.Add(new TextBlock { Text = $"{ChipPortCount(chip)} ports", FontSize = 10, Foreground = Brush("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = label, Width = rect.Width, Height = rect.Height, Padding = new Thickness(0),
                Background = Brush("Surface"), BorderBrush = Brush("UnitEdge"), Tag = UnitChipTag, Uid = chip.Id, ToolTip = help };
            button.Click += (_, e) => { SelectNode(chip); e.Handled = true; };
            button.MouseEnter += (_, _) => HoverUnitChip(chip, true);
            button.MouseLeave += (_, _) => HoverUnitChip(chip, false);
            Canvas.SetLeft(button, rect.X); Canvas.SetTop(button, rect.Y); Panel.SetZIndex(button, 2); canvas.Children.Add(button);
            // A short leader joins the chip to a bracket spanning precisely its own sockets.
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                if (horizontal)
                {
                    double x = bounds.Width - SocketWidth - TagRoom(n) - 7;
                    context.BeginFigure(new Point(rect.Right, mid), false, false); context.LineTo(new Point(x, mid), true, false);
                    context.BeginFigure(new Point(x + 4, low), false, false);
                    context.PolyLineTo([new Point(x, low), new Point(x, high), new Point(x + 4, high)], true, false);
                }
                else
                {
                    double y = bounds.Height - SocketHeight - TagRoom(n) - 7;
                    context.BeginFigure(new Point(mid, rect.Bottom), false, false); context.LineTo(new Point(mid, y), true, false);
                    context.BeginFigure(new Point(low, y + 4), false, false);
                    context.PolyLineTo([new Point(low, y), new Point(high, y), new Point(high, y + 4)], true, false);
                }
            }
            var bracket = new System.Windows.Shapes.Path { Data = geometry, Stroke = Brush("UnitEdge"), StrokeThickness = 1, IsHitTestVisible = false, Tag = "unit-port-bracket", Uid = chip.Id };
            canvas.Children.Add(bracket);
            unitEmphasis[chip.Id] = [on => { button.BorderBrush = bracket.Stroke = Brush(on ? "Accent" : "UnitEdge");
                bracket.StrokeThickness = on ? 1.8 : 1; band.Opacity = on ? .7 : .28; }];
        }
        foreach (var chip in chips.Where(c => c != n))
        {
            var parent = UnitParent(chip)!; var from = boxes[parent.Id]; var to = boxes[chip.Id];
            var geometry = new StreamGeometry();
            Point end;
            bool adjacent = chips.IndexOf(chip) == chips.IndexOf(parent) + 1;
            bool arrowDown = horizontal ? adjacent : !adjacent;
            using (var context = geometry.Open())
            {
                if (horizontal && adjacent)
                {
                    var start = new Point(from.Left + from.Width / 2, from.Bottom);
                    end = new Point(to.Left + to.Width / 2, to.Top);
                    double track = (start.Y + end.Y) / 2;
                    context.BeginFigure(start, false, false);
                    context.PolyLineTo([new Point(start.X, track), new Point(end.X, track), new Point(end.X, end.Y - 3)], true, false);
                }
                else if (!horizontal && adjacent)
                {
                    var start = new Point(from.Right, from.Top + from.Height / 2);
                    end = new Point(to.Left, to.Top + to.Height / 2);
                    double track = (start.X + end.X) / 2;
                    context.BeginFigure(start, false, false);
                    context.PolyLineTo([new Point(track, start.Y), new Point(track, end.Y), new Point(end.X - 3, end.Y)], true, false);
                }
                else if (horizontal)
                {
                    double track = boxes.Values.Min(r => r.Left) - 10 - Depth(parent) * 5;
                    var start = new Point(from.Left, from.Top + from.Height / 2); end = new Point(to.Left, to.Top + to.Height / 2);
                    context.BeginFigure(start, false, false);
                    context.PolyLineTo([new Point(track, start.Y), new Point(track, end.Y), new Point(end.X - 3, end.Y)], true, false);
                }
                else
                {
                    double track = boxes.Values.Min(r => r.Top) - 8 - Depth(parent) * 5;
                    var start = new Point(from.Left + from.Width / 2, from.Top); end = new Point(to.Left + to.Width / 2, to.Top);
                    context.BeginFigure(start, false, false);
                    context.PolyLineTo([new Point(start.X, track), new Point(end.X, track), new Point(end.X, end.Y - 3)], true, false);
                }
            }
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = Brush("TextMuted"), StrokeThickness = 1,
                IsHitTestVisible = false, Tag = "unit-chip-link", Uid = chip.Id });
            canvas.Children.Add(new System.Windows.Shapes.Polygon { Points = arrowDown
                ? [new Point(end.X - 3, end.Y - 4), end, new Point(end.X + 3, end.Y - 4)]
                : [new Point(end.X - 4, end.Y - 3), end, new Point(end.X - 4, end.Y + 3)], Fill = Brush("TextMuted"), IsHitTestVisible = false });
        }
        Canvas.SetLeft(canvas, bounds.X); Canvas.SetTop(canvas, bounds.Y); Panel.SetZIndex(canvas, 1); Graph.Children.Add(canvas);
    }
    private void AddUnitDetails(UsbNode node)
    {
        var owner = DrawnAs(node);
        if (!unitGroups.TryGetValue(owner.Id, out var chips)) return;
        var content = new StackPanel();
        foreach (var (chip, index) in chips.Select((chip, i) => (chip, i + 1)))
            foreach (var side in Sides(chip))
            {
                string use = UsbBudgets.LinkUse(side) is (var used, var capacity, _)
                    ? $" · {UsbBudgets.Rate(used)} / {UsbBudgets.Rate(capacity)} reserved" : "";
                var button = new Button { Content = $"Chip {index} · {(side.LinkMbps is <= 480 ? "USB 2" : "USB 3")} · {ShortSpeed(side)}",
                    Tag = side, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0),
                    ToolTip = pathLabels.GetValueOrDefault(side.Id) + use + "\n" + string.Join(" · ", Issues(side).Select(i => i.Text)) };
                button.Click += (_, _) => SelectNode(side);
                content.Children.Add(button);
                var meter = MeterFor(side);
                if (meter is var (_, _, capacity2, verdict, detail2))
                    content.Children.Add(NodeVisuals.Meter(MeterParts(side), capacity2, verdict, detail2, out _));
            }
        Details.Children.Add(new Expander { Header = $"Hub unit · {chips.Count} chips · {UnitSocketCount(owner)} ports",
            Tag = "unit-details", Content = content, Margin = new Thickness(0, 10, 0, 0), Foreground = Brush("TextSecondary") });
    }
}

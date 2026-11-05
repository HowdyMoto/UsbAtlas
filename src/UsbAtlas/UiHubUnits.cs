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

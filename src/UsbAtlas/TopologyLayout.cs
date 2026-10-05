using System.Windows;

namespace UsbAtlas;

// Lays out each hub's children in port order without wrapping: as one row under the
// hub, or for a group of end devices, as a staircase beside the hub's ports. Routes
// are planar by construction, so connections never cross each other or pass cards.
// Layout and routing work in the vertical frame; horizontal trees are transposed.
internal static partial class TopologyLayout
{
    internal const double CardWidth = 260, CardHeight = 58, Gap = 16, LevelGap = 28;
    // Fan geometry: drop below a port, spacing between turning lanes, clearance above the child row.
    internal const double Stub = 12, LaneSpacing = 8, Clearance = 14, StackClearance = 20;

    // X/Y place the subtree within its parent's subtree; CardX/CardY place the node's card within its own.
    internal sealed record Item(UsbNode Node, double X, double Y, double Width, double Height, double CardX, double CardY, List<Item> Children, bool Stacked) { internal List<Item>? SnappedStages { get; init; } internal bool SnappedColumn { get; init; } }

    internal static Item Measure(UsbNode node, Func<UsbNode, List<UsbNode>> children, bool horizontal, Func<UsbNode, double> width, Func<UsbNode, double> height,
        Func<UsbNode, UsbNode, double?> portOffset, IReadOnlySet<string> stacked, Func<UsbNode, List<UsbNode>>? groups = null, double gap = Gap)
    {
        var tree = horizontal
            ? MeasureCore(node, children, height, width, portOffset, new HashSet<string>(), null, gap)
            : MeasureCore(node, children, width, height, portOffset, stacked, groups, gap);
        return horizontal ? Transpose(tree) : tree;
    }

    // Only groups of end devices stack: a staircase has no room for grandchildren.
    internal static bool CanStack(UsbNode node, Func<UsbNode, List<UsbNode>> children, Func<UsbNode, UsbNode, double?> portOffset)
    {
        var kids = children(node);
        return kids.Count >= 2 && kids.All(k => children(k).Count == 0 && portOffset(node, k) != null);
    }

    private static Item Transpose(Item item) =>
        new(item.Node, item.Y, item.X, item.Height, item.Width, item.CardY, item.CardX, item.Children.Select(Transpose).ToList(), item.Stacked);

    private static Item MeasureCore(UsbNode node, Func<UsbNode, List<UsbNode>> children, Func<UsbNode, double> cross, Func<UsbNode, double> along,
        Func<UsbNode, UsbNode, double?> portOffset, IReadOnlySet<string> stacked, Func<UsbNode, List<UsbNode>>? groups = null, double gap = Gap)
    {
        if (groups?.Invoke(node) is { Count: > 1 } members) return MeasureSnapped(members, children, cross, along, portOffset, stacked, groups);
        double width = cross(node), height = along(node);
        var kids = children(node);
        if (kids.Count == 0) return new(node, 0, 0, width, height, 0, 0, [], false);
        var items = kids.Select(k => MeasureCore(k, children, cross, along, portOffset, stacked, groups, gap)).ToList();
        double Port(int i) => portOffset(node, kids[i]) ?? width * (i + 0.5) / kids.Count;
        if (stacked.Contains(node.Id) && CanStack(node, children, portOffset))
        {
            // The column sits left of the first port, so each wire drops and turns once.
            double column = items.Max(i => i.Width);
            double cardX = Math.Max(0, column + StackClearance - Port(0)), y = height + LevelGap;
            var placed = new List<Item>();
            foreach (var item in items) { placed.Add(item with { X = 0, Y = y }); y += item.Height + gap; }
            return new(node, 0, 0, Math.Max(column, cardX + width), y - gap, cardX, 0, placed, true);
        }
        var xs = new List<double>();
        double x = 0;
        foreach (var item in items) { xs.Add(x); x += item.Width + gap; }
        double Center(int i) => xs[i] + items[i].CardX + cross(kids[i]) / 2;
        // A single child hangs straight below its port; a row is centered under the card.
        double card = items.Count == 1 ? Center(0) - Port(0) : (Center(0) + Center(items.Count - 1)) / 2 - width / 2;
        double shift = Math.Max(0, -card);
        card += shift;
        int lanes = LaneCount(Enumerable.Range(0, items.Count).Select(i => (card + Port(i), Center(i) + shift)));
        double drop = lanes == 0 ? LevelGap : Math.Max(LevelGap, Stub + (lanes - 1) * LaneSpacing + Clearance);
        var row = items.Select((item, i) => item with { X = xs[i] + shift, Y = height + drop }).ToList();
        return new(node, 0, 0, Math.Max(card + width, shift + x - gap), height + drop + items.Max(i => i.Height), card, 0, row, false);
    }

    private static int Direction(double port, double child) => Math.Abs(child - port) < 1 ? 0 : Math.Sign(child - port);

    // Wires heading left and right turn in separate, mirrored lane sets, so both can start at the first lane.
    private static int LaneCount(IEnumerable<(double Port, double Child)> wires)
    {
        var directions = wires.Select(w => Direction(w.Port, w.Child)).ToList();
        return Math.Max(directions.Count(d => d < 0), directions.Count(d => d > 0));
    }

    // Returns one orthogonal polyline per child, from its port (or the parent's edge) to its card.
    internal static List<List<Point>> Route(Rect parent, IReadOnlyList<Point?> ports, IReadOnlyList<Rect> children, bool stacked, bool horizontal)
    {
        static Point Flip(Point p) => new(p.Y, p.X);
        static Rect FlipRect(Rect r) => new(r.Y, r.X, r.Height, r.Width);
        if (horizontal) { parent = FlipRect(parent); children = children.Select(FlipRect).ToList(); ports = ports.Select(p => p is Point q ? Flip(q) : (Point?)null).ToList(); }
        // Without port graphics (a controller's root hubs), connections leave evenly spaced points on the edge.
        var starts = ports.Select((p, i) => p ?? new Point(parent.X + parent.Width * (i + 0.5) / ports.Count, parent.Bottom)).ToList();
        var routes = new List<List<Point>>();
        if (stacked)
        {
            foreach (var (start, child) in starts.Zip(children))
            {
                double y = child.Y + child.Height / 2;
                routes.Add([start, new(start.X, y), new(child.Right, y)]);
            }
        }
        else
        {
            // Outermost wires turn first: lanes count inward from each end of the row.
            var lanes = new int[children.Count];
            int left = 0, right = 0;
            for (int i = 0; i < children.Count; i++) if (Direction(starts[i].X, children[i].X + children[i].Width / 2) < 0) lanes[i] = left++;
            for (int i = children.Count - 1; i >= 0; i--) if (Direction(starts[i].X, children[i].X + children[i].Width / 2) > 0) lanes[i] = right++;
            for (int i = 0; i < children.Count; i++)
            {
                var start = starts[i];
                double center = children[i].X + children[i].Width / 2, top = children[i].Y;
                if (Direction(start.X, center) == 0) { routes.Add([start, new(start.X, top)]); continue; }
                double lane = parent.Bottom + Stub + lanes[i] * LaneSpacing;
                routes.Add([start, new(start.X, lane), new(center, lane), new(center, top)]);
            }
        }
        return horizontal ? routes.Select(r => r.Select(Flip).ToList()).ToList() : routes;
    }
}

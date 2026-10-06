using System.Windows;

namespace UsbAtlas;

// Lays out each hub's children in port order as one row under the hub, never wrapped, so a
// position means the same thing everywhere, except that a packed group of end devices fills a
// second row. Routes are planar by construction, so connections never cross each other or pass cards. Layout and routing work in the vertical frame; horizontal
// trees are transposed.
internal static partial class TopologyLayout
{
    internal const double CardWidth = 260, CardHeight = 58, Gap = 16, LevelGap = 28;
    // Fan geometry: drop below a port, spacing between turning lanes, clearance above the child row.
    internal const double Stub = 12, LaneSpacing = 8, Clearance = 14;
    // A packed group's siblings leave room for the widest connection to pass between them, and its
    // second row sits this far beyond the first.
    internal const double PackGap = 10, PackRowGap = 12;

    // X/Y place the subtree within its parent's subtree; CardX/CardY place the node's card within its own.
    // A packed node's children alternate between two rows (see MeasureCore).
    internal sealed record Item(UsbNode Node, double X, double Y, double Width, double Height, double CardX, double CardY, List<Item> Children, bool Packed) { internal List<Item>? SnappedStages { get; init; } }

    internal static Item Measure(UsbNode node, Func<UsbNode, List<UsbNode>> children, bool horizontal, Func<UsbNode, double> width, Func<UsbNode, double> height,
        Func<UsbNode, UsbNode, double?> portOffset, Func<UsbNode, List<UsbNode>>? groups = null, double gap = Gap, bool pack = false, Func<UsbNode, UsbNode?>? twin = null)
    {
        var tree = horizontal
            ? MeasureCore(node, children, height, width, portOffset, null, gap, pack, twin)
            : MeasureCore(node, children, width, height, portOffset, groups, gap, pack, twin);
        return horizontal ? Transpose(tree) : tree;
    }

    // Packing takes a group of four or more end devices: rows of grandchildren wouldn't fit between them,
    // and a folded hub is still a hub.
    internal static bool CanPack(UsbNode node, Func<UsbNode, List<UsbNode>> children)
    {
        var kids = children(node);
        return kids.Count >= 4 && kids.All(k => k.Kind != "Hub" && children(k).Count == 0);
    }

    private static Item Transpose(Item item) =>
        new(item.Node, item.Y, item.X, item.Height, item.Width, item.CardY, item.CardX, item.Children.Select(Transpose).ToList(), item.Packed);

    private static Item MeasureCore(UsbNode node, Func<UsbNode, List<UsbNode>> children, Func<UsbNode, double> cross, Func<UsbNode, double> along,
        Func<UsbNode, UsbNode, double?> portOffset, Func<UsbNode, List<UsbNode>>? groups, double gap, bool pack, Func<UsbNode, UsbNode?>? twin = null)
    {
        if (groups?.Invoke(node) is { Count: > 1 } members) return MeasureSnapped(members, children, cross, along, portOffset, groups, twin);
        double width = cross(node), height = along(node);
        var kids = children(node);
        if (kids.Count == 0) return new(node, 0, 0, width, height, 0, 0, [], false);
        var items = kids.Select(k => MeasureCore(k, children, cross, along, portOffset, groups, gap, pack, twin)).ToList();
        // A merged hub's two sides each have a port, and its two connections meet the two halves of its
        // card's entry edge in the order of those ports. Without port graphics, connections leave evenly
        // spaced points, the USB 2 side's (its twin's) just before the card's own.
        var twins = kids.Select(k => twin?.Invoke(k)).ToList();
        int entries = kids.Count + twins.Count(t => t != null);
        int Slot(int i) => i + twins.Take(i).Count(t => t != null);
        double Port(int i) => portOffset(node, kids[i]) ?? width * (Slot(i) + (twins[i] != null ? 1 : 0) + 0.5) / entries;
        double TwinPort(int i) => portOffset(node, twins[i]!) ?? width * (Slot(i) + 0.5) / entries;
        // A packed group fills two rows in socket order, alternating: the first, third and later devices
        // make the first row, and each other one sits beyond it, centered on the gap after its neighbor,
        // so its connection passes straight between two cards of the first row. Connections still leave
        // the hub in socket order and reach their rows in that order, so they never cross.
        bool packed = pack && CanPack(node, children);
        double siblingGap = packed ? PackGap : gap;
        var xs = new double[items.Count];
        double x = 0, firstRow = packed ? items.Where((_, i) => i % 2 == 0).Max(i => i.Height) : 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (packed && i % 2 == 1) { xs[i] = xs[i - 1] + items[i - 1].Width + siblingGap / 2 - items[i].Width / 2; continue; }
            xs[i] = x; x += items[i].Width + siblingGap;
        }
        double Center(int i) => xs[i] + items[i].CardX + cross(kids[i]) / 2;
        // A single child hangs straight below its port; a row is centered under the card.
        double card = items.Count == 1 ? Center(0) - Port(0) : (Center(0) + Center(items.Count - 1)) / 2 - width / 2;
        double shift = Math.Max(0, -Math.Min(card, xs.Min()));
        card += shift;
        int lanes = LaneCount(Enumerable.Range(0, items.Count).SelectMany(i =>
        {
            if (twins[i] == null) return new[] { (card + Port(i), Center(i) + shift) };
            double quarter = cross(kids[i]) / 4; var ports = new[] { Port(i), TwinPort(i) }.Order().ToArray();
            return [(card + ports[0], Center(i) + shift - quarter), (card + ports[1], Center(i) + shift + quarter)];
        }));
        double drop = lanes == 0 ? LevelGap : Math.Max(LevelGap, Stub + (lanes - 1) * LaneSpacing + Clearance);
        var row = items.Select((item, i) => item with { X = xs[i] + shift, Y = height + drop + (packed && i % 2 == 1 ? firstRow + PackRowGap : 0) }).ToList();
        double right = row.Max(r => r.X + r.Width), bottom = row.Max(r => r.Y + r.Height);
        return new(node, 0, 0, Math.Max(card + width, right), bottom, card, 0, row, packed);
    }

    private static int Direction(double port, double child) => Math.Abs(child - port) < 1 ? 0 : Math.Sign(child - port);

    // Wires heading left and right turn in separate, mirrored lane sets, so both can start at the first lane.
    private static int LaneCount(IEnumerable<(double Port, double Child)> wires)
    {
        var directions = wires.Select(w => Direction(w.Port, w.Child)).ToList();
        return Math.Max(directions.Count(d => d < 0), directions.Count(d => d > 0));
    }

    // Returns one orthogonal polyline per child, from its port (or the parent's edge) to its card, with turning
    // lanes the given distance apart.
    internal static List<List<Point>> Route(Rect parent, IReadOnlyList<Point?> ports, IReadOnlyList<Rect> children, bool horizontal, double spacing = LaneSpacing)
    {
        static Point Flip(Point p) => new(p.Y, p.X);
        static Rect FlipRect(Rect r) => new(r.Y, r.X, r.Height, r.Width);
        if (horizontal) { parent = FlipRect(parent); children = children.Select(FlipRect).ToList(); ports = ports.Select(p => p is Point q ? Flip(q) : (Point?)null).ToList(); }
        // Without port graphics (a controller's root hubs), connections leave evenly spaced points on the edge.
        var starts = ports.Select((p, i) => p ?? new Point(parent.X + parent.Width * (i + 0.5) / ports.Count, parent.Bottom)).ToList();
        var routes = new List<List<Point>>();
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
            double lane = parent.Bottom + Stub + lanes[i] * spacing;
            routes.Add([start, new(start.X, lane), new(center, lane), new(center, top)]);
        }
        return horizontal ? routes.Select(r => r.Select(Flip).ToList()).ToList() : routes;
    }
}

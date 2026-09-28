namespace UsbAtlas;

// Packs complete subtrees into rows. Each row owns its height, so deeper branches
// cannot collide with the next row. A left gutter carries wrapped connections.
internal sealed class TopologyLayout
{
    internal const double CardWidth = 290, CardHeight = 110, Gap = 16, Gutter = 16, LevelGap = 28;
    internal sealed record Item(UsbNode Node, double X, double Y, double Width, double Height, List<Item> Children);
    private static double MinimumWidth(UsbNode node, Func<UsbNode, List<UsbNode>> children, Func<UsbNode, double> crossSize)
    {
        double cardWidth = crossSize(node);
        var items = children(node);
        return items.Count == 0 ? cardWidth : Math.Max(cardWidth, items.Max(n => MinimumWidth(n, children, crossSize)) + (items.Count > 1 ? Gutter : 0));
    }
    internal static Item Measure(UsbNode node, double available, Func<UsbNode, List<UsbNode>> children, bool horizontal = false, Func<UsbNode, double>? height = null)
    {
        height ??= _ => CardHeight;
        Func<UsbNode, double> width = _ => CardWidth;
        var tree = MeasureCore(node, available, children, horizontal ? height : width, horizontal ? width : height);
        return horizontal ? Transpose(tree) : tree;
    }
    private static Item Transpose(Item item) => new(item.Node, item.Y, item.X, item.Height, item.Width, item.Children.Select(Transpose).ToList());
    private static Item MeasureCore(UsbNode node, double available, Func<UsbNode, List<UsbNode>> children, Func<UsbNode, double> crossSize, Func<UsbNode, double> alongSize)
    {
        double cardWidth = crossSize(node), cardHeight = alongSize(node);
        var descendants = children(node);
        if (descendants.Count == 0) return new(node, 0, 0, cardWidth, cardHeight, []);
        if (descendants.Count == 1)
        {
            var child = MeasureCore(descendants[0], available, children, crossSize, alongSize);
            return new(node, 0, 0, Math.Max(cardWidth, child.Width), cardHeight + LevelGap + child.Height,
                [child with { X = Math.Max(0, (cardWidth - child.Width) / 2), Y = cardHeight + LevelGap }]);
        }
        double minimumColumn = descendants.Max(n => MinimumWidth(n, children, crossSize));
        int columns = Math.Min(descendants.Count, Math.Max(1, (int)((available - Gutter + Gap) / (minimumColumn + Gap))));
        double columnWidth = Math.Max(cardWidth, (available - Gutter - (columns - 1) * Gap) / columns);
        var items = descendants.Select(n => MeasureCore(n, columnWidth, children, crossSize, alongSize)).ToList();
        var placed = new List<Item>();
        double y = cardHeight + LevelGap, width = cardWidth;
        for (int start = 0; start < items.Count; start += columns)
        {
            var row = items.Skip(start).Take(columns).ToList();
            double x = Gutter;
            foreach (var item in row) { placed.Add(item with { X = x, Y = y }); x += item.Width + Gap; }
            width = Math.Max(width, x - Gap);
            y += row.Max(n => n.Height) + LevelGap;
        }
        return new(node, 0, 0, width, y - LevelGap, placed);
    }
}

using System.Windows;

namespace UsbAtlas;
internal static partial class TopologyLayout
{
    private static Item MeasureSnapped(List<UsbNode> members, Func<UsbNode, List<UsbNode>> children,
        Func<UsbNode, double> width, Func<UsbNode, double> height, Func<UsbNode, UsbNode, double?> portOffset, IReadOnlySet<string> stacked, Func<UsbNode, List<UsbNode>>? groups)
    {
        var ids = members.Select(n => n.Id).ToHashSet();
        List<UsbNode> ExternalChildren(UsbNode node) => children(node).Where(n => !ids.Contains(n.Id)).ToList();
        var stages = new List<Item>(); double x = 0, maxHeight = members.Max(height);
        foreach (var member in members)
        {
            var childItems = ExternalChildren(member).Select(n => MeasureCore(n, children, width, height, portOffset, stacked, groups)).ToList();
            double column = Math.Max(width(member), childItems.Select(c => c.Width).DefaultIfEmpty(0).Max());
            double y = maxHeight + 52 + childItems.Count * 8;
            var placed = new List<Item>();
            foreach (var child in childItems) { placed.Add(child with { X = 0, Y = y }); y += child.Height + Gap; }
            var stage = new Item(member, x, 28, column + 16 + childItems.Count * 8, Math.Max(maxHeight + 32, y), 0, 0, placed, false) { SnappedColumn = true };
            stages.Add(stage); x += stage.Width + 32;
        }
        return new Item(members[0], 0, 0, x - 32, stages.Max(s => s.Height) + 28, stages[0].CardX, 28, [], false) { SnappedStages = stages };
    }
}

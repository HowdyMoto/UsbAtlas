using System.Windows;

namespace UsbAtlas;
internal static partial class TopologyLayout
{
    // Linked hub stages sit close together in a frame, as the chips of one enclosure do, with only room between
    // them for the link that feeds the next stage. Their devices share one row beneath the frame, grouped by
    // stage and in socket order within each, which is the order of their sockets along the frame, so each device
    // has its own connection, routed as any hub's are, and none cross. The frame reaches FrameBelow under the
    // tallest stage, and connections turn only below it, clear of the links between stages.
    internal const double FrameBelow = 28;
    // Long runs side by side, as a shared row's are, sit far enough apart that even the widest links read as
    // separate cables; each stage's devices are set apart from the next stage's.
    internal const double RowLaneSpacing = 16;
    private const double GroupGap = Gap * 2;
    // Room between stages for the short link that feeds the next one.
    private const double StageGap = 32;

    private static Item MeasureSnapped(List<UsbNode> members, Func<UsbNode, List<UsbNode>> children,
        Func<UsbNode, double> width, Func<UsbNode, double> height, Func<UsbNode, UsbNode, double?> portOffset, Func<UsbNode, List<UsbNode>>? groups, Func<UsbNode, UsbNode?>? twin)
    {
        var ids = members.Select(n => n.Id).ToHashSet();
        double maxHeight = members.Max(height), x = 0;
        var stageX = new List<double>();
        foreach (var member in members) { stageX.Add(x); x += width(member) + StageGap; }
        double block = x - StageGap;
        var kids = members.SelectMany((m, s) => children(m).Where(k => !ids.Contains(k.Id)).Select(k => (Stage: s, Node: k))).ToList();
        var items = kids.Select(k => MeasureCore(k.Node, children, width, height, portOffset, groups, Gap, false, twin)).ToList();
        var xs = new double[items.Count];
        double rowX = 0;
        for (int i = 0; i < items.Count; i++) { if (i > 0 && kids[i].Stage != kids[i - 1].Stage) rowX += GroupGap - Gap; xs[i] = rowX; rowX += items[i].Width + Gap; }
        double Center(int i) => xs[i] + items[i].CardX + width(kids[i].Node) / 2;
        // The stages are centered over the row, as a hub's card is over its own.
        double hubs = items.Count == 0 ? 0 : (Center(0) + Center(items.Count - 1)) / 2 - block / 2;
        double shift = Math.Max(0, -hubs); hubs += shift;
        // A merged hub's two connections meet the two halves of its card, as in MeasureCore.
        int lanes = LaneCount(Enumerable.Range(0, items.Count).SelectMany(i =>
        {
            var (stage, kid) = kids[i]; var other = twin?.Invoke(kid);
            double Port(UsbNode n) => hubs + stageX[stage] + (portOffset(members[stage], n) ?? width(members[stage]) / 2);
            if (other == null) return new[] { (Port(kid), Center(i) + shift) };
            double quarter = width(kid) / 4; var ports = new[] { Port(kid), Port(other) }.Order().ToArray();
            return [(ports[0], Center(i) + shift - quarter), (ports[1], Center(i) + shift + quarter)];
        }));
        double drop = lanes == 0 ? LevelGap : Math.Max(LevelGap, Stub + (lanes - 1) * RowLaneSpacing + Clearance);
        double rowY = 28 + maxHeight + FrameBelow + drop;
        var stages = members.Select((m, s) => new Item(m, hubs + stageX[s], 28, width(m), height(m), 0, 0, [], false)).ToList();
        var row = items.Select((item, i) => item with { X = xs[i] + shift, Y = rowY }).ToList();
        double right = Math.Max(hubs + block, row.Select(r => r.X + r.Width).DefaultIfEmpty(0).Max());
        double bottom = row.Select(r => r.Y + r.Height).DefaultIfEmpty(28 + maxHeight + FrameBelow).Max();
        return new Item(members[0], 0, 0, right, bottom, hubs, 28, row, false) { SnappedStages = stages };
    }
}

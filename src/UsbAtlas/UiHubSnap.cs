using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;
public partial class MainWindow
{
    private List<UsbNode> SnappedStages(UsbNode first)
    {
        if (horizontalTree || first.Kind != "Hub") return [];
        var members = new List<UsbNode> { first };
        var current = first;
        while (Children(current).FirstOrDefault(n => n.Kind == "Hub" && n.SnapToParentHub) is UsbNode next && !members.Contains(next))
        { members.Add(next); current = next; }
        return members;
    }
    private void PlaceSnapped(TopologyLayout.Item layout, double left, double top)
    {
        var stages = layout.SnappedStages!;
        double firstX = left + stages[0].X, lastX = stages.Max(s => left + s.X + s.Width), tallest = stages.Max(s => HeightFor(s.Node));
        var enclosure = new Border { Width = lastX - firstX + 20, Height = 28 + tallest + TopologyLayout.FrameBelow, CornerRadius = new CornerRadius(8), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(1), Background = Brush("Selection"), IsHitTestVisible = false, Tag = StageFrameTag, Child = new TextBlock { Text = "Linked hub stages", FontSize = 12, Foreground = Brush("TextSecondary"), Margin = new Thickness(10, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top } };
        // The frame lies beneath every connection, so the host's reaches the first stage's card through it.
        Canvas.SetLeft(enclosure, firstX - 10); Canvas.SetTop(enclosure, top); Panel.SetZIndex(enclosure, -1); Graph.Children.Add(enclosure);
        foreach (var stage in stages) Place(stage, left + stage.X, top + stage.Y);
        // Each link steps just below its socket and across into the next stage's left edge, level with that
        // stage's sockets, so the chain reads as one short hand-off.
        for (int i = 1; i < stages.Count; i++)
        {
            var child = stages[i].Node;
            snappedWires.Add(child.Id);
            if (!portAnchors.TryGetValue(child.Id, out var start)) continue;
            var card = cards[child.Id]; double lane = start.Y + 10, entryX = card.Point.X - 12, entryY = card.Point.Y + card.Card.Height - NodeVisuals.SocketHeight / 2;
            AddWire(child, [start, new Point(start.X, lane), new Point(entryX, lane), new Point(entryX, entryY), new Point(card.Point.X, entryY)]);
        }
        // Every stage's devices, in one row, each connected from its own socket; connections turn below the frame.
        var entries = Entries(layout.Children, left, top);
        var routes = TopologyLayout.Route(new Rect(firstX, top + 28, lastX - firstX, tallest + TopologyLayout.FrameBelow), entries.Select(e => e.Anchor).ToList(), entries.Select(e => e.Card).ToList(), false, TopologyLayout.RowLaneSpacing);
        for (int i = 0; i < entries.Count; i++) AddWire(entries[i].Node, routes[i]);
        foreach (var child in layout.Children) Place(child, left + child.X, top + child.Y);
        AddMissingUsb3Stubs(layout.Children);
    }
    private const string StageFrameTag = "stage-frame";
    private void AddHubSnapControls(UsbNode node)
    {
        if (node.Kind != "Hub") return;
        var parent = snapshot.Nodes.FirstOrDefault(n => n.Children.Any(c => c.Id == node.Id));
        if (parent?.Kind != "Hub")
        {
            var stages = node.Children.Where(n => n.Kind == "Hub").ToList();
            if (stages.Count == 0) return;
            Text("Arrange hub stages", 14);
            Text("Select a downstream hub stage to snap it beside this hub or unlink it.", 12, "TextSecondary");
            foreach (var stage in stages)
            {
                var select = new Button { Content = "Arrange: " + NodeVisuals.ShortName(stage), Tag = "select-hub-stage", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6), ToolTip = stage.DisplayName };
                select.Click += (_, _) => ShowOnCanvas(stage);
                Details.Children.Add(select);
            }
            return;
        }
        Text("Arrange hub stages", 14);
        var action = new Button { Content = node.SnapToParentHub ? "Unlink from upstream hub" : "Snap to upstream hub", Tag = "snap-upstream-hub", Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = parent.DisplayName };
        action.Click += (_, _) =>
        {
            bool enabled = !node.SnapToParentHub;
            if (!deviceLabels.TrySetSnap(node, snapshot, enabled, out var message)) { StatusText.Text = message; return; }
            // Stages are drawn only in the vertical layout, so linking one makes it the remembered layout; they
            // show at full size, framed on this hub, since a far view draws the real hierarchy instead.
            horizontalTree = false; ShowLayoutChoice();
            if (enabled) SaveLayout(false);
            focusedBranch = null; FocusBranchButton.Content = "Focus branch";
            foreach (var ancestor in FindPath(node.Id)) folded.Remove(ancestor.Id);
            FitClick(this, new RoutedEventArgs()); ShowDetails(); FrameSelectionPath();
            StatusText.Text = enabled ? "Hub stages snapped side by side; actual connections retained." : "Hub stage unlinked.";
        };
        Details.Children.Add(action);
        Text("User-defined grouping beside " + NodeVisuals.ShortName(parent) + ". Real port connections stay visible; uses vertical layout.", 12, "TextSecondary");
    }
}

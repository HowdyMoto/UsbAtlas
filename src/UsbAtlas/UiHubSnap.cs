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
        double firstX = left + stages[0].CardX;
        double lastX = stages.Max(s => left + s.X + s.CardX + WidthFor(s.Node));
        var enclosure = new Border { Width = lastX - firstX + 20, Height = stages.Max(s => HeightFor(s.Node)) + 48, CornerRadius = new CornerRadius(8), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(1), Background = Brush("Selection"), IsHitTestVisible = false, Child = new TextBlock { Text = "User-linked hub stages · real connections shown", FontSize = 12, Foreground = Brush("TextSecondary"), Margin = new Thickness(10, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top } };
        Canvas.SetLeft(enclosure, firstX - 10); Canvas.SetTop(enclosure, top); Graph.Children.Add(enclosure);
        foreach (var stage in stages) Place(stage, left + stage.X, top + stage.Y);
        double lane = top + 28 + stages.Max(s => HeightFor(s.Node)) + 10;
        for (int i = 1; i < stages.Count; i++)
        {
            var child = stages[i].Node;
            snappedWires.Add(child.Id);
            if (!portAnchors.TryGetValue(child.Id, out var start)) continue;
            var card = cards[child.Id]; double entryX = card.Point.X - 10, entryY = card.Point.Y + card.Card.Height / 2;
            AddWire(child, [start, new Point(start.X, lane), new Point(entryX, lane), new Point(entryX, entryY), new Point(card.Point.X, entryY)]);
        }
    }
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
            horizontalTree = false; OrientationButton.Content = "Layout: vertical";
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

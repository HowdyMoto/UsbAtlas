using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    private string? focusedBranch;
    private HashSet<string>? focusedIds;
    private void PrepareFocus()
    {
        focusedIds = null;
        TopologyTitle.Text = focusedBranch == null ? "TOPOLOGY" : "FOCUSED BRANCH";
        if (focusedBranch == null) return;
        var node = snapshot.Nodes.FirstOrDefault(n => n.Id == focusedBranch);
        if (node == null) { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; return; }
        focusedIds = FindPath(node.Id).Concat(node.Walk()).Select(n => n.Id).ToHashSet();
    }
    private void FocusBranchClick(object sender, RoutedEventArgs e)
    {
        if (focusedBranch != null) { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; }
        else if (selected != null) { focusedBranch = selected.Id; FocusBranchButton.Content = "Show all branches"; }
        FitClick(this, new RoutedEventArgs());
        FrameSelectionPath();
    }
    private void OpenInitialView()
    {
        double fit = Math.Min((GraphScroll.ViewportWidth - 32) / Graph.Width, (GraphScroll.ViewportHeight - 32) / Graph.Height);
        if (fit < 0.8 && selected != null)
        {
            var branch = selected.Kind == "Device" ? FindPath(selected.Id).LastOrDefault(n => n.Kind == "Hub") ?? selected : selected;
            focusedBranch = branch.Id;
            FocusBranchButton.Content = "Show all branches";
            Draw(); GraphScroll.UpdateLayout();
        }
        FrameSelectionPath();
    }
    private void FrameSelectionPath()
    {
        if (selected == null || GraphScroll.ViewportWidth < 1) return;
        var areas = FindPath(selected.Id).Select(CardNode).DistinctBy(n => n.Id)
            .Select(GraphBounds).OfType<Rect>().ToList();
        if (areas.Count == 0) return;
        var bounds = areas.Aggregate(Rect.Union);
        bounds.Inflate(24, 24);
        double fitAll = Math.Min((GraphScroll.ViewportWidth - 32) / Graph.Width, (GraphScroll.ViewportHeight - 32) / Graph.Height);
        // Fit modest topologies whole; large ones retain a readable selected upstream path.
        double scale = fitAll >= 0.8 ? Math.Min(1, fitAll) : Math.Clamp(Math.Min((GraphScroll.ViewportWidth - 32) / bounds.Width, (GraphScroll.ViewportHeight - 32) / bounds.Height), 0.8, 1);
        ResetPan(); SetZoom(scale); GraphScroll.UpdateLayout();
        var center = fitAll >= 0.8 ? new Point(Graph.Width / 2, Graph.Height / 2) : new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        GraphScroll.ScrollToHorizontalOffset(Math.Max(0, center.X * scale - GraphScroll.ViewportWidth / 2));
        GraphScroll.ScrollToVerticalOffset(Math.Max(0, center.Y * scale - GraphScroll.ViewportHeight / 2));
        GraphScroll.UpdateLayout(); RevealSelection(); UpdateGraphHint();
    }
    private void GraphScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateGraphHint();
    private void UpdateGraphHint()
    {
        if (GraphHint == null || GraphScroll == null) return;
        bool overflow = Graph.Width * GraphScale.ScaleX > GraphScroll.ViewportWidth + 2 || Graph.Height * GraphScale.ScaleY > GraphScroll.ViewportHeight + 2;
        GraphHint.Text = focusedBranch != null ? "Other branches & sockets hidden · Wheel to zoom" : overflow ? "More offscreen · Fit all · Wheel to zoom" : "Drag to pan · Wheel to zoom · Tab / arrows to navigate";
        GraphHint.ToolTip = (focusedBranch != null ? "Focused branch. Other branches and sockets are hidden. Use Show all branches to restore them. " : "") + (overflow ? "More topology extends offscreen. " : "Visible branches fit. ") + "Drag to pan; wheel to zoom; Tab and arrow keys to navigate.";
    }
}

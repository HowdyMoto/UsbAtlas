using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    // USB trees are shallow and wide: at most seven tiers, any number of ports per hub. Laid out from the
    // left, siblings stack as rows of horizontal text and position means one thing everywhere, so
    // horizontal is the default; a layout chosen from the Layout menu is remembered.
    private static string LayoutPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbAtlas", "layout.txt");
    private static bool SavedLayoutIsHorizontal()
    {
        try { return !System.IO.File.Exists(LayoutPath) || System.IO.File.ReadAllText(LayoutPath).Trim() != "vertical"; }
        catch (System.IO.IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    private static bool SaveLayout(bool horizontal)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LayoutPath)!);
            System.IO.File.WriteAllText(LayoutPath, horizontal ? "horizontal" : "vertical"); return true;
        }
        catch (System.IO.IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private string? focusedBranch;
    private HashSet<string>? focusedIds;
    private void PrepareFocus()
    {
        focusedIds = null;
        TopologyTitle.Text = "TOPOLOGY"; TopologyTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        if (focusedBranch == null) return;
        var node = snapshot.Nodes.FirstOrDefault(n => n.Id == focusedBranch);
        if (node == null) { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; return; }
        // A USB 3 hub's two sides are one hub, so focusing on either side, or on anything behind one, keeps both.
        var shown = FindPath(node.Id).Concat(node.Walk()).ToList();
        foreach (var side in shown.Where(n => n.CompanionHubId.Length > 0).ToList())
            if (snapshot.Nodes.FirstOrDefault(n => n.Id == side.CompanionHubId) is UsbNode other) shown.AddRange(FindPath(other.Id).Concat(other.Walk()));
        focusedIds = shown.Select(n => n.Id).ToHashSet();
        // Focus is only ever chosen, and it says plainly how much it hides.
        static bool Hardware(UsbNode n) => n.Kind is "Hub" or "Device" or "Unavailable";
        TopologyTitle.Text = $"Showing {snapshot.Nodes.Count(n => Hardware(n) && focusedIds.Contains(n.Id))} of {snapshot.Nodes.Count(Hardware)} hubs and devices";
        TopologyTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
    }
    private const double ReadableScale = 0.8, DetailAbove = 1.0;
    // Zooming past a level's range swaps in the next level's layout. The card under the pointer, or the
    // nearest one, stays where it was on screen. The scale follows the change in the graph's extent, kept
    // between the two thresholds so the next wheel step doesn't swap straight back.
    private bool SwitchDetail(double zoom, Point pointer)
    {
        var next = zoom < ReadableScale && detail < CardDetail.Far ? detail + 1 : zoom > DetailAbove && detail > CardDetail.Full ? detail - 1 : detail;
        if (next == detail || cards.Count == 0) return false;
        var at = GraphScroll.TranslatePoint(pointer, Graph);
        static Rect Box((Border Card, Point Point) c) => new(c.Point, new Size(c.Card.Width, c.Card.Height));
        static double Distance(Rect r, Point p) => new Vector(Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right)), Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom))).Length;
        static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        var anchor = cards.MinBy(c => Distance(Box(c.Value), at));
        var before = Graph.TranslatePoint(Center(Box(anchor.Value)), GraphScroll);
        double extent = horizontalTree ? Graph.Height : Graph.Width;
        detail = next; Draw();
        SetZoom(Math.Clamp(GraphScale.ScaleX * extent / (horizontalTree ? Graph.Height : Graph.Width), ReadableScale, DetailAbove));
        GraphScroll.UpdateLayout();
        if (cards.TryGetValue(anchor.Key, out var moved))
        {
            var after = Graph.TranslatePoint(Center(Box(moved)), GraphScroll);
            PanTransform.X += before.X - after.X; PanTransform.Y += before.Y - after.Y;
        }
        return true;
    }
    private void FocusBranchClick(object sender, RoutedEventArgs e)
    {
        if (focusedBranch != null) { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; }
        else if (selected != null) { focusedBranch = selected.Id; FocusBranchButton.Content = "Show all branches"; }
        FitClick(this, new RoutedEventArgs());
        FrameSelectionPath();
    }
    // The app opens on the whole topology, at the most detailed level that fits; it never hides branches
    // on its own.
    private void OpenInitialView()
    {
        if (focusedBranch != null) { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; }
        OverviewClick(this, new RoutedEventArgs());
        UpdateGraphHint();
    }
    private void FrameSelectionPath()
    {
        if (selected == null || GraphScroll.ViewportWidth < 1) return;
        var areas = FindPath(selected.Id).Select(DrawnAs).DistinctBy(n => n.Id)
            .Select(GraphBounds).OfType<Rect>().ToList();
        if (areas.Count == 0) return;
        var bounds = areas.Aggregate(Rect.Union);
        bounds.Inflate(24, 24);
        double fitAll = Math.Min((GraphScroll.ViewportWidth - 32) / Graph.Width, (GraphScroll.ViewportHeight - 32) / Graph.Height);
        // Fit modest topologies whole; large ones retain a readable selected upstream path.
        double scale = fitAll >= ReadableScale ? Math.Min(1, fitAll) : Math.Clamp(Math.Min((GraphScroll.ViewportWidth - 32) / bounds.Width, (GraphScroll.ViewportHeight - 32) / bounds.Height), ReadableScale, 1);
        ResetPan(); SetZoom(scale); GraphScroll.UpdateLayout();
        var center = fitAll >= ReadableScale ? new Point(Graph.Width / 2, Graph.Height / 2) : new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        GraphScroll.ScrollToHorizontalOffset(Math.Max(0, center.X * scale - GraphScroll.ViewportWidth / 2));
        GraphScroll.ScrollToVerticalOffset(Math.Max(0, center.Y * scale - GraphScroll.ViewportHeight / 2));
        GraphScroll.UpdateLayout(); RevealSelection(); UpdateGraphHint();
    }
    private void GraphScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The view controls float in the canvas's corner, clear of whichever scroll bars show.
        ViewControls.Margin = new Thickness(0, 0, GraphScroll.ActualWidth - GraphScroll.ViewportWidth + 12, GraphScroll.ActualHeight - GraphScroll.ViewportHeight + 12);
        UpdateGraphHint();
    }
    private void UpdateGraphHint()
    {
        if (GraphHint == null || GraphScroll == null) return;
        bool overflow = Graph.Width * GraphScale.ScaleX > GraphScroll.ViewportWidth + 2 || Graph.Height * GraphScale.ScaleY > GraphScroll.ViewportHeight + 2;
        GraphHint.Text = focusedBranch != null ? "Other branches & sockets hidden · Wheel to zoom" : overflow ? "More offscreen · Fit all (Shift+1) · Wheel to zoom" : "Drag to pan · Wheel to zoom · Arrows to navigate · Shift+2 finds the selection";
        GraphHint.ToolTip = (focusedBranch != null ? "Focused branch. Other branches and sockets are hidden. Use Show all branches to restore them. " : "") + (overflow ? "More topology extends offscreen. " : "Visible branches fit. ") + "Drag to pan; wheel to zoom; Tab and arrow keys to navigate. Shift+0 shows full cards at 100%, Shift+1 fits everything, and Shift+2 centers the selection.";
    }
}

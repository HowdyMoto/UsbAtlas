using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsbAtlas;

public partial class MainWindow
{
    private readonly Dictionary<string, TreeViewItem> treeItems = [];
    private readonly HashSet<string> collapsedTreeBranches = [];
    private bool syncingTree, treeRebuilt;
    private string treeSignature = "", treeSelectionId = "";
    private double treePanelWidth = 280;

    private void UpdateDeviceTree()
    {
        if (DeviceTree == null) return;
        // Empty ports are sockets on the canvas; the tree lists them only when they match a search.
        bool Include(UsbNode n) => Visible(n) && (n.Kind != "Empty port" || n.PortLabel.Length > 0 || appliedQuery.Length > 0);
        var nodes = snapshot.Nodes.Where(Include).ToList();
        string signature = System.Text.Json.JsonSerializer.Serialize(new { Theme.IsDark, appliedQuery, Nodes = nodes.Select(n => new { n.Id, n.DisplayName, n.PortLabel, n.Kind, n.Port, n.Status, n.Location, Issue = Issue(n) }) });
        if (signature == treeSignature) return;
        syncingTree = true;
        try
        {
            DeviceTree.Items.Clear(); treeItems.Clear(); treeRebuilt = true;
            TreeViewItem Create(UsbNode node)
            {
                // The name fills the row and trims, so the tree never scrolls sideways; the tooltip has the full name.
                var header = new DockPanel();
                var icon = NodeVisuals.Icon(node, 16); icon.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(icon, Dock.Left); header.Children.Add(icon);
                var issues = node.Kind is "Controller" or "Root hub" ? OtherIssues(node) : Issues(node);
                if (issues.Count > 0)
                {
                    var glyph = NodeVisuals.StatusGlyph(issues.Max(i => i.Severity)); glyph.Margin = new Thickness(6, 0, 0, 0);
                    DockPanel.SetDock(glyph, Dock.Right); header.Children.Add(glyph);
                }
                header.Children.Add(new TextBlock { FontSize = 13, Text = (node.Port > 0 ? $"{node.Port:00} · " : "") + (node.PortLabel.Length > 0 ? node.PortLabel + " → " : "") + NodeVisuals.ShortName(node), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var item = new TreeViewItem { Header = header, Tag = node, IsExpanded = appliedQuery.Length > 0 || !collapsedTreeBranches.Contains(node.Id), ToolTip = $"{node.DisplayName}\n{NodeVisuals.Label(node)} · {node.Status}\n{pathLabels.GetValueOrDefault(node.Id)}\n{HubRelationships.Description(node, snapshot)}\n{Issue(node)}" };
                System.Windows.Automation.AutomationProperties.SetName(item, $"{node.DisplayName}, {NodeVisuals.Label(node)}" + (node.Port > 0 ? $", port {node.Port}" : ""));
                item.Expanded += (_, e) => { if (!syncingTree && appliedQuery.Length == 0 && ReferenceEquals(e.OriginalSource, item)) collapsedTreeBranches.Remove(node.Id); };
                item.Collapsed += (_, e) => { if (!syncingTree && appliedQuery.Length == 0 && ReferenceEquals(e.OriginalSource, item)) collapsedTreeBranches.Add(node.Id); };
                treeItems[node.Id] = item;
                // As on the canvas, a controller with one root hub is one host whose children are its root ports.
                foreach (var child in (MergedRoot(node) ?? node).Children.Where(Include).OrderBy(n => n.Port)) item.Items.Add(Create(child));
                return item;
            }
            foreach (var root in snapshot.Controllers.Where(Visible)) DeviceTree.Items.Add(Create(root));
            treeSignature = signature;
        }
        finally { syncingTree = false; }
    }

    // Mirrors the canvas selection in the tree. Scrolls when the selection changes, or on request.
    private void SyncTreeSelection(bool reveal = false)
    {
        if (syncingTree) return;
        syncingTree = true;
        try
        {
            if (selected == null || !treeItems.TryGetValue(selected.Id, out var item))
            {
                // The selection is filtered out of the tree; don't leave a stale highlight behind.
                if (DeviceTree.SelectedItem is TreeViewItem stale) stale.IsSelected = false;
                treeSelectionId = "";
                return;
            }
            bool changed = treeSelectionId != selected.Id;
            if (changed)
                foreach (var ancestor in FindPath(selected.Id).SkipLast(1))
                    if (treeItems.TryGetValue(ancestor.Id, out var parent)) { parent.IsExpanded = true; collapsedTreeBranches.Remove(ancestor.Id); }
            item.IsSelected = true;
            treeSelectionId = selected.Id;
            bool rebuilt = treeRebuilt; treeRebuilt = false;
            // Wait for newly expanded branches to lay out, then show the row itself rather than its whole subtree.
            if ((changed || reveal || rebuilt) && TreePanel.Visibility == Visibility.Visible)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => (item.Template?.FindName("Row", item) as FrameworkElement ?? item).BringIntoView());
        }
        finally { syncingTree = false; }
    }

    private void DeviceTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (syncingTree || e.NewValue is not TreeViewItem { Tag: UsbNode node }) return;
        ShowOnCanvas(node);
    }

    // Clicking the row that is already selected raises no selection change, but should still find it on the canvas.
    private void DeviceTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var hit = e.OriginalSource as DependencyObject; hit != null && hit != DeviceTree; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is ToggleButton) return;
            if (hit is TreeViewItem item) { if (item.IsSelected && item.Tag is UsbNode node) ShowOnCanvas(node); return; }
        }
    }

    // Selects a tree node on the canvas, opening folded branches only when they hide it.
    private void ShowOnCanvas(UsbNode node)
    {
        bool redraw = false;
        if (focusedIds != null && !focusedIds.Contains(node.Id))
        {
            focusedBranch = (node.Kind == "Device" ? FindPath(node.Id).LastOrDefault(n => n.Kind == "Hub") ?? node : node).Id;
            redraw = true;
        }
        foreach (var ancestor in FindPath(node.Id).SkipLast(1)) redraw |= folded.Remove(ancestor.Id);
        if (redraw) Draw();
        SelectNode(snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id) ?? node);
        RevealSelection(); Pulse(node);
        if (focusedBranch != null) FrameSelectionPath();
    }

    private void TreePanelClick(object sender, RoutedEventArgs e)
    {
        bool hide = TreePanel.Visibility == Visibility.Visible;
        if (hide) treePanelWidth = TreeColumn.ActualWidth;
        TreeColumn.MinWidth = hide ? 0 : 180;
        TreeColumn.Width = new GridLength(hide ? 0 : treePanelWidth);
        TreeSplitterColumn.Width = new GridLength(hide ? 0 : 5);
        TreePanel.Visibility = TreeSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        TreeButton.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout(); FitSidePanels();
    }

    // A splitter beside the star-sized graph column honors only its own column's limits, so each
    // side panel's maximum is whatever leaves the graph its minimum width beside the other panel.
    private void FitSidePanels()
    {
        var graph = ((Grid)TreePanel.Parent).ColumnDefinitions[2];
        double free = ((Grid)TreePanel.Parent).ActualWidth - TreeSplitterColumn.ActualWidth - SplitterColumn.ActualWidth - graph.MinWidth;
        if (TreePanel.Visibility == Visibility.Visible) TreeColumn.MaxWidth = Math.Max(TreeColumn.MinWidth, free - InspectorColumn.ActualWidth);
        if (InspectorPanel.Visibility == Visibility.Visible) InspectorColumn.MaxWidth = Math.Max(InspectorColumn.MinWidth, Math.Min(InspectorMaxWidth, free - TreeColumn.ActualWidth));
    }
    private const double InspectorMaxWidth = 520;
    private void SidePanelsSizeChanged(object sender, SizeChangedEventArgs e) => FitSidePanels();
    private void SplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => FitSidePanels();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UsbAtlas;

public partial class MainWindow
{
    private void VerifyCompactUi()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        bool savedHorizontal = horizontalTree, savedCompact = Compact;
        try
        {
            snapshot = DemoData.Create();
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub");
            hub.ScanIncomplete = true; hub.SpeedLimited = true;
            for (int i = 5; i <= 33; i++) hub.Children.Add(new UsbNode { Id = hub.Id + "/" + i, Kind = "Empty port", Name = "Available port " + i, Port = i, Status = "Empty" });
            hub.PortCount = 33;
            hub.Name = "Long hub identity with several words and USB generation information";
            selected = hub; EmptyPorts.IsChecked = true;
            foreach (bool compact in new[] { true, false })
            foreach (bool horizontal in new[] { false, true })
            {
                CompactDensity.IsChecked = compact; horizontalTree = horizontal; FitClick(this, new RoutedEventArgs()); UpdateLayout();
                var items = cards.Values.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var a = items[i]; var bounds = new Rect(a.Point, new Size(a.Card.Width, a.Card.Height));
                    Check(bounds.Right <= Graph.Width + 1 && bounds.Bottom <= Graph.Height + 1, "Variable-height card exceeds graph bounds.");
                    Check(a.Card.Child.DesiredSize.Height <= a.Card.Height - a.Card.Padding.Top - a.Card.Padding.Bottom - a.Card.BorderThickness.Top - a.Card.BorderThickness.Bottom + 1, "Card content exceeds its allocated height.");
                    for (int j = i + 1; j < items.Count; j++)
                        Check(!bounds.IntersectsWith(new Rect(items[j].Point, new Size(items[j].Card.Width, items[j].Card.Height))), "Variable-height cards overlap.");
                    foreach (var child in Children((UsbNode)a.Card.Tag))
                        Check(horizontal ? cards[child.Id].Point.X >= bounds.Right + TopologyLayout.LevelGap : cards[child.Id].Point.Y >= bounds.Bottom + TopologyLayout.LevelGap, "Variable-height parent overlaps its children.");
                }
                Check(portSlots.Count == 31 && cards.Values.All(c => ((UsbNode)c.Card.Tag).Kind != "Empty port"), "Empty ports must render as slots, not full cards.");
            }
            horizontalTree = false; CompactDensity.IsChecked = true; EmptyPorts.IsChecked = false; FitClick(this, new RoutedEventArgs());
            var target = snapshot.Nodes.First(n => n.Kind == "Device");
            var originalCard = cards[target.Id].Card;
            SelectNode(target);
            Check(ReferenceEquals(originalCard, cards[target.Id].Card), "Selecting a node rebuilt the graph.");
            originalCard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(ReferenceEquals(originalCard, cards[target.Id].Card) && selected?.Id == target.Id, "Keyboard activation replaced the focused card.");
            folded.Add(snapshot.Controllers[0].Id);
            PanTransform.X = 250; PanTransform.Y = -800;
            Search.Text = "Available port"; ApplySearch();
            Check(matches.Count == 31 && portSlots.Count == 31, "Searching hidden empty ports must reveal all matching slots.");
            Check(PanTransform.X == 0 && PanTransform.Y == 0 && selected?.Kind == "Empty port", "Search must reset panning and select a match.");
            var first = selected!.Id; NextMatch(1); Check(selected!.Id != first, "Next result failed."); NextMatch(-1); Check(selected!.Id == first, "Previous result failed.");
            Search.Text = "Reduced speed"; ApplySearch();
            Check(matches.Count == 1 && selected?.Id == hub.Id, "Issue search did not select affected hardware.");
            UpdateIssues(); Check(IssuesButton.IsEnabled && Issue(hub).Contains("Scan incomplete"), "Incomplete scans must be visible as issues.");
            InspectorClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(InspectorPanel.Visibility == Visibility.Collapsed && InspectorColumn.ActualWidth == 0, "Inspector collapse failed.");
            InspectorClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(InspectorPanel.Visibility == Visibility.Visible && InspectorColumn.ActualWidth >= 260, "Inspector restore failed.");
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); folded.Clear(); expandedPorts.Clear(); EmptyPorts.IsChecked = false;
            snapshot = savedSnapshot; selected = savedSelection; horizontalTree = savedHorizontal; CompactDensity.IsChecked = savedCompact;
            OrientationButton.Content = horizontalTree ? "Horizontal" : "Vertical";
            FitClick(this, new RoutedEventArgs()); ShowDetails(); UpdateIssues();
        }
    }
}

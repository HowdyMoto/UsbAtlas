using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UsbAtlas;

public partial class MainWindow
{
    private void VerifyIdentityUi()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        var savedSnapshot = snapshot; var savedSelection = selected; var savedLabels = deviceLabels;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsbAtlas-ui-label-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            snapshot = DemoData.Create(); deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json"));
            selected = snapshot.Controllers[0]; Draw(); ShowDetails(); UpdateLayout();
            var text = Descendants(Details).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(text.Contains("Port support") && text.Any(t => t.Contains("USB 3.x")), "Host inspector lost reported port protocols.");
            Check(text.Contains("Supply capacity") && text.Contains("Unknown · not measured") && !text.Contains("Negotiated link"), "Host inspector must distinguish unknown supply from peripheral metrics.");
            var hub = snapshot.Nodes.First(n => n.Kind == "Hub"); SelectNode(hub); UpdateLayout();
            var editor = Details.Children.OfType<Expander>().First(); editor.IsExpanded = true; UpdateLayout();
            var input = Descendants(editor).OfType<TextBox>().Single(); input.Text = "Dell monitor KVM";
            Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Save label").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
            Check(hub.UserLabel == "Dell monitor KVM" && hub.Name == "Studio desktop hub", "Label editor overwrote reported identity or failed to save.");
            Check(Descendants(cards[hub.Id].Card).OfType<TextBlock>().Any(t => t.Text == "Dell monitor KVM"), "Saved label did not reach graph card.");
            Search.Text = "Dell monitor KVM"; ApplySearch(); Check(matches.Count == 1 && selected?.Id == hub.Id, "Saved labels must be searchable.");
            Search.Clear(); ApplySearch(); SelectNode(hub); UpdateLayout();
            deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json"));
            var refreshed = DemoData.Create(); deviceLabels.Apply(refreshed);
            Check(refreshed.Nodes.Single(n => n.Id == hub.Id).UserLabel == "Dell monitor KVM", "Labels did not survive a fresh snapshot and store reload.");
            editor = Details.Children.OfType<Expander>().First(); editor.IsExpanded = true; UpdateLayout();
            Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(hub.UserLabel == "" && hub.DisplayName == "Studio desktop hub", "Label Reset failed to restore the detected name.");
        }
        finally
        {
            Search.Clear(); searchTimer.Stop(); snapshot = savedSnapshot; selected = savedSelection; deviceLabels = savedLabels;
            Draw(); ShowDetails(); UpdateIssues();
            System.IO.Directory.Delete(directory, true);
        }
    }
    private async Task VerifyRefreshUi()
    {
        // Demo refresh completes immediately, exercising the shortest possible scan.
        if (!demo) return;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.F5) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Search.RaiseEvent(key);
        Check(key.Handled && busy && RefreshProgress.Visibility == Visibility.Visible && RefreshProgress.Opacity == 1, "F5 must show progress immediately, including from search.");
        int version = refreshIndicatorVersion;
        await Refresh();
        Check(refreshIndicatorVersion == version, "Repeated refresh must not start an overlapping scan.");
        while (busy) await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Check(RefreshButton.IsEnabled && RefreshProgress.Visibility == Visibility.Visible, "Fast refresh must enable controls while its feedback fades.");
        // Restart while the previous scan is fading; its completion must not hide the new bar.
        await Task.Delay(160);
        var restarted = Refresh();
        Check(RefreshProgress.Visibility == Visibility.Visible && RefreshProgress.Opacity == 1, "Refresh during fade must restore full visibility.");
        await restarted;
        await Task.Delay(170);
        Check(RefreshProgress.Visibility == Visibility.Visible, "An earlier fade hid a newer refresh indicator.");
        await Task.Delay(250);
        Check(RefreshProgress.Visibility == Visibility.Collapsed && !RefreshProgress.IsIndeterminate, "Progress animation must stop after fading out.");
    }
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
            hub.NameSource = "USB ID lookup";
            hub.UserLabel = "Dell monitor KVM with a longer personal label";
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UsbAtlas;

public partial class MainWindow
{
    private readonly HashSet<string> inspectorSections = [];
    private static bool MergedEnumerationFailure(UsbNode node) => node.Kind == "Unavailable"
        && node.Status == "Enumeration failed" && node.DriverProblems.Any(p => p.Code == 43);

    private List<(Severity Severity, string Text)> InspectorIssues(UsbNode node) => Issues(node)
        .Where(i => !(MergedEnumerationFailure(node) && i.Text == "Windows reports a problem"))
        .OrderByDescending(i => i.Severity).ToList();

    private TextBlock OverviewText(string text, double size = 14, string color = "TextPrimary") => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 5)
    };

    private void AddLocationSummary(UsbNode node)
    {
        var host = FindPath(node.Id).FirstOrDefault();
        var text = OverviewText(pathLabels.GetValueOrDefault(node.Id, "")
            + (host != null && host != node ? " · " + NodeVisuals.ShortName(host) : ""), 12, "TextSecondary");
        text.Tag = "inspector-location";
        Details.Children.Add(text);
    }

    // Reuse the full reference fields and editors without making them the default reading path.
    private void FoldInspectorContent(int start, string title, string key)
    {
        var content = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
        while (Details.Children.Count > start)
        {
            var child = Details.Children[start];
            Details.Children.RemoveAt(start);
            content.Children.Add(child);
        }
        var section = new Expander
        {
            Header = title, Content = content, IsExpanded = inspectorSections.Contains(key),
            FontSize = 14, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 12, 0, 4), Tag = "inspector-" + key
        };
        section.Expanded += (_, _) => inspectorSections.Add(key);
        section.Collapsed += (_, _) => inspectorSections.Remove(key);
        Details.Children.Add(section);
    }

    private void AddConnectionOverview(UsbNode node)
    {
        var box = new StackPanel();
        var title = OverviewText(node.Kind is "Controller" or "Root hub" ? "Connected devices" : "Connection", 15);
        title.FontWeight = FontWeights.SemiBold;
        box.Children.Add(title);

        var chain = FindPath(node.Id).Where(n => n.Id == node.Id || !mergedHosts.ContainsKey(n.Id)).ToList();
        // Show the host and immediate parent; the complete chain is available on expansion.
        var visible = chain.Count > 3 ? new[] { chain[0], chain[^2], chain[^1] }.ToList() : chain;
        foreach (var hop in visible)
        {
            if (chain.Count > 3 && hop == chain[^2])
                box.Children.Add(OverviewText($"⋮ {chain.Count - 3} intermediate hub{(chain.Count == 4 ? "" : "s")}", 12, "TextSecondary"));
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var icon = NodeVisuals.Icon(hop, 16); icon.Margin = new Thickness(0, 2, 8, 0);
            DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
            var label = (hop == node ? "↳ " : "") + NodeVisuals.ShortName(hop);
            if (hop != chain[0]) label = pathLabels.GetValueOrDefault(hop.Id, "") + " · " + label;
            line.Children.Add(OverviewText(label, 13, hop == node ? "TextPrimary" : "TextSecondary"));
            var button = new Button { Content = line, Style = (Style)FindResource("EditableNameButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), ToolTip = "Select " + hop.DisplayName };
            button.Click += (_, _) => { SelectNode(hop); LocateClick(this, new RoutedEventArgs()); };
            box.Children.Add(button);
        }
        if (chain.Count > 3)
        {
            var full = new StackPanel();
            foreach (var hop in chain) full.Children.Add(OverviewText(pathLabels.GetValueOrDefault(hop.Id, "") + " · " + hop.DisplayName, 13));
            box.Children.Add(new Expander { Header = $"Full path · {chain.Count - 2} hubs", Content = full, FontSize = 13, Margin = new Thickness(0, 4, 0, 4) });
        }
        if (node.Kind == "Unavailable")
            box.Children.Add(OverviewText("Link speed and power could not be read while the device is unavailable.", 13, "TextSecondary"));
        else if (node.Kind == "Empty port")
            box.Children.Add(OverviewText("Nothing connected · " + NodeVisuals.SocketLabel(node), 13, "TextSecondary"));
        else if (node.Kind is "Device" or "Hub")
        {
            var facts = new Grid { Margin = new Thickness(0, 10, 0, 4), Tag = "overview-metrics" };
            facts.ColumnDefinitions.Add(new ColumnDefinition()); facts.ColumnDefinitions.Add(new ColumnDefinition());
            void Fact(string label, string value, int col)
            {
                var p = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 8, 0, 0, 0) };
                var number = OverviewText(value, 18); number.FontWeight = FontWeights.SemiBold;
                p.Children.Add(number); p.Children.Add(OverviewText(label, 12, "TextSecondary"));
                Grid.SetColumn(p, col); facts.Children.Add(p);
            }
            if (node.LinkMbps.HasValue) Fact("Link signaling rate", ShortSpeed(node), 0);
            if (DeclaresSelfPower(node)) Fact("Power source", "Own supply", 1);
            else if (node.MaxPowerMa is int ma) Fact("Declared current", $"{ma} mA", 1);
            if (facts.Children.Count > 0) box.Children.Add(facts);
            if (DeclaresSelfPower(node) && node.MaxPowerMa is > 0) box.Children.Add(OverviewText($"Also declares {node.MaxPowerMa} mA from USB", 12, "TextSecondary"));
            if (UsbBudgets.LinkUse(node) is (var used, var capacity, _))
            {
                box.Children.Add(OverviewText($"Reserved: {UsbBudgets.Share(used, capacity)}", 12, "TextSecondary"));
                box.Children.Add(NodeVisuals.LinkBar(used / capacity));
            }
            if (node.PollIntervalMs is double poll) box.Children.Add(OverviewText("Polling · " + UsbBudgets.PollingRate(poll), 13, "TextSecondary"));
        }
        var children = node.Kind == "Controller" ? node.Children.SelectMany(n => n.Children).Where(n => n.Kind != "Empty port").ToList()
            : node.Children.Where(n => n.Kind != "Empty port").ToList();
        if (node.Kind is "Hub" or "Controller" or "Root hub")
        {
            box.Children.Add(OverviewText($"{children.Count} connected · {children.Count(n => Issues(n).Any(i => i.Severity != Severity.Note))} need attention", 13, "TextSecondary"));
            var list = new StackPanel();
            foreach (var child in children)
            {
                var row = new Button { Content = OverviewText(NodeVisuals.ShortName(child) + " · " + child.Status, 13),
                    Style = (Style)FindResource("EditableNameButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                row.Click += (_, _) => SelectNode(child); list.Children.Add(row);
            }
            if (children.Count > 0) box.Children.Add(new Expander { Header = "View connected devices", Content = list, FontSize = 13 });
        }
        Details.Children.Add(new Border { Child = box, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = Brush("Subtle"), Margin = new Thickness(0, 8, 0, 4), Tag = "connection-overview" });
    }

    private void AddPreviousOccupant(UsbNode node)
    {
        if (node.Kind != "Unavailable" || snapshot.Remembered == null || !pathLabels.TryGetValue(node.Id, out var path)) return;
        var earlier = snapshot.Remembered.Where(r => r.VendorId != "0000" && Remembered.Place(snapshot, r, pathLabels) == path)
            .OrderByDescending(r => r.LastConnected).FirstOrDefault();
        if (earlier == null) return;
        var body = new StackPanel();
        body.Children.Add(OverviewText(earlier.Name + " · " + earlier.VendorId + ":" + earlier.ProductId));
        if (earlier.LastConnected is DateTime at) body.Children.Add(OverviewText("Last arrival recorded " + at.ToString("g"), 12, "TextSecondary"));
        body.Children.Add(OverviewText("Windows remembers this device on the same port. This does not identify the device currently failing.", 13, "TextSecondary"));
        Details.Children.Add(new Expander { Header = "Previously seen on this port", Content = body, FontSize = 14, Margin = new Thickness(0, 10, 0, 4) });
    }
}

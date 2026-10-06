using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace UsbAtlas;
public partial class MainWindow
{
    // The few issues to fix first, above the canvas, so the answer is on screen before anything is opened:
    // each names the issue and where it is, then its most likely fix. Clicking one shows it on the canvas
    // with its explanation open. Hiding the strip lasts until the issues change; the issue list keeps them.
    private string fixFirstShown = "", fixFirstHidden = "";
    private Snapshot? fixFirstOf;

    // Drawing calls this for whichever snapshot is on the canvas, and it reads it again only when that changes;
    // a rescan or a theme change asks for it outright.
    private void UpdateFixFirst(bool force = false)
    {
        if (!force && ReferenceEquals(fixFirstOf, snapshot)) return;
        fixFirstOf = snapshot;
        var items = Triage.FixFirst(snapshot);
        fixFirstShown = string.Join("\n", items.Select(i => $"{i.Node.Id}|{i.Issue}|{i.Also.Count}"));
        FixFirstList.Children.Clear();
        foreach (var item in items) FixFirstList.Children.Add(FixFirstRow(item));
        FixFirstPanel.Visibility = items.Count > 0 && fixFirstShown != fixFirstHidden ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button FixFirstRow(Triage.Item item)
    {
        string where = NodeVisuals.ShortName(CardNode(item.Node));
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        // The issue's name is a status badge, as on cards: glyph and status ink on its tinted surface. The
        // place and the fix are plain text.
        text.Inlines.Add(new Run(where) { FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary") });
        text.Inlines.Add(new Run($" — {item.Fix}") { Foreground = Brush("TextSecondary") });
        var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
        var badge = NodeVisuals.StatusBadge(item.Severity, item.Issue); badge.Margin = new Thickness(0, 0, 7, 0); badge.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge); row.Children.Add(text);
        string also = item.Also.Count == 0 ? "" : " The same fix also resolves " + string.Join(", ", item.Also.Select(a => $"{a.Issue} on {NodeVisuals.ShortName(CardNode(a.Node))}")) + ".";
        var button = new Button
        {
            Content = row, Style = (Style)FindResource("EditableNameButton"), Padding = new Thickness(0), Margin = new Thickness(-4, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand, Tag = item,
            ToolTip = $"{item.Issue} on {where}. {item.Fix}{also} Click to show it and what to do."
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Fix first: {item.Issue} on {where}");
        button.Click += (_, _) => RevealIssue(item.Node, item.Issue);
        return button;
    }

    // Shows an issue's node on the canvas, with nothing hiding it, and its explanation open in Properties.
    private void RevealIssue(UsbNode node, string? issue = null)
    {
        Search.Clear(); searchTimer.Stop();
        foreach (var ancestor in FindPath(node.Id)) { folded.Remove(ancestor.Id); folded.Remove(DrawnAs(ancestor).Id); }
        Draw();
        if (issue != null)
        {
            openExplanations.Add(issue);
            if (InspectorPanel.Visibility != Visibility.Visible) InspectorClick(this, new RoutedEventArgs());
        }
        ShowOnCanvas(node);
    }

    private void HideFixFirstClick(object sender, RoutedEventArgs e)
    {
        fixFirstHidden = fixFirstShown;
        FixFirstPanel.Visibility = Visibility.Collapsed;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace UsbAtlas;
public partial class MainWindow
{
    // What to fix first is on screen without opening anything, leads with the worst, names its fix, opens it
    // when clicked, and stays hidden only until the issues change. The exported image holds the whole graph.
    private void VerifyFixFirst()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        UpdateIssues(); UpdateLayout();
        var expected = Triage.FixFirst(snapshot);
        var rows = FixFirstList.Children.OfType<Button>().ToList();
        Check(expected.Count > 0 && FixFirstPanel.Visibility == Visibility.Visible && FixFirstPanel.ActualHeight > 0, "Fix first must show when the sample has issues.");
        Check(rows.Count == expected.Count && rows.Select(r => ((Triage.Item)r.Tag).Issue).SequenceEqual(expected.Select(i => i.Issue)), "Fix first must list the ranked issues in order.");
        var lead = (Triage.Item)rows[0].Tag;
        var runs = ((TextBlock)((DockPanel)rows[0].Content).Children.OfType<TextBlock>().Single()).Inlines.OfType<Run>().ToList();
        Check(lead.Severity == Severity.Error && runs[0].Text == lead.Issue && runs[0].Foreground == Brush("Error") && runs[^1].Text.Contains(lead.Fix) && runs[^1].Foreground == Brush("TextSecondary"),
            "Fix first leads with the error, its name in status color and its fix in plain text.");
        Check(!runs[1].Text.Contains(lead.Issue), "Fix first names where the issue is without repeating it, as card titles do.");
        Check(((string)rows[0].ToolTip).Contains("also resolves"), "A grouped fix says what else it resolves.");
        CaptureUi("fix-first-preview.png");

        rows[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); UpdateLayout();
        Check(selected?.Id == CardNode(lead.Node).Id && openExplanations.Contains(lead.Issue) && Details.Children.OfType<FrameworkElement>().Any(x => Equals(x.Tag, "warning:" + lead.Issue)),
            "Clicking a fix selects its node and opens its explanation.");

        HideFixFirstClick(this, new RoutedEventArgs()); UpdateIssues();
        Check(FixFirstPanel.Visibility == Visibility.Collapsed, "Hidden fix first stays hidden while the issues are the same.");
        fixFirstHidden = "changed"; UpdateIssues();
        Check(FixFirstPanel.Visibility == Visibility.Visible, "Fix first comes back when the issues change.");
        fixFirstHidden = "";
        var saved = snapshot;
        snapshot = new Snapshot { IsDemo = true, Controllers = [new UsbNode { Id = "calm", Kind = "Controller", Name = "Host", Children = [new UsbNode { Id = "calm/1", Kind = "Device", Name = "Keyboard", Port = 1, LinkMbps = 12 }] }] };
        Draw();
        Check(FixFirstPanel.Visibility == Visibility.Collapsed, "Fix first goes away for a topology with nothing to fix.");
        snapshot = saved; Draw(); UpdateIssues();
        Check(FixFirstPanel.Visibility == Visibility.Visible, "Fix first comes back with the sample.");

        double zoom = GraphScale.ScaleX;
        var image = TopologyImage();
        Check(GraphScale.ScaleX == zoom, "Exporting an image must leave the zoom as it was.");
        Check(image.PixelWidth >= Graph.Width * 2 && image.PixelHeight > (Graph.Height + SocketLegend.ActualHeight) * 2, "The image holds the whole graph and the legend at twice their size.");
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = System.IO.File.Create("export-preview.png"); png.Save(stream);
    }
}

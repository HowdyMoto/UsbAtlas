using System.Windows.Controls;

namespace UsbAtlas;
public partial class MainWindow
{
    // A hub on the known-problems list that has been dropping says so in Properties, links the source in
    // Detection details and carries the note; the note goes when it's no longer listed.
    private void VerifyKnownProblems()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string FieldText(string label) => Details.Children.OfType<Grid>().Where(g => Equals(g.Tag, "field"))
            .Select(g => g.Children.OfType<TextBlock>().ToList()).FirstOrDefault(t => t.Count == 2 && t[0].Text == label)?[1].Text ?? "";
        List<string> Evidence() => Details.Children.OfType<Expander>().FirstOrDefault(x => Equals(x.Header, "Detection details"))?.Content is StackPanel p
            ? p.Children.OfType<TextBlock>().Select(t => t.Text).ToList() : [];
        var hub = snapshot.Nodes.First(n => n.Name == "Studio desktop hub");
        var (vendor, product, reconnects) = (hub.VendorId, hub.ProductId, hub.QuickReconnects);
        try
        {
            (hub.VendorId, hub.ProductId, hub.QuickReconnects) = ("05E3", "0612", 1);
            UpdateIssues(); SelectNode(hub); ShowDetails(); UpdateLayout();
            Check(FieldText("Known problems") == "Signs here · link power management" && Issue(hub).Contains(KnownProblems.Label)
                && Evidence().Any(t => t.StartsWith("Known chip problem: Genesys Logic hub (05E3:0612)") && t.EndsWith("quirks.c#L345")),
                "A listed hub that drops names its problem in Properties, links the source and carries the note.");
            CaptureUi("known-problem-preview.png");
        }
        finally
        {
            (hub.VendorId, hub.ProductId, hub.QuickReconnects) = (vendor, product, reconnects);
            UpdateIssues(); ShowDetails(); UpdateLayout();
        }
        Check(!Issue(hub).Contains(KnownProblems.Label) && FieldText("Known problems") == "None listed", "A chip that isn't listed shows none and carries no note.");
    }
}

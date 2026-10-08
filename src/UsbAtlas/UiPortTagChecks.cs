using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    // A port's name tag spans both halves of a USB 3 socket, and pointing at the socket or selecting the port or the
    // device in it shows the whole name, untrimmed, in both layouts. In the sample, ports 7 and 8 are empty halves
    // sharing sockets with the keyboard and the NVMe enclosure.
    private void VerifyPortTags()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected; var savedLabels = deviceLabels; bool savedHorizontal = horizontalTree;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsbAtlas-tag-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            snapshot = DemoData.Create(); deviceLabels = new DeviceLabels(System.IO.Path.Combine(directory, "labels.json")); selected = null;
            UsbNode Node(string id) => snapshot.Nodes.Single(n => n.Id == id);
            const string spanName = "Case front panel", loneName = "Capture card slot", cardName = "Audio interface front jack", longName = "Header JUSB3_2: the second front-panel USB 3.2 Gen 2 connector on the motherboard, case top";
            foreach (var (id, name) in new[] { ("demo/root/7", spanName), ("demo/root/1/3", loneName), ("demo/root/2", cardName), ("demo/root/8", longName) })
                Check(deviceLabels.TrySetPort(Node(id), snapshot, name, out var error), $"Naming {id} must save: {error}");
            foreach (bool horizontal in new[] { false, true })
            {
                string layout = horizontal ? "horizontal" : "vertical";
                horizontalTree = horizontal; selected = null; Draw(); UpdateLayout();
                Border TagFor(string name) => Graph.Children.OfType<Border>().Single(b => Equals(b.Tag, PortTagMarker) && ((TextBlock)b.Child).Text == name);
                static Rect Area(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);
                static bool Same(Rect a, Rect b) => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
                double Needed(Border tag)
                {
                    var label = (TextBlock)tag.Child;
                    var probe = new TextBlock { Text = label.Text, FontSize = label.FontSize, FontWeight = label.FontWeight, FontFamily = label.FontFamily };
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    return Math.Ceiling(probe.DesiredSize.Width) + 10;
                }
                var keyboardSlot = connectedPorts["demo/root/3"]; var emptySlot = portSlots["demo/root/7"];
                var socket = Rect.Union(Area(keyboardSlot), Area(emptySlot));
                var host = cards[CardNode(nodeParents["demo/root/7"]).Id].Card;
                var hostArea = Area(host);

                // Bring the socket into view without selecting it, so the preview shows the tag at rest.
                selected = Node("demo/root/7"); RevealSelection(); selected = null; UpdateLayout();
                CaptureUi($"port-tag-rest-{layout}-preview.png");
                // At rest the tag spans the whole socket, both halves, however long its name.
                var span = TagFor(spanName); var rest = Area(span);
                Check(((TextBlock)span.Child).TextTrimming == TextTrimming.CharacterEllipsis, $"A tag at rest trims its label to fit ({layout}).");
                if (horizontal)
                    Check(Math.Abs(rest.Top - socket.Top) < 0.5 && Math.Abs(rest.Bottom - socket.Bottom) < 0.5 && rest.Right <= socket.Left - 2, $"A named half's tag must span both halves of its socket, to the socket's left ({layout}): tag {rest}, socket {socket}.");
                else
                    Check(Math.Abs(rest.Left - socket.Left) < 0.5 && Math.Abs(rest.Right - socket.Right) < 0.5 && rest.Bottom <= socket.Top - 2, $"A named half's tag must span both halves of its socket, above it ({layout}): tag {rest}, socket {socket}.");

                // Pointing at the empty half, then at the keyboard in the other, shows the whole name.
                void Shows(Border tag, string what)
                {
                    UpdateLayout();
                    var area = Area(tag); var label = (TextBlock)tag.Child;
                    Check(label.TextTrimming == TextTrimming.None && area.Width >= Needed(tag) - 0.5, $"{what} must show its whole label ({layout}): {area.Width:0.#} px wide for {Needed(tag):0.#} px of text.");
                    Check(Panel.GetZIndex(tag) > Panel.GetZIndex(emptySlot), $"{what} must sit above the sockets ({layout}).");
                }
                void Rests(Border tag, string what)
                {
                    UpdateLayout();
                    Check(((TextBlock)tag.Child).TextTrimming == TextTrimming.CharacterEllipsis && Same(Area(tag), rest), $"{what} must return to its resting size and place ({layout}): now {Area(tag)}, was {rest}.");
                }
                HoverPort("demo/root/7", true); Shows(TagFor(spanName), "Pointing at the empty half's socket");
                var open = Area(TagFor(spanName));
                Check(open.Width > rest.Width - 0.5 && open.Left >= hostArea.Left + 3.5 && open.Right <= hostArea.Right - 3.5, $"A revealed tag must stay inside its card ({layout}): {open}, card {hostArea}.");
                if (!horizontal) Check(Math.Abs(open.Bottom - rest.Bottom) < 0.5, $"A revealed tag above a socket must keep its bottom edge: {open}, was {rest}.");
                else Check(Math.Abs(open.Right - rest.Right) < 0.5 && Math.Abs(open.Top + open.Height / 2 - (rest.Top + rest.Height / 2)) < 0.5, $"A revealed tag beside a socket must keep its right edge and its middle: {open}, was {rest}.");
                CaptureUi($"port-tag-reveal-{layout}-preview.png");
                FoldHoveredTag(); Rests(TagFor(spanName), "Moving off the socket");
                HoverPort("demo/root/3", true); Shows(TagFor(spanName), "Pointing at the keyboard in the other half");
                FoldHoveredTag(); Rests(TagFor(spanName), "Moving off the keyboard");
                SelectNode(Node("demo/root/3")); Shows(TagFor(spanName), "Selecting the keyboard in the other half");
                SelectNode(Node("demo/root/7")); Shows(TagFor(spanName), "Selecting the empty half");
                SelectNode(Node("demo/root/4")); Rests(TagFor(spanName), "Selecting an unrelated device");

                // A tag on a device's card grows from its place on the card's top border.
                var audioCard = cards["demo/root/2"].Card; var onCard = TagFor(cardName); var onCardRest = Area(onCard);
                HoverPort("demo/root/2", true); UpdateLayout();
                var shown = Area(TagFor(cardName));
                Shows(TagFor(cardName), "Pointing at the device whose port is named");
                Check(Math.Abs(shown.Top + shown.Height / 2 - (onCardRest.Top + onCardRest.Height / 2)) < 0.5 && shown.Right <= Canvas.GetLeft(audioCard) + audioCard.Width + 0.5 && shown.Width > onCardRest.Width, $"A revealed card tag must grow along the card's top border and stay inside the card ({layout}): {shown}, was {onCardRest}.");
                FoldHoveredTag(); UpdateLayout();
                Check(Same(Area(TagFor(cardName)), onCardRest), $"A card tag must return to its resting size and place ({layout}).");

                // A name too long for one line wraps inside its card rather than being trimmed or running off it.
                var longRest = Area(TagFor(longName));
                HoverPort("demo/root/8", true); UpdateLayout();
                var longOpen = Area(TagFor(longName));
                Check(((TextBlock)TagFor(longName).Child).TextTrimming == TextTrimming.None && ((TextBlock)TagFor(longName).Child).ActualHeight > 20 && longOpen.Left >= hostArea.Left + 3.5 && longOpen.Right <= hostArea.Right - 3.5 && longOpen.Width <= TagRevealWidth + 0.5,
                    $"A long name must wrap whole inside its card ({layout}): {longOpen}, was {longRest}, card {hostArea}.");
                CaptureUi($"port-tag-long-{layout}-preview.png");
                FoldHoveredTag(); UpdateLayout();
                Check(Same(Area(TagFor(longName)), longRest), $"A wrapped tag must return to its resting size and place ({layout}).");
            }
        }
        finally
        {
            snapshot = savedSnapshot; selected = savedSelection; deviceLabels = savedLabels; horizontalTree = savedHorizontal;
            Draw(); ShowDetails(); UpdateIssues();
            System.IO.Directory.Delete(directory, true);
        }
    }
}

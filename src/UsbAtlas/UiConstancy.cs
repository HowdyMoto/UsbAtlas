using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UsbAtlas;

// When a scan changes what's plugged in, the eye keeps its place: cards that stay slide from where they
// were to where they are now, their sockets with them, new cards fade in, removed cards fade out where
// they stood, and connections fade in once the cards have landed. Cards are matched by node, so a
// device keeps its card across scans. Windows' animation setting turns all of it off.
public partial class MainWindow
{
    private const string RemovedCardTag = "removed-card";
    private static readonly TimeSpan MoveTime = TimeSpan.FromMilliseconds(260), WireFadeTime = TimeSpan.FromMilliseconds(180);
    private Dictionary<string, (Border Card, Point Point)> CardPlaces() => cards.ToDictionary(c => c.Key, c => c.Value);
    private void AnimateChange(Dictionary<string, (Border Card, Point Point)> before)
    {
        // A rescan whose differences don't move, add or remove a card (a counter or timestamp) draws as is.
        if (!SystemParameters.ClientAreaAnimation || before.Count == 0
            || cards.Count == before.Count && cards.All(c => before.TryGetValue(c.Key, out var old) && (old.Point - c.Value.Point).Length < 0.5)) return;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Run(double from, double to, TimeSpan length, TimeSpan delay = default) => new(from, to, length) { BeginTime = delay, EasingFunction = ease };
        var shifts = new Dictionary<string, TranslateTransform>();
        foreach (var (id, item) in cards)
        {
            if (!before.TryGetValue(id, out var old)) { item.Card.BeginAnimation(OpacityProperty, Run(0, 1, MoveTime)); continue; }
            var delta = old.Point - item.Point;
            if (delta.Length < 0.5) continue;
            var shift = new TranslateTransform(delta.X, delta.Y);
            shift.BeginAnimation(TranslateTransform.XProperty, Run(delta.X, 0, MoveTime));
            shift.BeginAnimation(TranslateTransform.YProperty, Run(delta.Y, 0, MoveTime));
            item.Card.RenderTransform = shifts[id] = shift;
        }
        // Sockets are drawn beside their card, so they ride its slide.
        foreach (var (id, socket) in portSlots.Concat(connectedPorts))
            if (nodeParents.TryGetValue(id, out var hub) && shifts.TryGetValue(DrawnAs(hub).Id, out var shift)) socket.RenderTransform = shift;
        foreach (var (id, old) in before.Where(b => !cards.ContainsKey(b.Key)))
        {
            // The removed card's own element, no longer on the canvas, fades where it stood.
            var ghost = old.Card;
            if (ghost.Parent is Panel holder) holder.Children.Remove(ghost);
            ghost.IsHitTestVisible = false; ghost.Focusable = false; ghost.Tag = RemovedCardTag; ghost.RenderTransform = null; ghost.Effect = null;
            Canvas.SetLeft(ghost, old.Point.X); Canvas.SetTop(ghost, old.Point.Y); Panel.SetZIndex(ghost, 0);
            Graph.Children.Add(ghost);
            var fade = Run(1, 0, MoveTime);
            fade.Completed += (_, _) => Graph.Children.Remove(ghost);
            ghost.BeginAnimation(OpacityProperty, fade);
        }
        // A delayed animation leaves its property at the base value until it begins, so connections start hidden.
        foreach (var line in wires.Values.Cast<System.Windows.Shapes.Shape>().Concat(Graph.Children.OfType<System.Windows.Shapes.Line>().Where(l => Equals(l.Tag, MissingUsb3Tag))))
        {
            line.Opacity = 0;
            line.BeginAnimation(OpacityProperty, Run(0, 1, WireFadeTime, MoveTime));
        }
    }

    // Removing a device slides the cards after it into its place, fades it out where it was, and brings the
    // connections back once the cards land; with animations off, the change is drawn at once.
    private async Task VerifyObjectConstancy()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var savedSnapshot = snapshot; var savedSelection = selected;
        try
        {
            snapshot = DemoData.Create(); selected = null; detail = CardDetail.Full; Draw(); UpdateLayout();
            var before = CardPlaces();
            var removed = snapshot.Nodes.First(n => n.Id == "demo/root/2");
            var host = snapshot.Nodes.First(n => n.Kind == "Root hub");
            host.Children.Remove(removed);
            Draw(); AnimateChange(before); UpdateLayout();
            var moved = cards.Where(c => before.TryGetValue(c.Key, out var old) && (old.Point - c.Value.Point).Length >= 0.5).Select(c => c.Key).ToList();
            Check(moved.Count > 0, "Removing a device must move the cards after it.");
            var ghosts = Graph.Children.OfType<Border>().Where(b => Equals(b.Tag, RemovedCardTag)).ToList();
            if (!SystemParameters.ClientAreaAnimation)
            {
                Check(ghosts.Count == 0 && cards.Values.All(c => c.Card.RenderTransform is not TranslateTransform), "With animations off, a change must be drawn at once.");
                return;
            }
            Check(ghosts.Count == 1 && !ghosts[0].IsHitTestVisible && ghosts[0].HasAnimatedProperties, "A removed device's card must fade out where it stood.");
            Check(moved.All(id => cards[id].Card.RenderTransform is TranslateTransform { HasAnimatedProperties: true }), "Cards that stay must slide from their old places.");
            Check(wires.Values.All(w => w.HasAnimatedProperties && w.Opacity == 0), "Connections must stay hidden while the cards slide, then fade in.");
            await Task.Delay(MoveTime + WireFadeTime + TimeSpan.FromMilliseconds(250));
            Check(wires.Values.All(w => w.Opacity == 1), "Connections must be fully drawn once they've faded in.");
            Check(!Graph.Children.OfType<Border>().Any(b => Equals(b.Tag, RemovedCardTag)), "A removed card must leave the canvas after fading.");
            Check(moved.All(id => cards[id].Card.RenderTransform is TranslateTransform { X: 0, Y: 0 }), "Cards must land where they are drawn.");
            // A rescan that moves, adds and removes nothing redraws without animating.
            before = CardPlaces(); Draw(); AnimateChange(before);
            Check(!Graph.Children.OfType<Border>().Any(b => Equals(b.Tag, RemovedCardTag)) && wires.Values.All(w => !w.HasAnimatedProperties && w.Opacity == 1), "A rescan that changes nothing on the canvas must not animate.");
        }
        finally { snapshot = savedSnapshot; selected = savedSelection; Draw(); ShowDetails(); }
    }
}

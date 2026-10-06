using System.Windows;
using System.IO;

namespace UsbAtlas;
public partial class MainWindow
{
    private void VerifyHubSnapping()
    {
        var old = snapshot; var selection = selected; var focus = focusedBranch; bool horizontal = horizontalTree;
        string file = Path.Combine(Path.GetTempPath(), "usb-atlas-ports-" + Guid.NewGuid() + ".json");
        try
        {
            var host = new UsbNode { Id = "demo/snap", Kind = "Controller", Name = "USB host" };
            var hubs = Enumerable.Range(1, 3).Select(i => new UsbNode { Id = "demo/stage" + i, Kind = "Hub", Name = "Realtek stage " + i, Port = i == 1 ? 1 : 4, PortCount = 4, LinkMbps = 5000 }).ToList();
            host.Children.Add(hubs[0]);
            for (int i = 0; i < 3; i++)
            {
                // Mixed link rates and one slow link, so each connection can be seen to show its own.
                for (int p = 1; p <= (i == 2 ? 4 : 3); p++) hubs[i].Children.Add(new UsbNode { Id = hubs[i].Id + "/" + p, Port = p, Name = "Device " + (i * 3 + p), PortConnectorIsTypeC = p == 1,
                    LinkMbps = p switch { 1 => 12, 2 => 480, _ => 5000 }, UsbVersion = p == 2 ? "USB 3.20" : "", SpeedLimited = p == 2 });
                if (i < 2) hubs[i].Children.Add(hubs[i + 1]);
            }
            snapshot = new Snapshot { IsDemo = true, Controllers = [host] }; focusedBranch = null; horizontalTree = false;
            var store = new DeviceLabels(file);
            if (!store.TrySetPort(hubs[0].Children[0], snapshot, "Microphone", out var error) || !store.TrySetSnap(hubs[1], snapshot, true, out error) || !store.TrySetSnap(hubs[2], snapshot, true, out error)) throw new Exception(error);
            hubs[0].Children[0].Name = "Replacement device";
            new DeviceLabels(file).Apply(snapshot);
            if (hubs[0].Children[0].PortLabel != "Microphone" || !hubs[2].SnapToParentHub || hubs[0].Children[0].UserLabel.Length != 0) throw new Exception("Socket names and hub links must persist independently of device names.");
            Draw(); SelectNode(hubs[0]);
            // Linked stages sit side by side at every level that draws sockets; a far view shows the real hierarchy.
            foreach (var level in new[] { CardDetail.Compact, CardDetail.Full })
            {
                detail = level; Draw(); UpdateLayout();
                if (hubs.Any(h => Math.Abs(cards[h.Id].Point.Y - cards[hubs[0].Id].Point.Y) > .01) || cards[hubs[2].Id].Point.X <= cards[hubs[1].Id].Point.X) throw new Exception($"Linked stages must sit side by side in {level} cards.");
                VerifyWireRouting();
                // The stages sit close together, with room only for the links between them, and every stage's
                // devices share one row beneath the frame in stage and then socket order. Each device has its own
                // connection from its own socket into the top of its card, turning only below the frame.
                if (hubs.Zip(hubs.Skip(1)).Any(p => cards[p.Second.Id].Point.X - (cards[p.First.Id].Point.X + cards[p.First.Id].Card.Width) > 48)) throw new Exception($"Linked stages must sit close together in {level} cards.");
                var frame = Graph.Children.OfType<System.Windows.Controls.Border>().Single(b => Equals(b.Tag, StageFrameTag));
                double frameBottom = System.Windows.Controls.Canvas.GetTop(frame) + frame.Height;
                var row = hubs.SelectMany(h => h.Children.Where(c => !c.SnapToParentHub)).ToList();
                if (row.Zip(row.Skip(1)).Any(p => cards[p.First.Id].Point.X >= cards[p.Second.Id].Point.X) || row.Any(c => Math.Abs(cards[c.Id].Point.Y - cards[row[0].Id].Point.Y) > .01 || cards[c.Id].Point.Y <= frameBottom))
                    throw new Exception($"Linked stages' devices must share one row below the frame, in stage and socket order, in {level} cards.");
                if (row.Any(c => !portAnchors.TryGetValue(c.Id, out var port) || (wireRoutes[c.Id][0] - port).Length > .01 || Math.Abs(wireRoutes[c.Id][^1].Y - cards[c.Id].Point.Y) > .01 || wireRoutes[c.Id].Count > 2 && wireRoutes[c.Id][1].Y <= frameBottom))
                    throw new Exception($"Each device must have its own connection from its socket to the top of its card, turning below the frame, in {level} cards.");
                if (hubs.Skip(1).Any(h => wireRoutes[h.Id][1].Y - wireRoutes[h.Id][0].Y > 10.01 || Math.Abs(wireRoutes[h.Id][^1].Y - (cards[h.Id].Point.Y + cards[h.Id].Card.Height - NodeVisuals.SocketHeight / 2)) > .01)) throw new Exception($"Each link between stages must step just below its socket into the next stage, level with its sockets, in {level} cards.");
                // Long side-by-side runs keep the widest links visibly apart.
                if (row.Select(c => wireRoutes[c.Id]).Where(r => r.Count > 2).Select(r => r[1].Y).Distinct().Order().Zip(row.Select(c => wireRoutes[c.Id]).Where(r => r.Count > 2).Select(r => r[1].Y).Distinct().Order().Skip(1)).Any(p => p.Second - p.First < TopologyLayout.RowLaneSpacing - .01)) throw new Exception($"The row's turning lanes must sit {TopologyLayout.RowLaneSpacing} apart in {level} cards.");
                var deep = hubs[2].Children[1]; SelectNode(deep);
                if (wires[deep.Id].Stroke != Brush("Accent") || wires[hubs[2].Id].Stroke != Brush("Accent")) throw new Exception($"Selecting a device must light its connection and the links that lead to it in {level} cards.");
                if (level == CardDetail.Full) { ResetPan(); SetZoom(1); UpdateLayout(); CaptureUi("snapped-row-selected.png"); }
                SelectNode(hubs[0]);
            }
            ResetPan(); SetZoom(Math.Min(1, FitScale())); UpdateLayout(); CaptureUi("snapped-hubs-preview.png");
            if (!Details.Children.OfType<System.Windows.Controls.Button>().Any(b => Equals(b.Tag, "select-hub-stage"))) throw new Exception("The first hub must expose downstream stage arrangement.");
            SelectNode(hubs[1]); UpdateLayout();
            var snapButton = Details.Children.OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Tag, "snap-upstream-hub"));
            if (snapButton.TranslatePoint(new Point(), Details).Y > 240) throw new Exception("Hub snapping must be near the top of Properties.");
            CaptureUi("snap-control-preview.png");
            horizontalTree = true; Draw(); VerifyWireRouting();
            horizontalTree = false;
            if (!store.TrySetSnap(hubs[2], snapshot, false, out error)) throw new Exception(error);
            Draw();
            if (hubs[2].SnapToParentHub || hubs[1].Children.Last() != hubs[2]) throw new Exception("Unlinking must retain the real USB hierarchy.");
        }
        finally { snapshot = old; selected = selection; focusedBranch = focus; horizontalTree = horizontal; detail = CardDetail.Full; File.Delete(file); Draw(); ShowDetails(); }
    }
}

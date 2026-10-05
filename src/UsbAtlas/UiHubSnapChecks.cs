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
            var hubs = Enumerable.Range(1, 3).Select(i => new UsbNode { Id = "demo/stage" + i, Kind = "Hub", Name = "Realtek stage " + i, Port = i == 1 ? 1 : 4, PortCount = 4 }).ToList();
            host.Children.Add(hubs[0]);
            for (int i = 0; i < 3; i++)
            {
                for (int p = 1; p <= (i == 2 ? 4 : 3); p++) hubs[i].Children.Add(new UsbNode { Id = hubs[i].Id + "/" + p, Port = p, Name = "Device " + (i * 3 + p), PortConnectorIsTypeC = p == 1 });
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

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace UsbAtlas;
public partial class MainWindow
{
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = Brush("Surface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("Border") };
        void Add(string header, string tip, Action action)
        {
            var item = new MenuItem { Header = header, ToolTip = tip };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Image (PNG)…", "The whole topology as drawn, with what to fix first and the legend, to share in a support thread or a video. Cards carry no serial numbers.", SaveImage);
        Add("Copy image", "The same image, on the clipboard.", CopyImage);
        Add("Snapshot (JSON)…", "Everything USB Atlas read, including serial numbers, for atlascli --input or a later diff.", SaveSnapshot);
        menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }

    private void SaveSnapshot()
    {
        var dialog = new SaveFileDialog { Filter = "JSON snapshot|*.json", FileName = $"usb-atlas-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })); StatusText.Text = "Snapshot exported to " + dialog.FileName; }
        catch (Exception ex) { StatusText.Text = "Export failed: " + ex.Message; }
    }

    private void SaveImage()
    {
        var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"usb-atlas-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog() != true) return;
        try { SavePng(TopologyImage(), dialog.FileName); StatusText.Text = "Image saved to " + dialog.FileName; }
        catch (Exception ex) { StatusText.Text = "Image export failed: " + ex.Message; }
    }
    private void CopyImage()
    {
        try { Clipboard.SetImage(TopologyImage()); StatusText.Text = "Image copied."; }
        catch (Exception ex) { StatusText.Text = "Clipboard unavailable: " + ex.Message; }
    }
    private static void SavePng(BitmapSource image, string file)
    {
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file); png.Save(stream);
    }

    // The whole topology as it is drawn now (the same cards, folds and focus), not just what's in view, so it
    // reads on its own: a title with when it was scanned and what was found, what to fix first, the graph and
    // the legend, on the canvas's background. Drawn at twice its size for sharp text, smaller for a huge graph.
    // Cards carry no serial numbers; Properties, which does, isn't in it.
    private BitmapSource TopologyImage()
    {
        const double pad = 24, gap = 14;
        double zoom = GraphScale.ScaleX;
        GraphScale.ScaleX = GraphScale.ScaleY = 1; UpdateLayout();
        try
        {
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Typeface Face(FontWeight weight) => new((FontFamily)FindResource("UiFont"), FontStyles.Normal, weight, FontStretches.Normal);
            FormattedText Text(string text, double size, string ink, FontWeight weight) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face(weight), size, Brush(ink), dip);

            var (_, _, _, summary) = IssueCounts();
            int hubs = snapshot.Nodes.Count(n => n.Kind == "Hub"), devices = snapshot.Nodes.Count(n => n.Kind == "Device");
            var lines = new List<FormattedText>
            {
                Text("USB Atlas", 18, "TextPrimary", FontWeights.SemiBold),
                Text($"{(snapshot.IsDemo ? "Sample topology · " : "Scanned ")}{snapshot.CapturedAt:f} · {Count(snapshot.Controllers.Count, "host controller")}, {Count(hubs, "hub")}, {Count(devices, "device")} · {(summary.Length > 0 ? summary : "No issues")}", 12, "TextSecondary", FontWeights.Normal)
            };
            var fixes = Triage.FixFirst(snapshot);
            if (fixes.Count > 0) lines.Add(Text("FIX FIRST", 11, "TextMuted", FontWeights.SemiBold));
            foreach (var f in fixes)
            {
                string where = NodeVisuals.ShortName(CardNode(f.Node));
                var line = Text($"{f.Issue} · {where} — {f.Fix}", 12, "TextSecondary", FontWeights.Normal);
                line.SetForegroundBrush(Brush(NodeVisuals.StatusColor(f.Severity)), 0, f.Issue.Length);
                line.SetFontWeight(FontWeights.SemiBold, 0, f.Issue.Length + 3 + where.Length);
                lines.Add(line);
            }
            double width = Math.Max(Graph.Width, SocketLegend.ActualWidth);
            foreach (var text in lines) text.MaxTextWidth = Math.Max(width, 480);
            width = Math.Max(width, lines.Max(l => l.WidthIncludingTrailingWhitespace));
            double header = lines.Sum(l => l.Height + 4);
            double height = pad + header + gap + Graph.Height + gap + SocketLegend.ActualHeight + pad;
            width += 2 * pad;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brush("CanvasSurface"), null, new Rect(0, 0, width, height));
                double y = pad;
                foreach (var text in lines) { dc.DrawText(text, new Point(pad, y)); y += text.Height + 4; }
                y += gap;
                dc.DrawRectangle(Painted(Graph, Graph.Width, Graph.Height), null, new Rect(pad, y, Graph.Width, Graph.Height));
                y += Graph.Height + gap;
                if (SocketLegend.ActualWidth > 0) dc.DrawRectangle(Painted(SocketLegend, SocketLegend.ActualWidth, SocketLegend.ActualHeight), null, new Rect(pad, y, SocketLegend.ActualWidth, SocketLegend.ActualHeight));
            }
            double scale = Math.Min(2, 8000 / Math.Max(width, height));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze();
            return bitmap;
        }
        finally { GraphScale.ScaleX = GraphScale.ScaleY = zoom; UpdateLayout(); }
    }
    // An element drawn in its own coordinates, wherever it sits on screen.
    private static VisualBrush Painted(Visual element, double width, double height) => new(element)
    {
        Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
        ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height)
    };
}

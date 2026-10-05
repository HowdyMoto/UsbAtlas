using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsbAtlas;

internal static class NodeVisuals
{
    // Color says what a device does. Hubs, hosts and ports stay neutral so devices stand out, and
    // related types share a hue (the icon tells them apart) because pale fills need wide hue gaps.
    internal static string Color(UsbNode n) => n.Kind != "Device" ? "Neutral" : n.DeviceType switch
    {
        "Keyboard" or "Mouse" or "HID / controls" => "Input",
        "Game controller" or "VR headset" => "Gaming",
        "Audio" => "Audio",
        "Camera / video" or "Billboard" => "Video",
        var type when DeviceIdentity.IsStorage(type) => "Storage",
        "Wireless" or "Serial / communications" or "Printer" => "Connectivity",
        _ => "Neutral"
    };
    internal static readonly string[] Categories = ["Neutral", "Input", "Gaming", "Audio", "Video", "Storage", "Connectivity"];
    // A card's fill and outline share its icon and label hue.
    internal static string Fill(UsbNode n) => Color(n) + "Fill";
    internal static string Edge(UsbNode n) => Color(n) + "Edge";
    internal static string Label(UsbNode n) => n.Kind switch
    {
        "Controller" => "Host controller", "Root hub" => "Root ports",
        "Hub" when n.Location == "Internal" => "Internal hub · inferred",
        "Hub" when n.Location == "External" => "External hub · inferred",
        "Hub" => "Hub · location unknown", "Empty port" => "Empty port", "Unavailable" => "USB port",
        _ => n.DeviceType
    };
    internal static Brush Ink(string hex) => Theme.Brush(hex);

    // Every icon is a Google Material Symbol drawn in one ink at the requested size.
    internal static Viewbox Symbol(string name, Brush ink, double size)
    {
        var canvas = new Canvas { Width = MaterialSymbols.DesignSize, Height = MaterialSymbols.DesignSize };
        canvas.Children.Add(new Path { Data = MaterialSymbols.Shape(name), Fill = ink });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    // Warnings and errors share one look everywhere: a shape-coded glyph (filled triangle for a warning,
    // filled circle for an error) beside semibold text in a color nothing else uses, usually on a tinted pill.
    internal enum Severity { Warning, Error }
    internal const string StatusGlyphTag = "status-glyph";
    internal static string StatusColor(Severity s) => s == Severity.Error ? "Error" : "Warning";
    internal static FrameworkElement StatusGlyph(Severity s, double size = 13)
    {
        var glyph = Symbol(s == Severity.Error ? "error" : "warning", Ink(StatusColor(s)), size);
        glyph.Tag = StatusGlyphTag; glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }
    // Glyph and text without a surface, for hosts that provide their own (such as a button).
    internal static FrameworkElement StatusContent(Severity s, string text)
    {
        var row = new DockPanel();
        var glyph = StatusGlyph(s); glyph.Margin = new Thickness(0, 1, 5, 0); glyph.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(glyph, Dock.Left); row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ink(StatusColor(s)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
    internal static Border StatusBadge(Severity s, string text) => new()
    {
        Background = Ink(s == Severity.Error ? "ErrorSurface" : "WarningSurface"), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = StatusContent(s, text)
    };
    // Metric glyphs mark what a number measures: swap_vert for the negotiated link, schedule for bus
    // time a device reserves, and bolt for the power it requests. Neutral ink; never a status color.
    // Material Symbols leave a margin inside their square, so the glyph is drawn a little larger than the text.
    internal enum Metric { Link, Reserved, Power }
    internal const string MetricGlyphTag = "metric-glyph";
    internal static FrameworkElement MetricGlyph(Metric m, double size = 11)
    {
        var glyph = Symbol(m switch { Metric.Link => "swap_vert", Metric.Reserved => "schedule", _ => "bolt" }, Ink("TextSecondary"), Math.Round(size * 1.2));
        glyph.Tag = MetricGlyphTag; glyph.Uid = m.ToString();
        return glyph;
    }
    // A thin track for a link, filled by the share of it that reservations hold. The fill is a tinted
    // segment so measured traffic can later be drawn as a solid layer in the same track.
    internal const string LinkBarTag = "link-bar";
    // The track is an empty pipe, surface-colored inside a thin outline, so the unfilled part reads as
    // empty on every card tint and theme. On a tinted card, pass its edge color as the outline.
    internal static Border LinkBar(double fraction, string outline = "Divider")
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction > 0) fraction = Math.Max(fraction, 0.015);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = Ink("Accent"), CornerRadius = new CornerRadius(2) });
        return new Border { Height = 7, CornerRadius = new CornerRadius(3.5), Background = Ink("Surface"), BorderBrush = Ink(outline), BorderThickness = new Thickness(1), Child = grid, Tag = LinkBarTag, ClipToBounds = true };
    }
    // One trimming line of metrics separated by middle dots; each value may carry its glyph.
    internal static TextBlock MetricLine(IEnumerable<(Metric? Glyph, string Text)> parts, double fontSize = 11)
    {
        var line = new TextBlock { FontSize = fontSize, Foreground = Ink("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (var (glyph, text) in parts)
        {
            if (line.Inlines.Count > 0) line.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            if (glyph is Metric m)
            {
                var icon = MetricGlyph(m, fontSize); icon.Margin = new Thickness(0, 0, 3, 0);
                line.Inlines.Add(new System.Windows.Documents.InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
            }
            line.Inlines.Add(new System.Windows.Documents.Run(text));
        }
        return line;
    }
    // The Material Symbol for each kind of node and device type. Google has no USB-stick symbol, so
    // flash drives use the USB trident.
    internal static string SymbolName(UsbNode n) => n.Kind switch
    {
        "Controller" or "Root hub" => "developer_board",
        "Hub" => "device_hub",
        "Empty port" => "usb",
        "Unavailable" => "usb_off",
        _ => n.DeviceType switch
        {
            "Keyboard" => "keyboard", "Mouse" => "mouse", "HID / controls" => "tune",
            "Game controller" => "sports_esports", "VR headset" => "head_mounted_device",
            "Audio" => "headphones", "Camera / video" => "videocam", "Billboard" => "display_external_input",
            "External drive" or "Storage" => "hard_drive", "Optical drive" => "album", "Card reader" => "sd_card",
            "Flash drive" => "usb", "Floppy drive" => "save",
            "Wireless" => "bluetooth", "Serial / communications" => "cable", "Printer" => "print",
            _ => "devices_other"
        }
    };
    internal static FrameworkElement Icon(UsbNode n, double size = 36) => Symbol(SymbolName(n), Ink(Color(n)), size);

    // Card titles drop corporate suffixes and driver boilerplate so the model reads first; the full name
    // stays in the tooltip, tree and inspector. A custom label is shown as typed.
    internal static string ShortName(UsbNode n)
    {
        if (n.UserLabel.Length > 0) return n.UserLabel;
        var name = n.Name.Replace('_', ' ');
        name = Regex.Replace(name, @"\s+-\s+\d+(\.\d+)*(?=\s*(\(Microsoft\))?\s*$)", "");
        name = Regex.Replace(name, @"\s*\(Microsoft\)\s*$", "");
        name = Regex.Replace(name, @"eXtensible Host Controller", "xHCI", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @",?\s+(Inc|Incorporated|Corp|Corporation|Co|Ltd|Limited|LLC|GmbH)\.?(?=[\s,]|$)", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+(Semiconductor|Technology|Technologies|Tech|Electronics|International|Systems)\.?(?=\s|$)", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', ',');
        return name.Length > 0 ? name : n.Name;
    }

    // A labeled meter for the bus time a link has reserved: solid for what is held now, lighter out to
    // the most it could hold, inside an outlined surface track that reads as empty on any tint. The label
    // is drawn twice, dark over the track and light clipped to the solid fill, so it stays legible
    // wherever the fill ends. The labels sit outside the column grid so their width never moves the fill.
    internal const string MeterTag = "link-meter";
    internal static Border Meter(double now, double peak, double capacity, string label, string outline)
    {
        double solid = Math.Clamp(now / capacity, 0, 1), reach = Math.Clamp(peak / capacity, 0, 1);
        if (solid > 0) solid = Math.Max(solid, 0.015);
        reach = Math.Max(reach, solid);
        var bars = new Grid();
        foreach (var share in new[] { solid, reach - solid, 1 - reach })
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        var fill = new Border { Background = Ink("Accent") }; bars.Children.Add(fill);
        var possible = new Border { Background = Ink("Accent"), Opacity = 0.3 }; Grid.SetColumn(possible, 1); bars.Children.Add(possible);
        T Text<T>(T text, string ink) where T : TextBlock
        {
            text.Text = label; text.FontSize = 11; text.FontWeight = FontWeights.SemiBold; text.Foreground = Ink(ink);
            text.Margin = new Thickness(6, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Left;
            return text;
        }
        var light = Text(new UnspokenText { Clip = Geometry.Empty }, "OnAccent");
        fill.SizeChanged += (_, e) => light.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 6), 40));
        var layers = new Grid { Children = { bars, Text(new TextBlock(), "TextPrimary"), light } };
        return new Border { Height = 18, CornerRadius = new CornerRadius(3), Background = Ink("Surface"), BorderBrush = Ink(outline), BorderThickness = new Thickness(1), Child = layers, Tag = MeterTag, ClipToBounds = true };
    }

    // A second drawing of text that is already read aloud, kept out of the accessibility tree.
    private sealed class UnspokenText : TextBlock
    {
        protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
    }

    // A port is drawn as its socket: a USB-A shell with its tongue along the top, a USB-C pill with its
    // tongue centered, or a plain slot for a built-in port with no socket. The tongue carries USB's color
    // code for the socket's speed, with the port number on it. Shell and cavity colors come from the
    // control, so the canvas can fill an occupied socket's cavity, as a plug would, and accent the selected path.
    internal const double SocketWidth = 30, SocketHeight = 22, TongueDepth = 13;
    internal static bool HasTongue(UsbNode port) => port.Connector is "USB-A" or "USB-C";
    internal static string SocketInk(UsbNode port) => port.SocketSpeed switch
    {
        "USB 2.0" => "SocketUsb2", "5 Gb/s" or "≥5 Gb/s" => "SocketSuperSpeed", "≥10 Gb/s" => "SocketSuperSpeedPlus", _ => "SocketUnknown"
    };
    internal static string SocketLabel(UsbNode port) => (port.Connector switch { "USB-A" or "USB-C" => port.Connector, "Internal" => "Built-in", _ => "Connector unknown" })
        + " · " + (port.SocketSpeed == "Not reported" ? "speed unknown" : port.SocketSpeed);
    // A USB 3 socket's USB 2 and USB 3 halves, when both are on one hub, are drawn as one socket split
    // at a seam: outer corners rounded, one divider, one tongue carrying both numbers. Each half is still
    // its own control, so it fills, selects and wires up on its own. Along a vertical tree's bottom edge
    // the halves sit side by side; down a horizontal tree's right edge they stack, and the socket stands
    // upright with its tongue down its long side.
    internal enum SocketPart { Whole, First, Second }
    internal static ControlTemplate SocketTemplate(UsbNode port, SocketPart part = SocketPart.Whole, bool down = false)
    {
        bool typeC = port.Connector == "USB-C", split = part != SocketPart.Whole, first = part == SocketPart.First, upright = split && down;
        // Corners run top-left, top-right, bottom-right, bottom-left; a half rounds only its outer end.
        CornerRadius Round(double r) => !split ? new(r) : down ? (first ? new(r, r, 0, 0) : new(0, 0, r, r)) : first ? new(r, 0, 0, r) : new(0, r, r, 0);
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, Round(typeC ? (upright ? SocketWidth : SocketHeight) / 2 : HasTongue(port) ? 2.5 : 3));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var root = chrome;
        if (HasTongue(port))
        {
            // The number may be taller than the tongue; let it overhang rather than be clipped.
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0, -4, 0, -4));
            // A ring in the card's color keeps the tongue distinct from a filled cavity, even an accent one;
            // between halves it is the seam line. A half's tongue runs to the seam, over the shell's divider.
            var tongue = new FrameworkElementFactory(typeof(Border), "Tongue");
            tongue.SetValue(Border.BackgroundProperty, Ink(SocketInk(port)));
            tongue.SetValue(Border.BorderBrushProperty, Ink("Surface"));
            tongue.SetValue(Border.BorderThicknessProperty, !first ? new Thickness(1) : down ? new Thickness(1, 1, 1, 0) : new Thickness(1, 1, 0, 1));
            double inset = typeC ? 4 : 2.5, start = part == SocketPart.Second ? 0 : inset, end = first ? 0 : inset;
            tongue.SetValue(Border.CornerRadiusProperty, Round(typeC ? (upright ? SocketWidth / 2 - inset : TongueDepth / 2) : 2));
            if (!upright)
            {
                tongue.SetValue(FrameworkElement.HeightProperty, TongueDepth);
                tongue.SetValue(FrameworkElement.MarginProperty, new Thickness(start, typeC ? 0 : 2.5, end, 0));
                tongue.SetValue(FrameworkElement.VerticalAlignmentProperty, typeC ? VerticalAlignment.Center : VerticalAlignment.Top);
            }
            else
            {
                // Upright, USB-C's tongue runs down the middle and USB-A's down its left side.
                if (!typeC) { tongue.SetValue(FrameworkElement.WidthProperty, SocketWidth - 10); tongue.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); }
                tongue.SetValue(FrameworkElement.MarginProperty, new Thickness(inset, start, typeC ? inset : 0, end));
            }
            tongue.AppendChild(presenter);
            root = new FrameworkElementFactory(typeof(Grid));
            root.AppendChild(chrome); root.AppendChild(tongue);
        }
        else chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ContentControl)) { VisualTree = root };
        foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty, Ink("Accent"), "Chrome"));
            template.Triggers.Add(trigger);
        }
        return template;
    }
    // A first half leaves its seam side to the second, which draws the one divider between them.
    internal static Thickness SocketBorder(SocketPart part, bool down, double width) => part != SocketPart.First ? new(width)
        : down ? new(width, width, width, width - 1) : new(width, width, width - 1, width);
    // The port number a socket shows: white on a tongue, otherwise in the slot's own ink.
    internal static TextBlock SocketNumber(UsbNode port)
    {
        var number = new TextBlock { Text = port.Port.ToString("00"), FontSize = HasTongue(port) ? 10.5 : 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (HasTongue(port)) number.Foreground = Ink("OnSocket");
        return number;
    }

    // The legend beneath the graph: each kind of socket, drawn small with the canvas's own templates.
    internal const string SocketLegendHelp = "Shape is the connector: USB-A is a rectangle with its tongue along the top, USB-C a pill with its tongue in the middle, and a built-in port with no socket a plain slot. Tongue color is speed: black USB 2, blue USB 3 at 5 Gb/s, red 10 Gb/s or faster. A socket split at a seam is one physical socket that Windows reports as two logical ports, USB 2 and USB 3. A filled socket is in use.";
    internal static IEnumerable<FrameworkElement> SocketLegend()
    {
        FrameworkElement Glyph(string connector, string speed, SocketPart part = SocketPart.Whole) => new ContentControl
        {
            Template = SocketTemplate(new UsbNode { Connector = connector, SocketSpeed = speed }, part), Width = SocketWidth, Height = SocketHeight, Focusable = false, IsHitTestVisible = false,
            Background = Ink("Surface"), BorderBrush = Ink("Wire"), BorderThickness = SocketBorder(part, false, 1)
        };
        FrameworkElement Entry(string label, params FrameworkElement[] glyphs)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var shapes = new StackPanel { Orientation = Orientation.Horizontal, LayoutTransform = new ScaleTransform(0.75, 0.75), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var glyph in glyphs) shapes.Children.Add(glyph);
            entry.Children.Add(shapes);
            entry.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ink("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
            return entry;
        }
        yield return Entry("USB 2", Glyph("USB-A", "USB 2.0"));
        yield return Entry("5 Gb/s", Glyph("USB-A", "≥5 Gb/s"));
        yield return Entry("10 Gb/s+", Glyph("USB-A", "≥10 Gb/s"));
        yield return Entry("USB-C", Glyph("USB-C", "≥5 Gb/s"));
        yield return Entry("2 ports, 1 socket", Glyph("USB-A", "≥5 Gb/s", SocketPart.First), Glyph("USB-A", "≥5 Gb/s", SocketPart.Second));
        yield return Entry("Built-in", Glyph("Internal", "USB 2.0"));
    }

    // The partner is the socket's other half when both are on the same hub.
    internal static FrameworkElement Connector(UsbNode n, UsbNode? partner = null)
    {
        bool host = n.Kind is "Controller" or "Root hub";
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement graphic;
        if (host)
        {
            var bus = new Canvas { Width = 32, Height = 18 };
            bus.Children.Add(new Path { Data = Geometry.Parse("M1,9 H30 M6,4 V14 M13,4 V14 M20,4 V14 M27,4 V14"), Stroke = Ink("Neutral"), StrokeThickness = 1.5 });
            graphic = bus;
        }
        else
        {
            // The same socket the hub's card shows, both halves of it when it has two: each cavity is
            // filled while something is plugged into that half.
            ContentControl Half(UsbNode port, SocketPart part) => new() { Template = SocketTemplate(port, part), Content = SocketNumber(port), Width = SocketWidth, Height = SocketHeight, Focusable = false, IsHitTestVisible = false,
                Background = Ink(port.Kind == "Empty port" ? "Surface" : "Wire"), BorderBrush = Ink("Wire"), BorderThickness = SocketBorder(part, false, 1) };
            if (partner == null) graphic = Half(n, SocketPart.Whole);
            else
            {
                var (first, second) = n.Port < partner.Port ? (n, partner) : (partner, n);
                graphic = new StackPanel { Orientation = Orientation.Horizontal, Children = { Half(first, SocketPart.First), Half(second, SocketPart.Second) } };
            }
        }
        graphic.Margin = new Thickness(0, 0, 7, 0);
        panel.Children.Add(graphic);
        panel.Children.Add(new TextBlock { Text = host ? "Host bus" : SocketLabel(n), FontSize = 12, Foreground = Ink(n.Connector == "Not reported" && !host ? "TextMuted" : "TextPrimary"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.ToolTip = host ? "Logical host connection; physical controller placement is not reported."
            : n.SocketEvidence + " The cable and device-end plug are not identified.";
        return panel;
    }
}

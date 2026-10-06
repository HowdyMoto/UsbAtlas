using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsbAtlas;

internal static class NodeVisuals
{
    // An icon's ink says what a device does. Hubs, hosts and ports stay neutral so devices stand out, and
    // related types share a hue (the icon's shape tells them apart). Cards themselves are neutral, so on
    // the canvas color is left to status, selection and the socket speed code.
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
    internal static string Label(UsbNode n) => Topology.Label(n);
    internal static Brush Ink(string hex) => Theme.Brush(hex);

    // Every icon is a Google Material Symbol drawn in one ink at the requested size.
    internal static Viewbox Symbol(string name, Brush ink, double size)
    {
        var canvas = new Canvas { Width = MaterialSymbols.DesignSize, Height = MaterialSymbols.DesignSize };
        canvas.Children.Add(new Path { Data = MaterialSymbols.Shape(name), Fill = ink });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    // Notes, warnings and errors share one look everywhere: a shape-coded glyph (outlined ring for a note,
    // filled triangle for a warning, filled circle for an error) beside semibold text, usually on a tinted
    // pill. A note is calm gray: worth knowing, but nothing is affected now. Amber and red are reserved for
    // warnings and errors, which nothing else uses.
    internal const string StatusGlyphTag = "status-glyph";
    internal static string StatusColor(Severity s) => s switch { Severity.Error => "Error", Severity.Warning => "Warning", _ => "Note" };
    internal static FrameworkElement StatusGlyph(Severity s, double size = 13)
    {
        var glyph = Symbol(s switch { Severity.Error => "error", Severity.Warning => "warning", _ => "info" }, Ink(StatusColor(s)), size);
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
        Background = Ink(StatusColor(s) + "Surface"), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = StatusContent(s, text)
    };
    // Metric glyphs mark what a number measures: swap_vert for the negotiated link, schedule for bus
    // time a device reserves, timer for how often an input device is polled, and bolt for the power it
    // requests. Neutral ink; never a status color.
    // Material Symbols leave a margin inside their square, so the glyph is drawn a little larger than the text.
    internal enum Metric { Link, Reserved, Polling, Power }
    internal const string MetricGlyphTag = "metric-glyph";
    internal static FrameworkElement MetricGlyph(Metric m, double size = 11)
    {
        var glyph = Symbol(m switch { Metric.Link => "swap_vert", Metric.Reserved => "schedule", Metric.Polling => "timer", _ => "bolt" }, Ink("TextSecondary"), Math.Round(size * 1.2));
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
    // A port Windows couldn't configure has its status appended to its name; the card's badge already says
    // it, so the title doesn't.
    internal static string ShortName(UsbNode n)
    {
        var name = Topology.ShortName(n);
        return n.Kind == "Unavailable" && n.UserLabel.Length == 0 && n.Status.Length > 0 && name.EndsWith(" · " + n.Status, StringComparison.Ordinal) ? name[..^(n.Status.Length + 3)] : name;
    }

    // A meter for the bus time a link has reserved, labeled above a thin bar so the label never sits on
    // what it describes. The bar is split into parts, one per device sharing the link (one part on a
    // device's own card): first each part's solid share for what it holds now, then each part's lighter
    // share out to the most it could hold, both in socket order, on a track that reads as empty.
    internal const string MeterTag = "link-meter", MeterLabelTag = "meter-label", MeterBarTag = "meter-bar";
    internal const double MeterPeakOpacity = 0.35;
    internal sealed record MeterPart(UsbNode Node, double Now, double Peak);
    internal static Grid Meter(IReadOnlyList<MeterPart> parts, double capacity, string label, out List<(UsbNode Node, Border Segment)> segments)
    {
        // A part holding anything now shows at least a sliver; peaks past what the link can reserve run to
        // the end of the track.
        var solids = parts.Select(p => p.Now <= 0 ? 0 : Math.Max(p.Now / capacity, 0.01)).ToList();
        double solidTotal = solids.Sum();
        if (solidTotal > 1) { solids = solids.Select(s => s / solidTotal).ToList(); solidTotal = 1; }
        var extras = parts.Select(p => Math.Max(0, p.Peak - p.Now) / capacity).ToList();
        double extraTotal = extras.Sum(), room = 1 - solidTotal;
        if (extraTotal > room) { extras = extras.Select(e => e * room / extraTotal).ToList(); extraTotal = room; }
        var bars = new Grid { Height = 5, ClipToBounds = true, Tag = MeterBarTag, VerticalAlignment = VerticalAlignment.Bottom };
        var track = new Border { Background = Ink("Divider"), CornerRadius = new CornerRadius(2.5) };
        Grid.SetColumnSpan(track, 2 * parts.Count + 1); bars.Children.Add(track);
        var drawn = new List<(UsbNode, Border)>();
        void Add(double share, UsbNode node, bool peak)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            if (share <= 0) return;
            var segment = new Border { Background = Ink("Neutral"), Opacity = peak ? MeterPeakOpacity : 1, ToolTip = node.DisplayName };
            Grid.SetColumn(segment, bars.ColumnDefinitions.Count - 1); bars.Children.Add(segment); drawn.Add((node, segment));
        }
        for (int i = 0; i < parts.Count; i++) Add(solids[i], parts[i].Node, false);
        for (int i = 0; i < parts.Count; i++) Add(extras[i], parts[i].Node, true);
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - solidTotal - extraTotal), GridUnitType.Star) });
        segments = drawn;
        var text = new TextBlock
        {
            Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ink("TextSecondary"), Height = 12, LineHeight = 12,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis, Tag = MeterLabelTag
        };
        return new Grid { Height = 18, Tag = MeterTag, Children = { text, bars } };
    }

    // A connection is drawn as its link: wider for a faster negotiated rate, so the widths step with USB's
    // generations, and dashed when the link runs slower than its device supports, amber once that holds
    // something back. Selection recolors a connection and never changes its width or dashes.
    internal static double WireWidth(UsbNode n) => (n.LinkMbps ?? (n.Speed.StartsWith("SuperSpeedPlus", StringComparison.Ordinal) ? 10000 : 0)) switch
    {
        >= 10000 => 4, >= 5000 => 3, >= 480 => 2, _ => 1.25
    };
    internal static bool SlowLink(UsbNode n) => HubRelationships.ReducedSpeed(n);
    internal static string WireInk(UsbNode n) => SlowLink(n) && Explanations.SpeedSeverity(n) == Severity.Warning ? "Warning" : "Wire";
    internal static DoubleCollection? WireDashes(UsbNode n) => SlowLink(n) ? [2.5, 1.5] : null;

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
    internal const string SocketLegendHelp = "Shape is the connector: USB-A is a rectangle with its tongue along the top, USB-C a pill with its tongue in the middle, and a built-in port with no socket a plain slot. Tongue color is speed: black USB 2, blue USB 3 at 5 Gb/s, red 10 Gb/s or faster. A socket split at a seam is one physical socket that Windows reports as two logical ports, USB 2 and USB 3. A filled socket is in use. A connection's width is its negotiated link rate, widest at 10 Gb/s and faster; a dashed connection runs slower than its device supports, amber when that holds something back.";
    internal const string LegendGroupTag = "legend-group";
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
        // Connections are drawn at full size, since their widths are what the entries show.
        FrameworkElement Link(string label, string ink, bool dashed, params double[] widths)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var width in widths)
                entry.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = 16, Y1 = 7, Y2 = 7, Width = 16, Height = 14, Margin = new Thickness(0, 0, 4, 0), Stroke = Ink(ink), StrokeThickness = width, StrokeDashArray = dashed ? [2.5, 1.5] : null, VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ink("TextSecondary"), Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return entry;
        }
        // Entries are grouped by what they show, and each group stays together when the legend wraps.
        FrameworkElement Group(string name, params FrameworkElement[] entries)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2), Tag = LegendGroupTag };
            group.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ink("TextMuted"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var entry in entries) group.Children.Add(entry);
            return group;
        }
        yield return Group("Speed", Entry("USB 2", Glyph("USB-A", "USB 2.0")), Entry("5 Gb/s", Glyph("USB-A", "≥5 Gb/s")), Entry("10 Gb/s+", Glyph("USB-A", "≥10 Gb/s")));
        yield return Group("Socket", Entry("USB-C", Glyph("USB-C", "≥5 Gb/s")), Entry("Split", Glyph("USB-A", "≥5 Gb/s", SocketPart.First), Glyph("USB-A", "≥5 Gb/s", SocketPart.Second)), Entry("Built-in", Glyph("Internal", "USB 2.0")));
        yield return Group("Link", Link("Rate", "Wire", false, WireWidth(new UsbNode { LinkMbps = 12 }), WireWidth(new UsbNode { LinkMbps = 480 }), WireWidth(new UsbNode { LinkMbps = 5000 }), WireWidth(new UsbNode { LinkMbps = 10000 })), Link("Slower than device", "Warning", true, 2));
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

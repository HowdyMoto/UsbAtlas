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
            text.Text = label; text.FontSize = 10; text.FontWeight = FontWeights.SemiBold; text.Foreground = Ink(ink);
            text.Margin = new Thickness(6, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Left;
            return text;
        }
        var light = Text(new UnspokenText { Clip = Geometry.Empty }, "OnAccent");
        fill.SizeChanged += (_, e) => light.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 6), 40));
        var layers = new Grid { Children = { bars, Text(new TextBlock(), "TextPrimary"), light } };
        return new Border { Height = 14, CornerRadius = new CornerRadius(3), Background = Ink("Surface"), BorderBrush = Ink(outline), BorderThickness = new Thickness(1), Child = layers, Tag = MeterTag, ClipToBounds = true };
    }

    // A second drawing of text that is already read aloud, kept out of the accessibility tree.
    private sealed class UnspokenText : TextBlock
    {
        protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
    }

    // A port socket: a slot (a pill for USB-C) with its number inside. Colors come from the button, so
    // the canvas can fill occupied sockets in the wire color and accent the selected path.
    internal static ControlTemplate SocketTemplate(bool typeC)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(typeC ? 8 : 3));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty, Ink("Accent"), "Chrome"));
            template.Triggers.Add(trigger);
        }
        return template;
    }

    internal static FrameworkElement Connector(UsbNode n)
    {
        bool host = n.Kind is "Controller" or "Root hub";
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var graphic = new Canvas { Width = 32, Height = 18, Margin = new Thickness(0, 0, 7, 0) };
        if (n.PortConnectorIsTypeC == true)
        {
            graphic.Children.Add(new Rectangle { Width = 30, Height = 14, RadiusX = 7, RadiusY = 7, Stroke = Ink("TextSecondary"), StrokeThickness = 1.6, Margin = new Thickness(0, 2, 0, 0) });
            graphic.Children.Add(new Rectangle { Width = 18, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Ink("TextSecondary"), Margin = new Thickness(6, 7.5, 0, 0) });
        }
        else if (host)
        {
            graphic.Children.Add(new Path { Data = Geometry.Parse("M1,9 H30 M6,4 V14 M13,4 V14 M20,4 V14 M27,4 V14"), Stroke = Ink("Neutral"), StrokeThickness = 1.5 });
        }
        else
        {
            graphic.Children.Add(new Rectangle { Width = 28, Height = 16, RadiusX = 3, RadiusY = 3, Stroke = Ink("TextMuted"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 } });
            graphic.Children.Add(new TextBlock { Text = "?", FontSize = 12, Foreground = Ink("TextSecondary"), Margin = new Thickness(10, -1, 0, 0) });
        }
        string label = host ? "Host bus" : n.PortConnectorIsTypeC == true ? "USB-C port" : "Connector unknown";
        panel.Children.Add(graphic);
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Ink("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
        panel.ToolTip = host ? "Logical host connection; physical controller placement is not reported."
            : n.PortConnectorIsTypeC == true ? "Windows reports a USB-C receptacle on the parent hub. The cable and device-end plug are not identified."
            : "Windows does not identify this connector's shape. USB speed does not establish Type-A, Type-B, Micro, or Type-C.";
        return panel;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsbAtlas;

internal static class NodeVisuals
{
    internal static string Color(UsbNode n) => n.Kind switch
    {
        "Controller" or "Root hub" => "HostRole",
        "Hub" when n.Location == "Internal" => "HostRole",
        "Hub" when n.Location == "External" => "HubRole",
        "Hub" => "UnknownRole", "Empty port" => "UnknownRole", "Unavailable" => "Error", _ => "DeviceRole"
    };
    internal static string Label(UsbNode n) => n.Kind switch
    {
        "Controller" => "Host controller", "Root hub" => "Root ports",
        "Hub" when n.Location == "Internal" => "Internal hub · inferred",
        "Hub" when n.Location == "External" => "External hub · inferred",
        "Hub" => "Hub · location unknown", "Empty port" => "Empty port", "Unavailable" => "Port error",
        _ => n.DeviceType
    };
    internal static Brush Ink(string hex) => Theme.Brush(hex);
    internal static FrameworkElement Icon(UsbNode n, double size = 36)
    {
        if (n.Kind == "Hub")
        {
            // Google Material Icons device_hub, Apache-2.0. SVG path converted to WPF geometry.
            var hub = new Path { Data = Geometry.Parse("M17 16l-4-4V8.82C14.16 8.4 15 7.3 15 6c0-1.66-1.34-3-3-3S9 4.34 9 6c0 1.3.84 2.4 2 2.82V12l-4 4H3v5h5v-3.05l4-4.2 4 4.2V21h5v-5h-4z"), Fill = Ink(Color(n)) };
            var hubCanvas = new Canvas { Width = 24, Height = 24 }; hubCanvas.Children.Add(hub);
            return new Viewbox { Width = size, Height = size, Child = hubCanvas };
        }
        var kind = n.Kind is "Controller" or "Root hub" ? "Board" : n.Kind == "Hub" ? "Hub" : n.DeviceType;
        var path = kind switch
        {
            "Board" => "M4,4 H28 V28 H4 Z M11,10 H21 V21 H11 Z M1,9 H4 M1,16 H4 M1,23 H4 M28,9 H31 M28,16 H31 M28,23 H31 M9,1 V4 M16,1 V4 M23,1 V4 M9,28 V31 M16,28 V31 M23,28 V31",
            "Keyboard" => "M2,8 H30 V25 H2 Z M6,12 H8 M12,12 H14 M18,12 H20 M24,12 H26 M6,16 H8 M12,16 H14 M18,16 H20 M24,16 H26 M7,21 H25",
            "Mouse" => "M7,13 C7,0 25,0 25,13 V21 C25,34 7,34 7,21 Z M16,4 V15 M7,15 H25 M16,8 V11",
            "Storage" => "M6,3 H26 V29 H6 Z M10,7 H22 V19 H10 Z M11,25 H13 M19,25 H22",
            "Camera / video" => "M3,9 H23 V25 H3 Z M8,9 L11,5 H18 L21,9 M23,14 L30,10 V25 L23,21 M17,17 A5,5 0 1 1 7,17 A5,5 0 1 1 17,17",
            "Audio" => "M4,20 V15 C4,0 28,0 28,15 V20 M4,17 H10 V28 H4 Z M22,17 H28 V28 H22 Z",
            "Game controller" => "M9,9 H23 C28,9 29,18 30,24 Q29,30 24,25 L20,21 H12 L8,25 Q2,30 2,24 C3,18 4,9 9,9 Z M7,15 H15 M11,11 V19 M23,13 V15 M26,17 V19",
            "VR headset" => "M3,11 H29 V25 H20 L16,21 L12,25 H3 Z M7,11 V6 H25 V11 M8,16 H12 M20,16 H24",
            "Wireless" => "M2,10 Q16,-1 30,10 M7,16 Q16,8 25,16 M12,22 Q16,18 20,22 M15,28 H17",
            "Printer" => "M8,12 V3 H24 V12 M8,24 H3 V12 H29 V24 H24 M8,20 H24 V30 H8 Z M23,16 H25",
            "HID / controls" => "M3,5 H29 V28 H3 Z M8,11 H12 M10,9 V13 M21,11 H24 M8,21 H12 M19,20 H24 M21,18 V23",
            "Serial / communications" => "M3,8 H29 V25 H3 Z M8,13 V16 M13,13 V16 M18,13 V16 M23,13 V16 M11,21 H21 M10,3 V8 M22,3 V8",
            "Billboard" => "M3,4 H29 V24 H3 Z M16,24 V29 M10,29 H22 M15,8 H17 M16,13 V20",
            _ => "M10,3 H22 V13 H26 V29 H6 V13 H10 Z M14,6 V10 M18,6 V10 M11,19 H21 M11,24 H17"
        };
        var canvas = new Canvas { Width = 32, Height = 32 };
        canvas.Children.Add(new Path { Data = Geometry.Parse(path), Stroke = Ink(Color(n)), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
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
            graphic.Children.Add(new Path { Data = Geometry.Parse("M1,9 H30 M6,4 V14 M13,4 V14 M20,4 V14 M27,4 V14"), Stroke = Ink("HostRole"), StrokeThickness = 1.5 });
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace UsbAtlas;
public partial class MainWindow
{
    // A hub socket can be named when a numbered port sits on a parent; the name belongs to the socket, not the device in it.
    private bool CanNamePort(UsbNode node) => node.Port > 0 && snapshot.Nodes.Any(n => n.Children.Any(c => c.Id == node.Id));
    private static bool CanNameDevice(UsbNode node) => node.Kind is not ("Empty port" or "Unavailable");

    private FrameworkElement PortNameEditor(UsbNode node)
    {
        var button = new Button { Content = node.PortLabel.Length > 0 ? node.PortLabel + " · Edit" : "Name this port…", Padding = new Thickness(5, 1, 5, 1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Name this hub socket independently of the device plugged into it" };
        button.Click += (_, _) => EditPortName(node, button);
        return button;
    }

    private void EditPortName(UsbNode port, FrameworkElement target) => ShowNameEditor(target, $"Name port {port.Port:00}", port.PortLabel,
        "Stays with this hub and port when devices change.",
        value => deviceLabels.TrySetPort(port, snapshot, value, out var error) ? null : error,
        value => { Draw(); ShowDetails(); StatusText.Text = value.Trim().Length == 0 ? "Port name removed." : "Port name saved."; });

    private void EditDeviceName(UsbNode node, FrameworkElement target)
    {
        if (!CanNameDevice(node)) return;
        ShowNameEditor(target, "Name this device", node.UserLabel,
            DeviceLabels.FollowsDevice(node, snapshot)
                ? "Saved locally for this device's USB ID and serial number."
                : "Saved locally for this USB identity at this port path. No unique serial: moving it requires a new label.",
            value => snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id) is UsbNode current
                ? deviceLabels.TrySet(current, snapshot, value, out var error) ? null : error
                : "Device is no longer in this snapshot.",
            value => { Draw(); ShowDetails(); StatusText.Text = value.Trim().Length == 0 ? "Device label removed." : "Device label saved locally."; });
    }

    // One small popup serves every rename: save returns an error message or null; done runs after the popup closes.
    private void ShowNameEditor(FrameworkElement target, string heading, string current, string hint, Func<string, string?> save, Action<string> done)
    {
        var input = new TextBox { Text = current, MaxLength = 100, Padding = new Thickness(7, 5, 7, 5), MinWidth = 230 };
        System.Windows.Automation.AutomationProperties.SetName(input, heading);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), Foreground = Brush("TextPrimary") });
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), MaxWidth = 280, Foreground = Brush("TextSecondary") });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = new Border { Background = Brush("Surface"), BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = panel } };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Foreground = Brush("Error") };
        void Save(string value)
        {
            if (save(value) is string message) { error.Text = message; return; }
            popup.IsOpen = false; done(value);
        }
        foreach (var label in new[] { "Save", "Reset", "Cancel" })
        {
            var action = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
            action.Click += (_, _) => { if (label == "Cancel") popup.IsOpen = false; else Save(label == "Reset" ? "" : input.Text); };
            actions.Children.Add(action);
        }
        panel.Children.Add(actions); panel.Children.Add(error);
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(input.Text); e.Handled = true; } else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        popup.IsOpen = true;
        openNameEditor = popup;
    }
    private Popup? openNameEditor;

    // The port's name as a tiny tag: it straddles the top border of the device card plugged into it, or
    // sits beside an empty socket, so naming costs the layout no space.
    private FrameworkElement PortTag(UsbNode port, double maxWidth)
    {
        var tag = new Border
        {
            Background = Brush("Surface"), BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 0), MaxWidth = maxWidth, Cursor = Cursors.Hand, Tag = PortTagMarker,
            Child = new TextBlock { Text = port.PortLabel, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = $"Port {port.Port:00} · {port.PortLabel}\nClick to rename"
        };
        System.Windows.Automation.AutomationProperties.SetName(tag, $"Port {port.Port} name {port.PortLabel}, click to rename");
        tag.MouseLeftButtonDown += (_, e) => { EditPortName(port, tag); e.Handled = true; };
        Panel.SetZIndex(tag, 3);
        return tag;
    }
    internal const string PortTagMarker = "port-tag";

    private ContextMenu RenameMenu(UsbNode? device, UsbNode? port, FrameworkElement anchor)
    {
        var menu = new ContextMenu { Background = Brush("Surface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("Border") };
        if (device != null && CanNameDevice(device)) { var item = new MenuItem { Header = "Rename device…", InputGestureText = "F2" }; item.Click += (_, _) => EditDeviceName(device, anchor); menu.Items.Add(item); }
        if (port != null && CanNamePort(port)) { var item = new MenuItem { Header = "Rename port…", InputGestureText = device == null ? "F2" : "" }; item.Click += (_, _) => EditPortName(port, anchor); menu.Items.Add(item); }
        return menu;
    }
}

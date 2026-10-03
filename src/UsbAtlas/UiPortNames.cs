using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace UsbAtlas;
public partial class MainWindow
{
    private FrameworkElement PortNameEditor(UsbNode node)
    {
        var button = new Button { Content = node.PortLabel.Length > 0 ? node.PortLabel + " · Edit" : "Name this port…", Padding = new Thickness(5, 1, 5, 1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Name this hub socket independently of the device plugged into it" };
        button.Click += (_, _) =>
        {
            var input = new TextBox { Text = node.PortLabel, MaxLength = 100, Padding = new Thickness(7, 5, 7, 5), MinWidth = 230 };
            System.Windows.Automation.AutomationProperties.SetName(input, "Port name");
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = $"Name port {node.Port:00}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(input);
            panel.Children.Add(new TextBlock { Text = "Stays with this hub and port when devices change.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), MaxWidth = 280 });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = new Border { Background = Brush("Surface"), BorderBrush = Brush("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = panel } };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Foreground = Brush("Error") };
            void Save(string value)
            {
                if (!deviceLabels.TrySetPort(node, snapshot, value, out var message)) { error.Text = message; return; }
                popup.IsOpen = false; Draw(); ShowDetails(); StatusText.Text = value.Trim().Length == 0 ? "Port name removed." : "Port name saved.";
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
        };
        return button;
    }
}

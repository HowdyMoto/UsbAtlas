using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UsbAtlas;

public partial class MainWindow
{
    private static string ProtocolSummary(UsbNode node) => node.DownstreamProtocols + (node.ProtocolSummaryPartial && node.DownstreamProtocols != "Not reported" ? " · partial" : "");
    private void AddLabelEditor(UsbNode node)
    {
        if (node.Kind is "Empty port" or "Unavailable") return;
        var content = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var input = new TextBox { Text = node.UserLabel, MaxLength = 100, Padding = new Thickness(7, 5, 7, 5), ToolTip = "For example: Dell monitor KVM. Leave blank to restore the detected name." };
        System.Windows.Automation.AutomationProperties.SetName(input, "Your device label");
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = DeviceLabels.FollowsDevice(node, snapshot)
                ? "Saved locally for this device's USB ID and serial number."
                : "Saved locally for this USB identity at this port path. No unique serial: moving it requires a new label; an identical replacement here can inherit it.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextMuted"), Margin = new Thickness(0, 5, 0, 5)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save label", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        var clear = new Button { Content = "Reset", Padding = new Thickness(8, 3, 8, 3), ToolTip = "Remove your label and use the detected name" };
        var error = new TextBlock { Foreground = Brush("Warning"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        void Save(string value)
        {
            var current = snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id);
            if (current == null) { error.Text = "Device is no longer in this snapshot."; return; }
            if (!deviceLabels.TrySet(current, snapshot, value, out var message)) { error.Text = message; return; }
            Draw(); ShowDetails();
            StatusText.Text = value.Trim().Length == 0 ? "Device label removed." : "Device label saved locally.";
        }
        save.Click += (_, _) => Save(input.Text);
        clear.Click += (_, _) => Save("");
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(input.Text); e.Handled = true; } };
        buttons.Children.Add(save); buttons.Children.Add(clear); content.Children.Add(buttons); content.Children.Add(error);
        Details.Children.Add(new Expander { Header = node.UserLabel.Length > 0 ? "Edit your label" : "Add your own label", Content = content, FontSize = 11, Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 8) });
    }
}

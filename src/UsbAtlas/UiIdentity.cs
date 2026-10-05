using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UsbAtlas;

public partial class MainWindow
{
    private static string ProtocolSummary(UsbNode node) => node.DownstreamProtocols + (node.ProtocolSummaryPartial && node.DownstreamProtocols != "Not reported" ? " · partial" : "");
    private Action? editSelectedLabel;
    private string? editingLabelId, labelDraft;
    private int labelSelectionStart, labelSelectionLength;
    private bool restoreLabelFocus;
    private ContentControl? inlineLabelHost;
    private void EndLabelEdit() { editingLabelId = null; labelDraft = null; }
    private FrameworkElement? deviceHeading;
    private void AddLabelEditor(UsbNode node)
    {
        if (node.Kind is "Empty port" or "Unavailable") return;
        var content = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var input = new TextBox { Text = editingLabelId == node.Id ? labelDraft ?? node.UserLabel : node.UserLabel.Length > 0 ? node.UserLabel : node.DisplayName, FontSize = 16, MaxLength = 100, Padding = new Thickness(7, 5, 7, 5), ToolTip = "For example: Dell monitor KVM. Leave blank to restore the detected name." };
        System.Windows.Automation.AutomationProperties.SetName(input, "Your device label");
        content.Children.Add(new TextBlock { Text = "Device label", FontSize = 12, Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 5) });
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = DeviceLabels.FollowsDevice(node, snapshot)
                ? "Saved locally for this device's USB ID and serial number."
                : "Saved locally for this USB identity at this port path. No unique serial: moving it requires a new label; an identical replacement here can inherit it.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextMuted"), Margin = new Thickness(0, 5, 0, 5)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save label", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
        var clear = new Button { Content = "Reset", Padding = new Thickness(8, 3, 8, 3), ToolTip = "Remove your label and use the detected name" };
        var error = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        void Fail(string message) => error.Content = NodeVisuals.StatusBadge(Severity.Error, message);
        void Save(string value)
        {
            var current = snapshot.Nodes.FirstOrDefault(n => n.Id == node.Id);
            if (current == null) { Fail("Device is no longer in this snapshot."); return; }
            if (!deviceLabels.TrySet(current, snapshot, value, out var message)) { Fail(message); return; }
            EndLabelEdit(); Draw(); ShowDetails();
            StatusText.Text = value.Trim().Length == 0 ? "Device label removed." : "Device label saved locally.";
        }
        save.Click += (_, _) => Save(input.Text);
        clear.Click += (_, _) => Save("");
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(input.Text); e.Handled = true; } else if (e.Key == Key.Escape) { EndLabelEdit(); ShowDetails(); e.Handled = true; } };
        cancel.Click += (_, _) => { EndLabelEdit(); ShowDetails(); };
        buttons.Children.Add(save); buttons.Children.Add(clear); buttons.Children.Add(cancel); content.Children.Add(buttons); content.Children.Add(error);
        content.Tag = "inline-label-editor";
        if (inlineLabelHost == null) return;
        inlineLabelHost.Content = content;
        input.TextChanged += (_, _) => { if (editingLabelId == node.Id) labelDraft = input.Text; };
        input.SelectionChanged += (_, _) => { if (editingLabelId == node.Id) { labelSelectionStart = input.SelectionStart; labelSelectionLength = input.SelectionLength; } };
        if (editingLabelId == node.Id)
        {
            inlineLabelHost.Visibility = Visibility.Visible;
            if (deviceHeading != null) deviceHeading.Visibility = Visibility.Collapsed;
            input.Select(labelSelectionStart, labelSelectionLength);
            if (restoreLabelFocus) Dispatcher.BeginInvoke(() => input.Focus());
        }
        editSelectedLabel = () =>
        {
            editingLabelId = node.Id; labelDraft = input.Text;
            inlineLabelHost.Visibility = Visibility.Visible;
            if (deviceHeading != null) deviceHeading.Visibility = Visibility.Collapsed;
            UpdateLayout();
            DetailsScroll.ScrollToTop();
            input.Focus(); input.SelectAll();
        };
    }
}

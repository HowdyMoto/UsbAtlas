using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace UsbAtlas;
public partial class MainWindow
{
    // What has happened since the app opened, newest first: devices and monitors connecting and dropping, the
    // computer switching between AC and battery, sleep and wake. A problem that fixes itself in seconds, such as a
    // USB-C monitor losing its power contract under load, leaves nothing on the canvas, so this is where it shows.
    // Everything here comes from notifications the window already receives, so it needs no administrator rights;
    // the hub driver's own account of a drop is atlascli trace.
    private sealed record ActivityEntry(DateTime At, string Text, Severity Severity, string? NodeId);
    private readonly List<ActivityEntry> activity = [];
    private const int ActivityLimit = 200;
    private int activityUnseen;
    // Windows reports the AC/DC source as soon as it is registered; that is where things start, not a change.
    private bool? onAc;
    private Dictionary<string, UsbNode>? beforeSleep;
    private DateTime lastWake = DateTime.MinValue;
    // Devices that lost power while asleep take a few seconds to be set up again.
    private static readonly TimeSpan WakeSettle = TimeSpan.FromSeconds(8);
    private readonly List<IntPtr> powerNotifications = [];
    private readonly Dictionary<string, DateTime> returnedAt = new(StringComparer.OrdinalIgnoreCase);

    private const int WmPowerBroadcast = 0x0218, PbtApmSuspend = 4, PbtApmResumeSuspend = 7, PbtApmResumeAutomatic = 0x12, PbtPowerSettingChange = 0x8013;
    // GUID_DEVINTERFACE_MONITOR: a display appearing or going, whatever adapter it is on.
    private static readonly Guid MonitorInterface = new("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

    private void WatchActivity(IntPtr hwnd)
    {
        reconnects.Returned += NoteReturn;
        if (hwnd == IntPtr.Zero) return;
        var source = PowerStatus.AcDcPowerSource;
        if (RegisterPowerSettingNotification(hwnd, ref source, 0) is var handle && handle != IntPtr.Zero) powerNotifications.Add(handle);
        Closed += (_, _) => { foreach (var h in powerNotifications) UnregisterPowerSettingNotification(h); };
    }

    private void AddActivity(string text, Severity severity = Severity.Note, string? nodeId = null, DateTime? at = null)
    {
        activity.Add(new(at ?? DateTime.Now, text, severity, nodeId));
        if (activity.Count > ActivityLimit) activity.RemoveRange(0, activity.Count - ActivityLimit);
        if (ActivityPanel.Visibility != Visibility.Visible) activityUnseen++;
        ShowActivity();
    }

    // A device or hub that dropped and came back within seconds. A hub takes everything behind it along, so
    // devices that return with a hub already reported are part of its entry.
    private void NoteReturn(string instance, DateTime back, TimeSpan gone)
    {
        var node = snapshot.Nodes.FirstOrDefault(n => string.Equals(n.InstanceId, instance, StringComparison.OrdinalIgnoreCase));
        returnedAt[instance] = back;
        if (node == null) return;
        if (FindPath(node.Id).Any(a => a.Id != node.Id && a.InstanceId.Length > 0 && returnedAt.TryGetValue(a.InstanceId, out var t) && (back - t).Duration() <= ReconnectTracker.SameDrop)) return;
        int behind = node.Walk().Skip(1).Count(c => c.Kind == "Device");
        AddActivity($"{NodeVisuals.ShortName(CardNode(node))} dropped and came back{(behind > 0 ? $", with {behind} device{(behind == 1 ? "" : "s")} behind it" : "")} · gone {gone.TotalSeconds:0.#} s", Severity.Warning, node.Id, back);
    }

    private void HandlePower(int type, IntPtr data)
    {
        if (demo) return;
        if (type == PbtPowerSettingChange && data != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING is a GUID, a length, then the data: 0 is AC, 1 is battery, 2 is short-term (UPS).
            bool ac = Marshal.ReadInt32(data, 20) == 0;
            if (onAc == ac) return;
            bool first = onAc == null; onAc = ac;
            if (first) return;
            string battery = PowerStatus.Read() is { } b ? " · " + PowerStatus.Describe(b) : "";
            AddActivity((ac ? "AC power is back" : "AC power lost; running on the battery") + battery, ac ? Severity.Note : Severity.Warning);
        }
        else if (type == PbtApmSuspend)
        {
            beforeSleep = Occupants(snapshot);
            AddActivity("The computer is going to sleep");
        }
        else if (type is PbtApmResumeAutomatic or PbtApmResumeSuspend)
        {
            // Both wake notifications arrive when a person wakes the computer; one check serves them.
            var now = DateTime.Now;
            if (now - lastWake < WakeSettle) return;
            lastWake = now;
            reconnects.Woke(now);
            AddActivity($"The computer woke; checking what came back in {WakeSettle.TotalSeconds:0} s");
            _ = CheckAfterWaking(beforeSleep); beforeSleep = null;
        }
    }

    private async Task CheckAfterWaking(Dictionary<string, UsbNode>? was)
    {
        await Task.Delay(WakeSettle);
        while (busy) await Task.Delay(200);
        await Refresh();
        if (was == null) return;
        var now = Occupants(snapshot);
        var gone = was.Where(p => !now.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var slower = was.Where(p => now.TryGetValue(p.Key, out var m) && m.LinkMbps < p.Value.LinkMbps).Select(p => (Before: p.Value, After: now[p.Key])).ToList();
        foreach (var n in gone) AddActivity($"Not back after waking: {NodeVisuals.ShortName(CardNode(n))}", Severity.Warning);
        foreach (var (before, after) in slower) AddActivity($"Slower after waking: {NodeVisuals.ShortName(CardNode(after))}, {Topology.ShortSpeed(before)} → {Topology.ShortSpeed(after)}", Severity.Warning, after.Id);
        if (gone.Count == 0 && slower.Count == 0) AddActivity($"After waking, all {was.Count} devices and hubs are back at the link rates they had");
    }

    // A monitor appearing or going, named from the displays Windows knows, since the notification gives only its ID.
    private async void NoteMonitor(string instance, bool arrived, DateTime at)
    {
        string name = instance.Split('\\') is [_, var model, ..] ? model : instance;
        try
        {
            var read = await Task.Run(Displays.Read);
            if (read.Displays.FirstOrDefault(d => string.Equals(d.InstanceId, instance, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } known) name = Displays.MonitorName(known.Name);
        }
        catch (Exception) { }
        AddActivity($"Monitor {(arrived ? "connected" : "disconnected")}: {name}", arrived ? Severity.Note : Severity.Warning, null, at);
    }

    private void ShowActivity()
    {
        ActivityButton.Content = activityUnseen > 0 && ActivityPanel.Visibility != Visibility.Visible ? $"Activity · {activityUnseen} new" : "Activity";
        if (ActivityPanel.Visibility != Visibility.Visible) return;
        ActivityList.Children.Clear();
        if (activity.Count == 0)
            ActivityList.Children.Add(new TextBlock { Text = "Nothing yet. Plug, unplug or wiggle something.", FontSize = 12, Foreground = Brush("TextMuted"), Margin = new Thickness(0, 2, 0, 2) });
        foreach (var entry in activity.OrderByDescending(a => a.At)) ActivityList.Children.Add(ActivityRow(entry));
    }

    private FrameworkElement ActivityRow(ActivityEntry entry)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        text.Inlines.Add(new Run(entry.At.ToString("HH:mm:ss")) { Foreground = Brush("TextMuted"), FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        text.Inlines.Add(new Run("  " + entry.Text) { Foreground = entry.Severity switch { Severity.Error => Brush("Error"), Severity.Warning => Brush("Warning"), _ => Brush("TextPrimary") } });
        var button = new Button
        {
            Content = text, Style = (Style)FindResource("EditableNameButton"), Padding = new Thickness(0), Margin = new Thickness(0, 1, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = entry,
            Cursor = entry.NodeId != null ? Cursors.Hand : Cursors.Arrow, IsHitTestVisible = entry.NodeId != null,
            ToolTip = entry.NodeId != null ? "Click to show it on the canvas" : null
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{entry.At:HH:mm:ss} {entry.Text}");
        button.Click += (_, _) => { if (snapshot.Nodes.FirstOrDefault(n => n.Id == entry.NodeId) is { } node) RevealIssue(node); };
        return button;
    }

    private void ActivityClick(object sender, RoutedEventArgs e)
    {
        ActivityPanel.Visibility = ActivityPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (ActivityPanel.Visibility == Visibility.Visible) activityUnseen = 0;
        ShowActivity();
    }

    private void ActivityClearClick(object sender, RoutedEventArgs e) { activity.Clear(); activityUnseen = 0; ShowActivity(); }

    private string ActivityText() => string.Join("\n", activity.OrderBy(a => a.At).Select(a => $"{a.At:yyyy-MM-dd HH:mm:ss} {a.Text}"));

    private void ActivityCopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ActivityText()); StatusText.Text = "Activity copied."; }
        catch (Exception ex) { StatusText.Text = "Clipboard unavailable: " + ex.Message; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}

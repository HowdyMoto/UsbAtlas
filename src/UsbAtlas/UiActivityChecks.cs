using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace UsbAtlas;
public partial class MainWindow
{
    // The activity feed counts what it hasn't shown, lists newest first with problems in warning color, notices
    // the computer switching between AC and battery, reports a device that dropped and came back once however
    // many devices came back behind it, selects the device when a row is clicked, and copies as text.
    private void VerifyActivityFeed()
    {
        if (!demo) return;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static string RowText(Button row) => string.Concat(((TextBlock)row.Content).Inlines.OfType<Run>().Select(r => r.Text));
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var (savedEntries, savedUnseen, savedOnAc, savedVisibility) = (activity.ToList(), activityUnseen, onAc, ActivityPanel.Visibility);
        activity.Clear(); activityUnseen = 0; onAc = null; ActivityPanel.Visibility = Visibility.Collapsed;
        var hub = snapshot.Nodes.First(n => n.Kind == "Hub" && n.Children.Any(c => c.Kind == "Device"));
        var behind = hub.Children.First(c => c.Kind == "Device");
        var (hubInstance, behindInstance) = (hub.InstanceId, behind.InstanceId);
        try
        {
            AddActivity("Connected: a keyboard");
            Check(ActivityButton.Content.ToString() == "Activity · 1 new", "The Activity button must say how many entries are unseen.");
            ActivityClick(this, new RoutedEventArgs()); UpdateLayout();
            Check(ActivityPanel.Visibility == Visibility.Visible && ActivityPanel.ActualHeight > 0 && ActivityButton.Content.ToString() == "Activity" && ActivityList.Children.OfType<Button>().Count() == 1,
                "Opening the feed shows its entries and clears the unseen count.");

            // The AC/DC setting is delivered as a WM_POWERBROADCAST message; the first report is where things start.
            demo = false;
            try
            {
                foreach (int value in new[] { 0, 1, 0 })
                {
                    var data = Marshal.AllocHGlobal(24);
                    try
                    {
                        Marshal.Copy(PowerStatus.AcDcPowerSource.ToByteArray(), 0, data, 16); Marshal.WriteInt32(data, 16, 4); Marshal.WriteInt32(data, 20, value);
                        SendMessage(hwnd, WmPowerBroadcast, PbtPowerSettingChange, data);
                    }
                    finally { Marshal.FreeHGlobal(data); }
                }
            }
            finally { demo = true; }
            UpdateLayout();
            var rows = ActivityList.Children.OfType<Button>().ToList();
            Check(rows.Count == 3 && RowText(rows[0]).Contains("AC power is back") && RowText(rows[1]).Contains("AC power lost"), "Switching to the battery and back must be reported, newest first, and the first report must not be.");
            var lost = (TextBlock)rows[1].Content;
            Check(lost.Inlines.OfType<Run>().Last().Foreground == Brush("Warning") && ((TextBlock)rows[2].Content).Inlines.OfType<Run>().Last().Foreground == Brush("TextPrimary"),
                "Losing AC is in warning color and ordinary entries are plain.");

            // A hub that drops takes what is behind it along; devices returning with it belong to its entry.
            hub.InstanceId = "TEST/HUB/1"; behind.InstanceId = "TEST/DEVICE/1";
            // The wake checks before this one leave a wake seconds ago, after which returns are expected, not quick drops.
            reconnects.Woke(DateTime.MinValue);
            var gone = DateTime.Now.AddSeconds(-2.2);
            foreach (var id in new[] { hub.InstanceId, behind.InstanceId }) reconnects.Removed(id, gone);
            reconnects.Arrived(hub.InstanceId, gone.AddSeconds(2.3)); reconnects.Arrived(behind.InstanceId, gone.AddSeconds(2.4));
            UpdateLayout();
            rows = ActivityList.Children.OfType<Button>().ToList();
            Check(rows.Count == 4 && RowText(rows[0]).Contains("dropped and came back") && RowText(rows[0]).Contains("gone 2.3 s") && RowText(rows[0]).Contains("behind it"),
                "A hub that dropped and came back is one entry, with its length and what was behind it.");
            CaptureUi("activity-preview.png");
            selected = null; rows[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(selected?.Id == CardNode(hub).Id, "Clicking an entry selects its device.");

            Check(ActivityText().Split('\n').Length == 4 && ActivityText().Split('\n')[0].Contains("a keyboard"), "Copying gives one line per entry, oldest first.");
            ActivityClearClick(this, new RoutedEventArgs());
            Check(ActivityList.Children.OfType<Button>().Count() == 0 && activity.Count == 0, "Clearing empties the feed.");
            for (int i = 0; i < ActivityLimit + 20; i++) AddActivity("entry " + i);
            Check(activity.Count == ActivityLimit && activity[^1].Text == "entry " + (ActivityLimit + 19), "The feed keeps the latest entries and no more.");
        }
        finally
        {
            (hub.InstanceId, behind.InstanceId) = (hubInstance, behindInstance);
            returnedAt.Clear(); demo = true;
            activity.Clear(); activity.AddRange(savedEntries); activityUnseen = savedUnseen; onAc = savedOnAc; ActivityPanel.Visibility = savedVisibility; ShowActivity();
        }
    }
}

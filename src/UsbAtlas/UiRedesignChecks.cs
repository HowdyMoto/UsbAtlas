using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    private void VerifyRedesignedUi()
    {
        var oldSnapshot = snapshot; var oldSelection = selected; var oldFocus = focusedBranch; bool oldTheme = Theme.IsDark;
        try
        {
            snapshot = DemoData.Create(); focusedBranch = null; Draw();
            var hub = snapshot.Nodes.First(n => n.Id == "demo/root/1");
            var leaf = snapshot.Nodes.First(n => n.Id == "demo/root/5/1");
            SelectNode(hub); FocusBranchClick(this, new RoutedEventArgs()); UpdateLayout();
            if (!cards.ContainsKey(hub.Id) || cards.ContainsKey(leaf.Id) || !treeItems.ContainsKey(leaf.Id))
                throw new Exception("Focus must hide unrelated graph branches while retaining the complete device tree.");
            ShowOnCanvas(leaf); UpdateLayout();
            if (!cards.ContainsKey(leaf.Id) || selected?.Id != leaf.Id)
                throw new Exception("Selecting a tree device outside the focused branch must reveal its new branch.");
            CaptureUi("focused-branch-preview.png");
            FocusBranchClick(this, new RoutedEventArgs()); UpdateLayout();
            if (!cards.ContainsKey(hub.Id) || !cards.ContainsKey(leaf.Id) || focusedBranch != null)
                throw new Exception("Show all branches must restore the full topology.");
            var usb2 = new UsbNode { Kind = "Hub", UsbVersion = "USB 2.10", LinkMbps = 480, SpeedLimited = true };
            if (Issues(usb2).Any(i => i.Text == "Running at USB 2"))
                throw new Exception("Native-speed USB 2 hub sections must not warn about their USB 3 counterpart.");
            usb2.Usb3SideMissing = true;
            if (!Issues(usb2).Contains((Severity.Note, "Running at USB 2")))
                throw new Exception("A USB 2 hub section whose USB 3 side didn't connect must say so, calmly while it slows nothing.");
            usb2.Usb3SideMissing = false; usb2.Kind = "Device";
            if (!Issues(usb2).Contains((Severity.Warning, "Running at USB 2")))
                throw new Exception("A device slower than it supports must warn.");
            SelectNode(hub); editSelectedLabel!();
            TextBox Input() => ((StackPanel)inlineLabelHost!.Content).Children.OfType<TextBox>().Single();
            Input().Text = "My unsaved hub label";
            ApplyAppearance(!oldTheme);
            if (inlineLabelHost!.Visibility != Visibility.Visible || Input().Text != "My unsaved hub label")
                throw new Exception("Theme changes must preserve an inline label draft.");
            snapshot = DemoData.Create(); selected = snapshot.Nodes.Single(n => n.Id == hub.Id); Draw(); ShowDetails();
            if (inlineLabelHost.Visibility != Visibility.Visible || Input().Text != "My unsaved hub label")
                throw new Exception("Replacing a scan of the same selected device must preserve its unsaved label.");
            CaptureUi("preserved-draft-preview.png");
            ((StackPanel)inlineLabelHost.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (editingLabelId != null || selected.UserLabel.Length != 0)
                throw new Exception("Cancel must discard the draft without changing the device label.");
            ApplyAppearance(oldTheme);
            selected.PowerWarnings = ["Power at risk", "Over power budget", "Hub adapter not detected"];
            selected.QuickReconnects = 3; ShowDetails(); UpdateLayout();
            var warnings = Details.Children.OfType<WrapPanel>().First();
            double bottom = warnings.Children.OfType<FrameworkElement>().Max(b => b.TranslatePoint(new Point(0, b.ActualHeight), warnings).Y);
            if (bottom > warnings.ActualHeight + 0.5 || warnings.ActualHeight <= 22)
                throw new Exception("Multiple property warnings must wrap without clipping.");
            var action = warnings.Children.OfType<Button>().First();
            if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(action)?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) == null)
                throw new Exception("Warning explanations must expose a button Invoke action to accessibility tools.");
            CaptureUi("multiple-warning-preview.png");
        }
        finally
        {
            EndLabelEdit(); snapshot = oldSnapshot; selected = oldSelection; focusedBranch = oldFocus;
            FocusBranchButton.Content = oldFocus == null ? "Focus branch" : "Show all branches";
            ApplyAppearance(oldTheme);
        }
    }

    // Severity sets how much of an explanation leads Properties: a note is one line, so the figures and the
    // first section stay in view; a warning says whether it affects you and keeps what to do a click away,
    // open across selections once opened; an error is shown whole.
    private void VerifySeverityExplanations()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var oldSnapshot = snapshot; var oldSelection = selected; var oldOpen = openExplanations.ToHashSet();
        try
        {
            snapshot = DemoData.Create(); openExplanations.Clear(); Draw();
            StackPanel Body(string issue) => (StackPanel)((Border)Details.Children.OfType<FrameworkElement>().Single(e => Equals(e.Tag, "warning:" + issue))).Child;
            FrameworkElement More(string issue) => Body(issue).Children.OfType<StackPanel>().Single(p => Equals(p.Tag, ExplanationMoreTag));
            Button? Toggle(string issue) => Body(issue).Children.OfType<Button>().SingleOrDefault(b => Equals(b.Tag, ExplanationToggleTag));
            void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout(); }

            var hub = snapshot.Nodes.First(n => n.Id == "demo/root/1");
            Check(Issues(hub).Count > 0 && Issues(hub).All(i => i.Severity == Severity.Note), "The sample's desktop hub must have only notes.");
            SelectNode(hub); UpdateLayout();
            var section = Details.Children.OfType<TextBlock>().First(t => Equals(t.Tag, "section") && t.Text == "Device identity & connection");
            double below = section.TranslatePoint(new Point(0, section.ActualHeight), DetailsScroll).Y;
            Check(below <= DetailsScroll.ViewportHeight, $"With only notes, the figures and Device identity & connection must show without scrolling; they end at {below:0} px of {DetailsScroll.ViewportHeight:0}.");
            var note = Issues(hub)[0].Text;
            Check(More(note).Visibility == Visibility.Collapsed && Toggle(note) != null, "A note must start as one line with a way to open it.");
            Click(Toggle(note)!);
            Check(More(note).Visibility == Visibility.Visible, "Opening a note must show the rest of its explanation.");
            Click(Toggle(note)!);

            var drive = snapshot.Nodes.First(n => n.Id == "demo/root/5/1"); var light = snapshot.Nodes.First(n => n.Id == "demo/root/5/2");
            SelectNode(drive); UpdateLayout();
            Check(Issues(drive).Contains((Severity.Warning, "Power at risk")) && Issues(light).Contains((Severity.Warning, "Power at risk")), "The sample's travel hub devices must be at risk.");
            Check(Body("Power at risk").Children.OfType<TextBlock>().Any(t => t.Text == "Does it affect you?") && More("Power at risk").Visibility == Visibility.Collapsed,
                "A warning must say whether it affects you, with what to do a click away.");
            Click(Toggle("Power at risk")!);
            Check(More("Power at risk").Visibility == Visibility.Visible, "Opening a warning must show what to do.");
            SelectNode(light); UpdateLayout();
            Check(More("Power at risk").Visibility == Visibility.Visible, "An opened explanation must stay open on the next device with the same issue.");
            Click(Toggle("Power at risk")!);

            var failed = snapshot.Nodes.First(n => n.Id == "demo/root/5/3");
            SelectNode(failed); UpdateLayout();
            var error = Issues(failed).Single(i => i.Severity == Severity.Error).Text;
            Check(More(error).Visibility == Visibility.Visible && Toggle(error) == null, "An error's explanation must be shown whole.");
        }
        finally
        {
            snapshot = oldSnapshot; selected = oldSelection; openExplanations.Clear(); openExplanations.UnionWith(oldOpen);
            Draw(); ShowDetails();
        }
    }
}

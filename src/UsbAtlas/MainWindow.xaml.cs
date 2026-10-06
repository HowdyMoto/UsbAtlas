using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace UsbAtlas;

public partial class MainWindow : Window
{
    private Snapshot snapshot = new();
    private UsbNode? selected;
    private readonly HashSet<string> folded = [];
    private readonly Dictionary<string, (Border Card, Point Point)> cards = [];
    private bool busy, demo, render, fitNext = true;
    private long refreshStarted;
    private int refreshIndicatorVersion;
    private DeviceLabels deviceLabels = new();
    private MouseButton? panButton;
    private Point panStart, panOrigin;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private static Brush Brush(string hex) => Theme.Brush(hex);
    public MainWindow(bool demo, bool render, bool verifyUi = false, bool? horizontal = null)
    {
        InitializeComponent(); this.demo = demo; this.render = render;
        var searchGlyph = NodeVisuals.Symbol("search", Theme.Brush("TextSecondary"), 16);
        ((System.Windows.Shapes.Path)((Canvas)searchGlyph.Child).Children[0]).SetResourceReference(Shape.FillProperty, "TextSecondary");
        SearchIcon.Content = searchGlyph;
        var copyGlyph = NodeVisuals.Symbol("content_copy", Brush("TextPrimary"), 14);
        ((System.Windows.Shapes.Path)((Canvas)copyGlyph.Child).Children[0]).SetResourceReference(Shape.FillProperty, "TextPrimary");
        CopyDetailsIcon.Content = copyGlyph;
        horizontalTree = horizontal ?? SavedLayoutIsHorizontal();
        ShowLayoutChoice(); ShowViewTips(); ShowRefreshTips();
        ThemeButton.Content = Theme.IsDark ? "Light mode" : "Dark mode";
        Loaded += async (_, _) =>
        {
            await Refresh();
            if (verifyUi)
            {
                pinnedModifiers = ModifierKeys.None;
                try { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; detail = CardDetail.Full; overviewView = false; Draw(); VerifySmallWindow(); VerifySearchInput(); VerifyWarningExplanation(); VerifySpeedExplanation(); VerifyUi(); VerifyDeviceTree(); VerifyCompactUi(); VerifyIdentityUi(); VerifyInspectorConsistency(); VerifySeverityExplanations(); VerifyPowerUi(); VerifyCanvasNaming(); await VerifyRefreshUi(); await VerifyTreeCanvasSync(); await VerifyDeviceWatch(); await VerifyObjectConstancy(); VerifyRedesignedUi(); VerifyHubSnapping(); VerifyPairedHubs(); VerifyCardTitles(); VerifySemanticZoom(); VerifyEverythingExplains(); VerifyFixFirst(); File.WriteAllText("ui-test.txt", "UI checks passed: what to fix first and the exported image, everything on the graph explains itself on hover or click, device tree selection/filtering/collapse, tree and canvas selection sync, planar wire routing, layout, filtering, folding, focus, fit, variable-height cards, merged host cards, sockets, search navigation, issues, power and stability issues, link, polling and power figures, power saving, bandwidth meters, inspector and its consistent layout, explanations sized by severity, semantic zoom and opening on the whole topology, paired hubs drawn as one card, two-line card titles, the tree folding for a narrow window, a wrapping legend, animated topology changes, saved labels, host capabilities, selection reuse, refresh feedback and device-change rescans."); }
                catch (Exception ex) { File.WriteAllText("ui-test.txt", ex.ToString()); Application.Current.Shutdown(1); return; }
            }
            if (render) await RenderPreview();
        };
        timer.Tick += async (_, _) => { if (autoRefresh && !demo && !busy) await Refresh(); };
        searchTimer.Tick += (_, _) => ApplySearch();
        SourceInitialized += (_, _) => WatchDevices();
        SizeChanged += (_, e) => { if (e.WidthChanged) FoldTreeForRoom(e.NewSize.Width); };
        timer.Start(); Closed += (_, _) => { timer.Stop(); searchTimer.Stop(); };
    }
    private async Task Refresh()
    {
        if (busy) return;
        busy = true; RefreshButton.IsEnabled = false;
        ShowRefreshProgress();
        StatusText.Text = "Scanning controllers, hubs and device descriptors…";
        try
        {
            // Let WPF paint the indicator even when a scan completes synchronously.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var next = demo ? DemoData.Create() : await Task.Run(() => new UsbScanner().Scan());
            deviceLabels.Apply(next);
            reconnects.Apply(next);
            var before = snapshot.Controllers.Count > 0 && snapshot.IsDemo == next.IsDemo ? Occupants(snapshot) : null;
            var id = selected?.Id;
            bool changed = JsonSerializer.Serialize(snapshot.Controllers) != JsonSerializer.Serialize(next.Controllers) || !snapshot.Diagnostics.SequenceEqual(next.Diagnostics);
            snapshot = next;
            selected = snapshot.Nodes.FirstOrDefault(x => x.Id == id) ?? snapshot.Nodes.FirstOrDefault(x => x.Kind == "Hub") ?? snapshot.Controllers.FirstOrDefault();
            StatusText.Text = (demo ? "Sample topology  ·  " : "Local snapshot  ·  ") + $"Updated {snapshot.CapturedAt:T} · {snapshot.Nodes.Count(x => x.Kind == "Unavailable")} port errors";
            if (snapshot.Diagnostics.Count > 0) StatusText.Text += " · " + string.Join(" · ", snapshot.Diagnostics);
            UpdateIssues();
            if (deviceLabels.LoadError != null) StatusText.Text += " · Saved labels unavailable";
            if (changed) { var places = fitNext ? [] : CardPlaces(); Draw(); ShowDetails(); AnimateChange(places); }
            if (fitNext) { GraphScroll.UpdateLayout(); FitClick(this, new RoutedEventArgs()); OpenInitialView(); fitNext = false; }
            if (before != null) ReportConnections(before, Occupants(snapshot));
        }
        catch (Exception ex) { StatusText.Text = "Scan failed: " + ex.Message; EmptyMessage.Text = "Could not read USB devices. See status below; refresh to retry."; EmptyMessage.Visibility = Visibility.Visible; }
        finally
        {
            busy = false; RefreshButton.IsEnabled = true; FadeRefreshProgress();
            if (rescanQueued) { rescanQueued = false; QueueDeviceRescan(); }
        }
    }
    private void ShowRefreshProgress()
    {
        refreshIndicatorVersion++;
        refreshStarted = Stopwatch.GetTimestamp();
        RefreshProgress.BeginAnimation(OpacityProperty, null);
        RefreshProgress.Opacity = 1;
        RefreshProgress.Visibility = Visibility.Visible;
        RefreshProgress.IsIndeterminate = true;
    }
    private void FadeRefreshProgress()
    {
        int version = refreshIndicatorVersion;
        // Keep very fast refreshes perceptible without delaying the scan or controls.
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, 120 - Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds)),
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => CompleteRefreshProgress(version);
        RefreshProgress.BeginAnimation(OpacityProperty, fade);
    }
    private void CompleteRefreshProgress(int version)
    {
        if (version != refreshIndicatorVersion) return;
        RefreshProgress.Visibility = Visibility.Collapsed;
        RefreshProgress.IsIndeterminate = false;
        RefreshProgress.BeginAnimation(OpacityProperty, null);
        RefreshProgress.Opacity = 0;
    }
    // The keyboard's modifier state, which --verify-ui pins to none: its simulated keys must not pick up
    // Shift or Ctrl held in another window while the off-screen checks run.
    private ModifierKeys? pinnedModifiers;
    private ModifierKeys Modifiers => pinnedModifiers ?? Keyboard.Modifiers;
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        // Framing shortcuts, as in design tools; typing in the search box or a label editor keeps its characters.
        if (Modifiers == ModifierKeys.Shift && Keyboard.FocusedElement is not TextBox && FramingShortcut(e.Key)) { e.Handled = true; return; }
        if (Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox && ZoomShortcut(e.Key)) { e.Handled = true; return; }
        if (e.Key != Key.F5 || Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!e.IsRepeat) await Refresh();
    }
    private static string ShortSpeed(UsbNode n) => Topology.ShortSpeed(n);
    private const string NotApplicable = "—";
    // One layout per kind of selection, so ports can be compared by flipping between them: every port,
    // device and hub shows the same rows in the same places. "—" marks a row that doesn't apply or has
    // nothing attached; "Not reported" marks a value an attached device left out. What an issue means
    // leads, above the rows, since it's what people come to Properties for; actions and evidence follow them.
    private Explanations.Explanation Explain(UsbNode node, string issue) => Explanations.For(node, issue, FindPath(node.Id));
    private Button WarningBadge(UsbNode node, Severity severity, string issue)
    {
        var badge = new Button { Content = NodeVisuals.StatusBadge(severity, issue), Style = (Style)FindResource("WarningButton"), Padding = new Thickness(0), Tag = "warning-action" };
        badge.Cursor = Cursors.Hand;
        badge.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(badge, "Explain " + issue);
        badge.Click += (_, e) => { OpenExplanation(node, issue); e.Handled = true; };
        badge.ToolTip = this.Explain(node, issue).What + " Click to see what to do.";
        return badge;
    }
    // Selects the node and opens what an issue means whole, as asking about it does: from its badge, or a
    // mark on the canvas that stands for it.
    private void OpenExplanation(UsbNode node, string issue)
    {
        openExplanations.Add(issue);
        SelectNode(node);
        if (InspectorPanel.Visibility != Visibility.Visible) InspectorClick(this, new RoutedEventArgs());
        UpdateLayout();
        Details.Children.OfType<FrameworkElement>().FirstOrDefault(x => Equals(x.Tag, "warning:" + issue))?.BringIntoView();
    }
    private void CopyDetailsClick(object sender, RoutedEventArgs e)
    {
        if (selected is not UsbNode node) return;
        try
        {
            Clipboard.SetText(JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "Device details copied.";
        }
        catch (Exception ex) { StatusText.Text = "Clipboard unavailable: " + ex.Message; }
    }
    private void ShowDetails()
    {
        restoreLabelFocus = inlineLabelHost?.IsKeyboardFocusWithin == true;
        if (editingLabelId != selected?.Id) { editingLabelId = null; labelDraft = null; }
        editSelectedLabel = null;
        Details.Children.Clear();
        CopyDetailsButton.IsEnabled = selected is not null;
        if (selected is not UsbNode node) { Text("Select a device", 22); Text("Inspect a connection to see its link, power and path through your hardware.", 12, "TextMuted"); return; }
        if (appliedQuery.Length > 0 && !Matches(node, appliedQuery)) Text("Selection is outside the search results.", 11, "TextMuted");
        if (!cards.ContainsKey(DrawnAs(node).Id) && !portSlots.ContainsKey(node.Id)) Text("Selection is hidden by a collapsed branch or filter.", 11, "TextMuted");
        bool host = node.Kind is "Controller" or "Root hub", attached = node.Kind is "Device" or "Hub", hub = node.Kind == "Hub";
        string Reported(string value) => attached ? (value.Length > 0 && value != "Not reported" ? value : "Not reported") : node.Kind == "Unavailable" ? "Unknown" : NotApplicable;

        // A fixed-height heading, role line and status row keep the rows in place, except that an issue's
        // explanation, which leads, pushes them down by its own height.
        var heading = new DockPanel { Height = 48, Margin = new Thickness(0, 0, 0, 4) };
        var symbol = NodeVisuals.Icon(node, 26); symbol.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(symbol, Dock.Left); heading.Children.Add(symbol);
        var name = new TextBlock { Text = NodeVisuals.ShortName(node), FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 24, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 48, VerticalAlignment = VerticalAlignment.Center, ToolTip = node.DisplayName };
        if (node.Kind is not ("Empty port" or "Unavailable"))
        {
            var nameRow = new DockPanel();
            var pencil = new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,10 L2,14 L6,14 L14,6 L10,2 Z M9,3 L13,7"), Stroke = Brush("TextMuted"), StrokeThickness = 1.4, Width = 16, Height = 16, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            pencil.SetResourceReference(Shape.StrokeProperty, "TextMuted");
            DockPanel.SetDock(pencil, Dock.Right); nameRow.Children.Add(pencil); nameRow.Children.Add(name);
            var editName = new Button { Content = nameRow, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "Click to edit your device label", Tag = "edit-device-name" };
            editName.Style = (Style)FindResource("EditableNameButton");
            System.Windows.Automation.AutomationProperties.SetName(editName, "Edit label for " + node.DisplayName);
            editName.Click += (_, _) => editSelectedLabel?.Invoke();
            heading.Children.Add(editName);
        }
        else heading.Children.Add(name);
        Details.Children.Add(heading);
        deviceHeading = heading;
        inlineLabelHost = new ContentControl { Visibility = Visibility.Collapsed };
        Details.Children.Add(inlineLabelHost);
        Details.Children.Add(new TextBlock { Text = (HubRelationships.CardLabel(node).Length > 0 ? HubRelationships.CardLabel(node) : NodeVisuals.Label(node)) + " · " + node.Status + (node.UserLabel.Length > 0 ? " · detected as " + node.Name : ""), FontSize = 12, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = node.UserLabel.Length > 0 ? "Detected name: " + node.Name : null });
        var status = new WrapPanel { MinHeight = 22, Margin = new Thickness(0, 5, 0, 6) };
        foreach (var (severity, text) in Issues(node)) { var badge = WarningBadge(node, severity, text); badge.Margin = new Thickness(0, 0, 4, 0);  status.Children.Add(badge); }
        if (status.Children.Count == 0) status.Children.Add(new TextBlock { Text = "No issues", FontSize = 12, Foreground = Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center });
        Details.Children.Add(status);
        var issues = Issues(node);
        foreach (var (severity, issue) in issues) AddExplanation(node, severity, issue, issues.Count > 1);
        if (HubRelationships.MissingUsb3HubFor(node, snapshot) is UsbNode lostHub)
        {
            string title = $"USB 3 side of {NodeVisuals.ShortName(lostHub)} not connected";
            AddExplanation(node, Explanations.SpeedSeverity(lostHub), title, true, Explanations.MissingUsb3Half(lostHub, FindPath(lostHub.Id)));
            var show = new Button { Content = "Show " + NodeVisuals.ShortName(lostHub), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, -4, 0, 10), HorizontalAlignment = HorizontalAlignment.Left, Tag = ShowLostHubTag,
                ToolTip = "Select the hub whose USB 3 side belongs on this half of the socket." };
            show.Click += (_, _) => OpenExplanation(lostHub, Explanations.SpeedLabel(lostHub));
            Details.Children.Add(show);
        }
        AddHubSnapControls(node);


        if (!host)
        {
            var metrics = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
            void Metric(NodeVisuals.Metric glyph, string value, string label, int column, string help)
            {
                var stack = new StackPanel { ToolTip = help }; Grid.SetColumn(stack, column);
                var line = new DockPanel();
                var icon = NodeVisuals.MetricGlyph(glyph, 11); icon.Margin = new Thickness(0, 1, 3, 0); DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
                line.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush(value == NotApplicable || value == "Unknown" ? "TextMuted" : "TextPrimary") });
                stack.Children.Add(line);
                stack.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
                metrics.Children.Add(stack);
            }
            string unread = node.Kind == "Unavailable" ? "Unknown" : NotApplicable;
            Metric(NodeVisuals.Metric.Link, attached ? (ShortSpeed(node) == "Rate unknown" ? "Unknown" : ShortSpeed(node)) : unread, "Link speed", 0,
                "The signaling rate negotiated when the device connected. Everything upstream on the same path shares it; it is not a measured speed."
                + (UsbBudgets.BestTransfer(node.LinkMbps) is { Length: > 0 } best ? $" A fast drive on this link moves {best} at best; copy a large file or run a disk benchmark to see what it really does." : ""));
            Metric(NodeVisuals.Metric.Reserved, attached ? (node.ReservedMbps is double reserved ? UsbBudgets.Rate(reserved) : "Unknown") : unread, "Reserved", 1,
                "Bus time held for this device's open interrupt and isochronous pipes, such as audio, video and input. Bulk transfers, such as storage, reserve nothing and share what is left.");
            // A third of the panel is too narrow for the card's "External + 100 mA", so its parts are split
            // between the figure and its label.
            var (power, powerLabel) = node.Kind == "Empty port" ? (unread, "Power request")
                : UsesExternalPower(node) ? (node.MaxPowerMa is > 0 and var bus ? ($"{bus} mA", "External + bus") : ("External", "Power"))
                : (node.MaxPowerMa != null ? PowerFigure(node).Text : attached ? "Unknown" : unread, "Power request");
            Metric(NodeVisuals.Metric.Power, power, powerLabel, 2,
                UsesExternalPower(node) ? "Runs on its own supply, so it requests little or nothing from the bus." : "The most current the device's active configuration says it will draw. A declared maximum, not a measurement.");
            Details.Children.Add(metrics);
            Section("Device identity & connection");
            Field("VID / PID", attached && node.VendorId.Length > 0 ? $"{node.VendorId} : {node.ProductId}" : Reported(""), "The vendor and product IDs the device reports. They identify its chip or product, and are what driver and support pages search by.");
            Field("Manufacturer", Reported(node.Manufacturer), "The maker's name as the device reports it, often the chip's maker rather than the brand on the box.");
            Field("Serial", Reported(node.Serial), "The serial number the device reports. A unique one lets Windows and USB Atlas recognize it on any port; many devices report none, or one every unit shares.");
            Field("USB version", Reported(node.UsbVersion), "The USB version the device says it was built to (bcdUSB). How fast it runs now is Link speed.");
            Field("Device revision", Reported(node.DeviceRevision), "The maker's own revision number for this device (bcdDevice). For hubs and adapters it's usually the firmware version, the first thing their makers' support asks for.");
            Field("Alternate modes", node.Billboard != null ? Billboard.Summary(node) is { Length: > 0 } modes ? modes : "None offered" : node.Kind == "Unavailable" ? "Unknown" : NotApplicable,
                "What a USB-C device's Billboard reports about modes such as DisplayPort over USB-C: whether each was entered, failed, or never asked for.");
            Field("Part of", attached && Containers.PartOf(snapshot, node) is { Length: > 0 } part ? part : node.Kind == "Unavailable" ? "Unknown" : NotApplicable,
                "Windows groups the USB devices of one product, such as a monitor's hub, audio and Billboard, into a device container. Detection details lists the others.");
            // Polling applies to devices with an open interrupt input pipe; hubs poll only for port changes.
            Field("Polling rate", node.Kind == "Device" ? node.PollIntervalMs is double ms ? $"{UsbBudgets.PollingRate(ms)} · {UsbBudgets.PollingInterval(ms)}" : node.ReservedMbps != null ? NotApplicable : "Not reported" : node.Kind == "Unavailable" ? "Unknown" : NotApplicable, PollingHelp);
            Field("Power source", Reported(node.PowerSource), "Whether it runs on power from the USB port or its own supply, as its configuration and, for a hub, Windows report it.");
            Field("Power saving", attached ? PowerSavingText(node) : Reported(""), PowerSavingHelp);
            Field("Power at 5 V", attached && node.MaxPowerMa is int draw ? $"{draw * 0.005:0.##} W declared" : Reported(""), "Its power request at USB's nominal 5 volts, from its descriptor (MaxPower). A declared maximum, not a measurement.");
            Field("Reserved at peak", attached && node.PeakReservedMbps is double peak ? $"Up to {UsbBudgets.Rate(peak)} when active" : Reported(""), "The most bus time it would hold when fully active, such as a camera while streaming, from its busiest alternate settings.");
            if (UsbBudgets.LinkUse(node) is (var use, var room, _))
            {
                var usage = new DockPanel { ToolTip = MetricHelp(node) };
                var share = new TextBlock { Text = UsbBudgets.Share(use, room), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(share, Dock.Right); usage.Children.Add(share);
                var bar = NodeVisuals.LinkBar(use / room); bar.VerticalAlignment = VerticalAlignment.Center; usage.Children.Add(bar);
                Field("Link use", usage, LinkUseHelp);
            }
            else Field("Link use", attached ? "Not reported" : Reported(""), LinkUseHelp);
            Section("Port");
            Field("Path", pathLabels.GetValueOrDefault(node.Id, NotApplicable), PathHelp);
            Field("Port number", node.Port.ToString("00"), "The number Windows gives this port on its hub. A USB 3 socket has two, one for its USB 2 half and one for its USB 3 half.");
            Field("Port name", PortNameEditor(node), "Your own name for this socket. It stays with the hub port when the device in it changes.");
            Field("Port supports", node.Protocols, "The USB versions Windows says this port carries.");
            Field("Connector", NodeVisuals.Connector(node, SocketPartner(node)), ConnectorHelp + (UsbC.Socket(snapshot, node).Count > 0
                ? " Windows doesn't report whether a USB-C socket carries USB4 or Thunderbolt, DisplayPort, or what Power Delivery contract it has. Detection details says what is known." : ""));
            Field("Location", node.Location switch { "Unknown" => "Not reported", "External" => "Likely outside the computer", "Internal" => "Likely built in", "Host" => "In the computer", var other => other },
                "Worked out from whether Windows says the port is one you can plug into, and what it's connected through. Windows doesn't report where hardware sits.");
            Field("Power available", "Unknown · not measured", SupplyHelp);
            Section("Hub");
            Field("Ports", hub ? node.PortCount.ToString() : NotApplicable, PortsHelp);
            Field("Its ports support", hub ? ProtocolSummary(node) : NotApplicable, "The USB versions this hub's ports carry, as Windows reports them.");
            Field("Devices behind", hub ? node.Walk().Count(n => n.Kind == "Device").ToString() : NotApplicable, "Devices connected through this hub, including those behind hubs plugged into it.");
            // Only a single TT is shared; a hub with one per port gives each port its own, which Link use covers.
            Field("Slower devices", hub ? TtType(node) : NotApplicable, "Full- and low-speed devices, such as keyboards, mice and many controllers and audio interfaces, reach the computer through the hub's transaction translator (TT): one link shared by all ports, or one per port.");
            if (UsbBudgets.SharedTtUse(node) is (var ttNow, _, _, _))
            {
                var usage = new DockPanel { ToolTip = "Bus time that full- and low-speed devices on every port reserve on the one 12 Mb/s bus behind this hub's single transaction translator." };
                var share = new TextBlock { Text = UsbBudgets.Share(ttNow, UsbBudgets.FullSpeedReservableMbps), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(share, Dock.Right); usage.Children.Add(share);
                var bar = NodeVisuals.LinkBar(ttNow / UsbBudgets.FullSpeedReservableMbps); bar.VerticalAlignment = VerticalAlignment.Center; usage.Children.Add(bar);
                Field("Shared link", usage, SharedLinkHelp);
            }
            else Field("Shared link", node.TransactionTranslators == "Per port" ? "Not shared · one per port" : NotApplicable, SharedLinkHelp);
        }
        else
        {
            var roots = node.Kind == "Controller" ? node.Children.Where(c => c.Kind == "Root hub").ToList() : [node];
            Section("Host");
            Field("Path", pathLabels.GetValueOrDefault(node.Id, NotApplicable), PathHelp);
            // Several controllers often share one name; the chip's PCI IDs and address tell them apart.
            var controller = node.Kind == "Controller" ? node : snapshot.Controllers.FirstOrDefault(c => c.Children.Contains(node)) ?? node;
            Field("PCI device", controller.PciId.Length > 0 ? string.Join(" · ", new[] { Topology.PciVendor(controller.PciId), controller.PciId, controller.PciAddress }.Where(x => x.Length > 0)) : "Not reported",
                "The host controller chip's PCI vendor:device IDs and its bus:device.function address, which tell apart controllers that share a name.");
            bool heldBack = controller.PcieMaxGeneration > controller.PcieGeneration || controller.PcieMaxLanes > controller.PcieLanes;
            Field("PCIe link", UsbBudgets.PcieMbps(controller.PcieGeneration, controller.PcieLanes) is double uplink
                ? $"{UsbBudgets.PcieText(controller.PcieGeneration!.Value, controller.PcieLanes!.Value)} · about {UsbBudgets.Rate(uplink)}"
                    + (heldBack ? $" · can do {UsbBudgets.PcieText(controller.PcieMaxGeneration ?? controller.PcieGeneration.Value, controller.PcieMaxLanes ?? controller.PcieLanes.Value)}" : "")
                    + (controller.PcieTunneled == true ? " · over USB4/Thunderbolt" : "") : controller.PcieTunneled == true ? "Over USB4/Thunderbolt" : "Not reported",
                "The PCIe connection this controller reaches the computer over, shared by all its ports: the most they can move together. Built-in controllers often report a wide internal link."
                    + (controller.PcieTunneled == true ? " This one is tunneled over USB4 or Thunderbolt, so what it can carry depends on that connection, which Windows doesn't report here." : ""));
            Field("Endpoints", $"{UsbBudgets.ControllerLoad(controller).Endpoints} in use",
                "The channels this controller keeps open for its devices: one for each device's control plus one for each open pipe. Controllers hold only so many, and Windows doesn't say how many; some common ones top out at 96.");
            Field("Port support", ProtocolSummary(node), "The USB versions the host's root ports carry, as Windows reports them.");
            Field("Ports", roots.Sum(r => r.PortCount).ToString(), PortsHelp);
            Field("In use", roots.Sum(r => r.Children.Count(c => c.Kind != "Empty port")).ToString(), "Root ports with something plugged into them, counting each half of a USB 3 socket.");
            Field("Devices", node.Walk().Count(n => n.Kind == "Device").ToString(), "Devices connected through this host, directly or behind hubs.");
            Field("Connector", NodeVisuals.Connector(node), "The host's sockets are its root ports, each drawn on its card as the kind of socket it is.");
            Field("Location", controller.Location == UsbC.TunneledLocation ? UsbC.TunneledLocation : "Host hardware", controller.LocationEvidence);
            Field("Power source", node.PowerSource, "Host controllers run on the computer's own power.");
            // The root hub is what Device Manager lists, and what sim hardware guides point to.
            Field("Power saving", PowerSavingText(MergedRoot(node) ?? node), PowerSavingHelp);
            Field("Power plan", PowerSaving.PlanSummary(snapshot), "The power plan's USB selective suspend setting. While it's off, Windows suspends no USB device, whatever each device's own power-saving setting says.");
            Field("Power available", "Unknown · not measured", SupplyHelp);
        }
        Section("Upstream path");
        // A merged root hub is part of its host, so the path reads H01 › 03.
        var chain = FindPath(node.Id).Where(n => n.Id == node.Id || !mergedHosts.ContainsKey(n.Id)).ToList();
        var pathRow = new WrapPanel();
        foreach (var ancestor in chain)
        {
            var row = new Button { Content = ancestor.Kind == "Controller" ? pathLabels.GetValueOrDefault(ancestor.Id, "Host") : ancestor.Kind == "Root hub" ? "Root" : ancestor.Port.ToString("00"), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 4), ToolTip = ancestor.DisplayName, Foreground = Brush(ancestor.Id == node.Id ? "Accent" : "TextSecondary") };
            row.Click += (_, _) => { SelectNode(ancestor); LocateClick(this, new RoutedEventArgs()); };
            pathRow.Children.Add(row);
            if (ancestor != chain.Last()) pathRow.Children.Add(new TextBlock { Text = "›", Foreground = Brush("TextMuted"), Margin = new Thickness(0, 3, 4, 0) });
        }
        Details.Children.Add(pathRow);
        AddLabelEditor(node);
        var relationship = HubRelationships.Description(node, snapshot);
        if (relationship.Length > 0)
        {
            Text("How this hub is connected", 14, "TextPrimary");
            Text(relationship, 13, "TextSecondary");
            var companion = snapshot.Nodes.FirstOrDefault(n => n.Id == node.CompanionHubId);
            if (companion != null)
            {
                var pair = new Button { Content = "View " + (node.IsUsb2Companion ? "USB 3" : "USB 2") + " side", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
                pair.Click += (_, _) => ShowOnCanvas(companion);
                Details.Children.Add(pair);
            }
        }
        var evidence = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var notes = new List<string> { node.LocationEvidence };
        notes.Add("Name source: " + node.NameSource + ".");
        if (node.ReportedProduct.Length > 0) notes.Add("USB product string: " + node.ReportedProduct);
        if (node.WindowsName.Length > 0) notes.Add("Windows name: " + node.WindowsName);
        if (node.WindowsManufacturer.Length > 0) notes.Add("Windows INF manufacturer: " + node.WindowsManufacturer + " (may identify the driver supplier).");
        if (node.LookupVendor.Length > 0) notes.Add("USB ID vendor: " + node.LookupVendor);
        if (node.LookupProduct.Length > 0) notes.Add("USB ID product: " + node.LookupProduct);
        if (node.NameSource.Contains("lookup", StringComparison.OrdinalIgnoreCase)) notes.Add("This device doesn't report its own product name, so its name comes from the public USB ID database. That usually names the maker of the chip inside, such as Realtek, rather than the brand of the hub, dock or monitor. Click the name or pencil in Properties to name it yourself.");
        else if (node.LookupVendor.Length > 0) notes.Add("USB ID database entries usually name the maker of the chip inside rather than the retail brand.");
        if (host) notes.Add("Port support summarizes reported logical-port capabilities, including empty ports. USB revision and a single negotiated upstream link do not apply to this host summary.");
        if (node.Kind is "Controller" or "Root hub" or "Hub" or "Empty port") notes.Add("Supply capacity, USB-C charging limits and Power Delivery contracts are not queried. Device-declared draw is not the hub's available supply.");
        if (node.Kind == "Device") notes.Add(node.TypeEvidence);
        if (Interference.IsReceiver(node)) notes.Add("Taken to be a 2.4 GHz wireless receiver: " + (node.DeviceType == "Wireless" ? "it's a wireless controller, such as Bluetooth." : "its name says receiver, dongle or wireless.") + " USB 3 devices beside one can interfere with it.");
        if (node.InterfaceFunctions.Count > 0) notes.Add("Reported functions: " + string.Join(", ", node.InterfaceFunctions));
        if (node.HidUsages.Count > 0) notes.Add("HID collections: " + string.Join(", ", node.HidUsages) + ".");
        notes.AddRange(Billboard.Evidence(node));
        notes.AddRange(UsbC.Socket(snapshot, node).Select(f => $"{f.Feature}: {f.Text}"));
        if (host) notes.AddRange(UsbC.Computer(snapshot));
        notes.AddRange(Containers.Evidence(snapshot, node, n => pathLabels.GetValueOrDefault(n.Id, "")));
        if (host) notes.Add(PowerSaving.PlanNote(snapshot));
        var knownLinks = chain.Where(n => n.LinkMbps.HasValue).ToList();
        if (knownLinks.Count > 0) notes.Add($"Known path ceiling: {knownLinks.Min(n => n.LinkMbps):0.##} Mb/s, shared and before overhead.");
        if (node.Kind is "Hub" or "Root hub")
        {
            var connected = node.Children.Where(n => n.Status == "Connected").ToList();
            notes.Add($"Direct children's declared draw: {connected.Sum(n => n.MaxPowerMa ?? 0)} mA known; {connected.Count(n => n.MaxPowerMa == null)} unknown. Excludes devices behind child hubs; not a supply measurement.");
        }
        if (node.OpenPipes.Count > 0) notes.Add("Open pipes: " + string.Join("; ", node.OpenPipes) + ".");
        notes.Add("Port protocols: " + node.Protocols);
        if (host && (node.Kind == "Controller" ? node : snapshot.Controllers.FirstOrDefault(c => c.Children.Contains(node))) is { PciId.Length: > 0 } chip)
            notes.Add($"PCI identity: {chip.PciId}{(chip.PciSubsystem.Length > 0 ? ", subsystem " + chip.PciSubsystem : "")}{(chip.PciRevision.Length > 0 ? ", revision " + chip.PciRevision : "")}{(chip.PciAddress.Length > 0 ? ", at bus:device.function " + chip.PciAddress : "")}.");
        if (node.PortIsDebugCapable == true) notes.Add("Windows reports this port as debug capable: it can serve as the host's USB debug port.");
        if (node.PortHasMultipleCompanions == true) notes.Add("Windows reports that this port's socket has more than one other port.");
        if (host) notes.Add("Connector graphics identify the upstream socket. The cable and device-end plug are unknown.");
        else
        {
            notes.Add("Socket: " + NodeVisuals.SocketLabel(node) + ". " + node.SocketEvidence + " The cable and device-end plug are unknown.");
            if (node.CompanionId.Length > 0 && pathLabels.TryGetValue(node.CompanionId, out var companion))
                notes.Add($"Shares this socket with port {companion}: USB 3 sockets have a USB 2 and a USB 3 logical port, and both are drawn as the socket they share" + (SocketPartner(node) != null ? ", side by side as one socket split at a seam." : "."));
        }
        if (SharedSockets(node) is { Count: > 0 } shared) notes.Add(SharedSocketsHelp(shared));
        notes.AddRange(node.Notes);
        foreach (var note in notes) evidence.Children.Add(new TextBlock { Text = note, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 10) });
        Details.Children.Add(new Expander { Header = "Detection details", Content = evidence, Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 16, 0, 0), FontSize = 12 });
        Details.Children.Add(new Border { Height = 1, Background = Brush("Divider"), Margin = new Thickness(0, 20, 0, 16) });
        Text("Link rates are shared signaling limits. Reserved bandwidth, polling rates and requested power come from device descriptors, not live measurements. Power checks compare declared draw with what the USB specification guarantees a port; supply capacity itself is not measured.", 11, "TextMuted");

    }
    private string PowerSavingText(UsbNode n) => Topology.PowerSavingText(n, snapshot);
    private List<UsbNode> FindPath(string id) => Topology.FindPath(snapshot, id);
    private void Text(string value, double size = 13, string color = "TextPrimary") => Details.Children.Add(new TextBlock { Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) });
    // Issue types whose explanations have been opened. They stay open as the selection changes, so clicking
    // from port to port keeps the rows below them in place.
    private readonly HashSet<string> openExplanations = [];
    private const string ExplanationMoreTag = "explanation-more", ExplanationToggleTag = "explanation-toggle";
    // What an issue means, in a panel under the status badges: what is happening, whether it affects
    // anything now, and what to do. Severity sets how much shows before the rows: a note affects nothing
    // now, so it is one line until opened; a warning says what is happening and whether it affects you,
    // with what to do a click away; an error is shown whole. With several issues each panel names its own.
    private const string ShowLostHubTag = "show-lost-hub";
    // An explanation that isn't one of the node's issues, such as what an empty socket half is for, is given.
    private void AddExplanation(UsbNode node, Severity severity, string issue, bool named, Explanations.Explanation? given = null)
    {
        var e = given ?? Explain(node, issue);
        bool open = severity == Severity.Error || openExplanations.Contains(issue);
        var body = new StackPanel();
        var more = new StackPanel { Tag = ExplanationMoreTag };
        TextBlock Line(string text, string color = "TextPrimary", bool heading = false) => new()
        {
            Text = text, FontSize = heading ? 12 : 13, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(color),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, heading ? 4 : 0, 0, heading ? 2 : 6)
        };
        if (named) body.Children.Add(Line(issue, heading: true));
        var what = Line(e.What);
        bool hasMore = e.Affects.Length > 0 || e.Cause.Length > 0 || e.Steps is { Count: > 0 };
        if (severity == Severity.Note && !open && hasMore) { what.TextWrapping = TextWrapping.NoWrap; what.TextTrimming = TextTrimming.CharacterEllipsis; what.ToolTip = e.What; }
        body.Children.Add(what);
        var affects = severity == Severity.Note ? more : body;
        if (e.Affects.Length > 0) { affects.Children.Add(Line("Does it affect you?", "TextSecondary", true)); affects.Children.Add(Line(e.Affects)); }
        if (e.Cause.Length > 0 || e.Steps is { Count: > 0 })
        {
            more.Children.Add(Line(e.Steps is { Count: > 0 } ? "What to do" : "Why", "TextSecondary", true));
            if (e.Cause.Length > 0) more.Children.Add(Line(e.Cause));
            foreach (var step in e.Steps ?? [])
            {
                var row = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
                var bullet = Line("•", "TextSecondary"); bullet.Margin = new Thickness(0, 0, 7, 6); DockPanel.SetDock(bullet, Dock.Left);
                row.Children.Add(bullet); row.Children.Add(Line(step));
                more.Children.Add(row);
            }
        }
        // The rest stays in the panel while closed, so it is still read aloud once opened.
        more.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(more);
        if (severity != Severity.Error && more.Children.Count > 0)
        {
            string label = open ? "Show less" : severity == Severity.Note ? "Details" : e.Steps is { Count: > 0 } ? "What to do" : "Why";
            var toggle = new Button
            {
                Content = new TextBlock { Text = label + (open ? " ▴" : " ▾"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("Accent") },
                Style = (Style)FindResource("EditableNameButton"), Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(-3, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Tag = ExplanationToggleTag
            };
            System.Windows.Automation.AutomationProperties.SetName(toggle, (open ? "Show less about " : "Show more about ") + issue);
            toggle.Click += (_, _) => { if (!openExplanations.Remove(issue)) openExplanations.Add(issue); ShowDetails(); };
            body.Children.Add(toggle);
        }
        Details.Children.Add(new Border
        {
            Child = body, Background = Brush("Subtle"), BorderBrush = Brush("Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 9, 11, 3), Margin = new Thickness(0, 0, 0, 10), Tag = "warning:" + issue
        });
    }
    private void Section(string title)
    {
        Details.Children.Add(new Border { Height = 1, Background = Brush("Divider"), Margin = new Thickness(0, 9, 0, 8) });
        Details.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 7), Tag = "section" });
    }
    // Values stay on one line (full text in the tooltip) so every row keeps its height; missing values are muted.
    // Plain row names; where a technical term sits behind one, the label's tooltip names it.
    private const string PathHelp = "The host, then each port number on the way here. Windows numbers the USB 2 and USB 3 halves of a USB 3 socket as separate logical ports.";
    private const string PortsHelp = "Logical ports Windows reports. On the computer, a USB 3 socket counts as two, one USB 2 and one USB 3; a USB 3 hub instead appears as two hubs, each with one port per socket.";
    private const string PollingHelp = "How often the host asks it for input, as its endpoint descriptor requests. Not a measured report rate: a device skips a poll when it has nothing new.";
    private const string PowerSavingHelp = "Device Manager's “Allow the computer to turn off this device to save power”. When it's on and the power plan's USB selective suspend is on, Windows may suspend the device when it looks idle.";
    private const string LinkUseHelp = "How much of the time this link can set aside for timed transfers, such as audio, video and input, is held now. Bulk transfers, such as storage, reserve nothing and share what is left.";
    private const string SharedLinkHelp = "How much of the shared 12 Mb/s link's reservable time (10.8 Mb/s) the slower devices on every port hold. Low-speed devices count eight times their payload, since each byte takes eight times as long.";
    private const string ConnectorHelp = "The socket it's plugged into, as Windows reports it: USB-A, USB-C, or built in with no socket. The tongue's color is the fastest speed the socket is known to carry.";
    private const string SupplyHelp = "How much power the port can supply. Windows doesn't report it, so USB Atlas can't tell.";
    private void Field(string label, string value, string help)
    {
        bool missing = value is NotApplicable or "Not reported" || value.StartsWith("Unknown", StringComparison.Ordinal);
        Field(label, new TextBlock { Text = value, FontSize = 13, Foreground = Brush(missing ? "TextMuted" : "TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value }, help);
    }
    private void Field(string label, FrameworkElement value, string help)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Tag = "field", MinHeight = 18 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = help });
        value.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(value, 1); row.Children.Add(value);
        Details.Children.Add(row);
    }
    // The zoom readout is the one 100% control: full cards at actual size, laid out again for the window.
    private void ActualSizeClick(object sender, RoutedEventArgs e) { FitClick(this, new RoutedEventArgs()); RevealSelection(); }
    // Shift+0 shows 100%, Shift+1 fits everything and Shift+2 centers the selection.
    private bool FramingShortcut(Key key)
    {
        switch (key)
        {
            case Key.D0 or Key.NumPad0: ActualSizeClick(this, new RoutedEventArgs()); return true;
            case Key.D1 or Key.NumPad1: OverviewClick(this, new RoutedEventArgs()); return true;
            case Key.D2 or Key.NumPad2: LocateClick(this, new RoutedEventArgs()); return true;
            case Key.L: LayoutChoiceClick(horizontalTree ? VerticalLayoutButton : HorizontalLayoutButton, new RoutedEventArgs()); return true;
            default: return false;
        }
    }
    // Ctrl+= and Ctrl+− zoom by a step around the view's center, as the + and − buttons do.
    private bool ZoomShortcut(Key key)
    {
        switch (key)
        {
            case Key.OemPlus or Key.Add: ZoomIn(this, new RoutedEventArgs()); return true;
            case Key.OemMinus or Key.Subtract: ZoomOut(this, new RoutedEventArgs()); return true;
            default: return false;
        }
    }
    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        ApplyAppearance(!Theme.IsDark);
        if (!Theme.Save()) StatusText.Text = "Appearance changed; preference could not be saved.";
    }
    private void ApplyAppearance(bool dark)
    {
        var x = GraphScroll.HorizontalOffset; var y = GraphScroll.VerticalOffset;
        Theme.Apply(dark);
        ThemeButton.Content = dark ? "Light mode" : "Dark mode";
        Draw(); ShowDetails(); UpdateIssues(); GraphScroll.UpdateLayout();
        GraphScroll.ScrollToHorizontalOffset(x); GraphScroll.ScrollToVerticalOffset(y); GraphScroll.UpdateLayout();
    }
    private void LocateClick(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        // Locating shows the selection at full size, sockets and all.
        if (detail != CardDetail.Full) { detail = CardDetail.Full; Draw(); }
        var targetId = selected.Kind == "Empty port" ? FindPath(selected.Id).SkipLast(1).Select(DrawnAs).LastOrDefault()?.Id : DrawnAs(selected).Id;
        if (targetId == null || !cards.TryGetValue(targetId, out var item)) return;
        ResetPan();
        SetZoom(1); GraphScroll.UpdateLayout();
        var center = selected.Kind == "Empty port" && portAnchors.TryGetValue(selected.Id, out var anchor)
            ? anchor : new Point(item.Point.X + item.Card.Width / 2, item.Point.Y + item.Card.Height / 2);
        GraphScroll.ScrollToHorizontalOffset(Math.Max(0, center.X - GraphScroll.ViewportWidth / 2));
        GraphScroll.ScrollToVerticalOffset(Math.Max(0, center.Y - GraphScroll.ViewportHeight / 2));
        GraphScroll.UpdateLayout();
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh();
    // Auto-refresh lives with Refresh: the chevron beside it opens a menu that turns it on and off, and a dot
    // on Refresh says it's on.
    private bool autoRefresh;
    private const string AutoRefreshItem = "Auto-refresh every 10 s";
    private ContextMenu RefreshMenu()
    {
        var menu = new ContextMenu { Background = Brush("Surface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("Border"), PlacementTarget = RefreshMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var item = new MenuItem { Header = AutoRefreshItem, IsCheckable = true, IsChecked = autoRefresh };
        item.Click += (_, _) => SetAutoRefresh(item.IsChecked);
        menu.Items.Add(item);
        return menu;
    }
    private void RefreshMenuClick(object sender, RoutedEventArgs e) => RefreshMenu().IsOpen = true;
    private void SetAutoRefresh(bool on)
    {
        autoRefresh = on;
        AutoRefreshDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ShowRefreshTips();
    }
    private void ShowRefreshTips()
    {
        RefreshButton.ToolTip = ShortcutTip(autoRefresh ? "Refresh · auto on" : "Refresh", "F5");
        RefreshMenuButton.ToolTip = "Auto-refresh";
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { searchTimer.Stop(); searchTimer.Start(); }
    // Search stays centered in the title bar, narrowing rather than running under the buttons on either side.
    private void TitleBarSizeChanged(object sender, SizeChangedEventArgs e) =>
        SearchBox.Width = Math.Clamp(TitleBar.ActualWidth - 2 * Math.Max(TitleLeft.ActualWidth, TitleRight.ActualWidth) - 32, 160, 340);
    private void SetZoom(double value) { readableView = overviewView = false; value = Math.Clamp(value, 0.15, 2); GraphScale.ScaleX = GraphScale.ScaleY = value; }
    private void ZoomIn(object sender, RoutedEventArgs e) => ZoomAt(GraphScale.ScaleX * 1.2, new Point(GraphScroll.ViewportWidth / 2, GraphScroll.ViewportHeight / 2));
    private void ZoomOut(object sender, RoutedEventArgs e) => ZoomAt(GraphScale.ScaleX / 1.2, new Point(GraphScroll.ViewportWidth / 2, GraphScroll.ViewportHeight / 2));
    private void FitClick(object sender, RoutedEventArgs e)
    {
        if (arranging) return;
        arranging = true;
        try
        {
            ResetPan(); detail = CardDetail.Full;
            double scale = ReadingScale;
            layoutWidth = Math.Max(CardWidth + 48, (GraphScroll.ActualWidth - 32) / scale);
            Draw(); SetZoom(scale); readableView = true;
            GraphScroll.UpdateLayout();
            // At reading size, open on the selected path rather than an arbitrary corner.
            var host = snapshot.Controllers.Where(c => cards.ContainsKey(c.Id)).Select(c => cards[c.Id]).FirstOrDefault();
            double across = host.Card == null ? 0 : horizontalTree ? host.Point.Y + host.Card.Height / 2 - GraphScroll.ViewportHeight / 2 : host.Point.X + host.Card.Width / 2 - GraphScroll.ViewportWidth / 2;
            if (horizontalTree) { GraphScroll.ScrollToHorizontalOffset(0); GraphScroll.ScrollToVerticalOffset(Math.Max(0, across)); }
            else GraphScroll.ScrollToHorizontalOffset(Math.Max(0, across));
            GraphScroll.UpdateLayout();
        }
        finally { arranging = false; }
    }
    // Fit all shows the whole graph at the most detailed level that fits at a readable scale, or at the
    // farthest level, scaled to fit, when none does.
    private void OverviewClick(object sender, RoutedEventArgs e)
    {
        if (arranging) return;
        arranging = true;
        try
        {
            ResetPan();
            foreach (var level in Enum.GetValues<CardDetail>()) { detail = level; Draw(); if (FitScale() >= ReadableScale) break; }
            SetZoom(Math.Min(1, FitScale())); overviewView = true;
            GraphScroll.ScrollToHorizontalOffset(0); GraphScroll.ScrollToVerticalOffset(0);
        }
        finally { arranging = false; }
    }
    private double FitScale() => Math.Min((GraphScroll.ViewportWidth - 32) / Graph.Width, (GraphScroll.ViewportHeight - 32) / Graph.Height);
    // Layout is a two-way choice, so both options show, the current one is marked, and one click switches.
    private void LayoutChoiceClick(object sender, RoutedEventArgs e)
    {
        bool horizontal = sender == HorizontalLayoutButton;
        if (horizontal == horizontalTree) return;
        SetOrientation(horizontal);
        if (!SaveLayout(horizontal)) StatusText.Text = "Layout changed; preference could not be saved.";
    }
    private void ShowLayoutChoice()
    {
        foreach (var (button, horizontal) in new[] { (HorizontalLayoutButton, true), (VerticalLayoutButton, false) })
        {
            bool chosen = horizontalTree == horizontal;
            if (chosen) { button.SetResourceReference(BackgroundProperty, "SelectionStrong"); button.SetResourceReference(BorderBrushProperty, "Accent"); }
            else { button.ClearValue(BackgroundProperty); button.ClearValue(BorderBrushProperty); }
            Panel.SetZIndex(button, chosen ? 1 : 0);
            button.FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, chosen ? "Selected" : "");
        }
    }
    private void SetOrientation(bool horizontal)
    {
        horizontalTree = horizontal;
        ShowLayoutChoice();
        FitClick(this, new RoutedEventArgs());
    }
    private void GraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (arranging || snapshot.Controllers.Count == 0) return;
        bool wider = Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 24, taller = Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 24;
        // A readable view reflows to the new width; an overview fits again, choosing its level anew.
        if (readableView && (wider || horizontalTree && taller)) FitClick(this, new RoutedEventArgs());
        else if (overviewView && (wider || taller)) OverviewClick(this, new RoutedEventArgs());
    }
    private void ZoomAt(double zoom, Point pointer)
    {
        if (SwitchDetail(zoom, pointer)) return;
        var anchor = GraphScroll.TranslatePoint(pointer, Graph);
        SetZoom(zoom); GraphScroll.UpdateLayout();
        var after = Graph.TranslatePoint(anchor, GraphScroll);
        PanTransform.X += pointer.X - after.X;
        PanTransform.Y += pointer.Y - after.Y;
    }
    private void GraphWheel(object sender, MouseWheelEventArgs e)
    {
        if (panButton == null) ZoomAt(GraphScale.ScaleX * Math.Pow(1.12, e.Delta / 120.0), e.GetPosition(GraphScroll));
        e.Handled = true;
    }
    private void PanStart(object sender, MouseButtonEventArgs e)
    {
        if (panButton != null || e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        for (var hit = e.OriginalSource as DependencyObject; hit != null && hit != GraphScroll;
             hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is System.Windows.Controls.Primitives.ScrollBar) return;
            if (e.ChangedButton == MouseButton.Left && hit is FrameworkElement { Tag: UsbNode }) return;
        }
        if (!GraphScroll.CaptureMouse()) return;
        panButton = e.ChangedButton; panStart = e.GetPosition(GraphScroll);
        panOrigin = new Point(PanTransform.X, PanTransform.Y);
        readableView = overviewView = false; GraphScroll.Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void PanMove(object sender, MouseEventArgs e)
    {
        if (panButton == null) return;
        var current = e.GetPosition(GraphScroll);
        PanTransform.X = panOrigin.X + current.X - panStart.X;
        PanTransform.Y = panOrigin.Y + current.Y - panStart.Y;
        e.Handled = true;
    }
    private void PanEnd(object sender, MouseButtonEventArgs e)
    {
        if (panButton != e.ChangedButton) return;
        FinishPan(); e.Handled = true;
    }
    private void PanCaptureLost(object sender, MouseEventArgs e) => FinishPan();
    private void FinishPan()
    {
        panButton = null; GraphScroll.Cursor = null;
        if (GraphScroll.IsMouseCaptured) GraphScroll.ReleaseMouseCapture();
    }
    private void ResetPan() { FinishPan(); PanTransform.X = PanTransform.Y = 0; }
    private void VerifyUi()
    {
        VerifyWireRouting();
        VerifyCrowdedRouting();
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        foreach (var (key, weight) in new[] { ("UiFont", FontWeights.Normal), ("UiFont", FontWeights.SemiBold), ("UiFont", FontWeights.Bold), ("MonoFont", FontWeights.Normal) })
        {
            var face = new Typeface((FontFamily)FindResource(key), FontStyles.Normal, weight, FontStretches.Normal);
            Check(face.TryGetGlyphTypeface(out var glyph) && glyph.FontUri.ToString().Contains("Assets/Fonts/", StringComparison.OrdinalIgnoreCase), $"{key} {weight} did not load the bundled font.");
            Check(glyph.Weight == weight, $"{key} {weight} did not resolve the intended font weight.");
        }
        var visible = cards.Values.ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            var rect = new Rect(visible[i].Point, new Size(visible[i].Card.Width, visible[i].Card.Height));
            Check(rect.Right <= Graph.Width && rect.Bottom <= Graph.Height, "Card extends outside graph bounds.");
            for (int j = i + 1; j < visible.Count; j++)
                Check(!rect.IntersectsWith(new Rect(visible[j].Point, new Size(visible[j].Card.Width, visible[j].Card.Height))), "Hardware cards overlap.");
        }
        foreach (var node in snapshot.Nodes.Where(n => cards.ContainsKey(n.Id)))
            foreach (var child in Children(node))
                Check(horizontalTree ? cards[child.Id].Point.X > cards[node.Id].Point.X + cards[node.Id].Card.Width : cards[child.Id].Point.Y > cards[node.Id].Point.Y + cards[node.Id].Card.Height, "Child must follow its parent's flow direction.");
        var target = snapshot.Nodes.FirstOrDefault(n => n.Kind == "Device");
        if (target != null)
        {
            Search.Text = target.Name; ApplySearch();
            Check(cards.ContainsKey(target.Id), "Search lost the matching device.");
            Check(FindPath(target.Id).All(n => cards.ContainsKey(DrawnAs(n).Id)), "Search lost a matching device's ancestors.");
            Search.Text = "__usb_atlas_no_match__"; ApplySearch();
            Check(cards.Count == 0 && EmptyMessage.Visibility == Visibility.Visible, "Empty search state is not visible.");
            Search.Text = ""; ApplySearch();
        }
        var root = snapshot.Controllers.FirstOrDefault();
        if (root != null)
        {
            folded.Add(root.Id); Draw();
            Check(cards.ContainsKey(root.Id) && root.Walk().All(n => n == root || !cards.ContainsKey(n.Id)), "Collapsed branch still shows descendants.");
            folded.Remove(root.Id); Draw();
        }
        if (selected != null)
        {
            LocateClick(this, new RoutedEventArgs());
            Check(GraphScale.ScaleX == 1, "Locate should restore readable zoom.");
            var position = cards[selected.Id].Point;
            Check(position.X + cards[selected.Id].Card.Width > GraphScroll.HorizontalOffset && position.X < GraphScroll.HorizontalOffset + GraphScroll.ViewportWidth, "Selected card is horizontally outside the viewport.");
            Check(position.Y + cards[selected.Id].Card.Height > GraphScroll.VerticalOffset && position.Y < GraphScroll.VerticalOffset + GraphScroll.ViewportHeight, "Selected card is vertically outside the viewport.");
        }
        OverviewClick(this, new RoutedEventArgs()); GraphScroll.UpdateLayout();
        Check(Graph.Width * GraphScale.ScaleX <= GraphScroll.ViewportWidth + 1 && Graph.Height * GraphScale.ScaleY <= GraphScroll.ViewportHeight + 1, "Fit all leaves graph outside viewport.");
        FitClick(this, new RoutedEventArgs()); GraphScroll.UpdateLayout();
        Check(GraphScale.ScaleX >= 1, "Readable view must not shrink device text.");
        PanTransform.X = 87; PanTransform.Y = 53;
        var pointer = new Point(GraphScroll.ViewportWidth * 0.4, GraphScroll.ViewportHeight * 0.4);
        var anchored = GraphScroll.TranslatePoint(pointer, Graph);
        ZoomAt(GraphScale.ScaleX * 1.15, pointer);
        Check((Graph.TranslatePoint(anchored, GraphScroll) - pointer).Length < 1, "Wheel zoom moved the point under the pointer.");
        // Zooming out past the readable scale swaps in simpler cards, and the card under the pointer, or the
        // nearest one, keeps its place on screen.
        static double Away(Rect r, Point p) => new Vector(Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right)), Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom))).Length;
        Rect Box(string id) => new(cards[id].Point, new Size(cards[id].Card.Width, cards[id].Card.Height));
        Point Onscreen(string id) => Graph.TranslatePoint(new Point(Box(id).X + Box(id).Width / 2, Box(id).Y + Box(id).Height / 2), GraphScroll);
        var near = cards.Keys.MinBy(id => Away(Box(id), GraphScroll.TranslatePoint(pointer, Graph)))!;
        var held = Onscreen(near);
        ZoomAt(0.25, pointer);
        Check(detail == CardDetail.Compact && GraphScale.ScaleX >= ReadableScale && (Onscreen(near) - held).Length < 1, "Zooming out must swap in simpler cards and keep the card near the pointer in place.");
        FitClick(this, new RoutedEventArgs());
        Check(PanTransform.X == 0 && PanTransform.Y == 0, "Readable view must reset free panning.");
        var selection = selected?.Id;
        var collapsed = folded.ToHashSet();
        SetOrientation(!horizontalTree);
        Check(selected?.Id == selection && folded.SetEquals(collapsed), "Changing direction lost selection or folded branches.");
        var (on, off) = horizontalTree ? (HorizontalLayoutButton, VerticalLayoutButton) : (VerticalLayoutButton, HorizontalLayoutButton);
        Check(on.Background == Brush("SelectionStrong") && on.BorderBrush == Brush("Accent") && off.Background is SolidColorBrush { Color.A: 0 } && off.BorderBrush is SolidColorBrush { Color.A: 0 }, "The layout control must mark the current layout and only it.");
        var switched = cards.Values.ToList();
        for (int i = 0; i < switched.Count; i++)
            for (int j = i + 1; j < switched.Count; j++)
                Check(!new Rect(switched[i].Point, new Size(switched[i].Card.Width, switched[i].Card.Height)).IntersectsWith(new Rect(switched[j].Point, new Size(switched[j].Card.Width, switched[j].Card.Height))), "Cards overlap after changing direction.");
        foreach (var node in snapshot.Nodes.Where(n => cards.ContainsKey(n.Id)))
            foreach (var child in Children(node))
                Check(horizontalTree ? cards[child.Id].Point.X > cards[node.Id].Point.X + cards[node.Id].Card.Width : cards[child.Id].Point.Y > cards[node.Id].Point.Y + cards[node.Id].Card.Height, "Changed direction has incorrect parent-child placement.");
        SetOrientation(!horizontalTree);
    }
    private void CaptureUi(string filename)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(filename); png.Save(stream);
    }
    // --render --select TEXT previews Properties for the first node whose search text matches.
    public string? RenderSelection { get; init; }
    private async Task RenderPreview()
    {
        await Task.Delay(400);
        OpenInitialView(); UpdateLayout();
        if (RenderSelection != null && snapshot.Nodes.FirstOrDefault(n => Topology.SearchText(n, pathLabels.GetValueOrDefault(n.Id)).Contains(RenderSelection, StringComparison.OrdinalIgnoreCase)) is UsbNode chosen)
        {
            SelectNode(chosen); UpdateLayout();
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create("preview.png")) png.Save(stream);
        Close();
    }
}

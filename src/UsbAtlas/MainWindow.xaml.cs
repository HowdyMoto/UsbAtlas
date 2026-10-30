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
                try { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; detail = CardDetail.Full; overviewView = false; Draw(); VerifySmallWindow(); VerifySearchInput(); VerifyWarningExplanation(); VerifySpeedExplanation(); VerifyUi(); VerifyDeviceTree(); VerifyCompactUi(); VerifyIdentityUi(); VerifyInspectorConsistency(); VerifySeverityExplanations(); VerifyPowerUi(); VerifyCanvasNaming(); VerifyPortTags(); VerifyPortTagSides(); await VerifyRefreshUi(); await VerifyTreeCanvasSync(); await VerifyDeviceWatch(); await VerifyObjectConstancy(); VerifyRedesignedUi(); VerifyHubSnapping(); VerifyPairedHubs(); VerifyCardTitles(); VerifySemanticZoom(); VerifyEverythingExplains(); VerifyFixFirst(); VerifyActivityFeed(); VerifyLegendWording(); VerifyRememberedDevices(); VerifyKnownProblems(); VerifyCharging(); File.WriteAllText("ui-test.txt", "UI checks passed: remembered devices on other ports, known chip problems, charging needs and falling batteries, what to fix first and the exported image, the activity feed, everything on the graph explains itself on hover or click, device tree selection/filtering/collapse, tree and canvas selection sync, planar wire routing, layout, filtering, folding, focus, fit, variable-height cards, merged host cards, sockets, search navigation, issues, power and stability issues, link, polling and power figures, power saving, bandwidth meters, inspector and its consistent layout, explanations sized by severity, semantic zoom and opening on the whole topology, paired hubs drawn as one card, two-line card titles, the tree folding for a narrow window, a wrapping legend, animated topology changes, saved labels, port name tags that span paired sockets, show in full on hover or selection, name both sides of a merged hub and read in full on an unplugged socket, host capabilities, selection reuse, refresh feedback, device-change rescans and wake notifications suppressing false unstable connections."); }
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
            batteries.Apply(next, DateTime.Now);
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


        var chain = FindPath(node.Id).Where(n => n.Id == node.Id || !mergedHosts.ContainsKey(n.Id)).ToList();
        AddBreadcrumb(node);
        if (!host) { AddUpstream(node); AddConnection(node); AddDeviceProperties(node); AddSocketDetails(node); }
        else AddHostProperties(node);
        AddDownstream(node);
        AddLabelEditor(node);
        var relationship = HubRelationships.Description(node, snapshot);
        if (relationship.Length > 0)
        {
            Text("How this hub is connected", 14, "TextPrimary");
            Text(relationship, 13, "TextSecondary");
        }
        if (node.OtherEntries.Count > 0)
        {
            Text("Other entries for this device", 14, "TextPrimary");
            foreach (var other in node.OtherEntries) Text(Remembered.Line(other), 13, "TextSecondary");
            Text("Windows keeps one for each port a device without a serial number has been plugged into, each with its own settings and COM number. To remove old ones: Device Manager › View › Show hidden devices, then uninstall the grayed-out entries.", 12, "TextMuted");
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
        if (node.Kind is "Controller" or "Root hub" or "Hub" or "Empty port") notes.Add(node.TypeCPowerMode.Length > 0
            ? "This port's USB-C power is what Linux reports it offering, not a measurement or the contract it agreed with what's plugged in. Device-declared draw is not the hub's available supply."
            : "Windows doesn't report supply capacity, USB-C charging limits or Power Delivery contracts; only a socket's shape bounds them. Device-declared draw is not the hub's available supply.");
        notes.AddRange(PowerNeeds.Evidence(node));
        if (Battery.Evidence(node) is { Length: > 0 } batteryNote) notes.Add(batteryNote);
        if (node.Kind == "Device") notes.Add(node.TypeEvidence);
        if (Interference.IsReceiver(node)) notes.Add("Taken to be a 2.4 GHz wireless receiver: " + (node.DeviceType == "Wireless" ? "it's a wireless controller, such as Bluetooth." : "its name says receiver, dongle or wireless.") + " USB 3 devices beside one can interfere with it.");
        if (KnownProblems.Evidence(node) is { Length: > 0 } known) notes.Add(known);
        if (node.InterfaceFunctions.Count > 0) notes.Add("Reported functions: " + string.Join(", ", node.InterfaceFunctions));
        if (node.HidUsages.Count > 0) notes.Add("HID collections: " + string.Join(", ", node.HidUsages) + ".");
        if (node.ComPort.Length > 0) notes.Add($"Serial port: Windows calls it {node.ComPort}.");
        foreach (var other in node.OtherEntries) notes.Add($"Other entry: {other.Entry.InstanceId}, {Remembered.Line(other)}.");
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
    // The driver Windows loaded and whether Device Manager shows a problem, as on its General and Driver tabs.
    // Every kind of selection gets the same rows, so flipping between ports keeps them in place.
    private void DriverFields(UsbNode node)
    {
        bool has = node.Kind is "Device" or "Hub" or "Controller" or "Root hub";
        string Value(string v) => !has ? node.Kind == "Unavailable" ? "Unknown" : NotApplicable : v.Length > 0 ? v : "Not reported";
        // A host's card also stands for its merged root hub, whose problems its badge already shows.
        var problems = node.DriverProblems.Count > 0 ? node.DriverProblems : node.Kind == "Controller" ? MergedRoot(node)?.DriverProblems ?? [] : [];
        var worst = problems.OrderBy(p => p.Code is 22 or 29 ? 2 : p.Code is 14 or 47 ? 1 : 0).FirstOrDefault();
        Section("Driver");
        Field("Status", worst != null ? $"Code {worst.Code} · {worst.Meaning}" + (problems.Count > 1 ? $" · {problems.Count - 1} more" : "") : Value(has ? "No problem reported" : ""),
            "What Device Manager's General tab says about it: a problem code and what it means, or none. A problem on one of a device's functions, such as its audio or a HID collection, counts as the device's. Detection details lists every one.");
        Field("Driver", Value(Drivers.Summary(node)),
            "Whether Windows' own drivers run it, the same as on any computer, or a maker's driver does, with its version and date. An old maker's driver is the first thing to update when a device misbehaves; "
            + "removing one in Device Manager (Uninstall device, with “Attempt to remove the driver”) falls back to Windows' own. “An app drives it directly” means WinUSB or libusb, often installed by a vendor tool or Zadig: "
            + "Windows doesn't treat it as a keyboard, audio or storage device, only that app can use it. Detection details lists every driver and function.");
        Field("Filters", Value(Drivers.FilterSummary(node)),
            "Filter drivers other programs put in its stack, such as virtual machine USB passthrough, antivirus, backup, encryption, webcam effects or input remapping. "
            + "They see everything the device does, and a faulty or outdated one is a common cause of devices that drop out, fail to start or perform poorly. "
            + "One on every device of its kind sits under all cameras, or all disks. Windows' own filters are listed in Detection details.");
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
            Child = body, Background = Brush("Subtle"), BorderBrush = Brush("Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 11, 3), Margin = new Thickness(0, 0, 0, 10), Tag = "warning:" + issue
        });
    }
    private void Section(string title)
    {
        // A heading is a step larger and bolder than the rows, on a tinted band with more room above than below,
        // so it leads its rows. The band reaches left of the rows so its text lines up with their labels, and stops
        // at their right edge, clear of the scroll bar.
        Details.Children.Add(new Border
        {
            Child = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brush("TextPrimary") },
            Background = Brush("HeadingFill"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 5),
            Margin = new Thickness(-8, 14, 0, 8), Tag = "section"
        });
    }
    // Values stay on one line (full text in the tooltip) so every row keeps its height; missing values are muted.
    // Plain row names; where a technical term sits behind one, the label's tooltip names it.
    private const string PathHelp = "The host, then each port number on the way here. Windows numbers the USB 2 and USB 3 halves of a USB 3 socket as separate logical ports.";
    private const string PortsHelp = "Logical ports Windows reports. On the computer, a USB 3 socket counts as two, one USB 2 and one USB 3; a USB 3 hub instead appears as two hubs, each with one port per socket.";
    private const string PollingHelp = "How often the host asks it for input, as its endpoint descriptor requests. Not a measured report rate: a device skips a poll when it has nothing new.";
    private const string PowerSavingHelp = "Device Manager's “Allow the computer to turn off this device to save power”. When it's on and the power plan's USB selective suspend is on, Windows may suspend the device when it looks idle.";
    private const string LinkUseHelp = "How much of the time this link can set aside for timed transfers, such as audio, video and input, is held now. Bulk transfers, such as storage, reserve nothing and share what is left. "
        + "Cards show this as a meter only where it can fill: on hubs and on devices that stream. A keyboard's or mouse's slice can't grow and a drive's is nothing, so their cards leave it out and this row says which.";
    private const string SharedLinkHelp = "How much of the shared 12 Mb/s link's reservable time (10.8 Mb/s) the slower devices on every port hold. Low-speed devices count eight times their payload, since each byte takes eight times as long.";
    private const string ConnectorHelp = "The socket it's plugged into, as Windows reports it or as you set it in Socket type: USB-A, USB-C, or built in with no socket. The tongue's color is the fastest speed the socket is known to carry.";
    private const string SupplyHelp = "How much power the port can supply. Windows doesn't report it, but a socket bounds it: a USB-A port gives 5 V only, never more than about 15 W, while a USB-C port can give more under Power Delivery, which Windows doesn't show. On Linux, a USB-C port's Power Delivery offers are shown.";
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
    // --render --select TEXT previews Properties for the first node whose search text matches; --focus TEXT first
    // focuses that node's branch, as Focus branch does, so one hub on a busy computer previews at full detail.
    public string? RenderSelection { get; init; }
    public string? RenderFocus { get; init; }
    private async Task RenderPreview()
    {
        await Task.Delay(400);
        OpenInitialView(); UpdateLayout();
        UsbNode? Find(string text) => snapshot.Nodes.FirstOrDefault(n => Topology.SearchText(n, pathLabels.GetValueOrDefault(n.Id)).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (RenderFocus != null && Find(RenderFocus) is UsbNode branch)
        {
            SelectNode(branch); FocusBranchClick(this, new RoutedEventArgs()); selected = null; UpdateSelection(); UpdateLayout();
        }
        if (RenderSelection != null && Find(RenderSelection) is UsbNode chosen)
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

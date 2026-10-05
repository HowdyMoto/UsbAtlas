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
    public MainWindow(bool demo, bool render, bool verifyUi = false, bool horizontal = false)
    {
        InitializeComponent(); this.demo = demo; this.render = render;
        var searchGlyph = NodeVisuals.Symbol("search", Theme.Brush("TextMuted"), 16);
        ((System.Windows.Shapes.Path)((Canvas)searchGlyph.Child).Children[0]).SetResourceReference(Shape.FillProperty, "TextMuted");
        SearchIcon.Content = searchGlyph;
        var copyGlyph = NodeVisuals.Symbol("content_copy", Brush("TextPrimary"), 14);
        ((System.Windows.Shapes.Path)((Canvas)copyGlyph.Child).Children[0]).SetResourceReference(Shape.FillProperty, "TextPrimary");
        CopyDetailsIcon.Content = copyGlyph;
        horizontalTree = horizontal;
        OrientationButton.Content = horizontalTree ? "Layout: horizontal" : "Layout: vertical";
        ThemeButton.Content = Theme.IsDark ? "Light mode" : "Dark mode";
        Loaded += async (_, _) =>
        {
            await Refresh();
            if (verifyUi)
            {
                try { focusedBranch = null; FocusBranchButton.Content = "Focus branch"; Draw(); VerifySearchInput(); VerifyWarningExplanation(); VerifyUi(); VerifyDeviceTree(); VerifyCompactUi(); VerifyIdentityUi(); VerifyInspectorConsistency(); VerifyPowerUi(); VerifyCanvasNaming(); await VerifyRefreshUi(); await VerifyTreeCanvasSync(); await VerifyDeviceWatch(); VerifyRedesignedUi(); VerifyHubSnapping(); File.WriteAllText("ui-test.txt", "UI checks passed: device tree selection/filtering/collapse, tree and canvas selection sync, planar wire routing, layout, filtering, folding, focus, fit, variable-height cards, merged host cards, sockets, search navigation, issues, power and stability issues, link/power figures, bandwidth meters, inspector and its consistent layout, saved labels, host capabilities, selection reuse, refresh feedback and device-change rescans."); }
                catch (Exception ex) { File.WriteAllText("ui-test.txt", ex.ToString()); Application.Current.Shutdown(1); return; }
            }
            if (render) await RenderPreview();
        };
        timer.Tick += async (_, _) => { if (AutoRefresh.IsChecked == true && !demo && !busy) await Refresh(); };
        searchTimer.Tick += (_, _) => ApplySearch();
        SourceInitialized += (_, _) => WatchDevices();
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
            if (changed) { Draw(); ShowDetails(); }
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
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5 || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!e.IsRepeat) await Refresh();
    }
    private static string ShortSpeed(UsbNode n) => n.LinkMbps switch { 5000 => "5 Gb/s", 480 => "480 Mb/s", 12 => "12 Mb/s", 1.5 => "1.5 Mb/s", _ => n.Speed.StartsWith("SuperSpeedPlus") ? "≥10 Gb/s" : "Rate unknown" };
    private const string NotApplicable = "—";
    private static string IssueHelp(string issue) => issue switch
    {
        "Port error" => "Windows could not read this port. A device may still be connected.",
        "Reduced speed" => "A faster link is supported. Check the upstream port, hub and cable.",
        "Could exceed when streaming" => "The devices sharing this link can reserve more, at their peak, than the link can set aside for timed transfers. They fit now because some are idle; once enough cameras, microphones or audio devices start streaming at the same time, Windows may refuse one with Insufficient bandwidth. Move a streaming device to a port on a different hub or controller.",
        "Link nearly full" => "Reservations already hold at least 80% of the bus time this link can set aside for timed transfers such as audio, video and input. Another device of that kind, or one that starts streaming, may be refused with Insufficient bandwidth. Move a busy device to a port on a different hub or controller.",
        "Insufficient bandwidth" => "Windows could not configure this device because the bus cannot reserve the bandwidth it asks for. Move it, or a busy audio or video device on the same controller, to another port.",
        "Insufficient power" => "Windows refused to configure this device because it asks for more power than the port can supply. Connect it to a powered hub or directly to the computer.",
        "Overcurrent" => "The device drew more current than the port allows, so Windows switched the port off. Reconnect it to a powered hub or another port; a damaged cable or device can also cause this.",
        "Power at risk" => "This device declares more current than its port is guaranteed to supply, so it may disconnect or misbehave under load. See Detection details.",
        "Over power budget" => "The devices behind this bus-powered hub declare more current, in total, than its upstream port is guaranteed to supply. See Detection details.",
        "Hub adapter not detected" => "This hub can run from its own power supply but is running on bus power. If it has an adapter, check that it is plugged in.",
        "Unstable connection" => "USB Atlas observed at least three disconnect-and-reconnect cycles within five minutes, each returning within 30 seconds. This records reconnects, not their cause: unplugging, restarting or changing USB modes can trigger it, as can power interruptions or a loose cable. The warning stays until USB Atlas is restarted.",
        _ => "Enumeration is incomplete; counts may omit downstream devices. See Detection details."
    };
    // One layout per kind of selection, so ports can be compared by flipping between them: every port,
    // device and hub shows the same rows in the same places. "—" marks a row that doesn't apply or has
    // nothing attached; "Not reported" marks a value an attached device left out. Content that varies
    // (explanations, actions, evidence) follows the comparable rows.
    private static string WarningExplanation(UsbNode node, string issue)
    {
        var explanation = IssueHelp(issue);
        if (issue == "Reduced speed") explanation += $"\nObserved link: {ShortSpeed(node)}. Windows reports capability for a faster connection; the exact achievable rate is not measured.";
        if (issue == "Unstable connection")
        {
            explanation += $"\nObserved this session: {node.QuickReconnects} quick reconnects.";
            if (node.QuickReconnectTimes.Count > 0)
                explanation += "\nReconnect times: " + string.Join(", ", node.QuickReconnectTimes.Select(t => t.ToString("HH:mm:ss"))) + " (local time).";
        }
        return explanation;
    }
    private Button WarningBadge(UsbNode node, NodeVisuals.Severity severity, string issue)
    {
        var badge = new Button { Content = NodeVisuals.StatusBadge(severity, issue), Style = (Style)FindResource("WarningButton"), Padding = new Thickness(0), Tag = "warning-action" };
        badge.Cursor = Cursors.Hand;
        badge.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(badge, "Explain " + issue);
        void Explain()
        {
            SelectNode(node);
            if (InspectorPanel.Visibility != Visibility.Visible) InspectorClick(this, new RoutedEventArgs());
            UpdateLayout();
            Details.Children.OfType<FrameworkElement>().FirstOrDefault(x => Equals(x.Tag, "warning:" + issue))?.BringIntoView();
        }
        badge.Click += (_, e) => { Explain(); e.Handled = true; };
        badge.ToolTip = "Click to explain. " + WarningExplanation(node, issue);
        return badge;
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
        if (!cards.ContainsKey(node.Id) && !portSlots.ContainsKey(node.Id)) Text("Selection is hidden by a collapsed branch or filter.", 11, "TextMuted");
        bool host = node.Kind is "Controller" or "Root hub", attached = node.Kind is "Device" or "Hub", hub = node.Kind == "Hub";
        string Reported(string value) => attached ? (value.Length > 0 && value != "Not reported" ? value : "Not reported") : node.Kind == "Unavailable" ? "Unknown" : NotApplicable;

        // Fixed-height heading, role line and status row keep everything below in place.
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
            Metric(NodeVisuals.Metric.Link, attached ? (ShortSpeed(node) == "Rate unknown" ? "Unknown" : ShortSpeed(node)) : unread, "Negotiated link", 0,
                "The signaling rate negotiated when the device connected. Everything upstream on the same path shares it; it is not a measured speed.");
            Metric(NodeVisuals.Metric.Reserved, attached ? (node.ReservedMbps is double reserved ? UsbBudgets.Rate(reserved) : "Unknown") : unread, "Reserved", 1,
                "Bus time held for this device's open interrupt and isochronous pipes, such as audio, video and input. Bulk transfers, such as storage, reserve nothing and share what is left.");
            Metric(NodeVisuals.Metric.Power, node.Kind == "Empty port" ? unread : UsesExternalPower(node) || node.MaxPowerMa != null ? PowerFigure(node).Text : attached ? "Unknown" : unread, UsesExternalPower(node) ? "Power" : "Power request", 2,
                UsesExternalPower(node) ? "Runs on its own supply, so it requests little or nothing from the bus." : "The most current the device's active configuration says it will draw. A declared maximum, not a measurement.");
            Details.Children.Add(metrics);
            Section("Device identity & connection");
            Field("VID / PID", attached && node.VendorId.Length > 0 ? $"{node.VendorId} : {node.ProductId}" : Reported(""));
            Field("Manufacturer", Reported(node.Manufacturer));
            Field("Serial", Reported(node.Serial));
            Field("USB revision", Reported(node.UsbVersion));
            Field("Power source", Reported(node.PowerSource));
            Field("At nominal 5 V", attached && node.MaxPowerMa is int draw ? $"{draw * 0.005:0.##} W declared" : Reported(""));
            Field("Peak reserved", attached && node.PeakReservedMbps is double peak ? $"Up to {UsbBudgets.Rate(peak)} when active" : Reported(""));
            if (UsbBudgets.LinkUse(node) is (var use, var room, _))
            {
                var usage = new DockPanel { ToolTip = MetricHelp(node) };
                var share = new TextBlock { Text = UsbBudgets.Share(use, room), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(share, Dock.Right); usage.Children.Add(share);
                var bar = NodeVisuals.LinkBar(use / room); bar.VerticalAlignment = VerticalAlignment.Center; usage.Children.Add(bar);
                Field("Link use", usage);
            }
            else Field("Link use", attached ? "Not reported" : Reported(""));
            Section("Port");
            Field("Logical path", pathLabels.GetValueOrDefault(node.Id, NotApplicable));
            Field("Port number", node.Port.ToString("00"));
            Field("Port name", PortNameEditor(node));
            Field("Port supports", node.Protocols);
            Field("Connector", NodeVisuals.Connector(node, SocketPartner(node)));
            Field("Location", node.Location == "Unknown" ? "Not reported" : node.Location + " · inferred");
            Field("Supply capacity", "Unknown · not measured");
            Section("Hub");
            Field("Logical ports", hub ? node.PortCount.ToString() : NotApplicable);
            Field("Downstream", hub ? ProtocolSummary(node) : NotApplicable);
            Field("End devices", hub ? node.Walk().Count(n => n.Kind == "Device").ToString() : NotApplicable);
        }
        else
        {
            var roots = node.Kind == "Controller" ? node.Children.Where(c => c.Kind == "Root hub").ToList() : [node];
            Section("Host");
            Field("Logical path", pathLabels.GetValueOrDefault(node.Id, NotApplicable));
            Field("Port support", ProtocolSummary(node));
            Field("Logical ports", roots.Sum(r => r.PortCount).ToString());
            Field("Occupied", roots.Sum(r => r.Children.Count(c => c.Kind != "Empty port")).ToString());
            Field("End devices", node.Walk().Count(n => n.Kind == "Device").ToString());
            Field("Connector", NodeVisuals.Connector(node));
            Field("Location", "Host hardware");
            Field("Power source", node.PowerSource);
            Field("Supply capacity", "Unknown · not measured");
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

        foreach (var (_, issue) in Issues(node))
        {
            var explanation = new TextBlock {  FontSize = 12, Foreground = Brush("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Tag = "warning:" + issue };
            explanation.Inlines.Add(new System.Windows.Documents.Run(issue + " · " + NodeVisuals.ShortName(node)) { FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary") });
            explanation.Inlines.Add(new System.Windows.Documents.LineBreak());
            explanation.Inlines.Add(new System.Windows.Documents.Run(WarningExplanation(node, issue)));
            Details.Children.Add(explanation);
        }
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
        if (node.InterfaceFunctions.Count > 0) notes.Add("Reported functions: " + string.Join(", ", node.InterfaceFunctions));
        var knownLinks = chain.Where(n => n.LinkMbps.HasValue).ToList();
        if (knownLinks.Count > 0) notes.Add($"Known path ceiling: {knownLinks.Min(n => n.LinkMbps):0.##} Mb/s, shared and before overhead.");
        if (node.Kind is "Hub" or "Root hub")
        {
            var connected = node.Children.Where(n => n.Status == "Connected").ToList();
            notes.Add($"Direct children's declared draw: {connected.Sum(n => n.MaxPowerMa ?? 0)} mA known; {connected.Count(n => n.MaxPowerMa == null)} unknown. Excludes devices behind child hubs; not a supply measurement.");
        }
        if (node.OpenPipes.Count > 0) notes.Add("Open pipes: " + string.Join("; ", node.OpenPipes) + ".");
        notes.Add("Port protocols: " + node.Protocols);
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
        Text("Link rates are shared signaling limits. Reserved bandwidth and requested power come from device descriptors, not live measurements. Power checks compare declared draw with what the USB specification guarantees a port; supply capacity itself is not measured.", 11, "TextMuted");

    }
    private List<UsbNode> FindPath(string id)
    {
        List<UsbNode>? SearchPath(UsbNode n) { if (n.Id == id) return [n]; foreach (var c in n.Children) { var path = SearchPath(c); if (path != null) { path.Insert(0, n); return path; } } return null; }
        return snapshot.Controllers.Select(SearchPath).FirstOrDefault(x => x != null) ?? [];
    }
    private void Text(string value, double size = 13, string color = "TextPrimary") => Details.Children.Add(new TextBlock { Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) });
    private void Section(string title)
    {
        Details.Children.Add(new Border { Height = 1, Background = Brush("Divider"), Margin = new Thickness(0, 9, 0, 8) });
        Details.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 7), Tag = "section" });
    }
    // Values stay on one line (full text in the tooltip) so every row keeps its height; missing values are muted.
    private void Field(string label, string value)
    {
        bool missing = value is NotApplicable or "Not reported" || value.StartsWith("Unknown", StringComparison.Ordinal);
        Field(label, new TextBlock { Text = value, FontSize = 13, Foreground = Brush(missing ? "TextMuted" : "TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value });
    }
    private void Field(string label, FrameworkElement value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Tag = "field", MinHeight = 18 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        value.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(value, 1); row.Children.Add(value);
        Details.Children.Add(row);
    }
    private void ActualSizeClick(object sender, RoutedEventArgs e) => ZoomAt(1, new Point(GraphScroll.ViewportWidth / 2, GraphScroll.ViewportHeight / 2));
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
        var targetId = selected.Kind == "Empty port" ? FindPath(selected.Id).SkipLast(1).Select(CardNode).LastOrDefault()?.Id : selected.Id;
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
    private void SearchChanged(object sender, TextChangedEventArgs e) { searchTimer.Stop(); searchTimer.Start(); }
    private void SetZoom(double value) { readableView = false; value = Math.Clamp(value, 0.15, 2); GraphScale.ScaleX = GraphScale.ScaleY = value; ZoomLabel.Text = $"{value:P0}"; }
    private void ZoomIn(object sender, RoutedEventArgs e) => ZoomAt(GraphScale.ScaleX * 1.2, new Point(GraphScroll.ViewportWidth / 2, GraphScroll.ViewportHeight / 2));
    private void ZoomOut(object sender, RoutedEventArgs e) => ZoomAt(GraphScale.ScaleX / 1.2, new Point(GraphScroll.ViewportWidth / 2, GraphScroll.ViewportHeight / 2));
    private void FitClick(object sender, RoutedEventArgs e)
    {
        if (arranging) return;
        arranging = true;
        try
        {
            ResetPan();
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
    private void OverviewClick(object sender, RoutedEventArgs e)
    {
        ResetPan();
        SetZoom(Math.Min(1, Math.Min((GraphScroll.ViewportWidth - 32) / Graph.Width, (GraphScroll.ViewportHeight - 32) / Graph.Height)));
        GraphScroll.ScrollToHorizontalOffset(0); GraphScroll.ScrollToVerticalOffset(0);
    }
    private void OrientationClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (bool horizontal in new[] { false, true })
        {
            var item = new MenuItem { Header = horizontal ? "Horizontal" : "Vertical", IsCheckable = true, IsChecked = horizontalTree == horizontal };
            item.Click += (_, _) => SetOrientation(horizontal);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = OrientationButton; menu.IsOpen = true;
    }
    private void SetOrientation(bool horizontal)
    {
        horizontalTree = horizontal;
        OrientationButton.Content = horizontalTree ? "Layout: horizontal" : "Layout: vertical";
        FitClick(this, new RoutedEventArgs());
    }
    private void GraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (readableView && !arranging && snapshot.Controllers.Count > 0 && (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 24 || horizontalTree && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 24))
            FitClick(this, new RoutedEventArgs());
    }
    private void ZoomAt(double zoom, Point pointer)
    {
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
        readableView = false; GraphScroll.Cursor = Cursors.SizeAll; e.Handled = true;
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
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON snapshot|*.json", FileName = $"usb-atlas-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })); StatusText.Text = "Snapshot exported to " + dialog.FileName; }
        catch (Exception ex) { StatusText.Text = "Export failed: " + ex.Message; }
    }
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
            var rect = new Rect(visible[i].Point, new Size(CardWidth, visible[i].Card.Height));
            Check(rect.Right <= Graph.Width && rect.Bottom <= Graph.Height, "Card extends outside graph bounds.");
            for (int j = i + 1; j < visible.Count; j++)
                Check(!rect.IntersectsWith(new Rect(visible[j].Point, new Size(CardWidth, visible[j].Card.Height))), "Hardware cards overlap.");
        }
        foreach (var node in snapshot.Nodes.Where(n => cards.ContainsKey(n.Id)))
            foreach (var child in Children(node))
                Check(horizontalTree ? cards[child.Id].Point.X > cards[node.Id].Point.X + CardWidth : cards[child.Id].Point.Y > cards[node.Id].Point.Y + cards[node.Id].Card.Height, "Child must follow its parent's flow direction.");
        var target = snapshot.Nodes.FirstOrDefault(n => n.Kind == "Device");
        if (target != null)
        {
            Search.Text = target.Name; ApplySearch();
            Check(cards.ContainsKey(target.Id), "Search lost the matching device.");
            Check(FindPath(target.Id).All(n => cards.ContainsKey(CardNode(n).Id)), "Search lost a matching device's ancestors.");
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
            Check(position.X + CardWidth > GraphScroll.HorizontalOffset && position.X < GraphScroll.HorizontalOffset + GraphScroll.ViewportWidth, "Selected card is horizontally outside the viewport.");
            Check(position.Y + cards[selected.Id].Card.Height > GraphScroll.VerticalOffset && position.Y < GraphScroll.VerticalOffset + GraphScroll.ViewportHeight, "Selected card is vertically outside the viewport.");
        }
        OverviewClick(this, new RoutedEventArgs()); GraphScroll.UpdateLayout();
        Check(Graph.Width * GraphScale.ScaleX <= GraphScroll.ViewportWidth + 1 && Graph.Height * GraphScale.ScaleY <= GraphScroll.ViewportHeight + 1, "Fit all leaves graph outside viewport.");
        FitClick(this, new RoutedEventArgs()); GraphScroll.UpdateLayout();
        Check(GraphScale.ScaleX >= 1, "Readable view must not shrink device text.");
        // Rows never wrap, so the graph may overflow only once every group of end devices is a staircase.
        if (!horizontalTree) Check(Graph.Width * GraphScale.ScaleX <= GraphScroll.ActualWidth + 1 || stackableHubs.IsSubsetOf(stackedHubs), "Readable layout overflows while end devices could still stack.");
        PanTransform.X = 87; PanTransform.Y = 53;
        var pointer = new Point(GraphScroll.ViewportWidth * 0.4, GraphScroll.ViewportHeight * 0.4);
        var anchored = GraphScroll.TranslatePoint(pointer, Graph);
        ZoomAt(GraphScale.ScaleX * 1.15, pointer);
        Check((Graph.TranslatePoint(anchored, GraphScroll) - pointer).Length < 1, "Wheel zoom moved the point under the pointer.");
        ZoomAt(0.25, pointer);
        Check((Graph.TranslatePoint(anchored, GraphScroll) - pointer).Length < 1, "Zooming out moved the point under the pointer.");
        FitClick(this, new RoutedEventArgs());
        Check(PanTransform.X == 0 && PanTransform.Y == 0, "Readable view must reset free panning.");
        var selection = selected?.Id;
        var collapsed = folded.ToHashSet();
        SetOrientation(!horizontalTree);
        Check(selected?.Id == selection && folded.SetEquals(collapsed), "Changing direction lost selection or folded branches.");
        var switched = cards.Values.ToList();
        for (int i = 0; i < switched.Count; i++)
            for (int j = i + 1; j < switched.Count; j++)
                Check(!new Rect(switched[i].Point, new Size(CardWidth, switched[i].Card.Height)).IntersectsWith(new Rect(switched[j].Point, new Size(CardWidth, switched[j].Card.Height))), "Cards overlap after changing direction.");
        foreach (var node in snapshot.Nodes.Where(n => cards.ContainsKey(n.Id)))
            foreach (var child in Children(node))
                Check(horizontalTree ? cards[child.Id].Point.X > cards[node.Id].Point.X + CardWidth : cards[child.Id].Point.Y > cards[node.Id].Point.Y + cards[node.Id].Card.Height, "Changed direction has incorrect parent-child placement.");
        SetOrientation(!horizontalTree);
    }
    private void CaptureUi(string filename)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(filename); png.Save(stream);
    }
    private async Task RenderPreview()
    {
        await Task.Delay(400);
        OpenInitialView(); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create("preview.png")) png.Save(stream);
        Close();
    }
}

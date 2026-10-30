using System.Windows;
using System.Windows.Controls;

namespace UsbAtlas;

public partial class MainWindow
{
    // Properties reads top to bottom as the chain it sits in: the way up to the port this is plugged into, the
    // connection with its rate and power, the thing itself, then what hangs below it. Every kind of node shows the
    // same rows in the same order, so flipping between ports compares like with like; only the child list at the
    // very end varies in length.
    private string InspectorReported(UsbNode node, string value) =>
        node.Kind is "Device" or "Hub" ? (value.Length > 0 && value != "Not reported" ? value : "Not reported") : node.Kind == "Unavailable" ? "Unknown" : NotApplicable;

    // The trail from the computer to here; each step selects that hop and shows it on the canvas.
    private void AddBreadcrumb(UsbNode node)
    {
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
    }

    // The port this is plugged into: which hub and socket, its name and its connector.
    private void AddUpstream(UsbNode node)
    {
        Section("Parent port");
        var chain = FindPath(node.Id);
        var parent = chain.Count > 1 ? CardNode(chain[^2]) : null;
        FrameworkElement into = new TextBlock { Text = NotApplicable, FontSize = 13, Foreground = Brush("TextMuted") };
        if (parent != null)
        {
            var link = new Button { Content = new TextBlock { Text = $"Port {node.Port:00} · {NodeVisuals.ShortName(parent)}", TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)FindResource("EditableNameButton"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brush("Accent"), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Select " + parent.DisplayName };
            link.Click += (_, _) => { SelectNode(parent); LocateClick(this, new RoutedEventArgs()); };
            into = link;
        }
        Field("Plugged into", into, "The hub or host this port belongs to, and the number Windows gives the port on it. Click to select it.");
        // A USB 3 hub is two hubs on one cable; the way to its other half belongs with the port it shares.
        var companion = snapshot.Nodes.FirstOrDefault(n => n.Id == node.CompanionHubId);
        FrameworkElement otherSide = new TextBlock { Text = NotApplicable, FontSize = 13, Foreground = Brush("TextMuted") };
        if (node.Kind == "Hub" && companion != null)
        {
            var pair = new Button { Content = "View " + (node.IsUsb2Companion ? "USB 3" : "USB 2") + " side", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 1, 8, 1) };
            pair.Click += (_, _) => ShowOnCanvas(companion);
            otherSide = pair;
        }
        Field("Other side", otherSide, "A USB 3 hub is two hubs on one cable, one for USB 2 and one for USB 3. This shows the other half.");
        Field("Port name", PortNameEditor(node), "Your own name for this socket. It stays with the hub port when the device in it changes.");
        Field("Connector", NodeVisuals.Connector(node, SocketPartner(node)), ConnectorHelp + (UsbC.Socket(snapshot, node).Count > 0
            ? " Windows doesn't report whether a USB-C socket carries USB4 or Thunderbolt, DisplayPort, or what Power Delivery contract it has. Detection details says what is known." : ""));
    }

    // The link to the parent: its rate, the bus time held, and the power requested, with what the port can supply.
    private void AddConnection(UsbNode node)
    {
        bool attached = node.Kind is "Device" or "Hub";
        string Reported(string value) => InspectorReported(node, value);
        Section("Connection");
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
        // A third of the panel is too narrow for the card's "Self-powered + 100 mA", so the tile keeps the
        // request as its figure and the claim as its label.
        var (power, powerLabel) = node.Kind == "Empty port" ? (unread, "Power request")
            : DeclaresSelfPower(node) ? (node.MaxPowerMa is int declared ? ($"{declared} mA", "Self-powered") : ("Unknown", "Self-powered"))
            : (node.MaxPowerMa != null ? PowerFigure(node).Text : attached ? "Unknown" : unread, "Power request");
        Metric(NodeVisuals.Metric.Power, power, powerLabel, 2,
            DeclaresSelfPower(node) ? PowerEvidence(node)! : "The most current the device's active configuration says it will draw. A declared maximum, not a measurement.");
        Details.Children.Add(metrics);
        Field("Power source", Reported(node.PowerSource), "Whether it runs on power from the USB port or its own supply, as its configuration and, for a hub, Windows report it.");
        if (UsbBudgets.LinkUse(node) is (var use, var room, _))
        {
            var usage = new DockPanel { ToolTip = MetricHelp(node) };
            // A keyboard's slice can't grow and a drive's is nothing, which is why their cards have no meter; the row says so.
            string kind = node.Kind == "Device" ? UsbBudgets.ReservationOf(node) switch { UsbBudgets.Reservation.Fixed => " · can't grow", UsbBudgets.Reservation.None => " · reserves nothing", _ => "" } : "";
            var share = new TextBlock { Text = UsbBudgets.Share(use, room) + kind, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(share, Dock.Right); usage.Children.Add(share);
            var bar = NodeVisuals.LinkBar(use / room); bar.VerticalAlignment = VerticalAlignment.Center; usage.Children.Add(bar);
            Field("Link use", usage, LinkUseHelp);
        }
        else Field("Link use", attached ? "Not reported" : Reported(""), LinkUseHelp);
        Field("Power available", PowerNeeds.Available(node), SupplyHelp);
    }

    // The thing itself: who made it, what it says it is, and how it behaves.
    private void AddDeviceProperties(UsbNode node)
    {
        bool attached = node.Kind is "Device" or "Hub";
        string Reported(string value) => InspectorReported(node, value);
        Section("Device");
        Field("VID / PID", attached && node.VendorId.Length > 0 ? $"{node.VendorId} : {node.ProductId}" : Reported(""), "The vendor and product IDs the device reports. They identify its chip or product, and are what driver and support pages search by.");
        Field("Manufacturer", Reported(node.Manufacturer), "The maker's name as the device reports it, often the chip's maker rather than the brand on the box.");
        Field("Serial", Reported(node.Serial), "The serial number the device reports. A unique one lets Windows and USB Atlas recognize it on any port; many devices report none, or one every unit shares.");
        Field("USB version", Reported(node.UsbVersion), "The USB version the device says it was built to (bcdUSB). How fast it runs now is Link speed.");
        Field("Device revision", Reported(node.DeviceRevision), "The maker's own revision number for this device (bcdDevice). For hubs and adapters it's usually the firmware version, the first thing their makers' support asks for.");
        Field("Known problems", node.Kind is "Device" or "Hub" ? KnownProblems.Summary(node) : node.Kind == "Unavailable" ? "Unknown" : NotApplicable,
            "Whether this chip has a problem the Linux kernel works around, from a short bundled list of hub, dock and drive-enclosure chips, and whether anything here looks like it. Not being listed doesn't mean a chip has none. Detection details links the source.");
        Field("Alternate modes", node.Billboard != null ? Billboard.Summary(node) is { Length: > 0 } modes ? modes : "None offered" : node.Kind == "Unavailable" ? "Unknown" : NotApplicable,
            "What a USB-C device's Billboard reports about modes such as DisplayPort over USB-C: whether each was entered, failed, or never asked for.");
        Field("Part of", attached && Containers.PartOf(snapshot, node) is { Length: > 0 } part ? part : node.Kind == "Unavailable" ? "Unknown" : NotApplicable,
            "Windows groups the USB devices of one product, such as a monitor's hub, audio and Billboard, into a device container. Detection details lists the others.");
        // Polling applies to devices with an open interrupt input pipe; hubs poll only for port changes.
        Field("Polling rate", node.Kind == "Device" ? node.PollIntervalMs is double ms ? $"{UsbBudgets.PollingRate(ms)} · {UsbBudgets.PollingInterval(ms)}" : node.ReservedMbps != null ? NotApplicable : "Not reported" : node.Kind == "Unavailable" ? "Unknown" : NotApplicable, PollingHelp);
        Field("Power saving", attached ? PowerSavingText(node) : Reported(""), PowerSavingHelp);
        Field("Wakes computer", attached ? node.WakeSetting : Reported(""), "Device Manager's “Allow this device to wake the computer”: whether this device may wake the computer from sleep. Not supported means no driver for it offers the setting.");
        Field("Storage protocol", attached && Uas.Summary(node) is { Length: > 0 } protocol ? protocol : NotApplicable, "How Windows talks to a drive: UAS, the faster protocol fast enclosures support, or the older bulk-only protocol. Flash drives and card readers are bulk-only.");
        Field("Power at 5 V", attached && node.MaxPowerMa is int draw ? $"{draw * 0.005:0.##} W declared" : Reported(""), "Its power request at USB's nominal 5 volts, from its descriptor (MaxPower). A declared maximum, not a measurement.");
        Field("Charging", node.Kind == "Device" ? PowerNeeds.Summary(node) is { Length: > 0 } charging ? charging : "Not listed" : node.Kind == "Unavailable" ? "Unknown" : NotApplicable,
            "How much power it needs to charge at full speed, and the most this port can give. The need comes from a short bundled list of makers' figures, such as Meta's for Quest headsets, or the device's own Power Delivery descriptor; not being listed doesn't mean it needs little. Detection details gives the source.");
        Field("Battery", attached && Battery.Summary(node) is { Length: > 0 } battery ? battery : Reported(""),
            "Its battery level, for a device that reports it to Windows as phones and headsets do while they share files (MTP). With Auto-refresh on, or each refresh, USB Atlas watches it: a level that falls while it stays plugged in means it uses power faster than this port gives.");
        Field("Reserved at peak", attached && node.PeakReservedMbps is double peak ? $"Up to {UsbBudgets.Rate(peak)} when active" : Reported(""), "The most bus time it would hold when fully active, such as a camera while streaming, from its busiest alternate settings.");
        DriverFields(node);
    }

    // The rest of what is known about that socket, including where it likely sits, below the device so the
    // connection and the device stay near the top: in the smallest window, Link use must show without scrolling.
    private void AddSocketDetails(UsbNode node)
    {
        Section("Socket details");
        Field("Port number", node.Port.ToString("00"), "The number Windows gives this port on its hub. A USB 3 socket has two, one for its USB 2 half and one for its USB 3 half.");
        Field("Hubs above", HubDepth.Summary(node), "Hubs between the computer and this port, not counting the computer's own. USB allows five in a row; docks, monitors and keyboards often have hubs inside.");
        Field("Socket speed", SocketSpeedEditor(node), SocketSpeedHelp);
        Field("Socket type", SocketConnectorEditor(node), SocketConnectorHelp);
        Field("Port supports", node.Protocols, "The USB versions Windows says this port carries.");
        Field("Location", node.Location switch { "Unknown" => "Not reported", "External" => "Likely outside the computer", "Internal" => "Likely built in", "Host" => "In the computer", var other => other },
            "Worked out from whether Windows says the port is one you can plug into, and what it's connected through. Windows doesn't report where hardware sits.");
    }

    // What hangs below it: a hub's own figures, then the devices connected to it, each a step down the chain.
    private void AddDownstream(UsbNode node)
    {
        bool hub = node.Kind == "Hub", host = node.Kind is "Controller" or "Root hub";
        Section("Downstream");
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
        var roots = node.Kind == "Controller" ? node.Children.Where(c => c.Kind == "Root hub").ToList() : [node];
        if (host)
        {
            Field("Ports", roots.Sum(r => r.PortCount).ToString(), PortsHelp);
            Field("In use", roots.Sum(r => r.Children.Count(c => c.Kind != "Empty port")).ToString(), "Root ports with something plugged into them, counting each half of a USB 3 socket.");
            Field("Devices", node.Walk().Count(n => n.Kind == "Device").ToString(), "Devices connected through this host, directly or behind hubs.");
        }
        AddChildren(node, host ? roots.SelectMany(r => r.Children) : node.Children);
    }

    private const int ChildListLimit = 12;
    private void AddChildren(UsbNode node, IEnumerable<UsbNode> below)
    {
        var plugged = below.Where(c => c.Kind != "Empty port").ToList();
        if (plugged.Count == 0)
        {
            Details.Children.Add(new TextBlock { Text = node.Kind == "Empty port" ? "Nothing is plugged in here." : "Nothing is connected below.", FontSize = 13, Foreground = Brush("TextMuted"), Margin = new Thickness(0, 2, 0, 4), Tag = "child" });
            return;
        }
        foreach (var child in plugged.Take(ChildListLimit))
        {
            var line = new DockPanel();
            var rate = new TextBlock { Text = string.Join(" · ", new[] { ShortSpeed(child) == "Rate unknown" ? "" : ShortSpeed(child), child.MaxPowerMa is int ma ? $"{ma} mA" : "" }.Where(t => t.Length > 0)), FontSize = 12, Foreground = Brush("TextSecondary"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(rate, Dock.Right); line.Children.Add(rate);
            var icon = NodeVisuals.Icon(child, 16); icon.Margin = new Thickness(0, 0, 7, 0); DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
            line.Children.Add(new TextBlock { Text = $"{child.Port:00}  {NodeVisuals.ShortName(CardNode(child))}", FontSize = 13, Foreground = Brush("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = line, Style = (Style)FindResource("EditableNameButton"), Padding = new Thickness(2, 3, 2, 3), HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = "child", ToolTip = "Select " + child.DisplayName, Cursor = System.Windows.Input.Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(button, "Select " + child.DisplayName);
            button.Click += (_, _) => { SelectNode(child); LocateClick(this, new RoutedEventArgs()); };
            Details.Children.Add(button);
        }
        if (plugged.Count > ChildListLimit) Details.Children.Add(new TextBlock { Text = $"and {plugged.Count - ChildListLimit} more", FontSize = 12, Foreground = Brush("TextMuted"), Margin = new Thickness(2, 2, 0, 4), Tag = "child" });
    }

    // The computer's own controller or root hub, which has no parent port or connection of its own.
    private void AddHostProperties(UsbNode node)
    {
        var roots = node.Kind == "Controller" ? node.Children.Where(c => c.Kind == "Root hub").ToList() : [node];
        Section("Host");
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
        Field("Connector", NodeVisuals.Connector(node), "The host's sockets are its root ports, each drawn on its card as the kind of socket it is.");
        Field("Location", controller.Location == UsbC.TunneledLocation ? UsbC.TunneledLocation : "Host hardware", controller.LocationEvidence);
        Field("Power source", node.PowerSource, "Host controllers run on the computer's own power.");
        // The root hub is what Device Manager lists, and what sim hardware guides point to.
        Field("Power saving", PowerSavingText(MergedRoot(node) ?? node), PowerSavingHelp);
        Field("Power plan", PowerSaving.PlanSummary(snapshot), "The power plan's USB selective suspend setting. While it's off, Windows suspends no USB device, whatever each device's own power-saving setting says.");
        Field("Power available", "Unknown · not measured", SupplyHelp);
        DriverFields(node);
    }
}

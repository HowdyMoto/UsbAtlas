namespace UsbAtlas;

// Issues explained in plain words for the person at the computer: what is happening, whether it affects
// anything plugged in now, and what to do, most likely cause first. Evidence and figures stay in
// Detection details. Amber and red mean something plugged in is affected or at risk; what is only worth
// knowing is a calm note.
internal static class Explanations
{
    internal sealed record Explanation(string What, string Affects = "", string Cause = "", List<string>? Steps = null);

    internal const string AdapterNotDetected = "Hub adapter not detected";
    internal const string DriverProblem = "Windows reports a problem";
    // Disabled on purpose is worth knowing; waiting for a restart or for removal leaves it working until then.
    internal static Severity? DriverProblemSeverity(UsbNode n) =>
        n.DriverProblems.Count == 0 ? null
        : n.DriverProblems.All(p => p.Code is 22 or 29) ? Severity.Note
        : n.DriverProblems.All(p => p.Code is 14 or 22 or 29 or 47) ? Severity.Warning : Severity.Error;
    // A hub on bus power whose devices fit what the port gives it loses nothing yet; one that can't power them does.
    internal static Severity PowerSeverity(UsbNode n, string warning) =>
        warning == AdapterNotDetected && !n.PowerWarnings.Contains("Over power budget") && !n.Children.Any(c => c.PowerWarnings.Contains("Power at risk"))
            ? Severity.Note : Severity.Warning;

    // path runs from the host controller down to n.
    internal static Explanation For(UsbNode n, string issue, IReadOnlyList<UsbNode> path)
    {
        string noun = n.Kind switch { "Hub" => "hub", "Controller" or "Root hub" => "host", _ => "device" };
        var parent = path.Count >= 2 ? path[^2] : null;
        bool busHub = parent is { Kind: "Hub", PowerSource: "Bus powered" };
        switch (issue)
        {
            case string speed when speed.StartsWith("Running at", StringComparison.Ordinal):
                return Speed(n, path);
            case "Port error":
                return n.Status switch
                {
                    "Enumeration failed" or "General failure" => new("Something is plugged in here, but it didn't answer properly when Windows tried to set it up.", "Yes: Windows can't use it.", "",
                        ["Unplug it, wait a few seconds and plug it back in.", "Try another cable and another port.", "If it has its own power supply or switch, check that it's on. If it fails everywhere, the device may be faulty."]),
                    "Query failed" => new("USB Atlas couldn't read this port.", "Probably not: a device here may still be connected and working; this scan just can't see it.", "", ["Refresh (F5) to try again."]),
                    "Hub nested too deeply" => new("This device is behind too many hubs in a row for USB to reach it.", "Yes: Windows can't use it.",
                        "USB allows at most five hubs between a device and the computer, and monitors, docks and keyboards often have hubs inside.", ["Plug it, or the hub it's on, closer to the computer."]),
                    _ => new($"Windows reports this port as “{n.Status}”.", "Probably: Windows may not be able to use what's plugged in here.", "", ["Unplug the device and plug it back in.", "Try another port and cable."])
                };
            case "Limited by PCIe link" when UsbBudgets.Uplink(n) is (var uplink, _, var linked, var capped):
            {
                bool held = n.PcieMaxGeneration > n.PcieGeneration || n.PcieMaxLanes > n.PcieLanes;
                string what = $"This controller reaches the computer over {UsbBudgets.PcieText(n.PcieGeneration!.Value, n.PcieLanes!.Value)}, about {UsbBudgets.Rate(uplink)} for all its ports together."
                    + (held ? $" It can do {UsbBudgets.PcieText(n.PcieMaxGeneration ?? n.PcieGeneration.Value, n.PcieMaxLanes ?? n.PcieLanes.Value)}, so its slot or a setting is holding it back." : "");
                string affects = capped != null ? $"Yes: {Kinds([capped])} is linked at {UsbBudgets.Rate(capped.LinkMbps!.Value)} but can move only about {UsbBudgets.Rate(uplink)} through this controller."
                    : linked > uplink ? $"Only when they're busy together: what's plugged into its ports is linked at {UsbBudgets.Rate(linked)} in all, so transfers at the same time share about {UsbBudgets.Rate(uplink)}."
                    : $"Not right now: a fast device on its quickest port would be held to about {UsbBudgets.Rate(uplink)}.";
                var steps = new List<string>();
                if (held) steps.Add("If it's a card in the computer, a slot with more lanes or a newer PCIe version lets it run at full speed; the motherboard's manual says which slots do.");
                steps.Add("For fast drives, use a port on another controller: another host card here.");
                return new(what, affects, "", steps);
            }
            case "Many endpoints in use":
            {
                var failing = n.Walk().Where(d => d.Kind is "Device" or "Hub" && d.DriverProblems.Count > 0).ToList();
                return new($"Devices on this controller have {UsbBudgets.ControllerLoad(n).Endpoints} endpoints open: the channels it keeps for each device, one for control plus one for each pipe. Controllers hold only so many, and Windows doesn't say how many; some common ones top out at 96.",
                    failing.Count > 0 ? $"Maybe: {Kinds(failing)} {(failing.Count == 1 ? "isn't" : "aren't")} working, and running out of endpoints is a common reason. Windows then shows “Not enough USB controller resources”."
                        : "Not right now: everything plugged in is working. If it runs out, Windows refuses the next device with “Not enough USB controller resources”.", "",
                    ["If a device shows “Not enough USB controller resources”, plug it into a port on another controller: another host card here.",
                     "Unplug devices you aren't using. Webcams, audio interfaces, VR headsets, docks and hubs use the most endpoints."]);
            }
            case "Still connecting":
                return new("Something is plugged in here, and Windows was still setting it up when USB Atlas looked.", "Probably not: this usually finishes within seconds.", "",
                    ["Refresh (F5) in a moment. If it stays this way, unplug it and plug it back in."]);
            case "Insufficient bandwidth":
                return new("Windows couldn't start this device: the connection it shares can't set aside enough time for it.", "Yes: it won't work until there's room.",
                    "Cameras, microphones, audio interfaces and controllers each reserve time on the connection they share, and this one is full.",
                    ["Plug it into a port on a different hub, or straight into the computer.", "Or move another camera or audio device off this hub, then unplug this one and plug it back in."]);
            case "Insufficient power":
                return new($"Windows turned this device off because it asks for more power{(n.MaxPowerMa is int ma ? $" (up to {ma} mA)" : "")} than this port can give.", "Yes: it won't work until it has enough power.",
                    busHub ? "The hub it's plugged into runs on the computer's power and can give each port only a little." : "",
                    busHub ? ["Plug in that hub's power adapter, if it has one.", "Or plug the device straight into the computer."]
                        : ["Plug it into another port on the computer, or a hub with its own power adapter.", "If it came with a Y-cable or its own power supply, use it."]);
            case "Overcurrent":
                return new("The device on this port drew more power than the port allows, so Windows switched the port off.", "Yes: the port stays off until the device is unplugged.", "",
                    ["Unplug it and plug it back in.", "If it happens again, try another cable, another port, or a hub with its own power adapter.", "If it keeps happening with different cables and ports, the device or its cable may be damaged."]);
            case "Scan incomplete":
                return new($"USB Atlas couldn't read everything behind this {noun}.", "Maybe not: your hardware may be fine, but devices behind it may be missing from this view.", "",
                    ["Refresh (F5). Plugging or unplugging something during a scan causes this.", "If it keeps happening, Detection details shows what Windows reported."]);
            case "Power at risk":
            {
                int asks = UsbBudgets.Demand(n).Known, port = UsbBudgets.SuperSpeed(n) ? 150 : 100;
                return new($"This {noun}{(n.Kind == "Hub" ? " and its devices" : "")} can ask for up to {asks} mA, but the hub it's plugged into runs on the computer's power and promises each port only {port} mA.",
                    n.Kind == "Hub" ? "Maybe: devices on it may disconnect when they draw the most." : "Maybe: it may disconnect or misbehave when it draws the most, for example when a drive spins up.", "",
                    ["Plug in that hub's power adapter, if it has one.", $"Or plug this {noun} straight into the computer."]);
            }
            case "Over power budget":
            {
                var children = n.Children.Where(c => c.Kind is "Device" or "Hub").ToList();
                int own = n.MaxPowerMa ?? 0, total = own + children.Sum(c => UsbBudgets.Demand(c).Known), upstream = UsbBudgets.SuperSpeed(n) ? 900 : 500;
                var hungriest = children.OrderByDescending(c => UsbBudgets.Demand(c).Known).FirstOrDefault();
                var steps = new List<string> { "Plug in the hub's power adapter, if it has one." };
                if (hungriest != null) steps.Add($"Or move {Topology.ShortName(hungriest)} straight to the computer.");
                steps.Add("If the hub is on a USB-C or charging port, it may be getting more power than this assumes; Windows doesn't report it.");
                return new($"This hub runs on the computer's power, and it and its devices can ask for {total} mA in all, but a standard port promises only {upstream} mA.",
                    "Maybe: if they all draw their most at once, devices may disconnect or the hub may shut off.", "", steps);
            }
            case AdapterNotDetected:
            {
                // Devices that don't report their draw count as nothing in the budget, so say so rather than promise they fit.
                int unreported = n.Children.Where(c => c.Kind is "Device" or "Hub").Sum(c => UsbBudgets.Demand(c).Unknown);
                string affects = PowerSeverity(n, issue) == Severity.Warning ? "Yes: without the adapter, its devices can ask for more power than it can give."
                    : unreported > 0 ? $"Probably not: the devices that report their power fit within what it gets from the computer, but {unreported} {(unreported == 1 ? "device doesn't say how much it uses" : "devices don't say how much they use")}."
                    : "Not right now: what's plugged into it fits within the power it gets from the computer.";
                return new("This hub can use its own power adapter, but it's running on the computer's power.", affects, "",
                    ["If it has an adapter, check that it's plugged in and switched on.", "If it didn't come with one, this is how it's meant to run."]);
            }
            case "Unstable connection":
            {
                // The count is the whole session; the last few times say when, in the same local time format
                // as Detection details and the status bar.
                var shown = n.QuickReconnectTimes.TakeLast(4).Select(t => t.ToString("T")).ToList();
                string times = shown.Count == 0 ? "" : (n.QuickReconnectTimes.Count > shown.Count ? ", most recently at " : ", at ") + (shown.Count == 1 ? shown[0] : string.Join(", ", shown[..^1]) + " and " + shown[^1]);
                bool hub = n.Kind == "Hub";
                var steps = new List<string>
                {
                    hub ? "Switching a KVM, changing monitor inputs or undocking does this, and then it's nothing to worry about."
                        : "Unplugging it, restarting it or switching its mode does this, and then it's nothing to worry about.",
                    $"Otherwise, check the {noun}'s cable and plug, and try another port."
                };
                if (hub && n.PowerSource == "Bus powered") steps.Add("Plug in the hub's power adapter, if it has one: a power shortfall can cause drops.");
                else if (busHub) steps.Add("The hub it's plugged into runs on the computer's power: plug in its adapter, or plug this device into the computer, since a power shortfall can cause drops.");
                return new($"This {noun}{(hub ? ", and everything plugged into it," : "")} disconnected and came back within seconds {n.QuickReconnects} times this session{times}. It stays flagged until USB Atlas is restarted.",
                    $"Only if you didn't cause it: anything using {(hub ? "these devices" : "it")} was interrupted each time.", "", steps);
            }
            case "Link nearly full" when UsbBudgets.LinkUse(n) is (var reserved, var capacity, _):
                return new(n.Kind == "Hub" ? $"The cameras, microphones, audio and input devices on this hub have set aside {Percent(reserved, capacity)} of the time its connection can give them."
                        : $"This device has set aside {Percent(reserved, capacity)} of the time its connection can give it.",
                    "Not right now: everything fits. Another camera, microphone or audio device here, or one that starts streaming, may be refused.", "",
                    ["If a new device here won't start, plug it into a different hub or straight into the computer."]);
            case "Could exceed when streaming" when UsbBudgets.LinkUse(n) is (_, var capacity, _):
            {
                if (n.Kind != "Hub")
                    return new($"At its busiest, this device would need {UsbBudgets.Rate(UsbBudgets.PeakThroughLink(n).Mbps)}, more than its connection can set aside ({UsbBudgets.Rate(capacity)}).",
                        "Not yet, while it uses less. At its highest setting, such as a camera's top resolution, Windows may refuse it and it stops working.", "",
                        ["If it stops working at a high setting, choose a lower resolution or frame rate in the app using it.", "Or plug it into a faster port, if it supports one."]);
                var streamers = n.Walk().Where(d => d.Kind == "Device" && (d.PeakReservedMbps ?? 0) - (d.ReservedMbps ?? 0) >= 0.5).OrderByDescending(d => d.PeakReservedMbps).Take(2).ToList();
                return new($"If the cameras, microphones and audio devices on this {noun} all stream at once, they'd need {UsbBudgets.Rate(UsbBudgets.PeakThroughLink(n).Mbps)}, more than its connection can set aside ({UsbBudgets.Rate(capacity)}).",
                    "Not yet, while some are idle. When enough of them stream at the same time, such as on a video call, Windows may refuse one and it stops working.", "",
                    [$"Move {(streamers.Count > 0 ? Names(streamers) : "a camera or audio device")} to a port on a different hub, or straight to the computer."]);
            }
            case "Shared TT nearly full" when UsbBudgets.SharedTtUse(n) is (var now, _, _, _):
                return new($"Keyboards, mice, controllers, audio interfaces and other slower devices on this hub share one 12 Mb/s connection, and they've set aside {Percent(now, UsbBudgets.FullSpeedReservableMbps)} of it.",
                    "Not right now: everything fits. Another such device here, or one that starts streaming, may be refused.", "",
                    ["If a new device here won't start, plug it into another hub or straight into the computer.", "A hub with one translator per port (Multi-TT) gives each port its own 12 Mb/s."]);
            case "Shared TT could exceed" when UsbBudgets.SharedTtUse(n) is (_, var peak, _, _):
            {
                var busiest = n.Children.Where(c => c.Kind is "Device" or "Hub" && c.LinkMbps is 1.5 or 12).OrderByDescending(c => Math.Max(c.PeakReservedMbps ?? 0, c.ReservedMbps ?? 0)).Take(2).ToList();
                return new($"Slower devices on this hub, such as audio interfaces, controllers and keyboards, share one 12 Mb/s connection, and at their busiest they'd need {UsbBudgets.Rate(peak)}, more than it can set aside.",
                    "Not yet, while some are idle. When they stream at the same time, Windows may refuse one and it stops working.", "",
                    [$"Move {Names(busiest)} to another hub or straight to the computer.", "Or use a hub with one translator per port (Multi-TT)."]);
            }
            case DriverProblem:
                return DriverExplanation(n, noun);
            case Billboard.Failed or Billboard.NotEntered when n.Billboard != null:
                return Billboard.Explain(n, issue);
            case Containers.SharedId:
                return Containers.Explain(n);
            case LinuxProblems.NoDriver or LinuxProblems.NotAuthorized:
                return LinuxProblems.Explain(n);
            case string finding when PortMap.IsFinding(finding):
                return PortMapExplanation(finding);
            case PowerSaving.Warning:
                return new("Windows may turn this game controller off to save power when it looks idle.",
                    "Maybe: a wheel, pedals or button box turned off mid-session can be slow to wake or drop out.", "",
                    ["In Device Manager, open this device's Power Management tab and clear “Allow the computer to turn off this device to save power”.",
                     "Or turn it off for every USB device: Power Options › Change advanced power settings › USB settings › USB selective suspend."]);
            default:
                return new($"{issue}. See Detection details.");
        }
    }
    // The computer's firmware describes each built-in port to Windows; these are its contradictions. They
    // can't be fixed at the port, and Detection details says which ports are involved.
    private static Explanation PortMapExplanation(string finding)
    {
        string what = finding switch
        {
            PortMap.NoUsb2Half => "Windows lists this USB 3 port without a USB 2 half. Every USB 3 socket carries both, as two ports that Windows pairs.",
            PortMap.CompanionMissing => "Windows says this port shares its socket with another port, but no such port is in this scan.",
            PortMap.CompanionOneWay => "This port names another port as the other half of its socket, but that port doesn't name this one back.",
            PortMap.SameVersionHalves => "Windows pairs this port with another of the same USB version as the two halves of one socket. A socket's halves are one USB 2 port and one USB 3 port.",
            _ => "The two ports Windows pairs as one socket are described differently: one as USB-C, or as a port you can plug into, and the other not."
        };
        return new(what, "No: whatever is plugged in here works as usual. USB Atlas can't be sure which ports share this socket, so it may draw one socket as two, or two as one.",
            "The computer's firmware (BIOS/UEFI) describes its USB ports to Windows, and this part of the description is wrong or missing.",
            ["There's nothing to fix by replugging. A firmware (BIOS/UEFI) update from the computer's or motherboard's maker may correct it.",
             "If you make or test this board, compare this port's ACPI _UPC and _PLD with its other half's. Detection details names the ports."]);
    }
    // The worst problem leads; Device Manager shows the same code on the device's General tab.
    private static Explanation DriverExplanation(UsbNode n, string noun)
    {
        // A host's card also shows its merged root hub's issues.
        var problems = n.DriverProblems.Count > 0 ? n.DriverProblems : Topology.MergedRoot(n)?.DriverProblems ?? [];
        if (problems.Count == 0) return new($"{DriverProblem}. See Detection details.");
        var p = problems.OrderBy(p => p.Code is 22 or 29 ? 2 : p.Code is 14 or 47 ? 1 : 0).First();
        bool own = p.InstanceId.Equals(n.InstanceId, StringComparison.OrdinalIgnoreCase) || n.Kind == "Controller" && p.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);
        string where = own ? $"this {noun}" : $"part of this {noun} ({p.Name})";
        string others = problems.Count > 1 ? $" {problems.Count - 1} more of its functions also report a problem." : "";
        string what = $"Windows reports Code {p.Code} on {where}: “{p.Meaning}”{others}";
        return p.Code switch
        {
            22 => new(what, $"Yes, on purpose: Windows doesn't use {(own ? "it" : "that part")} while it's disabled.", "", ["To use it, open Device Manager, right-click it and choose Enable device."]),
            29 => new(what, "Yes: Windows can't use it.", "The computer's firmware turned it off.", ["Check the USB settings in the computer's firmware (BIOS/UEFI) setup."]),
            14 => new(what, "Maybe: it may not work fully until Windows restarts.", "", ["Restart the computer."]),
            47 => new(what, "Yes: it was ejected and stays off until it's unplugged.", "", ["Unplug it and plug it back in."]),
            28 or 1 or 18 or 24 => new(what, "Yes: Windows can't use it without its driver.", "",
                ["Check Windows Update › Advanced options › Optional updates for a driver.", "Or install the driver from the manufacturer's website.", "In Device Manager, Update driver › Search automatically can also find one."]),
            43 => new(what, "Yes: Windows stopped it, so it isn't working.",
                n.Kind == "Device" && n.VendorId is "0000" or "" ? "It didn't answer when Windows asked what it is, which usually means a bad cable, a loose plug or not enough power." : "The device or its driver reported a failure.",
                ["Unplug it, wait a few seconds and plug it back in.", "Try another cable and another port, ideally straight into the computer.", "In Device Manager, Uninstall device, then unplug and replug it so Windows sets it up again.", "If it fails everywhere, the device may be faulty."]),
            52 or 48 => new(what, "Yes: Windows won't load its driver.", "", ["Install the current driver from the manufacturer, or check Windows Update for one."]),
            _ => new(what, "Probably: Windows may not be able to use it.", "",
                ["Unplug it and plug it back in, or restart the computer.", "In Device Manager, Update driver, or Uninstall device and then replug it so Windows sets it up again.", "Install the current driver from the manufacturer."])
        };
    }
    private static string Percent(double part, double whole) => UsbBudgets.Share(part, whole).Split(' ')[0];

    // A device or hub linked slower than it supports: USB 2 when it supports USB 3, or 5 Gb/s when it supports 10.
    internal static string SpeedLabel(UsbNode n) => n.LinkMbps switch { 5000 => "Running at 5 Gb/s", 12 => "Running at 12 Mb/s", _ => "Running at USB 2" };

    // Devices behind a slow hub that support more than its link, so the hub holds them back.
    internal static List<UsbNode> HeldBack(UsbNode hub) => hub.Walk().Skip(1).Where(d => d.Kind == "Device" && hub.LinkMbps switch
    {
        12 => d.SpeedLimited || d.HighSpeedCapable == true,
        5000 => d.SpeedLimited && d.SuperSpeedPlusCapable == true && d.LinkMbps == 5000,
        _ => d.SpeedLimited
    }).ToList();

    // A slow hub that slows nothing plugged into it is worth knowing, not a warning, and so is a built-in
    // connection, which there's no way to change.
    internal static Severity SpeedSeverity(UsbNode n) =>
        n.Kind == "Hub" && HeldBack(n).Count == 0 || n.Connector == "Internal" ? Severity.Note : Severity.Warning;

    // The empty or failed USB 3 half of the socket a hub's USB 2 side uses, where its USB 3 side should
    // be. Windows lists it as just another port; the hub's speed explanation says why and what to do.
    // hubPath runs from the host controller down to the hub.
    internal static Explanation MissingUsb3Half(UsbNode hub, IReadOnlyList<UsbNode> hubPath)
    {
        var speed = Speed(hub, hubPath);
        string name = Topology.ShortName(hub);
        string what = hub.Usb3SideFailed
            ? $"This is the USB 3 half of the socket {name} is plugged into. The hub's USB 3 side tried to connect here and failed, so the hub runs at USB 2."
            : $"This is the USB 3 half of the socket {name} is plugged into. The hub's USB 3 side should connect here, but nothing did, so the hub runs at USB 2.";
        return new(what, speed.Affects, speed.Cause, speed.Steps);
    }

    // A connection on the canvas: what its width, dashes and color say about the link it stands for.
    internal static string LinkHelp(UsbNode n, string fromPath)
    {
        string rate = n.LinkMbps switch { 1.5 => "1.5 Mb/s (low speed)", 12 => "12 Mb/s (full speed, USB 1)", 480 => "480 Mb/s (high speed, USB 2)", 5000 => "5 Gb/s (SuperSpeed, USB 3)",
            _ => n.Speed.StartsWith("SuperSpeedPlus", StringComparison.Ordinal) ? "10 Gb/s or faster (SuperSpeedPlus)" : "an unreported rate" };
        var lines = new List<string>
        {
            $"{Topology.ShortName(n)} connects to {fromPath} at {rate}.",
            "Width is the negotiated link rate, the most the connection can signal, shared with everything upstream. Wider is faster: 12 Mb/s, 480 Mb/s, 5 Gb/s and 10 Gb/s+.",
        };
        if (HubRelationships.ReducedSpeed(n))
            lines.Add($"Dashed: it runs slower than it supports ({SpeedLabel(n)}). " + (SpeedSeverity(n) == Severity.Warning
                ? "Amber because that slows something plugged in now."
                : n.Connector == "Internal" ? "Gray because it's built in, so there's nothing to change."
                : "Gray because nothing plugged in is slowed by it now."));
        lines.Add("It turns blue along the path to the selected card.");
        lines.Add($"Click to select {Topology.ShortName(n)}.");
        return string.Join("\n", lines);
    }

    // path runs from the host controller down to n.
    internal static Explanation Speed(UsbNode n, IReadOnlyList<UsbNode> path)
    {
        bool usb2 = n.LinkMbps != 5000, fullSpeed = n.LinkMbps == 12, hub = n.Kind == "Hub";
        string noun = hub ? "hub" : "device", limit = fullSpeed ? "12 Mb/s" : usb2 ? "USB 2 speed" : "5 Gb/s";
        string now = n.LinkMbps switch { 5000 => "5 Gb/s", 480 => "USB 2 (480 Mb/s)", 12 => "12 Mb/s (USB 1 speed)", double rate => UsbBudgets.Rate(rate), null => "a slower speed" };
        string supports = n.SuperSpeedPlusCapable == true ? "10 Gb/s or faster" : n.SpeedLimited ? "USB 3 (5 Gb/s)" : "USB 2 (480 Mb/s)";
        string what = $"This {noun} is connected at {now}, though it supports {supports}.";

        string affects;
        if (!hub) affects = $"Yes: its transfers are limited to {limit}.";
        else if (HeldBack(n) is { Count: > 0 } held)
            affects = $"Yes: {Kinds(held)} {(held.Count == 1 ? "supports" : "support")} a faster link but {(held.Count == 1 ? "is" : "are")} slowed to {limit} through this hub.";
        else
        {
            var devices = n.Walk().Skip(1).Where(d => d.Kind == "Device").ToList();
            string fine = devices.Count == 0 ? "nothing is plugged into it"
                : fullSpeed ? $"{Kinds(devices)} {(devices.Count == 1 ? "needs no more than 12 Mb/s, so it loses" : "need no more than 12 Mb/s, so they lose")} nothing"
                : usb2 ? $"{Kinds(devices)} {(devices.Count == 1 ? "is a USB 2 device, so it loses" : "are USB 2 devices, so they lose")} nothing"
                : $"{Kinds(devices)} {(devices.Count == 1 ? "doesn't" : "don't")} support more than 5 Gb/s";
            affects = $"Not right now: {fine}. A drive, camera or network adapter plugged in here would be limited to {limit}.";
        }

        var (cause, steps) = SpeedCause(n, path, usb2);
        return new(what, affects, cause, steps);
    }

    private static (string Cause, List<string> Steps) SpeedCause(UsbNode n, IReadOnlyList<UsbNode> path, bool usb2)
    {
        bool usbC = n.Connector == "USB-C";
        string noun = n.Kind == "Hub" ? "hub" : "device", cable = usb2 ? "5 Gb/s" : "10 Gb/s";
        string port = usb2 ? "a USB 3 port" : "a 10 Gb/s port";
        string tongue = usb2 ? "In USB Atlas, a blue or red tongue marks a USB 3 socket." : "In USB Atlas, a red tongue marks a socket known or expected to carry 10 Gb/s.";
        string seat = "Make sure the plug is fully in, and if it goes through an extension cable or adapter, try without it.";
        string cableStep = $"Use a cable rated {cable} or faster." + (usbC ? " Many USB-C cables, especially charging cables, carry only USB 2." : usb2 ? " On USB-A, a blue tongue inside the plug marks USB 3." : "");
        if (n.Connector == "Internal")
            return ("It's built in and wired this way inside the computer or enclosure, so there's nothing to change.", []);
        if (n.Usb3SideFailed)
            return ("Its USB 3 side tried to connect and failed; the other half of its socket shows the error.", [seat, cableStep]);
        if (n.Usb3SideMissing)
            return usbC
                ? ("Its USB-C connection isn't carrying USB 3.",
                    ["If this hub is in a monitor and the picture comes through the same cable, the cable is fine: the monitor is using the cable's fast lanes for the display. Some monitors have a menu setting, such as USB-C Prioritization, that gives some of them to USB at a cost in resolution or refresh rate. Without one, USB over this cable stays at USB 2: plug drives and other fast devices into the computer instead.",
                     "Otherwise, the cable probably carries only USB 2, as many charging cables do. Use one rated 5 Gb/s or faster."])
                : ("Its USB 3 connection didn't come up; only its USB 2 side is connected.", [seat, cableStep]);
        // At 12 Mb/s where high speed was possible: a USB 1.1 hub on the way, or a connection that couldn't hold
        // high speed's faster signaling.
        if (n.LinkMbps == 12)
        {
            var before = path.TakeWhile(p => p.Id != n.Id).ToList();
            if (before.LastOrDefault(p => p.Kind == "Hub" && p.LinkMbps is <= 12) is UsbNode slow)
                return ((slow == before.LastOrDefault() ? "The hub it's plugged into" : $"It's connected through {Topology.ShortName(slow)}, which") + " runs at 12 Mb/s (USB 1 speed); old hubs and some KVM switches carry only that.",
                    [$"Plug this {noun} into a port on the computer, or a USB 2 or USB 3 hub."]);
            return ("Its connection couldn't hold USB 2's high speed. Long, thin or damaged cables, extension cables and some adapters cause this.",
                [seat, "Use a shorter, good-quality cable.", "Try another port."]);
        }
        // A hub upstream that runs slower holds everything behind it back. A paired USB 3 hub's USB 2 side
        // isn't one: the device could have used its USB 3 side.
        var upstream = path.TakeWhile(p => p.Id != n.Id).LastOrDefault(p => p.Kind == "Hub" && !p.IsUsb2Companion && (usb2 ? p.LinkMbps is <= 480 : p.LinkMbps == 5000));
        if (upstream != null)
        {
            var steps = new List<string>();
            if (upstream.Usb3SideMissing) steps.Add("Fix that hub's USB 3 connection: select it to see how.");
            steps.Add($"{(steps.Count > 0 ? "Or plug" : "Plug")} this {noun} into {port} on the computer or a faster hub. {tongue}");
            string rate = usb2 ? "USB 2" : "5 Gb/s";
            bool direct = upstream == path.TakeWhile(p => p.Id != n.Id).LastOrDefault();
            return (direct ? $"The hub it's plugged into runs at {rate}." : $"It's connected through {Topology.ShortName(upstream)}, which runs at {rate}.", steps);
        }
        return (usb2, n.SocketSpeed) switch
        {
            (true, "USB 2.0") => ("This port supports only USB 2.", [$"Plug it into {port}. {tongue}"]),
            (false, "5 Gb/s") => ("This port supports up to 5 Gb/s.", [$"Plug it into {port}. {tongue}"]),
            (false, "≥5 Gb/s") => ("This port may support only 5 Gb/s; Windows doesn't say until something links faster.", ["Try a port labeled 10 Gb/s on the computer.", "If this port does support 10 Gb/s, use a cable rated 10 Gb/s or faster."]),
            (_, "Not reported") => ("Windows didn't report what this port supports.", [$"Check that it's {port}. {tongue}", seat, cableStep]),
            _ => ($"The port supports {(usb2 ? "USB 3" : "10 Gb/s")}, so the cable or the plug is the likely cause.", [seat, cableStep])
        };
    }

    // Devices by what they are, as in "your keyboard, mouse and 1 other device": shorter than product names,
    // and what people call them. A device you named keeps its name, and so does a lone device of unknown kind.
    internal static string Kinds(List<UsbNode> devices)
    {
        if (devices.Count == 1 && (devices[0].UserLabel.Length > 0 || Kind(devices[0]) == null)) return Topology.ShortName(devices[0]);
        var parts = devices.Where(d => d.UserLabel.Length == 0 && Kind(d) != null).GroupBy(d => Kind(d)!)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} {(g.Key == "mouse" ? "mice" : g.Key + "s")}").ToList();
        bool typed = parts.Count > 0;
        parts.AddRange(devices.Where(d => d.UserLabel.Length > 0).Select(d => d.UserLabel));
        int others = devices.Count(d => d.UserLabel.Length == 0 && Kind(d) == null);
        if (others > 0) parts.Add(parts.Count > 0 ? $"{others} other device{(others == 1 ? "" : "s")}" : $"its {others} devices");
        string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return typed ? "your " + list : list;
    }
    private static string? Kind(UsbNode d) => d.DeviceType switch
    {
        "Keyboard" => "keyboard", "Mouse" => "mouse", "Camera / video" => "camera", "Audio" => "audio device", "Game controller" => "game controller",
        "VR headset" => "VR headset", "Storage" => "drive", "External drive" => "external drive", "Flash drive" => "flash drive", "Card reader" => "card reader",
        "Optical drive" => "optical drive", "Floppy drive" => "floppy drive", "Printer" => "printer", "Wireless" => "wireless adapter",
        "Serial / communications" => "communications device", _ => null
    };
    // "A", "A and B", "A, B and C", or "A, B and 3 more devices".
    internal static string Names(List<UsbNode> nodes)
    {
        var names = nodes.Select(Topology.ShortName).ToList();
        if (names.Count > 3) names = [.. names.Take(2), $"{names.Count - 2} more devices"];
        return names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
    }
}

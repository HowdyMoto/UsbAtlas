using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// Small builders for report objects. Null fields are left out, so text and JSON show only what applies.
internal static class J
{
    internal static JsonObject Obj(params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields) if (value != null) o[key] = value;
        return o;
    }
    internal static JsonArray Arr(IEnumerable<JsonNode?> items) => new([.. items]);
    // Null for an empty list, so it's left out.
    internal static JsonArray? Some(IEnumerable<JsonNode?> items) { var a = Arr(items); return a.Count == 0 ? null : a; }
    internal static JsonNode? S(string? value) => string.IsNullOrEmpty(value) ? null : value;
    internal static JsonNode? N(double? value, int digits = 3) => value is double v ? Math.Round(v, digits) : null;
}

// What each command reports, as one object that prints as JSON or text and that the MCP tools return.
internal static class Reports
{
    internal static string VidPid(UsbNode n) => n.VendorId.Length > 0 ? $"{n.VendorId}:{n.ProductId}" : "";
    internal static JsonObject Ref(Session s, UsbNode n) => J.Obj(("path", s.PathOf(n)), ("name", Topology.ShortName(n)), ("kind", Topology.Label(n)), ("vidPid", J.S(VidPid(n))));
    internal static string RefText(Session s, UsbNode n) => $"{s.PathOf(n)} {Topology.ShortName(n)} ({Topology.Label(n)}{(VidPid(n) is { Length: > 0 } id ? ", " + id : "")})";

    // A merged host's root-hub issues are the host's, as on its card.
    internal static List<(Severity Severity, string Text, UsbNode Node)> IssuesOf(Session s, UsbNode n)
    {
        var list = IssueRules.For(n).Select(i => (i.Severity, i.Text, n)).ToList();
        if (Topology.MergedRoot(n) is UsbNode root) list.AddRange(IssueRules.For(root).Select(i => (i.Severity, i.Text, root)));
        return list;
    }

    internal static JsonObject Header(Session s, string kind) => J.Obj(
        ("schemaVersion", Session.SchemaVersion), ("report", kind), ("source", s.Source), ("capturedAt", s.Snapshot.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ssK")),
        ("redacted", s.Redacted ? "serial numbers are replaced by hashes that stay the same within this output" : null));

    internal static JsonObject PowerPlan(Snapshot snapshot) => J.Obj(
        ("usbSelectiveSuspend", PowerSaving.PlanSummary(snapshot)),
        ("pluggedIn", snapshot.UsbSuspendPluggedIn is bool p ? (p ? "on" : "off") : "not reported"),
        ("onBattery", snapshot.UsbSuspendOnBattery is bool b ? (b ? "on" : "off") : "not reported"),
        ("powerSource", snapshot.OnBattery switch { true => "battery", false => "plugged in", null => "not reported" }));

    internal static JsonObject Counts(Session s) => J.Obj(
        ("controllers", s.Snapshot.Controllers.Count),
        ("hubs", s.Snapshot.Nodes.Count(n => n.Kind == "Hub")),
        ("devices", s.Snapshot.Nodes.Count(n => n.Kind == "Device")),
        ("emptyPorts", s.Snapshot.Nodes.Count(n => n.Kind == "Empty port")),
        ("portErrors", s.Snapshot.Nodes.Count(n => n.Kind == "Unavailable")));

    internal static JsonObject Explain(Session s, Severity severity, string issue, UsbNode at, bool evidence)
    {
        var e = Explanations.For(at, issue, s.Chain(at));
        var shown = s.IsMergedRoot(at) ? s.Parent(at)! : at;
        return J.Obj(("severity", severity.ToString().ToLowerInvariant()), ("issue", issue),
            ("path", s.PathOf(shown)), ("name", Topology.ShortName(shown)), ("kind", Topology.Label(shown)), ("vidPid", J.S(VidPid(shown))),
            ("what", e.What), ("affects", J.S(e.Affects)), ("cause", J.S(e.Cause)), ("steps", J.Some((e.Steps ?? []).Select(x => (JsonNode)x))),
            ("evidence", evidence ? J.Some(at.Notes.Select(x => (JsonNode)x)) : null));
    }

    internal static Severity? ParseSeverity(string? text) => text?.ToLowerInvariant() switch
    {
        null => null, "note" or "notes" => Severity.Note, "warning" or "warnings" => Severity.Warning, "error" or "errors" => Severity.Error,
        _ => throw new CliException("--min is note, warning or error.")
    };

    internal static JsonObject Issues(Session s, Severity min = Severity.Note, bool evidence = true)
    {
        var all = s.Listed.SelectMany(n => IssuesOf(s, n)).ToList();
        var shown = all.Where(i => i.Severity >= min).OrderByDescending(i => i.Severity).ToList();
        var report = Header(s, "issues");
        report["summary"] = J.Obj(("errors", all.Count(i => i.Severity == Severity.Error)), ("warnings", all.Count(i => i.Severity == Severity.Warning)), ("notes", all.Count(i => i.Severity == Severity.Note)));
        report["fixFirst"] = FixFirst(s);
        report["counts"] = Counts(s);
        report["powerPlan"] = PowerPlan(s.Snapshot);
        if (s.Snapshot.LastWake is WakeInfo wake)
        {
            var source = s.Snapshot.Nodes.FirstOrDefault(n => wake.InstanceId.Length > 0 && n.InstanceId.Equals(wake.InstanceId, StringComparison.OrdinalIgnoreCase));
            report["lastWake"] = J.Obj(("time", wake.Time.ToString("yyyy-MM-dd HH:mm")), ("source", wake.Source.Length > 0 ? wake.Source : "Not recorded by Windows"),
                ("path", source != null ? s.PathOf(source) : null), ("name", source != null ? Topology.ShortName(source) : null));
        }
        if (s.Snapshot.Diagnostics.Count > 0) report["scanDiagnostics"] = J.Arr(s.Snapshot.Diagnostics.Select(d => (JsonNode)d));
        report["issues"] = J.Arr(shown.Select(i => (JsonNode)Explain(s, i.Severity, i.Text, i.Node, evidence)));
        return report;
    }
    // The few issues to fix first, each with its most likely fix, as the app's Fix first strip shows them.
    internal static JsonArray FixFirst(Session s) => J.Arr(Triage.FixFirst(s.Snapshot).Select(i =>
    {
        var shown = s.IsMergedRoot(i.Node) ? s.Parent(i.Node)! : i.Node;
        return (JsonNode)J.Obj(("severity", i.Severity.ToString().ToLowerInvariant()), ("issue", i.Issue), ("path", s.PathOf(shown)), ("name", Topology.ShortName(shown)),
            ("fix", i.Fix), ("affectsNow", i.AffectsNow),
            ("alsoFixes", J.Some(i.Also.Select(a => (JsonNode)J.Obj(("issue", a.Issue), ("path", s.PathOf(s.IsMergedRoot(a.Node) ? s.Parent(a.Node)! : a.Node)), ("name", Topology.ShortName(a.Node)))))));
    }));
    // 0 clean or notes only, 1 warnings, 2 errors. A scan diagnostic, such as a problem Windows reports on
    // something the scan couldn't place, counts as a warning, as in the app's issue list.
    internal static int HealthCode(Session s) => s.Listed.SelectMany(n => IssuesOf(s, n)).Select(i => (Severity?)i.Severity).Max() switch
    {
        Severity.Error => 2, Severity.Warning => 1, _ => s.Snapshot.Diagnostics.Count > 0 ? 1 : 0
    };

    // One line of figures, as on a card: link, polling, power.
    internal static string Figures(UsbNode n)
    {
        var parts = new List<string>();
        if (n.Kind is "Device" or "Hub") parts.Add(Topology.ShortSpeed(n));
        if (Topology.ShowsPolling(n)) parts.Add(UsbBudgets.PollingRate(n.PollIntervalMs!.Value));
        if (n.Kind is "Device" or "Hub" or "Unavailable" && (n.MaxPowerMa.HasValue || Topology.DeclaresSelfPower(n))) parts.Add(Topology.PowerFigure(n).Text);
        if (n.Kind == "Unavailable") parts.Insert(0, n.Status);
        if (n.Kind == "Controller" && Topology.PciText(n) is { Length: > 0 } pci) parts.Add(pci);
        return string.Join(" · ", parts);
    }

    // The hub whose USB 3 side belongs on this empty or failed port, by path.
    private static string? HalfOf(Session s, UsbNode port) => HubRelationships.MissingUsb3HubFor(port, s.Snapshot) is UsbNode hub ? s.PathOf(hub) : null;

    // A device Windows remembers but that isn't connected, where it was and when.
    private static JsonObject Remembered(RememberedDevice r, string place) => J.Obj(("place", place), ("name", J.S(r.Name)), ("vidPid", $"{r.VendorId}:{r.ProductId}"), ("comPort", J.S(r.ComPort)),
        ("lastConnected", r.LastConnected?.ToString("yyyy-MM-dd HH:mm")), ("lastRemoved", r.LastRemoved?.ToString("yyyy-MM-dd HH:mm")), ("instanceId", r.InstanceId));

    internal static JsonObject Tree(Session s, bool ports, bool hidden = false)
    {
        JsonObject Node(UsbNode n)
        {
            var children = (Topology.MergedRoot(n) ?? n).Children;
            var o = J.Obj(("path", s.PathOf(n)), ("name", Topology.ShortName(n)), ("kind", Topology.Label(n)), ("vidPid", J.S(VidPid(n))), ("revision", J.S(n.DeviceRevision)), ("figures", J.S(Figures(n))),
                ("label", J.S(n.UserLabel)), ("portName", J.S(n.PortLabel)),
                ("issues", J.Some(IssuesOf(s, n).Select(i => (JsonNode)$"{i.Severity.ToString().ToLowerInvariant()}: {i.Text}"))),
                ("usb3HalfOf", HalfOf(s, n)));
            var shown = children.Where(c => ports || c.Kind != "Empty port").OrderBy(c => c.Port).ToList();
            if (shown.Count > 0) o["children"] = J.Arr(shown.Select(c => (JsonNode)Node(c)));
            var empty = children.Where(c => c.Kind == "Empty port").OrderBy(c => c.Port).ToList();
            // An empty port isn't always idle: it may be where a hub's USB 3 side should have connected.
            if (!ports && empty.Count > 0) o["emptyPorts"] = J.Arr(empty.Select(p => (JsonNode)(p.Port.ToString("00") + (HalfOf(s, p) is string hub ? $" (USB 3 half of {hub}'s socket, not connected)" : ""))));
            return o;
        }
        var report = Header(s, "tree");
        report["counts"] = Counts(s);
        report["controllers"] = J.Arr(s.Snapshot.Controllers.Select(c => (JsonNode)Node(c)));
        if (hidden)
        {
            var labels = Topology.PathLabels(s.Snapshot);
            report["remembered"] = s.Snapshot.Remembered == null ? "Not read: this snapshot was saved before remembered devices were, or comes from Linux."
                : J.Arr(s.Snapshot.Remembered.Select(r => (JsonNode)Remembered(r, UsbAtlas.Remembered.Place(s.Snapshot, r, labels))));
        }
        return report;
    }

    internal static JsonObject Find(Session s, string query)
    {
        var report = Header(s, "find");
        report["query"] = query;
        report["matches"] = J.Arr(s.Find(query).Select(n =>
        {
            var o = Ref(s, n);
            o["figures"] = J.S(Figures(n));
            if (IssuesOf(s, n) is { Count: > 0 } issues) o["issues"] = J.Arr(issues.Select(i => (JsonNode)i.Text));
            return (JsonNode)o;
        }));
        return report;
    }

    // Everything known about one node, its place and its neighbors, and what its issues mean.
    internal static JsonObject Show(Session s, UsbNode n)
    {
        var chain = s.Chain(n).Where(c => !s.IsMergedRoot(c) || c == n).ToList();
        var parent = chain.Count >= 2 ? chain[^2] : null;
        var report = Header(s, "node");
        var node = J.Obj(
            ("path", s.PathOf(n)), ("name", n.DisplayName), ("shortName", Topology.ShortName(n)), ("kind", Topology.Label(n)), ("nodeKind", n.Kind),
            ("label", J.S(n.UserLabel)), ("portName", J.S(n.PortLabel)), ("status", n.Status),
            ("deviceType", n.Kind == "Device" ? n.DeviceType : null), ("typeEvidence", n.Kind == "Device" ? n.TypeEvidence : null), ("wirelessReceiver", Interference.IsReceiver(n) ? true : null),
            ("vidPid", J.S(VidPid(n))), ("revision", J.S(n.DeviceRevision)), ("manufacturer", J.S(n.Manufacturer)), ("product", J.S(n.ReportedProduct)), ("windowsName", J.S(n.WindowsName)),
            ("lookup", J.S(string.Join(" · ", new[] { n.LookupVendor, n.LookupProduct }.Where(x => x.Length > 0)))), ("nameSource", n.NameSource),
            ("serial", J.S(n.Serial)), ("comPort", J.S(n.ComPort)), ("instanceId", J.S(n.InstanceId)), ("id", n.Id),
            ("deviceClass", J.S(n.DeviceClass)), ("interfaceFunctions", J.Some(n.InterfaceFunctions.Select(x => (JsonNode)x))), ("hidUsages", J.Some(n.HidUsages.Select(x => (JsonNode)x))));
        if (n.Kind is "Device" or "Hub" or "Unavailable")
            node["link"] = J.Obj(("usbVersion", n.UsbVersion), ("speed", n.Speed), ("linkMbps", J.N(n.LinkMbps)), ("lanes", n.LinkLanes), ("superSpeedPlusCapable", n.SuperSpeedPlusCapable),
                ("slowerThanSupported", n.SpeedLimited || HubRelationships.FullSpeedOnly(n) ? true : null), ("highSpeedCapable", n.HighSpeedCapable), ("protocols", n.Protocols),
                ("typicalBestTransfer", UsbBudgets.BestTransfer(n.LinkMbps) is { Length: > 0 } best ? $"{best} for a fast drive; typical, not measured" : null));
        else node["protocols"] = J.Obj(("ports", n.Protocols), ("downstream", J.S(n.DownstreamProtocols)));
        if (parent != null || n.Port > 0)
            node["socket"] = J.Obj(("port", n.Port), ("connector", n.Connector), ("socketSpeed", n.SocketSpeed), ("ratedMbps", J.N(n.SocketRatedMbps)), ("connectorSet", J.S(n.SocketConnectorSet)), ("evidence", J.S(n.SocketEvidence)),
                ("userConnectable", n.PortIsUserConnectable), ("usbC", n.PortConnectorIsTypeC), ("debugCapable", n.PortIsDebugCapable),
                ("sharesSocketWith", n.CompanionId.Length > 0 && s.ById(n.CompanionId) is UsbNode c ? s.PathOf(c) : null),
                ("alsoSharesSocketWith", J.Some(n.MoreCompanions.Where(m => m.Id.Length > 0 && s.ById(m.Id) != null).Select(m => (JsonNode)s.PathOf(s.ById(m.Id)!)))),
                ("usbCFeatures", UsbC.Socket(s.Snapshot, n) is { Count: > 0 } features ? J.Obj([.. features.Select(f => (f.Feature, (JsonNode?)f.Text))]) : null));
        if (n.PciId.Length > 0 || n.PcieTunneled == true)
            node["controller"] = J.Obj(("pciId", J.S(n.PciId)), ("vendor", J.S(Topology.PciVendor(n.PciId))), ("subsystem", J.S(n.PciSubsystem)), ("revision", J.S(n.PciRevision)), ("pciAddress", J.S(n.PciAddress)),
                ("pcieLink", n.PcieGeneration is int g && n.PcieLanes is int l ? J.Obj(("generation", g), ("lanes", l), ("maxGeneration", n.PcieMaxGeneration), ("maxLanes", n.PcieMaxLanes), ("mbps", J.N(UsbBudgets.PcieMbps(g, l)))) : null),
                ("tunneledOverUsb4OrThunderbolt", n.PcieTunneled == true ? true : null),
                ("endpointsInUse", UsbBudgets.ControllerLoad(n).Endpoints));
        if (n.Kind is "Controller" or "Root hub" && UsbC.Computer(s.Snapshot) is { Count: > 0 } computer)
            node["usbC"] = J.Arr(computer.Select(x => (JsonNode)x));
        node["location"] = J.Obj(("where", n.Location), ("evidence", n.LocationEvidence));
        if (n.Kind is not ("Controller" or "Root hub"))
            node["hubsAbove"] = J.Obj(("count", n.HubsAbove), ("limit", HubDepth.Max), ("chain", J.Some(HubDepth.Above(s.Chain(n)).Select(h => (JsonNode)$"{s.PathOf(h)} {Topology.ShortName(h)}"))));
        if (n.Kind is "Device" or "Hub" or "Unavailable")
            node["power"] = J.Obj(("figure", Topology.PowerFigure(n).Text), ("source", n.PowerSource), ("maxPowerMa", n.MaxPowerMa), ("selfPowerCapable", n.SelfPowerCapable), ("evidence", Topology.PowerEvidence(n)),
                ("drawThroughPortMa", n.Kind == "Hub" ? UsbBudgets.Demand(n).Known : null), ("warnings", J.Some(n.PowerWarnings.Select(x => (JsonNode)x))));
        if (n.Kind is "Device" or "Hub" or "Root hub" or "Controller")
            node["powerSaving"] = J.Obj(("setting", Topology.PowerSavingText(Topology.MergedRoot(n) ?? n, s.Snapshot)), ("plan", PowerSaving.PlanSummary(s.Snapshot)),
                ("canWakeComputer", n.Kind is "Device" or "Hub" ? n.WakeSetting : null), ("wokeComputerAt", n.WokeComputerAt?.ToString("yyyy-MM-dd HH:mm")));
        // What the reservation can do, and why that means its link can or can't fill, as the app's cards say it.
        if (n.ReservedMbps != null || n.PollIntervalMs != null || n.OpenPipes.Count > 0)
            node["bandwidth"] = J.Obj(("reservedMbps", J.N(n.ReservedMbps, 4)), ("peakReservedMbps", J.N(n.PeakReservedMbps, 4)),
                ("pollIntervalMs", J.N(n.PollIntervalMs)), ("pollingRate", n.PollIntervalMs is double ms ? UsbBudgets.PollingRate(ms) : null),
                ("reservation", n.Kind == "Device" ? ReservationKind(n) : null), ("note", n.Kind == "Device" ? ReservationNote(s, n) : null),
                ("openPipes", J.Some(n.OpenPipes.Select(x => (JsonNode)x))));
        if (n.Kind is "Hub" or "Root hub" or "Controller")
        {
            var hub = Topology.MergedRoot(n) ?? n;
            node["hub"] = J.Obj(("ports", hub.PortCount), ("inUse", hub.Children.Count(c => c.Kind != "Empty port")),
                ("transactionTranslators", n.Kind == "Hub" ? Topology.TtType(n) : null),
                ("pairedWith", n.CompanionHubId.Length > 0 && s.ById(n.CompanionHubId) is UsbNode pair ? s.PathOf(pair) : null),
                ("usb3SideMissing", n.Usb3SideMissing ? true : null), ("relationship", J.S(HubRelationships.Description(n, s.Snapshot))));
        }
        if (n.DriverService.Length > 0 || n.DriverVersion.Length > 0 || n.DriverProblems.Count > 0)
            node["driver"] = J.Obj(("service", J.S(n.DriverService)), ("version", J.S(n.DriverVersion)), ("date", J.S(n.DriverDate)), ("provider", J.S(n.DriverProvider)), ("inf", J.S(n.DriverInf)),
                ("problems", J.Some(n.DriverProblems.Select(p => (JsonNode)J.Obj(("code", p.Code), ("meaning", p.Meaning), ("instanceId", p.InstanceId), ("name", J.S(p.Name)))))));
        if (n.ContainerId.Length > 0 && !Containers.IsRoot(n.ContainerId))
        {
            var c = s.Snapshot.Containers.FirstOrDefault(x => x.Id.Equals(n.ContainerId, StringComparison.OrdinalIgnoreCase));
            var part = Containers.Of(s.Snapshot, n);
            node["container"] = J.Obj(("id", n.ContainerId), ("name", J.S(c?.Name)), ("manufacturer", J.S(c?.Manufacturer)), ("model", J.S(c?.Model)),
                ("partOf", J.S(part?.Product)), ("sameProduct", part is { } p ? J.Some(p.Others.Select(o => (JsonNode)J.Obj(("path", s.PathOf(o)), ("name", Topology.ShortName(o))))) : null),
                ("sharedWithUnrelated", J.Some(Containers.SharingWith(s.Snapshot, n).Select(o => (JsonNode)J.Obj(("path", s.PathOf(o)), ("name", Topology.ShortName(o)))))));
        }
        if (n.Billboard is BillboardInfo b)
            node["billboard"] = J.Obj(("version", b.Version), ("vconnPower", b.VconnPower), ("preferredMode", b.PreferredMode),
                ("insufficientPower", b.InsufficientPower ? true : null), ("powerDeliveryFailed", b.PowerDeliveryFailed ? true : null), ("additionalInfoUrl", J.S(b.AdditionalInfoUrl)),
                ("modes", J.Arr(b.Modes.Select(m => (JsonNode)J.Obj(("index", m.Index), ("svid", m.Svid), ("name", m.Name), ("description", J.S(m.Description)), ("state", m.State.ToLowerInvariant()), ("vdo", J.S(m.Vdo)))))));
        if (n.OtherEntries.Count > 0) node["otherEntries"] = J.Arr(n.OtherEntries.Select(o => (JsonNode)Remembered(o.Entry, o.Place)));
        if (Uas.Summary(n).Length > 0)
            node["storage"] = J.Obj(("protocol", J.S(n.StorageProtocol)), ("offersUas", n.OffersUas), ("summary", Uas.Summary(n)));
        if (KnownProblems.For(n) is KnownProblems.Entry known)
            node["knownProblem"] = J.Obj(("problem", KnownProblems.Short(known.Problem)), ("chip", known.Name), ("revisions", known.AllRevisions ? "all" : n.DeviceRevision),
                ("summary", KnownProblems.Summary(n)), ("signs", J.Some(KnownProblems.Signs(n, known).Select(x => (JsonNode)x))), ("source", known.Source));
        if (n.QuickReconnects > 0)
            node["reconnects"] = J.Obj(("count", n.QuickReconnects), ("times", J.Arr(n.QuickReconnectTimes.Select(t => (JsonNode)t.ToString("HH:mm:ss")))));
        if (n.Display is DisplayFinding f)
            node["display"] = J.Obj(("finding", UsbAtlas.Displays.NotShowing), ("lastDisplay", J.S(f.DisplayName)), ("lastShownThrough", J.S(f.GpuName)),
                ("lastShown", f.LastShown?.ToString("yyyy-MM-dd HH:mm")), ("adapterHasDriver", f.GpuName.Length > 0 ? !f.GpuWithoutDriver : null));
        report["node"] = node;
        // An empty socket half where a hub's USB 3 side should be explains that first, as the app does.
        if (HubRelationships.MissingUsb3HubFor(n, s.Snapshot) is UsbNode lost)
        {
            var e = Explanations.MissingUsb3Half(lost, s.Chain(lost));
            report["usb3HalfOf"] = J.Obj(("hub", s.PathOf(lost)), ("name", Topology.ShortName(lost)), ("severity", Explanations.SpeedSeverity(lost).ToString().ToLowerInvariant()),
                ("what", e.What), ("affects", J.S(e.Affects)), ("cause", J.S(e.Cause)), ("steps", J.Some((e.Steps ?? []).Select(x => (JsonNode)x))));
        }
        report["upstream"] = J.Arr(chain.SkipLast(1).Select(c => (JsonNode)J.Obj(("path", s.PathOf(c)), ("name", Topology.ShortName(c)), ("kind", Topology.Label(c)), ("figures", J.S(Figures(c))))));
        if (parent != null)
            report["siblings"] = J.Arr((Topology.MergedRoot(parent) ?? parent).Children.Where(c => c != n && c.Kind != "Empty port").OrderBy(c => c.Port)
                .Select(c => (JsonNode)J.Obj(("path", s.PathOf(c)), ("name", Topology.ShortName(c)), ("kind", Topology.Label(c)), ("figures", J.S(Figures(c))))));
        var children = (Topology.MergedRoot(n) ?? n).Children;
        if (children.Count > 0)
            report["children"] = J.Arr(children.OrderBy(c => c.Port).Select(c => (JsonNode)(c.Kind == "Empty port"
                ? J.Obj(("path", s.PathOf(c)), ("kind", "Empty port"), ("connector", c.Connector), ("socketSpeed", c.SocketSpeed), ("usb3HalfOf", HalfOf(s, c)))
                : J.Obj(("path", s.PathOf(c)), ("name", Topology.ShortName(c)), ("kind", Topology.Label(c)), ("figures", J.S(Figures(c)))))));
        report["issues"] = J.Arr(IssuesOf(s, n).Select(i => (JsonNode)Explain(s, i.Severity, i.Text, i.Node, false)));
        report["notes"] = J.Some(n.Notes.Select(x => (JsonNode)x));
        return report;
    }

    // Graphics adapters, every monitor Windows has known and the USB-C displays whose picture is missing.
    // A USB-C monitor's picture doesn't travel as USB, so this is where a dark monitor with working USB leads.
    internal static JsonObject Displays(Session s)
    {
        var report = Header(s, "displays");
        report["note"] = "A USB-C monitor's picture travels in a USB-C alternate mode, beside its USB, so its hub, keyboard and mouse can work while it shows nothing. The adapter a monitor was last shown through is a hint at which one drives that port: laptops that can switch adapters may use either.";
        report["adapters"] = J.Arr(s.Snapshot.Gpus.Select(g => (JsonNode)J.Obj(("name", UsbAtlas.Displays.GpuLabel(g)), ("vendor", J.S(g.Vendor)),
            ("driver", g.HasDriver ? $"running ({g.Service})" : g.Service.Length == 0 ? "none" : $"not running (Code {g.ProblemCode})"), ("instanceId", g.InstanceId))));
        string Through(DisplayInfo d) => s.Snapshot.Gpus.FirstOrDefault(g => g.InstanceId.Equals(d.GpuInstanceId, StringComparison.OrdinalIgnoreCase)) is GpuInfo g ? UsbAtlas.Displays.GpuLabel(g) : d.GpuInstanceId;
        report["monitors"] = J.Arr(s.Snapshot.Displays.OrderByDescending(d => d.Present).ThenByDescending(d => d.LastRemoval ?? d.LastArrival).Select(d => (JsonNode)J.Obj(
            ("name", UsbAtlas.Displays.MonitorName(d.Name)), ("where", d.External ? "external" : "built in"), ("connected", d.Present),
            (d.Present ? "shownThrough" : "lastShownThrough", J.S(Through(d))), ("lastConnected", d.LastArrival?.ToString("yyyy-MM-dd HH:mm")),
            ("lastRemoved", d.Present ? null : d.LastRemoval?.ToString("yyyy-MM-dd HH:mm")), ("instanceId", d.InstanceId))));
        report["usbCDisplays"] = J.Arr(s.Listed.Where(n => n.Display != null).Select(n => (JsonNode)Explain(s, UsbAtlas.Displays.SeverityOf(n), UsbAtlas.Displays.NotShowing, n, true)));
        if (s.Snapshot.Gpus.Count == 0 && s.Snapshot.Displays.Count == 0)
            report["note"] = s.Source == "live" ? "Windows reported no graphics adapters or monitors to this scan." : "This snapshot has no display information; take one with a current atlascli scan.";
        return report;
    }

    // Bandwidth and power arithmetic with its inputs, for hubs and anything with a budget issue.
    internal static JsonObject Budget(Session s, UsbNode? target)
    {
        var report = Header(s, "budget");
        report["assumptions"] = "Reserved bandwidth and power come from descriptors, not measurements. Periodic (interrupt, isochronous) pipes reserve bus time; bulk transfers share what is left. Reservable capacity is 90% of a low/full-speed frame, 80% of a high-speed microframe, and 90% of SuperSpeed bus time after encoding. Power limits are what the USB specification guarantees: 500 mA (USB 2) or 900 mA (USB 3) from a standard port, 100 mA (USB 2) or 150 mA (USB 3) per port of a bus-powered hub.";
        bool budgetIssue(UsbNode n) => IssueRules.For(n).Any(i => i.Text is "Link nearly full" or "Could exceed when streaming" or "Shared TT nearly full" or "Shared TT could exceed" or "Insufficient bandwidth" or "Power at risk" or "Over power budget" or "Insufficient power" or Explanations.AdapterNotDetected);
        var nodes = target != null ? [target] : s.Listed.Where(n => n.Kind == "Hub" || budgetIssue(n)).ToList();
        report["nodes"] = J.Arr(nodes.Select(n => (JsonNode)BudgetOf(s, n)));
        return report;
    }

    // "fixed", "none", "streaming" or "unknown": what a device's reservation can do.
    private static string ReservationKind(UsbNode n) => UsbBudgets.ReservationOf(n).ToString().ToLowerInvariant();
    private static string ReservationNote(Session s, UsbNode n) => string.Join(" ", new[] { UsbBudgets.ReservationNote(n), UsbBudgets.LinkSharing(n, s.Parent(n)) }.Where(x => x.Length > 0));

    private static JsonObject BudgetOf(Session s, UsbNode n)
    {
        var o = Ref(s, n);
        if (UsbBudgets.LinkUse(n) is (var reserved, var capacity, var unknown))
        {
            double peak = Math.Max(reserved, UsbBudgets.PeakThroughLink(n).Mbps);
            o["link"] = J.Obj(("rate", Topology.ShortSpeed(n)), ("reservableMbps", J.N(capacity, 2)), ("reservedNowMbps", J.N(reserved, 4)), ("peakMbps", J.N(peak, 4)),
                ("nowShare", UsbBudgets.Share(reserved, capacity)), ("peakShare", UsbBudgets.Share(peak, capacity)),
                ("unreportedDevices", unknown > 0 ? unknown : null),
                ("state", UsbBudgets.LinkNearlyFull(n) ? "nearly full" : UsbBudgets.CouldExceedWhenStreaming(n) ? "could exceed when streaming" : "fits"),
                ("reservation", n.Kind == "Device" ? ReservationKind(n) : null), ("note", n.Kind == "Device" ? ReservationNote(s, n) : null));
            // A hub's own status pipe reserves a few bits per second; under 1 kb/s isn't worth listing.
            var contributors = (n.Kind == "Hub" ? n.Walk() : [n]).Where(d => d.Kind is "Device" or "Hub" && Math.Max(d.ReservedMbps ?? 0, d.PeakReservedMbps ?? 0) >= 0.001)
                .OrderByDescending(d => Math.Max(d.PeakReservedMbps ?? 0, d.ReservedMbps ?? 0)).ToList();
            if (contributors.Count > 0)
                o["reservations"] = J.Arr(contributors.Select(d => (JsonNode)J.Obj(("path", s.PathOf(d)), ("name", Topology.ShortName(d)), ("link", Topology.ShortSpeed(d)),
                    ("reservedMbps", J.N(d.ReservedMbps, 4)), ("peakMbps", J.N(d.PeakReservedMbps, 4)), ("pipes", J.Some(d.OpenPipes.Where(p => p.Contains("reserves")).Select(p => (JsonNode)p))))));
        }
        else if (n.Kind is "Device" or "Hub") o["link"] = J.Obj(("rate", Topology.ShortSpeed(n)), ("note", "Reserved bandwidth isn't known for this link."));
        if (UsbBudgets.SharedTtUse(n) is (var ttNow, var ttPeak, var ttUnknown, var ports))
            o["sharedTt"] = J.Obj(("type", Topology.TtType(n)), ("portsUsingIt", ports), ("reservableMbps", UsbBudgets.FullSpeedReservableMbps), ("nowMbps", J.N(ttNow, 4)), ("peakMbps", J.N(ttPeak, 4)),
                ("unreportedDevices", ttUnknown > 0 ? ttUnknown : null),
                ("state", UsbBudgets.SharedTtNearlyFull(n) ? "nearly full" : UsbBudgets.SharedTtCouldExceed(n) ? "could exceed" : "fits"));
        else if (n.Kind == "Hub" && n.LinkMbps == 480) o["sharedTt"] = J.Obj(("type", Topology.TtType(n)));
        if (n.Kind is "Device" or "Hub" or "Unavailable")
        {
            var power = J.Obj(("source", n.PowerSource), ("requestMa", n.MaxPowerMa), ("warnings", J.Some(n.PowerWarnings.Select(x => (JsonNode)x))));
            if (n.Kind == "Hub" && n.PowerSource == "Bus powered")
            {
                bool super = UsbBudgets.SuperSpeed(n);
                var children = n.Children.Where(c => c.Kind is "Device" or "Hub").ToList();
                int total = (n.MaxPowerMa ?? 0) + children.Sum(c => UsbBudgets.Demand(c).Known);
                power["upstreamGuaranteeMa"] = super ? 900 : 500;
                power["totalDemandMa"] = total;
                power["unreportedDevices"] = children.Sum(c => UsbBudgets.Demand(c).Unknown) is > 0 and var u ? u : null;
                power["ports"] = J.Arr(children.OrderBy(c => c.Port).Select(c =>
                {
                    var (known, missing) = UsbBudgets.Demand(c);
                    int limit = UsbBudgets.SuperSpeed(c) ? 150 : 100;
                    return (JsonNode)J.Obj(("path", s.PathOf(c)), ("name", Topology.ShortName(c)), ("demandMa", known), ("unreported", missing > 0 ? missing : null), ("portGuaranteeMa", limit), ("over", known > limit ? true : null));
                }));
            }
            else if (n.Kind == "Hub") power["note"] = "Runs on its own supply; its ports' current comes from that supply, which Windows doesn't report.";
            o["power"] = power;
        }
        return o;
    }
}

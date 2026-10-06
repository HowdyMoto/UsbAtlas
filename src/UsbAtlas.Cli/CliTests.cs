using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// Checks for the command line, run by atlascli self-test after the app's own checks. They use the
// sample topology, so they pass on any machine.
internal static class CliTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool Throws(Action action) { try { action(); return false; } catch (CliException) { return true; } }
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        int code = Program.Execute(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }
    private static Session Demo() => new(DemoData.Create(), "demo");

    internal static void Run()
    {
        OptionTests();
        TargetTests();
        ReportTests();
        DiffTests();
        RedactTests();
        DescriptorTests();
        DriverProblemTests();
        FileTests();
        PortMapTests();
        WakeTests();
        WatchNoiseTests();
        TraceTests();
        McpTests();
        CommandTests();
    }

    private static void OptionTests()
    {
        var o = Options.Parse(["show", "H01/05", "--json", "--min=warning", "--for", "90s"]);
        Check(o.Command == "show" && o.Positional is ["H01/05"] && o.Json && o.Get("min") == "warning" && o.Duration("for", TimeSpan.Zero) == TimeSpan.FromSeconds(90), "Options parse commands, positionals, flags and both value forms.");
        Check(Options.ParseDuration("5m") == TimeSpan.FromMinutes(5) && Options.ParseDuration("2h") == TimeSpan.FromHours(2) && Options.ParseDuration("1d") == TimeSpan.FromDays(1) && Options.ParseDuration("30") == TimeSpan.FromSeconds(30), "Durations take s, m, h and d, and plain seconds.");
        Check(Options.ParseDuration("-1s") == null && Options.ParseDuration("soon") == null && Options.ParseDuration("1e308d") == null, "Bad durations are refused.");
        Check(Throws(() => Options.Parse(["issues", "--bogus"])) && Throws(() => Options.Parse(["issues", "--min"])) && Throws(() => Options.Parse(["issues", "--json=yes"])) && Throws(() => Options.Parse(["tree", "--format", "xml"])), "Unknown options, missing values and values for flags are refused.");
    }

    private static void TargetTests()
    {
        var s = Demo();
        var hub = s.Snapshot.Nodes.First(n => n.Name == "Studio desktop hub");
        Check(s.PathOf(hub) == "H01/01" && s.PathOf(hub.Children[1]) == "H01/01/02", "A merged root hub shares its controller's path, so root ports read H01/NN.");
        Check(s.Resolve("h01/01") == hub && s.Resolve("2109:0817") == hub && s.Resolve("Studio desktop") == hub && s.Resolve(hub.Id) == hub, "Targets resolve by path, VID:PID, name and ID.");
        Check(s.Resolve("H01") == s.Snapshot.Controllers[0] && s.Resolve("demo/root") == s.Snapshot.Controllers[0], "The host path and a merged root hub's ID name the host.");
        Check(Throws(() => s.Resolve("Available port")) && Throws(() => s.Resolve("no such thing")) && Throws(() => s.Resolve(" ")), "Ambiguous, unknown and empty targets fail with a message.");
        try { s.Resolve("Available port"); } catch (CliException ex) { Check(ex.Message.Contains("H01/01/03") && ex.Message.Contains("matches"), "An ambiguous target lists the paths it matches."); }
        Check(s.Find("1000 Hz").Select(n => n.Name).OrderBy(x => x).SequenceEqual(["Direct-drive wheel base", "Wireless mouse receiver"]), "Find searches polling rates as the app does.");
        Check(!s.Listed.Any(s.IsMergedRoot), "A merged root hub isn't listed apart from its host.");
    }

    private static void ReportTests()
    {
        var s = Demo();
        var issues = Reports.Issues(s);
        var list = issues["issues"]!.AsArray();
        Check(list.Count > 0 && list.All(i => i!["what"] != null && i["path"] != null && i["severity"] != null), "Every issue has a severity, a path and an explanation.");
        Check(list[0]!["severity"]!.ToString() == "error" && list.Any(i => i!["issue"]!.ToString() == "Insufficient power" && i["path"]!.ToString() == "H01/05/03"), "Errors lead, and the refused drive is placed at its port.");
        Check(list.Any(i => i!["issue"]!.ToString() == PowerSaving.Warning && i["steps"]!.AsArray().Count > 0), "A game controller Windows may suspend is listed with steps.");
        Check(Reports.HealthCode(s) == 2, "Errors exit 2.");
        Check(Reports.Issues(s, Severity.Error)["issues"]!.AsArray().All(i => i!["severity"]!.ToString() == "error") && (int)issues["summary"]!["errors"]! >= 1, "--min leaves out less severe issues but the summary counts them all.");
        Check(TextOut.Render(issues).Contains("ERROR: Insufficient power — H01/05/03"), "Issue text leads each issue with severity, name and path.");

        var tree = Reports.Tree(s, false);
        string text = TextOut.Render(tree);
        Check(text.Contains("H01/01 Studio desktop hub — Plug-in hub · 5 Gb/s") && text.Contains("empty ports 03,04"), "The tree shows each hub with its figures and summarizes empty ports.");
        Check(text.Contains("H01/09 Direct-drive wheel base — Game controller · 12 Mb/s · 1000 Hz"), "Game controllers show their polling rate.");
        Check(TextOut.Render(Reports.Tree(s, true)).Contains("H01/01/03 Available port 3 — Empty port"), "--ports lists every empty port.");

        var show = Reports.Show(s, s.Resolve("H01/05"));
        Check(show["node"]!["hub"]!["transactionTranslators"]!.ToString().Contains("single TT") && show["children"]!.AsArray().Count == 4 && show["siblings"]!.AsArray().Count > 0 && show["upstream"]!.AsArray().Count == 1, "show includes the hub, its children, its siblings and the chain to the host.");
        Check(show["issues"]!.AsArray().Any(i => i!["issue"]!.ToString() == "Over power budget"), "show explains the node's issues.");
        Check(TextOut.Render(show).Contains("transactionTranslators: Share one link · single TT"), "show renders as key: value text.");
        var host = Reports.Show(s, s.Resolve("H01"));
        var pciHost = DemoData.Create(); pciHost.Controllers[0].PciId = "1B21:2142";
        var pcie = Reports.Show(new Session(pciHost, "demo"), pciHost.Controllers[0])["node"]!["controller"]!;
        Check(pcie["pcieLink"]!["generation"]!.GetValue<int>() == 3 && pcie["pcieLink"]!["lanes"]!.GetValue<int>() == 4 && pcie["endpointsInUse"]!.GetValue<int>() > 0, "show reports a controller's PCIe link and endpoints in use.");
        Check(host["node"]!["hub"]!["ports"]!.GetValue<int>() == 9 && host["children"]!.AsArray().Count == 9, "A merged host shows its root ports.");

        var budget = Reports.Budget(s, null);
        var travel = budget["nodes"]!.AsArray().First(n => n!["path"]!.ToString() == "H01/05")!;
        Check(travel["power"]!["upstreamGuaranteeMa"]!.GetValue<int>() == 500 && travel["power"]!["ports"]!.AsArray().Any(p => p!["over"] != null && p["name"]!.ToString() == "LED ring light"), "The budget shows a bus-powered hub's guarantee and the ports over theirs.");
        Check(travel["sharedTt"]!["portsUsingIt"]!.GetValue<int>() >= 1, "The budget shows a single-TT hub's shared bus.");
        Check(Reports.Budget(s, s.Resolve("Studio camera"))["nodes"]!.AsArray()[0]!["reservations"] != null, "A device's budget lists what it reserves.");
        Check(TextOut.Render(Reports.Find(s, "USB-C")).Contains("match"), "Find renders as lines.");
    }

    private static void DiffTests()
    {
        // Demo devices report no IDs; a moved device without a serial is matched by its VID:PID.
        static void Identify(Snapshot s) { var d = s.Nodes.First(n => n.Name == "USB flash drive"); d.VendorId = "0781"; d.ProductId = "5567"; }
        var first = DemoData.Create(); Identify(first);
        var before = new Session(first, "demo");
        var next = DemoData.Create(); Identify(next);
        var root = next.Controllers[0].Children[0];
        root.Children.RemoveAll(n => n.Name == "Mechanical keyboard");
        var travel = root.Children.First(n => n.Name == "Travel hub");
        var drive = travel.Children.First(n => n.Name == "USB flash drive");
        // Moving the flash drive (no serial) from the travel hub's port 1 to the studio hub's port 3.
        travel.Children.Remove(drive);
        travel.Children.Insert(0, new UsbNode { Id = "demo/root/5/1", Name = "Available port 1", Kind = "Empty port", Port = 1, Status = "Empty" });
        var hub = root.Children.First(n => n.Name == "Studio desktop hub");
        hub.Children.RemoveAll(n => n.Port == 3);
        drive.Id = "demo/root/1/3"; drive.Port = 3;
        hub.Children.Insert(2, drive);
        var ssd = hub.Children.First(n => n.Name == "Portable SSD");
        ssd.LinkMbps = 480; ssd.Speed = "High speed · 480 Mb/s"; ssd.SpeedLimited = true; ssd.SuperSpeedPlusCapable = false;
        var after = new Session(next, "demo");
        var diff = Diff.Compare(before, after);
        Check(diff["disconnected"]!.AsArray().Any(n => n!["name"]!.ToString() == "Mechanical keyboard") && diff["connected"]!.AsArray().Count == 0, "A removed device is disconnected, and a moved one isn't connected.");
        Check(diff["moved"]!.AsArray().Any(m => m!["from"]!.ToString() == "H01/05/01" && m["to"]!.ToString() == "H01/01/03"), "A device without a serial that leaves one port and appears at another moved.");
        Check(diff["changed"]!.AsArray().Any(c => c!["name"]!.ToString() == "Portable SSD" && c["changes"]!.AsArray().Any(x => x!["field"]!.ToString() == "link")), "A link speed change is reported.");
        Check(diff["newIssues"]!.AsArray().Any(i => i!["issue"]!.ToString() == "Running at USB 2" && i["path"]!.ToString() == "H01/01/01"), "An issue that appears is new.");
        Check(!diff["newIssues"]!.AsArray().Any(i => i!["name"]!.ToString() == "USB flash drive"), "Issues follow a moved device instead of reappearing.");
        Check(Diff.Text(diff).Contains("> moved         USB flash drive H01/05/01 → H01/01/03") && Diff.Text(diff).Contains("- disconnected  H01/03 Mechanical keyboard"), "Diff text has one line per change.");
        Check(Diff.Empty(Diff.Compare(Demo(), Demo())), "Identical snapshots have no differences.");
        // A firmware update shows as a new revision; a snapshot saved before revisions were recorded doesn't.
        static Session Revised(string revision) { var s = DemoData.Create(); s.Nodes.First(n => n.Name == "Studio desktop hub").DeviceRevision = revision; return new(s, "demo"); }
        var update = Diff.Compare(Revised("1.04"), Revised("1.10"));
        Check(Diff.Text(update).Contains("~ changed       H01/01 Studio desktop hub: revision 1.04 → 1.10"), "A device whose revision changed is reported.");
        Check(Diff.Empty(Diff.Compare(Revised(""), Revised("1.10"))), "A snapshot without revisions doesn't report every device as changed.");
        var revised = Revised("1.10");
        Check(Reports.Show(revised, revised.Resolve("H01/01"))["node"]!["revision"]!.ToString() == "1.10" && revised.Find("rev 1.10").Count == 1, "show and find carry the revision.");
        // A redacted baseline compared with a redacted scan: redaction must be stable and idempotent.
        var serialed = DemoData.Create(); serialed.Nodes.First(n => n.Name == "Studio desktop hub").Serial = "HUB0123456789";
        var again = DemoData.Create(); again.Nodes.First(n => n.Name == "Studio desktop hub").Serial = "HUB0123456789";
        var once = Session.Redact(serialed);
        Check(Diff.Empty(Diff.Compare(new(Session.Redact(once), "file"), new(Session.Redact(again), "live"))), "Redacting a redacted snapshot changes nothing, so a redacted baseline diffs clean.");
    }

    private static void RedactTests()
    {
        var snapshot = DemoData.Create();
        var ssd = snapshot.Nodes.First(n => n.Name == "Portable SSD");
        ssd.Serial = "S4EVNX0R123456"; ssd.InstanceId = @"USB\VID_04E8&PID_61F5\S4EVNX0R123456"; ssd.Notes.Add("Serial S4EVNX0R123456 seen.");
        var keyboard = snapshot.Nodes.First(n => n.Name == "Mechanical keyboard");
        keyboard.Serial = "0000"; keyboard.VendorId = "0000";
        var redacted = Session.Redact(snapshot);
        string json = JsonSerializer.Serialize(redacted, Json.Options);
        Check(!json.Contains("S4EVNX0R123456") && json.Contains("redacted-"), "Redaction removes a serial everywhere it appears.");
        var r = redacted.Nodes.First(n => n.Name == "Portable SSD");
        Check(r.InstanceId.EndsWith(r.Serial) && r.Serial.StartsWith("redacted-"), "The same serial becomes the same hash throughout.");
        var k = redacted.Nodes.First(n => n.Name == "Mechanical keyboard");
        Check(k.Serial == "0000" && k.VendorId == "0000", "Short, repetitive serials aren't identifying and stay, so they don't look unique.");
        string text = Session.RedactText(@"Device USB\VID_05AC&PID_12A8\00008150001E1C403A28401C was configured. Parent USB\ROOT_HUB30\5&1c45f993&0&0 HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002046d_PID&b023&Col01\9&2a0e7f1&0&0000 BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1234abcd&0&A4C1385F2E91_C00000000");
        Check(!text.Contains("00008150001E1C403A28401C") && !text.Contains("A4C1385F2E91") && text.Contains(@"ROOT_HUB30\5&1c45f993&0&0") && text.Contains("00805f9b34fb") && text.Contains(@"9&2a0e7f1&0&0000"),
            "Text redaction hashes serial segments and Bluetooth addresses, and leaves port-based instance IDs and GUIDs alone.");
        Check(Session.RedactText(text) == text, "Text redaction is idempotent.");
    }

    private static void DescriptorTests()
    {
        // A USB 3.2 device, a configuration with a bulk and an interrupt endpoint, and a BOS with a
        // SuperSpeedPlus capability listing one 10 Gb/s sublink speed.
        byte[] device = Convert.FromHexString("12" + "01" + "1003" + "000000" + "09" + "4C05" + "8A0D" + "0001" + "010203" + "01");
        var d = Descriptors.Decode(device, 3, 0x0310);
        Check(d["bcdUSB"]!.ToString() == "3.10" && d["idVendor"]!.ToString() == "054C" && d["idProduct"]!.ToString() == "0D8A", "Device descriptors decode IDs and the USB version.");
        byte[] config = Convert.FromHexString(
            "09022C00010100C032" +   // configuration: 44 bytes, 1 interface, self-powered, 50 units
            "090400000203000000" +   // interface 0: 2 endpoints, HID
            "07058103040004" +       // endpoint 81 IN interrupt, 4 bytes, bInterval 4
            "063000000400" +         // SuperSpeed companion: 4 bytes per interval
            "07050202000200" +       // endpoint 02 OUT bulk, 512 bytes
            "063000000000");
        var walked = Descriptors.Walk(config, 3, 0x0310);
        Check(walked.Count == 6 && walked[0]["bMaxPower"]!.ToString().StartsWith("50 (400 mA)") && walked[0]["bmAttributes"]!.ToString().Contains("self-powered"), "Configurations decode power in 8 mA units at USB 3, and attributes.");
        Check(walked[2]["bEndpointAddress"]!.ToString().Contains("endpoint 1 IN") && walked[2]["bmAttributes"]!.ToString().Contains("interrupt") && walked[2]["bInterval"]!.ToString().Contains("every 1 ms"), "Endpoints decode direction, type and service interval.");
        Check(walked[3]["wBytesPerInterval"]!.GetValue<ushort>() == 4, "SuperSpeed companions decode bytes per interval.");
        Check(Descriptors.Walk([9, 2, 0xFF], 2, 0x0200)[0]["error"] != null, "A bad length keeps the rest as hex.");
        byte[] bos = Convert.FromHexString(
            "050F150001" +           // BOS: 21 bytes, 1 capability
            "10100A00" + "00000000" + "0000" + "0000" + // SuperSpeedPlus: one sublink speed attribute
            "30400A00");             // ID 0, symmetric, Gb/s exponent, SuperSpeedPlus lanes, mantissa 10
        var caps = Descriptors.Walk(bos, 3, 0x0320);
        Check(caps[0]["bNumDeviceCaps"]!.GetValue<byte>() == 1 && caps[1]["speeds"]!.AsArray()[0]!.ToString().Contains("10 Gb/s"), "SuperSpeedPlus capabilities decode lane speeds.");
        // A Gen 2 device lists ID 0 RX and ID 0 TX, both symmetric: bit 6 is symmetry, bit 7 direction.
        byte[] gen2 = Convert.FromHexString("14100A00" + "01000000" + "0000" + "0000" + "30400A00" + "B0400A00");
        var sublinks = Descriptors.Decode(gen2, 3, 0x0320)["speeds"]!.AsArray().Select(x => x!.ToString()).ToList();
        Check(sublinks.Count == 2 && sublinks.All(x => x.Contains("symmetric") && !x.Contains("asymmetric")) && sublinks[1].Contains("TX") && sublinks[0].Contains("SuperSpeedPlus protocol"), "A TX sublink is symmetric, not asymmetric.");
        Check(Descriptors.Decode(Convert.FromHexString("12012003000000094C058A0D000101020301"), 3, 0x0320)["bMaxPacketSize0"]!.ToString().Contains("512 bytes"), "USB 3 bMaxPacketSize0 is an exponent.");
        // A Billboard capability captured from a monitor-style hub, and a Billboard Ex VDO.
        var billboard = Descriptors.Decode(Convert.FromHexString("30100D050100000003" + new string('0', 62) + "0102000001FF0006"), 2, 0x0201);
        Check(billboard["alternateModes"]!.AsArray()[0]!["wSVID"]!.ToString() == "0xFF01 (DisplayPort)" && billboard["alternateModes"]!.AsArray()[0]!["state"]!.ToString() == "entered"
            && billboard["bAdditionalFailureInfo"]!.ToString() == "0x00" && billboard["iAdditionalInfoURL"]!.GetValue<byte>() == 5, "Billboard capabilities decode their modes and failure info.");
        Check(Descriptors.Decode([8, 0x10, 0x0F, 1, 0x45, 0x0C, 0, 0], 2, 0x0201)["dwAlternateModeVdo"]!.ToString() == "0x00000C45", "Billboard Ex decodes its VDO.");
        Check(Descriptors.Walk([0x09, 0x29, 0x04], 2, 0x0200)[0]["error"] != null && Descriptors.Decode([0x05, 0x29, 0x04, 0x00, 0x00], 2, 0x0200)["hex"] != null, "Truncated descriptors don't read past their bytes.");
        Check(Descriptors.ServiceMs(1, 1, 1) == 1 && Descriptors.ServiceMs(4, 1, 1) == 8 && Descriptors.ServiceMs(4, 2, 3) == 1, "Isochronous full-speed endpoints count 2^(bInterval-1) frames.");
    }

    private static void DriverProblemTests()
    {
        var device = new UsbNode { Kind = "Device", Name = "Camera", InstanceId = @"USB\VID_1234&PID_5678\1", DriverProblems = [new() { InstanceId = @"USB\VID_1234&PID_5678\1", Code = 43, Meaning = Drivers.Meaning(43) }] };
        var issue = IssueRules.For(device).Single(i => i.Text == Explanations.DriverProblem);
        var e = Explanations.For(device, issue.Text, [device]);
        Check(issue.Severity == Severity.Error && e.What.Contains("Code 43") && e.Steps!.Count >= 3, "A problem code is an error, explained with steps.");
        device.DriverProblems = [new() { InstanceId = @"USB\VID_1234&PID_5678&MI_02\2", Name = "Camera audio", Code = 22, Meaning = Drivers.Meaning(22) }];
        Check(IssueRules.For(device).Single(i => i.Text == Explanations.DriverProblem).Severity == Severity.Note && Explanations.For(device, Explanations.DriverProblem, [device]).What.Contains("part of this device (Camera audio)"), "A disabled function is a note that names the function.");
        Check(Drivers.Meaning(28).Contains("not installed") && Drivers.Meaning(999).Contains("999"), "Problem codes have Device Manager's meanings.");
        // A merged host card shows its root hub's issues and explains them against the host.
        var root = new UsbNode { Kind = "Root hub", InstanceId = @"USB\ROOT_HUB30\5&1", DriverProblems = [new() { InstanceId = @"USB\ROOT_HUB30\5&1", Code = 43, Meaning = Drivers.Meaning(43) }] };
        var controller = new UsbNode { Kind = "Controller", Children = [root] };
        Check(Explanations.For(controller, Explanations.DriverProblem, [controller]).What.Contains("Code 43 on this host"), "A host explains its merged root hub's problem.");
        Check(Explanations.For(new UsbNode { Kind = "Device" }, Explanations.DriverProblem, []).What.Length > 0, "A node without problems still gets an explanation, not a crash.");
        // Problems the scan can't place are reported for the whole scan.
        var devices = new Dictionary<string, UsbScanner.DevNode>(StringComparer.OrdinalIgnoreCase)
        {
            [@"USB\VID_0000&PID_0002\5&2&0&3"] = new(@"USB\ROOT_HUB30\4&1", "", [], "Unknown USB Device (Device Descriptor Request Failed)", "", 43),
            [@"PCI\VEN_1022&DEV_15B6\4&1"] = new("", "USBXHCI", [], "USB xHCI Compliant Host Controller", "", 10),
            [@"PCI\VEN_10DE&DEV_1234\4&2"] = new("", "nvlddmkm", [], "Display adapter", "", 43),
        };
        // Drivers are read from the Windows registry.
        if (OperatingSystem.IsWindows())
        {
            var snap = new Snapshot();
            Drivers.Apply(snap, devices);
            Check(snap.Diagnostics.Count == 2 && snap.Diagnostics.Any(d => d.Contains("Code 43") && d.Contains("Descriptor Request Failed")) && snap.Diagnostics.Any(d => d.Contains("Code 10")),
                "Unplaced USB problems become scan diagnostics, and other hardware's don't.");
        }
    }

    private static void FileTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "atlascli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var demo = DemoData.Create();
            string camel = Path.Combine(folder, "camel.json"), pascal = Path.Combine(folder, "pascal.json"), bad = Path.Combine(folder, "bad.json");
            File.WriteAllText(camel, JsonSerializer.Serialize(demo, Json.Options));
            File.WriteAllText(pascal, JsonSerializer.Serialize(demo, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(bad, "{ not json");
            foreach (var file in new[] { camel, pascal })
            {
                var loaded = new Session(Session.LoadFile(file), "file");
                Check(Json.Write(Reports.Issues(loaded)["issues"]) == Json.Write(Reports.Issues(Demo())["issues"]), "A saved snapshot, from the CLI or the app, reports the same issues.");
            }
            Check(Throws(() => Session.LoadFile(bad)) && Throws(() => Session.LoadFile(Path.Combine(folder, "missing.json"))), "Bad and missing files fail with a message.");
            string nulls = Path.Combine(folder, "nulls.json");
            File.WriteAllText(nulls, """{"controllers":[{"kind":"Controller","name":null,"children":null,"notes":null},null]}""");
            Check(Run("tree", "--input", nulls).Code == 0 && Run("issues", "--input", nulls).Code == 0, "Nulls in a file stand in as empty values instead of crashing.");
            File.WriteAllText(nulls, """{"controllers":null}""");
            Check(Run("tree", "--input", nulls).Code == 3, "A file with no controllers fails with a message.");
            var (code, output, _) = Run("diff", camel, pascal);
            Check(code == 0 && output.Contains("No changes."), "diff compares two files.");
            Check(Run("issues", "--input", camel, "--json").Code == 2, "issues reads --input.");
        }
        finally { Directory.Delete(folder, true); }
    }

    // The sample host with its hub wired in, which leaves its port map without findings.
    private static Snapshot Board()
    {
        var snapshot = DemoData.Create();
        var hub = snapshot.Nodes.First(n => n.Name == "Studio desktop hub");
        hub.PortIsUserConnectable = false; hub.Notes.RemoveAll(n => n.StartsWith("Port map evidence"));
        PortMap.Analyze(snapshot);
        return snapshot;
    }

    private static void PortMapTests()
    {
        var demo = Demo();
        var ports = PortMapFile.Export(demo, false)["controllers"]![0]!["ports"]!.AsArray();
        Check(ports.Count == 9 && ports[5]!["sharesSocketWith"]!.ToString() == "H01/08" && ports[7]!["sharesSocketWith"]!.ToString() == "H01/06" && ports[0]!["sharesSocketWith"]!.ToString() == "none"
            && ports[5]!["usbC"]!.GetValue<bool>() && ports[5]!["supports"]!.ToString() == "USB 3" && ports[1]!["supports"]!.ToString() == "USB 2" && ports.All(p => p!["expect"] == null),
            "The map lists each root port with what it speaks and its other half, and expects nothing that is only plugged in.");
        var fixture = PortMapFile.Export(demo, true);
        Check(PortMapFile.PortCount(fixture) == 17 && fixture["controllers"]![0]!["ports"]![0]!["expect"]!["ports"]!.AsArray().Count == 4 && fixture["controllers"]![0]!["ports"]![1]!["expect"]!["minLinkMbps"]!.GetValue<double>() == 480,
            "--devices expects what is plugged in, with hubs' ports and each link rate.");
        var noted = PortMapFile.Check(demo, null, null);
        Check(noted["result"]!.ToString() == "fail" && noted["mismatches"]!.AsArray().Count == 0 && noted["findings"]!.AsArray().Single()!["path"]!.ToString() == "H01/01" && noted["findings"]![0]!["evidence"]!.AsArray().Count == 1
            && PortMapFile.Text(noted).Contains("FAIL · 1 port map finding · 1 controller, 9 ports checked") && PortMapFile.Text(noted).Contains("! H01/01 Studio desktop hub: No USB 2 half reported"),
            "Without a map, check fails on the port map's own findings and shows their evidence.");

        // A board with nothing to find passes against its own map, wired-in hub and all.
        var board = new Session(Board(), "demo");
        var map = PortMapFile.Export(board, false);
        Check(map["controllers"]![0]!["ports"]![0]!["expect"]!["kind"]!.ToString() == "hub" && PortMapFile.PortCount(map) == 13, "What is wired in is part of the map, with a built-in hub's ports.");
        var pass = PortMapFile.Check(board, map, "board.json");
        Check(pass["result"]!.ToString() == "pass" && PortMapFile.Text(pass).Contains("PASS · matches board.json and the port map is consistent · 1 controller, 13 ports and 1 expected device checked"), "A machine matches its own map.");
        Check(PortMapFile.Text(PortMapFile.Check(board, null, null)).Contains("PASS · the port map is consistent · 1 controller, 9 ports checked"), "Without a map, a consistent port map passes.");

        // Another unit: its hub links slower, a socket's halves are unpaired and one isn't USB-C, a port is gone and another added.
        var other = Board();
        var root = other.Controllers[0].Children[0];
        var hub = root.Children.First(n => n.Port == 1);
        hub.LinkMbps = 480; hub.Speed = "High speed · 480 Mb/s"; hub.Children.RemoveAll(n => n.Port == 4);
        root.Children.First(n => n.Port == 6).CompanionId = ""; root.Children.First(n => n.Port == 8).CompanionId = "";
        root.Children.First(n => n.Port == 6).PortConnectorIsTypeC = false;
        root.Children.RemoveAll(n => n.Port == 9);
        root.Children.Add(new UsbNode { Id = "demo/root/10", Port = 10, Kind = "Empty port", Protocols = "USB 2.0", PortIsUserConnectable = true });
        root.Children.First(n => n.Port == 5).ScanIncomplete = true;
        var fail = PortMapFile.Check(new Session(other, "demo"), map, "board.json");
        string text = PortMapFile.Text(fail);
        Check(fail["result"]!.ToString() == "fail" && fail["mismatches"]!.AsArray().Count == 8, $"Every difference from the map is a mismatch (got {fail["mismatches"]!.AsArray().Count}):\n{text}");
        Check(text.Contains("✗ H01/01: link rate — expected at least 5 Gb/s, found 480 Mb/s") && text.Contains("✗ H01/01/04: port is missing") && text.Contains("✗ H01/06: USB-C — expected yes, found no")
            && text.Contains("✗ H01/06: shares its socket with — expected H01/08, found none") && text.Contains("✗ H01/08: shares its socket with — expected H01/06, found none")
            && text.Contains("✗ H01/09: port is missing") && text.Contains("✗ H01/10: port isn't in the map") && text.Contains("✗ H01/05 “Travel hub”: scan — expected complete, found incomplete"), "Each mismatch names the port, what differs, and both values:\n" + text);
        // Deleting a field from the map stops checking it, and a wired-in device that is gone is a mismatch.
        var edited = (JsonObject)map.DeepClone();
        var first = edited["controllers"]![0]!["ports"]![0]!.AsObject();
        first["expect"]!.AsObject().Remove("minLinkMbps"); first["expect"]!.AsObject().Remove("ports");
        edited["controllers"]![0]!["ports"]![5]!.AsObject().Remove("usbC");
        var fewer = PortMapFile.Text(PortMapFile.Check(new Session(other, "demo"), edited, "board.json"));
        Check(!fewer.Contains("link rate") && !fewer.Contains("H01/01/04") && !fewer.Contains("USB-C") && fewer.Contains("H01/06"), "A field left out of the map isn't checked.");
        // A controller whose root hub isn't drawn as part of it still matches: the map names ports by number.
        var apart = Board(); apart.Controllers[0].Children.Add(new UsbNode { Id = "demo/second", Kind = "Root hub" });
        Check(PortMapFile.Check(new Session(apart, "demo"), map, "board.json")["result"]!.ToString() == "pass", "The map doesn't depend on how a host is drawn.");
        hub.Kind = "Empty port";
        Check(PortMapFile.Text(PortMapFile.Check(new Session(other, "demo"), map, "board.json")).Contains("✗ H01/01: hub — expected Studio desktop hub 2109:0817, found nothing connected"), "A wired-in hub that is gone is a mismatch.");

        // Controllers are matched by PCI identity, and one that only moved is a note.
        var pci = Board(); pci.Controllers[0].PciId = "8086:A36D"; pci.Controllers[0].PciSubsystem = "17AA:1234"; pci.Controllers[0].PciAddress = "00:14.0";
        var pciMap = PortMapFile.Export(new Session(pci, "demo"), false);
        pci.Controllers[0].PciAddress = "00:15.0";
        var moved = PortMapFile.Check(new Session(pci, "demo"), pciMap, "board.json");
        Check(moved["result"]!.ToString() == "pass" && moved["notes"]!.AsArray().Single()!.ToString().Contains("00:15.0 here and 00:14.0 in the map"), "A controller at another PCI address still matches, with a note.");
        pci.Controllers[0].PciId = "1B21:2142";
        string swapped = PortMapFile.Text(PortMapFile.Check(new Session(pci, "demo"), pciMap, "board.json"));
        Check(swapped.Contains("controller is missing — expected PCI 8086:A36D at 00:14.0") && swapped.Contains("controller isn't in the map — found PCI 1B21:2142 at 00:15.0"), "A different controller chip is a missing controller and an unlisted one.");
        Check(Reports.Figures(pci.Controllers[0]) == "PCI 1B21:2142 at 00:15.0" && Reports.Show(new Session(pci, "demo"), pci.Controllers[0])["node"]!["controller"]!["vendor"]!.ToString() == "ASMedia", "A host controller shows its PCI identity.");

        string folder = Path.Combine(Path.GetTempPath(), "atlascli-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string snapshot = Path.Combine(folder, "unit.json"), file = Path.Combine(folder, "board.json");
            File.WriteAllText(snapshot, JsonSerializer.Serialize(Board(), Json.Options));
            Check(Run("map", "--input", snapshot, "--out", file).Out.Contains("1 controllers and 13 ports") && Run("check", file, "--input", snapshot).Code == 0, "map saves a file that check passes against.");
            Check(Run("check", file, "--demo").Code == 1 && Run("check", "--demo", "--json").Out.Contains("\"result\": \"fail\"") && Run("check", "--input", snapshot).Code == 0, "check exits 1 on a mismatch or a finding, and 0 when there are none.");
            Check(Run("check", snapshot).Code == 3 && Run("check", snapshot).Err.Contains("is a snapshot, not a port map") && Run("check", Path.Combine(folder, "none.json")).Code == 3 && Run("check", file, file).Code == 3, "check refuses a snapshot, a missing file and two files.");
            Check(JsonNode.Parse(Run("map", "--demo").Out)!["kind"]!.ToString() == PortMapFile.Kind, "map prints the map as JSON.");
            // A snapshot saved without findings is still checked by today's rules when it is loaded.
            var stale = DemoData.Create(); foreach (var n in stale.Nodes) n.PortMapWarnings.Clear();
            File.WriteAllText(snapshot, JsonSerializer.Serialize(stale, Json.Options));
            Check(Run("check", "--input", snapshot).Code == 1 && Run("issues", "--input", snapshot).Out.Contains("NOTE: No USB 2 half reported — H01/01"), "A loaded snapshot's port map is checked again.");
        }
        finally { Directory.Delete(folder, true); }

        var plus = Demo();
        var enclosure = plus.Snapshot.Nodes.First(n => n.Name == "NVMe SSD enclosure");
        enclosure.Raw = new() { SuperSpeedPlus = "30400A0001000000" + "30400A0001000000" }; enclosure.PortIsDebugCapable = true;
        Check(Descriptors.Report(plus, enclosure)["superSpeedPlusLink"]!["rx"]!.ToString() == "10 Gb/s per lane × 2 lanes, SuperSpeedPlus protocol", "raw decodes the lane speed and lane count Windows reports for a SuperSpeedPlus link.");
        Check(Reports.Show(plus, enclosure)["node"]!["socket"]!["debugCapable"]!.GetValue<bool>(), "show says whether a port is debug capable.");
    }

    private static void WakeTests()
    {
        var before = Demo();
        var next = DemoData.Create();
        var root = next.Controllers[0].Children[0];
        root.Children.RemoveAll(n => n.Name == "Mechanical keyboard");
        var ssd = next.Nodes.First(n => n.Name == "Portable SSD");
        ssd.LinkMbps = 480; ssd.Speed = "High speed · 480 Mb/s";
        var woke = Watch.AfterWaking(before, new Session(next, "demo"), new DateTime(2026, 1, 1, 8, 0, 0));
        string text = Watch.Text(woke);
        Check(woke["notBack"]!.AsArray().Single()!["name"]!.ToString() == "Mechanical keyboard" && woke["slower"]!.AsArray().Single()!["before"]!.ToString() == "5 Gb/s" && woke["back"]!.GetValue<int>() == 10, "After a wake, what didn't return and what returned slower are listed.");
        Check(text.Contains("08:00:00.000 ! not back after waking: H01/03 Mechanical keyboard") && text.Contains("! slower after waking: H01/01/01 Portable SSD 5 Gb/s → 480 Mb/s"), "Each is one line.");
        Check(Watch.Text(Watch.AfterWaking(before, Demo(), DateTime.Now)).Contains("✓ after waking, all 11 devices and hubs are back at the link rates they had"), "A clean wake says so.");
        Check(Watch.Text(J.Obj(("time", "08:01:00.000"), ("event", "summary"), ("watchedSeconds", 60), ("rescans", 1), ("changes", 1), ("sleeps", 1), ("notBackAfterWaking", 1), ("slowerAfterWaking", 0), ("unstable", new JsonArray()),
            ("issues", J.Obj(("errors", 0), ("warnings", 0), ("notes", 0))))).Contains("slept once: 1 not back after waking, 0 back slower"), "The summary totals what sleeping cost.");
        Check(Watch.Text(J.Obj(("time", "08:00:00.000"), ("event", "sleep"))).Contains("going to sleep") && Watch.Text(J.Obj(("time", "08:00:09.000"), ("event", "wake"), ("checkingIn", "8 s"))).Contains("checking what came back in 8 s"), "Sleep and wake are reported as they happen.");
    }

    // What a long watch showed: devices caught while Windows was still setting them up, and issues "resolved" by
    // unplugging, then announced again when their device came back.
    // Hub driver events as TDH decodes them, placed on the sample topology by a rundown like the one USBHUB3
    // sends when a session starts, with its handles and the controller's PCI address.
    private static void TraceTests()
    {
        static EtwEvent Event(int id, int level, string name, params (string Key, object Value)[] fields) => new(new DateTime(2026, 10, 6, 9, 30, 0), UsbTrace.Hub3, id, level, name, fields.ToDictionary(f => f.Key, f => f.Value));
        const ulong Root = 0x1000, Hub = 0x2000, Ssd = 0x3000, Camera = 0x4000;
        var snapshot = DemoData.Create(); snapshot.Controllers[0].PciAddress = "02:00.0";
        var s = new Session(snapshot, "demo");
        var trace = new UsbTrace(() => s);
        (string, object)[] Pci = [("fid_PciBus", 2UL), ("fid_PciDevice", 0UL), ("fid_PciFunction", 0UL)];
        (string, object)[] Device(ulong handle, ulong hub, params ulong[] path) => [("fid_HubDevice", hub), ("fid_UsbDevice", handle), ("fid_PortNumber", path[^1]), ("fid_PortPathDepth", (ulong)path.Length),
            ("fid_PortPath", new List<object>(path.Concat(Enumerable.Repeat(0UL, 6 - path.Length)).Cast<object>())), .. Pci];
        Check(trace.Interpret(Event(8, 4, "USB 3.0 Port", [("fid_UsbDevice", Root), ("fid_PortNumber", 1UL), .. Pci]), false) == null, "Rundown events place things; they aren't news.");
        trace.Interpret(Event(6, 4, "USB Device Information", Device(Hub, Root, 1)), false);
        trace.Interpret(Event(6, 4, "USB Device Information", Device(Ssd, Hub, 1, 1)), false);
        // A camera that connects while recording: placed by its new-device event, though the scan doesn't know it.
        var arrived = trace.Interpret(Event(43, 4, "New USB Device Information", Device(Camera, Hub, 1, 3)), true)!;
        Check(arrived["path"]!.ToString() == "H01/01/03" && arrived["severity"]!.ToString() == "info", "A new device is placed by its port path.");

        // A hub's port status: a failed USB 3 link on the studio hub's port 2, where the camera is.
        var failed = trace.Interpret(Event(101, 4, "Port Status for 3.0 Port", ("fid_UsbDevice", Hub), ("fid_PortNumber", 2UL), ("fid_PortStatus", 0x2C1UL), ("fid_PortChange", 0x40UL)), false)!;
        Check(failed["path"]!.ToString() == "H01/01/02" && failed["name"]!.ToString() == "Studio camera" && failed["severity"]!.ToString() == "error" && failed["what"]!.ToString().Contains("SS.Inactive"),
            "A USB 3 link that drops to SS.Inactive is an error on the hub's port, named after what's plugged in.");
        Check(UsbTrace.PortStatus(true, 0x2A1, 0x80).What!.Contains("config error") && UsbTrace.PortStatus(true, 0x203, 0x20).Count == "warm resets" && UsbTrace.PortStatus(true, 0x209, 0x08).Count == "overcurrent events",
            "Config errors, warm resets and overcurrent decode from the change bits.");
        Check(UsbTrace.PortStatus(true, 0x203, 0x40).Severity == "info" && UsbTrace.PortStatus(true, 0x263, 0x40).What == "Link state changed to U3 (suspended).", "Suspending a link is routine.");
        Check(UsbTrace.PortStatus(false, 0x503, 0x04).What == "Resumed from suspend." && UsbTrace.PortStatus(false, 0x501, 0x02).Count == "ports disabled" && UsbTrace.PortStatus(false, 0x100, 0x01).What == "Disconnected.",
            "USB 2 ports decode resume, a port the hub disabled, and disconnects.");
        Check(trace.Interpret(Event(100, 4, "Port Status for 2.0 Port", ("fid_UsbDevice", Hub), ("fid_PortNumber", 1UL), ("fid_PortStatus", 0x503UL), ("fid_PortChange", 0x04UL)), false) == null,
            "Routine resumes are left out unless verbose.");
        // A device's own events: the SSD came up on the USB 2 bus, and Windows retried setting it up.
        var slow = trace.Interpret(Event(173, 2, "SuperSpeed Device is Connected on the 2.0 Bus", ("fid_UsbDevice", Ssd)), false)!;
        Check(slow["path"]!.ToString() == "H01/01/01" && slow["what"]!.ToString().Contains("USB 2 bus") && slow["source"]!.ToString() == "USBHUB3 173", "A SuperSpeed device on the USB 2 bus is placed at the device.");
        trace.Interpret(Event(62, 2, "Retry Enumeration", ("fid_HubDevice", Hub), ("fid_PortNumber", 4UL)), false);
        trace.Interpret(Event(62, 2, "Retry Enumeration", ("fid_HubDevice", Hub), ("fid_PortNumber", 4UL)), false);
        var unknown = trace.Interpret(Event(999, 3, "Something New Went Wrong", ("fid_UsbDevice", 0x9999UL)), false)!;
        Check(unknown["what"]!.ToString() == "Something New Went Wrong." && unknown["path"] == null, "An event USB Atlas doesn't know is told as Windows names it, unplaced when its handle is unknown.");
        Check(UsbTrace.Describe(Event(74, 2, "Validation Failure of Configuration Descriptor")).What == "Windows rejected a malformed descriptor (“Validation Failure of Configuration Descriptor”).", "Rejected descriptors name the descriptor.");

        // As captured on real hardware: setting up a device on the travel hub's port 2 failed once and was retried.
        // The failures name a handle no event has placed yet, and the retry names none.
        const ulong Travel = 0x5000, Fresh = 0x6000, Light = 0x7000;
        trace.Interpret(Event(6, 4, "USB Device Information", Device(Travel, Root, 5)), false);
        trace.Interpret(Event(60, 4, "Start of USB Device Enumeration", ("fid_UsbDevice", Travel), ("fid_PortNumber", 2UL)), false);
        var request = trace.Interpret(Event(162, 2, "Request for Configuration Descriptor Failed", ("fid_UsbDevice", Fresh), ("fid_PortNumber", 0UL)), false)!;
        var retry = trace.Interpret(Event(62, 2, "Retry Enumeration", ("fid_UsbDevice", 0UL)), false)!;
        Check(request["path"]!.ToString() == "H01/05/02" && retry["path"]!.ToString() == "H01/05/02" && request["name"]!.ToString() == "LED ring light", "Failures while a device is being set up are placed at the port being set up.");
        trace.Interpret(Event(61, 4, "Completion of USB Device Enumeration", ("fid_HubDevice", Travel), ("fid_UsbDevice", Light), ("fid_PortNumber", 2UL)), false);
        Check(trace.Interpret(Event(132, 2, "Device Control Transfer Error", ("fid_UsbDevice", Light), ("fid_PortNumber", 0UL)), false)!["path"]!.ToString() == "H01/05/02"
            && trace.Interpret(Event(132, 2, "Device Control Transfer Error", ("fid_UsbDevice", 0x8000UL)), false)!["path"] == null, "Once set up, the device is placed by its handle, and nothing else is guessed.");
        // The hub driver logs a connection's status twice; the second line says nothing new.
        (string, object)[] Connect = [("fid_UsbDevice", Travel), ("fid_PortNumber", 4UL), ("fid_PortStatus", 0x101UL), ("fid_PortChange", 0x01UL)];
        Check(trace.Interpret(Event(100, 4, "Port Status for 2.0 Port", Connect), false) != null && trace.Interpret(Event(100, 4, "Port Status for 2.0 Port", Connect), false) == null, "A repeated port status isn't told twice.");
        // A failed port change names no handle, only the device's VID:PID.
        var change = trace.Interpret(Event(123, 2, "Failure during Port Change Request", ("fid_PortNumber", 5UL), ("fid_idVendor", 0x05E3UL), ("fid_idProduct", 0x0610UL)), false)!;
        Check(change["path"]!.ToString() == "H01/05" && change["name"]!.ToString() == "Travel hub", "An event naming only a VID:PID is placed at the one device with it.");
        // Handles are pointers the driver reuses: once the travel hub's port disconnects, an old handle that comes
        // back for a new device being set up at the studio hub belongs there, and the travel hub keeps its name.
        trace.Interpret(Event(100, 4, "Port Status for 2.0 Port", ("fid_UsbDevice", Root), ("fid_PortNumber", 5UL), ("fid_PortStatus", 0x100UL), ("fid_PortChange", 0x01UL)), false);
        trace.Interpret(Event(60, 4, "Start of USB Device Enumeration", ("fid_UsbDevice", Hub), ("fid_PortNumber", 4UL)), false);
        var reused = trace.Interpret(Event(132, 2, "Device Control Transfer Error", ("fid_UsbDevice", Light)), false)!;
        Check(reused["path"]!.ToString() == "H01/01/04", "A reused handle is placed where the device is being set up, not where it was.");
        s.Snapshot.Controllers[0].Children[0].Children.RemoveAll(n => n.Name == "Travel hub");
        var unplugged = trace.Interpret(Event(100, 4, "Port Status for 2.0 Port", ("fid_UsbDevice", Root), ("fid_PortNumber", 5UL), ("fid_PortStatus", 0x101UL), ("fid_PortChange", 0x01UL)), false)!;
        Check(unplugged["name"]!.ToString() == "Travel hub" && unplugged["what"]!.ToString() == "Connected.", "A port keeps the name of what was last there.");
        Check(UsbTrace.Singular("enumeration retries") == "enumeration retry" && UsbTrace.Singular("transfer errors") == "transfer error" && UsbTrace.Singular("SuperSpeed on USB 2") == "SuperSpeed on USB 2", "Counts of one read in the singular.");

        var summary = trace.Summary(DateTime.Now, s);
        var ports = summary["ports"]!.AsArray();
        static int Total(JsonNode? p) => p!["counts"]!.AsObject().Sum(c => c.Value!.GetValue<int>());
        Check(ports.Any(p => p!["path"]!.ToString() == "H01/01/04" && p["counts"]!["enumeration retries"]!.GetValue<int>() == 2) && ports.Zip(ports.Skip(1)).All(x => Total(x.First) >= Total(x.Second))
            && ports.Any(p => p!["path"]!.ToString() == "H01/01/02" && p["counts"]!["link failures"]!.GetValue<int>() == 1), "The summary counts each port's events, busiest first.");
        string text = UsbTrace.Text(summary) + UsbTrace.Text(failed);
        Check(text.Contains("H01/01/04: 2 enumeration retries") && text.Contains("error    H01/01/02 Studio camera  The USB 3 link failed and entered SS.Inactive."), "Trace text has a line per event and per port.");
        Check(EtwSession.Value([1, 0, 0, 0], 8) is 1UL && EtwSession.Value([0x41, 0, 0, 0], 1) is "A" && EtwSession.Value([1, 0, 0, 0], 13) is true && EtwSession.Value([0xAB], 14) is "AB", "TDH values decode by their in-type.");
        Check(Run("trace", "--demo").Code == 3 && Options.Parse(["watch", "--trace"]).Has("trace"), "trace refuses sample data, and watch takes --trace.");
    }

    private static void WatchNoiseTests()
    {
        static Session Without(string name) { var s = DemoData.Create(); s.Controllers[0].Children[0].Children.RemoveAll(n => n.Name == name); return new(s, "demo"); }
        var before = Demo(); var unplugged = Without("Travel hub");
        var gone = Diff.Compare(before, unplugged);
        Check(gone["disconnected"]!.AsArray().Any(n => n!["name"]!.ToString() == "Travel hub") && gone["resolvedIssues"]!.AsArray().Count == 0, "Unplugging a device doesn't resolve its issues.");
        var reported = Diff.IssueKeys(before);
        Check(Diff.Compare(before, unplugged, reported)["resolvedIssues"]!.AsArray().Count == 0 && reported.Keys.Any(k => k.EndsWith("|Over power budget")), "A watch keeps an unplugged device's issues as reported.");
        var back = Diff.Compare(unplugged, Demo(), reported);
        Check(back["connected"]!.AsArray().Any(n => n!["name"]!.ToString() == "Travel hub") && back["newIssues"]!.AsArray().Count == 0, "A device that comes back with issues already reported doesn't announce them again.");
        Check(Diff.Compare(unplugged, Demo())["newIssues"]!.AsArray().Any(i => i!["issue"]!.ToString() == "Over power budget"), "Without a watch's memory, a device that connects with an issue reports it.");
        Diff.Compare(Demo(), unplugged, reported);
        var clean = DemoData.Create(); clean.Nodes.First(n => n.Name == "Travel hub").PowerWarnings.Remove("Hub adapter not detected");
        var fixedOnReturn = Diff.Compare(unplugged, new(clean, "demo"), reported);
        Check(fixedOnReturn["resolvedIssues"]!.AsArray().Any(i => i!["issue"]!.ToString() == "Hub adapter not detected" && i["name"]!.ToString() == "Travel hub") && fixedOnReturn["newIssues"]!.AsArray().Count == 0
            && !reported.Keys.Any(k => k.EndsWith("|Hub adapter not detected")), "A device that comes back without a reported issue resolves it.");

        // A scan taken while a device is being set up isn't reported until two scans agree.
        var settling = DemoData.Create(); settling.Nodes.First(n => n.Name == "Studio camera").ReservedMbps = 0;
        var scans = new Queue<Session>([new(settling, "demo"), Demo(), Demo()]);
        int pauses = 0;
        var settled = Watch.ScanUntilSteady(Without("Mechanical keyboard"), () => scans.Dequeue(), () => { pauses++; return true; });
        Check(settled.Snapshot.Nodes.First(n => n.Name == "Studio camera").ReservedMbps == 98.3 && pauses == 2 && scans.Count == 0, "A watch rescans until two scans agree.");
        pauses = 0;
        Watch.ScanUntilSteady(Demo(), Demo, () => { pauses++; return true; });
        Check(pauses == 0, "Nothing changed, so one scan is enough.");
        pauses = 0; int flips = 0;
        Watch.ScanUntilSteady(Without("Mechanical keyboard"), () => flips++ % 2 == 0 ? Demo() : new(settling, "demo"), () => { pauses++; return true; });
        Check(pauses == Watch.SteadyTries, "A device that never settles is reported after a few tries.");

        // Counts read naturally, and a watch without an end says so.
        string Watching(object span, int notes) => Watch.Text(J.Obj(("time", "08:00:00.000"), ("event", "watching"), ("for", span.ToString()), ("devices", 14), ("hubs", 1),
            ("issues", J.Obj(("errors", 0), ("warnings", 2), ("notes", notes))), ("tracksSleep", true)));
        Check(Watching("until stopped", 1).Contains("watching until stopped · 14 devices, 1 hub · 0 errors, 2 warnings, 1 note.") && Watching("90 s", 3).Contains("watching for 90 s ·") && Watching("90 s", 3).Contains("3 notes"),
            "The watching line reads naturally.");
    }

    private static void McpTests()
    {
        var input = new StringReader(string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"usb_issues","arguments":{"min_severity":"error"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"usb_show","arguments":{"target":"H01/05","format":"json"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"usb_show","arguments":{"target":"nothing like this"}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"usb_show","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":7,"method":"bogus"}""",
            "not json",
            """{"jsonrpc":"2.0","id":"eight","method":"ping"}""",
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"usb_baseline","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"usb_diff","arguments":{}}}"""));
        var output = new StringWriter();
        Mcp.Serve(["--demo"], input, output, CancellationToken.None);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Check(replies.Count == 11, $"Every request gets one reply and notifications none (got {replies.Count}).");
        JsonObject Reply(JsonNode id) => replies.First(r => JsonNode.DeepEquals(r["id"], id));
        Check(Reply(1)["result"]!["protocolVersion"]!.ToString() == "2025-06-18" && Reply(1)["result"]!["capabilities"]!["tools"] != null, "initialize agrees a protocol version and offers tools.");
        var tools = Reply(2)["result"]!["tools"]!.AsArray();
        Check(tools.Count == 12 && tools.Any(t => t!["name"]!.ToString() == "usb_trace") && tools.All(t => t!["inputSchema"]!["properties"]!["format"] != null && t["annotations"]!["readOnlyHint"]!.GetValue<bool>()), "Every tool is listed, read-only, with a format option.");
        string Text(JsonObject r) => r["result"]!["content"]![0]!["text"]!.ToString();
        Check(Text(Reply(3)).Contains("ERROR: Insufficient power") && !Text(Reply(3)).Contains("WARNING:") && !Reply(3)["result"]!["isError"]!.GetValue<bool>(), "usb_issues runs the issues command; finding errors isn't a tool error.");
        Check(JsonNode.Parse(Text(Reply(4)))!["node"]!["path"]!.ToString() == "H01/05", "format json returns the same report as JSON.");
        Check(Reply(5)["result"]!["isError"]!.GetValue<bool>() && Text(Reply(5)).Contains("Nothing matches"), "A target that matches nothing is a tool error with the message.");
        Check(Reply(6)["result"]!["isError"]!.GetValue<bool>(), "A missing required argument is a tool error.");
        Check(Reply(7)["error"]!["code"]!.GetValue<int>() == -32601, "Unknown methods are JSON-RPC errors.");
        Check(replies.Any(r => r["id"] == null && r["error"]!["code"]!.GetValue<int>() == -32700), "Unparseable lines get a parse error.");
        Check(Reply("eight")["result"] != null, "String IDs are echoed.");
        // The server outlives a file that makes a command fail.
        string broken = Path.Combine(Path.GetTempPath(), $"usbatlas-mcp-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(broken, """{"controllers":[{"kind":"Controller","children":[{"kind":"Hub","children":null}]}]}""");
        try
        {
            var o2 = new StringWriter();
            Mcp.Serve(["--input", broken], new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"usb_tree","arguments":{}}}""" + "\n" + """{"jsonrpc":"2.0","id":2,"method":"ping"}"""), o2, CancellationToken.None);
            Check(o2.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "The MCP server answers after a command fails.");
        }
        finally { File.Delete(broken); }
        Check(Text(Reply(9)).Contains("Baseline saved") && Text(Reply(10)).Contains("No changes."), "usb_baseline then usb_diff compares with the saved state.");
    }

    private static void CommandTests()
    {
        Check(Run("issues", "--demo").Code == 2 && Run("tree", "--demo").Code == 0 && Run("help").Out.Contains("Exit codes"), "Commands run and issues exits with the health of what it found.");
        Check(Run("bogus").Code == 3 && Run("show", "--demo").Code == 3 && Run("show", "--demo", "nothing").Code == 3 && Run("issues", "--demo", "--min", "loud").Code == 3, "Failures exit 3.");
        Check(Run("show", "--demo", "Studio", "camera").Out.Contains("H01/01/02"), "Several words form one target.");
        var (code, output, _) = Run("scan", "--demo");
        Check(code == 0 && JsonNode.Parse(output)!["controllers"]!.AsArray().Count == 1, "scan writes the snapshot as JSON.");
        Check(Run("raw", "--demo", "H01/01").Out.Contains("no descriptors"), "raw explains when a snapshot has no descriptors.");
        Check(Run("watch", "--demo").Code == 3, "watch refuses sample data.");
        Check(Run("budget", "--demo", "--json").Out.TrimStart().StartsWith('{') && Run("show", "--demo", "H01/05", "--format", "json").Out.TrimStart().StartsWith('{'), "--json and --format json write JSON.");
    }
}

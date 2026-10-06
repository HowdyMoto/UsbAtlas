using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// A board's USB port map as a file, and the check of a machine against one. The map lists each host
// controller's ports as the firmware describes them to Windows: what each speaks, whether it can be
// plugged into, whether it is USB-C or can be the debug port, and which port is the other half of its
// socket, plus what is wired in. Take it from a known-good unit with map, edit it where the design
// differs, and check other units or firmware builds against it. A field left out of the file isn't checked.
internal static class PortMapFile
{
    internal const string Kind = "usb-atlas-port-map";
    private const string None = "none", NotInScan = "not in scan", Missing = "missing", Unlisted = "not in the map";

    private static string Str(JsonNode? n) => n == null ? "" : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString(Json.Compact);
    private static string Supports(UsbNode p) => PortMap.Generation(p) switch { 3 => "USB 3", 2 => "USB 2", _ => p.Protocols == "Not reported" ? "not reported" : p.Protocols };
    private static IEnumerable<UsbNode> RootPorts(UsbNode controller) => controller.Children.Where(r => r.Kind == "Root hub").SelectMany(r => r.Children).Where(p => p.Port > 0).OrderBy(p => p.Port);
    // A port's place in the map: its controller's path, then each port number down to it. Unlike the paths
    // other commands show, it doesn't depend on how a controller's root hub is drawn.
    private static string Place(Session s, UsbNode p) =>
        s.Chain(p) is { Count: > 0 } chain ? s.PathOf(chain[0]) + string.Concat(chain.Skip(1).Where(n => n.Port > 0).Select(n => $"/{n.Port:00}")) : s.PathOf(p);
    // The places of the ports Windows names as sharing this port's socket.
    private static List<string> Companions(Session s, UsbNode p) =>
        PortMap.Companions(p).Select(c => c.Id.Length > 0 && s.ById(c.Id) is UsbNode other ? Place(s, other) : NotInScan).ToList();

    internal static JsonObject Export(Session s, bool devices)
    {
        JsonObject Port(UsbNode p)
        {
            var o = J.Obj(("port", p.Port), ("name", J.S(p.PortLabel)), ("supports", Supports(p)));
            if (p.PortIsUserConnectable is bool user) o["userConnectable"] = user;
            if (p.PortConnectorIsTypeC is bool c) o["usbC"] = c;
            if (p.PortIsDebugCapable is bool debug) o["debugCapable"] = debug;
            var shares = Companions(s, p);
            o["sharesSocketWith"] = shares.Count switch { 0 => None, 1 => shares[0], _ => J.Arr(shares.Select(x => (JsonNode)x)) };
            // What is wired in is part of the board; what is plugged in is expected only when asked.
            if (p.Kind is "Device" or "Hub" && (devices || p.PortIsUserConnectable == false)) o["expect"] = Expect(p);
            return o;
        }
        JsonObject Expect(UsbNode n)
        {
            var o = J.Obj(("kind", n.Kind == "Hub" ? "hub" : "device"), ("vidPid", J.S(Reports.VidPid(n))), ("name", Topology.ShortName(n)), ("minLinkMbps", J.N(n.LinkMbps)));
            if (n.Kind == "Hub") o["ports"] = J.Arr(n.Children.Where(c => c.Port > 0).OrderBy(c => c.Port).Select(c => (JsonNode)Port(c)));
            return o;
        }
        var map = J.Obj(("schemaVersion", Session.SchemaVersion), ("kind", Kind), ("source", s.Source), ("capturedAt", s.Snapshot.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ssK")));
        map["controllers"] = J.Arr(s.Snapshot.Controllers.Select(c => (JsonNode)J.Obj(("path", s.PathOf(c)), ("name", Topology.ShortName(c)),
            ("pci", J.S(c.PciId)), ("subsystem", J.S(c.PciSubsystem)), ("address", J.S(c.PciAddress)), ("ports", J.Arr(RootPorts(c).Select(p => (JsonNode)Port(p)))))));
        return map;
    }

    internal static int PortCount(JsonObject map)
    {
        static int Count(JsonArray? ports) => ports?.OfType<JsonObject>().Sum(p => 1 + Count(p["expect"]?["ports"] as JsonArray)) ?? 0;
        return (map["controllers"] as JsonArray)?.OfType<JsonObject>().Sum(c => Count(c["ports"] as JsonArray)) ?? 0;
    }

    internal static JsonObject Load(string file)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { throw new CliException($"Can't load {file}: {ex.Message}"); }
        if (node is not JsonObject map || map["controllers"] is not JsonArray)
            throw new CliException($"{file} isn't a USB Atlas port map. Make one with atlascli map --out FILE.");
        if (Str(map["kind"]) != Kind)
            throw new CliException($"{file} is a snapshot, not a port map. Make a map from it with atlascli map --input {file} --out FILE.");
        return map;
    }

    // Compares the machine with the map, when there is one, and lists the port map's own contradictions
    // either way. Anything in either list fails the check.
    internal static JsonObject Check(Session s, JsonObject? map, string? file)
    {
        var mismatches = new JsonArray(); var notes = new List<string>();
        int controllers = 0, ports = 0, expectedDevices = 0;
        void Miss(string path, string name, string what, string expected, string found) =>
            mismatches.Add(J.Obj(("path", path), ("name", J.S(name)), ("what", what), ("expected", expected), ("found", found)));
        static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "not reported" };
        static double? Link(UsbNode n) => n.LinkMbps ?? (n.Speed.StartsWith("SuperSpeedPlus", StringComparison.Ordinal) ? 10000 : null);

        if (map == null)
        {
            controllers = s.Snapshot.Controllers.Count;
            ports = s.Snapshot.Controllers.Sum(c => RootPorts(c).Count());
        }
        else
        {
            var expected = map["controllers"]!.AsArray().OfType<JsonObject>().ToList();
            var match = new Dictionary<JsonObject, UsbNode>();
            // The same chip at the same PCI address, then the same chip wherever it sits, then, for a map
            // without PCI identities, the same place in the list.
            void Pair(Func<JsonObject, UsbNode, bool> same)
            {
                foreach (var e in expected.Where(e => !match.ContainsKey(e)))
                    if (s.Snapshot.Controllers.FirstOrDefault(a => !match.ContainsValue(a) && same(e, a)) is UsbNode a) match[e] = a;
            }
            Pair((e, a) => Str(e["pci"]).Length > 0 && Str(e["pci"]) == a.PciId && Str(e["address"]) == a.PciAddress);
            Pair((e, a) => Str(e["pci"]).Length > 0 && Str(e["pci"]) == a.PciId && Str(e["subsystem"]) == a.PciSubsystem);
            Pair((e, a) => Str(e["pci"]).Length == 0 && Str(e["path"]) == s.PathOf(a));
            // The map's places are its own unit's; a port it names is found here through its controller.
            var heads = match.Where(m => Str(m.Key["path"]).Length > 0).GroupBy(m => Str(m.Key["path"])).ToDictionary(g => g.Key, g => s.PathOf(g.First().Value));
            string Here(string path)
            {
                if (path is None or NotInScan) return path;
                var parts = path.Split('/', 2);
                return heads.TryGetValue(parts[0], out var head) ? head + (parts.Length > 1 ? "/" + parts[1] : "") : path + " (its controller isn't here)";
            }

            void Ports(JsonArray? wanted, List<UsbNode> have, string parent)
            {
                if (wanted == null) return;
                var listed = new HashSet<int>();
                foreach (var w in wanted.OfType<JsonObject>())
                {
                    int number = w["port"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : 0;
                    listed.Add(number);
                    string name = Str(w["name"]);
                    if (have.FirstOrDefault(x => x.Port == number) is not UsbNode p) { Miss($"{parent}/{number:00}", name, "port", "present", Missing); continue; }
                    string path = s.PathOf(p);
                    if (name.Length == 0) name = p.PortLabel;
                    ports++;
                    if (p.Kind == "Unavailable" && p.Status == "Query failed") { Miss(path, name, "port", "readable", "Windows couldn't read it"); continue; }
                    if (w["supports"] is JsonNode supports && Str(supports) != Supports(p)) Miss(path, name, "supports", Str(supports), Supports(p));
                    foreach (var (key, what, actual) in new[] { ("userConnectable", "can be plugged into", p.PortIsUserConnectable), ("usbC", "USB-C", p.PortConnectorIsTypeC), ("debugCapable", "debug capable", p.PortIsDebugCapable) })
                        if (w[key] is JsonValue flag && flag.TryGetValue<bool>(out var want) && actual != want) Miss(path, name, what, YesNo(want), YesNo(actual));
                    if (w["sharesSocketWith"] is JsonNode shares)
                    {
                        var want = (shares is JsonArray several ? several.Select(Str) : [Str(shares)]).Where(x => x != None).Select(Here).OrderBy(x => x).ToList();
                        var found = Companions(s, p).OrderBy(x => x).ToList();
                        if (!want.SequenceEqual(found)) Miss(path, name, "shares its socket with", want.Count > 0 ? string.Join(" and ", want) : None, found.Count > 0 ? string.Join(" and ", found) : None);
                    }
                    if (w["expect"] is JsonObject e) Expected(e, p, path, name);
                }
                foreach (var extra in have.Where(x => x.Port > 0 && !listed.Contains(x.Port)).OrderBy(x => x.Port)) Miss(s.PathOf(extra), extra.PortLabel, "port", Unlisted, "present");
            }
            void Expected(JsonObject e, UsbNode p, string path, string name)
            {
                expectedDevices++;
                bool hub = Str(e["kind"]) == "hub";
                string wanted = string.Join(" ", new[] { Str(e["name"]), Str(e["vidPid"]) }.Where(x => x.Length > 0));
                if (wanted.Length == 0) wanted = hub ? "a hub" : "a device";
                if (p.Kind is not ("Device" or "Hub")) { Miss(path, name, hub ? "hub" : "device", wanted, p.Kind == "Empty port" ? "nothing connected" : p.Status); return; }
                string found = $"{Topology.ShortName(p)} {Reports.VidPid(p)}".Trim();
                if (Str(e["vidPid"]) is { Length: > 0 } id && !id.Equals(Reports.VidPid(p), StringComparison.OrdinalIgnoreCase) || e["kind"] != null && hub != (p.Kind == "Hub"))
                {
                    Miss(path, name, hub ? "hub" : "device", wanted, found + (hub != (p.Kind == "Hub") ? $" ({(p.Kind == "Hub" ? "a hub" : "not a hub")})" : ""));
                    return;
                }
                if (e["minLinkMbps"] is JsonValue min && min.TryGetValue<double>(out var least) && !(Link(p) >= least))
                    Miss(path, name, "link rate", "at least " + UsbBudgets.Rate(least), Link(p) is double rate ? UsbBudgets.Rate(rate) : "not reported");
                if (p.Kind == "Hub") Ports(e["ports"] as JsonArray, p.Children, path);
            }

            foreach (var e in expected)
            {
                string identity = string.Join(" at ", new[] { Str(e["pci"]) is { Length: > 0 } pci ? "PCI " + pci : "", Str(e["address"]) }.Where(x => x.Length > 0));
                if (!match.TryGetValue(e, out var a)) { Miss(Str(e["path"]), Str(e["name"]), "controller", identity.Length > 0 ? identity : "present", Missing); continue; }
                controllers++;
                if (Str(e["address"]) is { Length: > 0 } address && address != a.PciAddress)
                    notes.Add($"{s.PathOf(a)} {Topology.ShortName(a)}: its PCI address is {(a.PciAddress.Length > 0 ? a.PciAddress : "not reported")} here and {address} in the map. Matched by its PCI IDs.");
                Ports(e["ports"] as JsonArray, RootPorts(a).ToList(), s.PathOf(a));
            }
            foreach (var a in s.Snapshot.Controllers.Where(a => !match.ContainsValue(a)))
                Miss(s.PathOf(a), Topology.ShortName(a), "controller", Unlisted, Topology.PciText(a) is { Length: > 0 } pci ? pci : "present");
        }
        // Ports behind something the scan couldn't read weren't checked, so the check can't pass.
        foreach (var n in s.Listed.Where(n => n.ScanIncomplete))
            Miss(s.PathOf(n), Topology.ShortName(n), "scan", "complete", "incomplete" + (n.Notes.LastOrDefault(x => x.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Cannot", StringComparison.Ordinal)) is string why ? ": " + why : ""));
        notes.AddRange(s.Snapshot.Diagnostics.Select(d => "Scan: " + d));

        var findings = J.Arr(s.Snapshot.Nodes.SelectMany(n => n.PortMapWarnings.Select(finding => (JsonNode)J.Obj(
            ("path", s.PathOf(n)), ("name", Topology.ShortName(n)), ("issue", finding), ("what", Explanations.For(n, finding, s.Chain(n)).What),
            ("evidence", J.Some(n.Notes.Where(x => x.StartsWith("Port map evidence: ", StringComparison.Ordinal)).Select(x => (JsonNode)x["Port map evidence: ".Length..])))))));

        var report = Reports.Header(s, "check");
        if (file != null) report["map"] = file;
        report["result"] = mismatches.Count == 0 && findings.Count == 0 ? "pass" : "fail";
        report["checked"] = J.Obj(("controllers", controllers), ("ports", ports), ("expectedDevices", map != null ? expectedDevices : null));
        report["mismatches"] = mismatches;
        report["findings"] = findings;
        report["notes"] = J.Some(notes.Select(x => (JsonNode)x));
        return report;
    }

    // The verdict on one line, then one line per mismatch and a short block per finding.
    internal static string Text(JsonObject r)
    {
        static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : word.EndsWith('h') ? "es" : "s")}";
        var sb = new StringBuilder();
        sb.AppendLine($"USB Atlas · {Str(r["source"])} · {Str(r["capturedAt"])}{(r["redacted"] != null ? " · serials redacted" : "")}");
        var mismatches = r["mismatches"]!.AsArray(); var findings = r["findings"]!.AsArray(); var done = r["checked"]!.AsObject();
        string map = Str(r["map"]);
        string scope = $"{Plural(done["controllers"]!.GetValue<int>(), "controller")}, {Plural(done["ports"]!.GetValue<int>(), "port")}"
            + (done["expectedDevices"] is JsonNode devices && devices.GetValue<int>() > 0 ? $" and {Plural(devices.GetValue<int>(), "expected device")}" : "") + " checked";
        if (Str(r["result"]) == "pass")
            sb.AppendLine(map.Length > 0 ? $"PASS · matches {map} and the port map is consistent · {scope}" : $"PASS · the port map is consistent · {scope}");
        else
        {
            var parts = new List<string>();
            if (mismatches.Count > 0) parts.Add(Plural(mismatches.Count, "mismatch") + (map.Length > 0 ? " with " + map : ""));
            if (findings.Count > 0) parts.Add(Plural(findings.Count, "port map finding"));
            sb.AppendLine($"FAIL · {string.Join(", ", parts)} · {scope}");
        }
        foreach (var node in mismatches)
        {
            var m = node!.AsObject();
            string expected = Str(m["expected"]), found = Str(m["found"]);
            sb.AppendLine($"✗ {Str(m["path"])}{(m["name"] is JsonNode name ? $" “{Str(name)}”" : "")}: {Str(m["what"])} " + (found == Missing ? $"is missing{(expected != "present" ? $" — expected {expected}" : "")}"
                : expected == Unlisted ? $"isn't in the map{(found != "present" ? $" — found {found}" : "")}" : $"— expected {expected}, found {found}"));
        }
        foreach (var node in findings)
        {
            var f = node!.AsObject();
            sb.AppendLine($"! {Str(f["path"])} {Str(f["name"])}: {Str(f["issue"])}");
            sb.AppendLine("    " + Str(f["what"]));
            foreach (var evidence in f["evidence"]?.AsArray() ?? []) sb.AppendLine("    " + Str(evidence));
        }
        foreach (var note in r["notes"]?.AsArray() ?? []) sb.AppendLine("note: " + Str(note));
        return sb.ToString();
    }
}

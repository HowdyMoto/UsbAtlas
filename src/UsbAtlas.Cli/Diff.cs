using System.Text;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// What changed between two snapshots: devices connected, disconnected or moved to another port, figures
// and settings that changed, and issues that appeared or went away.
internal static class Diff
{
    // A device with a unique serial is itself wherever it's plugged in; anything else is known by what it
    // is and where. Port errors are known by their port.
    private static string Identity(UsbNode n, Snapshot s) => n.Kind != "Unavailable" && DeviceLabels.FollowsDevice(n, s)
        ? $"{n.Kind}|{n.VendorId}:{n.ProductId}|{n.Serial}" : $"{n.Kind}|{n.VendorId}:{n.ProductId}|@{n.Id}";
    internal static Dictionary<string, UsbNode> Occupants(Session s) => s.Snapshot.Nodes.Where(n => n.Kind is "Device" or "Hub" or "Unavailable")
        .GroupBy(n => Identity(n, s.Snapshot)).ToDictionary(g => g.Key, g => g.First());

    private static readonly (string Field, Func<UsbNode, Session, string> Value)[] Fields =
    [
        ("status", (n, _) => n.Status), ("link", (n, _) => n.Speed), ("usbVersion", (n, _) => n.UsbVersion),
        ("power", (n, _) => Topology.PowerFigure(n).Text), ("powerSource", (n, _) => n.PowerSource),
        ("powerSaving", (n, s) => Topology.PowerSavingText(n, s.Snapshot)), ("deviceType", (n, _) => n.DeviceType),
        ("polling", (n, _) => n.PollIntervalMs is double ms ? UsbBudgets.PollingRate(ms) : ""),
        ("reserved", (n, _) => n.ReservedMbps is double r ? UsbBudgets.Rate(r) : ""),
        ("driver", (n, _) => string.Join(" ", new[] { n.DriverService, n.DriverVersion }.Where(x => x.Length > 0))),
        ("driverProblems", (n, _) => string.Join(", ", n.DriverProblems.Select(p => "Code " + p.Code))),
        ("label", (n, _) => n.UserLabel), ("reconnects", (n, _) => n.QuickReconnects > 0 ? n.QuickReconnects.ToString() : ""),
    ];

    // An issue is known by what it's on and what it says.
    private static string IssueNode(UsbNode n, Session s) => n.Kind is "Device" or "Hub" or "Unavailable" ? Identity(n, s.Snapshot) : "@" + n.Id;
    internal static Dictionary<string, Severity> IssueKeys(Session s) =>
        s.Listed.SelectMany(n => Reports.IssuesOf(s, n).Select(i => (Key: IssueNode(n, s) + "|" + i.Text, i.Severity))).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First().Severity);

    // reported, when given, holds the issues already announced, as a watch keeps them: one isn't new again when
    // its device leaves and comes back with it, and is resolved once its device is back without it.
    internal static JsonObject Compare(Session before, Session after, Dictionary<string, Severity>? reported = null)
    {
        var was = Occupants(before); var now = Occupants(after);
        var gone = was.Where(p => !now.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var came = now.Where(p => !was.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        // Something without a unique serial that left one port as the same kind of thing arrived at another, alone, moved.
        var moved = new List<(UsbNode From, UsbNode To)>();
        foreach (var group in gone.Where(n => n.Kind != "Unavailable" && n.VendorId.Length > 0).GroupBy(n => (n.Kind, n.VendorId, n.ProductId)).Where(g => g.Count() == 1))
            if (came.Where(n => (n.Kind, n.VendorId, n.ProductId) == group.Key).ToList() is [var to] && before.PathOf(group.First()) != after.PathOf(to))
                moved.Add((group.First(), to));
        foreach (var (from, to) in moved) { gone.Remove(from); came.Remove(to); }
        foreach (var (key, n) in was) if (now.TryGetValue(key, out var m) && before.PathOf(n) != after.PathOf(m)) moved.Add((n, m));

        var changed = new JsonArray();
        foreach (var (key, n) in was)
        {
            if (!now.TryGetValue(key, out var m)) m = moved.FirstOrDefault(p => p.From == n).To;
            if (m == null) continue;
            var changes = Fields.Select(f => (f.Field, Before: f.Value(n, before), After: f.Value(m, after))).Where(c => c.Before != c.After).ToList();
            if (changes.Count > 0)
                changed.Add(J.Obj(("path", after.PathOf(m)), ("name", Topology.ShortName(m)), ("changes", J.Arr(changes.Select(c => (JsonNode)J.Obj(("field", c.Field), ("before", c.Before), ("after", c.After)))))));
        }

        // Issues are matched by what they're on, so one that follows a moved device isn't counted twice.
        var movedFrom = moved.ToDictionary(p => p.To, p => p.From);
        string KeyBefore(UsbNode n) => IssueNode(n, before);
        string KeyAfter(UsbNode n) => movedFrom.TryGetValue(n, out var from) ? KeyBefore(from) : IssueNode(n, after);
        var issuesBefore = before.Listed.SelectMany(n => Reports.IssuesOf(before, n).Select(i => (Key: KeyBefore(n) + "|" + i.Text, i.Severity, i.Text, Node: n))).ToList();
        var issuesAfter = after.Listed.SelectMany(n => Reports.IssuesOf(after, n).Select(i => (Key: KeyAfter(n) + "|" + i.Text, i.Severity, i.Text, Node: n))).ToList();
        var beforeKeys = issuesBefore.Select(i => i.Key).ToHashSet(); var afterKeys = issuesAfter.Select(i => i.Key).ToHashSet();
        JsonNode Issue(Session s, (string Key, Severity Severity, string Text, UsbNode Node) i) =>
            J.Obj(("severity", i.Severity.ToString().ToLowerInvariant()), ("issue", i.Text), ("path", s.PathOf(i.Node)), ("name", Topology.ShortName(i.Node)));

        var report = J.Obj(("schemaVersion", Session.SchemaVersion), ("report", "diff"),
            ("before", J.Obj(("source", before.Source), ("capturedAt", before.Snapshot.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ssK")))),
            ("after", J.Obj(("source", after.Source), ("capturedAt", after.Snapshot.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ssK")))));
        JsonObject Placed(Session s, UsbNode n) { var o = Reports.Ref(s, n); o["figures"] = J.S(Reports.Figures(n)); return o; }
        report["connected"] = J.Arr(came.Select(n => (JsonNode)Placed(after, n)));
        report["disconnected"] = J.Arr(gone.Select(n => (JsonNode)Placed(before, n)));
        report["moved"] = J.Arr(moved.Select(p => (JsonNode)J.Obj(("name", Topology.ShortName(p.To)), ("vidPid", J.S(Reports.VidPid(p.To))), ("from", before.PathOf(p.From)), ("to", after.PathOf(p.To)))));
        report["changed"] = changed;
        // A device that leaves takes its issues with it: that's a disconnect, not a fix.
        var afterIds = after.Snapshot.Nodes.Select(m => m.Id).ToHashSet();
        bool Left(UsbNode n) => gone.Contains(n) || n.Kind is not ("Device" or "Hub" or "Unavailable") && !afterIds.Contains(n.Id);
        var appeared = issuesAfter.Where(i => !beforeKeys.Contains(i.Key) && reported?.ContainsKey(i.Key) != true).ToList();
        var resolved = issuesBefore.Where(i => !afterKeys.Contains(i.Key) && !Left(i.Node)).Select(i => Issue(before, i)).ToList();
        if (reported != null)
        {
            // An announced issue whose device came back without it is resolved, though the scan before didn't list it.
            var afterNodes = after.Listed.GroupBy(KeyAfter).ToDictionary(g => g.Key, g => g.First());
            foreach (var (key, severity) in reported.Where(r => !afterKeys.Contains(r.Key) && !beforeKeys.Contains(r.Key)).ToList())
            {
                int at = key.LastIndexOf('|');
                if (afterNodes.TryGetValue(key[..at], out var node)) resolved.Add(Issue(after, (key, severity, key[(at + 1)..], node)));
            }
            foreach (var i in appeared) reported[i.Key] = i.Severity;
            foreach (var i in issuesBefore.Where(i => !afterKeys.Contains(i.Key) && !Left(i.Node))) reported.Remove(i.Key);
            foreach (var key in reported.Keys.Where(k => !afterKeys.Contains(k) && afterNodes.ContainsKey(k[..k.LastIndexOf('|')])).ToList()) reported.Remove(key);
        }
        report["newIssues"] = J.Arr(appeared.Select(i => Issue(after, i)));
        report["resolvedIssues"] = J.Arr(resolved);
        var planBefore = Reports.PowerPlan(before.Snapshot); var planAfter = Reports.PowerPlan(after.Snapshot);
        if (Json.Write(planBefore) != Json.Write(planAfter)) report["powerPlan"] = J.Obj(("before", planBefore), ("after", planAfter));
        return report;
    }

    internal static bool Empty(JsonObject diff) => new[] { "connected", "disconnected", "moved", "changed", "newIssues", "resolvedIssues" }.All(k => diff[k]!.AsArray().Count == 0) && diff["powerPlan"] == null;

    // One line per change: + connected, - disconnected, > moved, ~ changed, ! new issue, ✓ resolved.
    internal static string Text(JsonObject diff, bool header = true)
    {
        static string S(JsonNode? n) => n?.ToString() ?? "";
        var sb = new StringBuilder();
        if (header) sb.AppendLine($"USB Atlas diff · {S(diff["before"]!["source"])} {S(diff["before"]!["capturedAt"])} → {S(diff["after"]!["source"])} {S(diff["after"]!["capturedAt"])}");
        foreach (var n in diff["connected"]!.AsArray()) sb.AppendLine($"+ connected     {S(n!["path"])} {S(n["name"])} ({S(n["kind"])}{(n["vidPid"] is JsonNode id ? ", " + id : "")}){(n["figures"] is JsonNode f ? " · " + f : "")}");
        foreach (var n in diff["disconnected"]!.AsArray()) sb.AppendLine($"- disconnected  {S(n!["path"])} {S(n["name"])} ({S(n["kind"])}{(n["vidPid"] is JsonNode id ? ", " + id : "")})");
        foreach (var n in diff["moved"]!.AsArray()) sb.AppendLine($"> moved         {S(n!["name"])} {S(n["from"])} → {S(n["to"])}");
        foreach (var n in diff["changed"]!.AsArray())
            foreach (var c in n!["changes"]!.AsArray()) sb.AppendLine($"~ changed       {S(n["path"])} {S(n["name"])}: {S(c!["field"])} {Shown(S(c["before"]))} → {Shown(S(c["after"]))}");
        foreach (var n in diff["newIssues"]!.AsArray()) sb.AppendLine($"! new {S(n!["severity"]),-8}  {S(n["path"])} {S(n["name"])}: {S(n["issue"])}");
        foreach (var n in diff["resolvedIssues"]!.AsArray()) sb.AppendLine($"✓ resolved      {S(n!["path"])} {S(n["name"])}: {S(n["issue"])}");
        if (diff["powerPlan"] is JsonObject plan) sb.AppendLine($"~ power plan    {Json.Write(plan["before"], false)} → {Json.Write(plan["after"], false)}");
        if (header && Empty(diff)) sb.AppendLine("No changes.");
        return sb.ToString();
    }
    private static string Shown(string value) => value.Length == 0 ? "(none)" : value;
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// Plain text for people and for language models reading a terminal: issues and the tree as lines, and
// everything else as indented key: value pairs. JSON holds the same content.
internal static class TextOut
{
    internal static string Render(JsonObject report) => report["report"]?.GetValue<string>() switch
    {
        "issues" => Issues(report),
        "tree" => Tree(report),
        "find" => Find(report),
        "events" => Events(report),
        "check" => PortMapFile.Text(report),
        _ => Generic(report)
    };

    private static string Str(JsonNode? n) => n == null ? "" : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString(Json.Compact);
    private static string Source(JsonObject r) => $"USB Atlas · {Str(r["source"])} · {Str(r["capturedAt"])}{(r["redacted"] != null ? " · serials redacted" : "")}";

    private static string Issues(JsonObject r)
    {
        var sb = new StringBuilder();
        var counts = r["counts"]!.AsObject(); var summary = r["summary"]!.AsObject();
        sb.AppendLine($"{Source(r)} · {Plural(counts["controllers"], "controller")}, {Plural(counts["hubs"], "hub")}, {Plural(counts["devices"], "device")}");
        sb.AppendLine($"{Plural(summary["errors"], "error")}, {Plural(summary["warnings"], "warning")}, {Plural(summary["notes"], "note")}");
        var plan = r["powerPlan"]!.AsObject();
        sb.AppendLine($"USB selective suspend in the power plan: plugged in {Str(plan["pluggedIn"])}, battery {Str(plan["onBattery"])}; now {Str(plan["powerSource"])}");
        if (r["lastWake"] is JsonObject wake) sb.AppendLine($"Last woke from sleep {Str(wake["time"])}: {Str(wake["source"])}{(wake["path"] is JsonNode at ? $" ({Str(at)} {Str(wake["name"])})" : "")}");
        foreach (var d in r["scanDiagnostics"]?.AsArray() ?? []) sb.AppendLine("Scan: " + Str(d));
        if (r["fixFirst"] is JsonArray { Count: > 0 } first)
        {
            sb.AppendLine();
            sb.AppendLine("Fix first:");
            int number = 0;
            foreach (var f in first)
            {
                sb.AppendLine($"  {++number}. {Str(f!["issue"])} — {Str(f["path"])} {Str(f["name"])}: {Str(f["fix"])}");
                if (f["alsoFixes"] is JsonArray also) sb.AppendLine("     Also fixes " + string.Join(", ", also.Select(a => $"{Str(a!["issue"])} on {Str(a["path"])} {Str(a["name"])}")) + ".");
            }
        }
        var issues = r["issues"]!.AsArray();
        if (issues.Count == 0) { sb.AppendLine(); sb.AppendLine("No issues to show."); }
        foreach (var node in issues)
        {
            var i = node!.AsObject();
            sb.AppendLine();
            sb.AppendLine($"{Str(i["severity"]).ToUpperInvariant()}: {Str(i["issue"])} — {Str(i["path"])} {Str(i["name"])} ({Str(i["kind"])}{(i["vidPid"] is JsonNode id ? ", " + Str(id) : "")})");
            sb.AppendLine("  What: " + Str(i["what"]));
            if (i["affects"] is JsonNode affects) sb.AppendLine("  Affects: " + Str(affects));
            if (i["cause"] is JsonNode cause) sb.AppendLine("  Cause: " + Str(cause));
            if (i["steps"] is JsonArray steps) { sb.AppendLine("  Do:"); foreach (var step in steps) sb.AppendLine("    - " + Str(step)); }
        }
        if (issues.Count > 0) { sb.AppendLine(); sb.AppendLine("Details and evidence for any of these: atlascli show <path>"); }
        return sb.ToString();
    }
    private static string Plural(JsonNode? count, string word) => $"{Str(count)} {word}{(Str(count) == "1" ? "" : "s")}";

    private static string Tree(JsonObject r)
    {
        var sb = new StringBuilder();
        var counts = r["counts"]!.AsObject();
        sb.AppendLine($"{Source(r)} · {Plural(counts["controllers"], "controller")}, {Plural(counts["hubs"], "hub")}, {Plural(counts["devices"], "device")}, {Plural(counts["emptyPorts"], "empty port")}");
        void Line(JsonObject n, int depth)
        {
            var parts = new List<string> { Str(n["kind"]) };
            foreach (var key in new[] { "figures", "vidPid" }) if (n[key] is JsonNode v) parts.Add(Str(v));
            if (n["portName"] is JsonNode port) parts.Add($"port “{Str(port)}”");
            string line = $"{new string(' ', depth * 2)}{Str(n["path"])} {Str(n["name"])} — {string.Join(" · ", parts)}";
            if (n["emptyPorts"] is JsonArray empty) line += $" · empty ports {string.Join(", ", empty.Select(Str))}";
            if (n["usb3HalfOf"] is JsonNode hub) line += $"  [USB 3 half of {Str(hub)}'s socket: its USB 3 side should connect here but didn't]";
            if (n["issues"] is JsonArray issues) line += "  [" + string.Join("; ", issues.Select(Str)) + "]";
            sb.AppendLine(line);
            foreach (var c in n["children"]?.AsArray() ?? []) Line(c!.AsObject(), depth + 1);
        }
        foreach (var c in r["controllers"]!.AsArray()) Line(c!.AsObject(), 0);
        return sb.ToString();
    }

    private static string Find(JsonObject r)
    {
        var matches = r["matches"]!.AsArray();
        var sb = new StringBuilder($"{matches.Count} match{(matches.Count == 1 ? "" : "es")} for “{Str(r["query"])}”\n");
        foreach (var m in matches)
        {
            var o = m!.AsObject();
            sb.Append($"{Str(o["path"])} {Str(o["name"])} — {Str(o["kind"])}");
            foreach (var key in new[] { "figures", "vidPid" }) if (o[key] is JsonNode v) sb.Append(" · " + Str(v));
            if (o["issues"] is JsonArray issues) sb.Append("  [" + string.Join("; ", issues.Select(Str)) + "]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Events(JsonObject r)
    {
        var events = r["events"]!.AsArray();
        var sb = new StringBuilder($"{Source(r)} · {events.Count} event{(events.Count == 1 ? "" : "s")} in the {Str(r["window"])}, newest first\n");
        foreach (var d in r["logDiagnostics"]?.AsArray() ?? []) sb.AppendLine("Log: " + Str(d));
        sb.AppendLine(Str(r["note"]));
        foreach (var e in events)
        {
            var o = e!.AsObject();
            bool usb = Str(o["category"]) is "usb" or "";
            string where = o["path"] is JsonNode path ? $"{Str(path)} {Str(o["name"])}"
                : o["name"] is JsonNode name ? Str(name) + (o["instanceId"] is JsonNode named ? $" ({Str(named)})" : "")
                : o["instanceId"] is JsonNode id ? Str(id) + (usb ? " (not connected now)" : "") : "";
            sb.AppendLine($"{Str(o["time"])} {Str(o["level"]),-8} {Str(o["category"]),-8} {Str(o["source"])}  {where}".TrimEnd());
            if (o["message"] is JsonNode message) sb.AppendLine("    " + Str(message));
        }
        return sb.ToString();
    }

    // Indented key: value, lists as dashes. Header fields are folded into one first line.
    internal static string Generic(JsonObject r)
    {
        var sb = new StringBuilder();
        if (r["report"] != null) sb.AppendLine(Source(r));
        var body = new JsonObject();
        foreach (var (key, value) in r)
            if (key is not ("schemaVersion" or "report" or "source" or "capturedAt" or "redacted")) body[key] = value?.DeepClone();
        foreach (var line in Lines(body)) sb.AppendLine(line);
        return sb.ToString();
    }
    private static bool Empty(JsonNode? v) => v is JsonObject { Count: 0 } or JsonArray { Count: 0 } or null;
    // Short lists of single words, such as functions or empty ports, read best on one line.
    private static bool Inline(JsonArray a) => a.All(x => x is JsonValue) && a.Sum(x => Str(x).Length + 2) < 80 && (a.Count == 1 || a.All(x => !Str(x).Contains(' ')));
    private static IEnumerable<string> Lines(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (Empty(v)) continue;
                    if (v is JsonValue) yield return $"{k}: {Str(v)}";
                    else if (v is JsonArray a && Inline(a)) yield return $"{k}: {string.Join(", ", a.Select(Str))}";
                    else { yield return k + ":"; foreach (var line in Lines(v)) yield return "  " + line; }
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    if (item is JsonValue || item == null) { yield return "- " + Str(item); continue; }
                    bool first = true;
                    foreach (var line in Lines(item)) { yield return (first ? "- " : "  ") + line; first = false; }
                }
                break;
            default:
                yield return Str(value);
                break;
        }
    }
}

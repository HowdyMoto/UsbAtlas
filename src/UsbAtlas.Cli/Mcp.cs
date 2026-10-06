using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsbAtlas.Cli;

// A Model Context Protocol server on stdin and stdout (newline-delimited JSON-RPC 2.0), so an agent can
// call USB Atlas as tools. Each tool runs the matching command, so tools and commands always agree.
// Everything is read-only. Nothing but protocol messages is written to stdout.
internal static class Mcp
{
    private static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private const string Instructions = "USB Atlas reads this Windows PC's USB topology: host controllers, hubs, ports and devices, their negotiated link rates, reserved bandwidth, requested power, polling rates, power-saving settings and drivers, and explains problems in plain words. Start with usb_issues; use usb_tree for the layout and usb_show <path> for one device. Paths such as H01/04/02 name the host and each port on the way. For intermittent problems, call usb_watch while the person replugs or wiggles the device, usb_trace for the hub driver's account of why a link dropped or came up slow, or usb_baseline before a change and usb_diff after it. All tools are read-only. Figures come from descriptors and Windows, not measurements.";

    private sealed record Tool(string Name, string Description, JsonObject Properties, string[] Required, Func<JsonObject, List<string>> Args);

    private static JsonObject Prop(string type, string description, params (string, JsonNode)[] extra)
    {
        var o = J.Obj(("type", type), ("description", description));
        foreach (var (k, v) in extra) o[k] = v;
        return o;
    }
    private static string? Arg(JsonObject a, string name) => a[name] is JsonValue v ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString() : null;
    private static bool Flag(JsonObject a, string name) => a[name] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static List<Tool> Tools(string baseline) =>
    [
        new("usb_issues", "List every USB issue on this PC, most severe first: devices linked slower than they support, power and bandwidth budgets, Windows driver problem codes, port errors, unstable connections, game controllers Windows may suspend, contradictions in the firmware's port map. Each comes with what it means, whether it affects anything now, the likely cause and steps to fix it. Start here.",
            J.Obj(("min_severity", Prop("string", "Leave out issues below this severity.", ("enum", new JsonArray("note", "warning", "error"))))), [],
            a => ["issues", .. Arg(a, "min_severity") is string m ? new[] { "--min", m } : []]),
        new("usb_tree", "The USB topology as an indented tree: each host controller, hub and device with its path (such as H01/04/02), kind, link rate, polling rate, requested power, VID:PID and issues. Empty ports are summarized per hub.",
            J.Obj(("include_empty_ports", Prop("boolean", "List every empty port as its own line."))), [],
            a => ["tree", .. Flag(a, "include_empty_ports") ? new[] { "--ports" } : []]),
        new("usb_show", "Everything known about one controller, hub, port or device: identity, driver, link, socket, power, bandwidth, polling, power saving, the chain up to the host, what shares its hub, what's plugged into it, its issues explained, and the evidence USB Atlas used.",
            J.Obj(("target", Prop("string", "A path such as H01/04/02, a VID:PID such as 046D:C52B, an instance ID, or words from its name."))), ["target"],
            a => ["show", Arg(a, "target") ?? ""]),
        new("usb_find", "Search like the app: names, labels, VID:PID, manufacturers, serials, device types, paths, sockets (\"USB-C\", \"10 Gb/s\"), polling rates (\"1000 Hz\"), HID collections (\"Joystick\") and issue names.",
            J.Obj(("query", Prop("string", "Text to look for."))), ["query"], a => ["find", Arg(a, "query") ?? ""]),
        new("usb_budget", "Bandwidth and power arithmetic with its inputs: reserved and peak periodic bandwidth against what each link can reserve, the shared transaction translator of single-TT hubs, and bus-powered hubs' current against what the specification guarantees. Without a target, covers every hub and anything with a budget issue.",
            J.Obj(("target", Prop("string", "Optional: a path, VID:PID or name."))), [],
            a => ["budget", .. Arg(a, "target") is string t ? new[] { t } : []]),
        new("usb_descriptors", "Raw USB descriptors for one node, decoded field by field with their hex: device, configuration (interfaces, endpoints, class-specific), BOS capabilities (USB 2.0 LPM, SuperSpeed, SuperSpeedPlus lane speeds, platform), hub descriptor, and connection and connector flags.",
            J.Obj(("target", Prop("string", "A path, VID:PID or name."))), ["target"], a => ["raw", Arg(a, "target") ?? ""]),
        new("usb_events", "Recent USB history from the Windows event logs: devices set up, started, failing to start (Kernel-PnP 411) or removed, drivers that failed to load (219), and USB host and hub driver events, each matched to where the device is now when it's connected.",
            J.Obj(("hours", Prop("number", "How far back to look. Default 24.")), ("max", Prop("integer", "Most events to return, newest first. Default 100.")), ("errors_only", Prop("boolean", "Only critical, error and warning events."))), [],
            a => ["events", "--since", (Arg(a, "hours") ?? "24") + "h", "--max", Arg(a, "max") ?? "100", .. Flag(a, "errors_only") ? new[] { "--errors" } : []]),
        new("usb_watch", "Watch USB devices connect and disconnect for some seconds and report each change (connected, disconnected, moved, link or power changes, issues appearing or resolving) and devices that drop and come back quickly. Ask the person to replug or wiggle the device while it runs.",
            J.Obj(("seconds", Prop("integer", "How long to watch, 5 to 120. Default 20."))), [],
            a => ["watch", "--for", Math.Clamp(int.TryParse(Arg(a, "seconds"), out var s) ? s : 20, 5, 120) + "s"]),
        new("usb_trace", "Record what Windows' USB hub driver reports for some seconds, placed on the topology: connections, port and warm resets, USB 3 link failures (config errors, SS.Inactive, compliance mode), overcurrent, enumeration retries and failures, rejected descriptors, SuperSpeed devices that came up on the USB 2 bus, U1/U2 refused, and USB-C alternate modes; then a count per port. This explains why a device dropped or runs slower. Ask the person to replug the device while it runs. Needs administrator rights or the Performance Log Users group.",
            J.Obj(("seconds", Prop("integer", "How long to record, 5 to 120. Default 30."))), [],
            a => ["trace", "--for", Math.Clamp(int.TryParse(Arg(a, "seconds"), out var s) ? s : 30, 5, 120) + "s"]),
        new("usb_baseline", "Save the current USB state in this session, to compare with usb_diff after the person changes something (moves a device, swaps a cable, changes a setting).",
            new JsonObject(), [], _ => ["scan", "--out", baseline]),
        new("usb_diff", "Compare the USB state now with the one saved by usb_baseline: what connected, disconnected or moved, what changed, and which issues appeared or were resolved.",
            new JsonObject(), [], _ => ["diff", baseline]),
        new("usb_snapshot", "The full snapshot as JSON: every node with every field USB Atlas reads. Large; prefer the other tools unless you need a field they leave out.",
            new JsonObject(), [], _ => ["scan", "--json"]),
    ];

    internal static int Serve(IReadOnlyList<string> serverArgs, TextReader input, TextWriter output, CancellationToken cancel)
    {
        string baseline = Path.Combine(Path.GetTempPath(), $"usbatlas-mcp-baseline-{Environment.ProcessId}.json");
        var tools = Tools(baseline);
        try
        {
            while (!cancel.IsCancellationRequested && input.ReadLine() is string line)
            {
                if (line.Trim().Length == 0) continue;
                JsonNode? message;
                try { message = JsonNode.Parse(line); }
                catch (JsonException) { Send(output, Error(null, -32700, "Parse error")); continue; }
                if (message is not JsonObject request) { Send(output, Error(null, -32600, "Batches and non-object messages aren't supported.")); continue; }
                var id = request["id"]?.DeepClone();
                string method = request["method"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : "";
                if (id == null) continue; // A notification, such as notifications/initialized, or a response: nothing to answer.
                var parameters = request["params"] as JsonObject ?? new JsonObject();
                JsonObject response;
                try
                {
                    response = method switch
                {
                    "initialize" => Result(id, J.Obj(
                        ("protocolVersion", Versions.Contains(Arg(parameters, "protocolVersion")) ? Arg(parameters, "protocolVersion") : Versions[0]),
                        ("capabilities", J.Obj(("tools", J.Obj(("listChanged", false))))),
                        ("serverInfo", J.Obj(("name", "usb-atlas"), ("title", "USB Atlas"), ("version", Program.Version))),
                        ("instructions", Instructions))),
                    "ping" => Result(id, new JsonObject()),
                    "tools/list" => Result(id, J.Obj(("tools", J.Arr(tools.Select(t => (JsonNode)J.Obj(("name", t.Name), ("description", t.Description),
                        ("inputSchema", J.Obj(("type", "object"), ("properties", WithFormat(t.Properties)), ("required", J.Some(t.Required.Select(r => (JsonNode)r))), ("additionalProperties", false))),
                        ("annotations", J.Obj(("readOnlyHint", true), ("openWorldHint", false))))))))),
                    "tools/call" => Call(id, parameters, tools, serverArgs, cancel),
                    _ => Error(id, -32601, $"Method not found: {method}")
                };
                }
                catch (Exception ex) { response = Error(id, -32603, $"Internal error: {ex.Message}"); }
                Send(output, response);
            }
        }
        finally { try { File.Delete(baseline); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return 0;
    }

    private static JsonObject WithFormat(JsonObject properties)
    {
        var o = (JsonObject)properties.DeepClone();
        o["format"] = Prop("string", "text (default), written to be read, or json for the same content as structured data.", ("enum", new JsonArray("text", "json")));
        return o;
    }

    private static JsonObject Call(JsonNode id, JsonObject parameters, List<Tool> tools, IReadOnlyList<string> serverArgs, CancellationToken cancel)
    {
        string name = Arg(parameters, "name") ?? "";
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool == null) return Error(id, -32602, $"Unknown tool: {name}");
        foreach (var required in tool.Required)
            if (Arg(arguments, required) is not { Length: > 0 }) return ToolResult(id, $"{required} is required.", true);
        var args = tool.Args(arguments);
        if (Arg(arguments, "format") == "json" && !args.Contains("--json")) args.Add("--json");
        args.AddRange(serverArgs);
        var stdout = new StringWriter(); var stderr = new StringWriter();
        int code;
        try { code = Program.Execute(args, stdout, stderr, cancel); }
        catch (Exception ex) { return ToolResult(id, $"{ex.GetType().Name}: {ex.Message}", true); }
        string text = stdout.ToString();
        if (name == "usb_baseline" && code == 0) text = "Baseline saved. After the change, call usb_diff.";
        // 1 and 2 are the health of what was found, not failures.
        bool failed = code >= 3;
        if (stderr.ToString().Trim() is { Length: > 0 } err) text = failed ? err : text + "\n" + err;
        return ToolResult(id, text.Length > 0 ? text : $"(no output, exit code {code})", failed);
    }

    private static JsonObject ToolResult(JsonNode id, string text, bool isError) =>
        Result(id, J.Obj(("content", new JsonArray(J.Obj(("type", "text"), ("text", text)))), ("isError", isError)));
    private static JsonObject Result(JsonNode id, JsonObject result) => J.Obj(("jsonrpc", "2.0"), ("id", id), ("result", result));
    private static JsonObject Error(JsonNode? id, int code, string message)
    {
        var o = J.Obj(("jsonrpc", "2.0"), ("error", J.Obj(("code", code), ("message", message))));
        o["id"] = id;
        return o;
    }
    private static void Send(TextWriter output, JsonObject message) { output.Write(Json.Write(message, false) + "\n"); output.Flush(); }
}

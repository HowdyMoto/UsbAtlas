using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UsbAtlas.Cli;

internal static class Json
{
    // camelCase out; snapshots exported by the app (PascalCase) load as well. Symbols such as ≥ and · stay readable.
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    internal static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
    internal static string Write(JsonNode? node, bool indented = true) => node?.ToJsonString(indented ? Options : Compact) ?? "null";
}

// One snapshot and what every command needs to talk about it: paths such as H01/04/02, parents, and
// targets given as a path, an ID, a VID:PID or a search.
internal sealed class Session
{
    internal const int SchemaVersion = 1;
    internal Snapshot Snapshot { get; }
    // live, demo, or the file it was loaded from.
    internal string Source { get; }
    private readonly Dictionary<string, string> paths;
    private readonly Dictionary<string, UsbNode> parents = [];

    internal Session(Snapshot snapshot, string source)
    {
        Snapshot = snapshot; Source = source;
        paths = Topology.PathLabels(snapshot);
        foreach (var n in snapshot.Nodes) foreach (var c in n.Children) parents[c.Id] = n;
    }

    // --input FILE, --demo, or a live scan with the app's saved labels applied. --redact applies to all three.
    internal static Session Open(Options o, bool raw = false)
    {
        Snapshot snapshot; string source;
        if (o.Get("input") is string file)
        {
            snapshot = LoadFile(file); source = Path.GetFileName(file);
        }
        else if (o.Has("demo")) { snapshot = DemoData.Create(); source = "demo"; }
        else
        {
            try { snapshot = Scanner.ThisComputer(raw); }
            catch (PlatformNotSupportedException ex) { throw new CliException(ex.Message + " Read a saved snapshot with --input FILE."); }
            source = "live";
            var labels = new DeviceLabels();
            if (labels.LoadError != null) snapshot.Diagnostics.Add(labels.LoadError);
            else labels.Apply(snapshot);
        }
        if (o.Has("redact")) snapshot = Redact(snapshot);
        return new(snapshot, source) { Redacted = o.Has("redact") };
    }

    internal static Snapshot LoadFile(string file)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), Json.Options) ?? throw new CliException($"{file} is empty.");
            Normalize(snapshot);
            // Derived from what the file holds, so a snapshot saved before a rule existed is still checked by it.
            PortMap.Analyze(snapshot);
            HubDepth.Analyze(snapshot);
            Containers.Analyze(snapshot);
            if (snapshot.Controllers.Count == 0 && snapshot.Diagnostics.Count == 0) throw new CliException($"{file} has no controllers. Is it a USB Atlas snapshot (atlascli scan, or Export in the app)?");
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new CliException($"Can't load {file}: {ex.Message}");
        }
    }

    // A file may hold nulls where the model expects values: empty lists and strings stand in for them.
    private static void Normalize(Snapshot snapshot)
    {
        snapshot.Controllers ??= []; snapshot.Diagnostics ??= []; snapshot.Containers ??= []; snapshot.Gpus ??= []; snapshot.Displays ??= [];
        snapshot.Gpus.RemoveAll(g => g == null); snapshot.Displays.RemoveAll(d => d == null);
        snapshot.Controllers.RemoveAll(c => c == null);
        snapshot.Containers.RemoveAll(c => c == null);
        foreach (var c in snapshot.Containers) { c.Id ??= ""; c.Name ??= ""; c.Manufacturer ??= ""; c.Model ??= ""; }
        var stack = new Stack<UsbNode>(snapshot.Controllers);
        var seen = new HashSet<UsbNode>(ReferenceEqualityComparer.Instance);
        while (stack.TryPop(out var n))
        {
            if (!seen.Add(n)) continue;
            foreach (var p in typeof(UsbNode).GetProperties().Where(p => p.CanWrite && p.GetValue(n) == null))
                if (p.PropertyType == typeof(string)) p.SetValue(n, "");
                else if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>)) p.SetValue(n, Activator.CreateInstance(p.PropertyType));
            n.Children.RemoveAll(c => c == null);
            n.DriverProblems.RemoveAll(p => p == null);
            n.MoreCompanions.RemoveAll(c => c == null);
            foreach (var c in n.MoreCompanions) { c.HubSymbolicLink ??= ""; c.Id ??= ""; }
            if (n.Billboard is BillboardInfo b)
            {
                b.Modes ??= []; b.Modes.RemoveAll(m => m == null);
                b.Version ??= ""; b.AdditionalInfoUrl ??= ""; b.VconnPower ??= "";
                foreach (var m in b.Modes) { m.Svid ??= ""; m.Name ??= ""; m.Description ??= ""; m.State ??= ""; m.Vdo ??= ""; }
            }
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    // Serial numbers identify a particular device, and Windows builds them into instance IDs and hub
    // links, so each is replaced everywhere by a short hash: the same serial reads the same throughout.
    internal static Snapshot Redact(Snapshot snapshot)
    {
        // Short or repetitive serials, such as 0000 or 1, aren't identifying and would match unrelated
        // values, so they stay; hashing them would also make them look unique. Hashes aren't hashed again.
        var serials = snapshot.Nodes.Select(n => n.Serial).Where(Identifying).Distinct().OrderByDescending(s => s.Length).ToList();
        foreach (var n in snapshot.Nodes) if (Identifying(n.Serial)) n.Serial = Token(n.Serial);
        string json = JsonSerializer.Serialize(snapshot, Json.Compact);
        foreach (var serial in serials)
        {
            string escaped = JsonSerializer.Serialize(serial, Json.Compact)[1..^1];
            json = json.Replace(escaped, Token(serial), StringComparison.OrdinalIgnoreCase);
        }
        return JsonSerializer.Deserialize<Snapshot>(json, Json.Options)!;
    }
    private static bool Identifying(string serial) => serial.Trim().Length >= 6 && serial.Distinct().Count() > 2 && !serial.StartsWith("redacted-", StringComparison.Ordinal);
    private static string Token(string serial) => "redacted-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial.ToUpperInvariant())))[..8].ToLowerInvariant();

    // Instance IDs in text this snapshot doesn't know, such as event logs of devices since unplugged: the
    // last segment of USB\VID_…&PID_…\SERIAL is a serial when it has no &, and a run of 12 hex digits
    // not inside a GUID is usually a Bluetooth address.
    internal static string RedactText(string text)
    {
        text = Regex.Replace(text, @"(?<=\b(?:USB|USBSTOR|HID|BTHENUM|BTHLE|BTHLEDEVICE|SWD|WPDBUSENUM|SCSI)\\[^\\\s]+\\)(?>[^\\\s&]{6,})(?!&)", m => m.Value.StartsWith("redacted-") ? m.Value : Token(m.Value), RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", m => m.Value.Distinct().Count() > 2 ? Token(m.Value) : m.Value);
    }
    internal bool Redacted { get; init; }

    internal string PathOf(UsbNode n) => paths.GetValueOrDefault(n.Id, "?");
    internal UsbNode? Parent(UsbNode n) => parents.GetValueOrDefault(n.Id);
    internal List<UsbNode> Chain(UsbNode n) => Topology.FindPath(Snapshot, n.Id);
    internal UsbNode? ById(string id) => Snapshot.Nodes.FirstOrDefault(n => n.Id == id);
    // A merged root hub is drawn, named and pathed as its controller, so it isn't listed on its own.
    internal bool IsMergedRoot(UsbNode n) => n.Kind == "Root hub" && Parent(n) is UsbNode p && Topology.MergedRoot(p) == n;
    internal IEnumerable<UsbNode> Listed => Snapshot.Nodes.Where(n => !IsMergedRoot(n));

    internal UsbNode Resolve(string target)
    {
        target = target.Trim();
        if (target.Length == 0) throw new CliException("Name a target: a path such as H01/04/02, a VID:PID, an instance ID or words from its name.");
        var byPath = Listed.FirstOrDefault(n => PathOf(n).Equals(target.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (byPath != null) return byPath;
        var byId = Snapshot.Nodes.FirstOrDefault(n => n.Id.Equals(target, StringComparison.OrdinalIgnoreCase) || n.InstanceId.Length > 0 && n.InstanceId.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return IsMergedRoot(byId) ? Parent(byId)! : byId;
        List<UsbNode> found = Regex.IsMatch(target, "^[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}$")
            ? Listed.Where(n => $"{n.VendorId}:{n.ProductId}".Equals(target, StringComparison.OrdinalIgnoreCase)).ToList()
            : Find(target);
        return found.Count switch
        {
            1 => found[0],
            0 => throw new CliException($"Nothing matches “{target}”. Run atlascli tree to see paths."),
            _ => throw new CliException($"“{target}” matches {found.Count} nodes; name one by path:\n" + string.Join("\n", found.Take(12).Select(n => $"  {PathOf(n)}  {Topology.ShortName(n)} ({Topology.Label(n)})")) + (found.Count > 12 ? $"\n  … and {found.Count - 12} more" : ""))
        };
    }

    // The app's search: names, IDs, types, sockets, issues, paths, functions and polling rates.
    internal List<UsbNode> Find(string query) => Listed.Where(n => Topology.SearchText(n, PathOf(n)).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
}

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsbAtlas;

internal sealed class DeviceLabels
{
    private readonly string path;
    private Dictionary<string, string> labels = [];
    internal string? LoadError { get; }
    internal DeviceLabels(string? filePath = null)
    {
        path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbAtlas", "device-labels.json");
        try
        {
            if (File.Exists(path)) labels = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? throw new JsonException("Empty label data.");
            if (labels.Values.Any(value => value == null || value.Length > 100)) throw new JsonException("Invalid label data.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            labels = []; LoadError = "Saved labels could not be loaded: " + ex.Message;
        }
    }
    internal static bool FollowsDevice(UsbNode node, Snapshot snapshot) => node.VendorId.Length > 0 && node.ProductId.Length > 0
        && !string.IsNullOrWhiteSpace(node.Serial) && node.Serial.Distinct().Count() > 1
        && !node.Serial.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        && snapshot.Nodes.Count(n => n.Kind == node.Kind && n.VendorId == node.VendorId && n.ProductId == node.ProductId && n.Serial == node.Serial) == 1;
    private static string Key(UsbNode node, Snapshot snapshot)
    {
        var identity = FollowsDevice(node, snapshot)
            ? $"serial|{node.Kind}|{node.VendorId}|{node.ProductId}|{node.Serial}"
            : $"port|{node.Kind}|{node.Id}|{node.VendorId}|{node.ProductId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((snapshot.IsDemo ? "demo|" : "hardware|") + identity)));
    }
    private static string PortKey(UsbNode node, Snapshot snapshot)
    {
        var parent = snapshot.Nodes.FirstOrDefault(n => n.Children.Any(c => c.Id == node.Id));
        return parent == null ? "" : "port-name|" + Key(parent, snapshot) + "|" + node.Port;
    }
    private static string SnapKey(UsbNode node, Snapshot snapshot) => "snap|" + PortKey(node, snapshot) + "|" + node.VendorId + ":" + node.ProductId + "|" + node.Serial;
    internal bool TrySetSnap(UsbNode node, Snapshot snapshot, bool enabled, out string error)
    {
        var parent = snapshot.Nodes.FirstOrDefault(n => n.Children.Contains(node));
        if (node.Kind != "Hub" || parent?.Kind != "Hub") { error = "Select a hub connected directly to another hub."; return false; }
        if (enabled && parent.Children.Any(n => n.Id != node.Id && n.SnapToParentHub)) { error = "This hub already has a snapped downstream stage. Unlink that stage first."; return false; }
        return Set(node, snapshot, enabled ? "linked" : "", false, out error, true);
    }
    internal bool TrySetPort(UsbNode node, Snapshot snapshot, string label, out string error) => Set(node, snapshot, label, true, out error);
    private static string SocketKey(UsbNode node, Snapshot snapshot) => PortKey(node, snapshot) is { Length: > 0 } key ? "socket-speed|" + key : "";
    private static string ConnectorKey(UsbNode node, Snapshot snapshot) => PortKey(node, snapshot) is { Length: > 0 } key ? "socket-connector|" + key : "";
    internal static readonly string[] Connectors = ["USB-A", "USB-C", "Internal"];
    internal bool TrySetSocketSpeed(UsbNode node, Snapshot snapshot, double? mbps, out string error)
    {
        if (mbps is not (null or 5000 or 10000)) { error = "Set a socket to 5 Gb/s, or to 10 Gb/s or faster."; return false; }
        return SetSocket(node, snapshot, SocketKey, mbps?.ToString(CultureInfo.InvariantCulture), out error);
    }
    internal bool TrySetSocketConnector(UsbNode node, Snapshot snapshot, string? connector, out string error)
    {
        if (connector != null && !Connectors.Contains(connector)) { error = "Set a socket to USB-A, USB-C or Internal."; return false; }
        return SetSocket(node, snapshot, ConnectorKey, connector, out error);
    }
    // What the user sets for a socket is kept under both of its halves, so either half finds it and clears it.
    private bool SetSocket(UsbNode node, Snapshot snapshot, Func<UsbNode, Snapshot, string> keyOf, string? value, out string error)
    {
        error = "";
        if (LoadError != null) { error = LoadError + " Existing file has been preserved."; return false; }
        var halves = snapshot.Nodes.Where(n => n.Id == node.Id || node.CompanionId.Length > 0 && n.Id == node.CompanionId);
        var keys = halves.Select(n => keyOf(n, snapshot)).Where(k => k.Length > 0).ToList();
        if (keys.Count == 0) { error = "Select a numbered port on a hub."; return false; }
        var next = new Dictionary<string, string>(labels);
        foreach (var key in keys) { if (value != null) next[key] = value; else next.Remove(key); }
        return Save(next, snapshot, out error);
    }
    internal void Apply(Snapshot snapshot)
    {
        foreach (var node in snapshot.Nodes)
        {
            node.UserLabel = labels.GetValueOrDefault(Key(node, snapshot), ""); node.PortLabel = labels.GetValueOrDefault(PortKey(node, snapshot), ""); node.SnapToParentHub = labels.GetValueOrDefault(SnapKey(node, snapshot), "") == "linked";
            node.SocketRatedMbps = double.TryParse(labels.GetValueOrDefault(SocketKey(node, snapshot), ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var rated) && rated > 0 ? rated : null;
            node.SocketConnectorSet = labels.GetValueOrDefault(ConnectorKey(node, snapshot)) is string connector && Connectors.Contains(connector) ? connector : null;
        }
        // The scan drew the sockets before it knew what the user set for them, so they are drawn again with it.
        DeviceIdentity.ClassifySockets(snapshot);
    }
    internal bool TrySet(UsbNode node, Snapshot snapshot, string label, out string error) => Set(node, snapshot, label, false, out error);
    private bool Set(UsbNode node, Snapshot snapshot, string label, bool port, out string error, bool snap = false)
    {
        error = "";
        if (LoadError != null) { error = LoadError + " Existing file has been preserved."; return false; }
        label = label.Trim();
        if (label.Length > 100 || label.Any(char.IsControl)) { error = "Use a label of at most 100 characters on one line."; return false; }
        var next = new Dictionary<string, string>(labels);
        string key = snap ? SnapKey(node, snapshot) : port ? PortKey(node, snapshot) : Key(node, snapshot);
        if (key.Length == 0) { error = "Select a numbered port on a hub."; return false; }
        if (label.Length == 0) next.Remove(key); else next[key] = label;
        return Save(next, snapshot, out error);
    }
    // Writes the labels whole through a temporary file, so a failed write leaves the saved ones intact.
    private bool Save(Dictionary<string, string> next, Snapshot snapshot, out string error)
    {
        error = "";
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            labels = next; Apply(snapshot); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Label could not be saved: " + ex.Message; return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

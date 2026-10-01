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
    internal void Apply(Snapshot snapshot)
    {
        foreach (var node in snapshot.Nodes) node.UserLabel = labels.GetValueOrDefault(Key(node, snapshot), "");
    }
    internal bool TrySet(UsbNode node, Snapshot snapshot, string label, out string error)
    {
        error = "";
        if (LoadError != null) { error = LoadError + " Existing file has been preserved."; return false; }
        label = label.Trim();
        if (label.Length > 100 || label.Any(char.IsControl)) { error = "Use a label of at most 100 characters on one line."; return false; }
        var next = new Dictionary<string, string>(labels);
        string key = Key(node, snapshot);
        if (label.Length == 0) next.Remove(key); else next[key] = label;
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

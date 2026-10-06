using System.Runtime.InteropServices;

namespace UsbAtlas;

// A Windows device container: one physical product, such as a monitor whose hub, Billboard and audio are
// separate USB devices. Windows groups devices by the Container ID they report, or by where they sit.
public sealed class DeviceContainer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
}

internal static class Containers
{
    internal const string SharedId = "Container ID shared";
    // The computer itself: everything built in that Windows doesn't put in a container of its own.
    private const string RootContainer = "{00000000-0000-0000-ffff-ffffffffffff}";

    // Each device container's display name, manufacturer and model, by ID, from the device query API.
    // Empty when Windows doesn't answer; containers are a refinement, not something a scan needs.
    internal static Dictionary<string, DeviceContainer> Read(IEnumerable<string> ids)
    {
        var wanted = ids.Where(i => i.Length > 0 && !i.Equals(RootContainer, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, DeviceContainer>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;
        var keys = new[] { (ItemNameDisplay, 10u), (ContainerProperties, 0x2000u), (ContainerProperties, 0x2002u) };
        var requested = keys.Select(k => new CompKey { Key = new PropertyKey { Category = k.Item1, Id = k.Item2 } }).ToArray();
        try
        {
            if (DevGetObjects(2, 0, (uint)requested.Length, requested, 0, IntPtr.Zero, out uint count, out IntPtr objects) != 0 || objects == IntPtr.Zero) return result;
            try
            {
                int objectSize = Marshal.SizeOf<DevObject>(), propertySize = Marshal.SizeOf<DevProperty>();
                for (int i = 0; i < count; i++)
                {
                    var o = Marshal.PtrToStructure<DevObject>(objects + i * objectSize);
                    var id = Marshal.PtrToStringUni(o.ObjectId) ?? "";
                    if (!wanted.Contains(id)) continue;
                    var container = new DeviceContainer { Id = id.ToLowerInvariant() };
                    for (int p = 0; p < o.PropertyCount; p++)
                    {
                        var property = Marshal.PtrToStructure<DevProperty>(o.Properties + p * propertySize);
                        // DEVPROP_TYPE_STRING.
                        if (property.Type != 0x12 || property.Buffer == IntPtr.Zero) continue;
                        var text = (Marshal.PtrToStringUni(property.Buffer) ?? "").Trim();
                        if (property.Key.Key.Category == ItemNameDisplay) container.Name = text;
                        else if (property.Key.Key.Id == 0x2000) container.Manufacturer = text;
                        else container.Model = text;
                    }
                    result[id] = container;
                }
            }
            finally { DevFreeObjects(count, objects); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return result;
    }

    internal static string Normalize(Guid id) => id.ToString("B").ToLowerInvariant();
    internal static bool IsRoot(string id) => id.Equals(RootContainer, StringComparison.OrdinalIgnoreCase);

    // The USB devices and hubs that share each container, other than the computer's own.
    internal static Dictionary<string, List<UsbNode>> Groups(Snapshot snapshot) => snapshot.Nodes
        .Where(n => n.Kind is "Device" or "Hub" && n.ContainerId.Length > 0 && !IsRoot(n.ContainerId))
        .GroupBy(n => n.ContainerId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    // The members of a group that aren't plugged into another member: one for a product, or a USB 3 hub's two
    // sides, which are plugged in separately. Any other second top means unrelated hardware shares the ID.
    internal static List<UsbNode> Tops(Snapshot snapshot, List<UsbNode> members)
    {
        var set = members.ToHashSet();
        return members.Where(m => !Topology.FindPath(snapshot, m.Id).SkipLast(1).Any(set.Contains)).ToList();
    }
    // The physical units among a group's tops: a USB 3 hub's USB 2 side is the same unit as its USB 3 side.
    internal static List<UsbNode> Units(List<UsbNode> tops) => tops.Where(t => !(t.IsUsb2Companion && tops.Any(o => o.Id == t.CompanionHubId))).ToList();
    internal static bool Shared(Snapshot snapshot, List<UsbNode> members) => Units(Tops(snapshot, members)).Count > 1;
    private static bool SameUnit(UsbNode a, UsbNode b) => a == b || a.CompanionHubId == b.Id || b.CompanionHubId == a.Id;

    // A product name worth showing: the container's model or name when it says more than "USB hub" and more
    // than the chip's own name, else, for display, what you named its top device.
    internal static string ProductName(Snapshot snapshot, string containerId, List<UsbNode> members, bool labels = true)
    {
        var c = snapshot.Containers.FirstOrDefault(x => x.Id.Equals(containerId, StringComparison.OrdinalIgnoreCase));
        foreach (var name in new[] { c?.Model, c?.Name })
            if (!DeviceIdentity.IsGenericName(name) && !members.Any(m => m.LookupProduct.Length > 0 && name!.Contains(m.LookupProduct, StringComparison.OrdinalIgnoreCase)))
                return name!;
        return labels ? Tops(snapshot, members).Select(t => t.UserLabel).FirstOrDefault(l => l.Length > 0) ?? "" : "";
    }

    // Flags a Container ID that unrelated hardware shares, which comes from firmware that gives every unit
    // the same one, and names chip-named hubs and devices after the product Windows says they're part of.
    // Snapshots are analyzed again when loaded, so a name is extended only once.
    internal static void Analyze(Snapshot snapshot)
    {
        foreach (var n in snapshot.Nodes) n.ContainerIdShared = false;
        foreach (var (id, members) in Groups(snapshot))
        {
            var c = snapshot.Containers.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            foreach (var m in members) m.ContainerName = c == null ? "" : c.Model.Length > 0 ? c.Model : c.Name;
            if (members.Count < 2) continue;
            // One unit carries the finding: a paired hub's USB 3 side, or the top on its own.
            if (Shared(snapshot, members))
            {
                foreach (var top in Units(Tops(snapshot, members))) top.ContainerIdShared = true;
                continue;
            }
            var product = ProductName(snapshot, id, members, labels: false);
            if (product.Length == 0) continue;
            foreach (var m in members.Where(m => m.NameSource is "USB ID lookup" or "USB vendor ID lookup" or "Generic reported name"))
            {
                m.Notes.Add($"Named after the product it's part of, “{product}”, as Windows groups it; on its own it reads “{m.Name}”.");
                m.Name = $"{m.Name} in {product}";
                m.NameSource += " and device container";
            }
        }
    }

    // Everything in a node's container, when it's a product of several USB devices, and what it's called.
    internal static (string Product, List<UsbNode> Others)? Of(Snapshot snapshot, UsbNode n)
    {
        if (n.ContainerId.Length == 0 || IsRoot(n.ContainerId) || !Groups(snapshot).TryGetValue(n.ContainerId, out var members) || members.Count < 2 || Shared(snapshot, members)) return null;
        return (ProductName(snapshot, n.ContainerId, members), members.Where(m => m != n).ToList());
    }
    // The other units that report the same Container ID as this one, each by its top hub or device.
    internal static List<UsbNode> SharingWith(Snapshot snapshot, UsbNode n) =>
        n.ContainerId.Length > 0 && Groups(snapshot).TryGetValue(n.ContainerId, out var members) && Shared(snapshot, members) && Tops(snapshot, members).Contains(n)
            ? Units(Tops(snapshot, members)).Where(t => !SameUnit(t, n)).ToList() : [];

    internal static Explanations.Explanation Explain(UsbNode n)
    {
        string noun = n.Kind == "Hub" ? "hub" : "device";
        return new($"This {noun} reports the same Container ID as other USB hardware it isn't part of, so Windows treats them as one device.",
            "No: everything plugged in works. Windows' Settings › Bluetooth & devices and Devices and Printers show them as one device, and may mix up their names and icons.",
            "Its firmware gives every unit the same Container ID, which is meant to be unique to each one.",
            ["There's nothing to fix by replugging.",
             "If you make this hardware or its firmware: give each unit its own Container ID (the BOS Container ID capability or the Microsoft OS Container ID descriptor), for example derived from its serial number, or leave it out and Windows makes one up from where the device sits."]);
    }

    // The container's ID and names, and what else is in it, for Detection details.
    internal static List<string> Evidence(Snapshot snapshot, UsbNode n, Func<UsbNode, string> path)
    {
        if (n.ContainerId.Length == 0) return [];
        if (IsRoot(n.ContainerId)) return ["Windows counts this as part of the computer itself (its root device container)."];
        var c = snapshot.Containers.FirstOrDefault(x => x.Id.Equals(n.ContainerId, StringComparison.OrdinalIgnoreCase));
        var names = c == null ? "" : string.Join(", ", new[] { c.Name.Length > 0 ? $"“{c.Name}”" : "", c.Manufacturer.Length > 0 ? "by " + c.Manufacturer : "", c.Model.Length > 0 && c.Model != c.Name ? "model " + c.Model : "" }.Where(x => x.Length > 0));
        var lines = new List<string> { $"Device container {n.ContainerId}{(names.Length > 0 ? ": " + names : "")}." };
        if (Of(snapshot, n) is { } part) lines.Add("The same product as " + string.Join(", ", part.Others.Select(o => $"{path(o)} {Topology.ShortName(o)}")) + ".");
        if (SharingWith(snapshot, n) is { Count: > 0 } others) lines.Add("Reports the same Container ID as unrelated hardware: " + string.Join(", ", others.Select(o => $"{path(o)} {Topology.ShortName(o)}")) + ".");
        return lines;
    }
    // Properties' Part of row, short enough for its column; Detection details names the others.
    internal static string PartOf(Snapshot snapshot, UsbNode n)
    {
        if (SharingWith(snapshot, n) is { Count: > 0 } sharing) return $"ID shared with {sharing.Count} other{(sharing.Count == 1 ? "" : "s")}";
        if (Of(snapshot, n) is not { } part) return "";
        return part.Product.Length > 0 ? part.Product : $"{part.Others.Count} other USB device{(part.Others.Count == 1 ? "" : "s")}";
    }

    private static readonly Guid ItemNameDisplay = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
    private static readonly Guid ContainerProperties = new("656A3BB3-ECC0-43FD-8477-4AE0404A96CD");
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Category; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct CompKey { public PropertyKey Key; public int Store; public IntPtr LocaleName; }
    [StructLayout(LayoutKind.Sequential)] private struct DevProperty { public CompKey Key; public uint Type; public uint BufferSize; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct DevObject { public int ObjectType; public IntPtr ObjectId; public uint PropertyCount; public IntPtr Properties; }
    // DevObjectTypeDeviceContainer is 2; no query flags or filters.
    [DllImport("cfgmgr32.dll")] private static extern int DevGetObjects(int objectType, int flags, uint requestedCount, CompKey[] requested, uint filterCount, IntPtr filter, out uint count, out IntPtr objects);
    [DllImport("cfgmgr32.dll")] private static extern void DevFreeObjects(uint count, IntPtr objects);
}

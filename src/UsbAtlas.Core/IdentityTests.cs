using System.IO;

namespace UsbAtlas;

internal static class IdentityTests
{
    internal static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        using var data = new StringReader("# fixture\n1234  Component Vendor\n\t5678  Controller Chip\n\t\t0001  Interface\n2345  Another Vendor\nC 00  Classes\n\t5678  Must not become a product\n");
        var database = UsbIdDatabase.Parse(data);
        Check(database.Lookup("1234", "5678").Product == "Controller Chip", "USB product lookup failed.");
        Check(database.Lookup("2345", "5678").Product == "", "Class data leaked into product lookup.");
        Check(database.Lookup("ZZZZ", "5678") == ("", ""), "Malformed USB ID must not match.");
        Check(UsbIdDatabase.Default.Lookup("0BDA", "0411").Vendor.Contains("Realtek"), "Bundled database did not load vendor identity.");
        Check(UsbIdDatabase.Default.Lookup("05E3", "0610").Vendor.Contains("Genesys"), "Bundled database did not load hub identity.");
        var node = new UsbNode { Id = "host/root/1", Kind = "Hub", VendorId = "1234", ProductId = "5678", Manufacturer = "Generic", ReportedProduct = "USB3.2 Hub", WindowsName = "Dell Monitor KVM" };
        DeviceIdentity.ResolveName(node, database);
        Check(node.Name == "Dell Monitor KVM" && node.NameSource == "Windows device name", "Specific Windows name must beat generic firmware.");
        node.ReportedProduct = "Acme Super Hub"; node.Manufacturer = "Acme";
        DeviceIdentity.ResolveName(node, database);
        Check(node.Name == "Acme Super Hub" && node.NameSource.StartsWith("USB product"), "Specific product name must win without duplicating brand.");
        node.ReportedProduct = "USB3.2 Hub"; node.Manufacturer = "Generic"; node.WindowsName = "Generic USB Hub";
        DeviceIdentity.ResolveName(node, database);
        Check(node.Name == "Component Vendor Controller Chip" && node.NameSource == "USB ID lookup", "Lookup fallback must be explicit and must not guess a retail brand.");
        node.VendorId = "FFFF"; node.ProductId = "FFFF";
        DeviceIdentity.ResolveName(node, database);
        Check(node.Name == "USB3.2 Hub" && node.LookupVendor == "", "Unknown IDs must keep the reported identity.");
        var root = new UsbNode { Kind = "Root hub", Children = [new() { Kind = "Empty port", Protocols = "USB 3.x" }, new() { Protocols = "USB 1.x / USB 2.0" }, new() { Protocols = "USB 2.0" }] };
        DeviceIdentity.SummarizeProtocols(root);
        Check(root.DownstreamProtocols == "USB 1.x / USB 2.0 / USB 3.x" && !root.ProtocolSummaryPartial, "Root summary must deduplicate and include empty port capabilities.");
        root.Children.Add(new UsbNode { Kind = "Unavailable" }); DeviceIdentity.SummarizeProtocols(root);
        var controller = new UsbNode { Kind = "Controller", Children = [root] }; DeviceIdentity.SummarizeProtocols(controller);
        Check(controller.ProtocolSummaryPartial && controller.DownstreamProtocols.Contains("USB 3.x"), "Incomplete capability coverage must propagate to the controller.");
        var noData = new UsbNode { Kind = "Root hub", ScanIncomplete = true }; DeviceIdentity.SummarizeProtocols(noData);
        Check(noData.DownstreamProtocols == "Not reported", "Unknown protocols must never become an empty string or an inferred capability.");
        Check(root.MaxPowerMa == null && controller.MaxPowerMa == null, "Protocol summaries must not invent power budgets.");
        // Cards lead with a short name: driver suffixes and corporate words go, the product stays.
        foreach (var (reported, shown) in new[] {
            ("AMD USB 3.10 eXtensible Host Controller - 1.10 (Microsoft)", "AMD USB 3.10 xHCI"), ("Realtek Semiconductor Corp. RTS5411 Hub", "Realtek RTS5411 Hub"),
            ("Genesys Logic, Inc. USB2.0 Hub", "Genesys Logic USB2.0 Hub"), ("Alpha Imaging Tech. Corp. Razer Kiyo", "Alpha Imaging Razer Kiyo"), ("Corsair Gaming HARPOON RGB Mouse", "Corsair Gaming HARPOON RGB Mouse") })
            Check(Topology.ShortName(new UsbNode { Name = reported }) == shown, $"\"{reported}\" must shorten to \"{shown}\".");
        Check(Topology.ShortName(new UsbNode { Name = "Realtek Semiconductor Corp. RTS5411 Hub", UserLabel = "Desk Corp. hub" }) == "Desk Corp. hub", "A user's own label must never be shortened.");

        var folder = Path.Combine(Path.GetTempPath(), "UsbAtlas-identity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "labels.json");
            var labels = new DeviceLabels(file);
            node.VendorId = "1234"; node.ProductId = "5678"; node.Serial = "serial-123";
            var snapshot = new Snapshot { Controllers = [node] };
            Check(labels.TrySet(node, snapshot, " Dell monitor KVM ", out _) && node.DisplayName == "Dell monitor KVM", "Label save failed.");
            string reportedName = node.Name;
            node.Id = "host/root/8";
            new DeviceLabels(file).Apply(snapshot);
            Check(node.UserLabel == "Dell monitor KVM" && node.Name == reportedName, "Serial-based labels must follow a moved device without replacing reported identity.");
            Check(new DeviceLabels(file).TrySet(node, snapshot, "", out _) && node.UserLabel == "", "Label reset failed.");
            node.Serial = "";
            labels = new DeviceLabels(file); Check(labels.TrySet(node, snapshot, "Port label", out _), "Port-scoped label save failed.");
            node.Id = "host/root/9"; labels.Apply(snapshot); Check(node.UserLabel == "", "A port-scoped label incorrectly followed a device.");
            node.Id = "host/root/8"; node.ProductId = "9999"; labels.Apply(snapshot); Check(node.UserLabel == "", "A label incorrectly transferred to a different product at the same port.");
            node.ProductId = "5678"; snapshot.IsDemo = true; labels.Apply(snapshot); Check(node.UserLabel == "", "Hardware labels leaked into demo data.");
            snapshot.IsDemo = false; node.Serial = "duplicate";
            snapshot.Controllers.Add(new UsbNode { Kind = node.Kind, VendorId = node.VendorId, ProductId = node.ProductId, Serial = node.Serial, Id = "other" });
            Check(!DeviceLabels.FollowsDevice(node, snapshot), "Duplicate serials must use port-scoped labels.");
            File.WriteAllText(file, "invalid json"); labels = new DeviceLabels(file);
            Check(!labels.TrySet(node, snapshot, "replacement", out _) && File.ReadAllText(file) == "invalid json", "Malformed label data must not be silently overwritten.");
            var blocked = new DeviceLabels(Path.Combine(folder, "missing", "labels.json"));
            File.WriteAllText(Path.Combine(folder, "missing"), "blocks directory creation");
            string prior = node.UserLabel;
            Check(!blocked.TrySet(node, snapshot, "unsaved", out _) && node.UserLabel == prior, "Failed writes must not apply an unsaved label.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
